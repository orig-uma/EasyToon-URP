# ToonPBR for URP — アーキテクチャ設計

EasyToon パッケージ内 Idol シェーダー（実装接頭辞 ToonPBR）の設計文書。
姉妹シェーダー **Doll（EasyPBR）と違う所を明示する**のがこの文書の役目。

> 呼称の経緯: 旧 Idol シェーダーは Cel へ改名し、この新シェーダーが
> Idol の名を引き継いだ（T-249）。その Cel は **T-356 で廃止**した
>（用途が Idol で満たされたため一本化。セル鏡面・2影などの様式化機能は
> 「BRDF は物理ベースのまま」の方針に合わず引き継がない）。

## 命名（確定）

| | 値 |
| :--- | :--- |
| シェーダー名 | `Origuma/EasyToon_URP/Idol` |
| 輪郭の LightMode | `IdolOutline` |
| 前髪透過の LightMode | `IdolHairSeeThrough`（T-341。旧 `SRPDefaultUnlit`） |
| コードの接頭辞 | `Toon*` / `_Toon*`（HLSL・C# とも。LightMode だけ `Idol*`） |
| Renderer Feature | `ToonOutlineFeature` / `HairSeeThroughFeature` |

## パッケージ間依存

**EasyShaderCore とは純粋関数だけを共有する**（設計ルール 3。T-340 で実施）。
`ToonPBRCommon.hlsl` が Core の `Common_Math` / `Common_Color` / `Common_Sampling` /
`BRDF/BRDF_GGX` を include し、実装が同値の 7 関数（`ToonIGN` / GGX の D・V・F /
`ToonVogelDisk` / `ToonRgbToHsv` / `ToonHsvToRgb`）は本体を Core に置き、Toon\* 側は
1 行の前方転送で残す（静的検査が Toon 接頭辞だけを見るため・呼び出し側を触らないため）。
VogelDisk（位相回転版）と HSV（max 下限形・float）は当初 Core 版が旧式で寄せられず、
**Idol の実装を Core へ逆輸入**して同値化した ── Doll の影も
同じ sincos 削減を得ている。

**寄せないもの**（判断は各関数のコメントに明記）: 影 HQ / 環境反射 / Dissolve
（Idol の方が高機能）。`ToonD_Charlie` / `ToonV_Ashikhmin` / `ToonSheenAlbedo` /
`ToonAOMultiBounce` / `ToonEnvBRDF_AB` は Core に受け皿が無いため自前のまま
（将来 Core 側へ足すなら候補）。

**EasyPBR の `Doll/` は include しない**（設計ルール 4）。現状 0 件。

## ディレクトリ構成

```
Idol.shader                 プロパティ・パス宣言・pragma のみ
ToonPBRCommon.hlsl          include・CBUFFER・テクスチャ宣言
Shading/                    シェーディング本体。**include の順序がそのまま依存関係**
  ToonPBRTypes.hlsl         構造体
  ToonPBRColor.hlsl         色ユーティリティ・曲率推定
  ToonPBRDiffuse.hlsl       拡散の伝達関数・スペキュラ AA のカーネル
  ToonPBRSpecular.hlsl      GGX / Charlie / Kajiya-Kay・クリアコート
  ToonPBREnv.hlsl           プローブのブレンド・多重散乱・AO・鏡面遮蔽
  ToonPBRShadows.hlsl       影 2 種（HQ / マイクロ。コンタクト・前髪は T-344 で廃止）
  ToonPBRLighting.hlsl      1 灯分のシェーディング・間接光
  ToonPBRRim.hlsl           リムライト
Passes/                     各パスの本体
  ForwardPass.hlsl / OutlinePass.hlsl / ShadowPass.hlsl /
  DepthOnlyPass.hlsl / DepthNormalsPass.hlsl /
  MotionVectorsPass.hlsl
```

**`#pragma` を `Passes/` や `Shading/` へ置かないこと。**
Unity は素の `#include` の中の pragma を読まず、**キーワードが永久に立たない。**
コンパイルは通り絵も出るので実機で気付けない。`param_check` が見ている。

## シェーディングモデル設計

> **BRDF は物理ベースのまま維持し、拡散光の伝達関数だけを様式化する。**

旧 Cel（T-356 で廃止）との最大の違い。Cel は鏡面もセル化できたが、
**ToonPBR はしない。** 鏡面は GGX / Charlie / Kajiya-Kay をそのまま使う。

### 陰の決定パイプライン

```
NdotL
  → ToonWrapDiffuse((N·L + w) / (1 + w)²)      **厳密にエネルギー保存**（検算済み）
  → + NPRMap の G（影のオフセット）
  → × リアルタイム影（同じ伝達関数を通す。別々に掛けると境界だけ硬くなる）
  → smoothstep(閾値 − s, 閾値 + s)             s = _ShadowSoftness × (1 + 曲率 × 係数)
```

**曲率で柔らかさが変わるのが中核。** 曲率の高い面ほど境界が広い。

### 鏡面

セル化しない。代わりに:

| | |
| :--- | :--- |
| 主ローブ | GGX（多重散乱の補償つき） |
| 布 | Charlie sheen + Ashikhmin。**エネルギー保存**（下地を `1 − sheen×E` で縮める） |
| 髪 | 異方性 GGX（2 ローブ）または Kajiya-Kay |
| クリアコート | 二層目。拡散・鏡面の両方を減衰させる |

### キーワード方針

**8 個のみ。** 追加は `param_check` の `ALLOWED_KEYWORDS` へ書くのが手続き。

```
_ALPHATEST_ON  _HQ_SHADOW_ON  _OUTLINE_ON
_SURFACETYPE_{DEFAULT,SKIN,FACE,HAIR,CLOTH}
```

### ステンシルレイアウト（前髪透過）

**実装済み**（T-223）。ビットの割り当てはこう:

| ビット | 使う部位 | 書く / 読む |
| :--- | :--- | :--- |
| 1 | 髪 | 従来方式（FR-22・瞳を ZTest Always で手前に出す）で髪が書く |
| 2 | 眉 | 前髪透過で眉が書く |
| 4 | 目 | 前髪透過で目が書く |

**2 つの方式は併用しない。** ビット 1 は「瞳が不透明で手前に出る」方式、
ビット 2/4 は「髪が半透明に透ける」方式。使うビットを分けてあるので、
片方を設定したマテリアルにもう片方が誤爆することはない。

前髪透過は `HairSeeThrough` パス（`IdolHairSeeThrough`。T-341）だけで成立する。
**ゲートはステンシルそのもの** ── 眉と目がビット 2/4 を書いていなければ
1 画素も描かれないので、設定していないマテリアルには影響しない。
**キーワードは持たない**（持たせると、ステンシルを設定しただけでは効かない）。

## Pass / LightMode

| Pass | LightMode | 備考 |
| :--- | :--- | :--- |
| ForwardLit | UniversalForward | メイン |
| **HairSeeThrough** | **IdolHairSeeThrough** | 前髪透過。`HairSeeThroughFeature` が後段一括描画（T-341）。`ForwardPass.hlsl` を define 違いで再利用 |
| Outline | **IdolOutline** | `ToonOutlineFeature` が後段一括描画。既定 OFF |
| ShadowCaster | ShadowCaster | `_ShadowCasterOff` で顔だけ外せる |
| DepthOnly | DepthOnly | **リム（深度モード）の前提** |
| DepthNormals | DepthNormals | **SSAO の前提** |
| MotionVectors | MotionVectors | **TAA の前提** |

**7 パス。** 前髪影・コンタクト影は T-344 で廃止し HQ セルフシャドウへ一本化した。機能面では 2影（2nd Shadow）を持たない（**見送りで確定** ── Ramp Override が N 段・色も自由で上位互換。T-362）。

## 移植の手順（完了済みの記録）

**移植は完了した**（`Packages/com.origuma.easytoon-urp/` 配下に配置済み・検証道具は `Documentation~/` にある）。以下は当時の手順の記録で、コマンド中のパスは移植前のもの。

**1. 名前を決めて振り直す。**

```bash
cd Assets/ToonPBR
python rename_shader.py <名前>          # 下見。70 箇所 / 23 ファイル
python rename_shader.py <名前> --apply
python check.py --self-test             # W107 が片側漏れを撃つ
```

作法は Idol から読み取ってある ── シェーダー名 `Origuma/EasyToon_URP/<名前>`、
LightMode は `<名前>Outline` / `<名前>HairSeeThrough`。

**2. `ShaderCompileCheck.cs` を消すか `Editor/` へ移す。**
`ToonPBRVariantCheck` に置き換わっていてどこからも呼ばれておらず、
しかも `Editor/` の外にある。**パッケージでは Editor 専用コードを
`Editor/` に置かないと実行時にも載る。**

**3. ファイルを移す。**

| 元 | 先 |
| :--- | :--- |
| `*.shader` / `*.hlsl` / `Shading/` / `Passes/` | `Runtime/Shaders/<名前>/` |
| `Runtime/*.cs` | `Runtime/Scripts/`（`Origuma.EasyToon.URP.Runtime`）|
| `Editor/*.cs` | `Editor/`（`Origuma.EasyToon.URP.Editor`）|
| `*.md` / `check.py` ほかの道具 | `Documentation~/`（Unity は `~` 付きを無視する）|

**`.meta` を一緒に動かすこと。** GUID が保たれるので、
**46 個のマテリアルの参照は切れない**（名前ではなく GUID で指している）。

`.shader` は `ToonPBRCommon.hlsl` を相対パスで include しているので、
まとめて動かせば include も切れない。

**4. asmdef の参照は足りている（実測済み）。**

パッケージの 2 つの asmdef が持つ参照だけでコンパイルできることを確かめた:

| | 参照 | 結果 |
| :--- | :--- | :--- |
| `Runtime/*.cs` | URP Runtime + Core Runtime | **エラー 0** |
| `Editor/*.cs` | 上 + EasyShaderCore.Editor + 自分の Runtime | **エラー 0** |

**4.5. 道具のパス解決（済み・実地で確認）。** ツリーは移すと 4 箇所に分かれるが、
検査は名前で部品を探す形にしてある（`find_file` / `find_main_shader`）。
**本番と同じ配置を作って 4 つの道具が通ることを確かめた**（T-250）。
移した後は `root` に**パッケージルート**を渡すこと。

**5. `package.json` と `CHANGELOG.md` を更新する。**

**全項目完了。** `ShaderCompileCheck.cs` は削除済み（`ToonPBRVariantCheck` が後継）。

## 設計ルール

Idol の 6 つに従う。現状:

1. CBUFFER 単一・全プロパティ包含 — **満たす**
2. キーワードは上表のみ — **満たす**（8 個。許可リストで機械的に守る）
3. Core は純粋関数のみ — **満たす**（共有は純関数 4 つ: IGN / GGX の D・V・F。T-340）
4. include 順 / `Doll/` 禁止 — **満たす**
5. 未ベイク・既定値で安全にスキップ — **満たす**（2D 20 個すべて既定値あり）
6. RendererFeature は Render Graph — **満たす**（2 つとも）

## 検証

`python check.py --self-test` で静的検査・値の検算・自己診断を回す。
実コンパイルは `--unity` を渡す（57 組 × D3D/Vulkan × 頂点/フラグメント）。

**物理の検算を持っている。**
sheen の多項式・環境 BRDF・拡散のラップ・平均フレネル・AO 多重バウンスを
Python 側で積分して突き合わせている。

**既知の食い違い:** `ToonV_SmithGGX` が Karis の近似形で、
厳密な Smith より**常に暗い**（RMS 13% / 最悪 27%）。
髪の異方版は厳密形なので、**同じ物理状況で髪と体の明るさが食い違う**（T-214・指示待ち）。

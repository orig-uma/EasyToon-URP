# EasyToon for URP

シェーダー名: `Origuma/EasyToon_URP/Idol`

トゥーンキャラクターを **PBR ライティングのステージ（3D ライブ）に置く**ための URP 向けキャラクターシェーダーです。
BRDF は物理ベースのまま保ち、**拡散の伝達関数だけを様式化**するので、フォトリアルな背景・反射プローブ・多灯の中でキャラが浮きません。
BaseMap 一枚＋既定値で成立し、質感は Editor 内のマップベイクで積み増します。
[EasyPBR](https://github.com/orig-uma/EasyPBR-URP)（Doll）の姉妹パッケージで、共通基盤 [EasyShaderCore](https://github.com/orig-uma/EasyShaderCore)（`com.origuma.easyshader-core`）の
HLSL ライブラリ・マップベイク・Inspector 部品を共有し、**Inspector のタブ構成は Doll と同一**（基本 / 陰・影 / ライト / スペキュラ / 質感 / 演出 / 詳細 / Baking）です。

## 特徴

* **物理ベースの BRDF ＋ 様式化した拡散:** GGX（デュアルローブ・Schlick Fresnel・Smith 可視性・Specular AA）、Charlie sheen（布）、Kajiya-Kay / 異方性 GGX（髪）、クリアコート＋イリデッセンス、グリッター（スパンコール）は物理のまま。拡散だけ Half-Lambert ＋ ソフトステップの伝達関数で、影色は HSV（色相回転・彩度）で設計し、落ち影だけ別色で塗り分けられます。Ramp テクスチャによる上書きも可。
* **サーフェスタイプ:** Default / Skin / Face / Hair / Cloth をマテリアルごとに選び、部位専用の機能（肌の皮下散乱・透過、顔 SDF、髪の異方性、布の sheen）をそのタイプにだけ出します。
* **顔:** ベイクした **16bit 顔 SDF**（距離場ブレンド・落ち影込み）でライトに滑らかに追従する顔影。正面付近の左右クロスフェード、下向きの面（顎裏）の法線陰影への戻し、頭ボーン追従（`FaceDirectionBinder`）。ステンシルによる前髪透過（眉・目が前髪越しに透ける）。
* **高品質セルフシャドウ:** メインライトのシャドウマップを Vogel ディスクでフィルタし、Penumbra / Receiver Normal Bias / Contact Hardening（PCSS）を調整可。落ち影（シャドウマップ）と陰（伝達関数）を分離合成します。
* **リム / Peach Fuzz:** フレネルリム・深度リム・バックライトリムの 3 系統と、肌の産毛（Peach Fuzz）。ライトの色に光源ごとに追従します。
* **ライブ運用:** ライト色整形（Light Conditioning）・白飛び防止（Anti-Blowout・追加ライトの Add / Max 合成）・フィルライト（照り返し）・暗転（Black Out。EasyShaderCore の `BlackOutController` でキャラ単位・Timeline 直キー）・Dissolve（`DissolveController`）。
* **質感の積み増し:** MatCap、ディテールマップ（アルベド・法線）、ベント法線（間接光の向き補正）、キャビティ、曲率マップ（境界幅の場所制御）、SSS マップ（厚み＋透過方向）、シアー生地（ストッキング）。
* **アウトライン:** 背面法線押し出し（`ToonOutlineFeature` による後段一括描画）。カメラ距離・画面幅上限、アルベドブレンドの線色、Cutout / Dissolve 同期。既定 OFF。
* **DCC 不要のマップベイク:** AO / Shade Normal / Hair Flow / Face SDF / Bent Normal / Curvature / SSS / Cavity を Inspector の Baking タブから焼いて自動アサイン（EasyShaderCore の Baker を共有）。
* **SRP Batcher を意識した設計:** 全プロパティ単一 CBUFFER。静的キーワードは `_ALPHATEST_ON` / `_HQ_SHADOW_ON` / `_SURFACETYPE_*`（5 択）/ `_OUTLINE_ON`（輪郭パスのみ）だけで、それ以外は uniform 動的分岐。ランタイムの供給（頭ボーン向き・暗転）は Play 中マテリアルインスタンス経由で Batcher を維持します（→ [SRP_BATCHER](Documentation~/SRP_BATCHER.md)）。
* **URP 機能との統合:** Forward / Forward+、追加ライトの影、ライトクッキー、Light Layers、Reflection Probe（ボックス投影）、APV、SSAO（DepthNormals パス）、デカール、MotionVectors（TAA）、LOD クロスフェード。

## インストール

### Package Manager（Git URL）

`Window > Package Manager > + > Add package from git URL...` に以下を入力する。

```
https://github.com/orig-uma/EasyToon-URP.git
```

特定バージョンを指定する場合:

```
https://github.com/orig-uma/EasyToon-URP.git#v0.2.2
```

依存する共通基盤パッケージ [EasyShaderCore](https://github.com/orig-uma/EasyShaderCore)（`com.origuma.easyshader-core`）は、
インストール直後（同一エディタセッション内・再起動不要）に**自動でインストールされる**（git が必要）。自動導入に失敗した場合のみ
手動手順つきの案内ウィンドウが表示される。手動で先に入れる場合:

```
https://github.com/orig-uma/EasyShaderCore.git#v0.3.1
```

### Embedded

`Packages/com.origuma.easytoon-urp` に配置すると embedded package として認識される（EasyShaderCore も同様に配置する）。

## 動作環境

* Unity 6 (6000.3) 以降
* Universal RP 17.3 以降 / Forward・Forward+
* [EasyShaderCore](https://github.com/orig-uma/EasyShaderCore) 0.3.0 以降（自動インストールされる）
* Render Graph 有効（既定）。Compatibility Mode では RendererFeature（アウトライン・前髪透過）が動作しません
* [EasyPBR for URP](https://github.com/orig-uma/EasyPBR-URP) は**任意**（Doll からの移行変換にのみ必要。コード依存なし）

## セットアップ

1. **RendererFeature の追加** — `Window > Origuma > Idol Setup` で、アクティブな Renderer Data へワンクリック追加（マテリアル Inspector の該当セクションにも未導入ガードがあり、そこからも開ける）
   - `Toon Outline Feature` — アウトライン描画（LightMode `IdolOutline` の後段一括描画）。アウトラインを使うときだけ
   - `Hair See-Through Feature` — 前髪透過（LightMode `IdolHairSeeThrough`）。前髪透過を使うときだけ
2. **Surface Type** — マテリアルごとに Default / Skin / Face / Hair / Cloth を選ぶ（基本タブ）。Default のままでは部位専用の機能が一つも出ません。`Tools > Idol > サーフェスタイプを名前から設定` でマテリアル名から一括設定できます
3. **FaceDirectionBinder** — 顔 SDF を使うキャラのルートに追加し、頭ボーンを指定（頭の向きを `_HeadForward` / `_HeadRight` に供給）。無い場合はオブジェクト軸で代用され、首振りには追従しません
4. **前髪透過** — 詳細タブの Hair See-Through プリセットで Body / Face / Brow / Hair / Eye を選ぶと、Stencil・Render Queue・透過パスを一括適用
5. **ベイク**（Baking タブ）— Source Root（キャラの GameObject）を指定し、Face SDF（16bit）/ Shade Normal / Hair Flow / Bent Normal / Curvature / SSS / Cavity / AO を焼く。生成 PNG は自動アサインされ、対応する強度が自動で立ちます

困ったときは `Tools > Idol > セットアップ診断`（Renderer Feature の欠落・URP Asset の影設定・マテリアル値の矛盾を列挙）。
EasyPBR(Doll) のマテリアルからは `Tools > Idol > EasyToon・EasyPBR から移行` で変換できます（顔 SDF は形式が違うため焼き直し。詳細 → [MIGRATION](Documentation~/MIGRATION.md)）。

## ドキュメント

| ドキュメント | 内容 |
| :--- | :--- |
| [SETUP](Documentation~/SETUP.md) | セットアップ手順（Renderer Feature / Binder / ベイク / 前髪透過） |
| [README_ToonPBR](Documentation~/README_ToonPBR.md) | よく使う設定と数値レシピ |
| [PROPERTIES](Documentation~/PROPERTIES.md) | 全プロパティ一覧（シェーダーと Inspector から自動生成） |
| [REQUIREMENTS](Documentation~/REQUIREMENTS.md) | 要件定義（採用技術と機能要件） |
| [ARCHITECTURE](Documentation~/ARCHITECTURE.md) | 内部構成・設計方針（Doll との差分 / Pass / キーワード） |
| [SRP_BATCHER](Documentation~/SRP_BATCHER.md) | SRP Batcher を効かせるための指針 |
| [VARIANTS](Documentation~/VARIANTS.md) | シェーダーバリアント（キーワード）一覧 |
| [MIGRATION](Documentation~/MIGRATION.md) | EasyPBR(Doll) からの移行ガイド |
| [VERIFICATION](Documentation~/VERIFICATION.md) | 動作検証チェックリストと検証スイート |

## ライセンス

[MIT License](LICENSE.md)

## 作者

Origuma — [https://github.com/orig-uma](https://github.com/orig-uma)

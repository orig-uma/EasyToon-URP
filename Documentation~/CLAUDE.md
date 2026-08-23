# ToonNPR — URP キャラクターシェーダー

Arknights: Endfield 系の「PBRライティングに馴染むソフトなトゥーン影」を Unity URP で実装するプロジェクト。

詳細な要件は `REQUIREMENTS.md`、着手すべきタスクは `BACKLOG.md` にある。**作業を始める前にこの2つを読むこと。**

---

## 設計思想（これを崩す変更は却下）

> BRDF は物理ベースのまま維持し、**拡散光の伝達関数だけ**を様式化する。

- 鏡面反射は GGX / Charlie sheen / Kajiya-Kay をそのまま使う。ステップ化しない
- 環境光はリフレクションプローブと SH から取る。ここがキャラと背景を繋ぐ主経路
- 拡散反射だけを、曲率駆動のソフトステップ + HSV による影色変換に通す

「トゥーンだから物理を捨てる」方向の提案はこのプロジェクトの目的と逆。影の階調を増やす、ランプテクスチャを必須にする、鏡面をステップ化する、といった変更は**提案する前に必ず確認を取ること**。

参考にしている絵の特徴（実装判断の根拠）:

- **輪郭線が無い。** シルエットは逆光リムと明度差で抜く。アウトラインパスは既定で OFF
- 影の境界の柔らかさが場所によって違う。曲率の高い面ほど広い
- 影色は暗くしただけでなく色相が回り彩度が上がっている
- 真珠ビーズや金具に本物の GGX ハイライトが乗っている

---

## ディレクトリ

**すべて `Packages/com.origuma.easytoon-urp/` の配下にある**（Assets/ からパッケージへ移植済み。旧図の `Assets/ToonPBR/` は当時の配置）。C# の asmdef は `Origuma.EasyToon.URP.Editor` / `…Runtime`。Idol 固有分は `Idol/` サブディレクトリに置く（姉妹シェーダー Cel は T-356 で廃止・Idol に一本化）。

```
Packages/com.origuma.easytoon-urp/
  Runtime/Shaders/Idol/
    Idol.shader               メイン。7パス
                              （ForwardLit / HairSeeThrough / Outline / ShadowCaster /
                               DepthOnly / DepthNormals / MotionVectors）
    ToonPBRCommon.hlsl        include・CBUFFER・テクスチャ宣言。**以降を順に include するだけ**
    Shading/                  **シェーディング本体。include の順序がそのまま依存関係**
      ToonPBRTypes.hlsl       構造体
      ToonPBRColor.hlsl       色ユーティリティ・曲率推定
      ToonPBRDiffuse.hlsl     拡散の伝達関数・スペキュラ AA のカーネル
      ToonPBRSpecular.hlsl    GGX / Charlie / Kajiya-Kay・クリアコート
      ToonPBREnv.hlsl         プローブのブレンド・多重散乱・AO・鏡面遮蔽
      ToonPBRShadows.hlsl     影 2 種（HQ / マイクロ。コンタクト・前髪は T-344 で廃止）
      ToonPBRLighting.hlsl    1 灯分のシェーディング・間接光
      ToonPBRRim.hlsl         リムライト
      ToonPBRDissolve.hlsl    ディゾルブ（**キーワードレス**。冒頭に判断の記録）
    Passes/                   **各パスの本体。`.shader` にはパス宣言と pragma だけが残る**
      ForwardPass.hlsl        ForwardLit（前髪透過が同じものを define 違いで使う）
      OutlinePass.hlsl        輪郭（LightMode = IdolOutline）
      ShadowPass.hlsl         ShadowCaster
      DepthOnlyPass.hlsl      Screen Silhouette モードのリムの前提
      DepthNormalsPass.hlsl   SSAO の前提
      MotionVectorsPass.hlsl  TAA の前提

  Runtime/Scripts/Idol/
    FaceDirectionBinder.cs        頭ボーンの向きを _HeadForward / _HeadRight に転送
    HairSeeThroughFeature.cs      前髪透過を後段一括描画（T-341。SetPass 分断対策）
    ToonOutlineFeature.cs         背面法線押し出しの輪郭を別 LightMode で分離

  Editor/Idol/
    SmoothNormalBaker.cs          アウトライン用の平滑法線を頂点カラーへベイク
    ToonPBRShaderGUI.cs           カスタムインスペクタ
    ToonPBRBakingPanel.cs         Baking タブ（EasyShaderCore の Baker へ委譲）
    ToonPBRVariantCheck.cs        キーワードを指定した実コンパイル検証（batchmode 可）
    ToonPBRSetupCheck.cs          シーン・URP設定・マテリアル値の診断
    ToonPBRPresets.cs             影／鏡面／リムを軸で振って比べるプリセット
    ToonPBRMigrator.cs            旧 Cel（T-356 で廃止）/ EasyPBR (Doll) からの移行
    ToonPBRSurfaceTypeFromName.cs 名前から Surface Type 一括設定・重ね描きパス停止
    ToonPBRDropDeadWork.cs        絵に出ない計算を止める

  Documentation~/
    REQUIREMENTS.md           要件定義（FR / NFR / 受け入れ基準）
    BACKLOG.md                優先順位付きタスク
    SETUP.md                  導入手順
    README_ToonPBR.md         よく使う設定と数値レシピ
    PROPERTIES.md             全プロパティの一覧（生成物・手で書き換えない）
    SRP_BATCHER.md            バッチングの実践ガイド
    VARIANTS.md               キーワード台帳
    check.py                  下の検査をまとめて回す入口
    shader_lint.py            Unity 無しで動く静的検査（E/W コード）
    param_check.py            式が実際の値で成立しているかの検算
    self_test.py              欠陥を注入して検査の生死を確かめる
    editor_log_check.py       Editor.log から実コンパイル結果を読む（L コード）
    hlsl_compile.py           d3dcompiler を直接叩く実コンパイル（Unity 不要）
    csharp_compile.py         同梱の Roslyn を叩く C# の実コンパイル（Unity 不要）
    smoke_tools.py            道具の入口が全部動くかだけを確かめる（T-259）
    gen_properties.py         プロパティ一覧を生成（PROPERTIES.md・手で書き換えない）
    rename_shader.py          シェーダー名・LightMode の一括改名（下見つき）
    verify_variants.py        バリアント検証（スクラッチプロジェクト経由）
```

`Idol.shader` は `ToonPBRCommon.hlsl` を**相対パス**で include している。ファイルを移動するときは両方まとめて動かすこと。

**`#pragma` を `Passes/` へ移さないこと。** 素の `#include` の中の pragma は Unity が読まず、**キーワードが黙って立たなくなる**（バリアントが消えても絵は出るので実機で「なぜか効かない」としか見えない）。pragma は `.shader` 側に残す。

---

## 検証（重要）

**シェーダーを編集したら必ず検証を通すこと。** これを飛ばして「書けました」と報告しない。

検査は3つに分かれていて、**互いの穴を埋め合っている**。まとめて回す入口を用意した:

```bash
cd Packages/com.origuma.easytoon-urp/Documentation~ && python check.py
```

```bash
cd Packages/com.origuma.easytoon-urp/Documentation~ && python check.py --unity "C:/Program Files/Unity/Hub/Editor/6000.3.8f1/Editor/Unity.exe"
```

**`check.py` は毎回 fxc で実コンパイルする。** Unity は要らない ──
Windows に必ず入っている `d3dcompiler_47.dll`（fxc の本体）を ctypes で叩き、
`Library/PackageCache/` にある URP のヘッダをそのまま include する。

```bash
cd Packages/com.origuma.easytoon-urp/Documentation~ && python hlsl_compile.py ../Runtime/Shaders/Idol            # 7 パス 14 プログラム・2 秒台
cd Packages/com.origuma.easytoon-urp/Documentation~ && python hlsl_compile.py ../Runtime/Shaders/Idol --variants # 全キーワード組・1〜2 分
cd Packages/com.origuma.easytoon-urp/Documentation~ && python check.py --full              # 上を check.py から
```

**Editor が開いていても動く。** これが入るまで、Editor 起動中は
型エラーも未宣言も一切検証できなかった。

| fxc が捕まえる | fxc が捕まえない |
|---|---|
| 未宣言の識別子（X3004）| **ベクトルの次元不一致**（黙って切り詰め・水増しする）|
| 引数の数（X3013）| D3D11 以外のバックエンド |
| 存在しないメンバ（X3018）| Unity 独自の前処理 |
| リソース上限（サンプラ本数など）| C#（そちらは Editor のログで見る）|

次元不一致を見ないので、**E012 / W108 と役割が分かれている。**

**コストも実測できる**（`--cost`）。`param_check --cost` はソースのゲートを
評価したテクスチャフェッチ数の**推定**だが、こちらはコンパイル済み
バイトコードから読む **実測の命令数と一時レジスタ数**。

```bash
cd Packages/com.origuma.easytoon-urp/Documentation~ && python hlsl_compile.py ../Runtime/Shaders/Idol --cost --variants
```

**`--branch-cost` は一様分岐の中身を実測する。** 既定 OFF の機能をキーワードで
切るか一様分岐で持つかを、**勘ではなく数字で**決めるための道具。ツリーを複製して
条件を `false` に潰し、差を取る。全部が「消せる無駄」ではない ── プローブの
重みのようなデータ依存の分岐はキーワードにしようがない（T-239）。

**サンプラの本数も実測する。** `ps_4_0` は 16 本しかなく、超えると実機で落ちる
（T-072 で実際に落ちた）。W105 は自前の `SAMPLER()` 宣言を数えるだけで
**URP が使うぶんが見えない**。現状の最大は **8 本（余裕 8 本）**。
14 本で警告、16 本で失敗にしてある。

現状の最大は **ForwardLit / 全部盛り / Hair のフラグメントで 2,573 命令・
一時レジスタ 56**。**一時レジスタが多いほど同時に走るスレッドが減る**ので、
命令数より効くことがある。機能を足したらここを見ること。

**C# も同じ手が使える**（`csharp_compile.py`）。Unity は Roslyn を同梱していて、
参照アセンブリも `Editor/Data/Managed/` と `Library/ScriptAssemblies/` にある。

```bash
cd Packages/com.origuma.easytoon-urp/Documentation~ && python csharp_compile.py ..            # エラーのみ
cd Packages/com.origuma.easytoon-urp/Documentation~ && python csharp_compile.py .. --warnings # 警告も
```

**Unity 自身の応答ファイルを使う。** `Library/Bee/artifacts/*.dag/Assembly-CSharp*.rsp`
に Unity が csc へ渡した引数がそのまま残っているので、**推測せずに同じ条件で**回せる。

推測した参照集合は**Unity より寛容だった**（T-241）── 存在しないオーバーロードを
通してしまい、Editor だけが落ちた。応答ファイル方式で**同じ壊し方を捕まえることを
確認済み**（T-242）。応答ファイルが無いときだけ自前の集合に落ち、
そのときは「エラー 0 件」が保証にならないと出力に書く。

**応答ファイルが古いと新しいファイルが検査されない。** 載っていないものは名指しで警告する。

自前の集合に落ちたときの注意 ── **一枚岩の `UnityEditor.dll` を参照しないこと。** モジュール版
（`Managed/UnityEditor/*.dll`）と併せると `EditorWindow` などが二重定義になり、
CS0433 で全滅する。`Assembly-CSharp*.dll` も外す（自分自身が入っている）。

`--unity` を渡すと Unity 側の実コンパイル（3分ほど）まで回す。
**Editor 自身のログ**からも実コンパイルの結果を読む
（`editor_log_check.py`）。**Editor が起動中は batchmode がそもそも起動できない**
ので、そのとき唯一残るコンパイル証拠がこれ。

| コード | 内容 |
|---|---|
| L001 | 最後の取り込み以降に出ている、現行のコンパイルエラー |
| L002 | 古い指摘の除外。ログは追記されるので**直したエラーが残り続ける** |
| L003 | このツリーを取り込んだ記録がログに無い（別プロジェクトのログを見ている） |
| L004 | **ログがソースより古い。合格ではなく未検証**（シェーダーと C# は別々に判定する）|

L004 が要。編集直後は「エラー 0 件」に見えるが、それは通ったのではなく
**まだコンパイルしていない**だけ。だから `check.py` のまとめは
合否の2値ではなく **未検証／スキップ**を別に持ち、通っていない項目が
あるのに「すべて通過」と言わない。

**ただし 56 組を通したのとは違う。** ログに残るのは失敗だけで成功は 1 行も
出ないので、**何組コンパイルされたかは原理的に分からない。**
まとめでは `実コンパイル・部分` と名乗らせている。

個別に回すなら:

```bash
cd Packages/com.origuma.easytoon-urp/Documentation~
python shader_lint.py ../Runtime/Shaders/Idol --strict
```

エラー 0・警告 0 が通過条件。検出するもの:

| コード | 内容 |
|---|---|
| E000 | **検査そのものが例外で落ちた。** シェーダーの問題ではない。この行が出ているときは他の指摘が網羅されていないので、まず検査を直すこと（落ちた検査が「0 件」と報告する形で 3 回踏んでいる ── T-132 / T-166 / T-167）|
| E001 | HLSL で参照しているプロパティが CBUFFER に無い |
| E002 | CBUFFER にあるが Properties に無い（`// lint:script-set` で除外可） |
| E003 | サンプルしているテクスチャが未宣言 |
| E004 | 依存ヘッダが使用行より後でインクルードされている |
| E005 | UnityPerMaterial CBUFFER の重複宣言 |
| E006 | TRANSFORM_TEX に対応する `_XXX_ST` が無い |
| E007 | 深度を読むのに DepthOnly パスが無い |
| E008 | 自前の `Toon*` 関数を定義より前で呼んでいる（HLSL は宣言順に解析する）|
| E009 | `saturate()` の結果に下駄を足している。値域が [0,1] から上へずれ、下流の `1.0 - x` が負になる。**負の底の pow は NaN**（T-165）。`max(saturate(x), eps)` と下限で挟むこと |
| E010 | 自前の `Toon*` 関数を呼んでいるが定義がツリーに無い。**打ち間違いと、分割したファイルの include 漏れ**が同じ形で出る |
| E011 | 自前の `Toon*` 関数の引数の数が定義と合わない。既定引数は「必要な数〜全部」の幅で許す |
| E012 | 自前の `Toon*` への引数の**成分数が足りない**（`float2` を `float3` の仮引数へ）。**足りない側は暗黙変換されない** |
| E013 | `lint:script-set` と書いてあるのに**その名前を設定している C# が無い**。印は警告を黙らせるので、誰も設定しないまま**実行時 0 で動く状態を自分の手で隠す**ことになる |
| E014 | URP が **0 か 1 で必ず定義する**マクロ（`USE_CLUSTER_LIGHT_LOOP` など）を `defined()` で見ている。**常に真**になり、機能を切った変種でも中がコンパイル対象になって、その機能の中でしか定義されない識別子を参照して落ちる。**有効な環境では通ってしまう**ので全キーワード組を回すまで出ない |
| W109 | 宣言したキーワードがどの `#if` にも現れない。**コードが同一のバリアントが倍に増えるだけ**。排他グループの先頭（`shader_feature A B C` の A）は対象外 |
| W110 | 形を持つパスがディゾルブを切っていない（消えた画素がそのパスにだけ残る） |
| W111 | Renderer Feature が探す LightMode / Pass 名がシェーダーに無い（描画対象 0 件のまま静かに通る） |
| W101 | `#if defined()` のキーワードを宣言する pragma が無い |
| W102 | pragma で宣言したキーワードを ON にする Property が無い |
| W103 | 未参照のプロパティ |
| W104 | カスタム ShaderGUI が参照していないプロパティ（インスペクタに出ない） |
| W105 | サンプラの本数が上限（16）に近い。超えると実機で落ちる |
| W106 | Range が [0,1] を外れるプロパティを lerp の補間係数に裸で渡している（外挿） |
| W108 | 自前の `Toon*` への引数の**成分数が多い**（`float4` を `float3` の仮引数へ）。**黙って切り捨てられる**のでコンパイルは通る |
| W107 | Editor / Runtime スクリプトのプロパティ名・シェーダー名がシェーダー側と一致しない。`HasProperty` や `Shader.Find` の null で守られて**黙って何もしない**ので気付けない |<br>`// lint:foreign-begin` 〜 `// lint:foreign-end` で挟んだ範囲は対象外（移行スクリプトが**移行元**のプロパティ名を書くため）。**ファイル単位で外さないこと** ── 同じファイルの移行先の名前は見たい |

**値の検算も通すこと。** `shader_lint.py` はコードの構造（宣言漏れ・include 順）を見るが、
**式が実際の値で成立しているかは見ない。** このプロジェクトで出た退行はどれもそちらだった。

```bash
python param_check.py . --materials "../requiem/vjT4u4BcId/Materials 3"
```

見ているもの:

| 検査 | 内容 |
|---|---|
| 厚み判定が発火しない | `Thickness/2 >= Length` だとレイが届かず判定が死ぬ（T-108） |
| 遮蔽の帯が潰れている | `Bias+ramp >= Thickness/2` だと Strength を上げても濃くならない |
| 半影半径の可動域 | `_HQShadowSoftness` を**出荷時の既定より広げている**とき、その代償を示す。既定値はシェーダーから読む（T-167） |
| **守りが外れている** | 式に入れた `max()` や `saturate()` をソースで探す。**外すと退行が再発する箇所**を名指しで守る（T-110 / T-113） |
| Range を外れた値 | `Range` はスライダを縛るだけで実行時は縛らない。移植した .mat に残ると lerp が外挿になる（T-076 / T-098） |
| 光源ループ内の光源非依存な計算 | `ToonShadeLight` はライトの数だけ呼ばれる。そこに置いた光源非依存の計算は灯数ぶん無駄になる（T-122 / T-123 で2回やった）|
| トグルとキーワードの食い違い | .mat をスクリプトで一括編集するとプロパティだけ変えてキーワードを忘れる。**インスペクタは ON に見えるのに効かない**という形で出る |
| .mat の構造の破損 | 一括編集で値を消す・色の成分を落とす・構造キーを壊す。Unity は黙って既定値に落ちるか、インポートに失敗する |
| **sheen の多項式** | `ToonSheenAlbedo` は数値積分を人が書き写した 15 個の定数。Charlie 分布と Ashikhmin 可視項の半球積分を Python 側で解いて突き合わせる。**物理そのものを検算する唯一の検査**（T-182） |
| AA が1つも無い | キャラを映しているシーンで MSAA もカメラ AA も無効。**シェーダーをいくら直しても消えないちらつき**が出る（T-174） |
| 移行の対応表 | `ToonPBRMigrator.cs` の対応表が**両側とも実在の名前**を指しているか。移行元の名前は W107 の対象外にしてあるので、ここが唯一の守り（T-186） |
| 移行の値域 | 変換を書いていない行で、移行元の Range が移行先に収まるか。**移行元ごとに別々に見ること** ── Idol と Doll をマージすると片方の食い違いが隠れる（T-189）|
| `_MaskMap` の中身 | パックしていない生の AO を入れていないか。**R=Metallic / G=Occlusion / B=Thickness / A=Smoothness**。実際に読まれるチャンネルが増えたときだけ撃つ（T-196）|
| **バリアント数** | パスごとに実装から数え、サマリの記録と突き合わせる。**キーワードを 1 つ足すだけで倍**になるので特に古くなりやすい。`--variants` で全パスの内訳が出る（T-227）|
| **効果ゼロの機能** | ゲートの値が 0 でないのに、その機能が要求するテクスチャが未割り当て。**絵は変わらないのにフェッチと命令を毎画素払う** ── 目視でも実機でも気付けない（T-255）|
| サマリの数字 | BACKLOG に書いた「値の検算 N 種」「N コード」「N 項目」「N 組」を**実装から数え直して突き合わせる**。文章の鮮度は見られないが、数字は計算できる（T-200）|
| **素の include の中の `#pragma`** | Unity は読まない。キーワードが**永久に立たず**、コンパイルは通り絵も出るので実機で「なぜか効かない」としか見えない。`#include_with_pragmas` なら対象外（T-216）|
| **どこからも include されていない HLSL** | 分割で置いたファイルが繋がっていないと**コンパイルは通り絵も出る** ── そこの関数が呼ばれないだけなので、影が薄い程度にしか見えない（T-213）|
| **移植先パッケージの設計ルール** | EasyToon（`Documentation~/ARCHITECTURE.md` 末尾）の 6 つのうち静的に見える 4 つ ── キーワードの許可制 / `Doll/` の include 禁止 / テクスチャ既定値の明示 / RendererFeature が Render Graph。**移植を進めながら作法を外さないための足場**（T-207）|

`--cost` で**1画素あたりのテクスチャフェッチ数**、`--variants` で**パスごとのバリアント数**が出る（どちらも合否には影響しない）。
ゲートを評価して数えるので、マテリアルごとに切ってある経路は除外される。
現状の最大は 42 フェッチで、**うち 32（76%）が影**。
機能を足す前にここを見ること。

**正しい設定に警告を出さないこと。** 一度「Bias が歩幅より狭い」を警告にしたが、
守りが効いている正常な状態で 46 件全部に出て、他の指摘を埋もれさせた。
**誤検出の出る検査は無いより悪い。**

### 静的検査の限界

これは**コンパイラではない**。以下は検出できないので、実機確認が必要:

- 数式の誤り、見た目の破綻
- バリアント爆発、実行時パフォーマンス
- ベクトルの次元不一致（**fxc も黙って変換する**。E012 / W108 が見る）

**実コンパイルそのものは `hlsl_compile.py` で回せる**（Unity 不要）。
未宣言・引数の数・存在しないメンバ・リソース上限はそこで出る。

**自前の `Toon*` に限れば、名前・引数の数・ベクトルの成分数は見られる**
（E010 / E011 / E012 / W108・T-226 / T-228）。定義も呼び出しもツリーの中にあるため。
成分数は**確実に分かる式だけ**を見ている（現状 72%）。
URP や core の関数は対象外なので、そちらは変わらず実コンパイルが要る。

Unity が使える環境なら、静的検査を通した後にこれを回す:

```bash
Unity -batchmode -quit -nographics -projectPath . \
      -executeMethod ToonNPR.EditorTools.ToonPBRVariantCheck.RunCI -logFile -
```

Unity が無い環境では、**推測でコンパイルが通ったと報告しないこと。** 「静的検査は通ったが実コンパイルは未検証」と正直に書く。

### Editor が開いていて batchmode が使えないとき

**これは条件付きでしか成り立たない。** ロックがプロジェクト単位なのは本当だが、
**Unity のグローバルキャッシュは全プロジェクトで共有される。**

Editor が起動中に batchmode を叩くと、こう落ちる（6000.3.8f1 で実測）:

```
database is locked
Failed to delete database file .../AppData/Local/Unity/Caches/CurlRequestCache.db
windows exception 0x80000003 ... CurlFileCache::Instance
```

exit 3 で**ログファイルすら作られない。** 回避策は無い ──
`LOCALAPPDATA` を差し替えても Unity は実パスを見に行くし、
`-noUpm` を付けても起動時の curl 初期化で同じ所を触る（両方試した・T-220）。

**Editor が開いているなら実コンパイルは諦めること。** 代わりにできるのは:

- **Editor 自身のログを読む** ── `%LOCALAPPDATA%/Unity/Editor/Editor.log`。
  ユーザーが Unity にフォーカスを戻せばリフレッシュが走り、
  `error CS` / `Shader error` がそこに出る。**塞いでいる当人がコンパイラでもある**
- **バイト一致で守れる変更に絞る** ── 切り出しのような「1 行も変えない移動」なら、
  include を展開し直して元とバイト一致することを確かめれば、
  コンパイルの通り／通らないは変わらない（T-210 / T-211 / T-212 でこれを使った）

Editor を閉じられるなら、別プロジェクトを立てる手は有効:

```bash
V=/tmp/verify                       # スクラッチ領域に作る
mkdir -p "$V/Assets" "$V/ProjectSettings" "$V/Packages"
echo "m_EditorVersion: 6000.3.8f1" > "$V/ProjectSettings/ProjectVersion.txt"
cp -r Packages/com.origuma.easytoon-urp "$V/Packages/com.origuma.easytoon-urp"   # ドキュメントも .meta も丸ごとで良い
# manifest.json は本番から com.unity.modules.* を全部引き写し、URP を足す
Unity -batchmode -quit -nographics -projectPath "$V" \
      -executeMethod ToonNPR.EditorTools.ToonPBRVariantCheck.RunCI -logFile -
```

**ビルトインモジュールを引き写すのを忘れないこと。** URP だけの manifest だと `Animator` や `HumanBodyBones` が見つからず、コードの問題ではないエラーが出る。

この方法で分かるのは HLSL と C# のコンパイル可否まで。**シーンの見た目は分からない**ので、目視確認の代わりにはならない。

**バリアント検証は Unity 側の `ToonPBRVariantCheck` を使うこと。**

```
Tools > Idol > バリアントを実コンパイル検証
Unity -batchmode -quit -nographics -projectPath . \
      -executeMethod ToonNPR.EditorTools.ToonPBRVariantCheck.RunCI -logFile -
```

検証しているのは **54 組**（ForwardLit の 7 セット × サーフェスタイプ 5、加えて HairSeeThrough 3 / Outline 3 / ShadowCaster 4 / DepthOnly 3 / DepthNormals 3 / MotionVectors 3）× D3D と Vulkan × 頂点とフラグメント。

**ForwardLit 以外にも必ずキーワードを渡すこと。** ここは長い間 ForwardLit だけにキーワードを渡していて、`_OUTLINE_ON` で囲まれた輪郭の押し出しコードと、`_ALPHATEST_ON` を立てた ShadowCaster / DepthOnly / DepthNormals が一度もコンパイルされていなかった。パスごとの組み合わせは `ToonPBRVariantCheck.PassSets` にある。**`.shader` に `#pragma` を足したらこの表にも足す。** 表に無いパスは WARN で出る。

`ShaderData.Pass.CompileVariant` でキーワードを指定して**その場でコンパイルする**。`ShaderUtil.GetShaderMessages` と違い、Unity がインポート時に何を流すかに依存しない。

**`GetShaderMessages` ベースの検証は通っていなくても「通った」と出る。** 実際に2回取りこぼした ── T-072（サンプラ上限超過）と T-085（変数の二重宣言で Hair 全滅）。どちらも「0 errors」と報告された後に実機や別手段で発覚している。

`verify_variants.py` は**同じ検証をスクラッチプロジェクトで回す**ためのもの（Editor を開いたまま使える）。
`#define` を注入する旧方式はやめ、中で `ToonPBRVariantCheck.RunCI` を呼ぶ。

```bash
python verify_variants.py --unity "C:/Program Files/Unity/Hub/Editor/6000.3.8f1/Editor/Unity.exe"
```

**移行スクリプトは実際に走らせて確かめること。** GUI を開かずに回せる入口が3つある:

| メソッド | 何を通すか |
|---|---|
| `RunDryRunCI` | 下見。`Run` 経由なので報告の組み立てとファイル書き出しも通る |
| `RunApplyCI` | **適用して読み直す。** 書き込み経路はここでしか通らない |
| `RunAoReuseCI` | AO を `_MaskMap` に流用する分岐（既定 OFF なので放置すると未実行）|

```bash
Unity -batchmode -quit -nographics -projectPath . \n      -executeMethod ToonNPR.EditorTools.ToonPBRMigrator.RunApplyCI -logFile <path>
```

**コンパイルが通ることと動くことは別。** 実際に走らせて初めて出た誤りが既にある（T-189）。
移行元のマテリアルが 0 個なら**失敗を返す** ── 空振りした下見は検証にならない。

**この道具は長らく偽の合格を出していた（T-132）。** 削除済みのクラスを呼んでおり、Unity がエラー終了しても `0 組で指摘あり` と表示して exit 0 を返していた。**集計行が取れなければ失敗を返す**ようにしてある ── `-logFile -` が Windows で捕捉できない問題も、この判定が即座に拾った。

---

## URP のバージョン依存で実際に踏んだ罠

対象は **Unity 6000.3.8f1 / URP 17.3.0**（`Packages/manifest.json` と `ProjectVersion.txt` で確認した実際の値）。
以下は経験済みの落とし穴なので繰り返さないこと。**URP 12〜14 に関する記述が混じっているが、
それは「下位互換のために避けている API」の理由であって、現在の対象バージョンではない。**

**インクルード順。** HLSL は上から順に解析される。`ToonPBRCommon.hlsl` の中で `SampleSceneDepth` を使うなら、`DeclareDepthTexture.hlsl` は同ファイル内の**使用行より前**に置く。パス側で後から include しても手遅れ。E004 がこれを見る。

**`GlossyEnvironmentReflection` は使わない。** URP 12 と 14 でシグネチャが違う。自前の `ToonSampleEnvSpecular`（`unity_SpecCube0` を直接 LOD サンプル）を使っている。URP の関数に置き換える提案はしないこと。

**`PerceptualRoughnessToMipmapLevel` も使わない。** 同じ理由で `ToonRoughnessToMip` に自前実装がある。

**`LIGHT_LOOP_BEGIN` は `inputData` という名前のローカル変数を要求する。** Forward+ 経路でマクロが `inputData.positionWS` と `inputData.normalizedScreenSpaceUV` を参照するため。変数名を変えると壊れる。

**`GetAdditionalLight` の3引数版（shadowMask 付き）は URP 12 以前に無い。** バージョンを下げる必要が出たら2引数版に落とす。

---

## コーディング規約

**命名。** 自前の関数・マクロは `Toon` 前置詞を付ける（`ToonD_GGX`, `ToonMicroShadow`）。URP の関数名と衝突させない。

**CBUFFER。** 全マテリアルプロパティを `UnityPerMaterial` に1箇所だけ置く。テクスチャは入れない。分割やパスごとの再定義は SRP Batcher を壊す。

**キーワード。** マテリアル単位のものは `shader_feature_local` を使う。`multi_compile` はパイプラインが要求するものだけ。バリアント数は増える一方なので、追加するときは既存のもので代用できないか先に考える。

**単位を書く。** `fwidth` 由来の量は「ピクセルあたり」か「1/m」かで桁が変わる。粗さは alpha か perceptual かで意味が変わる。**この2つで既に4回踏んでいる**（T-026 / T-060 / T-062×2）。関数のコメントに入力と出力の単位を明記すること。静的検査では絶対に見つからない。

**サンプラは共有する。** `ps_4_0` のレジスタは 16 本で、URP と分け合う。テクスチャごとに `SAMPLER()` を書くと**実機で落ちる**（T-072 で実際に落ちた）。共有には **core のインライン名**（`sampler_LinearRepeat` / `sampler_LinearClamp` / `sampler_PointClamp`）を使うこと。`sampler_BaseMap` のようなテクスチャ紐付け形式を共有に使うと、そのテクスチャがストリップされたパスで `Unrecognized sampler` になる。lint の W105 が本数を見ている。

**`UNITY_BRANCH` の中から `return` しない。** fxc が「未初期化の可能性がある」と警告する。単一 return に畳むか、結果をフラグで持ち回ること（T-049 / T-073 で2回踏んだ）。

**コメントは日本語。** コードは英語識別子、コメントは日本語で書く。「何をしているか」ではなく**「なぜそうしたか」**を書く。既存コードのコメントの粒度に合わせること。

**プロパティを足したら3箇所を同時に更新する。** `Properties` ブロック / `CBUFFER_START(UnityPerMaterial)` / 実際に読む箇所。1つ忘れると静的検査が E001 か E002 で落ちる。

---

## やってはいけないこと

- **数値のデフォルト値を勝手に変えない。** README のレシピと連動している。変える理由があるなら先に相談
- **シーン側の設定から導いた数値を文書やツールに書き写さない。** シャドウマップの
  テクセル寸法、顔が何テクセルか、カスケードの分割 ── これらは URP アセットで変わる。
  実際「1テクセル 4.9mm / 顔 31 テクセル」を10回以上言い続け、その間ずっと
  実際は 2.93mm / 51 テクセルだった（T-155）。**戒めをコメントに書くだけでは足りない**
  ── 同じ関数の中に焼き込んだ数字が残っていた（T-167）。計算元から読むか、
  Unity 側の診断（テクセル密度）に出させること
- **既存パスを消さない。** DepthOnly はリムライトの前提、DepthNormals は SSAO の前提、
  MotionVectors は TAA の前提（消すとアニメーション中のキャラが尾を引く）
- `.meta` ファイルを手で作らない。Unity が生成する
- サンプルシーンやテクスチャのバイナリを生成しようとしない。手元に無いものは「必要」とだけ書く
- v1 実装（ランプ方式の `ToonCharacter.shader`）は v2 に統合済みで、このリポジトリには含めていない。復活させないこと

---

## 作業の進め方

1. `BACKLOG.md` から着手するタスクを選び、何をやるか宣言する
2. 変更は1タスク1コミット相当の粒度に保つ。複数タスクを混ぜない
3. 編集後に `Documentation~/` で `python shader_lint.py ../Runtime/Shaders/Idol --strict` を通す
4. 新しいマテリアルプロパティを足したら `REQUIREMENTS.md` の該当 FR も更新する
5. 見た目に関わる変更は、**何をどう確認すればいいか**を報告に書く（「Directional Light を背面に回して脚の輪郭を見る」など）

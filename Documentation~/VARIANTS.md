# EasyToon for URP — シェーダーバリアント（キーワード）

バリアントを生むキーワードと切り替えるマテリアルプロパティの対応。バッチングへの影響は [SRP_BATCHER](SRP_BATCHER.md) を参照。

## Idol

### 機能キーワード（`shader_feature_local*` — マテリアルの設定で切り替え）

| キーワード | 状態数 | 対応プロパティ（UI ラベル） | 対象パス |
| :--- | :---: | :--- | :--- |
| `_SURFACETYPE_DEFAULT` / `_SKIN` / `_FACE` / `_HAIR` / `_CLOTH` | 5 | `_SurfaceType`（Surface Type） | ForwardLit / HairSeeThrough |
| `_ALPHATEST_ON` | 2 | `_AlphaClipOn`（Alpha Clip） | 全 7 パス |
| `_HQ_SHADOW_ON` | 2 | `_HQShadowOn`（Enable HQ Self Shadow） | ForwardLit / HairSeeThrough |
| `_OUTLINE_ON` | 2 | `_OutlineOn`（Enable Outline） | Outline |

パスごとの機能組み合わせ数: **ForwardLit / HairSeeThrough = 5·2·2 = 20**、Outline = 2·2 = 4、その他 4 パス（ShadowCaster / DepthOnly / DepthNormals / MotionVectors）= 2。

**この 8 個は許可制。** `param_check.py` の `ALLOWED_KEYWORDS` が上表と 1:1 に対応しており、表に無いキーワードの追加を機械的に検出する（キーワードを足したいときは先に表と許可リストの両方を更新することになる＝設計判断が必ず記録に残る）。

**上表以外の機能はキーワードを持たない。** 既定 OFF の機能（Dissolve / MatCap / シアー生地 など）は uniform 動的分岐（`UNITY_BRANCH if (_Xxx > 0.0)`）で切り、OFF のときブロックごとスキップする。判断の記録は `Runtime/Shaders/Idol/Shading/ToonPBRDissolve.hlsl` 冒頭 — キーワードを 1 つ足すと ForwardLit の feature 組が 20 → 40 に倍化するため。

### システムキーワード（`multi_compile*` — URP 側の都合で展開）

ForwardLit が宣言するセット: メインライト影（`_MAIN_LIGHT_SHADOWS` / `_CASCADE` / `_SCREEN`）/ `_SHADOWS_SOFT` / `_ADDITIONAL_LIGHTS` / `_ADDITIONAL_LIGHT_SHADOWS` / `_CLUSTER_LIGHT_LOOP` / `_REFLECTION_PROBE_BOX_PROJECTION` / `_SCREEN_SPACE_OCCLUSION` / `_LIGHT_COOKIES` / `_LIGHT_LAYERS` / `_DBUFFER_MRT1/2/3` / APV（`#include_with_pragmas`）/ `multi_compile_fog` / `LOD_FADE_CROSSFADE`。

**意図的に宣言していないもの**（`Idol.shader` の該当コメントに理由が書いてある。削らないこと）:

- `_ADDITIONAL_LIGHTS_VERTEX`（頂点ライティング品質を使わない）
- `_REFLECTION_PROBE_BLENDING`（クラスタ経路のプローブブレンドで代替）
- `multi_compile_instancing`（Enable GPU Instancing は SRP Batcher から外すだけ）

### バリアント数

総数はシステムキーワード側（URP バージョン・Quality 設定・APV の状態数）で変わるため、この文書には数を焼き込まない。実測の記録は `ToonPBRDissolve.hlsl` 冒頭（T-344 前の実測で約 270 万・現在はコンタクト影の廃止で半減）。数えるときは:

```
cd Packages/com.origuma.easytoon-urp/Documentation~
python param_check.py ../Runtime/Shaders/Idol --variants
```

実コンパイルでの検証は `Tools > Idol > バリアントを実コンパイル検証`（`ToonPBRVariantCheck`。batchmode 可）。

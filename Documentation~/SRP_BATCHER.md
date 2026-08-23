# EasyToon for URP — SRP Batcher を効かせるために

Idol シェーダーの実践ガイド。キーワードの全リストは [VARIANTS](VARIANTS.md) を参照。

## 要点

SRP Batcher は**マテリアルが違っても同じシェーダーバリアント・同じ描画状態なら連続して低コストで描く**仕組み。Idol は CBUFFER（`UnityPerMaterial`）を 1 つにまとめており互換（Inspector で "SRP Batcher: compatible"）。

バリアントが分かれても SRP Batcher は**無効化されない**。起きるのは**バッチの分断**で、描画列の中でバリアントや描画状態が切り替わるたびにそこで一度切れる。

> つまり「同時に描かれるマテリアル間で値が割れる設定」ほどバッチを細切れにする。逆に同時に映るキャラ間で揃っていれば、バリアントが分かれていても切れない。

カスタム Inspector 上でバリアントを生むプロパティには **⚡ マーク**が付く。

## Idol

### ⚡ キーワード（バリアント）

| プロパティ（UI ラベル） | キーワード | 備考 |
| :--- | :--- | :--- |
| Surface Type | `_SURFACETYPE_*`（5 状態） | **キャラ内で割れるのが設計**（肌・顔・髪・布で部位別コードを切り替える）。分断は部位の境界で起き、複数キャラの同部位同士は同じバリアントでまとまる |
| Alpha Clip | `_ALPHATEST_ON` | 全 7 パス |
| Enable HQ Self Shadow | `_HQ_SHADOW_ON` | ForwardLit / HairSeeThrough |
| Enable Outline | `_OUTLINE_ON` | Outline パスのみ（ForwardLit を分断しない） |

**同時に映るキャラ間で揃えるべきは HQ Self Shadow / Alpha Clip** — この 2 つを部位やキャラで割る理由は通常無い。Surface Type は部位ごとに割れてよい（そのための設計）。

### キーワードレス方針（Dissolve ほか）

既定 OFF の機能（Dissolve / MatCap / シアー生地 など）は**キーワードを持たず**、`_DissolveAmount > 0` などの uniform 動的分岐で切る（判断の記録は `Runtime/Shaders/Idol/Shading/ToonPBRDissolve.hlsl` 冒頭 — キーワードを足すと ForwardLit の feature 組が 40 → 80 に倍化するため）。この方針により:

- ON/OFF がマテリアル間で混在しても**バッチは切れない**
- EasyPBR(Doll) の Dissolve で必要な Shader Variant Collection の Warmup 運用も**不要**（踏むべき未コンパイルバリアントが存在しない）

### MaterialPropertyBlock（ランタイム制御時の注意）

`MaterialPropertyBlock` を設定したレンダラーは **SRP Batcher の対象から外れる**。ランタイムでプロパティを動かす場合は、MPB ではなく**マテリアルインスタンス**（`renderer.materials`）経由で値を書くこと——別マテリアル同士は同一バリアントである限り SRP Batcher でバッチされる。

- `FaceDirectionBinder`（頭ボーン向きの `_HeadForward` / `_HeadRight` 供給）は**この方式で実装済み**: Play はマテリアルインスタンスへ書き（SRP Batcher 維持・共有資産非汚染）、Edit は非破壊 MPB プレビュー、OnDisable で復元。設定は不要で常にバッチングを保つ（旧版の「既定 MPB ＋ Write To Material トグル」は廃止）。
- `_HeadForward` / `_HeadRight` が Properties ブロックに宣言してあるのは SRP Batcher 互換の必要条件（CBUFFER にあって Properties に無いプロパティが 1 つでもあると**シェーダーごと非互換**になる。T-338）。

### GPU Instancing

Idol は `multi_compile_instancing` を**意図的に宣言していない**（`Idol.shader` の ForwardLit 冒頭コメントに理由）。マテリアルの Enable GPU Instancing に印を入れても instanced 描画にはならず、**そのレンダラーが SRP Batcher から外れるだけ**で得るものが無い。OFF のまま使うこと。

### HairSeeThrough パス（Feature による後段一括描画）

前髪透過パスの LightMode は独自タグ `IdolHairSeeThrough` で、`HairSeeThroughFeature` が**不透明の後にまとめて描く**（T-341。Idol Setup から追加）。旧構成（`SRPDefaultUnlit`）では URP が UniversalForward と同じ描画パスで処理するため [本体][透過] が交互に並び、**SetPass が跳ねて ForwardLit がまとまらなかった** ── T-040 の輪郭と同じ問題。現構成では ForwardLit は素でバッチされ、透過同士も 1 箇所でバッチされる。

透過を使わない材質は引き続き `Tools > Idol > 使っていない重ね描きパスを止める` で切ること（Feature はタグを持つパスを全部描くため、止めていない材質のぶん draw が走る）。

### やること

1. **HQ Self Shadow / Alpha Clip を、同時に映るキャラ間で揃える**
2. Surface Type は部位設計どおりに割る（割れるのが正常）
3. ランタイムからマテリアル値を書くコンポーネントは**マテリアルインスタンス経由**にする（MPB は Edit プレビューのみ）
4. 確認は **Frame Debugger**（"SRP Batch" の区切り）で。切れている境界のオブジェクトで何が割れているかを疑う

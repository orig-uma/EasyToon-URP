# セットアップ手順

動作確認シーンを再現するための手順書。**モデルもテクスチャもリポジトリに入っていない**ので、ここには「何を用意して、どう組むか」だけを書く。バイナリは含めない。

数値レシピは `README_ToonPBR.md` §4 にある。ここでは重複させず、シーンの組み方だけを扱う。

検証環境は Unity 6000.3.8f1 / URP 17.3.0（Linear / Forward・Forward+ 両方）。

---

## 0. まず診断を回す

見た目がおかしいときは、**マテリアルの `Debug View` で中間量を直接見る**のが早い。曲率・遮蔽量・Cavity は最終色に混ざると効いているか判断できない。特に:

- **Lit** — トゥーンの境界が立っているか（影が出ないときはまずこれ）
- **Curvature** — 0=平面 / 1=丸い。全面が真っ白なら正規化が壊れている
- **ShadowAtten** — ちらつきの原因がシャドウマップ側かステップ側か
- **Cavity** — マップが効いているか


```
Tools > Idol > セットアップ診断
```

**キャラのルートを選択してから実行すること。** シーン側まで見ます。

このシェーダーの機能は**マテリアルの値だけでは完結しない**。Renderer Feature、シーンのコンポーネント、URP Asset の設定のどれかが欠けると、**エラーも警告も出ないまま黙って無効になる**。実際に踏んだもの:

- 焼いたマップは割り当て済みなのに強度が 0 で1枚も使われていない
- 環境光が主光源と同量で、影が「わずかに暗い」程度にしかならない
- Face のマテリアルがあるのに `FaceDirectionBinder` が無く、SDF が丸ごと不使用
- Alpha Clip と Base Color のアルファの組み合わせで、1ピクセルも描かれない

いずれも**絵を見ても原因が分からない**。診断はこれらを名指しし、直せるものはボタン1つで直す（Undo 対応）。組み終わった後だけでなく、**見た目がおかしいと思ったら最初に回す**のが早い。

---

## 1. 先に用意するもの

### アセット

| 物 | 要否 | 条件 |
|---|---|---|
| キャラモデル | 必須 | 部位ごとにマテリアルを分けられること。顔を SDF で塗るなら顔専用 UV が要る |
| ベースカラー | 必須 | sRGB ON |
| 背景 | 必須 | URP Lit で十分。**キャラ単体では馴染み具合が判定できない**ので、必ず背景の中に置く |
| Reflection Probe | 必須 | 背景を焼いたもの。空のプローブだと金属が死ぬ |
| MaskMap | 任意 | sRGB **OFF**。未設定（白）でも動く |
| NPRMap | 任意 | sRGB **OFF**。G（影オフセット）が一番効く |
| Face SDF | 顔を使うなら必須 | sRGB OFF・**非圧縮**。16bit 1ch（R×256+G）の一方式。Baking タブで焼く（Core の `FACE_SDF_BAKING.md` が仕様）|
| Hair Shift Noise | 任意 | 未設定時は "gray" |
| Ramp | 任意 | `Use Ramp Map` を ON にしたときだけ |

### マップは自分で描かなくてよい（**Baking タブ**）

**Idol が読む 8 種のマップは、インスペクタの「Baking」タブから焼けます。**
中身は EasyShaderCore の Baker です（Doll と共有）。
Hierarchy でキャラを選ぶと `Source Root` に自動で入ります。

| マップ | 何のため | 焼いた後 |
|---|---|---|
| **Shade Normal** | 顔の陰から鼻・眉の凹凸を落とす | 強度まで自動で入る |
| **Hair Flow** | UV ミラーで天使の輪が割れるのを直す | 強度まで自動で入る |
| **Cavity** | 窪みの微細遮蔽 | 強度まで自動で入る |
| **Face SDF** | 顔の影境界（Surface Type = Face の本命）| `SDF Blend` を 1 にする |
| **Bent Normal** | 壁際・脇の下で間接光が回り込むのを防ぐ | `Use Bent Normal` を ON にする |
| **Curvature** | 曲率で境界幅を変える唯一の供給源（**任意**）| `Curvature Influence` が 0 なら 1 にする |
| **SSS** | 散乱の向きと厚み（透過が使う）| `SSS Map Strength` を 1 にする |
| **AO** | 遮蔽 | **手で合成が要る** ── 下記 |

**AO だけ扱いが違います。** Idol は遮蔽を単体テクスチャではなく
`Mask Map` の **G チャンネル**に詰める設計なので、焼いた画像は
**保存されるだけで自動では割り当たりません。**
画像編集で G へ合成してください（R:Metallic / G:Occlusion / B:Thickness / A:Smoothness）。
パネルにも同じ警告を出しています。

**モデルの Read/Write Enabled が要ります。** 焼いた画像は Source Root の隣に保存されます。

**Face SDF を焼いても、シーンに `FaceDirectionBinder` が無いと顔だけ破綻します。**
頭ボーンの向きが供給されないためで、マップの問題ではありません（下記）。

**Face SDF は 16bit 1ch（R×256+G）だけです。** Doll の 4ch マップや 8bit の
外部 SDF はそのままでは読めません（G が下位バイトとして解釈されて顔が壊れる）。
Idol の Baking タブで焼き直してください。テクスチャは**非圧縮・sRGB OFF**が必須です
（BC 圧縮は RG の連続性を壊す）。

**首まわりの影が不自然なときは `X Axis Tilt`。** Face SDF のスイープは
既定で水平方向へスイープして焼きます。実際のライトは通常やや上から差すため、
モデルによっては顎下〜首の境界がずれて不自然に見えます。Baking タブの
`X Axis Tilt`（度）で左右スイープ光に仰角を付けて焼き直してください
（0 が従来の水平。まずは 10〜20 度あたりから合わせる）。上下（B/A）チャンネルは変わりません。

---

### 陰の質感で最初に触るもの

**`Curvature Influence` は既定 0 です。** 曲率の供給源は Baking タブで焼く
**Curvature Map** だけなので（画面微分の推定は T-381 で撤去）、焼かずに上げても
何も起きません。焼くと Influence が 0 なら 1 に立ちます。

**`Shade Normal` も既定 OFF です**（強度 0）。値が入るのは Baking タブで焼いたときだけで、
顔の鼻・眉まわりの陰を整えるためのものです。体や衣装に効かせると凹凸の情報が
落ちて平坦に見えることがあるので、**顔マテリアルにだけ焼く**のが基本です。

---

### 付属スクリプト

| スクリプト | いつ要るか | 使い方 |
|---|---|---|
| `Runtime/FaceDirectionBinder.cs` | **Surface Type = Face を使うなら必須** | キャラのルートに追加する。Humanoid なら頭ボーンは Animator から自動で拾う |
| `Editor/SmoothNormalBaker.cs` | アウトラインで Use Baked Smooth Normal を使うとき | メッシュを選んで `Tools > Idol > Bake Smooth Normals` |
| `Runtime/HairSeeThroughFeature.cs` | **前髪透過を使うなら必須**（T-341 で Feature 化） | `Window > Origuma > Idol Setup` で Renderer に追加 |
| `Editor/ToonPBRSetupCheck.cs` | **常に。組む前も、おかしいと思ったときも** | §0 |
| `verify_variants.py` | シェーダーを編集したとき（開発者向け） | `python verify_variants.py --unity "<Unity.exe>"` |

**FaceDirectionBinder が無いときはオブジェクトの軸（+Z 正面 / +X 右）で代用される**（`Fallback to Object Axis`、既定 ON）。立ちポーズならこれで成立するが、**頭の回転には追従しない**。首を振る演出では Binder を付けること。両方無効なら通常の法線陰影に落ちる（壊れはしない）。

頭ボーンのローカル軸はモデルによって違う。顔の影が横にずれる・反転する場合は `Forward Axis` / `Right Axis` を変えて合わせる。

**SmoothNormalBaker はモデルの Read/Write Enabled が要る。** ベイク結果は元の FBX ではなく、隣に `<名前>_SmoothNormals.asset` として保存され、Renderer に差し替わる。

---

## 2. URP Asset の設定

| 項目 | 値 | 理由 |
|---|---|---|
| Depth Texture | **ON** | リムが深度差を読む。OFF だとリムが出ない |
| Opaque Texture | OFF | 使っていない |
| HDR | ON | リムとエミッシブが HDR 前提の強度 |
| Rendering Path | Forward / Forward+ どちらでも | NFR-05 のため**両方で確認する** |
| Shadow Distance | 20〜30 m | 長いとテクセルが粗くなり自己影がガタつく |
| Cascade Count | 2〜4 | 近景カットなら 2 で足りる |
| Soft Shadows | ON | |

Color Space は Linear（Project Settings > Player）。Gamma では影色の HSV 変換が意図した色にならない。

引きの画（キャラ全身が小さく入るカット）を作る場合は、Shadow Distance を 40 m 前後まで伸ばした状態でも影がガタつかないか確認しておくと、後でカメラを引いたときに破綻しない。

---

## 3. シーンの組み方

### レイヤー

キャラ用のレイヤーを1つ作る（例: `Character`）。ライトを2灯に分けるのに使う。

### ライト（2灯構成）

| ライト | Culling Mask | 役割 |
|---|---|---|
| 背景用 Directional | Character 以外 | 背景の見た目を決める |
| キャラ用 Directional | Character のみ | 強度と色だけ独立させる。0.2 EV 明るめが目安 |

**方向は2灯とも揃える。** 揃えないと影の向きが背景と食い違って、合成写真のように見える。

**Culling Mask の代わりにレンダリングレイヤーも使える。** URP Asset で Light Layers を有効にし、ライトとキャラの Renderer に同じレイヤーを割り当てる方式。Culling Mask はカメラ単位の仕組みを流用したものなので、ライト単位で細かく分けるならこちらの方が素直。シェーダー側は対応済み。

### 逆光の作り方

リムはシェーダー単体では作れない。**Directional Light をカメラの向こう側に回す**のが前提。

- 方位角: カメラの正面から見て 160〜200°（真後ろ ±20°）
- 仰角: 15〜30°。高すぎると頭頂だけ光って輪郭が抜けない
- `Backlight Bias` が 0.7 前後だと、光源側の輪郭だけが光る

### Reflection Probe

- Type は Baked、形状は Box でキャラを内側に含める
- **背景を配置してから** Bake する。順序を逆にすると空のキューブが焼ける
- Importance をキャラ周辺のプローブで高くしておくとブレンドが安定する

### カメラ

Post Processing を ON。Rendering path は URP Asset 側の設定に従う。

---

## 3.6 瞳を前髪より手前に出す（任意）

前髪が瞳に掛かる角度でハイライトを見せたい場合だけ。マテリアル2つの設定で完結する。

1. **髪マテリアル**のインスペクタ → Advanced → Stencil → **「髪 (書き込む)」**
2. **瞳マテリアル**（ハイライトを持つもの）→ 同じ場所で **「瞳 (前髪を抜く)」**

ボタンは Ref・Comp・マスク・Z Test・Render Queue を一括で設定する。手で入れると必ずどれかを落とすため。値の意味と、なぜ Read Mask を絞る必要があるかは `REQUIREMENTS.md` §6 を参照。

**同じシーンに複数のキャラが居る場合は注意。** bit 0 を共有するので、キャラ A の髪とキャラ B の瞳が画面上で重なると B の瞳が A の髪を貫く。避けるならキャラごとに bit 1〜3 を割り当てる。

## 3.7 顔の自己影を消す（推奨）

鼻や眉が顔に落とす影は、SDF で引いた境界を汚すだけで絵として使い道がない。

- **顔が独立した Renderer なら** — その Renderer の `Cast Shadows` を Off。これが最短
- **顔が体と同じ SkinnedMeshRenderer のサブメッシュなら** — 顔マテリアルの Advanced → `Exclude from Shadow Map` を ON。Renderer 単位の設定では体まで影が消えるため

どちらでも**首と顎の落ち影も一緒に消える。** 必要なら NPRMap の G（影オフセット）に描く。README §3 が「首の下」を挙げているのはこのため。

## 3.8 スクリーンスペース輪郭（廃止）

**T-380 で撤去した。** 実プロジェクトで未導入のまま、MSAA と両立しない制約だけが残っていた。輪郭は押し出し方式の `Toon Outline Feature`（§3.9.5）で出す。

## 3.9 SSAO を使う場合

URP の SSAO（Renderer Feature）を入れると、このシェーダーも AO を受け取ります。DepthNormals パスを持っているのはそのためです。

**SSAO 側の「Direct Lighting Strength」は効きません。** このシェーダーは AO を遮蔽値として一度受け取り、そこから直接光へどれだけ効かせるかを**マテリアルの `Apply AO to Direct Light`** で決めています。両方を掛けると二重になるため、URP 側の直接光係数は意図的に捨てています。

直接光への効き方を変えたいときは、SSAO の設定ではなくマテリアル側を触ってください。

## 3.9.5 アウトラインを使う場合

**`Enable Outline` を ON にしただけでは描画されません。** Renderer Data に **`Toon Outline Feature`** を追加してください。

理由は性能です。URP は不透明の描画で `UniversalForward` と `SRPDefaultUnlit` を同じパスにまとめるため、アウトラインをそこに置くと本体と輪郭が交互に描かれ、**ForwardLit の SRP Batcher が分断されます。** しかもこれはアウトラインを使っていないマテリアルにも波及します（パスが存在するだけで起きる）。

独自 LightMode に逃がすことで、本体は素でバッチングされ、アウトライン同士もまとめて描かれます。代わりに Feature の追加が必要になる、というトレードオフです。

## 3.10 追加機能の確認手順

実装済みだが**一度も画面で確認していない**機能の一覧と、それぞれの見方。上から順に見ると、前提が崩れている場合に早く気づける。

### 第1段階: 既定で効いているもの（設定不要）

シーンを組んだ時点で既に効いている。**これらが破綻していると以降の確認が無意味になる**ので最初に見る。

| 機能 | 見方 | 正しい状態 | 疑うところ |
|---|---|---|---|
| 多重散乱の補償 | 金具・ビーズを Smoothness 0.3 前後にする | 粗くしても黒く沈まない | 白飛びするなら `Energy Compensation` を 0 にして比較 |
| 鏡面遮蔽 | 脇の下・襟の内側の映り込み | AO が濃くても映り込みが残る | 消えるなら MaskMap の G を確認 |
| AO 多重バウンス | **白い衣装**の暗部 | 灰色でなく白いまま暗くなる | `AO Multi Bounce` を 0 にして差を見る |
| マイクロシャドウ | AO を焼いた布の皺 | 浅い角度の光で谷が締まる | AO 未設定なら効かない（仕様） |
| 影境界の AA | `Base Softness` を 0.03 にしてカメラを回す | 境界がちらつかない | ちらつくなら `Edge Anti-Aliasing` を確認 |
| ターミネータ距離減衰 | カメラを 40m まで引く | 境界の芯が消えている | 寄りで弱いなら Fade Start が近すぎる |
| シャドウ距離フェード | Shadow Distance の境目 | 影が滑らかに消える | ぷつりと切れるなら URP Asset 側の設定 |

### 第2段階: URP 側の設定が要るもの

シェーダーは対応済みだが、**URP Asset か Renderer Data を触らないと何も起きない**。

#### このパッケージが持つ Renderer Feature

**既定では 1 つも入っていない。** 入れるまで、対応するパスは
**一度も描かれない** ── 例外も警告も出ないので、マテリアル側を
いくら見直しても原因に辿り着けない。

| Feature | 何が動くか | マテリアル側 | 入れないと |
|---|---|---|---|
| `Toon Outline Feature` | 輪郭（独自 LightMode `IdolOutline`） | `Enable Outline` | **線が 1 本も出ない。** 実行時のコストも無いので、`Enable Outline` を 1 のままにしておいても害は無い |

**どの Renderer Data に入れるか。** URP Asset が参照している
`Universal Renderer Data` に入れる。品質レベルごとに URP Asset が
分かれているなら、**使うレベルの側**に入れること
── PC 用にだけ入れて Mobile で確認すると「入れたのに出ない」になる。

診断（`python check.py` または `Tools > Idol > セットアップ診断`）は、
**マテリアルが要求しているのに入っていない Feature** を名指しする。

| 機能 | 有効にする場所 | 確認 |
|---|---|---|
| ボックス投影 | URP Asset > Lighting > Reflection Probes > Box Projection | 室内でカメラを動かすと映り込みが壁に固定される |
| SSAO | Renderer Data に SSAO を追加 | キャラの凹部が暗くなる。§3.9 の注意も参照 |
| ライトクッキー | ライトに Cookie を設定 | 窓枠の影がキャラに落ちる |
| レンダリングレイヤー | URP Asset > Light Layers を ON | レイヤーを外したライトがキャラに当たらなくなる |
| APV | Lighting > Adaptive Probe Volumes をベイク | 屋内外を移動すると間接光の色が変わる |
| LOD クロスフェード | LOD Group の Fade Mode を Cross Fade | 切り替わりでポップしない |
| デカール | Renderer Data に Decal を追加 | キャラに汚れ・傷が乗る。レイヤーで絞る場合は Rendering Layers も ON |

### 第3段階: マテリアルで明示的に有効化するもの

既定 OFF。**1つずつ ON にして、前後で比較する**こと。まとめて入れると原因の切り分けができない。

| 機能 | マテリアル設定 | 見方 |
|---|---|---|
| 異方性 GGX（髪） | Hair > Use Anisotropic GGX | **環境反射が筋状に伸びる**。Intensity の取り直しが要る |
| 布の異方性 sheen | Cloth > Anisotropy を 0.5 | 織り方向に光沢が伸びる |
| ベントノーマル | Environment > Use Bent Normal + マップ | 壁際で間接光の入り方が変わる |
| ライト方向上書き | Light Direction Override | 背景と影の向きが意図的にずれる |
| 瞳の描画順 | §3.6 参照 | 前髪越しにハイライトが出る |
| 顔のシャドウキャスタ除外 | §3.7 参照 | 鼻の自己影が消える |
| Cavity（窪みの微細遮蔽） | Mask Map > Cavity Map + Strength | 縫い目・ベルト・靴の皺が締まり、そこの鏡面が引く |
| 布の sheen のエネルギー保存 | Cloth > Energy Conservation を 1 | 縁の明るさは残り、その下の下地が沈む |
| 追加光源の影色 | Shadow Color > from Add. Lights を 0 | リム光の色が正面の影に被らなくなる |

### 記録しておくこと

破綻を見つけたら、**どの段階のどの項目か**をメモしてください。第1段階の破綻は全体に波及するため、第2・第3段階の確認結果が信用できなくなります。
## 4. Volume

Global Volume を1つ置き、新規 Profile を作る。効かせる項目と値は `README_ToonPBR.md` §5 の表に従う。

Tonemapping だけ注意。**Neutral を使う。** ACES は影の階調を潰すので、このシェーダーで一番作り込む部分が見えなくなる。

---

## 5. マテリアルの割り当て

1. 部位ごとにマテリアルを作る（顔 / 肌 / 髪 / 服 / 金具）
2. 各マテリアルの `Surface Type` を設定する
3. 数値は `README_ToonPBR.md` §4 のレシピから始める
4. MaskMap / NPRMap は **sRGB OFF** で読み込む。ON のままだと Metallic と Smoothness がガンマを被って明らかに白っぽくなる

---

## 6. 組み終わったら確認すること

見た目の確認手順。上から順に潰すと原因の切り分けが早い。

| 何を | どう見るか | 対応する要件 |
|---|---|---|
| リム | Directional Light を背面に回し、脚とスカートの輪郭に光が乗るか | FR-11 |
| 環境反射 | 金具・ビーズに Probe 由来のハイライトが乗るか。真っ黒なら Probe 未 Bake | FR-12 |
| 曲率による軟らかさ | 頬・肩など曲率の高い面で影の境界が広く、太ももの平らな面で狭くなっているか | FR-01 |
| 影色 | 影の中をスポイトで拾い、明度が下がるだけでなく色相が回っているか | FR-02 |
| ターミネータ | 境界帯にだけ色が乗っているか。全体に乗っていたら Sharpness が低すぎる | FR-03 |
| Forward / Forward+ | URP Asset で切り替え、追加光源の当たり方が変わらないか | NFR-05 |
| バッチング | Frame Debugger で SRP Batch にまとまっているか | NFR-01 |

---

## 7. 症状と原因

`README_ToonPBR.md` §7 に加えて、セットアップ由来のもの。

| 症状 | 原因 |
|---|---|
| 顔だけ黒い／ちらつく | `FaceDirectionBinder` が無く `_HeadForward` が未設定（§1 参照）。付いているのに壊れる場合は `Forward / Right Axis` が真上／真下を向いている（コンソールに警告が出る） |
| リムが出ない | URP Asset の Depth Texture が OFF |
| 金属が真っ黒 | Reflection Probe が無い／背景を置く前に Bake した |
| キャラだけ浮く | ライト2灯の向きが揃っていない |
| 影が階段状 | Shadow Distance が長すぎる |
| Metallic が効きすぎ | MaskMap を sRGB ON で読み込んでいる |

# Origuma/EasyToon_URP/Idol — セットアップと数値レシピ

パッケージ（`com.origuma.easytoon-urp`）として導入すれば配置は不要です。
セットアップの手順（Renderer Feature / `FaceDirectionBinder` / ベイク）は [SETUP.md](SETUP.md)。

**マップは自分で描かなくて構いません。** インスペクタの「Baking」タブから、
Shade Normal / Hair Flow / Face SDF / Bent Normal / Curvature / SSS / Cavity / AO を焼けます
（中身は EasyShaderCore の Baker）。詳しくは [SETUP.md](SETUP.md)。

**全プロパティの一覧は [PROPERTIES.md](PROPERTIES.md)。**
この文書は「よく使う設定と数値レシピ」で、189 個すべては載せていません。
一覧はシェーダーとインスペクタから自動生成しているので、既定値や値域が実装とずれることがありません。

---

## 旧 Cel について

同梱していた姉妹シェーダー **Cel は T-356 で廃止**しました（用途が本シェーダーで
満たされたため一本化）。セル鏡面・2影・副次鏡面ローブなど Cel 固有の様式化
機能は「BRDF は物理ベースのまま維持する」方針に合わないため引き継いでいません。

---

## v1 から何が変わったか

| | v1 (ランプ式) | v2 (PBRハイブリッド) |
|---|---|---|
| 拡散 | ランプテクスチャで階調化 | 曲率駆動ソフトステップ + HSV影色 |
| 鏡面 | ステップ化 Blinn-Phong | GGX / Charlie sheen / Kajiya-Kay |
| 環境 | SH のみ | SH + リフレクションプローブ (IBL) |
| 影の境界 | 一定の硬さ | 面の曲率で自動的に変化 |
| 輪郭線 | 既定 ON | 既定 OFF |

ランプ方式が要らなくなったわけではありません。`Use Ramp Map` で上から被せられるので、特定の部位だけ階調を明示的に作りたいときに使ってください。

---

## 1. 最初にやること

1. **URP Asset > Depth Texture を ON**（リムが深度を読みます）
2. **URP Asset > Opaque Texture は不要**
3. シーンに **Reflection Probe** を置いて Bake
   これが無いと `Env Specular Intensity` が効かず、キャラだけ背景から浮きます。ここが v2 で一番重要な準備です
4. マテリアルを部位ごとに分けて `Surface Type` を設定

---

## 2. Surface Type の使い分け

| Type | 追加される処理 | 使う場所 |
|---|---|---|
| Default | GGX のみ | プラスチック、金属、硬い装備 |
| Skin | 皮下散乱の色混ぜ + 透過 | 肌、耳、指 |
| Face | SDF による影境界 + 肌の処理（皮下散乱・透過・頬の赤み） | 顔だけ |
| Hair | Kajiya-Kay 異方性 2層 | 髪 |
| Cloth | Charlie sheen + 透過 | 布、ドレス、リボン |

---

## 3. テクスチャの仕様

### MaskMap（PBR側 / **sRGB OFF**）

| ch | 用途 |
|---|---|
| R | Metallic |
| G | Occlusion |
| B | Thickness（Skin/Cloth の透過量。耳や薄い布を白に） |
| A | Smoothness |

### NPRMap（様式化側 / **sRGB OFF**）

| ch | 用途 |
|---|---|
| R | スペキュラマスク |
| G | 影のオフセット。0.5が基準、暗くすると影が入りやすい |
| B | リムマスク（顔だけリムを弱めたい等） |
| A | ランプ行インデックス（ランプを使う場合のみ） |

両方とも省略可（白テクスチャのまま動きます）。まず無しで組んで、破綻した部分だけ後から描き足すのが早いです。

**NPRMap の G が一番効きます。** 首の下、前髪の落ち影、袖の内側、スカートの内股。ここを描くかどうかで手描き感が決まります。

---

## 4. スクショに寄せる数値

送ってもらった画像から逆算した設定です。ここから微調整してください。

> **これは出発点であって、現在の値ではありません。** 実際のプロジェクトでは
> 部位ごと・キャラごとに詰めるので、ここの数値と一致しないのが普通です。
> **振って比べるときは `Tools > Idol > プリセットを適用`**（影の濃さ・鏡面・リムの
> 3軸を一括で切り替え）を使ってください。キャラ 1 体ぶんのマテリアル
> （このプロジェクトでは 20〜46 個）を手で触る必要はありません。
> 今の値が絵としてどうかは `Tools > Idol > セットアップ診断` が影／光の比で教えます。

### 共通（全マテリアル）

```
Shadow Threshold        0.50
Base Softness           0.14      ← 硬いセルにしたいなら 0.03
Curvature Influence     1.0       ← これが「境界の柔らかさが場所で変わる」の正体
Diffuse Wrap            0.30
Receive Realtime Shadow 0.65
Realtime Shadow Softness 0.40

Ambient Intensity       0.55
Ambient Flatten         0.40
Env Specular Intensity  0.35
Specular Intensity      0.2       ← 直接光の鏡面。0 で完全に消える
Smoothness Scale        0.25

Value Scale (Shadow)    0.62      ← 影色の明度。影／光の比を決める主因のひとつ
Intensity in Shadow     0.45      ← 影の中の環境光。**影を濃くする副作用の少ないノブ**
Cast Shadow Strength    0.45      ← 落ち影だけを別色で濃くする
```

**影の濃さは3つの値の合成で決まる**ので、単体で見ても判断できません（`Value Scale` を
下げても環境光が強ければ影は薄いまま）。上の3つはプリセットの「標準」の値で、
影／光の比がおよそ 0.54 になります。診断がこの比を計算して出します。

> **鏡面の既定値は 2026-08-02 に下げました。** `Specular Intensity` が無く base GGX が
> 常時フル出力だったため（BACKLOG T-087）、Metallic が 0 でも金属的に見えていました。
> **「金属っぽさ」の主因は Smoothness と環境鏡面**で、Metallic ではありません。
> まだ光るなら `Specular Intensity` を 0 に。環境の映り込みだけ残したいときは
> `Env Specular Intensity` を別に調整してください（0 にするとキャラだけ背景から浮きます）。

> **環境鏡面の明るさは 2026-08-02 に変わりました。** DFG 近似へ渡す粗さの単位を直した
> ためで（BACKLOG T-026）、**肌や布のような誘電体の「縁」が暗くなります**。逆光で白く
> 浮いていたのが正しい明るさに戻ったもので、金属は変わりません。
> 以前の見た目には**スカラー1つでは戻せません**（誤差が角度依存だったため）。

### 落ち影を濃くする（PBR 背景と並べるとき）

**ターミネータと落ち影は役割が違います。** 前者は形を見せるもので、濃くすると立体感が潰れます。後者は前後関係を見せるもので、**濃い方が PBR の背景と並べたときに芯が出ます。**

```
Cast Shadow Color       (0.30, 0.18, 0.22)   ← 暗く彩度のある影
Cast Shadow Strength    0.6                  ← 0 で従来どおり
```

掛かるのはシャドウマップ・HQ 影・コンタクトシャドウ・前髪の影 ── **「何かに遮られた」影だけ**です。頬や鼻の陰（NdotL 由来）には掛かりません。

**環境光にも同じ色が掛かります。** 拡散だけだと環境光に洗い流されて効きません（Ambient 2 の構成でターミネータ比 0.74 止まり。環境光にも掛けて 0.54）。

### 肌（Surface Type = Skin）

```
Smoothness Scale        0.35
Hue Shift              -0.04     （赤側へ）
Saturation Scale        1.35
Value Scale             0.80
Terminator Color        (1.0, 0.78, 0.68)
Terminator Strength     0.45
Subsurface Strength     0.7      ← **既定は 0（OFF）。使うなら明示的に上げる**
Transmission Strength   0.6      ← 同上
```

**散乱は既定 OFF です。** 既定で乗っていると肌以外にも回り込んで蝋のような質感になりやすいので、
必要な部位だけ上げる運用にしています。色・Power・Distortion は残してあるので、
Strength を上げれば上の値がそのまま出ます。**耳や鼻翼を透かしたいときだけ**触ってください。

頬の赤み（Blush）は **T-349 で廃止**しました。頬の色は肌テクスチャに描くか、`Skin - Subsurface`（皮下散乱）で出してください。

### 白いドレス（Surface Type = Cloth）

```
Smoothness Scale        0.25
Sheen Roughness         0.30
Sheen Intensity         0.7      ← 縁のふわっとした明るさ
Energy Conservation     0        ← 1 にすると物理的に正しくなる（下記）
Hue Shift              +0.04     （青紫側へ。白物は寒色に転ばせる）
Saturation Scale        1.5
Value Scale             0.78
Terminator Strength     0.25
```

> **`Energy Conservation` を 1 にすると sheen が物理的に正しくなります。** 現状の 0 は
> sheen を下地に足すだけなので、**縁でエネルギーが増えています**（この設定だと 43%）。
> 1 にすると sheen が反射するぶん下地を縮めます（glTF KHR_materials_sheen と同じ）。
> **縁の明るさ自体は残り**、その下が沈んで「光っているのは布の毛羽立ち」と読めるようになります。
> 既定を 0 にしてあるのは、これがバグではなく**足りていないモデル項**で、絵としてどちらを
> 採るかの判断が要るためです。設計思想に沿うのは 1 の方です。

白い布は元の彩度がほぼ0なので、`Saturation Scale` を上げても効きが薄いです。`Shadow Tint` に薄い青紫を直接入れる方が確実です。

### 髪（Surface Type = Hair）

```
Primary Shift           0.06
Primary Smoothness      0.72
Secondary Shift        -0.14
Secondary Smoothness    0.35
Hair Spec Intensity     0.9
Hue Shift              -0.02
Value Scale             0.72
```

### 金属パーツ・アーマー（Surface Type = Default）

```
Metallic Scale          1.0
Smoothness Scale        0.75
Spec AA Variance        0.2      ← ビーズや細かい金具のちらつき対策
Base Softness           0.05     ← 硬い物は境界も硬く
Curvature Influence     0.3
```

### リム（逆光を作る）

スクショは明確な逆光です。**まず Directional Light をカメラの向こう側に回してください。** シェーダー側だけでは作れません。

```
Rim Color               (1.0, 0.72, 0.45)  HDR強度 1.5〜2.5
Rim Intensity           1.8
Rim Width               1.5
Fresnel Falloff         2.5
Backlight Bias          0.75     ← 逆光のときだけ強く出す（画面全体に一様）
Directionality          1.0      ← 光が回り込んだ側の縁だけに出す（0 で全周）
Receive Cast Shadow     1.0      ← 落ち影の中では消す（NdotL の陰では消さない）
Depth Blend             0.6
```

### 輪郭線

スクショには**入っていません**。`Enable Outline` は OFF のままで。

どうしても入れるなら `Width 0.5` / `Blend with Albedo 0.7` くらいの、色が付いた極細の線に留めてください。黒い線を足した瞬間に十年前の絵になります。

---

## 5. ポストプロセス（Global Volume）

シェーダーと同じくらい効きます。

| エフェクト | 設定 |
|---|---|
| Tonemapping | **Neutral**。ACES は影の階調を潰します |
| Bloom | Threshold 0.95 / Intensity 0.4 / Scatter 0.7 |
| Depth of Field | Bokeh、Focus Distance をキャラに、Aperture 2.0前後。背景をしっかりボカす |
| Color Adjustments | Post Exposure でキャラと背景の露出を合わせる |
| White Balance | Temperature +5 くらい。全体をわずかに暖色へ |
| Vignette | 0.25 |
| Film Grain | 0.15。実写背景との粒状感の差を埋めます |

スクショの背景ボケの強さを見ると、被写界深度がかなり効いています。キャラを立たせている主因の一つです。

---

## 6. 背景と馴染ませる

シェーダー以外の要素で決まる部分です。

**ライトを2つに分ける。** 背景用の Directional Light と、キャラ専用の Directional Light（Culling Mask でキャラのレイヤーのみ）。方向は揃えて、強度と色だけ独立させる。キャラだけ 0.2 EV 明るくすると視線が集まります。

**リフレクションプローブは実際の背景を焼く。** 空のプローブだと金属が死にます。

**Ambient Flatten を 0 にしない。** 環境光の方向性がそのまま乗るとキャラの陰影が濁ります。0.3〜0.5 が目安。

**影が環境光で持ち上がって濁るなら `Intensity in Shadow` を下げる。** 環境光は影の内外に一律で乗るのが既定なので、屋外の明るい環境では影が浅くなります。`Tint in Shadow` に寒色を入れて影だけ転ばせる使い方もできます。

**Shadow Distance を詰める。** URP Asset の Shadow Distance が長いと影のテクセルが粗くなり、キャラの自己影がガタつきます。MMD的な近景カットなら 20〜30m で十分です。

---

## 7. 詰まったときに見るところ

| 症状 | 原因 |
|---|---|
| 金属が真っ黒 | Reflection Probe が無い／未 Bake |
| リムが出ない | URP Asset の Depth Texture が OFF |
| 影の境界が全部同じ硬さ | Curvature Influence が 0、または Curvature Map を焼いていない |
| **影がまったく出ない・のっぺり平ら** | `Ambient (SH) Intensity` が高すぎる。環境光は影の中にも一律で乗るので、主光源と同量まで上げると影が埋まる。**`Intensity in Shadow` を 0.5 前後に下げる**のが正攻法（全体の明るさを保ったまま影だけ沈む） |
| 影色が濁る | Saturation Scale を上げすぎ。1.2〜1.5 が実用域 |
| 顔の影が反転 | Flip SDF U、または Binder の軸設定 |
| 細かいパーツがちらつく | Spec AA Variance を 0.2〜0.3 に |
| **ライトを回すと影がちらつく** | ステップの幅がシャドウマップの粒度より狭い。`Edge Anti-Aliasing` が 0 になっていないか確認（既定 1 で自動的に吸収する）。それでも出るなら `Realtime Shadow Softness` を 0.6 前後へ。**解像度を上げるのは対症療法** |
| 全体が眠い | Terminator Strength を上げる。境界の芯が絵を締めます |

---

## 8. さらに先へ

### 追加済み（既定では OFF）

**前髪の影を顔に落とす専用パス。** 髪だけを頭上から正射影で焼いた深度を顔に投影します。顔の陰影を汚さずに髪の影だけ落とせます。組み方は `SETUP.md` §3.5。

**異方性 GGX の髪。** `Use Anisotropic GGX` を ON にすると Kajiya-Kay から切り替わり、環境反射まで筋状に伸びます。**`Anisotropy` の符号で伸びる向きが逆になります** ── 負が「毛を横切る帯（天使の輪）」、正が「毛に沿った縦の筋」。天使の輪だけが目的なら既定の Kajiya-Kay で足りるので、この機能は濡れ髪など**筋を出したいとき**に使ってください。**Fresnel を通すぶん暗く出る**ので `Hair Spec Intensity` を取り直してください。上の §4 のレシピは Kajiya-Kay 前提の値です。
**瞳の描画順制御（FR-22）。** Stencil で前髪より手前にハイライトを出せます。

---

## 7. 部位・演出の追加機能

**どれも既定 OFF**で、触らなければ絵は変わりません。**キーワードを 1 つも
持たない**ので、バリアントは増えません。

### 前髪透過（眉・睫毛を透かす）

瞳を前髪の手前に出す従来の方式（§6 の Stencil）とは**別方式**です。あちらは瞳が
不透明で手前に出ます。こちらは**前髪が半透明に透けて**下の眉・睫毛が見えます。
**併用しないでください**（使うステンシルのビットは分けてあるので誤爆はしません）。

インスペクタの Stencil セクションに 3 つのボタンがあります。**3 つ揃って初めて機能します。**

```
眉のマテリアル    「眉 (bit 2 を書く)」    Queue 2000
目のマテリアル    「目 (bit 4 を書く)」    Queue 2000
髪のマテリアル    「髪 (透過を有効化)」    Queue 2010 / See-Through Alpha 0.6
```

**髪の Queue を眉・目より後ろにすること。** 先にビットが書かれていないと抜けません。
**Render Mode で Queue を動かさないこと** ── Cutout を AlphaTest 帯（2450+）へ
動かすと眉が髪より後になり、仕掛けが壊れます。

> 斜めから見て睫毛が濃く見えるときは、透過ではなく**眉のアウトライン**が髪を
> 突き抜けています。Outline Width を 0 にするか Z Offset を上げてください。
> ZTest / ZWrite で直そうとしないこと（移植元で検証済み）。

### シアー生地（ストッキング・タイツ）

布を別メッシュで重ねずに、**視角依存の不透明度**で肌の上へ乗せます。
Surface Type が Skin か Cloth のとき「Sheer Fabric」に出ます。

```
Stocking Intensity      0.8
Stocking Color          (0.76, 0.65, 0.55)
Front Opacity           0.25     ← 正面。低いほど肌が透ける
Graze Power             1.5      ← 大きいほど縁だけが密になる
```

**光沢は Cloth - Sheen（物理ベースの Charlie sheen）で出してください。**
移植元にある加算の「すそ光沢」は二重になるので入れていません。

### アルベドの HSV 補正

テクスチャを描き直さずに色を振ります。**影側の Shadow Color (HSV) とは別で**、
こちらは素の色そのものを動かします（両方掛かります）。

```
Albedo Hue Shift       -0.02     （わずかに寒色へ）
Albedo Saturation       1.1
Albedo Value            1.0
```

> **Value は 1 で頭打ちです。** アルベドは反射率なので 1 を超えると
> 「入った以上の光を返す」ことになり、エネルギー保存が破綻します。
> 移植元は抑えていないので、1 以上を入れていたマテリアルは移行後に暗く見えます。

### 正面・上向きの陰の持ち上げ

**顔の自己陰をマスク無しで消すための仕組み**です。鼻や眉が作る陰は SDF で引いた
境界を汚すだけなので、テクスチャを描かずに潰せます。

```
Front Brightness        0.4      ← 正面を向いた面
Up Brightness           0.2      ← 上を向いた面
Falloff                 2.0
```

**逆光では効きません。** 背後から光が来ているときまで持ち上げると、シルエットを
抜く逆光リムが死ぬためです。顔の汚れが気になるときは、まずここを 0.3 ほど上げて
から FR-26（顔をシャドウキャスタから外す）を検討してください。

### MatCap（加算のアクセント）

```
MatCap Intensity        0.5
MatCap (RGB)            球状のライティングを焼いた画像
Align to Light          0.5      ← 画面内の光の向きへ回す
```

**加算だけです。** 乗算は環境光の主経路を上書きできてしまうので持ちません。
`Align to Light` を上げると、MatCap 特有の「カメラに貼り付いて見える」弱点が
減ります（光源が動くとハイライトも動く）。0 で従来どおり固定です。

### ディゾルブ（消失演出）

```
Dissolve Progress       0 → 1    ← ここを動かす
Axis                    WorldY
Start Y / End Y         0 / 2    （ワールド座標のメートル）
Noise (R)               ノイズテクスチャ
Edge Width              0.05
Edge Glow (HDR)         (1, 0.6, 0) × 3 くらい
```

**影・深度・法線のパスでも同じ場所が切れます。** ここを省くと消えたはずの部分の
影だけが残って輪郭が宙に浮きます。`Progress` が 0 なら分岐ごと飛ぶので、
使わないマテリアルの負担はありません。

### 影の中に残す鏡面

```
Specular in Shadow      0.1      ← 既定。従来の焼き込み値そのもの
```

0 にすると影の中の鏡面が完全に消えます。移植元の 184 マテリアル中 92 が
ここを既定から動かしていたので、ノブに出しました。**既定のままなら絵は変わりません。**


### まだ無いもの

**MMD のトゥーンテクスチャ（toon01〜10.bmp）はそのままでは使えません。** MMD は **V 軸**を「光の当たり具合」に使いますが、`_RampMap` は **U 軸**を使います（V は行インデックス）。そのまま割り当てると U 方向の一定値を引くだけになり陰影が消えます。転置したものを用意してください。なお標準の toon02 は暗い側でも G/B が 12% 落ちるだけで、`Shadow Color (HSV)` の方が遥かに強く効きます。

**マットキャップの乗算モード。** MatCap 自体は**加算のアクセントとして**入りました（下記）。乗算は入れていません ── **環境の映り込みはリフレクションプローブと SH から取る**のがこのシェーダーの設計で（背景とキャラを繋ぐ主経路）、乗算のマットキャップはそれを固定の絵で上書きしてしまいます。加算なら物理の上に載るだけなので主経路が保たれます。

**回転したリフレクションプローブ。** URP の `REFLECTION_PROBE_ROTATION` は未対応です。キーワードが増える割にキャラクターで効く場面が無いため。

**前髪の影の複数キャラ同時対応。** グローバルに1組しか持てません（REQUIREMENTS §8）。

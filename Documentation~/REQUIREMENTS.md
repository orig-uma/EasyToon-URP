# ToonNPR 要件定義

最終更新: 初版

---

## 1. 目的

アニメ調のキャラクターを、フォトリアルな背景の中に破綻なく成立させる URP 向けシェーダー一式を作る。

達成したい見た目を一文で言うと「**PBR のライティングを受けながら、拡散光の階調だけが絵として様式化されている状態**」。硬いセルシェードでも、単なる PBR でもない中間を狙う。

---

## 2. スコープ

### 含む

- キャラクター用シェーダー本体と、それが依存するエディタ拡張
- 顔・髪・肌・布・金属を1つのシェーダーで扱うための分岐
- 前髪透過、スクリーンスペース輪郭などの Renderer Feature
- マテリアル設定を扱いやすくするカスタムインスペクタ
- 数値のプリセット（部位ごとのマテリアル雛形）

### 含まない

- 背景・小物のシェーダー（URP Lit を使う）
- アニメーション、リギング、モデル制作
- ポストプロセススタックの自作（URP 標準の Volume を使う）
- モバイル向け最適化（当面は据え置き / PC を対象）

---

## 3. 動作環境

| 項目 | 要件 |
|---|---|
| Unity | 2022.3 LTS 以降 |
| URP | 14.0.x 以降 |
| 色空間 | Linear のみ |
| レンダーパス | Forward および Forward+ |
| グラフィックス API | DX11 / DX12 / Vulkan / Metal |
| 必須設定 | URP Asset の Depth Texture が ON |
| 必須アセット | シーンを焼いた Reflection Probe |
| 検証環境 | Unity 6000.3.8f1 / URP 17.3.0（このリポジトリで実際に動かしている構成） |

**Forward+ のキーワードはバージョンで名前が違う。** URP 17 で `_FORWARD_PLUS` が非推奨になったため、ForwardLit パスは `_CLUSTER_LIGHT_LOOP` を宣言している。URP 14〜16 に落とす場合はここを `_FORWARD_PLUS` に戻すこと。戻さなくてもコンパイルは通るが、Forward+ 時に追加光源がクラスタ経路を通らず NFR-05 を満たさなくなる。

---

## 4. 機能要件

### 実装済み

| ID | 要件 | 実装箇所 |
|---|---|---|
| FR-01 | 拡散光の伝達関数を、曲率で軟らかさが変化するソフトステップとして実装する（**既定 OFF**。`_CurvatureSoftness = 0` で帯の幅は一定） | `ToonLightResponse` |
| FR-02 | 影側の色を HSV 空間で色相回転・彩度スケール・明度スケールできる | `ToonShadowAlbedo` |
| FR-03 | 影の境界帯にのみ別色を乗せられる | `ToonTerminatorBand` / `ToonDiffuseColor` |
| FR-04 | リアルタイム影を拡散光と同じ伝達関数に通し、境界の質を揃える | `ToonLightResponse` |
| FR-05 | 鏡面反射に GGX（D / V / F を分離した実装）を用いる | `ToonD_GGX` ほか |
| FR-06 | 布に Charlie 分布の sheen を加算できる | `ToonD_Charlie` / `ToonV_Ashikhmin` |
| FR-07 | 髪に Kajiya-Kay の異方性ハイライトを2層で乗せられる | `ToonStrandSpecular` |
| FR-08 | 肌に皮下散乱の色混ぜと裏側からの透過を加えられる | `_SURFACETYPE_SKIN` 分岐 |
| FR-09 | 顔の影境界を SDF テクスチャで制御し、法線由来の破綻を避ける | `_SURFACETYPE_FACE` 分岐 |
| FR-10 | 頭ボーンの向きをシェーダーに供給する | `FaceDirectionBinder.cs` |
| FR-11 | 深度差とフレネルを組み合わせたリムライトを、逆光度合いで変調する | `ToonRimLight` |
| FR-12 | リフレクションプローブから鏡面環境光を取得する | `ToonSampleEnvSpecular` |
| FR-13 | SH による拡散環境光の方向性を平坦化できる | `ToonShadeIndirect` |
| FR-14 | 法線の変化率から粗さを補正し、細部のちらつきを抑える | `ToonFilterRoughness` |
| FR-15 | 追加光源を主光源と同じ伝達関数で処理する | ForwardLit の光源ループ |
| FR-16 | 法線押し出しによる輪郭線（既定 OFF） | Outline パス |
| FR-17 | 平滑法線を頂点カラーへベイクする | `SmoothNormalBaker.cs` |
| FR-18 | 任意でランプテクスチャによる階調を上書きできる | `_UseRampMap` |
| ~~FR-20~~ | ~~髪専用シャドウマップの顔への投影~~ **T-344 で廃止** ── 投影が頭上からの固定でライト方向と無関係なため、動くライトで原理的に嘘が出る。前髪→顔の影は HQ セルフシャドウ（FR-19 系）で出す | （撤去済み） |
| ~~FR-21~~ | ~~深度・法線・マテリアルIDから Sobel を取るスクリーンスペース輪郭~~ | **T-380 で廃止**（実プロジェクトで未導入・MSAA と両立せず、押し出し輪郭で足りる） |
| ~~FR-30~~ | ~~頬から鼻先にかけての赤みを NdotV 由来の分布で乗せる~~ | **T-349 で廃止**（全マテリアルが強度 0 ＝ 未使用。頬の色は肌テクスチャか皮下散乱で出す） |
| FR-31 | 影の中に入った部分の環境光を、影の外と別扱いにする | `ToonShadeIndirect` の `mainLit` 引数 |
| FR-32 | カメラ距離でターミネータの強度を落とす | `ToonTerminatorFade`（既定 20m→40m） |
| FR-33 | 拡散光のライト方向をマテリアルごとに手で上書きできる | `ToonOverrideLightDir` / `_LightOverrideOn` |
| FR-34 | 布の sheen をしわ方向に沿って異方化する | Cloth 分岐のハーフベクトル変形 |
| FR-40 | GGX の多重散乱を補償し、粗い金属のエネルギー欠損を埋める | `ToonEnvBRDFMultiScatter` / `ToonEnergyCompensation` |
| FR-41 | 鏡面の遮蔽を AO 直掛けではなく視線と粗さから求める | `ToonSpecularOcclusion` |
| FR-42 | リフレクションプローブをボックス投影し、映り込みを空間に合わせる | `ToonBoxProjectReflection` |
| ~~FR-43~~ | ~~接地部のコンタクトシャドウ~~ **T-344 で廃止** ── 画面外の遮蔽物が影を落とさない・ディザが TAA 無しで這うという画面空間原理の弱点のため。接地の影は HQ セルフシャドウへ一本化 | （撤去済み） |
| FR-44 | URP の SSAO を遮蔽として受け取る | `_SCREEN_SPACE_OCCLUSION` 分岐 |
| FR-45 | ベントノーマルを間接拡散のサンプル方向に使う | `ToonContext.bentN` / `_BentNormalOn` |
| FR-46 | AO の多重バウンスを補正し、暗部がアルベドの色を保つようにする | `ToonAOMultiBounce` |
| FR-47 | 主光源のライトクッキーとシャドウ距離フェードを受ける | `GetMainLight` 3引数版 / `_LIGHT_COOKIES` |
| FR-48 | ライトのレンダリングレイヤーで当たり判定する | `IsMatchingLightLayer` / `_LIGHT_LAYERS` |
| FR-49 | Adaptive Probe Volumes から間接拡散を取得する | `SampleProbeVolumePixel` / `PROBE_VOLUMES_L1` |
| FR-50 | AO と入射角から細かい凹凸の自己遮蔽を作る | `ToonMicroShadow` |
| FR-51 | 影境界のソフトさに画面空間の下限を設け、ジャギを防ぐ | `ToonLightResponse` の `edgeAA` |
| FR-52 | LOD Group のクロスフェードに対応する | `LOD_FADE_CROSSFADE`（4パス） |
| FR-53 | URP のデカールを受ける | `ApplyDecal` / `_DBUFFER_MRT1/2/3` |
| FR-54 | DepthNormals からレンダリングレイヤーを書き出す | `EncodeMeshRenderingLayer` / `_WRITE_RENDERING_LAYERS` |
| FR-55 | 主光源の影を回転 Vogel PCF と PCSS で自前サンプルする | `ToonSampleMainShadowHQ` / `_HQ_SHADOW_ON` |
| ~~FR-56~~ | ~~顔 SDF を 4ch 化し、非対称な顔と上下光に対応する~~ | **T-382 で廃止**。16bit 1ch ＋ 距離場ブレンド ＋ Cast Shadow のベイクが品質で上回った。Idol の顔 SDF は 16bit 1ch（R×256+G）のみ |
| FR-57 | アウトラインを独自 LightMode で分離し、ForwardLit のバッチを守る | `ToonOutlineFeature.cs` / LightMode `ToonOutline` |
| FR-58 | クリアコート（二層目の鏡面）と薄膜干渉を加えられる | `ToonV_Kelemen` / `ToonIridescence` |
| FR-59 | 焼いた曲率マップで境界の広がりを場所ごとに制御する（**唯一の曲率供給源**。画面空間推定は T-381 で撤去） | `_CurvatureMap` / `_CurvatureSoftness` |
| FR-60 | 毛流れマップで髪の繊維方向を決める | `ToonHairStrandDir` / `_HairFlowMap` |
| FR-61 | 陰ランプだけを平滑法線で駆動する | `ToonContext.shadeN` / `_ShadeNormalMap` |
| FR-62 | 透過を法線方向へ曲げ、SSS マップの方向と厚みを使う | `_TransmissionDistortion` / `_SSSMap` |
| FR-63 | 環境反射に地平線遮蔽を掛け、面の裏側への映り込み漏れを防ぐ | `ToonShadeIndirect` の horizon |
| FR-64 | リフレクションプローブを重なりで混ぜ、境界を跨いでも映り込みが飛ばない | `ToonSampleEnvSpecular`。Forward+ は八面体アトラス、Forward は SpecCube0/1 |
| FR-66 | 布の sheen が反射するぶん下地を縮め、エネルギーを増やさない | `ToonSheenAlbedo`。既定 OFF（`Energy Conservation` で有効化） |
| FR-67 | 焼いた Cavity マップで窪みの微細遮蔽を作り、アルベドと鏡面の両方に掛ける | `_CavityMap` / `_CavityStrength`。法線マップが無いモデルの主なディテール源 |
| FR-68 | 追加光源が影色の下駄を足す量を制御できる | `_AddLightShadowColor`。既定 1（従来どおり） |
| FR-69 | 中間量をデバッグ表示できる | `_DebugMode`。15 種。動的分岐でバリアント増加なし |
| FR-70 | 落ち影だけを別の色で濃くでき、ターミネータの階調は保つ | `_CastShadowColor` / `_CastShadowColorStrength`。既定 0 で従来どおり |
| FR-71 | 髪の副バンドを毛束の粒に割り、縁で強める | `_HairStrandScale` / `_HairStrandSparkle` + 副バンドのフレネル |
| FR-72 | リムが光の回り込んだ側の縁だけに出て、遮蔽された場所では消える | `_RimDirectionality` / `_RimReceiveShadow`。どちらも既定 1 |
| FR-73 | 影色を albedo の彩度に依存せず任意の色相へ寄せられる（乗算では無彩色の面に色が入らない） | `_ShadowColor` / `_ShadowColorMix`。Rec.709 輝度を合わせてから lerp するので**濃さは不変**。既定 Mix 0 で従来どおり |
| FR-74 | 間接鏡面が持ち去ったエネルギーを間接拡散から引く（合計が 1 を超えないようにする） | `ToonShadeIndirect` 内。**実際に足した量**（`envBRDF × _EnvSpecIntensity × specOcclusion`）と同じ量を引く。ノブは持たない |
| FR-75 | 直接光の鏡面が持ち去ったエネルギーを拡散から引ける（縁の締まり） | `_SpecEnergyConservation`。**既定 0 で従来どおり。** 縁で最大 23% と見える量なのでノブにしてある |
| FR-33 | MatCap を**加算のアクセント**として乗せられる（光の向きに追従できる） | `ToonMatCap` / `_MatCapIntensity` / `_MatCapTex` / `_MatCapColor` / `_MatCapLightAlign`。**乗算モードは持たない** ── 環境光の主経路（プローブ + SH）を上書きできてしまうため。既定 0 で分岐ごと飛ぶ |
| ~~FR-31~~ | ~~正面・上向きの面の陰を持ち上げる~~ | **T-370 で廃止**（用途の「顔の自己陰の消去」は SDF が受け持ち、実使用 0/46 件。方向付きのバウンス光はフィルライトが担う） |
| FR-32 | 影の中に残す鏡面の量をノブにする | `_SpecShadowFloor`。**既定 0.1 は従来の焼き込み値**なので絵は変わらない。0 で影の中の鏡面が完全に消える |
| FR-30 | ディゾルブ（消失演出）。縁が発光し、影・深度・法線でも同じ場所が切れる | `ToonDissolve` / `ToonDissolveClip` / `_DissolveAmount` ほか 11。**キーワードを持たない** ── `_DissolveAmount > 0` の一様分岐で切るのでバリアントが増えない。勾配は頂点で 1 float に畳んで運ぶ |
| FR-29 | シアー生地（ストッキング・タイツ）を布メッシュ無しで肌の上に重ねられる | `ToonStockingLayer` / `_StockingIntensity` / `_StockingColor` / `_StockingMask` / `_StockingFrontOpacity` / `_StockingPower`。**視角依存の不透明度**が本体で、正面は糸の隙間から肌が透け、シルエットへ寄るほど密に見える。**拡散色を作る前**に掛けるので影色にも布が乗る。既定 OFF |
| FR-28 | 素のアルベドを HSV で振れる（テクスチャを描き直さずに色を変える） | `ToonAlbedoHSV` / `_AlbedoHueShift` / `_AlbedoSaturation` / `_AlbedoValue`。**影側の `_Shadow*` とは別物**で両方掛かる。結果は 1 で頭打ち ── アルベドは反射率なので、1 を超えるとエネルギー保存と多重散乱の補償が破綻する |
| FR-27 | 前髪を半透明にして眉・睫毛を透かす（眉・目がステンシルに書いた画素だけ） | `HairSeeThrough` パス（`SRPDefaultUnlit`）/ `_HairSeeThroughAlpha` / §6 の値域。**キーワードは持たない** ── ゲートはステンシルで、眉と目がビットを書いていなければ 1 画素も描かない |
| FR-22 | 瞳のハイライトを前髪より手前に出す描画順制御 | ForwardLit の Stencil ブロック / §6 の値域 |
| FR-26 | 顔メッシュをシャドウキャスタから除外する仕組み | `_ShadowCasterOff`（ShadowCaster パス） |
| FR-23 | 髪の鏡面を異方性 GGX にし、環境反射まで筋状に伸ばす | `ToonD_GGXAniso` / `ToonStrandSpecularGGX` / `ToonAnisoReflectVector` |
| FR-24 | カスタム ShaderGUI で Surface Type に応じた項目のみ表示する | `Editor/ToonPBRShaderGUI.cs` |
| FR-25 | 絵の方向を軸で一括適用でき、原因の切り分けもできる | `ToonPBRPresets`（Tools > Idol > プリセットを適用）。影の濃さ／色味／鏡面／リムの4軸に加え、ちらつきの切り分け・顔のシャドウキャスタ・材質ID。サーフェスタイプごとに違う値が入る |

### 未実装

**現在なし。** 未着手の項目は `BACKLOG.md` の「判断待ち」に移した（既定を変えるか絵として要るかの判断が要るもの）。

---

## 5. 非機能要件

| ID | 要件 | 検証方法 |
|---|---|---|
| NFR-01 | SRP Batcher と互換であること。全マテリアルプロパティが単一の `UnityPerMaterial` に入る | `shader_lint.py` の E001 / E002 / E005 が 0 件。Frame Debugger で SRP Batch にまとまることを確認 |
| NFR-02 | ランタイムのマテリアル書き込みは「**Play = マテリアルインスタンス / Edit = 非破壊 MPB**」の二層に統一する（`FaceDirectionBinder`。Doll の `DollLiveDirector` と同型）。MPB を付けた Renderer は SRP Batcher から外れ、顔の Renderer は目・眉・睫毛・口も抱えるので巻き添えが大きい（実測 8 マテリアル）── Play 中は MPB を使わない。共有マテリアル資産へ直接書く経路は持たない（他キャラを巻き込み .mat を汚すため。旧 `Write To Material` トグルは廃止） | Frame Debugger で Play 中の顔 Renderer が SRP Batch に残ることを確認 |
| NFR-03 | マテリアル単位の分岐は `shader_feature_local` を使い、不要なバリアントを生まない | ビルドログのバリアント数を確認 |
| NFR-04 | 全パスが Opaque キューで完結する。半透明への依存を作らない | `Queue` タグの確認 |
| NFR-05 | Forward と Forward+ の両方で同じ絵が出る | 両モードで目視比較 |
| NFR-06 | 自前関数は `Toon` 前置詞を持ち、URP の識別子と衝突しない | `shader_lint.py` では検出しない。レビュー項目 |
| NFR-07 | URP のバージョン差が大きい API に依存しない | 該当箇所は `CLAUDE.md` に列挙済み |
| NFR-08 | 機能が無効になっている状態をエディタから検出できる | `ToonPBRSetupCheck`（Tools > Idol > セットアップ診断） |


**キーワード名の表記について。** T-037 で「動的分岐優先」の方針を入れたとき、`_USE_RAMP_MAP` や `_BENT_NORMAL_ON` といった `shader_feature` は**プロパティによる動的分岐に置き換えた**（バリアント数を抑えるため）。この表の実装欄はプロパティ名（`_UseRampMap` など）で書いてある。**現在キーワードとして残っているのは `_ALPHATEST_ON` / `_CONTACT_SHADOW_ON` / `_HQ_SHADOW_ON` / `_OUTLINE_ON` / `_SURFACETYPE_*`（5 値）の 5 種。** バリアントを増やすので、足す前に動的分岐で足りないかを先に考えること。

---

## 6. マテリアルの外部仕様

### Surface Type

排他的な列挙。部位ごとにマテリアルを分けて設定する。

| 値 | 追加される処理 |
|---|---|
| Default | GGX のみ |
| Skin | 皮下散乱の色混ぜ + 透過 |
| Face | SDF による影境界 |
| Hair | Kajiya-Kay 異方性 2層 |
| Cloth | Charlie sheen + 透過 |

### MaskMap（sRGB OFF）

| ch | 用途 |
|---|---|
| R | Metallic |
| G | Occlusion |
| B | Thickness（Skin / Cloth の透過量） |
| A | Smoothness |

### NPRMap（sRGB OFF）

| ch | 用途 |
|---|---|
| R | 鏡面マスク |
| G | 影のオフセット（0.5 が基準） |
| B | リムマスク |
| A | ランプ行インデックス |

両方とも未設定で動作すること。テクスチャを必須にしない。

**NPRMap は `Use NPR Map` を OFF にしたときが中立**（鏡面マスク 1 / 影オフセット 0 / リムマスク 1）。白テクスチャは G=1 ＝影オフセット最大であり中立ではないため、既定テクスチャに頼らずトグルで切り替える。MaskMap は白がそのまま中立（Metallic は `_Metallic` が 0、Occlusion 1、Smoothness は `_Smoothness` が握る）。

### Stencil の値域

URP は `StencilUsage` で **bit 4〜7 を予約**している（bit 4 = ライト形状、bit 5〜6 = マテリアル種別、bit 7 = 予約）。**ユーザーが使えるのは bit 0〜3（`0x0F`）だけ。** ここを踏むと Deferred やライトのステンシルと衝突する。

この範囲の中での割り当て:

| bit | 値 | 用途 |
|---|---|---|
| 0 | `0x01` | 前髪（瞳を手前に出すためのマスク・FR-22） |
| 1 | `0x02` | 眉（前髪透過のマスク・FR-27） |
| 2 | `0x04` | 目（前髪透過のマスク・FR-27） |
| 3 | `0x08` | 未使用。他の用途に足すならここから取る |

**FR-22 と FR-27 は別方式で、使うビットを分けてある。** 前者は瞳が不透明で
前髪の手前に出る。後者は前髪が半透明に透けて眉・睫毛が下に見える。
ビットが違うので、片方を設定したマテリアルにもう片方が誤爆することはない。
**ただし絵としては両立しない**ので、どちらかを選ぶこと。

設定の組み合わせ（インスペクタの Advanced にプリセットボタンがある）:

| | 髪マテリアル | 瞳マテリアル |
|---|---|---|
| Ref | 1 | 1 |
| Comp | Always | Equal |
| Pass Op | Replace | Keep |
| Read Mask | 0x0F | **0x01** |
| Write Mask | **0x01** | 0x00 |
| Z Test | LEqual | **Always** |
| Render Queue | 既定 (2000) | **2010** |

**Read Mask を 0x01 に絞るのが要点。** 絞らないと URP がビット 5〜6 に書いたマテリアル種別まで比較に入り、`Equal` が成立しなくなる。

**Write Mask を 0x01 に絞るのも同様。** 広いままだと髪が URP の予約ビットを潰す。

前髪透過（FR-27）の組み合わせ。**3 つ揃って初めて機能する**:

| | 眉マテリアル | 目マテリアル | 髪マテリアル |
|---|---|---|---|
| Ref | 2 | 4 | 0 |
| Comp | Always | Always | **Equal** |
| Pass Op | Replace | Replace | Keep |
| Read Mask | 0x0F | 0x0F | **0x06** |
| Write Mask | **0x02** | **0x04** | 0x00 |
| Z Test | LEqual | LEqual | LEqual |
| Render Queue | 2000 | 2000 | **2010** |

**髪の Queue を眉・目より後ろにすること。** 先にビットが書かれていないと
髪が抜けない。**Render Mode で Queue を動かさないこと** ── Cutout を
AlphaTest 帯（2450+）へ動かすと眉が髪より後になり、この仕掛けが壊れる。

髪の `Read Mask` を `0x06` に絞るのは上と同じ理由。広いままだと
URP がビット 5〜6 に書いた値まで比較に入り、`Equal` が成立しなくなる。

既定値はどのマテリアルも「何もしない」状態（Ref 0 / Comp Always / Pass Keep）なので、設定しなければ従来どおり描画される。

---

## 7. 受け入れ基準

新しい実装が完了したと言えるのは、以下をすべて満たしたとき。

**共通**

- `python3 tools/shader_lint.py Assets/ToonNPR/Shaders --strict` がエラー 0・警告 0 で通る
- Unity が使える環境では `ShaderCompileCheck.RunCI` が終了コード 0 で抜ける
- 新しいプロパティを足した場合、本書の該当 FR とマテリアル外部仕様が更新されている

**FR-21（スクリーンスペース輪郭）**

- 法線押し出しでは出せない内側の線が出る
- カメラ距離を変えても線幅が破綻しない
- Renderer Feature を無効にすると完全に元の絵に戻る

**FR-22（瞳の描画順）**

- 前髪が瞳に重なる角度でハイライトが手前に出る
- 他のキャラや背景の描画に影響しない

**FR-24（カスタムインスペクタ）**

- Surface Type を切り替えると無関係な項目が消える
- インスペクタ経由でキーワードが正しく設定される
- ShaderGUI を外しても既存マテリアルが壊れない

---

## 8. リスクと既知の制約

**実コンパイル検証ができない環境がある。** Unity 無しでは静的検査までしか行えない。エージェントが作業する場合、検証済みの範囲を報告に明示すること。

**参考にしている絵は動画からの1フレーム。** 光源の数、プローブの設定、ポスト処理の詳細は推測を含む。数値は完全一致を目指すものではなく出発点。

**バリアント数が増えやすい。** Surface Type が5値、その他のトグルが複数あるため、機能追加のたびに掛け算で増える。新しいキーワードを足す前に、既存のもので表現できないか検討する。

**モデル依存の前提がある。** 顔 SDF は顔専用 UV を前提にしており、アウトラインの平滑法線ベイクはモデルの Read/Write 有効化を要求する。汎用アセットとして配る場合は別途対応が必要。

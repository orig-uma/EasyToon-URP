// ============================================================================
//  Origuma/EasyToon_URP/Idol
//  Unity 2022.3 LTS / URP 14+ 想定
//
//  BRDF は物理ベースのまま、拡散光の伝達関数だけを様式化するハイブリッド。
//  ToonPBRCommon.hlsl を同じフォルダに置くこと。
// ============================================================================
Shader "Origuma/EasyToon_URP/Idol"
{
    Properties
    {
        [Header(Surface Type)][Space(4)]
        [KeywordEnum(Default, Skin, Face, Hair, Cloth)] _SurfaceType ("Surface Type", Float) = 0

        [Space(10)][Header(Base)][Space(4)]
        [MainTexture] _BaseMap   ("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor ("Base Color", Color) = (1,1,1,1)
        [Toggle] _NormalMapOn ("Use Normal Map", Float) = 0
        [Normal] _BumpMap        ("  Normal Map", 2D) = "bump" {}
        _BumpScale               ("  Normal Scale", Range(0,2)) = 1
        // ディテールマップ（T-368。Doll と同名）: タトゥー・チーク等を A の
        // 合成率でベースへ重ねる。ベースと独立したタイリング（ST）を持つ。
        [Toggle] _DetailOn       ("Use Detail Map", Float) = 0
        _DetailMap               ("  Detail Map (RGB=color A=blend)", 2D) = "black" {}
        _DetailColor             ("  Detail Color", Color) = (1,1,1,1)
        [NoScaleOffset][Normal] _DetailNormalMap ("  Detail Normal Map", 2D) = "bump" {}
        _DetailNormalScale       ("  Detail Normal Scale", Range(0,2)) = 1
        // **Render Mode（GUI）が設定する派生状態。** 直接いじる入口は持たない
        // ── カットアウトはブレンド・キュー・RenderType とセットで決まるので、
        // トグル単独で切ると食い違う（T-358）。
        [HideInInspector][Toggle(_ALPHATEST_ON)] _AlphaClipOn ("Alpha Clip", Float) = 0
        _Cutoff                  ("  Cutoff", Range(0,1)) = 0.5

        // テクスチャを描き直さずに色を振るための補正。既定は無変化。
        // 影側の Shadow Color HSV とは別で、こちらは**素の色**を動かす。
        _AlbedoHueShift          ("Albedo Hue Shift", Range(-0.5,0.5)) = 0
        _AlbedoSaturation        ("  Albedo Saturation", Range(0,2)) = 1
        _AlbedoValue             ("  Albedo Value", Range(0,2)) = 1

        [Space(10)][Header(Mask Map    R Metallic    G Occlusion    B Thickness    A Smoothness)][Space(4)]
        _MaskMap                 ("Mask Map", 2D) = "white" {}
        // 高精細から焼いた微細遮蔽（窪み）。法線マップが無いモデルでは
        // これが唯一のディテール源になる。R チャンネルのみ。
        _CavityMap               ("  Cavity Map (R)", 2D) = "white" {}
        _CavityStrength          ("  Cavity Strength", Range(0,1)) = 0
        _Metallic                ("  Metallic Scale", Range(0,1)) = 0
        _Smoothness              ("  Smoothness Scale", Range(0,1)) = 0.25
        _OcclusionStrength       ("  Occlusion Strength", Range(0,1)) = 1
        _DirectOcclusion         ("  Apply AO to Direct Light", Range(0,1)) = 0.3
        // AO と入射角から細かい凹凸の自己遮蔽を作る。AO が無ければ何も起きない。
        _MicroShadow             ("  Micro Shadow", Range(0,1)) = 1

        [Space(10)][Header(NPR Map    R SpecMask    G ShadowOffset    B RimMask    A RampIndex)][Space(4)]
        // 未使用時は中立値を使う。白テクスチャは G=1（＝影オフセット最大）で
        // 中立ではないため、既定テクスチャに頼れない。
        [Toggle] _NPRMapOn       ("Use NPR Map", Float) = 0
        _NPRMap                  ("  NPR Map", 2D) = "white" {}
        _NPRShadowOffsetStrength ("  Shadow Offset Strength", Range(0,1)) = 0.4

        [Space(10)][Header(Diffuse Transfer)][Space(4)]
        _ShadowThreshold         ("Shadow Threshold", Range(0,1)) = 0.5
        _ShadowSoftness          ("  Base Softness", Range(0.001,0.5)) = 0.12
        // 曲率の供給源は焼いた Curvature Map だけ（T-381）。画面微分の推定は
        // 三角形ごとに一定で陰に面が並ぶため撤去した。0.5 が平坦＝無変化。
        _CurvatureSoftness       ("  Curvature Influence", Range(0,4)) = 0
        _CurvatureMap            ("  Curvature Map (R)", 2D) = "gray" {}
        // 陰ランプ専用の平滑法線。鏡面やリムには影響しない。
        [Normal] _ShadeNormalMap ("  Shade Normal Map", 2D) = "bump" {}
        _ShadeNormalStrength     ("  Shade Normal Strength", Range(0,1)) = 0
        _DiffuseWrap             ("  Diffuse Wrap", Range(0,1)) = 0.25
        _ReceiveShadowStrength   ("  Receive Realtime Shadow", Range(0,1)) = 0.7
        _ShadowAttenSoftness     ("  Realtime Shadow Softness", Range(0.001,1)) = 0.35
        // 硬いセル設定で境界が 1px を切ったときのジャギ止め。0 で従来どおり。
        _ShadowEdgeAA            ("  Edge Anti-Aliasing", Range(0,2)) = 1

        [Space(10)][Header(High Quality Self Shadow    main light only)][Space(4)]
        // これだけキーワード。全経路コンパイルで occupancy が落ちるため。
        [Toggle(_HQ_SHADOW_ON)] _HQShadowOn ("Enable HQ Self Shadow", Float) = 0
        _HQShadowSoftness        ("  Penumbra (texels)", Range(0,1)) = 0.3
        // 半影の広がり方。深度差（＝遮蔽物までの距離）に掛ける倍率。
        // 上げるほど「離れた影ほど柔らかい」が強く出る。
        _ShadowPenumbraScale     ("  Penumbra Scale", Range(0,1000)) = 200
        _ReceiverNormalBias      ("  Receiver Normal Bias", Range(0,4)) = 1
        [Toggle] _ShadowContactHardening ("  Contact Hardening (PCSS)", Float) = 0

        [Space(10)][Header(Shadow Color   HSV)][Space(4)]
        _ShadowHueShift          ("Hue Shift", Range(-0.2,0.2)) = -0.03
        _ShadowSaturation        ("Saturation Scale", Range(0,3)) = 1.3
        _ShadowValue             ("Value Scale", Range(0,1)) = 0.75
        _AddLightShadowColor     ("  Shadow Color from Add. Lights", Range(0,1)) = 1
        _ShadowTint              ("Tint (multiply)", Color) = (1,1,1,1)
        // 影色を「掛ける」のではなく、その色相へ**寄せる**ための組。
        // 掛け算は減法混色なので、色を持つ Tint を掛けると2つの色相が
        // 打ち消し合って彩度が落ち、影が濁る（「濡れた色紙」に見える）。
        // さらに元の色が無彩色に近い面（白い布・銀髪）は Saturation Scale が
        // 効かないため、掛け算だけでは**影に色を入れる手段が無い**。
        // 寄せる方式なら albedo の彩度に関係なくこの色相が出る。既定 0 で従来どおり。
        _ShadowColor             ("Shadow Hue (mix toward)", Color) = (0.50, 0.32, 0.62, 1)
        _ShadowColorMix          ("  Hue Mix", Range(0,1)) = 0
        // 落ち影（シャドウマップ・前髪由来）だけを別の色で濃くする ── **影の色の話**で、影が落ちるかどうかは変えない
        // （それは Receive Realtime Shadow）。
        // NdotL 由来のターミネータには掛からない。既定 0 で従来どおり。
        _CastShadowColor         ("Cast Shadow Color", Color) = (0.5, 0.45, 0.5, 1)
        _CastShadowColorStrength ("  Cast Shadow Color Strength", Range(0,1)) = 0

        [Space(10)][Header(Terminator)][Space(4)]
        _TerminatorColor         ("Terminator Color", Color) = (1.0, 0.82, 0.72, 1)
        _TerminatorStrength      ("  Strength", Range(0,1)) = 0.35
        _TerminatorSharpness     ("  Sharpness", Range(0.1,8)) = 2.0
        // 引きの画で線として煩くなるので距離で消す。寄り（〜20m）では従来どおり。
        _TerminatorFadeStart     ("  Fade Start (m)", Range(0,200)) = 20
        _TerminatorFadeEnd       ("  Fade End (m)", Range(0,200)) = 40

        [Space(10)][Header(Optional Ramp Override)][Space(4)]
        [Toggle] _UseRampMap ("Use Ramp Map", Float) = 0
        _RampMap                 ("  Ramp Map", 2D) = "white" {}
        _RampRowCount            ("  Ramp Row Count", Float) = 8
        _RampIndexOverride       ("  Ramp Index Override (-1 = use NPR.a)", Float) = -1
        _RampStrength            ("  Blend", Range(0,1)) = 1

        [Space(10)][Header(Specular)][Space(4)]
        // 直接光の鏡面の倍率。移植元（EasyToon の Idol）と同名・同意味。
        _SpecularIntensity       ("Specular Intensity", Range(0,4)) = 0.2
        // 鏡面が持ち去ったエネルギーを拡散から引く。**既定 0（従来どおり）。**
        // 間接光側（FR-74）は影響が 1% 未満なので常時入れているが、
        // 直接光は縁で最大 23% と**見える量**なので、入れるかどうかは絵の判断。
        // 1 にすると縁の拡散が締まり、リムとの重なりが物理的に正しくなる。
        _SpecEnergyConservation  ("  Energy Conservation", Range(0,1)) = 0
        _SpecularTint            ("Specular Tint", Color) = (1,1,1,1)
        _SpecularTintStrength    ("  Tint Strength", Range(0,1)) = 0
        // 多重散乱の補償。1 が物理的に正しい。0 にすると従来（単散乱のみ）に戻る。
        _EnergyCompensation      ("  Energy Compensation", Range(0,1)) = 1
        // 2 ローブ目（T-369。Doll のデュアルローブから輸入・同名）。シャープな
        // 芯の下に広いマットなにじみを敷く、肌・シルクの定番。0 で分岐ごとスキップ。
        [HDR] _SecSpecularColor  ("  2nd Lobe Color (HDR)", Color) = (1,1,1,1)
        _SecSpecularIntensity    ("  2nd Lobe Intensity", Range(0,2)) = 0
        _SecSmoothness           ("  2nd Lobe Smoothness", Range(0.01,1)) = 0.2
        // クリアコート（二層目の鏡面）。0 で分岐ごとスキップされる。
        _ClearcoatStrength       ("  Clearcoat", Range(0,1)) = 0
        _ClearcoatSmoothness     ("  Clearcoat Smoothness", Range(0,1)) = 0.9
        // 薄膜干渉。真珠・玉虫塗り。0 で色が付かない。
        _IridescenceIntensity    ("  Iridescence", Range(0,1)) = 0
        _IridescenceThickness    ("  Iridescence Thickness", Range(0,4)) = 1
        _IridescenceShift        ("  Iridescence Shift", Range(0,1)) = 0
        // 影の中に残す鏡面。**既定 0.1 は従来の焼き込み値**なので、
        // 触らなければ絵は変わらない。0 で影の中の鏡面が完全に消える。
        _SpecShadowFloor         ("  Specular in Shadow", Range(0,1)) = 0.1
        _SpecAAVariance          ("  Spec AA Variance", Range(0,1)) = 0.15
        _SpecAAThreshold         ("  Spec AA Threshold", Range(0,1)) = 0.2

        [Space(10)][Header(Glitter)][Space(4)]
        // ラメ・スパンコール（T-348）。プロパティ群は Doll と同名・実装は
        // Core の BRDF_Glitter を共有。Intensity 0 で UNITY_BRANCH により
        // マスクのフェッチごとスキップ＝キーワード不要でバリアント非増。
        [NoScaleOffset] _GlitterMask ("Glitter Mask (R)", 2D) = "white" {}
        [HDR] _GlitterColor      ("  Glitter Color (HDR)", Color) = (2,2,2,1)
        _GlitterIntensity        ("  Glitter Intensity", Range(0,50)) = 0
        _GlitterScale            ("  Glitter Density (Scale)", Range(10,1000)) = 100
        _GlitterSize             ("  Dot Size", Range(0.0005,0.05)) = 0.005
        _GlitterTilt             ("  Normal Tilt Strength", Range(0,2)) = 0.8
        _GlitterSparsity         ("  Sparsity", Range(0,1)) = 0.5
        _GlitterIridescence      ("  Iridescence Amount", Range(0,1)) = 0.5
        _GlitterIridescenceShift ("  Iridescence Shift", Range(0,1)) = 0.5
        _GlitterBaseReflection   ("  Base Reflection", Range(0,0.5)) = 0.05

        // シアー生地（ストッキング・タイツ）。布を別メッシュで重ねずに、
        // **視角依存の不透明度**で肌の上へ手続き的に乗せる。既定 OFF。
        // 正面は糸の隙間から肌が透け、シルエットへ寄るほど糸が重なって密に見える。
        //
        // **移植元にある加算の「すそ光沢」は入れていない。** ToonPBR は
        // 物理ベースの Charlie sheen（下の Sheen）を持っており、
        // ライト非依存の加算光沢を重ねると二重になる。布の光沢はそちらで出すこと。
        [Space(10)][Header(Sheer Fabric    stockings and tights)][Space(4)]
        _StockingIntensity       ("Stocking Intensity", Range(0,1)) = 0
        _StockingColor           ("  Stocking Color", Color) = (0.76, 0.65, 0.55, 1)
        [NoScaleOffset] _StockingMask ("  Stocking Mask (R)", 2D) = "white" {}
        _StockingFrontOpacity    ("  Front Opacity", Range(0,1)) = 0.25
        _StockingPower           ("  Graze Power", Range(0.5,8)) = 1.5

        // 散乱は既定 OFF。**必要な部位で明示的に上げる**運用にする。
        // 既定で乗っていると肌以外にも回り込み、蝋のような質感になりやすい。
        // 色と Power / Distortion は残してあるので、Strength を上げれば以前の値で出る。
        [Space(10)][Header(Skin    only when SurfaceType is Skin)][Space(4)]
        _SubsurfaceColor         ("Subsurface Color", Color) = (1.0, 0.55, 0.45, 1)
        _SubsurfaceStrength      ("  Strength", Range(0,2)) = 0
        _TransmissionColor       ("Transmission Color", Color) = (1.0, 0.35, 0.25, 1)
        _TransmissionPower       ("  Power", Range(1,16)) = 4
        _TransmissionStrength    ("  Strength", Range(0,4)) = 0
        // 光を法線方向へ曲げてから裏面成分を取る。0 だと透過が均一になる。
        _TransmissionDistortion  ("  Distortion", Range(0,1)) = 0.2
        // ベイクした SSS。RGB=透過方向（接線空間） A=厚み。0 で MaskMap の B を使う。
        _SSSMap                  ("  SSS Map (RGB=dir A=thickness)", 2D) = "bump" {}
        _SSSMapStrength          ("  SSS Map Strength", Range(0,1)) = 0

        [Space(10)][Header(Cloth Sheen    only when SurfaceType is Cloth)][Space(4)]
        _SheenColor              ("Sheen Color", Color) = (1,1,1,1)
        _SheenRoughness          ("  Sheen Roughness", Range(0.02,1)) = 0.3
        _SheenIntensity          ("  Intensity", Range(0,4)) = 0.6
        _SheenEnergyConservation ("  Energy Conservation", Range(0,1)) = 0
        // 0.9 止まりなのは、1.0 だとハーフベクトルが織り方向と一致したとき
        // 縮めた結果が 0 ベクトルになって normalize が壊れるため。
        _ClothAnisotropy         ("  Anisotropy", Range(0,0.9)) = 0
        [Toggle] _ClothTangentSwap ("  Use Bitangent as Weave Dir", Float) = 0

        [Space(10)][Header(Hair    only when SurfaceType is Hair)][Space(4)]
        [Toggle] _HairTangentSwap ("Use Bitangent as Strand Dir", Float) = 1
        [Toggle] _HairAnisoGGXOn ("  Use Anisotropic GGX (off = Kajiya-Kay)", Float) = 0
        // **符号で伸びる向きが逆。** 負 = 毛を横切る帯（天使の輪）／正 = 毛に沿った縦の筋。
        _HairAnisotropy          ("  Anisotropy", Range(-1,1)) = 0.8
        _HairShiftMap            ("  Shift Noise (R)", 2D) = "gray" {}
        // 毛流れ（倍角エンコード R=cos2t G=sin2t B=信頼度）。UV ミラーで接線が
        // 反転する髪でもエンジェルリングが割れなくなる。0 で接線をそのまま使う。
        // 未割り当ての既定は **black**。B は信頼度なので 0 =「データが無い」で、
        // 強度をいくつにしても接線そのままに落ちる。
        // **"white" だった。** それだと RG=(1,1) が信頼度 1 で渡り、
        // マップを割り当てないまま強度を上げると**ハイライトが 22.5 度回る**。
        // 何も割り当てていないのに向きが変わるので、原因に辿り着けない。
        _HairFlowMap             ("  Hair Flow (RG=dir B=conf)", 2D) = "black" {}
        // 混合率は saturate(信頼度 × 強度)。焼いた信頼度が低いマップでも
        // 試せるよう上限を 1 より上に取る（信頼度 0.07 なら 1 では 7% しか効かない）。
        _HairFlowStrength        ("  Hair Flow Strength", Range(0,8)) = 0
        _HairSpecColor1          ("  Primary Color", Color) = (1,1,1,1)
        _HairShift1              ("  Primary Shift", Range(-1,1)) = 0.08
        _HairSmoothness1         ("  Primary Smoothness", Range(0,1)) = 0.7
        _HairSpecColor2          ("  Secondary Color", Color) = (0.75,0.85,0.8,1)
        _HairShift2              ("  Secondary Shift", Range(-1,1)) = -0.12
        _HairSmoothness2         ("  Secondary Smoothness", Range(0,1)) = 0.35
        _HairSpecIntensity       ("  Intensity", Range(0,4)) = 1.0
        // 毛束の粒。副バンドを UV 方向のノイズで割る。0 で従来どおり滑らかな帯。
        _HairStrandScale         ("  Strand Scale", Range(0,200)) = 50
        _HairStrandSparkle       ("  Strand Sparkle", Range(0,1)) = 0

        [Space(10)][Header(Face SDF    only when SurfaceType is Face)][Space(4)]
        // 16bit 1ch（R×256+G）の一方式だけ（T-382）。Baking タブが焼く形式そのもの。
        // 8bit の R だけだと閾値が約 0.7 度刻みの階段になり、ライトを回すと
        // 影の線がカクつく。**非圧縮テクスチャ必須**（BC 圧縮は RG の連続性を壊す）。
        // 既定 white = R,G とも 1 → 1.0 = 最後まで照らされる（SDF 無しと同じ絵）。
        _FaceSDFMap              ("Face SDF (16-bit R*256+G)", 2D) = "white" {}
        [Toggle] _FaceSDFFlipU   ("  Flip SDF U", Float) = 0
        _FaceShadowOffset        ("  Shadow Offset", Range(-0.5,0.5)) = 0
        _FaceFlatness            ("  SDF Blend", Range(0,1)) = 1
        // 下向きの面（顎の裏・首）は SDF を切って法線の陰影へ戻す（T-376）。
        // SDF のスイープは水平面内なので光の仰角を知らず、顎裏を「照らされる」と
        // 焼いてしまう。隣の首は N·L で正しく陰るため、つなぎ目で段差になる。
        _FaceSDFBlendNormalMin   ("  SDF Blend Normal Min", Range(-1.5,1)) = -1
        _FaceSDFBlendNormalMax   ("  SDF Blend Normal Max", Range(-1,1.5)) = 0
        // FaceDirectionBinder が無いときにオブジェクトの軸（+Z 正面 / +X 右）で代用する。
        // 頭の回転には追従しないので、首を振る演出では Binder を付けること。
        [Toggle] _FaceUseObjectAxis ("Fallback to Object Axis", Float) = 1

        // **SRP Batcher は「なぜ無いか」を聞かない。**
        // `FaceDirectionBinder` が毎フレーム書く値なので Properties に出す
        // 必要は無い ── と思って `// lint:script-set` で検査を免除していたが、
        // SRP Batcher は `UnityPerMaterial` の全メンバーが Properties にあることを
        // **無条件で**要求する。1 つ欠けるとそのシェーダーは丸ごと非対応になり、
        //   `SRP Batcher: not compatible`
        //   `UnityPerMaterial var is not declared in shader property section (_HeadForward)`
        // と出る。**バッチングが効かないだけで絵は正しい**ので、
        // インスペクタを見ている限り気付けない（T-338）。
        [HideInInspector] _HeadForward ("Head Forward (script)", Vector) = (0,0,0,0)
        [HideInInspector] _HeadRight   ("Head Right (script)",   Vector) = (0,0,0,0)

        [Space(10)][Header(Rim Light)][Space(4)]
        // Fresnel (PBR)（**既定**）は EasyPBR(Doll) と同じ Core の式（T-343）。リムが
        // ライトのエネルギーに比例し、ステージ照明の色・強度がそのまま縁に乗る。
        // 深度差方式（Screen Silhouette）は背後が近い・画面端・遮蔽で消える弱点が
        // あり（利用者実測）、既定を Fresnel にした。縁取り線が要る材質だけ 0 へ。
        [Enum(Screen Silhouette,0,Fresnel PBR,1)] _RimMode ("Rim Mode", Float) = 1
        [HDR] _RimColor          ("Rim Color", Color) = (1.0, 0.75, 0.5, 1)
        _RimIntensity            ("  Intensity", Range(0,8)) = 1.5
        _RimWidth                ("  Width", Range(0,10)) = 1.5
        _RimThreshold            ("  Depth Threshold", Range(0,0.5)) = 0.02
        _RimSoftness             ("  Depth Softness", Range(0.001,0.5)) = 0.05
        _RimFresnelPower         ("  Fresnel Falloff", Range(0.1,8)) = 2.5
        _RimBacklightBias        ("  Backlight Bias", Range(0,1)) = 0.7
        // 光が回り込んだ側の縁だけに出す。0 だとシルエット全周に等しく出る。
        _RimDirectionality       ("  Directionality", Range(0,1)) = 1
        // 落ち影の中ではリムを消す。0 だと遮蔽物の影の中でもシルエットが光る。
        _RimReceiveShadow        ("  Receive Cast Shadow", Range(0,1)) = 1

        [Space(6)]
        // 産毛（ピーチファズ）。リムと同じ「縁の光沢」だが、リムが**光が回り込んだ
        // 縁**に出るのに対し、こちらは**面が光源を向いているほど**出る（産毛が
        // 順光で白く光る現象）。Doll と同名・同値域なので値をそのまま持ち込める。
        [HDR] _FuzzColor         ("Peach Fuzz Color (HDR)", Color) = (1.0, 0.95, 0.9, 1.0)
        _FuzzIntensity           ("  Peach Fuzz Intensity", Range(0,5)) = 0
        _FuzzPower               ("  Peach Fuzz Width", Range(0.1,10)) = 4
        _RimDepthBlend           ("  Depth Blend", Range(0,1)) = 0.6
        // Fresnel (PBR) モードの太さ。Doll と同じ写像（0 = 極細・指数 12 / 1 = 極太・指数 0.5）。
        _RimFresnelThickness     ("  Fresnel Thickness (PBR)", Range(0,1)) = 0.3

        [Space(10)][Header(Bent Normal    unoccluded direction for indirect light)][Space(4)]
        [Toggle] _BentNormalOn ("Use Bent Normal", Float) = 0
        [Normal] _BentNormalMap  ("  Bent Normal Map", 2D) = "bump" {}

        [Space(10)][Header(Environment)][Space(4)]
        _AmbientIntensity        ("Ambient (SH) Intensity", Range(0,2)) = 0.5
        _AmbientFlatten          ("  Flatten", Range(0,1)) = 0.4
        // 1 で暗部がアルベドの色を保つ。0 で従来どおり素の AO を掛ける。
        _AOMultiBounce           ("  AO Multi Bounce", Range(0,1)) = 1
        _ShadowAmbientTint       ("  Tint in Shadow", Color) = (1,1,1,1)
        _ShadowAmbientIntensity  ("  Intensity in Shadow", Range(0,2)) = 1
        _EnvSpecIntensity        ("Env Specular Intensity", Range(0,2)) = 0.35
        _EnvSpecFlatten          ("  Roughness Push", Range(0,1)) = 0.1

        [Space(10)][Header(Light Direction Override    intentionally non physical)][Space(4)]
        [Toggle] _LightOverrideOn ("Override Light Direction", Float) = 0
        _LightOverrideYaw        ("  Yaw (deg)", Range(-180,180)) = 0
        _LightOverridePitch      ("  Pitch (deg)", Range(-89,89)) = 30
        // 拡散だけ回すと金具のハイライトと影の向きが割れるので、既定は鏡面も回す。
        [Toggle] _LightOverrideSpecular ("  Rotate Specular Too", Float) = 1

        [Space(10)][Header(Emission)][Space(4)]
        [Toggle] _EmissionOn ("Enable Emission", Float) = 0
        _EmissionMap             ("  Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor     ("  Emission Color", Color) = (0,0,0,1)

        [Space(10)][Header(Outline    reference look uses none)][Space(4)]
        [Toggle(_OUTLINE_ON)] _OutlineOn ("Enable Outline", Float) = 0
        [Toggle] _UseSmoothNormal ("  Use Baked Smooth Normal", Float) = 0
        [Toggle]  _UseVertexWidth  ("  Width Mask from vertex color A", Float) = 0
        _OutlineColor            ("  Color", Color) = (0.2,0.15,0.18,1)
        _OutlineAlbedoBlend      ("  Blend with Albedo", Range(0,1)) = 0.5
        _OutlineAlbedoDarken     ("  Albedo Darken", Range(0,1)) = 0.45
        _OutlineWidth            ("  Width", Range(0,10)) = 0.8
        _OutlineZOffset          ("  Z Offset", Range(0,1)) = 0
        _OutlineMaxDistance      ("  Fade Distance", Range(1,100)) = 25

        // デバッグ表示。絵から逆算しにくい量を直接見る。動的分岐なのでバリアントは増えない。
        // **[Enum] ドロワーを付けないこと（T-375）。** Unity の Enum ドロワーは名前/値の対を
        // **最大 7 組**しか受け付けず、14 組だと「Failed to create material drawer」を
        // ドロワー適用のたび（Inspector 表示・描画時のカリング）にスタックトレース付きで
        // 吐いて Inspector が引っかかる。選択肢は GUI（ToonPBRShaderGUI.DrawDebug）が
        // 自前の Popup で出す。値の対応表もそちらが唯一の出所。
        _DebugMode               ("Debug View", Float) = 0

        // ディゾルブ（消失演出）。**キーワードを持たない** ── 既に 270 万
        // バリアントあり、ここへ足すとシェーダー全体が倍になる。
        // Amount 0 で分岐ごと飛ぶので、使わないマテリアルの負担はほぼ無い。
        // 正面・上向きの面の陰を持ち上げる。**顔の自己陰をマスク無しで消す**ための
        // 仕組みで、移行元の 184 マテリアル中 101（54%）が使っている。
        // 拡散の伝達関数を様式化する方向なので、このプロジェクトの方針と正面から合う。
        // **逆光では効かない** ── 背後から光が来ているときまで持ち上げると
        // シルエットを抜く逆光リムが死ぬ。
        [Space(10)][Header(Fill Light    bounce)][Space(4)]
        // 陰側に注ぐ方向付きのバウンス光（T-370。Doll と同名）。床の照り返しが
        // 典型。旧「陰の持ち上げ」（_FrontLift* 系）はこの輸入と同時に廃止した
        // ── 用途（顔の自己陰の消去）は SDF が受け持ち、実使用も 0 件だった。
        [HDR] _FillColor         ("Fill Light Color (HDR)", Color) = (0.4, 0.45, 0.6, 1)
        _FillIntensity           ("  Fill Light Intensity", Range(0,2)) = 0
        _FillPitch               ("  Fill Light Pitch", Range(-90,90)) = -60
        _FillYaw                 ("  Fill Light Yaw", Range(-180,180)) = 0
        _FillShadeOnly           ("  Fill Shade Side Only", Range(0,1)) = 1

        // MatCap。**加算のアクセントに限る** ── 乗算は環境の主経路
        // （プローブ + SH）を上書きできてしまうので持たない。既定 OFF。
        // Light Align を上げると画面内の光の向きへ回り、「カメラに貼り付いて
        // 見える」MatCap 特有の弱点が減る。
        [Space(10)][Header(MatCap    additive accent only)][Space(4)]
        _MatCapIntensity         ("MatCap Intensity", Range(0,5)) = 0
        [NoScaleOffset] _MatCapTex ("  MatCap (RGB)", 2D) = "black" {}
        [HDR] _MatCapColor       ("  Tint (HDR)", Color) = (1,1,1,1)
        _MatCapLightAlign        ("  Align to Light", Range(0,1)) = 0

        [Space(10)][Header(Dissolve)][Space(4)]
        _DissolveAmount          ("Dissolve Progress", Range(0,1)) = 0
        [Toggle] _DissolveInvert ("  Invert", Float) = 0
        [Enum(None,0,WorldY,1,LocalY,2)] _DissolveType ("  Axis", Float) = 1
        _DissolveStartY          ("  Start Y", Float) = 0
        _DissolveEndY            ("  End Y", Float) = 2
        [NoScaleOffset] _DissolveTex ("  Noise (R)", 2D) = "white" {}
        _DissolveNoiseScale      ("  Noise Scale", Float) = 1
        _DissolveNoiseStrength   ("  Noise Strength", Range(0,1)) = 0.5
        [HDR] _DissolveEdgeColor ("  Edge Glow (HDR)", Color) = (1, 0.6, 0, 1)
        [HDR] _DissolveEdgeColor2("  Edge Char Color (HDR)", Color) = (1, 0, 0, 1)
        _DissolveEdgeWidth       ("  Edge Width", Range(0.001,0.5)) = 0.05
        [Toggle] _DissolveEdgeStep ("  Step Edge (toon)", Float) = 0

        // 暗転（T-361）。最終色を黒へ寄せる。輪郭パスにも同じ値が掛かる。
        // **アルファは触らない** ── 消えるのではなく「黒く沈む」演出。
        _BlackOut                ("Black Out", Range(0,1)) = 0

        [Space(10)][Header(Light Conditioning and Anti Blowout)][Space(4)]
        // ステージ照明からキャラの可読性を守る防御層（Doll から輸入・T-350）。
        // **既定はすべて素通し**（influence 1 / satLimit 1 / minBright 0 / limit 0）。
        // Doll の既定（limit 1.0・Blend Max）とは違うが、既存マテリアルの絵を
        // 変えないためにこちらは「無効」から始める。
        _LightColorInfluence     ("Light Color Influence", Range(0,1)) = 1
        _LightSaturationLimit    ("  Light Saturation Limit", Range(0,1)) = 1
        _LightMinBrightness      ("  Light Min Brightness", Range(0,1)) = 0
        // 1 灯あたりの拡散光の輝度上限。**0 = OFF**（分岐ごとスキップ）。
        _DiffuseLightLimit       ("Diffuse Light Limit (0 = Off)", Range(0,5)) = 0
        // 追加光源の合成。Add = 物理的（重なると白飛びする）/ Max = アニメ向け
        // （最も強い 1 灯だけが効くので彩度が残る）。既定は従来どおり Add。
        [Enum(Add, 0, Max, 1)] _AdditionalLightBlendMode ("Additional Light Blend", Float) = 0

        [Space(10)][Header(Render State)][Space(4)]
        // 描画モード（不透明 / カットアウト / 半透明。T-358）。
        // **個別に触らないこと。** GUI の Render Mode が この 3 つと
        // _AlphaClipOn・renderQueue・RenderType タグをまとめて設定する。
        // 半透明は不透明キューの外へ出るので深度プリパスに載らない
        //（＝深度モードのリム・SSAO の対象から外れる）。
        [HideInInspector] _SurfaceTransparent ("Alpha Blend (Transparent)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Z Test", Float) = 4
        // 深度オフセット（lilToon の Offset Factor / Units 相当。T-348）。
        // 眉・睫毛を顔面のわずかに手前に浮かせる用途（負で手前・正で奥）。
        // 本体・前髪透過・深度・法線パスに同じ値を掛けて深度の食い違いを防ぐ。
        // ShadowCaster には掛けない ── 影の自己遮蔽はシャドウバイアスの管轄。
        _OffsetFactor            ("Offset Factor", Float) = 0
        _OffsetUnits             ("Offset Units", Float) = 0
        [Toggle] _ShadowCasterOff ("Exclude from Shadow Map", Float) = 0

        // 瞳を前髪より手前に出すための Stencil（FR-22）。
        // 既定値は「何もしない」状態。値域は REQUIREMENTS.md §6 を見ること。
        [Space(6)][Header(Stencil    see REQUIREMENTS section 6 for the bit range)][Space(4)]
        _StencilRef              ("Ref", Range(0,15)) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Comp", Float) = 8
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilPass ("Pass Op", Float) = 0
        _StencilReadMask         ("Read Mask", Range(0,255)) = 15
        _StencilWriteMask        ("Write Mask", Range(0,255)) = 15

        // 前髪透過（FR-27）。眉・目がステンシルに書いた画素の上へ、髪を半透明で
        // 重ね描きする。**ゲートはステンシルそのもの**で、キーワードは持たない
        // ── 眉と目がビットを書いていなければ 1 画素も描かれない。
        // 眉／目／髪の3つを揃えて初めて成立する。部位プリセットが一括で設定する。
        [Space(6)][Header(Hair See Through    set all three parts via the presets)][Space(4)]
        _HairSeeThroughAlpha     ("See-Through Alpha", Range(0,1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType"            = "Opaque"
            "RenderPipeline"        = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue"                 = "Geometry"
        }
        LOD 300

        // ====================================================================
        //  Pass 1 : ForwardLit
        // ====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            // 既定は Blend One Zero / ZWrite On ＝ 不透明。
            // 半透明にすると GUI が SrcAlpha OneMinusSrcAlpha / ZWrite Off を入れる。
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            // 瞳を前髪の上に出すときだけ Always にする。既定は LEqual。
            ZTest [_ZTest]
            Offset [_OffsetFactor], [_OffsetUnits]

            // 髪が書き、瞳が読む。専用パスを足さず ForwardLit に持たせているのは、
            // 書き込みのためだけに髪をもう一度ラスタライズするのが無駄だから。
            // 描画順は Render Queue で担保する（瞳を Geometry+10 など後ろへ）。
            Stencil
            {
                Ref       [_StencilRef]
                Comp      [_StencilComp]
                Pass      [_StencilPass]
                ReadMask  [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   ToonVert
            #pragma fragment ToonFrag

            #pragma shader_feature_local          _ALPHATEST_ON
            #pragma shader_feature_local_fragment _HQ_SHADOW_ON
            #pragma shader_feature_local_fragment _SURFACETYPE_DEFAULT _SURFACETYPE_SKIN _SURFACETYPE_FACE _SURFACETYPE_HAIR _SURFACETYPE_CLOTH

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            // 頂点ライトは実装していないので _ADDITIONAL_LIGHTS_VERTEX は宣言しない。
            // 宣言してもコードが同一のバリアントが1つ増えるだけ。URP 側が
            // このキーワードを立てても、宣言が無ければ OFF のバリアントが使われる。
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            // URP 17 (Unity 6) で _FORWARD_PLUS は非推奨になり、互換シムが警告を出す。
            // LIGHT_LOOP_BEGIN 側は _CLUSTER_LIGHT_LOOP を見るのでこちらに揃える。
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            // _REFLECTION_PROBE_BLENDING は宣言しない（T-025 で実装済み）。
            // 自前の ToonSampleEnvSpecular が常にブレンド経路を通すので、
            // 切り替えるキーワードが要らない。プローブが1つなら重み 1 の
            // 単一サンプルに畳まれるだけで、宣言するとバリアントが倍になる。

            // URP Asset の Reflection Probes > Box Projection に連動する。
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            // SSAO の Renderer Feature が有効なときだけ立つ。
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            // 窓枠・木漏れ日など、ライト側のクッキーを受ける。
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            // ライトのレンダリングレイヤーで当たり判定する。
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            // URP のデカール。Renderer Feature 側の MRT 構成で変わる。
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3

            // Adaptive Probe Volumes のキーワードは URP がこのファイルで配っている。
            // 自前で multi_compile を書くと URP 側の更新に追従できない。
            //
            // 通常の #include だと中の #pragma が無視される（コンパイル警告になるだけで
            // エラーにならないため、バリアントが作られないまま気付かない）。
            // pragma を拾わせるには #include_with_pragmas を使う。URP 本体も同じ。
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_fog
            // **GPU インスタンシングは宣言しない（Doll と揃えた）。**
            //
            //   - キャラは SkinnedMeshRenderer で描くので**そもそも
            //     インスタンシングされない。** 利得はゼロ
            //   - `UNITY_INSTANCING_BUFFER` を持たないので、変種が増える以外に
            //     できることが無い（全パスで 2 倍）
            //   - **マテリアルの Enable GPU Instancing に印が入った瞬間、
            //     そのレンダラーは SRP Batcher から外れる。** Unity は
            //     インスタンシングを優先するため。得の無い側に倒れる罠
            //
            // `UNITY_VERTEX_INPUT_INSTANCE_ID` などのマクロは残してあるが、
            // この pragma が無ければ何にも展開されないので害は無い。

            // LOD グループのクロスフェード。全パスで同じディザを掛けないと
            // 色と深度が食い違う。URP と同じくキーワードで切り替える。
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "ToonPBRCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

            #include "Passes/ForwardPass.hlsl"
            ENDHLSL
        }

        // ====================================================================
        //  Pass 2 : HairSeeThrough — 前髪透過（既定では何も描かない）
        //
        //  眉・目がステンシルに書いた画素の上にだけ、**ForwardLit と同一の
        //  ライティングで**髪を半透明に重ね描きする。仕掛けは2段:
        //
        //    1. 髪の ForwardLit が穴を空ける（Comp Equal / Ref 0 / ReadMask 6）
        //    2. その穴をこのパスが埋める（Comp NotEqual で、同じ画素だけ描く）
        //
        //  瞳を ZTest Always で手前に出す従来の方式（FR-22）とは別物で、
        //  **どちらか一方を使う。** あちらは瞳が不透明で手前に出る。
        //  こちらは髪が透けて眉・睫毛が下に見える。
        //
        //  LightMode は独自タグ IdolHairSeeThrough（T-341）。以前は SRPDefaultUnlit で
        //  Feature 不要が利点だったが、URP が UniversalForward と同じ描画パスで処理する
        //  ため [本体][透過] が交互に並び、**SetPass が跳ね上がって ForwardLit が
        //  SRP Batcher でまとまらなかった**（T-040 の輪郭と同じ問題）。
        //  HairSeeThroughFeature が不透明の後にまとめて描く（Idol Setup から追加）。
        //
        //  描画順「眉・目 → 髪透過」は Feature 化で構造的に保証される
        //  （不透明が全部終わってから描くため）。部位プリセットの Queue は
        //  不透明内の順（眉・目 → 髪本体）にはそのまま必要。
        //
        //  「斜めから見ると睫毛が濃く見える」は透過ではなく**眉のアウトライン**が
        //  髪を突き抜けているのが原因（移植元で検証済み）。ZTest / ZWrite で
        //  直そうとしないこと。Outline Width を 0 にするか Z Offset を上げる。
        // ====================================================================
        Pass
        {
            Name "HairSeeThrough"
            Tags { "LightMode" = "IdolHairSeeThrough" }

            // (stencil & 6) != 0 の画素、つまり眉(2)・目(4) が書いた所だけ。
            // 髪が書くビット(1) とは別のビットなので、従来のステンシル運用
            //（FR-22）を設定したマテリアルには当たらない。
            Stencil
            {
                Ref       0
                ReadMask  6
                Comp      NotEqual
            }

            Cull [_Cull]
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Offset [_OffsetFactor], [_OffsetUnits]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   ToonVert
            #pragma fragment ToonFrag

            #pragma shader_feature_local          _ALPHATEST_ON
            #pragma shader_feature_local_fragment _HQ_SHADOW_ON
            #pragma shader_feature_local_fragment _SURFACETYPE_DEFAULT _SURFACETYPE_SKIN _SURFACETYPE_FACE _SURFACETYPE_HAIR _SURFACETYPE_CLOTH

            // **ライティングに効くキーワードは ForwardLit と揃える。**
            // ここがずれると、穴の縁で色が段になる ── 透過そのものではなく
            // 「同じ髪なのに明るさが違う」という形で出るので原因が読みにくい。
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fog

            // **二次的な寄与のキーワードは宣言しない。** SSAO・クッキー・
            // ライトレイヤー・デカール・APV・LOD クロスフェードを足すと
            // このパスだけでバリアントが ForwardLit と同じ数になる。
            // 描くのは眉と目の上の数百画素で、しかもアルファ 0.6 で載るだけなので、
            // 差が出るとしても段としては見えない。**主光源と追加光源の
            // 明るさだけが揃っていればよい。**

            #include "ToonPBRCommon.hlsl"

            #define TOON_HAIR_SEETHROUGH
            #include "Passes/ForwardPass.hlsl"
            ENDHLSL
        }

        // ====================================================================
        //  Pass 3 : Outline (既定では無効)
        // ====================================================================
        Pass
        {
            Name "Outline"
            // 独自タグ。SRPDefaultUnlit のままだと URP が不透明描画に混ぜ込み、
            // [本体][輪郭][本体][輪郭] と交互に描かれて ForwardLit の
            // SRP Batcher が分断される（アウトライン未使用のマテリアルまで巻き込む）。
            // 描画は ToonOutlineFeature がまとめて行う。
            Tags { "LightMode" = "IdolOutline" }

            Cull Front
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   OutlineVert
            #pragma fragment OutlineFrag

            #pragma shader_feature_local _OUTLINE_ON
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_fog

            #include "ToonPBRCommon.hlsl"

            #include "Passes/OutlinePass.hlsl"
            ENDHLSL
        }

        // ====================================================================
        //  Pass 4 : ShadowCaster
        // ====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            // LOD グループのクロスフェード。全パスで同じディザを掛けないと
            // 色と深度が食い違う。URP と同じくキーワードで切り替える。
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "ToonPBRCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            #include "Passes/ShadowPass.hlsl"
            ENDHLSL
        }

        // ====================================================================
        //  Pass 5 : DepthOnly   (リムライトが深度テクスチャを読むため必須)
        // ====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]
            Offset [_OffsetFactor], [_OffsetUnits]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma shader_feature_local _ALPHATEST_ON

            // LOD グループのクロスフェード。全パスで同じディザを掛けないと
            // 色と深度が食い違う。URP と同じくキーワードで切り替える。
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "ToonPBRCommon.hlsl"

            #include "Passes/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // ====================================================================
        //  Pass 6 : DepthNormals
        // ====================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]
            Offset [_OffsetFactor], [_OffsetUnits]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma shader_feature_local _ALPHATEST_ON

            // LOD グループのクロスフェード。全パスで同じディザを掛けないと
            // 色と深度が食い違う。URP と同じくキーワードで切り替える。
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            // レンダリングレイヤーの書き出し。デカールレイヤーや
            // ライトレイヤーのマスクがこのターゲットを読む。
            // pragma を持つファイルなので #include_with_pragmas で取り込む。
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #include "ToonPBRCommon.hlsl"

            #include "Passes/DepthNormalsPass.hlsl"
            ENDHLSL
        }

        // ====================================================================
        //  Pass 8 : MotionVectors
        //
        //  **TAA を使うならこのパスが要る。** URP の MotionVectorRenderPass は
        //  LightMode "MotionVectors" を持つシェーダーだけを描く。無いオブジェクトは
        //  カメラの動きぶんしか速度を持たないので、TAA は**アニメーションで動いた
        //  キャラを静止物と見なして前フレームの色を引きずる** ── 尾を引く。
        //
        //  URP の Lit.shader は最初からこのパスを持っている。ToonPBR には無く、
        //  「ちらつくので AA を入れてください」と勧めても TAA では別の破綻が出る
        //  状態だった（T-174 で AA が一切入っていないことが分かった流れ）。
        //
        //  **URP の ObjectMotionVectors.hlsl は使えない。** 中で読む
        //  SampleAlbedoAlpha / Alpha を持つ SurfaceInput.hlsl が
        //  `TEXTURE2D(_BaseMap)` を再宣言し、ToonPBRCommon の宣言と衝突する。
        //  DepthOnly と同じ書き方で自前に持つ。
        // ====================================================================
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }

            // 速度は RG の2成分。URP 本体と同じ。
            ColorMask RG
            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   MotionVert
            #pragma fragment MotionFrag
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            #include "ToonPBRCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MotionVectorsCommon.hlsl"

            #include "Passes/MotionVectorsPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
    CustomEditor "ToonNPR.EditorTools.ToonPBRShaderGUI"
}

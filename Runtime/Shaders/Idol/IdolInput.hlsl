// =============================================================================
//  IdolInput.hlsl
//  テクスチャ宣言と UnityPerMaterial CBUFFER（SRP Batcher 互換のため単一に集約）。
//  ベイクマップのプロパティ名は EasyShaderCore Baker と同名（Baker 自動アサインの再利用のため）。
// =============================================================================
#ifndef IDOL_INPUT_INCLUDED
#define IDOL_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// --- テクスチャ宣言（CBUFFER 外） --------------------------------------------
TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);
TEXTURE2D(_NormalMap);
TEXTURE2D(_ShadeNormalMap);
TEXTURE2D(_ShadeRampMap);
SAMPLER(sampler_IdolLinearClamp);   // Ramp サンプル用（横 0..1 の linear clamp）
TEXTURE2D(_OcclusionMap);        // R=影しきい値オフセット + AO 暗化（EasyShaderCore Baker と同名）
TEXTURE2D(_SpecularMask);         // R=スペキュラマスク
TEXTURE2D(_MatCapTex);
TEXTURE2D(_EmissionMap);
TEXTURE2D(_FaceSDFMap);          // 4ch SDF: R=右/G=左/B=上/A=下（EasyShaderCore Baker と同名）
TEXTURE2D(_HairFlowMap);         // 毛流れ 倍角(cos2θ,sin2θ,信頼度)（EasyShaderCore Baker と同名）
TEXTURE2D(_DissolveTex);         // Dissolve ノイズ（R）。Doll と同名で資産流用。
TEXTURE2D(_StockingMask);        // ストッキング適用範囲（R・白既定）

// 拡張余地: _CavityMap / _CurvatureMap / _SSSMap 等をここに追加（Baker 同名で流用）。

// --- 変数宣言（SRP Batcher 対応のため単一 CBUFFER に集約） -------------------
CBUFFER_START(UnityPerMaterial)
    // [Surface Options]
    float4 _MainTex_ST;
    half4  _BaseColor;
    half   _RenderMode;         // 0=Opaque, 1=Cutout
    half   _Cutoff;
    half   _Cull;

    // [Color Correction]
    half   _UseColorCorrection;
    half   _HueShift;
    half   _Saturation;
    half   _ValueMulti;

    // [Normal]
    half   _NormalScale;
    half   _ShadeNormalStrength;

    // [Shading Mode]
    half   _ShadingMode;        // 0=2段影, 1=Ramp

    // [2段影]
    half4  _ShadowColor;
    half   _ToonStep;
    half   _ToonFeather;
    half4  _Shadow2Color;       // A=Enable
    half   _Shadow2Step;
    half   _Shadow2Feather;
    half   _ShadowHueShift;
    half   _ShadowSaturation;

    // [落ち影]
    half4  _CastShadowColor;    // A=Enable
    half   _ReceiveShadowStrength;
    half   _HalfLambertWrap;

    // [影しきい値オフセット / AO]
    half   _OcclusionToShadow;
    half   _OcclusionStrength;

    // [セルスペキュラ]
    half4  _SpecularColor;          // HDR
    half   _ToonSpecularStep;
    half   _ToonSpecularFeather;
    half   _Smoothness;
    half   _SpecularIntensity;
    half   _SpecularShadeInfluence;
    half   _SpecularAA;

    // [間接光]
    half   _IndirectFlatten;
    half   _IndirectIntensity;
    half4  _IndirectTint;

    // [Light Conditioning]
    half   _LightColorInfluence;
    half   _LightSaturationLimit;
    half   _LightMinBrightness;

    // [追加ライト]
    half   _AdditionalLightBlendMode;      // 0=Add, 1=Max
    half   _AdditionalBlowoutLimit;

    // [MatCap]
    half   _UseMatCap;
    half   _MatCapBlend;          // 0=Add, 1=Multiply
    half4  _MatCapColor;         // HDR
    half   _MatCapIntensity;

    // [Emission]
    half   _UseEmission;
    half4  _EmissionColor;       // HDR
    half   _EmissionIntensity;

    // [Black Out]
    half   _BlackOut;

    // [部位 / Stencil]
    half   _CharaPart;           // 0=Body,1=Face,2=Brow,3=Hair,4=Eye
    half   _StencilRef;
    half   _StencilComp;
    half   _StencilPass;
    half   _StencilFail;
    half   _StencilZFail;
    half   _StencilReadMask;
    half   _StencilWriteMask;

    // [アウトライン]
    half4  _OutlineColor;
    half   _OutlineAlbedoBlend;
    half   _OutlineWidth;        // mm 単位
    half   _OutlineMaxScreenPx;
    half   _OutlineZOffset;

    // [Face SDF 顔影]
    half   _FaceSDFEnable;
    half   _FaceSDFFlip;
    half   _FaceSDFSoftness;
    half   _FaceSDFShadowMix;        // SDF 領域での落ち影の効かせ具合
    half   _FaceSDFBlendNormalMin;   // SDF 無効化のしきい値（法線 OS.y）
    half   _FaceSDFBlendNormalMax;   // SDF 有効化のしきい値

    // [リムライト]
    half4  _RimColor;                // HDR（深度リム・フレネルリム共通色）
    half   _RimDepthIntensity;       // 0 で深度リムのサンプルごとスキップ
    half   _RimWidthPx;              // 画面ピクセル一定幅
    half   _RimDepthThreshold;       // 線形深度差しきい値（キャラ厚を考慮）
    half   _RimLightAlign;           // 0=全周 ←→ 1=受光側のみ
    half   _RimIntensity;     // フレネルリムの個別強度（0 でスキップ）
    half   _RimThickness;            // フレネルリムの太さ
    half   _BackRimEnable;
    half4  _BackRimColor;            // HDR
    half   _BackRimPitch;
    half   _BackRimYaw;
    half   _BackRimPower;

    // [天使の輪]
    half4  _AngelRingColor;          // HDR
    half   _AngelRingIntensity;      // 0 でスキップ（既定 0）
    half   _AngelRingThreshold;
    half   _AngelRingSoftness;
    half   _AngelRingShift;          // バンド位置（法線方向オフセット）
    half   _AngelRingViewFollow;     // 0=ライト追従 ←→ 1=カメラ追従
    half   _HairFlowStrength;        // 毛流れマップの適用強度（Baker 同名）

    // [前髪透過]
    half   _HairSeeThroughAlpha;

    // [仮想ライト方向オーバーライド] IdolCharacter が書く。
    //  xyz=正規化ワールド方向, w=ブレンド(0..1)。既定 (0,0,1,0) で mainLight を素通し。
    half4  _VirtualLightDir;

    // [キャラ専用セルフシャドウ 受影側追加バイアス]
    //  Face/Eye のアクネ追い込み用。既定 0。
    half   _CharShadowFaceBias;

    // [Dissolve] EasyShaderCore Fx_Dissolve を流用。プロパティは Idol 側で完結。
    half   _DissolveAmount;          // 0..1 進行度（Doll と同名）
    half   _DissolveType;            // 0=None(UVノイズ), 1=WorldY, 2=LocalY
    half   _DissolveInvert;
    half   _DissolveStartY;
    half   _DissolveEndY;
    half   _DissolveNoiseScale;
    half   _DissolveNoiseStrength;
    half4  _DissolveEdgeColor;  // HDR 発光する最前線色（edgeColor）
    half4  _DissolveEdgeColor2;  // HDR 焦げ/縁の置換色（edgeColor2）
    half   _DissolveEdgeWidth;
    half   _DissolveEdgeStep;        // Toon 調の階調エッジ


    // [髪→顔スクリーンスペース落ち影] 
    half   _HairShadowIntensity;      // 0 で深度サンプルごとスキップ（既定 0）
    half   _HairShadowOffsetPx;       // ライト方向スクリーン投影向きのオフセット（px）
    half   _HairShadowDepthMin;       // 遮蔽判定窓の下限（m。手前すぎる自己面を除外）
    half   _HairShadowDepthMax;       // 遮蔽判定窓の上限（m。薄い近接遮蔽だけを拾う）

    // [ストッキング/シアー生地] 
    half   _StockingIntensity;        // 0 でスキップ（既定 0）
    half4  _StockingColor;            // 布色（肌馴染みのベージュ系既定）
    half   _StockingFrontOpacity;     // 正面の布不透明度（肌の透け具合）
    half   _StockingPower;            // グレージング項の指数（糸密度の視角応答）
    half4  _StockingSheenColor;       // すそ光沢（HDR・既定黒=OFF）
    half   _StockingSheenPower;       // すそ光沢の指数
CBUFFER_END

#endif // IDOL_INPUT_INCLUDED

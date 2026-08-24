#ifndef TOON_PBR_COMMON_INCLUDED
#define TOON_PBR_COMMON_INCLUDED

// ============================================================================
//  ToonPBRCommon.hlsl
//
//  設計方針:
//    BRDF は物理ベースのまま維持し、「拡散光の伝達関数」だけを様式化する。
//    - 鏡面反射   : GGX / Charlie sheen / Kajiya-Kay をそのまま使う
//    - 環境光     : リフレクションプローブ + SH (方向性だけ平坦化)
//    - 拡散反射   : NdotL を曲率駆動のソフトステップに通し、影側を HSV で転ばせる
//
//  こうすると背景の PBR ライティングと同じ空気を吸いながら、
//  キャラだけが絵として成立する。
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// LOD グループのクロスフェードのディザ。**Core.hlsl より後に置くこと。**
// LODCrossFade.hlsl は TEXTURE2D / SAMPLER マクロを使うのに、自分では
// GlobalSamplers.hlsl しか include しない。core のプラットフォーム API より
// 前に置くと「unrecognized identifier 'SAMPLER'」で落ちる。
//
// **以前はパス側の、ToonPBRCommon.hlsl より前に書いてあった。**
// そのため LOD_FADE_CROSSFADE のバリアントが**全パスでコンパイルできていなかった**
// （既定バリアントではキーワードが OFF なので気付けない。T-084）。
// ここに置けば全パスが同じ順序で解決される。
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// ToonRimLight が SampleSceneDepth を使うので、ここで宣言を取り込んでおく。
// 各パス側でインクルードすると順序を間違えたときに未宣言エラーになる。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

// EasyShaderCore の純粋関数レイヤ（設計ルール 3: Core と共有するのは純粋関数のみ）。
// 実装が同値の純関数だけを取り、Toon* 側は前方転送で残す（T-340）。
// VogelDisk（回転版）と HSV は Idol の改良実装を **Core 側へ逆輸入して**同値に
// なったので転送化できた。URP 結合層（Shadow_HQ 等）と、Core に受け皿の無い
// Charlie / Kajiya-Kay 系は自前のまま ── 判断は各 Toon* 関数のコメントにある。
// BRDF_GGX は URP の PI に依存するので、上の Core.hlsl より後に置くこと。
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/Common_Math.hlsl"
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/Common_Color.hlsl"
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/Common_Sampling.hlsl"
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/BRDF/BRDF_GGX.hlsl"
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/BRDF/BRDF_RimFuzz.hlsl"
// グリッタ（ラメ・スパンコール。T-348）。Prepare / Apply の 2 段 API。
// Hash21（Common_Math）と HueToRGB（Common_Color）に依存するので上の 2 つより後。
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/BRDF/BRDF_Glitter.hlsl"

#define TOON_PI 3.14159265359
#define TOON_SPECCUBE_LOD_STEPS 6

// ----------------------------------------------------------------------------
//  マテリアル定数
// ----------------------------------------------------------------------------
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _BaseColor;
    float  _Cutoff;
    float  _BumpScale;
    float  _DetailOn;
    float4 _DetailMap_ST;
    float4 _DetailColor;
    float  _DetailNormalScale;
    float  _NormalMapOn;

    // 素のアルベドの HSV 補正（影側の _Shadow* とは別物。両方掛かる）
    float  _AlbedoHueShift;
    float  _AlbedoSaturation;
    float  _AlbedoValue;

    // PBR
    float  _Metallic;
    float  _Smoothness;
    float  _OcclusionStrength;
    float  _CavityStrength;
    float  _DirectOcclusion;
    float  _MicroShadow;
    float  _SpecularTintStrength;
    float  _SpecularIntensity;
    float  _MetalSpecularBoost;
    float  _MetalEnvBoost;
    float  _SpecEnergyConservation;
    float4 _SpecularTint;

    // 拡散伝達関数
    float  _ShadowThreshold;
    float  _ShadowSoftness;
    float  _CurvatureSoftness;
    float  _ShadeNormalStrength;
    float  _DiffuseWrap;
    float  _NPRMapOn;
    float  _NPRShadowOffsetStrength;
    float  _ReceiveShadowStrength;
    float  _ShadowAttenSoftness;
    float  _ShadowEdgeAA;

    // 高品質セルフシャドウ（主光源専用）
    float  _HQShadowSoftness;
    float  _ReceiverNormalBias;
    float  _ShadowContactHardening;
    float  _ShadowPenumbraScale;

    // 影色 (HSV)
    float  _ShadowHueShift;
    float  _ShadowSaturation;
    float  _ShadowValue;
    float  _AddLightShadowColor;
    float4 _ShadowTint;
    float4 _ShadowColor;
    float  _ShadowColorMix;
    float4 _CastShadowColor;
    float  _CastShadowColorStrength;

    // ターミネータ
    float4 _TerminatorColor;
    float  _TerminatorStrength;
    float  _TerminatorSharpness;
    float  _TerminatorFadeStart;
    float  _TerminatorFadeEnd;

    // ランプ (任意)
    float  _UseRampMap;
    float  _RampRowCount;
    float  _RampIndexOverride;
    float  _RampStrength;

    // 顔 SDF
    float  _FaceSDFFlipU;
    float  _FaceUseObjectAxis;
    float  _FaceShadowOffset;
    float  _FaceFlatness;
    float  _FaceSDFBlendNormalMin;
    float  _FaceSDFBlendNormalMax;

    // スキン
    float4 _SubsurfaceColor;
    float  _SubsurfaceStrength;

    // 頬の赤み（Skin / Face 共通）
    float4 _TransmissionColor;
    float  _TransmissionPower;
    float  _TransmissionStrength;
    float  _TransmissionDistortion;
    float  _SSSMapStrength;

    // 布
    // シアー生地（ストッキング）。布を別メッシュで重ねずに手続きで乗せる
    float4 _StockingColor;
    float  _StockingIntensity;
    float  _StockingFrontOpacity;
    float  _StockingPower;
    float4 _SheenColor;
    float  _SheenRoughness;
    float  _SheenIntensity;
    float  _SheenEnergyConservation;
    float  _ClothAnisotropy;
    float  _ClothTangentSwap;

    // 髪
    float  _HairTangentSwap;
    float4 _HairSpecColor1;
    float  _HairShift1;
    float  _HairSmoothness1;
    float4 _HairSpecColor2;
    float  _HairShift2;
    float  _HairSmoothness2;
    float  _HairSpecIntensity;
    float  _HairStrandScale;
    float  _HairStrandSparkle;
    float  _HairAnisotropy;
    float  _HairAnisoGGXOn;
    float  _HairFlowStrength;

    // リム
    float  _RimMode;
    float  _RimFresnelThickness;
    float4 _RimColor;
    float  _RimIntensity;
    float  _RimWidth;
    float  _RimThreshold;
    float  _RimSoftness;
    float  _RimFresnelPower;
    float  _RimBacklightBias;
    float  _RimDirectionality;
    float  _RimReceiveShadow;
    float4 _FuzzColor;
    float  _FuzzIntensity;
    float  _FuzzPower;
    float  _RimDepthBlend;

    // 環境
    float  _AmbientIntensity;

    // ライト防御層（T-350。Doll と同名）
    float  _LightColorInfluence;
    float  _LightSaturationLimit;
    float  _LightMinBrightness;
    float  _DiffuseLightLimit;
    float  _AdditionalLightBlendMode;
    float  _AmbientFlatten;
    float  _AOMultiBounce;
    float  _BentNormalOn;
    float4 _ShadowAmbientTint;
    float  _ShadowAmbientIntensity;

    // ライト方向の手動上書き
    float  _LightOverrideOn;
    float  _LightOverrideYaw;
    float  _LightOverridePitch;
    float  _LightOverrideSpecular;
    float  _EnvSpecIntensity;
    float  _EnvSpecFlatten;

    // スペキュラ AA
    float  _EnergyCompensation;
    float4 _SecSpecularColor;
    float  _SecSpecularIntensity;
    float  _SecSmoothness;
    float  _ClearcoatStrength;
    float  _ClearcoatSmoothness;
    float  _IridescenceIntensity;
    float  _IridescenceThickness;
    float  _IridescenceShift;
    float  _SpecAAVariance;
    float  _SpecAAThreshold;

    // グリッタ（ラメ・スパンコール。T-348。Doll と同名）
    float4 _GlitterColor;
    float  _GlitterIntensity;
    float  _GlitterScale;
    float  _GlitterSize;
    float  _GlitterTilt;
    float  _GlitterSparsity;
    float  _GlitterIridescence;
    float  _GlitterIridescenceShift;
    float  _GlitterBaseReflection;

    // エミッシブ
    float  _EmissionOn;
    float4 _EmissionColor;

    // 描画モードのマーカー（T-358。ブレンド自体は ShaderLab 側で解決する）
    float  _SurfaceTransparent;

    // アウトライン
    float  _UseSmoothNormal;
    float  _UseVertexWidth;
    float  _ShadowCasterOff;
    float4 _OutlineColor;
    float  _OutlineAlbedoBlend;
    float  _OutlineAlbedoDarken;
    float  _OutlineWidth;
    float  _OutlineZOffset;
    float  _OutlineMaxDistance;

    // FaceDirectionBinder から供給。Properties にも [HideInInspector] で宣言済み
    //（CBUFFER にあって Properties に無いと SRP Batcher が丸ごと外れる。T-338）。
    // `lint:script-set` の印は Properties 宣言後は何も免除しない不活性コメントに
    // なっていたので外した ── 要らない印は次の人が真似る（下の _DebugMode と同じ教訓）。
    float4 _HeadForward;
    float4 _HeadRight;

    // スクリーンスペース輪郭の材質ID（DepthNormals の A に出る）
    // **`lint:script-set` を付けていたが誤り。** これは Properties に在る
    // 普通のマテリアル値で、インスペクタの Debug View がそのまま書く。
    // 印は警告を黙らせるので、要らない所に付けると次の人が真似る。
    float  _DebugMode;

    // コンタクトシャドウ


    // MatCap（加算のアクセント。乗算は持たない ── 環境の主経路を守るため）
    float4 _MatCapColor;
    float  _MatCapIntensity;
    float  _MatCapLightAlign;

    float  _SpecShadowFloor;    // 影の中に残す鏡面。既定 0.1 は従来の焼き込み値

    // 正面・上向きの陰の持ち上げ（顔の自己陰をマスク無しで消す）
    float4 _FillColor;
    float  _FillIntensity;
    float  _FillPitch;
    float  _FillYaw;
    float  _FillShadeOnly;

    // ディゾルブ（消失演出）。キーワードを持たず _DissolveAmount で切る
    float  _DissolveAmount;
    float  _DissolveInvert;
    float  _DissolveType;
    float  _DissolveStartY;
    float  _DissolveEndY;
    float  _DissolveNoiseScale;
    float  _DissolveNoiseStrength;
    float4 _DissolveEdgeColor;
    float4 _DissolveEdgeColor2;
    float  _DissolveEdgeWidth;
    float  _DissolveEdgeStep;
    float  _BlackOut;

    // 前髪透過（HairSeeThrough パスだけが読む）。
    // トグル側（_HairSeeThroughOn）はキーワードにしか使わないので、
    // 他の [Toggle] と同じく CBUFFER には入れない。
    float  _HairSeeThroughAlpha;
CBUFFER_END

// ----------------------------------------------------------------------------
//  テクスチャ
// ----------------------------------------------------------------------------
// **サンプラは共有すること。** ps_4_0 のサンプラレジスタは 16 本しかない。
// URP 側もシャドウマップ・深度・環境・デカール・SSAO・LOD ディザで数本使うので、
// テクスチャごとに 1 本ずつ宣言すると Hair + HQ 影のような組み合わせで
// **上限を超えてコンパイルが落ちる**（実機で落ちた）。
//
// **`sampler_XXX` 形式（テクスチャ紐付け）を共有に使ってはいけない。**
// このサンプラは同名テクスチャが「そのパスで生きていること」が条件で、
// 例えば DepthNormals パスで _BaseMap がストリップされると
// "Unrecognized sampler" で落ちる。共有には core のインライン名を使う
// （名前にフィルタとラップを含むものだけが認識される）。
//
// マテリアルのマップは同じメッシュの同じ UV に貼るので、フィルタとラップを
// 揃えて困る場面が無い。Unity の既定インポート設定に合わせて Linear/Repeat。
#define sampler_BumpMap        sampler_LinearRepeat
#define sampler_DetailMap       sampler_LinearRepeat
#define sampler_DetailNormalMap sampler_LinearRepeat
#define sampler_MaskMap        sampler_LinearRepeat
#define sampler_NPRMap         sampler_LinearRepeat
#define sampler_HairShiftMap   sampler_LinearRepeat
#define sampler_EmissionMap    sampler_LinearRepeat
#define sampler_BentNormalMap  sampler_LinearRepeat
#define sampler_CurvatureMap   sampler_LinearRepeat
#define sampler_HairFlowMap    sampler_LinearRepeat
#define sampler_ShadeNormalMap sampler_LinearRepeat
#define sampler_CavityMap      sampler_LinearRepeat
#define sampler_SSSMap         sampler_LinearRepeat
#define sampler_StockingMask   sampler_LinearRepeat
#define sampler_GlitterMask    sampler_LinearRepeat
#define sampler_DissolveTex    sampler_LinearRepeat
#define sampler_MatCapTex      sampler_LinearClamp

// クランプが要るものだけ分ける。
//   RampMap    : U が「光の当たり具合」。リピートすると両端が巻き込む
//   FaceSDFMap : ミラーで 1-u を引くので端の挙動が効く
#define sampler_RampMap    sampler_LinearClamp
#define sampler_FaceSDFMap sampler_LinearClamp

// _BaseMap だけは自前で持つ。ドット絵を Point で入れるといった
// インポート設定を殺したくないのはここだけ。
SAMPLER(sampler_BaseMap);

TEXTURE2D(_BaseMap);
TEXTURE2D(_BumpMap);
TEXTURE2D(_DetailMap);
TEXTURE2D(_DetailNormalMap);

// MaskMap : R=Metallic  G=Occlusion  B=Thickness  A=Smoothness
TEXTURE2D(_MaskMap);

// NPRMap  : R=SpecMask   G=ShadowOffset  B=RimMask  A=RampIndex
TEXTURE2D(_NPRMap);

TEXTURE2D(_RampMap);
TEXTURE2D(_FaceSDFMap);

// 顔 SDF の 16bit デコード（R が上位・G が下位 = R×256+G）。
// dot の係数 65280 = 255×256。bilinear 補間後の RG でも線形なので、
// 補間してからデコードしても値が破綻しない（非圧縮テクスチャが前提）。
float ToonDecodeFaceSdf16(float2 rg)
{
    return dot(rg, float2(65280.0, 255.0)) / 65535.0;
}
TEXTURE2D(_HairShiftMap);
TEXTURE2D(_EmissionMap);
TEXTURE2D(_BentNormalMap);
TEXTURE2D(_CurvatureMap);
TEXTURE2D(_HairFlowMap);
TEXTURE2D(_ShadeNormalMap);
TEXTURE2D(_CavityMap);
TEXTURE2D(_SSSMap);
TEXTURE2D(_StockingMask);
TEXTURE2D(_GlitterMask);
TEXTURE2D(_DissolveTex);
TEXTURE2D(_MatCapTex);


#include "Shading/ToonPBRTypes.hlsl"
#include "Shading/ToonPBRColor.hlsl"
#include "Shading/ToonPBRDissolve.hlsl"
#include "Shading/ToonPBRDiffuse.hlsl"
#include "Shading/ToonPBRSpecular.hlsl"
#include "Shading/ToonPBREnv.hlsl"
#include "Shading/ToonPBRShadows.hlsl"
#include "Shading/ToonPBRLighting.hlsl"
#include "Shading/ToonPBRRim.hlsl"

#endif // TOON_PBR_COMMON_INCLUDED

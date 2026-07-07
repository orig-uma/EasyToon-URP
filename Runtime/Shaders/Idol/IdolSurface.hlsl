// =============================================================================
//  IdolSurface.hlsl
// -----------------------------------------------------------------------------
//  GatherSurface / ApplyPostEffects
//  型定義: IdolSurfaceTypes.hlsl（IdolLighting 等が include）
//  実装:   ForwardPass で Varyings 定義後に #define IDOL_SURFACE_IMPL して include。
// =============================================================================

#if defined(IDOL_SURFACE_IMPL) && !defined(IDOL_SURFACE_IMPL_INCLUDED)
#define IDOL_SURFACE_IMPL_INCLUDED

#include "IdolFabric.hlsl"   // ストッキング/シアー生地

// ベースアルベド × 陰色（Hue Shift / Sat Boost）をライト非依存に 1 回だけ算出。
// 「ただ暗い影」を彩度・色相の残る影にする。既定（Shift 0 / Boost 1）で素通し。
half3 GetShadedBase(half3 albedo)
{
    half3 shadowBase = albedo;
    UNITY_BRANCH
    if (abs(_ShadowHueShift) > 0.0001 || abs(_ShadowSaturation - 1.0) > 0.0001)
    {
        shadowBase = ApplyColorCorrection(shadowBase, _ShadowHueShift, _ShadowSaturation, 1.0);
    }
    return shadowBase;
}

IdolSurfaceData GatherSurface(Varyings input, half3 viewDirectionWS, out half alpha)
{
    IdolSurfaceData s = (IdolSurfaceData)0;

    float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;

    half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _BaseColor;

    UNITY_BRANCH
    if (_UseColorCorrection > 0.5)
    {
        albedo.rgb = ApplyColorCorrection(albedo.rgb, _HueShift, _Saturation, _ValueMulti);
    }

    alpha = albedo.a;

    #if defined(_ALPHATEST_ON)
        clip(alpha - _Cutoff);
    #endif

    // --- 法線（ディテール） ---
    half4 normalSample = SAMPLE_TEXTURE2D(_NormalMap, sampler_MainTex, uv);
    half3 normalTS = UnpackNormalScale(normalSample, _NormalScale);
    half3 detailNormalWS = normalize(normalTS.x * input.tangentWS
                                   + normalTS.y * input.bitangentWS
                                   + normalTS.z * input.normalWS);

    s.cleanNormalWS  = normalize(input.normalWS);
    s.detailNormalWS = detailNormalWS;

    // --- シェード法線: 拡散陰ランプだけを平滑化法線で駆動（未ベイクで無効） ---
    //  スペキュラ・リムはディテール法線のまま（陰の輪郭のみ綺麗な曲線にする）。
    s.shadeNormalWS = detailNormalWS;
    UNITY_BRANCH
    if (_ShadeNormalStrength > 0.0)
    {
        half4 shadeSample = SAMPLE_TEXTURE2D(_ShadeNormalMap, sampler_MainTex, uv);
        half3 shadeTS = shadeSample.xyz * 2.0 - 1.0;
        half3 shadeWS = normalize(shadeTS.x * input.tangentWS
                                + shadeTS.y * input.bitangentWS
                                + shadeTS.z * input.normalWS);
        s.shadeNormalWS = normalize(lerp(detailNormalWS, shadeWS, _ShadeNormalStrength));
    }

    // --- Occlusion: (1) しきい値オフセット (2) 従来の AO 暗化（別軸） ---
    half ao = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_MainTex, uv).r;
    // 0.5 をニュートラルに ±で HalfLambert をオフセット（0=常影 / 1=常明側）。
    s.halfLambertOffset = (ao - 0.5) * 2.0 * _OcclusionToShadow;
    albedo.rgb *= lerp(1.0, ao, _OcclusionStrength);

    s.specMask = SAMPLE_TEXTURE2D(_SpecularMask, sampler_MainTex, uv).r;

    // Geometric Specular AA のカーネル量（frag 側で 1 回算出）。
    s.specAAVariance = (_SpecularAA > 0.0)
        ? ComputeSpecularAAVariance(detailNormalWS, _SpecularAA, 0.25)
        : 0.0;

    s.NdotV = saturate(dot(detailNormalWS, viewDirectionWS));

    // --- ストッキング/シアー生地: アルベド確定直後・陰色算出より前 ---
    //  ここで albedo に布レイヤを合成することで、陰色（1影/2影/落ち影）にも
    //  布色が自動で乗る。すそ光沢は ApplyPostEffects で加算。
    UNITY_BRANCH
    if (_StockingIntensity > 0.0)
    {
        ApplyStocking(uv, s.NdotV, albedo.rgb, s.stockingSheen);
    }

    s.albedo = albedo.rgb;

    // 陰色をライト非依存に確定。
    half3 shadedBase   = GetShadedBase(albedo.rgb);
    s.shadow1Albedo    = shadedBase * _ShadowColor.rgb;
    s.shadow2Albedo    = shadedBase * _Shadow2Color.rgb;
    s.castShadowAlbedo = shadedBase * _CastShadowColor.rgb;

    // --- フレネルリム項（ライト非依存に 1 回算出。Intensity 0 で 0） ---
    half fuzzDummy;
    GetFresnelTerms(s.NdotV, _RimIntensity, _RimThickness, 0.0, 1.0,
                    s.rimFresnel, fuzzDummy);

    // --- 天使の輪の接線前計算（未使用時はスキップ。毛流れは Baker 同名マップ） ---
    UNITY_BRANCH
    if (_AngelRingIntensity > 0.0)
    {
        float hairFlowC2 = 1.0, hairFlowS2 = 0.0, hairFlowConf = 0.0;
        UNITY_BRANCH
        if (_HairFlowStrength > 0.0)
        {
            half3 hf = SAMPLE_TEXTURE2D(_HairFlowMap, sampler_MainTex, uv).rgb;
            hairFlowC2   = hf.r * 2.0 - 1.0;
            hairFlowS2   = hf.g * 2.0 - 1.0;
            hairFlowConf = hf.b;
        }
        // 毛束ノイズは天使の輪では使わない（strandStrength 0）。
        s.anisoPrecomp = PrecomputeAnisoTangent(
            input.tangentWS, input.bitangentWS, detailNormalWS, uv,
            0.0, 0.0, 1.0, 0.0,
            _AngelRingShift, 0.0,
            hairFlowC2, hairFlowS2, hairFlowConf, _HairFlowStrength);
    }

    // --- 間接光（SH）: Flatten で方向成分を潰し、均一アンビエントで包む ---
    half3 indirect = SampleSH(s.detailNormalWS);
    UNITY_BRANCH
    if (_IndirectFlatten > 0.0)
    {
        indirect = lerp(indirect, SampleSH(half3(0.0, 0.0, 0.0)), _IndirectFlatten);
    }
    s.indirectLight = indirect * (_IndirectTint.rgb * _IndirectIntensity);

    return s;
}

// -----------------------------------------------------------------------------
//  Face SDF 顔影: ベイク 4ch SDF（R=右/G=左/B=上/A=下）をライトの顔ローカル
//  方向で重み付けブレンドし、ライト正面度としきい値比較して滑らかな顔影を作る。
//  戻り値 sdfLit: 0(影)..1(光)。無効時は -1（呼び出し側で通常陰へフォールバック）。
//  sdfMask: 法線 OS.y による SDF 適用マスク（顎下等の SDF 不適合領域を除外）。
//  顔ローカル軸はオブジェクト空間の +Z を正面とみなす（Doll と同じ機構）。
// -----------------------------------------------------------------------------
float ComputeFaceSDF(Varyings input, Light mainLight, out half sdfMask)
{
    float sdfLit = -1.0;
    sdfMask = 1.0;

    UNITY_BRANCH
    if (_FaceSDFEnable > 0.5)
    {
        float3 normalOS = TransformWorldToObjectDir(input.normalWS);

        // 上向き法線ほど SDF を有効化（下向き＝顎下等はフェードアウト）。
        sdfMask = smoothstep(_FaceSDFBlendNormalMin, _FaceSDFBlendNormalMax, normalOS.y);

        float3 faceUp    = normalize(TransformObjectToWorldDir(float3(0, 1, 0)));
        float3 faceFwd   = normalize(TransformObjectToWorldDir(float3(0, 0, 1)))
                         * (_FaceSDFFlip > 0.5 ? -1.0 : 1.0);
        float3 faceRight = normalize(cross(faceUp, faceFwd));

        float dirX = dot(mainLight.direction, faceRight);
        float dirY = dot(mainLight.direction, faceUp);
        float frontness = dot(mainLight.direction, faceFwd);

        float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
        half4 sdfRGBA = SAMPLE_TEXTURE2D(_FaceSDFMap, sampler_MainTex, uv);

        // ライトの水平/垂直成分で 4 方向 SDF を重み付けブレンド。
        float weightRight = max(0.0, dirX);
        float weightLeft  = max(0.0, -dirX);
        float weightUp    = max(0.0, dirY);
        float weightDown  = max(0.0, -dirY);
        float weightSum = weightRight + weightLeft + weightUp + weightDown + 0.0001;

        float sdf = (sdfRGBA.r * weightRight +
                     sdfRGBA.g * weightLeft +
                     sdfRGBA.b * weightUp +
                     sdfRGBA.a * weightDown) / weightSum;

        // ライト正面度(0..1)を SDF しきい値と比較。fwidth で最低 1px の AA。
        float baseSoft = max(_FaceSDFSoftness, fwidth(sdf));
        float soft = max(baseSoft, _HalfLambertWrap * 0.5);
        float f = frontness * 0.5 + 0.5;
        sdfLit = smoothstep(sdf - soft, sdf + soft, f);
    }

    return sdfLit;
}

// MatCap / Emission / Black Out（ライト非依存の後段合成）。
void ApplyPostEffects(inout half3 finalColor, Varyings input, IdolSurfaceData s, Light mainLight)
{
    float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;

    UNITY_BRANCH
    if (_UseMatCap > 0.5)
    {
        float2 matcapUV = GetMatCapUV(s.detailNormalWS);
        half3 matcapColor = SAMPLE_TEXTURE2D(_MatCapTex, sampler_MainTex, matcapUV).rgb * _MatCapColor.rgb;
        finalColor = ApplyMatCap(finalColor, matcapColor, _MatCapIntensity, _MatCapBlend);
    }

    UNITY_BRANCH
    if (_UseEmission > 0.5)
    {
        half3 emissionMapColor = SAMPLE_TEXTURE2D(_EmissionMap, sampler_MainTex, uv).rgb;
        finalColor += CalculateEmission(emissionMapColor, _EmissionColor.rgb, _EmissionIntensity);
    }

    // ストッキングのすそ光沢（ライト非依存の加算。既定黒=0）。
    finalColor += s.stockingSheen;

    finalColor = lerp(finalColor, half3(0.0, 0.0, 0.0), _BlackOut);
}

#endif // IDOL_SURFACE_IMPL_INCLUDED

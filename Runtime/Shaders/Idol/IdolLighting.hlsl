// =============================================================================
//  IdolLighting.hlsl  (policy)
// -----------------------------------------------------------------------------
//  Idol（Toon 本命）のライティングポリシー層。汎用計算は EasyShaderCore Common へ
//  委譲し、ここでは Toon 固有ポリシーのみを担う:
//    (1) 陰の量子化（2段影 / Ramp テクスチャ）と影しきい値オフセット
//    (2) 落ち影と角度陰の分離合成（Cast Shadow Color 塗り分け）
//    (3) セルスペキュラ
//    (4) CalculateSingleLight（IdolSurfaceData 経由）
//
//  前提: URP Core.hlsl / Lighting.hlsl を本ファイルより前に include すること。
// =============================================================================
#ifndef IDOL_LIGHTING_INCLUDED
#define IDOL_LIGHTING_INCLUDED

// EasyShaderCore Common（純粋関数のみ。Doll 配下は include 禁止）。
#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/Common.hlsl"
#include "IdolSurfaceTypes.hlsl"
#include "IdolHair.hlsl"

// -----------------------------------------------------------------------------
//  陰ランプ: 2段影（数値制御）か Ramp テクスチャかを uniform 分岐で解決。
//   halfLambert（オフセット適用済み 0..1）を 0..1 の「明るさ」係数へ写像する。
//   1段目のしきい値・ぼかしは _ToonStep / _ToonFeather。
// -----------------------------------------------------------------------------
half3 ResolveDiffuseShade(half3 albedo, half3 shadow1Albedo, half3 shadow2Albedo,
                          float halfLambert, out float litMask)
{
    UNITY_BRANCH
    if (_ShadingMode > 0.5)
    {
        // Ramp モード: 横 = HalfLambert(0..1) を linear_clamp でサンプルし乗算。
        //   数値の 1影/2影は使わず、色設計を Ramp テクスチャに委ねる。
        half3 ramp = SAMPLE_TEXTURE2D(_ShadeRampMap, sampler_IdolLinearClamp, float2(halfLambert, 0.5)).rgb;
        litMask = halfLambert; // スペキュラ減衰等の「明るさ」代表値
        return albedo * ramp;
    }

    // 2段影モード: ToonRamp（smoothstep + fwidth AA）で 1影・2影を段階量子化。
    float shade1 = ToonRamp(halfLambert, _ToonStep, _ToonFeather);
    litMask = shade1;
    half3 diffuse = lerp(shadow1Albedo, albedo, shade1);

    UNITY_BRANCH
    if (_Shadow2Color.a > 0.0)
    {
        float shade2 = ToonRamp(halfLambert, _Shadow2Step, _Shadow2Feather);
        diffuse = lerp(shadow2Albedo, diffuse, shade2);
    }
    return diffuse;
}

// -----------------------------------------------------------------------------
//  セルスペキュラ: Blinn-Phong lobe をしきい値でセル化。
//   陰側（shade）で減衰し、Specular AA を適用。
// -----------------------------------------------------------------------------
half3 CalculateCelSpecular(IdolSurfaceData s, float3 lightDirWS, half3 viewDirectionWS,
                           float3 lightEnergy, float castShadow, float shade)
{
    UNITY_BRANCH
    if (_SpecularIntensity <= 0.0)
    {
        return half3(0.0, 0.0, 0.0);
    }

    float3 halfVector = SafeNormalize(lightDirWS + viewDirectionWS);
    float  NdotH = saturate(dot(s.detailNormalWS, halfVector));
    float  smoothness = ApplySpecularAA(_Smoothness, s.specAAVariance);

    // Blinn-Phong ローブ（tint=白, intensity=1）を輝度 lobe として取り出す。
    half3 lobeRGB = BlinnPhongLobe(NdotH, smoothness, half3(1.0, 1.0, 1.0), 1.0);
    float lobe = lobeRGB.r; // tint 白なので単一チャンネルで可

    // セル化: しきい値の縁を smoothstep（fwidth で最低 1px AA）。
    float softness = max(_ToonSpecularFeather, fwidth(lobe));
    float cel = smoothstep(_ToonSpecularStep - softness, _ToonSpecularStep + softness, lobe);

    // ndotl の立ち上がりで裏面を除外し、マスク・落ち影を掛ける。
    float ndotl = dot(s.detailNormalWS, lightDirWS);
    float visMask = saturate(ndotl * 10.0) * s.specMask * castShadow;

    // 陰側減衰: 陰ランプ（shade）に入った面のハイライトを沈める。
    float shadeDim = lerp(1.0, shade, _SpecularShadeInfluence);

    return _SpecularColor.rgb * (cel * visMask * _SpecularIntensity * shadeDim) * lightEnergy;
}

// -----------------------------------------------------------------------------
//  ライト 1 灯ぶんの寄与（IdolSurfaceData 経由）。
//   isMainLight: true で間接光を加算（1回のみ）。false で白飛び防止クランプ適用。
//   sdfLit / sdfMask: Face SDF 顔影（ComputeFaceSDF の結果）。sdfLit < 0 で無効。
//     追加ライトは SDF 非対応のため sdfLit = -1.0 を渡すこと。
// -----------------------------------------------------------------------------
half3 CalculateSingleLight(Light light, IdolSurfaceData s, half3 viewDirectionWS,
                           half castShadow, bool isMainLight, float sdfLit, half sdfMask)
{
    // 直接光エネルギー。白飛び防止クランプは追加ライトのみ（メインライトは素通し）。
    // 間接光の加算はメインライト呼び出しの 1 回だけ（多重加算防止）。
    float3 directEnergy = light.color * light.distanceAttenuation;
    float3 diffuseEnergy = isMainLight
        ? directEnergy + s.indirectLight
        : ApplyLuminanceClamp(directEnergy, _AdditionalBlowoutLimit);

    // 拡散陰はシェード法線で駆動。HalfLambert に Occlusion オフセットを加える。
    float diffuseNdotL = dot(s.shadeNormalWS, light.direction);
    float halfLambert = saturate(HalfLambert(diffuseNdotL, _HalfLambertWrap) + s.halfLambertOffset);

    // Face SDF 顔影: SDF 有効領域では halfLambert を SDF 由来の連続値に置き換え、
    // 法線由来の陰バンドを顔に持ち込まない。2段影/Ramp 両モードで同じランプを通る。
    // 落ち影は _FaceSDFShadowMix で SDF 領域内の効き具合を制御する。
    UNITY_BRANCH
    if (sdfLit >= 0.0)
    {
        halfLambert = lerp(halfLambert, sdfLit, sdfMask);
        castShadow  = lerp(castShadow, lerp(1.0, castShadow, _FaceSDFShadowMix), sdfMask);
    }

    // 落ち影の合成:
    //  - Cast Shadow Color 有効時: 角度陰と落ち影を分離し、落ち影を専用色で塗る。
    //  - 無効時: halfLambert に落ち影を折り込み、同じランプを 1 回だけ通す
    //    （2段影 / Ramp どちらのモードでも落ち影がランプの色設計で暗くなる）。
    float rampInput = (_CastShadowColor.a > 0.0) ? halfLambert : min(halfLambert, castShadow);

    // 角度陰（1影/2影 or Ramp）をライト非依存の色で解決。
    float litMask;
    half3 angleDiffuse = ResolveDiffuseShade(
        s.albedo, s.shadow1Albedo, s.shadow2Albedo, rampInput, litMask);

    half3 diffuseColor = (_CastShadowColor.a > 0.0)
        ? lerp(s.castShadowAlbedo, angleDiffuse, castShadow)
        : angleDiffuse;

    half3 finalDiffuse = diffuseColor * diffuseEnergy;

    // セルスペキュラ（陰側減衰は角度陰 litMask を代表値に）。
    float shadeForSpec = min(litMask, castShadow);
    half3 finalSpecular = CalculateCelSpecular(
        s, light.direction, viewDirectionWS, directEnergy, castShadow, shadeForSpec);

    // 天使の輪（Angel Ring）ヘアハイライト（Intensity 0 でスキップ・既定 0）。
    half3 finalAngelRing = CalculateAngelRing(
        s, light.direction, viewDirectionWS, directEnergy, castShadow, shadeForSpec);

    return finalDiffuse + finalSpecular + finalAngelRing;
}

#endif // IDOL_LIGHTING_INCLUDED

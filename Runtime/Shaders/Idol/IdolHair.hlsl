// =============================================================================
//  IdolHair.hlsl  (policy)
// -----------------------------------------------------------------------------
//  天使の輪（Angel Ring）ヘアハイライト。
//  Common の異方性バンド（PrecomputeAnisoTangent / CalculateAnisotropicSpecular）
//  を流用し、結果をしきい値でセル化してトゥーンの様式的な輪にする。
//  _AngelRingViewFollow でライト追従 ←→ カメラ追従を配合（様式的な輪はカメラ追従寄り）。
//
//  前提: URP Core.hlsl、EasyShaderCore Common（BRDF_Anisotropic.hlsl）、
//        IdolInput.hlsl、IdolSurfaceTypes.hlsl。
// =============================================================================
#ifndef IDOL_HAIR_INCLUDED
#define IDOL_HAIR_INCLUDED

// 天使の輪 1 灯ぶんの寄与。shade は陰ランプ代表値（_SpecularShadeInfluence を共用）。
half3 CalculateAngelRing(IdolSurfaceData s, float3 lightDirWS, half3 viewDirectionWS,
                         float3 lightEnergy, float castShadow, float shade)
{
    UNITY_BRANCH
    if (_AngelRingIntensity <= 0.0)
    {
        return half3(0.0, 0.0, 0.0);
    }

    // ビュー追従: 擬似ライト方向をカメラ方向へ寄せる（1 でハーフベクトル≒視線）。
    //   カメラ追従にすると頭を回しても輪が「見た目の定位置」に留まる。
    float3 ringDirWS = normalize(lerp(lightDirWS, (float3)viewDirectionWS, _AngelRingViewFollow));

    // 異方性バンドを白色・エネルギー1・影1で「素のバンド値」として取り出す。
    //   バンドの太さはセル化しきい値で制御するため、ローブ厚は中庸の固定値。
    const float kRingThickness = 0.4;
    half3 bandRGB = CalculateAnisotropicSpecular(
        s.anisoPrecomp,
        s.detailNormalWS, ringDirWS, viewDirectionWS,
        half4(1.0, 1.0, 1.0, 1.0), kRingThickness,
        half4(0.0, 0.0, 0.0, 0.0), 0.0,
        float3(1.0, 1.0, 1.0), 1.0);
    float band = bandRGB.r; // 白バンドなので単一チャンネルで可

    // セル化: しきい値の縁を smoothstep（fwidth で最低 1px AA）。
    float softness = max(_AngelRingSoftness, fwidth(band));
    float cel = smoothstep(_AngelRingThreshold - softness, _AngelRingThreshold + softness, band);

    // 陰側減衰（セルスペキュラと共用の _SpecularShadeInfluence）+ 落ち影。
    float shadeDim = lerp(1.0, shade, _SpecularShadeInfluence);

    return _AngelRingColor.rgb * (cel * _AngelRingIntensity * shadeDim * castShadow) * lightEnergy;
}

#endif // IDOL_HAIR_INCLUDED

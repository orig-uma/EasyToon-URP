// =============================================================================
//  IdolFabric.hlsl  (policy)
// -----------------------------------------------------------------------------
//  ストッキング/シアー生地（R9）: 肌ベース（BaseMap）の上に、
//  視角依存の不透明度を持つ布レイヤを手続き的に重ねる。
//   - 正面（NdotV 大）: 肌が布越しに透ける（_StockingFrontOpacity）
//   - シルエット近く（NdotV 小）: 布が密に見える＝糸密度の視角変化の近似
//   - すそ光沢（シアーシーン）: グレージング角の加算光沢（ライト非依存・HDR）
//  適用範囲は _StockingMask（R・白既定）。既定 OFF（_StockingIntensity 0）。
//
//  呼び出し: GatherSurface のアルベド確定直後（色補正後・GetShadedBase より前）。
//  → 陰色（1影/2影/落ち影）にも布色が自動で乗る。
//
//  前提: IdolInput.hlsl（_Stocking* / _StockingMask / sampler_MainTex）。
// =============================================================================
#ifndef IDOL_FABRIC_INCLUDED
#define IDOL_FABRIC_INCLUDED

// albedo を布レイヤ合成で上書きし、すそ光沢（加算用）を sheen に返す。
void ApplyStocking(float2 uv, half NdotV, inout half3 albedo, out half3 sheen)
{
    half mask = SAMPLE_TEXTURE2D(_StockingMask, sampler_MainTex, uv).r;

    // グレージング項: シルエットに近いほど 1（糸が視線方向に重なって密に見える）。
    half graze = pow(saturate(1.0 - NdotV), _StockingPower);

    // 布の不透明度: 正面は FrontOpacity（肌が透ける）→ 縁は 1（布が密）。
    half opacity = saturate(lerp(_StockingFrontOpacity, 1.0, graze)) * mask * _StockingIntensity;

    // 布の色: 正面=肌に布色が乗る（乗算）/ 縁=布そのものの色。
    half3 fabric = lerp(albedo * _StockingColor.rgb, _StockingColor.rgb, graze);
    albedo = lerp(albedo, fabric, opacity);

    // すそ光沢（シアーシーン）: グレージング角の加算光沢。既定黒=OFF。
    sheen = _StockingSheenColor.rgb * (pow(graze, _StockingSheenPower) * mask * _StockingIntensity);
}

#endif // IDOL_FABRIC_INCLUDED

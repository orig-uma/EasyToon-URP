// =============================================================================
//  IdolShadows.hlsl  (policy / thin wrapper)
// -----------------------------------------------------------------------------
//  落ち影（シャドウマップ）の取得を 1 箇所に集約するラッパー。
//  URP 標準のメインライト影に加え、_IDOL_CHARSHADOW 定義時はキャラ専用
//  セルフシャドウマップ（IdolCharShadowFeature が供給）を
//  3x3 PCF でサンプルし、URP 影と min() 合成する（髪→顔の落ち影がこれで出る）。
//
//  前提: URP Core.hlsl / Lighting.hlsl を本ファイルより前に include すること。
// =============================================================================
#ifndef IDOL_SHADOWS_INCLUDED
#define IDOL_SHADOWS_INCLUDED

#if defined(_IDOL_CHARSHADOW)
// Feature が供給するグローバル（per-material CBUFFER 外＝SRP Batcher 維持）。
TEXTURE2D_SHADOW(_IdolCharShadowMap);
SAMPLER_CMP(sampler_IdolCharShadowMap);
float4x4 _IdolCharShadowMatrix;   // ライト VP（キャスターと共有）
float4   _IdolCharShadowParams;   // x=1/解像度, y=強度, z=有効フラグ, w=未使用

// 受影側でキャラ専用シャドウマップを 3x3 PCF サンプル。
// 戻り値: 0(影)..1(光)。範囲外は 1（影なし）。faceBias は受影側追加深度バイアス。
half SampleIdolCharShadow(float3 positionWS, half faceBias)
{
    // 有効フラグ 0 のフレーム（メインライト不在等）は素通し。
    if (_IdolCharShadowParams.z < 0.5)
        return 1.0;

    // キャスターと同一の VP（GL.GetGPUProjectionMatrix 適用済み）で射影する。
    // GPU 射影が Y 反転・reversed-Z をターゲット準拠に済ませているため、ここでは
    // 追加の Y 反転を行わない（NDC.xy→UV, NDC.z→深度をそのまま使う）。
    float4 shadowCS = mul(_IdolCharShadowMatrix, float4(positionWS, 1.0));
    float3 shadowNDC = shadowCS.xyz / shadowCS.w;
    float2 shadowUV = shadowNDC.xy * 0.5 + 0.5;

    // アトラス範囲外は影なし。
    if (any(shadowUV < 0.0) || any(shadowUV > 1.0))
        return 1.0;

    float depthRef = shadowNDC.z;
    #if UNITY_REVERSED_Z
        depthRef += faceBias; // reversed-Z: 大きい方が手前。受影を手前側へ寄せて自己影を抜く
    #else
        depthRef -= faceBias;
    #endif

    // 3x3 PCF（専用マップは高解像度なので小カーネルで十分）。
    float texel = _IdolCharShadowParams.x;
    half sum = 0.0;
    [unroll] for (int y = -1; y <= 1; ++y)
    {
        [unroll] for (int x = -1; x <= 1; ++x)
        {
            float2 offset = float2(x, y) * texel;
            sum += SAMPLE_TEXTURE2D_SHADOW(_IdolCharShadowMap, sampler_IdolCharShadowMap,
                                           float3(shadowUV + offset, depthRef));
        }
    }
    half atten = sum / 9.0;

    // 強度: 1 で完全な専用影、0 で無効（1 に寄せる）。
    return lerp(1.0, atten, _IdolCharShadowParams.y);
}
#endif

// メインライトの落ち影減衰（0=影, 1=光）を返す。
//   URP 標準影 と キャラ専用影（有効時）を min() 合成する。
//   faceBias: 受影側の追加深度バイアス（Face/Eye のアクネ追い込み用、既定 0）。
half GetIdolMainShadow(Light mainLight, float3 positionWS, half3 normalWS, half NdotL, half faceBias)
{
    half shadow = mainLight.shadowAttenuation;

    #if defined(_IDOL_CHARSHADOW)
        half charShadow = SampleIdolCharShadow(positionWS, faceBias);
        shadow = min(shadow, charShadow);
    #endif

    return shadow;
}

#endif // IDOL_SHADOWS_INCLUDED

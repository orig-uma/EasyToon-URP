// =============================================================================
//  IdolDissolve.hlsl  (policy / thin wrapper)
// -----------------------------------------------------------------------------
//  Dissolve（消失）のテクスチャサンプリングとプロパティ解決のみを担い、計算と
//  クリップは EasyShaderCore Common の ResolveDissolve（Fx_Dissolve.hlsl）へ委譲する。
//  Doll の DollEffects.hlsl の配線方式に合わせているが、Doll 配下は include せず
//  Common の純粋関数だけを使う（キーワード分岐は _DISSOLVE_ON のみで、軸は
//  uniform 動的分岐 _DissolveType にしてバリアント数を増やさない）。
//
//  全パスから呼べるよう、Common umbrella ではなく Fx_Dissolve 単体を include する。
//  前提: IdolInput.hlsl（_Dissolve* 宣言）が本ファイルより前に見えていること。
// =============================================================================
#ifndef IDOL_DISSOLVE_INCLUDED
#define IDOL_DISSOLVE_INCLUDED

#include "Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/Effects/Fx_Dissolve.hlsl"

// ノイズは URP グローバルの linear-repeat サンプラで引く（専用サンプラ不要）。
// clip は各パスで、エッジ発光（dissolveEmission）は ForwardLit のみで加算する。
void ApplyIdolDissolve(float2 uv, float3 positionWS, float3 positionOS, float3 normalWS,
                        inout half3 albedo, out half3 dissolveEmission)
{
    dissolveEmission = half3(0, 0, 0);

#if defined(_DISSOLVE_ON)
    float dissolveNoise = 0.5;
    float dissolveGrad  = 0.5;
    bool  isNoneType    = (_DissolveType < 0.5); // 0=None: ノイズのみで切る

    UNITY_BRANCH
    if (_DissolveType > 1.5) // 2=LocalY
    {
        dissolveNoise = SAMPLE_TEXTURE2D(_DissolveTex, sampler_LinearRepeat, uv * _DissolveNoiseScale).r;
        dissolveGrad  = saturate((positionOS.y - _DissolveStartY) / (_DissolveEndY - _DissolveStartY + 0.0001));
    }
    else if (_DissolveType > 0.5) // 1=WorldY（三平面投影ノイズ + ワールド高さグラデ）
    {
        float3 blendWeights = abs(normalWS);
        blendWeights /= (blendWeights.x + blendWeights.y + blendWeights.z + 0.0001);
        float minW = min(blendWeights.x, min(blendWeights.y, blendWeights.z));
        blendWeights = max(blendWeights - minW, 0.0);
        blendWeights /= (blendWeights.x + blendWeights.y + blendWeights.z + 0.0001);

        float noiseX = 0.0, noiseY = 0.0, noiseZ = 0.0;
        if (blendWeights.x > 0.0) noiseX = SAMPLE_TEXTURE2D(_DissolveTex, sampler_LinearRepeat, positionWS.zy * _DissolveNoiseScale).r;
        if (blendWeights.y > 0.0) noiseY = SAMPLE_TEXTURE2D(_DissolveTex, sampler_LinearRepeat, positionWS.xz * _DissolveNoiseScale).r;
        if (blendWeights.z > 0.0) noiseZ = SAMPLE_TEXTURE2D(_DissolveTex, sampler_LinearRepeat, positionWS.xy * _DissolveNoiseScale).r;

        dissolveNoise = noiseX * blendWeights.x + noiseY * blendWeights.y + noiseZ * blendWeights.z;
        dissolveGrad  = saturate((positionWS.y - _DissolveStartY) / (_DissolveEndY - _DissolveStartY + 0.0001));
    }
    else // 0=None: UV ノイズのみ
    {
        dissolveNoise = SAMPLE_TEXTURE2D(_DissolveTex, sampler_LinearRepeat, uv * _DissolveNoiseScale).r;
    }

    DissolveInput di;
    di.noise         = dissolveNoise;
    di.grad          = dissolveGrad;
    di.amount        = _DissolveAmount;
    di.edgeWidth     = _DissolveEdgeWidth;
    di.noiseStrength = _DissolveNoiseStrength;
    di.edgeColor     = _DissolveEdgeColor.rgb;
    di.edgeColor2    = _DissolveEdgeColor2.rgb;
    di.invert        = _DissolveInvert;
    di.edgeStep      = (_DissolveEdgeStep > 0.5);
    di.isNoneType    = isNoneType;

    ResolveDissolve(di, albedo, dissolveEmission);
#endif
}

// clip 専用の軽量エントリ（Shadow / Depth / Outline / CharShadow 用）。
// albedo は捨てる（発光も不要）。
void ApplyIdolDissolveClip(float2 uv, float3 positionWS, float3 positionOS, float3 normalWS)
{
#if defined(_DISSOLVE_ON)
    half3 dummyAlbedo = half3(1, 1, 1);
    half3 dummyEmission;
    ApplyIdolDissolve(uv, positionWS, positionOS, normalWS, dummyAlbedo, dummyEmission);
#endif
}

#endif // IDOL_DISSOLVE_INCLUDED

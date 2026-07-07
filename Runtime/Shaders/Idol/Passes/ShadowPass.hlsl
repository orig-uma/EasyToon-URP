// =============================================================================
//  ShadowPass.hlsl
//  URP 標準の落ち影用（ShadowCaster）。
// =============================================================================
#ifndef IDOL_SHADOW_PASS_INCLUDED
#define IDOL_SHADOW_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "../IdolInput.hlsl"
#include "../IdolDissolve.hlsl"

// URP の組み込み変数（点光源シャドウ用）。
float3 _LightPosition;

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    float3 positionOS : TEXCOORD2;
    float3 normalWS   : TEXCOORD3;
};

Varyings vert_shadow(Attributes input)
{
    Varyings output = (Varyings)0;
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

    float3 lightDirectionWS = GetMainLight().direction;
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
        lightDirectionWS = normalize(_LightPosition.xyz - positionWS);
    #endif

    float3 biasedPositionWS = ApplyShadowBias(positionWS, normalWS, lightDirectionWS);
    float4 positionCS = TransformWorldToHClip(biasedPositionWS);

    #if UNITY_REVERSED_Z
        positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
    #else
        positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
    #endif

    output.positionCS = positionCS;
    output.uv = input.uv;
    output.positionWS = positionWS;
    output.positionOS = input.positionOS.xyz;
    output.normalWS   = normalWS;
    return output;
}

half4 frag_shadow(Varyings input) : SV_Target
{
    // ColorMask 0。アルファクリップ用に .a のみ取得する。
    #if defined(_ALPHATEST_ON)
        float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
        half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a * _BaseColor.a;
        clip(alpha - _Cutoff);
    #endif

    #if defined(_DISSOLVE_ON)
        ApplyIdolDissolveClip(input.uv * _MainTex_ST.xy + _MainTex_ST.zw,
                               input.positionWS, input.positionOS, input.normalWS);
    #endif
    return 0;
}

#endif // IDOL_SHADOW_PASS_INCLUDED

// =============================================================================
//  DepthOnlyPass.hlsl
//  深度バッファのみへ書き込む（DepthOnly）。Depth Prepass / Forward+ の深度生成用。
// =============================================================================
#ifndef IDOL_DEPTH_ONLY_PASS_INCLUDED
#define IDOL_DEPTH_ONLY_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "../IdolInput.hlsl"
#include "../IdolDissolve.hlsl"

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

Varyings vert_depth(Attributes input)
{
    Varyings output = (Varyings)0;
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.uv         = input.uv;
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionOS = input.positionOS.xyz;
    output.normalWS   = TransformObjectToWorldNormal(input.normalOS);

    return output;
}

half4 frag_depth(Varyings input) : SV_Target
{
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

#endif // IDOL_DEPTH_ONLY_PASS_INCLUDED

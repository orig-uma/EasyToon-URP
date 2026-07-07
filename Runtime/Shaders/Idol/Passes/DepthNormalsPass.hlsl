// =============================================================================
//  DepthNormalsPass.hlsl
//  深度＋ワールド法線を書き込む（DepthNormals）。SSAO / Decal / 深度リム前提。
// =============================================================================
#ifndef IDOL_DEPTH_NORMALS_PASS_INCLUDED
#define IDOL_DEPTH_NORMALS_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "../IdolInput.hlsl"
#include "../IdolDissolve.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float3 tangentWS  : TEXCOORD2;
    float3 bitangentWS: TEXCOORD3;
    float3 positionWS : TEXCOORD4;
    float3 positionOS : TEXCOORD5;
};

Varyings vert_depthnormals(Attributes input)
{
    Varyings output = (Varyings)0;
    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.positionCS  = vertexInput.positionCS;
    output.uv          = input.uv;
    output.normalWS    = normalInput.normalWS;
    output.tangentWS   = normalInput.tangentWS;
    output.bitangentWS = normalInput.bitangentWS;
    output.positionWS  = vertexInput.positionWS;
    output.positionOS  = input.positionOS.xyz;

    return output;
}

half4 frag_depthnormals(Varyings input) : SV_Target
{
    float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;

    #if defined(_ALPHATEST_ON)
        half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a * _BaseColor.a;
        clip(alpha - _Cutoff);
    #endif

    #if defined(_DISSOLVE_ON)
        ApplyIdolDissolveClip(uv, input.positionWS, input.positionOS,
                               normalize(input.normalWS));
    #endif

    // ノーマルマップを反映した法線を出力（SSAO / Decal の精度向上）。
    // sampler_MainTex は使わない: キーワード無しでは _MainTex がこのパスで
    // サンプルされず、コンパイラが _MainTex を除去して共有サンプラが
    // 「どのテクスチャにも属さない」扱いになりエラーになるため（d3d11）。
    half4 normalSample = SAMPLE_TEXTURE2D(_NormalMap, sampler_LinearRepeat, uv);
    half3 normalTS = UnpackNormalScale(normalSample, _NormalScale);
    half3 normalWS = normalize(normalTS.x * input.tangentWS
                             + normalTS.y * input.bitangentWS
                             + normalTS.z * input.normalWS);

    return half4(NormalizeNormalPerPixel(normalWS), 0.0);
}

#endif // IDOL_DEPTH_NORMALS_PASS_INCLUDED

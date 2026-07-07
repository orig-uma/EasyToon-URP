// =============================================================================
//  CharShadowPass.hlsl
//  キャラ専用セルフシャドウのキャスター（LightMode = "IdolCharShadow"）。
//  キャラだけをライト方向の専用深度マップへ描く方式。頂点変換は
//  グローバル _IdolCharShadowMatrix（ライト VP）、バイアスは _IdolCharShadowBias
//  （x=深度, y=法線）。深度のみ書き込み（ColorMask 0）。
// =============================================================================
#ifndef IDOL_CHAR_SHADOW_PASS_INCLUDED
#define IDOL_CHAR_SHADOW_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "../IdolInput.hlsl"
#include "../IdolDissolve.hlsl"

// Feature が供給するグローバル（per-material CBUFFER には入れない＝SRP Batcher 維持）。
float4x4 _IdolCharShadowMatrix;   // ライト VP（正射影）
float2   _IdolCharShadowBias;     // x=深度バイアス, y=法線バイアス

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

Varyings vert_charshadow(Attributes input)
{
    Varyings output = (Varyings)0;

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

    // キャスター側バイアス: 法線方向に押してアクネ（自己影の縞）を抑える。
    positionWS += normalWS * _IdolCharShadowBias.y;

    // グローバルのライト VP でクリップ座標へ。
    float4 positionCS = mul(_IdolCharShadowMatrix, float4(positionWS, 1.0));

    // 深度バイアス: キャスターの格納深度を光源から遠ざけ（深く）して自己影の
    //   縞（アクネ）を抑える。reversed-Z は near=1/far=0 のため深く = z を減らす。
    #if UNITY_REVERSED_Z
        positionCS.z -= _IdolCharShadowBias.x;
    #else
        positionCS.z += _IdolCharShadowBias.x;
    #endif

    output.positionCS = positionCS;
    output.uv         = input.uv;
    output.positionWS = positionWS;
    output.positionOS = input.positionOS.xyz;
    output.normalWS   = normalWS;
    return output;
}

half4 frag_charshadow(Varyings input) : SV_Target
{
    #if defined(_ALPHATEST_ON)
        float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
        half alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a * _BaseColor.a;
        clip(alpha - _Cutoff);
    #endif

    #if defined(_DISSOLVE_ON)
        float2 duv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
        ApplyIdolDissolveClip(duv, input.positionWS, input.positionOS, input.normalWS);
    #endif

    return 0;
}

#endif // IDOL_CHAR_SHADOW_PASS_INCLUDED

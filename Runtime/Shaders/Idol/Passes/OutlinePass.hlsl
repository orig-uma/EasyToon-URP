// =============================================================================
//  OutlinePass.hlsl
//  背面法線拡張アウトライン（Cull Front）。LightMode = "IdolOutline"。
//  頂点カラー R=太さ倍率 / G=Zオフセット。カメラ距離正規化 + FOV 補正 +
//  スクリーン幅上限クランプで、近接で太らず遠方で消えない一定幅の線にする。
// =============================================================================
#ifndef IDOL_OUTLINE_PASS_INCLUDED
#define IDOL_OUTLINE_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "../IdolInput.hlsl"
#include "../IdolDissolve.hlsl"

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 color        : COLOR;   // R=太さ倍率, G=Zオフセット
    float2 uv           : TEXCOORD0;
};

struct Varyings
{
    float4 positionCS   : SV_POSITION;
    float2 uv           : TEXCOORD0;
    half   fogFactor    : TEXCOORD1;
    float3 positionWS   : TEXCOORD2;
    float3 positionOS   : TEXCOORD3;
    float3 normalWS     : TEXCOORD4;
};

Varyings vert_outline(Attributes input)
{
    Varyings output = (Varyings)0;

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

    // カメラからの距離（ビュー空間 -Z）。
    float viewDepth = abs(TransformWorldToView(positionWS).z);

    // mm 単位の実太さ。頂点カラー R で部位ごとに倍率調整。
    float widthMul = input.color.r;
    float worldWidth = _OutlineWidth * 0.001 * max(widthMul, 0.0);

    // FOV 補正 + 距離正規化: 距離に比例して押し出しつつ FOV を打ち消すことで、
    //   ズーム（狭 FOV）でも画面上の幅がほぼ一定になる。
    //   unity_CameraProjection._m11 = cot(fovY/2)。距離 / projScaleY で
    //   「画面高さに対する縦方向のワールド長」に換算した基準太さを得る。
    float projScaleY = max(unity_CameraProjection._m11, 1e-4); // = cot(fovY/2)
    float worldPerScreenHeight = viewDepth / projScaleY;       // 深度での画面高さ相当ワールド長
    float expand = worldWidth * worldPerScreenHeight;

    // スクリーン幅上限クランプ: 近接で太りすぎないよう、画面ピクセル換算した
    //   太さの上限 _OutlineMaxScreenPx に収める。
    //   画面高さ = _ScreenParams.y px ⇔ worldPerScreenHeight ワールド長。
    float maxWorld = (_OutlineMaxScreenPx / max(_ScreenParams.y, 1.0)) * worldPerScreenHeight;
    expand = min(expand, maxWorld);

    float3 expandedPositionWS = positionWS + normalWS * expand;
    output.positionCS = TransformWorldToHClip(expandedPositionWS);

    // Z オフセット（頂点カラー G）: 線を奥に押して重なりを整える。
    float zOffset = _OutlineZOffset * input.color.g;
    #if UNITY_REVERSED_Z
        output.positionCS.z -= zOffset * output.positionCS.w;
    #else
        output.positionCS.z += zOffset * output.positionCS.w;
    #endif

    output.uv = input.uv;
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    output.positionWS = positionWS;    // 押し出し前の元位置（Dissolve 判定用）
    output.positionOS = input.positionOS.xyz;
    output.normalWS   = normalWS;
    return output;
}

half4 frag_outline(Varyings input) : SV_Target
{
    float2 uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
    half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _BaseColor;

    #if defined(_ALPHATEST_ON)
        clip(albedo.a - _Cutoff);
    #endif

    #if defined(_DISSOLVE_ON)
        ApplyIdolDissolveClip(uv, input.positionWS, input.positionOS,
                               normalize(input.normalWS));
    #endif

    // アルベド連動: その場のアルベド × Outline Color を線色にブレンド。
    //   髪には髪の、肌には肌の系統色の線が付き、固定単色より馴染む。
    half3 lineColor = lerp(_OutlineColor.rgb, albedo.rgb * _OutlineColor.rgb, _OutlineAlbedoBlend);

    // フォグ（本体と同条件で沈め、線だけ浮かないようにする）。
    lineColor = MixFog(lineColor, input.fogFactor);

    return half4(lineColor, _OutlineColor.a);
}

#endif // IDOL_OUTLINE_PASS_INCLUDED

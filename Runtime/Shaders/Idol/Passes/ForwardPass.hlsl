// =============================================================================
//  ForwardPass.hlsl
//  Idol のメイン描画パス（UniversalForward）。
// =============================================================================
#ifndef IDOL_FORWARD_PASS_INCLUDED
#define IDOL_FORWARD_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
// 深度リム用（_CameraDepthTexture。カメラの Depth Texture が必要）。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "../IdolLighting.hlsl"
#include "../IdolShadows.hlsl"
#include "../IdolRim.hlsl"
#include "../IdolDissolve.hlsl"

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float2 uv           : TEXCOORD0;
};

struct Varyings
{
    float4 positionCS   : SV_POSITION;
    float3 positionWS   : TEXCOORD0;
    float3 normalWS     : TEXCOORD1;
    float2 uv           : TEXCOORD2;
    float4 shadowCoord  : TEXCOORD3;
    float3 tangentWS    : TEXCOORD4;
    float3 bitangentWS  : TEXCOORD5;
    half   fogFactor    : TEXCOORD6;
    float3 positionOS   : TEXCOORD7;  // Dissolve LocalY 用
};

#define IDOL_SURFACE_IMPL
#include "../IdolSurface.hlsl"

Varyings vert(Attributes input)
{
    Varyings output = (Varyings)0;
    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.positionWS  = vertexInput.positionWS;
    output.positionCS  = vertexInput.positionCS;
    output.normalWS    = normalInput.normalWS;
    output.tangentWS   = normalInput.tangentWS;
    output.bitangentWS = normalInput.bitangentWS;
    output.uv          = input.uv;
    output.shadowCoord = GetShadowCoord(vertexInput);
    output.fogFactor   = ComputeFogFactor(vertexInput.positionCS.z);
    output.positionOS  = input.positionOS.xyz;

    return output;
}

half4 frag(Varyings input) : SV_Target
{
    half3 finalColor = half3(0, 0, 0);

    half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

    half alpha;
    IdolSurfaceData s = GatherSurface(input, viewDirectionWS, alpha);

    // Dissolve（消失）: 縁付近でアルベド・陰色を縁色へ寄せつつ clip。
    //   エッジ発光は後段で Emission に加算。_DISSOLVE_ON 未定義時は完全スキップ。
    //   陰色（1影/2影/落ち影）も同じ縁色へ lerp し、暗部でも縁が沈まないようにする。
    half3 dissolveEmission = half3(0, 0, 0);
    #if defined(_DISSOLVE_ON)
        half3 preAlbedo = s.albedo;
        ApplyIdolDissolve(input.uv * _MainTex_ST.xy + _MainTex_ST.zw,
                           input.positionWS, input.positionOS, s.cleanNormalWS,
                           s.albedo, dissolveEmission);
        // アルベドの縁色寄せ量（0..1）を陰色にも同量だけ適用（hue-shift 済みの
        //   陰色を保ちつつ、縁だけを縁色へ寄せる）。
        half edgeLerp = saturate(distance(s.albedo, preAlbedo) / (distance(_DissolveEdgeColor2.rgb, preAlbedo) + 1e-4));
        s.shadow1Albedo    = lerp(s.shadow1Albedo,    s.albedo, edgeLerp);
        s.shadow2Albedo    = lerp(s.shadow2Albedo,    s.albedo, edgeLerp);
        s.castShadowAlbedo = lerp(s.castShadowAlbedo, s.albedo, edgeLerp);
    #endif

    // --- メインライト ---
    // カスケード有効時はカスケード跨ぎで頂点補間座標が破綻するため per-pixel で再計算。
    // SCREEN ケースは頂点側の GetShadowCoord が処理済み。
    #if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
        float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    #else
        float4 shadowCoord = input.shadowCoord;
    #endif
    Light mainLight = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));

    // 仮想ライト方向オーバーライド。陰・スペキュラ・リム・SDF すべてに効く。
    //   IdolCharacter が _VirtualLightDir(xyz=方向, w=ブレンド) を書く。既定 w=0 で素通し。
    UNITY_BRANCH
    if (_VirtualLightDir.w > 0.0)
    {
        mainLight.direction = normalize(lerp(mainLight.direction, _VirtualLightDir.xyz, _VirtualLightDir.w));
    }

    // キャラ用ライト整形（色影響度 / 彩度上限 / 輝度下限）。既定値では素通し。
    UNITY_BRANCH
    if (_LightColorInfluence < 1.0 || _LightSaturationLimit < 1.0 || _LightMinBrightness > 0.0)
    {
        mainLight.color = ConditionLightColor(mainLight.color,
            _LightColorInfluence, _LightSaturationLimit, _LightMinBrightness);
    }

    // 落ち影（URP 標準。IdolShadows のラッパー経由で後日差し替え可）。
    float mainNdotL = dot(s.cleanNormalWS, mainLight.direction);
    half mainShadowAtten = GetIdolMainShadow(mainLight, input.positionWS, s.cleanNormalWS, mainNdotL, _CharShadowFaceBias);
    half castShadow = lerp(1.0, mainShadowAtten, _ReceiveShadowStrength);

    // 髪→顔のスクリーンスペース落ち影。castShadow に min() 合成。
    //   合成順: URP影・キャラ影 → 髪影 → SDF（SDF の _FaceSDFShadowMix が
    //   CalculateSingleLight 内で castShadow に掛かるため、この位置で折り込む）。
    UNITY_BRANCH
    if (_HairShadowIntensity > 0.0)
    {
        float2 hairScreenUV = GetNormalizedScreenSpaceUV(input.positionCS);
        castShadow = min(castShadow,
            CalculateHairScreenShadow(hairScreenUV, input.positionCS.w, mainLight.direction));
    }

    // Face SDF 顔影（_FaceSDFEnable。無効時 sdfLit = -1 で通常陰へ）。
    half sdfMask;
    float sdfLit = ComputeFaceSDF(input, mainLight, sdfMask);

    finalColor += CalculateSingleLight(mainLight, s, viewDirectionWS, castShadow, true, sdfLit, sdfMask);

    // リムライト（深度リム / フレネルリム / バックライトリム。メインライトのみ）。
    //   深度リムは positionCS からスクリーン UV、positionCS.w から視深度を取得。
    UNITY_BRANCH
    if (_RimDepthIntensity > 0.0 || _RimIntensity > 0.0 || _BackRimEnable > 0.5)
    {
        float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
        finalColor += CalculateRimLighting(s, screenUV, input.positionCS.w,
                                           mainLight, viewDirectionWS, castShadow);
    }

    // --- 追加ライト（Forward+ / クラスタ対応） ---
    #if defined(_ADDITIONAL_LIGHTS) || defined(_CLUSTER_LIGHT_LOOP) || defined(_FORWARD_PLUS)
        uint pixelLightCount = GetAdditionalLightsCount();

        // URP の LIGHT_LOOP_BEGIN は Forward+/クラスタ時に「inputData」という名前の
        // ローカル変数（positionWS / normalizedScreenSpaceUV）をマクロ内で直接参照する。
        // Idol は InputData を使わないため、マクロ要件を満たす最小構成だけ用意する。
        InputData inputData = (InputData)0;
        inputData.positionWS = input.positionWS;
        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

        #define IDOL_ACCUMULATE_ADDITIONAL_LIGHT(index)                                  \
            {                                                                             \
                Light addLight = GetAdditionalLight(index, input.positionWS, half4(1,1,1,1)); \
                half addCast = lerp(1.0, addLight.shadowAttenuation, _ReceiveShadowStrength); \
                half3 addContrib = CalculateSingleLight(addLight, s, viewDirectionWS, addCast, false, -1.0, 1.0); \
                finalColor = (_AdditionalLightBlendMode > 0.5)                                      \
                    ? max(finalColor, addContrib)                                         \
                    : finalColor + addContrib;                                            \
            }

        #if defined(USE_FORWARD_PLUS) || defined(USE_CLUSTER_LIGHT_LOOP)
        [loop] for (uint dirLightIndex = 0u;
                    dirLightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS);
                    dirLightIndex++)
        {
            IDOL_ACCUMULATE_ADDITIONAL_LIGHT(dirLightIndex)
        }
        #endif

        LIGHT_LOOP_BEGIN(pixelLightCount)
            IDOL_ACCUMULATE_ADDITIONAL_LIGHT(lightIndex)
        LIGHT_LOOP_END

        #undef IDOL_ACCUMULATE_ADDITIONAL_LIGHT
    #endif

    ApplyPostEffects(finalColor, input, s, mainLight);

    // Dissolve のエッジ発光（HDR）を加算（Black Out より後、フォグより前）。
    #if defined(_DISSOLVE_ON)
        finalColor += dissolveEmission;
    #endif

    // フォグ（頂点で算出した係数を最終色に合成）。
    finalColor = MixFog(finalColor, input.fogFactor);

    // 前髪透過パス（HairSeeThrough）: 同一ライティングのまま、眉・目の上に
    // 重なるピクセルだけを _HairSeeThroughAlpha の半透明で重ね描きする。
    #if defined(IDOL_HAIR_SEETHROUGH)
        alpha = _HairSeeThroughAlpha;
    #endif

    return half4(finalColor, alpha);
}

#endif // IDOL_FORWARD_PASS_INCLUDED

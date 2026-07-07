// =============================================================================
//  Origuma/EasyToon_URP/Idol
// -----------------------------------------------------------------------------
//  URP (Universal Render Pipeline) Forward+ 用の高品質 NPR（Toon）キャラクター
//  シェーダー。BaseMap 一枚 + 既定値で成立し、ベイクで質感を積み増す設計。
// =============================================================================
Shader "Origuma/EasyToon_URP/Idol"
{
    Properties
    {
        // --- Surface Options ---------------------------------------------------
        [Header(Surface Options)]
        [Enum(Opaque, 0, Cutout, 1)] _RenderMode ("Render Mode", Float) = 0
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clipping", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2

        // --- Base --------------------------------------------------------------
        [Header(Base)]
        [MainTexture] _MainTex ("Base Map (RGB / Alpha)", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        [Toggle] _UseColorCorrection ("Enable Color Correction", Float) = 0
        _HueShift ("Hue Shift", Range(-0.5, 0.5)) = 0.0
        _Saturation ("Saturation", Range(0.0, 2.0)) = 1.0
        _ValueMulti ("Value Multiplier", Range(0.0, 2.0)) = 1.0
        [NoScaleOffset][Normal] _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalScale ("Normal Scale", Range(0.0, 2.0)) = 1.0

        // --- Shading Mode ------------------------------------------------------
        [Header(Shading)]
        [Enum(TwoBand, 0, Ramp, 1)] _ShadingMode ("Shading Mode", Float) = 0
        [NoScaleOffset] _ShadeRampMap ("Shade Ramp (HalfLambert 0..1)", 2D) = "white" {}
        [NoScaleOffset] _ShadeNormalMap ("Shade Normal Map", 2D) = "bump" {}
        _ShadeNormalStrength ("Shade Normal Strength", Range(0.0, 1.0)) = 0.0
        _HalfLambertWrap ("Light Wrap", Range(0.0, 1.0)) = 0.0

        // --- Two Band Shadow ---------------------------------------------------
        [Header(Two Band Shadow)]
        _ShadowColor ("1st Shadow Color", Color) = (0.62, 0.60, 0.72, 1)
        _ToonStep ("1st Shadow Threshold", Range(0.0, 1.0)) = 0.5
        _ToonFeather ("1st Shadow Softness", Range(0.0, 1.0)) = 0.08
        _Shadow2Color ("2nd Shadow Color (A = Enable)", Color) = (0.48, 0.46, 0.62, 0)
        _Shadow2Step ("2nd Shadow Threshold", Range(0.0, 1.0)) = 0.28
        _Shadow2Feather ("2nd Shadow Softness", Range(0.0, 1.0)) = 0.06
        _ShadowHueShift ("Shadow Hue Shift", Range(-0.5, 0.5)) = 0.02
        _ShadowSaturation ("Shadow Saturation Boost", Range(0.0, 2.0)) = 1.15

        // --- Cast Shadow -------------------------------------------------------
        [Header(Cast Shadow)]
        _CastShadowColor ("Cast Shadow Color (A = Enable)", Color) = (0.55, 0.58, 0.72, 0)
        _ReceiveShadowStrength ("Receive Shadow Strength", Range(0.0, 1.0)) = 1.0

        // --- Occlusion (EasyShaderCore Baker 同名) -----------------------------------
        [Header(Occlusion)]
        [NoScaleOffset] _OcclusionMap ("Occlusion Map (R)", 2D) = "white" {}
        _OcclusionToShadow ("AO To Shadow Threshold", Range(0.0, 1.0)) = 0.0
        _OcclusionStrength ("Occlusion Strength (Albedo Darken)", Range(0.0, 1.0)) = 0.0

        // --- Cel Specular ------------------------------------------------------
        [Header(Cel Specular)]
        [HDR] _SpecularColor ("Specular Color (HDR)", Color) = (1, 1, 1, 1)
        _SpecularMask ("Specular Mask (R)", 2D) = "white" {}
        _ToonSpecularStep ("Specular Threshold", Range(0.0, 1.0)) = 0.5
        _ToonSpecularFeather ("Specular Softness", Range(0.0, 0.5)) = 0.05
        _Smoothness ("Specular Smoothness", Range(0.01, 1.0)) = 0.8
        _SpecularIntensity ("Specular Intensity", Range(0.0, 5.0)) = 0.0
        _SpecularShadeInfluence ("Specular Shade Dimming", Range(0.0, 1.0)) = 0.5
        _SpecularAA ("Specular Anti-Aliasing", Range(0.0, 1.0)) = 1.0

        // --- Stocking / Sheer Fabric ---------------------------------
        [Header(Stocking)]
        _StockingIntensity ("Stocking Intensity", Range(0.0, 1.0)) = 0.0
        _StockingColor ("Stocking Color", Color) = (0.76, 0.65, 0.55, 1)
        [NoScaleOffset] _StockingMask ("Stocking Mask (R)", 2D) = "white" {}
        _StockingFrontOpacity ("Stocking Front Opacity", Range(0.0, 1.0)) = 0.25
        _StockingPower ("Stocking Graze Power", Range(0.5, 8.0)) = 1.5
        [HDR] _StockingSheenColor ("Stocking Sheen Color (HDR)", Color) = (0, 0, 0, 1)
        _StockingSheenPower ("Stocking Sheen Power", Range(1.0, 16.0)) = 3.0

        // --- Face SDF ----------------------------------------------------------
        [Header(Face SDF Shadow)]
        [Toggle] _FaceSDFEnable ("Enable Face SDF Shadow", Float) = 0
        [NoScaleOffset] _FaceSDFMap ("Face SDF Map (R=Right G=Left B=Up A=Down)", 2D) = "white" {}
        [Toggle] _FaceSDFFlip ("Face SDF Flip Forward", Float) = 0
        _FaceSDFSoftness ("Face SDF Softness", Range(0.001, 0.5)) = 0.5
        _FaceSDFShadowMix ("Face SDF External Shadow Mix", Range(0.0, 1.0)) = 1.0
        _FaceSDFBlendNormalMin ("SDF Blend Normal Min (SDF無効化のしきい値)", Range(-1.5, 1.0)) = -1.0
        _FaceSDFBlendNormalMax ("SDF Blend Normal Max (SDF有効化のしきい値)", Range(-1.0, 1.5)) = 0.0

        // --- Hair Screen-Space Shadow ---------------------------------
        [Header(Hair Screen Shadow)]
        _HairShadowIntensity ("Hair Shadow Intensity", Range(0.0, 1.0)) = 0.0
        _HairShadowOffsetPx ("Hair Shadow Offset (px)", Range(0.5, 16.0)) = 4.0
        _HairShadowDepthMin ("Hair Shadow Depth Min (m)", Range(0.001, 0.5)) = 0.01
        _HairShadowDepthMax ("Hair Shadow Depth Max (m)", Range(0.001, 1.0)) = 0.15

        // --- Rim Light ---------------------------------------------------------
        [Header(Rim Light)]
        [HDR] _RimColor ("Rim Color (HDR)", Color) = (1, 1, 1, 1)
        _RimDepthIntensity ("Depth Rim Intensity", Range(0.0, 5.0)) = 0.0
        _RimWidthPx ("Depth Rim Width (px)", Range(0.5, 16.0)) = 3.0
        _RimDepthThreshold ("Depth Rim Threshold (m)", Range(0.01, 2.0)) = 0.3
        _RimLightAlign ("Rim Light Align (0=All 1=Lit Side)", Range(0.0, 1.0)) = 0.5
        _RimIntensity ("Fresnel Rim Intensity", Range(0.0, 5.0)) = 0.0
        _RimThickness ("Fresnel Rim Thickness", Range(0.0, 1.0)) = 0.2
        [Toggle] _BackRimEnable ("Enable Back Rim (Live)", Float) = 0
        [HDR] _BackRimColor ("Back Rim Color (HDR)", Color) = (1, 1, 1, 1)
        _BackRimPitch ("Back Rim Pitch", Range(-90.0, 90.0)) = 30.0
        _BackRimYaw ("Back Rim Yaw", Range(-180.0, 180.0)) = 180.0
        _BackRimPower ("Back Rim Power", Range(0.5, 12.0)) = 4.0

        // --- Angel Ring --------------------------------------------------------
        [Header(Angel Ring Hair Highlight)]
        [HDR] _AngelRingColor ("Angel Ring Color (HDR)", Color) = (1, 1, 1, 1)
        _AngelRingIntensity ("Angel Ring Intensity", Range(0.0, 5.0)) = 0.0
        _AngelRingThreshold ("Angel Ring Threshold", Range(0.0, 1.0)) = 0.5
        _AngelRingSoftness ("Angel Ring Softness", Range(0.0, 0.5)) = 0.1
        _AngelRingShift ("Angel Ring Shift", Range(-1.0, 1.0)) = 0.0
        _AngelRingViewFollow ("View Follow (0=Light 1=Camera)", Range(0.0, 1.0)) = 1.0
        [NoScaleOffset] _HairFlowMap ("Hair Flow Map", 2D) = "white" {}
        _HairFlowStrength ("Hair Flow Strength", Range(0.0, 1.0)) = 0.0

        // --- Hair See-Through --------------------------------------------------
        [Header(Hair See Through)]
        _HairSeeThroughAlpha ("Hair See-Through Alpha", Range(0.0, 1.0)) = 0.6

        // --- Indirect ----------------------------------------------------------
        [Header(Indirect Light)]
        _IndirectFlatten ("Indirect Flatten", Range(0.0, 1.0)) = 0.0
        _IndirectIntensity ("Indirect Intensity", Range(0.0, 2.0)) = 1.0
        _IndirectTint ("Indirect Tint", Color) = (1, 1, 1, 1)

        // --- Light Conditioning ------------------------------------------------
        [Header(Light Conditioning)]
        _LightColorInfluence ("Light Color Influence", Range(0.0, 1.0)) = 1.0
        _LightSaturationLimit ("Light Saturation Limit", Range(0.0, 1.0)) = 1.0
        _LightMinBrightness ("Light Min Brightness", Range(0.0, 1.0)) = 0.0

        // --- Additional Lights -------------------------------------------------
        [Header(Additional Lights)]
        [Enum(Add, 0, Max, 1)] _AdditionalLightBlendMode ("Additional Light Blend", Float) = 1
        _AdditionalBlowoutLimit ("Additional Blowout Limit", Range(0.1, 5.0)) = 1.0

        // --- MatCap ------------------------------------------------------------
        [Header(MatCap)]
        [Toggle] _UseMatCap ("Enable MatCap", Float) = 0
        [Enum(Add, 0, Multiply, 1)] _MatCapBlend ("MatCap Blend Mode", Float) = 0
        [NoScaleOffset] _MatCapTex ("MatCap Texture (RGB)", 2D) = "black" {}
        [HDR] _MatCapColor ("MatCap Tint (HDR)", Color) = (1, 1, 1, 1)
        _MatCapIntensity ("MatCap Intensity", Range(0.0, 5.0)) = 1.0

        // --- Emission ----------------------------------------------------------
        [Header(Emission)]
        [Toggle] _UseEmission ("Enable Emission", Float) = 0
        [NoScaleOffset] _EmissionMap ("Emission Map (RGB)", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color (HDR)", Color) = (0, 0, 0, 1)
        _EmissionIntensity ("Emission Intensity", Range(0.0, 10.0)) = 1.0

        // --- Black Out ---------------------------------------------------------
        [Header(Live)]
        _BlackOut ("Black Out", Range(0.0, 1.0)) = 0.0

        // --- Virtual Light / Char Shadow ----------------------------
        //  _VirtualLightDir は IdolCharacter が書き込む（xyz=方向, w=ブレンド）。
        [Header(Character Control)]
        _VirtualLightDir ("Virtual Light Dir (xyz=dir w=blend)", Vector) = (0, 0, 1, 0)
        _CharShadowFaceBias ("Char Shadow Face Bias (受影追い込み)", Range(0.0, 0.02)) = 0.0

        // --- Dissolve (EasyShaderCore 資産流用) -----------------------------
        [Header(Dissolve)]
        [Toggle(_DISSOLVE_ON)] _UseDissolve ("Enable Dissolve", Float) = 0
        _DissolveAmount ("Dissolve Progress", Range(0.0, 1.0)) = 0.0
        [Toggle] _DissolveInvert ("Invert Dissolve", Float) = 0
        [Enum(None, 0, WorldY, 1, LocalY, 2)] _DissolveType ("Dissolve Axis", Float) = 1
        _DissolveStartY ("Start Y", Float) = 0.0
        _DissolveEndY ("End Y", Float) = 2.0
        [NoScaleOffset] _DissolveTex ("Dissolve Noise (R)", 2D) = "white" {}
        _DissolveNoiseScale ("Noise Scale", Float) = 1.0
        _DissolveNoiseStrength ("Noise Strength", Range(0.0, 1.0)) = 0.5
        [HDR] _DissolveEdgeColor ("Edge Outer Color (HDR)", Color) = (1.0, 0.6, 0.0, 1.0)
        [HDR] _DissolveEdgeColor2 ("Edge Inner Color (HDR)", Color) = (1.0, 0.0, 0.0, 1.0)
        _DissolveEdgeWidth ("Edge Width", Range(0.001, 0.5)) = 0.05
        [Toggle] _DissolveEdgeStep ("Step Edge (Toon Style)", Float) = 0

        // --- Chara Part / Stencil ---------------------------------------------
        //  前髪透過のステンシルビットレイアウト（bit1=2: Brow, bit2=4: Eye）:
        //   Brow: Ref=2, Comp=Always, Pass=Replace, WriteMask=6
        //   Eye : Ref=4, Comp=Always, Pass=Replace, WriteMask=6
        //   Hair: Ref=0, Comp=Equal, ReadMask=6（眉・目の上には本体を描かない）
        //         → 眉・目の上は HairSeeThrough パスが半透明で重ね描き
        //   Body/Face: 既定値（Comp=Always, Keep）のまま
        //  描画順の前提: Brow/Eye が Hair より先に描かれること。
        //  上記一式は IdolShaderGUI の Chara Part プリセットが一括適用する
        //  （Stencil / Render Queue: Body,Face=2000, Brow,Eye=2002, Hair=2010 /
        //    HairSeeThrough パスの有効化）。手動設定は非推奨。
        [Header(Chara Part and Stencil)]
        [Enum(Body, 0, Face, 1, Brow, 2, Hair, 3, Eye, 4)] _CharaPart ("Chara Part", Float) = 0
        _StencilRef ("Stencil Reference", Range(0, 255)) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Compare", Float) = 8 // Always
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilPass ("Stencil Pass", Float) = 0 // Keep
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilFail ("Stencil Fail", Float) = 0 // Keep
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilZFail ("Stencil ZFail", Float) = 0 // Keep
        _StencilReadMask ("Stencil Read Mask", Range(0, 255)) = 255
        _StencilWriteMask ("Stencil Write Mask", Range(0, 255)) = 255

        // --- Outline -----------------------------------------------------------
        [Header(Outline)]
        _OutlineColor ("Outline Color", Color) = (0.15, 0.12, 0.16, 1)
        _OutlineAlbedoBlend ("Outline Albedo Blend", Range(0.0, 1.0)) = 0.35
        _OutlineWidth ("Outline Width (mm)", Range(0.0, 20.0)) = 2.0
        _OutlineMaxScreenPx ("Outline Max Screen Px", Range(1.0, 30.0)) = 8.0
        _OutlineZOffset ("Outline Z Offset", Range(0.0, 1.0)) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
            #include "IdolInput.hlsl"
        ENDHLSL

        // =====================================================================
        //  ForwardLit パス
        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
                Fail [_StencilFail]
                ZFail [_StencilZFail]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            // キャラ専用セルフシャドウ（IdolCharShadowFeature がグローバル供給）。
            #pragma multi_compile_fragment _ _IDOL_CHARSHADOW
            #pragma multi_compile_fog

            #include "Passes/ForwardPass.hlsl"
            ENDHLSL
        }

        // =====================================================================
        //  HairSeeThrough パス（前髪透過）
        //  Brow/Eye がステンシル bit(2/4) を書いたピクセルの上にだけ、本体と
        //  同一ライティングの髪を _HairSeeThroughAlpha の半透明で重ね描きする。
        //  本体 ForwardLit（髪: Comp Equal, Ref 0, ReadMask 6）が描かなかった
        //  穴をこのパスが埋める構成。LightMode=SRPDefaultUnlit は URP が既定で
        //  描画するため RendererFeature 不要。
        //  ※ 描画順の前提: Brow/Eye → Hair（Chara Part プリセットが Queue で保証）。
        //  ※ IdolShaderGUI の Chara Part プリセットが _CharaPart != Hair の
        //    マテリアルでこのパスを SetShaderPassEnabled("SRPDefaultUnlit", false)
        //    により無効化する（非髪の誤描画防止）。
        // =====================================================================
        Pass
        {
            Name "HairSeeThrough"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Stencil
            {
                Ref 0
                ReadMask 6
                Comp NotEqual
            }

            Cull [_Cull]
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            // ForwardPass 共有のため同じキャラ影キーワードを持たせる。
            #pragma multi_compile_fragment _ _IDOL_CHARSHADOW
            #pragma multi_compile_fog

            // ForwardPass と同一ライティング。alpha のみ _HairSeeThroughAlpha へ。
            #define IDOL_HAIR_SEETHROUGH
            #include "Passes/ForwardPass.hlsl"
            ENDHLSL
        }

        // =====================================================================
        //  ShadowCaster パス
        // =====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert_shadow
            #pragma fragment frag_shadow

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Passes/ShadowPass.hlsl"
            ENDHLSL
        }

        // =====================================================================
        //  DepthOnly パス
        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            // ForwardLit と同一のプロパティ駆動 Stencil。
            //  理由: Depth Priming / 深度プリパス構成では髪の DepthOnly が眉・目の
            //  深度を先に書き、眉の ForwardLit が ZTest で落ちてステンシルビットが
            //  立たず前髪透過が壊れる。プリパス側でも Brow(Ref2/Replace)→Hair
            //  (Comp Equal Ref0 ReadMask6) の相互作用を再現し、プリパス有無で
            //  挙動を一致させる。
            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
                Fail [_StencilFail]
                ZFail [_StencilZFail]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert_depth
            #pragma fragment frag_depth

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON

            #include "Passes/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // =====================================================================
        //  DepthNormals パス
        // =====================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            // DepthOnly と同理由で ForwardLit と同一の Stencil を敷く
            //  （DepthNormals プリパス構成でも前髪透過ビットを一致させる）。
            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
                Fail [_StencilFail]
                ZFail [_StencilZFail]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert_depthnormals
            #pragma fragment frag_depthnormals

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON

            #include "Passes/DepthNormalsPass.hlsl"
            ENDHLSL
        }

        // =====================================================================
        //  Outline パス
        //  LightMode は独自タグ "IdolOutline"。URP 既定では描画されないため
        //  ForwardLit のバッチングを阻害しない。描画には IdolOutlineFeature
        //  （RendererFeature）が必要。
        // =====================================================================
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "IdolOutline" }

            Cull Front // 背面法線拡張のため前面をカリング
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert_outline
            #pragma fragment frag_outline

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON
            #pragma multi_compile_fog

            #include "Passes/OutlinePass.hlsl"
            ENDHLSL
        }

        // =====================================================================
        //  CharShadowCaster パス（キャラ専用セルフシャドウ）
        //  LightMode = "IdolCharShadow"。IdolCharShadowFeature が専用深度マップへ
        //  描画する（URP 既定では描かれない）。ColorMask 0・深度のみ。頂点変換は
        //  グローバル _IdolCharShadowMatrix（ライト VP）を使う。
        // =====================================================================
        Pass
        {
            Name "CharShadowCaster"
            Tags { "LightMode" = "IdolCharShadow" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert_charshadow
            #pragma fragment frag_charshadow

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DISSOLVE_ON

            #include "Passes/CharShadowPass.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "Origuma.EasyToon.URP.Editor.IdolShaderGUI"
}

// ShadowCaster（URP の落ち影）。
// `_ShadowCasterOff` で顔だけシャドウマップから外せる（FR-26）。
//
// **`#pragma` はここに置かないこと。** 素の `#include` の中の pragma は
// Unity が読まず、**キーワードが黙って立たなくなる。**
// pragma は `.shader` 側に残してある。
//
// 切り出しは 1 行も変えていない（include を展開し直してバイト一致を確認・T-211）。

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                // ディゾルブの進み具合（頂点で求めて 1 float で運ぶ）。
                // **本体と同じ場所で切らないと、消えた部分が影／深度に残る。**
                float  dissolveGrad : TEXCOORD1;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                UNITY_BRANCH
                if (_ShadowCasterOff > 0.5)
                {
                    // 顔をシャドウマップから外す（FR-26）。鼻や眉が作る自己影は
                    // SDF で引いた境界を汚すだけで、絵として使い道が無い。
                    // 首の落ち影は NPRMap の G に描く前提。
                    //
                    // Renderer 単位の Cast Shadows Off では、顔と体が同じ
                    // SkinnedMeshRenderer のサブメッシュだと体まで消える。
                    // マテリアル単位で潰せるこの形にしている理由。
                    output.positionCS = float4(0, 0, -1e10, 1);
                    output.uv         = 0;
                    output.dissolveGrad = 0;
                    return output;
                }

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                output.positionCS = positionCS;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.dissolveGrad = ToonDissolveGradient(
                    input.positionOS.xyz, TransformObjectToWorld(input.positionOS.xyz));
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(input.positionCS);
                #endif

                #if defined(_ALPHATEST_ON)
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(a - _Cutoff);
                #endif

                // ディゾルブ。ForwardLit と**同じ式で同じ場所を切る**。
                UNITY_BRANCH
                if (_DissolveAmount > 0.0)
                {
                    ToonDissolveClip(input.uv, input.dissolveGrad);
                }
                return 0;
            }

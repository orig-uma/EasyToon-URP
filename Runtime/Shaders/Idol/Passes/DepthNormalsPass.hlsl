// DepthNormals。
// SSAO の前提。**消すと SSAO が黙って効かなくなる。**
//
// **`#pragma` はここに置かないこと。** 素の `#include` の中の pragma は
// Unity が読まず、**キーワードが黙って立たなくなる。**
// pragma は `.shader` 側に残してある。
//
// 切り出しは 1 行も変えていない（include を展開し直してバイト一致を確認・T-211）。

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
                // ディゾルブの進み具合（頂点で求めて 1 float で運ぶ）。
                // **本体と同じ場所で切らないと、消えた部分が影／深度に残る。**
                float  dissolveGrad : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                // **シングルパスインスタンス ステレオに必須。** 無いとレンダーターゲット
                // 配列のインデックスが設定されず、**片目にしか描かれない**。
                // このパスが埋めるのは _CameraNormalsTexture で、
                // 画面空間輪郭と SSAO がそれを読む。VR で片目だけ輪郭が消える形で出る。
                // URP 本体の DepthNormalsPass も同じ2つを持っている。
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexNormalInputs nrmIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.dissolveGrad = ToonDissolveGradient(
                    input.positionOS.xyz, TransformObjectToWorld(input.positionOS.xyz));
                output.normalWS   = nrmIn.normalWS;
                output.tangentWS  = float4(nrmIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                return output;
            }

            void DepthNormalsFrag(
                Varyings input
                , out half4 outNormalWS : SV_Target0
                #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
                #endif
            )
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

                float3 normalWS = normalize(input.normalWS);
                UNITY_BRANCH
                if (_NormalMapOn > 0.5)
                {
                    float3 tangentWS   = normalize(input.tangentWS.xyz);
                    float3 bitangentWS = normalize(cross(normalWS, tangentWS) * input.tangentWS.w);
                    float3 nTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                    normalWS = normalize(mul(nTS, float3x3(tangentWS, bitangentWS, normalWS)));
                }

                // A は URP 本体と同じく 0。以前は画面空間輪郭の材質 ID を載せていたが、
                // その Feature ごと廃止した（T-380）。
                outNormalWS = half4(NormalizeNormalPerPixel(normalWS), 0.0);

                #ifdef _WRITE_RENDERING_LAYERS
                    outRenderingLayers = EncodeMeshRenderingLayer();
                #endif
            }

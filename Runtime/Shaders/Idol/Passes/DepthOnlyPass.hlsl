// DepthOnly。
// リムライトとコンタクトシャドウが読む `_CameraDepthTexture` を埋める。**消すと両方が黙って死ぬ。**
//
// **`#pragma` はここに置かないこと。** 素の `#include` の中の pragma は
// Unity が読まず、**キーワードが黙って立たなくなる。**
// pragma は `.shader` 側に残してある。
//
// 切り出しは 1 行も変えていない（include を展開し直してバイト一致を確認・T-211）。

            struct Attributes
            {
                float4 positionOS : POSITION;
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
                // **シングルパスインスタンス ステレオに必須**（DepthNormals と同じ理由）。
                // このパスが埋めるのは _CameraDepthTexture で、
                // リムライトとコンタクトシャドウがそれを読む。
                // 無いと VR で片目だけリムが出ない／接地影がずれる。
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.dissolveGrad = ToonDissolveGradient(
                    input.positionOS.xyz, TransformObjectToWorld(input.positionOS.xyz));
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
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

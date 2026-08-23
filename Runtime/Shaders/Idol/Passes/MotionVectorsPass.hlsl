// 速度バッファ。
// **TAA の前提。** 無いと TAA がアニメーション中のキャラを静止物と見なして尾を引く（T-175）。
//
// **`#pragma` はここに置かないこと。** 素の `#include` の中の pragma は
// Unity が読まず、**キーワードが黙って立たなくなる。**
// pragma は `.shader` 側に残してある。
//
// 切り出しは 1 行も変えていない（include を展開し直してバイト一致を確認・T-211）。

            struct Attributes
            {
                float4 positionOS  : POSITION;
                float2 uv          : TEXCOORD0;
                // **前フレームの頂点位置は TEXCOORD4 に来る。** Unity が
                // SkinnedMeshRenderer の Skinned Motion Vectors から流し込む枠で、
                // 番号は Unity 側の取り決めなので変えられない。
                float3 positionOld : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS                 : SV_POSITION;
                float4 positionCSNoJitter         : TEXCOORD0;
                float4 previousPositionCSNoJitter : TEXCOORD1;
                float2 uv                         : TEXCOORD2;
                float  dissolveGrad               : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings MotionVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.dissolveGrad = ToonDissolveGradient(
                    input.positionOS.xyz, TransformObjectToWorld(input.positionOS.xyz));

                // **ジッタを除いた行列で撮り直すこと。** positionCS には TAA の
                // サブピクセルジッタが載っている。そのまま前フレームと引くと
                // **ジッタそのものが速度として出て、静止物が震える。**
                output.positionCSNoJitter =
                    mul(_NonJitteredViewProjMatrix, mul(UNITY_MATRIX_M, input.positionOS));

                // unity_MotionVectorsParams.x が 1 のときだけ positionOld が有効。
                // スキンしないメッシュには前フレームの頂点が来ないので現在位置で代用し、
                // 動きは行列（UNITY_PREV_MATRIX_M）の差だけで表す。
                float4 prevPos = (unity_MotionVectorsParams.x == 1)
                               ? float4(input.positionOld, 1)
                               : input.positionOS;

                output.previousPositionCSNoJitter =
                    mul(_PrevViewProjMatrix, mul(UNITY_PREV_MATRIX_M, prevPos));

                return output;
            }

            half4 MotionFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                #if defined(_ALPHATEST_ON)
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(a - _Cutoff);
                #endif

                // ディゾルブ。ForwardLit と**同じ式で同じ場所を切る**。
                // 切らないと、**消えた画素が速度を書き続けて TAA が尾を引く。**
                // 絵としては「居ないはずの場所に残像」で、AA の設定を疑うことになる。
                UNITY_BRANCH
                if (_DissolveAmount > 0.0)
                {
                    ToonDissolveClip(input.uv, input.dissolveGrad);
                }

                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(input.positionCS);
                #endif

                // forceNoMotion（unity_MotionVectorsParams.y == 0）の判定は
                // URP 側の関数が持っている。自前で書くとカメラのカット切り替えで
                // 一瞬だけ巨大な速度が出る。
                return half4(CalcNdcMotionVectorFromCsPositions(
                                 input.positionCSNoJitter,
                                 input.previousPositionCSNoJitter), 0, 0);
            }

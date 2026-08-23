// 輪郭（背面法線の押し出し）。
// LightMode は独自タグ `ToonOutline`。`ToonOutlineFeature` が後段で一括描画する。
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
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float  fogFactor    : TEXCOORD1;
                float  dissolveGrad : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings OutlineVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                #if !defined(_OUTLINE_ON)
                    output.positionCS = float4(0, 0, -1e10, 1);
                    return output;
                #endif

                float3 normalOS = input.normalOS;
                UNITY_BRANCH
                if (_UseSmoothNormal > 0.5)
                {
                    float3 bitangentOS = cross(input.normalOS, input.tangentOS.xyz) * input.tangentOS.w;
                    float3x3 tbn = float3x3(normalize(input.tangentOS.xyz),
                                            normalize(bitangentOS),
                                            normalize(input.normalOS));
                    // **頂点カラー由来なので SafeNormalize。** 平滑法線を焼いていないメッシュの
                    // 頂点カラーが中間色だとゼロベクトルになる。
                    normalOS = SafeNormalize(mul(input.color.rgb * 2.0 - 1.0, tbn));
                }

                float widthMask = (_UseVertexWidth > 0.5) ? input.color.a : 1.0;

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(normalOS);

                float4 positionCS = TransformWorldToHClip(positionWS);
                float3 normalCS   = TransformWorldToHClipDir(normalWS);

                float dist = distance(GetCameraPositionWS(), positionWS);
                float fade = 1.0 - saturate(dist / max(_OutlineMaxDistance, 0.001));

                float2 aspect = float2(_ScaledScreenParams.y / max(_ScaledScreenParams.x, 1.0), 1.0);
                // **ゼロ除けはスカラー加算ではなく長さで判定する。**
                // 素で `normalize(normalCS.xy + 1e-5)` としていたが、
                // これは両成分に同じ値を足すので**常に 45 度方向へ寄る**。
                // 寄り幅は xy が短いほど大きく、実測で:
                //   |xy| = 1e-4 → 5.2 度 / 3e-5 → 14 度 / 1e-5 → 26.6 度
                // **xy が 0 に近づく場所とはシルエットそのもの**で、
                // 輪郭が一番要る所で押し出し方向が崩れていた。
                // 長さで判定すれば、潰れた点だけを既定方向に倒せる。
                float2 nxy = normalCS.xy;
                float  nlen = length(nxy);
                float2 ndir = (nlen > 1e-5) ? (nxy / nlen) : float2(0.0, 1.0);

                float2 extend = ndir
                              * _OutlineWidth * 0.002 * widthMask * fade * aspect;

                positionCS.xy += extend * positionCS.w;
                // **Z の向きはプラットフォームで逆になる。**
                // Reversed-Z（D3D / Vulkan / Metal）は近 = 1 / 遠 = 0 なので、
                // 奥へ逃がすには **引く**。素で足していたので D3D では逆に手前へ出て、
                // Z Offset を上げるほど輪郭が本体を突き抜けるようになっていた
                // （既定 0 なので誰も踏んでいないが、上げた瞬間に壊れる）。
                // Cull Front で背面を描くパスなので、奥へ逃がすのが本来の意図。
                float zPush = _OutlineZOffset * 0.001 * positionCS.w;
                #if UNITY_REVERSED_Z
                    positionCS.z -= zPush;
                #else
                    positionCS.z += zPush;
                #endif

                output.positionCS = positionCS;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor  = ComputeFogFactor(positionCS.z);
                // **押し出す前の位置で取ること。** 輪郭は法線方向へずらして描くので、
                // ずらした後の座標で切ると本体と縁がわずかに違う場所で消える。
                output.dissolveGrad = ToonDissolveGradient(
                    input.positionOS.xyz, TransformObjectToWorld(input.positionOS.xyz));
                return output;
            }

            half4 OutlineFrag(Varyings input) : SV_Target
            {
                float4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                #if defined(_ALPHATEST_ON)
                    clip(baseTex.a * _BaseColor.a - _Cutoff);
                #endif

                // ディゾルブ。ForwardLit と**同じ式で同じ場所を切る**。
                // 切らないと、**消えた本体の輪郭だけが宙に残る。**
                UNITY_BRANCH
                if (_DissolveAmount > 0.0)
                {
                    ToonDissolveClip(input.uv, input.dissolveGrad);
                }

                float3 tinted = baseTex.rgb * _BaseColor.rgb * (1.0 - _OutlineAlbedoDarken);
                float3 col    = lerp(_OutlineColor.rgb, tinted, _OutlineAlbedoBlend);

                // 暗転は輪郭にも掛ける（T-361）。掛けないと**暗転しきったキャラの
                // 輪郭線だけが明るく残って宙に浮く**（ディゾルブで輪郭も切るのと同じ理屈）。
                col = lerp(col, float3(0.0, 0.0, 0.0), _BlackOut);

                col = MixFog(col, input.fogFactor);
                return half4(col, 1);
            }

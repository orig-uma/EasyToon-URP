// ForwardLit の本体（頂点・フラグメント）。
//
// **`.shader` から切り出した理由。** 前髪透過（HairSeeThrough）は
// ForwardLit と**まったく同じライティング**で、アルファだけ差し替えたものを
// 重ね描きする。同じ 488 行を 2 か所に持つわけにいかないので、
// define 違いで 2 回 include できる形にした（EasyToon の Idol と同じ作り）。
//
// **`#pragma` はここに置かないこと。** 素の `#include` の中の pragma は
// Unity が読まず、**キーワードが黙って立たなくなる。**
// バリアントが消えても絵は出るので、実機で「なぜか効かない」としか見えない。
// pragma は `.shader` 側に残してある（`#include_with_pragmas` も使わない）。
//
// 切り出し時（T-210）は 1 行も変えず、バイト一致を確認して分けた。
// その後 T-386 でフラグメントを 4 段の関数に分けた（fxc の命令数・
// 一時レジスタの完全一致で意味不変を確認済み）。

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
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float4 tangentWS  : TEXCOORD3;
                float  fogFactor  : TEXCOORD4;
                // ディゾルブの進み具合。**頂点で求めて 1 float で運ぶ** ──
                // 位置の一次式なので線形補間で厳密に一致する。
                float  dissolveGrad : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ToonVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs   nrmIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.normalWS   = nrmIn.normalWS;
                output.tangentWS  = float4(nrmIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor  = ComputeFogFactor(posIn.positionCS.z);
                output.dissolveGrad = ToonDissolveGradient(input.positionOS.xyz,
                                                           posIn.positionWS);
                return output;
            }

            // =================================================================
            //  フラグメントの段構成（T-386）。Doll の ForwardPass に倣い
            //  「サーフェス収集 → コンテキスト → ライト → 環境と後処理」の
            //  4 段に分け、ToonFrag は流れだけを書く。**切り出しは意味を変えて
            //  いない** ── fxc の命令数・一時レジスタの完全一致で確認済み。
            //  各段の中身と置き順の理由は、段の中のコメントがそのまま持っている。
            // =================================================================

            // ---- 1. サーフェス収集 ----------------------------------------
            // テクスチャ群 → ToonSurface。法線（TBN・幾何法線）と SpecAA
            // カーネルはコンテキスト側も使うので out で返す。
            // アルファテスト（clip）とディゾルブもここ ── 消える画素の
            // ライティングを計算しないため、できるだけ早い段に置く。
            ToonSurface ToonGatherSurface(Varyings input, float2 uv,
                                          out float3 normalWS, out float3 baseNormalWS,
                                          out float3 tangentWS,
                                          out float3 bitangentWS, out float3 geomNormalWS,
                                          out float specAAKernel)
            {
                // ---- サーフェス ------------------------------------------------
                float4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
                float4 albedo  = baseTex * _BaseColor;

                // **アルファテストより前に掛ける。** HSV は alpha を触らないので
                // 切り抜きの結果は変わらないが、順序を後にすると
                // 「切り抜かれた画素だけ補正前の色」という食い違いが生まれうる。
                albedo.rgb = ToonAlbedoHSV(albedo.rgb);

                // NPR Map（T-419 で R / G / B の 3 チャンネルに縮小）。
                // 中立値。R = 鏡面フル / G = 0.5（オフセット 0）/ B = ディテールをフルに掛ける。
                // **白テクスチャでは G が 1 になり、影が最大まで遅れて出なくなる。**
                // 仕様（REQUIREMENTS §6「G は 0.5 が基準」）と食い違うのでトグルで切る。
                // ディテールより前に読む ── B（Detail Mask）をディテールの合成率に掛けるため。
                float4 npr = float4(1.0, 0.5, 1.0, 0.0);
                UNITY_BRANCH
                if (_NPRMapOn > 0.5) npr = SAMPLE_TEXTURE2D(_NPRMap, sampler_NPRMap, uv);
                float detailMask = npr.b;

                // ディテールマップ（T-368）: A の合成率でベースへ重ねる。
                // HSV 補正の**後**に置く ── ディテール（チークの赤等）は
                // 「その色で置く」意図なので、全体の色調補正に巻き込まない。
                UNITY_BRANCH
                if (_DetailOn > 0.5)
                {
                    float2 detailUV = uv * _DetailMap_ST.xy + _DetailMap_ST.zw;
                    float4 detail = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, detailUV)
                                  * _DetailColor;
                    // 置き換え（タトゥー等）か乗算（生地の陰・AO。T-404）か。
                    // 乗算は「元の色を暗くする」ので、灰のテクスチャをどのアルベドにも掛けられる。
                    // 強さは detail.a（テクスチャの A × Detail Color の A）× Detail Mask（NPR Map の B。T-419）
                    //（レースの部分だけ織りを入れる、など場所で効き方を変える）。
                    float3 detailTarget = (_DetailMultiply > 0.5) ? albedo.rgb * detail.rgb : detail.rgb;
                    albedo.rgb = lerp(albedo.rgb, detailTarget, detail.a * detailMask);
                }

                #if defined(_ALPHATEST_ON)
                    clip(albedo.a - _Cutoff);
                #endif

                // ディゾルブ。**アルファテストの直後**に置く ── 消える画素の
                // ライティングを計算しても捨てるだけなので、早いほどよい。
                // 縁の発光は下のエミッシブに足す（ここでは受け取るだけ）。
                float3 dissolveEmission = 0;
                UNITY_BRANCH
                if (_DissolveAmount > 0.0)
                {
                    ToonDissolve(uv, input.dissolveGrad, albedo.rgb, dissolveEmission);
                }

                float4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);
                // Fabric Map（T-419）: 反射率 / sheen / クリアコート / イリデッセンスの場所ごとの倍率。白が中立
                float4 fabric = 1.0;
            #if defined(_FABRICMAP_ON)
                fabric = SAMPLE_TEXTURE2D(_FabricMap, sampler_FabricMap, uv);
            #endif
                // Geometry Map（T-422）: R Cavity / G Curvature / B AO。ON の間は個別の 2 枚を読まない
                float4 geometryMap = float4(1.0, 0.5, 1.0, 1.0);
            #if defined(_GEOMETRYMAP_ON)
                geometryMap = SAMPLE_TEXTURE2D(_GeometryMap, sampler_GeometryMap, uv);
            #endif

                // 窪みの微細遮蔽。**アルベドと鏡面の両方に掛ける。**
                // EasyPBR はアルベドだけに掛けているが、それだと縫い目や皺の底に
                // 鏡面がそのまま残って、暗くしたはずの場所が逆に目立つ。
                // 供給源は Geometry Map の R だけ（個別の Cavity Map は T-423 で廃止）。
                // Geometry Map が無ければコードごと消える。
                float cavity = 1.0;
            #if defined(_GEOMETRYMAP_ON)
                UNITY_BRANCH
                if (_CavityStrength > 0.0)
                {
                    cavity = lerp(1.0, geometryMap.r, _CavityStrength);
                    albedo.rgb *= cavity;
                }
            #endif

                ToonSurface s;
                s.cavity     = cavity;
                // アルベドの明るさ上限（T-421）。白い衣装が 1 灯で飛ぶのを材質側で抑える。
                // 最大成分で縮めるので色相・彩度は変わらない。1 = OFF
                UNITY_BRANCH
                if (_AlbedoBrightnessLimit < 1.0)
                {
                    float amax = max(albedo.r, max(albedo.g, albedo.b));
                    albedo.rgb *= min(1.0, _AlbedoBrightnessLimit / max(amax, 1e-4));
                }
                s.albedo     = albedo.rgb;
                s.alpha      = albedo.a;
                s.thickness  = mask.b;
                // **saturate すること。** Range は 0..1 だが、他シェーダーから移植した
                // マテリアルには範囲外の値がシリアライズされて残る（実際 5 件が 2 だった）。
                // Range 属性はインスペクタのスライダを縛るだけで、実行時の値は縛らない。
                // 強度 2 だと lerp が外挿になり `2*ao - 1`、AO 0.5 未満で**遮蔽が負**になる。
                // 負の遮蔽は多重バウンス補正・マイクロシャドウ・鏡面遮蔽の全部を狂わせる。
                // 遮蔽の出どころ（T-422）。Geometry Map が無ければ従来どおり Mask の G
                float aoSource = mask.g;
            #if defined(_GEOMETRYMAP_ON)
                aoSource = (_OcclusionSource < 0.5) ? mask.g
                         : (_OcclusionSource < 1.5) ? geometryMap.b
                                                    : mask.g * geometryMap.b;
            #endif
                s.occlusion  = saturate(lerp(1.0, aoSource, _OcclusionStrength));
                s.geomCurvature = geometryMap.g;
                s.specMask   = npr.r;
                s.shadowOffset = (npr.g - 0.5) * 2.0;
                s.sheenMask  = fabric.g;
                s.coatMask   = fabric.b;
                s.iridMask   = fabric.a;
                // npr.b は Detail Mask（上で使用済み）。npr.a は未使用（T-419 で RampIndex を廃止）

                float metallic   = mask.r * _Metallic;
                // A を Roughness として書き出したマップ（InstaMat の標準）はここで反転（T-419）
                // _MaskInvertA = トグル × 割り当て済み（T-435）。マップ無しで反転すると白 → 0 で全面マットになる
                float maskSmooth = (_MaskInvertA > 0.5) ? 1.0 - mask.a : mask.a;
                float smoothness = maskSmooth * _Smoothness;

                s.emission = dissolveEmission;
                UNITY_BRANCH
                if (_EmissionOn > 0.5)
                {
                    s.emission += SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv).rgb
                                * _EmissionColor.rgb;
                }

                // ---- 法線 -----------------------------------------------------
                normalWS    = normalize(input.normalWS);
                tangentWS   = normalize(input.tangentWS.xyz);
                bitangentWS = normalize(cross(normalWS, tangentWS) * input.tangentWS.w);

                // 法線マップを掛ける前の幾何法線。ベイクしたマップは
                // この向きの接線空間で焼かれているので、戻すときもこれを使う。
                geomNormalWS = normalWS;

                // ベースとディテールの法線は**接空間で合成してから 1 回だけ回す**。
                // TBN 回転を 2 回重ねると合成にならない（回転の連結は加算と違う）。
                // ディテール法線は**鋭いローブには入れない**（T-401）。
                // 織り目のような高周波の起伏が GGX の鏡面や環境反射を通ると、起伏 1 つ
                // ごとに点が立って網点印刷になる。しかも三角形ごとに UV 密度（＝ミップ）が
                // 違うので「点が立つ三角形」と「均されて平らな三角形」が隣り合う。
                // 一方、柔らかい拡散・sheen・リムを通ると、半影にだけ生地の目が浮く
                //（参考にした実機の布の見え方）。そこで法線を 2 本持つ:
                //   normalWS     = ベース ＋ ディテール → 拡散・sheen・リム・環境光（c.N）
                //   baseNormalWS = ベースのみ           → GGX・環境反射・MatCap・グリッター（c.specN）
                float3x3 tbn = float3x3(tangentWS, bitangentWS, normalWS);
                float3 normalTS = float3(0.0, 0.0, 1.0);
                baseNormalWS = normalWS;
                UNITY_BRANCH
                if (_NormalMapOn > 0.5)
                {
                    normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
                    baseNormalWS = normalize(mul(normalTS, tbn));
                    // Normal Cavity（T-434）。Specular / Sheen Normal Flatten で法線を均すと、細かい凹凸は
                    // ハイライトから**見えなくなる**。向きは均したまま、傾き（= 凹凸の斜面）で量だけ落として返す:
                    // ハイライトを割らずに、凹凸の陰だけがハイライトの中に残る。既存の cavity の経路に畳むので
                    // 鏡面・sheen・クリアコート・環境反射に一括で効き、アルベドは動かない（掛けた後なので）。
                    // 高さではなく傾きなので、落ちるのは谷底ではなく斜面。
                    s.cavity *= saturate(1.0 - _NormalCavity * length(normalTS.xy));
                }
                UNITY_BRANCH
                if (_DetailOn > 0.5)
                {
                    // 強さに Detail Mask（NPR Map の B）も掛ける（T-419）
                    float2 dBase = uv;
                    // 回転（T-431）。UV を R で回すと模様は面の上で逆向きに回るので、
                    // 引いた法線の xy は Rᵀ で面の軸へ戻す。**戻さないと模様だけ回って陰影の向きが回らない。**
                    float2 dRot; // x = sin, y = cos
                    sincos(_DetailNormalRotation * (PI / 180.0), dRot.x, dRot.y);
                    dBase = float2(dBase.x * dRot.y - dBase.y * dRot.x,
                                   dBase.x * dRot.x + dBase.y * dRot.y);
                    float3 dTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap,
                                         dBase * _DetailNormalMap_ST.xy + _DetailNormalMap_ST.zw),
                        _DetailNormalScale * detailMask);
                    dTS.xy = float2(dTS.x * dRot.y + dTS.y * dRot.x,
                                   -dTS.x * dRot.x + dTS.y * dRot.y);
                    // Detail Cavity（T-434）。ディテール法線は鏡面に入れない契約（T-401）なので、織り目は
                    // 鏡面から**完全に見えない** ── 平らな面のように均一に光る。法線の傾き（= 目の斜面）で
                    // cavity を落として、鏡面・sheen・クリアコート・環境反射にだけ織り目の陰を返す。
                    // 向きを変えずに量だけ落とすので点は立たない。アルベドには掛からない（掛けた後なので）。
                    // 高さではなく傾きなので、落ちるのは谷底ではなく斜面 ── 周期の細かい織り目では区別が付かない。
                    s.cavity *= saturate(1.0 - _DetailCavity * length(dTS.xy));
                    // whiteout ブレンド（xy 加算・z 乗算。Doll と同じ）
                    normalTS = normalize(float3(normalTS.xy + dTS.xy, normalTS.z * dTS.z));
                    normalWS = normalize(mul(normalTS, tbn));
                }
                else
                {
                    normalWS = baseNormalWS;
                }

                // --- リム用の法線（T-432）-----------------------------------
                // リムは N·V が 0 に近い所で急に立つので、細かい凹凸の斜面 1 つ 1 つに縁が出る。
                // Flatten でノーマルマップの傾きをメッシュの法線へ寄せる: 傾きが一律に縮むので、
                // **浅い凹凸から先に縁の閾値を割って消え、傾きの大きい深いしわは残る。**
                // ミップで均す案（Rim Normal Blur）は不採用 ── ミップの段はメッシュの UV 密度と面の向きで
                // 変わるので、距離を変えるとメッシュごとに縁の出方が食い違う（利用者判断）。
                // ディテール法線は量で別に絞る。既定（Flatten 0 / Detail 1）は従来と同じ法線。
                float3 rimBaseWS = lerp(baseNormalWS, geomNormalWS, _RimNormalFlatten);
                s.rimN = normalize(rimBaseWS + (normalWS - baseNormalWS) * _RimDetailNormal);
                // 鏡面と sheen も同じ考え方（T-434）。既定（0 / 0 / 1）は従来と同じ法線
                s.specFlatN = normalize(lerp(baseNormalWS, geomNormalWS, _SpecularNormalFlatten));
                s.sheenN    = normalize(lerp(baseNormalWS, geomNormalWS, _SheenNormalFlatten)
                                      + (normalWS - baseNormalWS) * _SheenDetailNormal);

                #if defined(_DBUFFER)
                    // URP のデカール（汚れ・傷・タトゥー）を受ける。
                    // **法線が確定した後・f0 と粗さを導出する前**に掛ける。
                    // 前に置くと法線マップがデカールの法線を上書きし、
                    // 後に置くと金属度と粗さがデカール前の値のまま残って質感が割れる。
                    half3 decalSpecular = 0;
                    ApplyDecal(input.positionCS,
                               s.albedo, decalSpecular, normalWS,
                               metallic, s.occlusion, smoothness);
                #endif

                // --- シアー生地（ストッキング）------------------------------
                // **拡散色を作る前に乗せる。** 影色は拡散色から作るので、
                // ここで乗せておけば 1影・落ち影にも布の色が自動で乗る。
                // デカール（DBuffer）より後なのは、布は服なのでデカールの上に来るため。
                //
                // 視線方向をここでもう一度求めているのは、`c.V` の確定が
                // 法線の後（コンテキストの組み立て）だから。**既定 OFF の分岐の中**なので
                // 使わないマテリアルでは 1 命令も走らない。
            #if defined(_STOCKING_ON)
                UNITY_BRANCH
                if (_StockingIntensity > 0.0)
                {
                    float3 viewWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                    ToonStockingLayer(uv, saturate(dot(normalWS, viewWS)), s.albedo);
                }
            #endif

                // 金属の拡散は物理では 0。Metal Diffuse Retain で一部残す（T-420。0 = 従来）
                s.diffuseColor = s.albedo * (1.0 - metallic * (1.0 - _MetalDiffuseRetain));
                s.metallic     = metallic;
                // 非金属の f0 = 0.16 × Reflectance²（0.5 で 0.04 = 従来）× Fabric Map の R（T-419）
                float f0d = 0.16 * _Reflectance * _Reflectance * fabric.r;
                s.f0           = lerp(f0d, s.albedo, metallic);
                // 金属部だけスペキュラの倍率を上書き（T-383）。f0 と同じく
                // **デカール適用後の metallic** から作ること。ライトに依存しないので
                // ここで 1 回だけ。metallic 0 なら 1 = 従来と完全一致。
                s.metalSpecBoost = lerp(1.0, _MetalSpecularBoost, metallic);
                s.metalEnvBoost  = lerp(1.0, _MetalEnvBoost, metallic);
                s.perceptualRoughness = 1.0 - smoothness;

                // 影色はライトに依存しないので1回だけ求める。**diffuseColor の確定後**に置くこと
                // （デカールが albedo と metallic を書き換えるため、その前だと値が違う）。
                s.shadowColor = ToonShadowAlbedo(s.diffuseColor);

                // ジオメトリックな法線変化から粗さを補正 (スペキュラのちらつき対策)
                //
                // **カーネルはここで1回だけ求める。** 微分を使うので光源ループの中では
                // 取れない（Forward+ は反復回数が実行時に決まる）。
                // ToonContext はまだ宣言されていないのでローカルに受け、後で載せる。
                specAAKernel = ToonSpecAAKernel(normalWS);

                s.roughness = ToonApplyRoughnessKernel(
                                  s.perceptualRoughness * s.perceptualRoughness, specAAKernel);
                s.roughness = max(s.roughness, 0.002);

                // フィルタ後の粗さを perceptual 側にも戻す。戻さないと
                // ToonRoughnessToMip が AA 前の粗さで mip を引き、
                // **スペキュラ AA が環境反射に効かない**（金具のちらつきが残る）。
                s.perceptualRoughness = sqrt(s.roughness);

                return s;
            }

            // ---- 2. コンテキスト（ライト非依存の前計算）--------------------
            // ToonShadeLight はライトの数だけ呼ばれるので、光源に依らない量は
            // すべてここで 1 回だけ求める（Forward+ で灯数ぶんの再計算が消える）。
            // 微分（ddx/ddy/fwidth）もここ ── 光源ループの中は Forward+ だと
            // 反復回数が実行時に決まるので、微分が保証されない。
            // SSS マップが s.thickness を、SSAO が s.occlusion を書き換えるので
            // ToonSurface は inout。
            ToonContext ToonBuildContext(Varyings input, float2 uv, inout ToonSurface s,
                                         float3 normalWS, float3 baseNormalWS,
                                         float3 tangentWS,
                                         float3 bitangentWS, float3 geomNormalWS,
                                         float specAAKernel)
            {
                // ---- コンテキスト ---------------------------------------------
                ToonContext c;
                c.positionWS = input.positionWS;
                c.N          = normalWS;
                c.rimN       = s.rimN;
                c.sheenN     = s.sheenN;
                c.specN      = s.specFlatN;   // 鋭いローブ用。ディテール法線を含まない（T-401）
                c.V          = normalize(GetWorldSpaceViewDir(input.positionWS));
                c.T          = tangentWS;
                c.B          = bitangentWS;
                // **下駄を足さずに下限で挟むこと。** 以前は `saturate(...) + 1e-4` で、
                // 値域が **[1e-4, 1.0001]** になっていた。ゼロ除算は避けられるが、
                // **1 を超えるぶん `1.0 - NdotV` が負になる。**
                // HLSL は負の底の pow を未定義とする（fxc は exp2(y*log2(負)) を
                // 計算するので NaN）。実際クリアコートの間接フレネルが
                // `pow(1.0 - c.NdotV, 5.0)` を計算しており、
                // **カメラを真正面から向いた面で NaN が出る**条件が揃っていた。
                // 球体である眼球には必ずその点があり、コートは目で有効になっている。
                //
                // max で挟めば値域は [1e-4, 1.0] に収まり、
                // ゼロ除算を避けたまま `1 - NdotV` が負にならない。
                c.NdotV      = max(saturate(dot(c.N, c.V)), 1e-4);
                c.specAAKernel = specAAKernel;   // 全鏡面ローブで共有（シーン・髪も含む）
                c.specGrain    = 1.0;            // Glitter Specular が決める（ToonShadeLights）
                c.rimGrain     = 1.0;            // Glitter Rim が決める（同上）

                // 陰ランプ専用の平滑法線。TBN の3行目は法線マップを掛ける前の
                // 幾何法線（ベイクがその空間で焼かれているため。T-024 と同じ理由）。
                c.shadeN = normalWS;
                UNITY_BRANCH
                if (_ShadeNormalStrength > 0.0)
                {
                    float3 sTS = UnpackNormal(
                        SAMPLE_TEXTURE2D(_ShadeNormalMap, sampler_ShadeNormalMap, uv));
                    float3 shadeWS = normalize(mul(sTS,
                                        float3x3(tangentWS, bitangentWS, geomNormalWS)));
                    c.shadeN = normalize(lerp(normalWS, shadeWS, _ShadeNormalStrength));
                }

                // 透過を曲げる方向。ベイクした SSS があればそちらを使う。
                // A に焼かれた厚みは MaskMap の B を置き換える。
                c.sssDir = normalWS;

                // **消費側も条件に入れる。** SSS マップが供給するのは c.sssDir と
                // s.thickness の2つだけで、どちらも皮下散乱と透過でしか読まれない。
                // 両方 0 ならフェッチ + normalize + 行列積が丸ごと無駄になる。
                // 散乱を既定 OFF にしたので**この状態が既定**であり、
                // マテリアル側で _SSSMapStrength を 0 にして回る運用に頼らない。
                float scatterOn = _SubsurfaceStrength + _TransmissionStrength;

                UNITY_BRANCH
                if (_SSSMapStrength > 0.0 && scatterOn > 0.0)
                {
                    float4 sss = SAMPLE_TEXTURE2D(_SSSMap, sampler_SSSMap, uv);
                    // **テクスチャ由来なので SafeNormalize。** 未使用領域が中間色 (0.5,0.5,0.5) で
                    // 塗られていると 2x-1 がゼロベクトルになり、素の normalize は NaN を返す。
                    float3 dirTS = SafeNormalize(sss.rgb * 2.0 - 1.0);
                    float3 dirWS = normalize(mul(dirTS,
                                       float3x3(tangentWS, bitangentWS, geomNormalWS)));

                    c.sssDir    = normalize(lerp(normalWS, dirWS, _SSSMapStrength));
                    s.thickness = lerp(s.thickness, sss.a, _SSSMapStrength);
                }

                c.bentN = normalWS;
                UNITY_BRANCH
                if (_BentNormalOn > 0.5)
                    // 接線空間で焼いたベントノーマルをワールドへ戻す。
                {
                    // TBN の3行目は **法線マップを掛ける前** の幾何法線を使う。
                    // 掛けた後の値を使うと、ベイク済みの向きに法線マップが二重に乗る。
                    float3 bentTS = UnpackNormal(
                        SAMPLE_TEXTURE2D(_BentNormalMap, sampler_BentNormalMap, uv));
                    c.bentN = normalize(mul(bentTS,
                                            float3x3(tangentWS, bitangentWS, geomNormalWS)));
                }
                // **曲率は焼いた Curvature Map だけから取る（T-381）。** 以前は法線の
                // 画面微分で推定していたが、補間法線の微分は三角形の中で一定・辺で
                // 不連続なので、境界幅に入れると低ポリで陰に面が並んだ（T-339）。
                // ベイカーが DCC 不要で焼けるようになった今、推定経路は
                // 「焼かずに Influence を上げると面が出る」罠でしかないので撤去した。
                //
                // ベイク値は 0.5=平坦 / >0.5=凸 / <0.5=凹 の符号付き。境界幅に要るのは
                // 曲がりの**大きさ**だけ（凹凸どちらでも散乱の帯は広がる）なので絶対値を取る。
                // 大きさの校正はベイカー側の Intensity が持つ。無次元 0..1（0=平坦）。
                // 既定テクスチャ（gray）は 0 になるので、未割り当てなら何も起きない。
                // 供給源は Geometry Map の G だけ（個別の Curvature Map は T-423 で廃止）
                c.curvature = 0.0;
            #if defined(_GEOMETRYMAP_ON)
                UNITY_BRANCH
                if (_CurvatureSoftness > 0.0)
                    c.curvature = abs(s.geomCurvature * 2.0 - 1.0);
            #endif
                c.uv         = uv;
                c.screenUV   = GetNormalizedScreenSpaceUV(input.positionCS);
                c.positionSS = input.positionCS.xy;

                // 影フィルタの回転角。**ブルーノイズを画面座標で引く（T-390）。**
                // 以前は IGN（手続きノイズ）だったが、IGN は対角の格子構造を持つので
                // Penumbra を上げて半径が大きくなると、回転角の構造がそのまま
                // **縞**として見えた（利用者報告「粒は仕方ないが縞状なのが気になる」）。
                // ブルーノイズは高周波だけの等方な粒で、同じタップ数でも縞にならない。
                // 256 タイル・点サンプル（補間すると値が鈍って回転が偏る）・LOD 0。
                // 既定テクスチャは .shader.meta で包内の 256² を指す。
                // 解像度はテクスチャ側（TexelSize）で決まる（T-413）。粒は常に 1 画素で、大きくしても周期が伸びるだけ。
                c.dither     = SAMPLE_TEXTURE2D_LOD(_BlueNoiseTex, sampler_PointRepeat,
                                                    input.positionCS.xy * _BlueNoiseTex_TexelSize.xy, 0).r;

                // UV の画面微分。**ここで取ること。** 光源ループの中は Forward+ だと
                // 反復回数が実行時に決まるので暗黙 LOD が使えない。ミップを捨てずに
                // 済ませるため、ループ内のサンプルにはこれを渡して _GRAD で引く。
                c.uvDx       = ddx(uv);
                c.uvDy       = ddy(uv);

                // 顔 SDF の境界 AA。同じ理由でループ内では fwidth を呼べない。
                // Face 以外はフェッチごと発生しない。
                c.faceSdfAA   = 0.0;
                c.faceSdfVAA  = 0.0;
                c.faceSdf     = 0.0;
                c.faceSdfV    = 1.0;
                c.faceSdfMask = 1.0;
                c.faceTone    = 0.0;
                #if defined(_SURFACETYPE_FACE)
                    // 16bit 1ch（R×256+G）をデコードしてから変化率を取る。
                    // 上位バイトだけの fwidth だと 256 段の飛びを拾って AA が過大になる。
                    // BA は縦スイープ（T-441）。同じ 1 フェッチから取れるので追加コスト無し。
                    float4 faceSdfPx = SAMPLE_TEXTURE2D(_FaceSDFMap, sampler_FaceSDFMap, uv);
                    c.faceSdf    = ToonDecodeFaceSdf16(faceSdfPx.rg);
                    c.faceSdfAA  = fwidth(c.faceSdf);
                    c.faceSdfV   = ToonDecodeFaceSdf16(faceSdfPx.ba);
                    c.faceSdfVAA = fwidth(c.faceSdfV);

                    // **下向きの面は SDF から法線の陰影へ戻す（T-376・Doll と同じ仕組み）。**
                    // SDF のスイープは水平面内で回すので光の仰角を知らない。顎の裏は法線が
                    // ほぼ真下で、わずかな前向き成分だけで「ほぼ全方位で照らされる」と焼かれる。
                    // 隣接する首（Default）は N·L で上からの光に正しく陰るため、下から覗くと
                    // 「顎裏＝明・首＝陰」の段差が出ていた（利用者報告）。
                    // **判定は頭の up 軸で取る（T-440）。** 以前はオブジェクト空間の法線 Y
                    // だったが、スキンメッシュのオブジェクト空間はルート（素体の直立）なので
                    // 頭の回転に追従しない。俯くと顎裏の法線が前を向いて Y が上がり、
                    // マスクが「SDF 100%」へ振れる ── 俯いたときほど顎裏〜首の繋ぎが悪化する
                    // 向きに働いていた。頭 up との内積なら、頭がどこを向いても「顎の裏」は
                    // 顎の裏のまま。Binder が無いときはオブジェクトの Y に落ちるので従来と同じ。
                    // ライトに依存しないのでここで 1 回（光源ループでは灯数ぶん掛かる）。
                    // 既定 Min -1 / Max 0 で真下 → 0、水平以上 → 1。Min==Max は 0 除算なので離す。
                    {
                        float3 headFwd, headRight, headUp;
                        float  headValid;
                        ToonFaceHeadBasis(headFwd, headRight, headUp, headValid);
                        c.faceSdfMask = smoothstep(_FaceSDFBlendNormalMin,
                                                   max(_FaceSDFBlendNormalMax, _FaceSDFBlendNormalMin + 1e-4),
                                                   dot(headUp, c.N));
                    }

                    // 顔の影トーンの網点しきい値（T-440）。画面固定・ライト非依存なのでここで 1 回。
                    c.faceTone = ToonFaceTonePattern(input.positionCS.xy, c.dither);
                #endif

                // ---- 光源に依存しない前計算 -----------------------------------
                // ToonShadeLight はライトの数だけ呼ばれる。ここで1回求めておくと
                // Forward+ で灯数ぶんの再計算が消える（前髪の影と同じ考え方）。
                c.hairT1 = c.T;
                c.hairT2 = c.T;
                c.hairSparkle = 1.0;
                c.hairExp     = float2(1.0, 1.0);
                #if defined(_SURFACETYPE_HAIR)
                    // 毛流れマップとシフトマップのフェッチ2枚 + atan2/sincos がここ1回に畳まれる。
                    float3 strandDir = ToonHairStrandDir(c.T, c.B, uv, c.uvDx, c.uvDy);
                    float  shiftNoise = SAMPLE_TEXTURE2D(_HairShiftMap, sampler_HairShiftMap, uv).r - 0.5;
                    c.hairT1 = ToonShiftTangent(strandDir, c.N, _HairShift1 + shiftNoise * 0.3);
                    c.hairT2 = ToonShiftTangent(strandDir, c.N, _HairShift2 + shiftNoise * 0.3);

                    // **毛束の粒。** 副バンドを UV に沿った3オクターブのサインで割る。
                    // これが無いと2本目が滑らかな帯のままで、アニメ髪の「束感」が出ない。
                    // 参照実装（EasyPBR）が sparkle として持っている項。
                    float sc = uv.x * _HairStrandScale;
                    float sn = sin(sc) + sin(sc * 2.34) * 0.5 + sin(sc * 3.71) * 0.25;
                    float sparkle = saturate(sn * 0.5 + 0.5);

                    // **画面上で周期が1ピクセルを切ったら効かせない。** 高周波のサインは
                    // 髪が小さく映った瞬間にモアレになる。参照実装には無いが、
                    // ここまで境界 AA を入れてきたのと同じ理由で入れる。
                    float sparkleAA = saturate(1.0 - fwidth(sc) * 0.5);
                    c.hairSparkle = lerp(1.0, sparkle, _HairStrandSparkle * sparkleAA);

                    // Kajiya の指数もライトに依存しない。ここで畳む。
                    c.hairExp = float2(
                        ToonFilterBlinnExponent(exp2(10.0 * _HairSmoothness1 + 1.0), c.specAAKernel),
                        ToonFilterBlinnExponent(exp2(10.0 * _HairSmoothness2 + 1.0), c.specAAKernel));
                #endif

                c.energyComp = ToonEnergyCompensation(s.f0, s.perceptualRoughness, c.NdotV);

                c.sheenScale = 1.0;
                c.sheenAlpha = 0.0;
                c.clothT     = c.T;
                c.clothAniso = 0.0;
                #if defined(_SURFACETYPE_CLOTH)
                {
                    // 織りの向き（T-419）。既定はメッシュの接線（Tangent Swap で従接線）。
                    // Anisotropy Map があれば RG の向きを接線空間で回し、B を強さに掛ける。
                    // 光源に依存しないのでここで 1 回だけ。
                    c.clothT     = (_ClothTangentSwap > 0.5) ? c.B : c.T;
                    c.clothAniso = _ClothAnisotropy;
                #if defined(_ANISOMAP_ON)
                    float3 am = SAMPLE_TEXTURE2D(_AnisotropyMap, sampler_AnisotropyMap, uv).rgb;
                    float2 ad = am.rg * 2.0 - 1.0;
                    float  al = length(ad);
                    // 向きが潰れている（0.5, 0.5）画素は接線のまま
                    if (al > 1e-3) c.clothT = normalize((c.T * ad.x + c.B * ad.y) / al);
                    c.clothAniso = _ClothAnisotropy * am.b;
                #endif
                    // **ここも光源に依存しない。** 以前はエネルギー保存の計算と
                    // ライトループの D 項とで**二重に**求めていた。1回に畳む。
                    c.sheenAlpha = ToonApplyRoughnessKernel(max(_SheenRoughness, 0.02), c.specAAKernel);

                    float3 sc = _SheenColor.rgb * _SheenIntensity * s.sheenMask
                              * lerp(1.0, s.albedo, s.metallic * _SheenMetalTint);
                    c.sheenScale = saturate(1.0
                        - ToonSheenAlbedo(c.NdotV, c.sheenAlpha)
                        * max(max(sc.r, sc.g), sc.b) * _SheenEnergyConservation);
                }
                #endif

                #if defined(_SCREEN_SPACE_OCCLUSION)
                    // URP の SSAO を遮蔽に畳む。DepthNormals パスを残しているのは
                    // これを成立させるため（REQUIREMENTS の NFR / パス構成の前提）。
                    //
                    // 取るのは indirect 側だけ。direct への効かせ方は _DirectOcclusion が
                    // 握っているので、URP 側の direct 係数まで掛けると二重になる。
                    AmbientOcclusionFactor ssao = GetScreenSpaceAmbientOcclusion(c.screenUV);
                    s.occlusion = min(s.occlusion, ssao.indirectAmbientOcclusion);
                #endif


                return c;
            }

            // ---- 追加光 1 灯ぶん（T-438 で関数に切り出し。Forward+ の Directional 用ループと共用）----
            // 影は自前の硬い 1 タップ（T-418）。URP の GetAdditionalLight(…, shadowMask) は
            // 主光源と同じソフトフィルタを追加光にも掛けるので使わない。
            // 追加光ではクリアコートと Glitter のフラッシュを省く（T-418）。ステージの
            // 追加光で要るのは色・光沢・sheen・リムで、コートの薄い層と粒のきらめきは
            // 主光源だけで足りる（利用者判断）。allowCoat = false はコンパイル時に畳まれる。
            void ToonAccumulateAdditionalLight(uint lightIndex, int addShadowIndex, float3 positionWS,
                                               uint meshRenderingLayers, ToonSurface s, ToonContext c,
                                               float2 rimShape, inout float3 addAccum)
            {
                Light addLight = GetAdditionalLight(lightIndex, positionWS);
                addLight.shadowAttenuation = ToonAdditionalLightShadowHard(addShadowIndex, positionWS, addLight.direction);
                #if defined(_LIGHT_COOKIES)
                    addLight.color *= SampleAdditionalLightCookie(addShadowIndex, positionWS);
                #endif
                #ifdef _LIGHT_LAYERS
                    if (!IsMatchingLightLayer(addLight.layerMask, meshRenderingLayers)) return;
                #endif

                ToonConditionLight(addLight);

                // 間接光の分岐は主光源基準に固定するので lit は捨てるが、
                // 落ち影の量はリムの消灯に要る（T-351）。
                // リムもこの光源で光る（成分として返る）。ステージのスポットで色を作る
                // 使い方では、これが無いとリムだけ主光源の色に取り残される。
                float addLitUnused, addCast;
                ToonLightTerms at = ToonShadeLight(s, c, addLight, addLight.direction,
                                                   _AddLightShadowColor, 0.0, rimShape, false,
                                                   addLitUnused, addCast);
                float3 addFlashUnused;
                ToonGlitterSet spNone = (ToonGlitterSet)0;   // glitterActive = false（定数で畳まれる）
                float3 addContrib = ToonComposeLight(at, c, addLight, 0.0, spNone, addFlashUnused);

                // Add = 物理的な加算 / Max = 最も強い 1 灯だけを採る（T-350）。
                // ステージのように何灯も浴びる絵では、加算だと肌が白へ寄って
                // 彩度が飛ぶ。Max なら色が残る（アニメ的な嘘だが目的に適う）。
                addAccum = (_AdditionalLightBlendMode > 0.5) ? max(addAccum, addContrib)
                                                             : addAccum + addContrib;
            }

            // ---- 3. ライト（主光源 + フィル + グリッタ + 追加光源）---------
            // c.dNdx / dNdy / edgeAA をここで書くので ToonContext は inout。
            // mainLit / mainCast は間接光の陰側判定に、realLightDir は MatCap の
            // ライト連動に、mainShadowAtten はデバッグ表示に、後段が使う。
            float3 ToonShadeLights(Varyings input, ToonSurface s, inout ToonContext c,
                                   float3 geomNormalWS,
                                   out float mainLit, out float mainCast,
                                   out float3 realLightDir, out float mainShadowAtten)
            {
                // ---- 主光源 ---------------------------------------------------
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);

                // 3引数版を使う理由:
                //   1. ライトクッキーを適用してくれる（窓枠や木漏れ日の影）。2引数版は素通し
                //   2. シャドウ距離のフェードを通る。2引数版は距離端で影が切れる
                // shadowMask が 1 なのはベイク影を使っていないため（リアルタイム専用）。
                Light mainLight = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));

                // ライト色の整形は**シェーディングの前に**掛ける（T-350）。
                // ここで書き換えておけば陰影・リム・グリッタが同じ色を見る。
                ToonConditionLight(mainLight);

                // 境界 AA の下限。主光源基準の NdotL の画面変化率を取る。
                // **必ずここで取ること。** 光源ループの中は Forward+ だと
                // 分岐するので、その中で ddx/ddy を呼ぶと値が保証されない。
                // 法線の画面微分。**ここで1回だけ取る。**
                // 光源ごとの edgeAA はこれと L から求める（ToonShadeLight を参照）。
                // ループ内で微分を取ると Forward+ で保証されないので、必ず外で。
                c.dNdx = ddx(c.N);   // c.N はこの段では法線マップ適用後の normalWS
                c.dNdy = ddy(c.N);

                // 主光源基準の値。追加光源へは使わなくなったが、
                // 顔の SDF など主光源だけを見る箇所がまだ参照する。
                c.edgeAA = fwidth(dot(c.N, mainLight.direction)) * 0.5;

                // レンダリングレイヤーが合わないライトは無視する。
                // 「背景用とキャラ用でライトを分ける」構成（README §6）を、
                // Culling Mask ではなく URP 標準のレイヤーで実現するための経路。
                #ifdef _LIGHT_LAYERS
                    uint meshRenderingLayers = GetMeshRenderingLayer();
                    bool mainLightMatches = IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers);
                #else
                    uint meshRenderingLayers = 0;   // ToonAccumulateAdditionalLight の引数に要る（読まれない）
                    bool mainLightMatches = true;
                #endif

                // 拡散に使う向き。既定は実ライトと同じ。
                float3 mainDiffuseDir = mainLight.direction;

                // 上書きされる前の実ライト方向を控えておく。
                // シャドウマップは常に実ライトから焼かれているので、
                // 受け側バイアスの計算はこちらを使わないと押し出す量がずれる。
                realLightDir = mainLight.direction;

                // 絵の都合で影の向きを差し替える。上書きは主光源だけに掛ける
                // （追加光源まで回すと点光源が意味を成さなくなる）。
                UNITY_BRANCH
                if (_LightOverrideOn > 0.5)
                {
                    float3 overrideDir = ToonOverrideLightDir();
                    mainDiffuseDir = overrideDir;

                    if (_LightOverrideSpecular > 0.5)
                        mainLight.direction = overrideDir;
                }

                #if defined(_HQ_SHADOW_ON)
                    // URP 標準の影を自前のサンプラで置き換える。
                    // 法線は法線マップを掛ける前の幾何法線を渡す（細部のノイズが
                    // オフセット量に乗るとアクネが戻るため）。
                    // NdotL は **実ライト方向**で取る。絵の都合で回した向きを渡すと、
                    // シャドウマップを焼いた向きと食い違ってアクネ対策が効かなくなる。
                    mainLight.shadowAttenuation = ToonSampleMainShadowHQ(
                        input.positionWS, geomNormalWS,
                        dot(geomNormalWS, realLightDir), c.dither,
                        mainLight.shadowAttenuation);
                #endif

                // 影の中の環境光を分けるため、主光源の遮蔽量だけを受け取っておく。
                // レイヤーが合わないときは 1（＝遮蔽なし）。効かないライトの影を
                // 環境光に反映してしまわないようにする。
                mainLit  = 1.0;
                mainCast = 0.0;   // 落ち影の量。間接光にも同じ着色を掛けるのに要る
                float3 color    = 0.0;

                // ---- グリッタ（ラメ・スパンコール。T-348）--------------------
                // ライト非依存の幾何（最近傍セル探索）を 1 回だけ計算し、各ライトで
                // フラッシュだけ乗せる（Doll と同じ 2 段構成・Core BRDF_Glitter 共有）。
                // _GLITTER_ON（Intensity > 0 に追従するキーワード）が無ければコードごと消える（T-418）。
                // 以前は一様分岐だったが、OFF でも最悪経路のレジスタ（8 本）を確保され占有率を下げていた。
                // 粒の色。Albedo Tint で生地の色を掛ける（T-407）。
                // アルベドは 1 以下なので、既定の HDR 色 (2,2,2) と組むと「生地の色 × 2」のラメになる。
                GlitterGeom glitterGeom = (GlitterGeom)0;
                bool glitterActive = false;
                float glitterMask = 0.0;
                ToonGlitterSet sp = (ToonGlitterSet)0;
            #if defined(_GLITTER_ON)
                float3 glitterColor = _GlitterColor.rgb * lerp(1.0, s.albedo, _GlitterAlbedoTint);
                UNITY_BRANCH
                if (_GlitterIntensity > 0.0)
                {
                    // uv はこの段では c.uv（コンテキストに載せた同じ値）
                    glitterMask = SAMPLE_TEXTURE2D(_GlitterMask, sampler_GlitterMask, c.uv).r;
                    glitterActive = PrepareGlitter(c.specN, c.V, c.uv,
                                                   _GlitterScale, _GlitterSize,
                                                   _GlitterTilt, glitterMask,
                                                   _GlitterIntensity, _GlitterSparsity,
                                                   glitterGeom);
                }


                // スパンコールで鏡面・リムを分解する（T-413）。マスクは円盤の中で > 0、外で 0、
                // 平均 ≈ 1。機能が有効（Intensity > 0 かつマスク > 0）な画素だけ。
                float glitterGrain = 1.0;
                UNITY_BRANCH
                if (_GlitterIntensity > 0.0 && glitterMask > 0.0 && (_GlitterRim > 0.0 || _GlitterSpecular > 0.0))
                    glitterGrain = glitterActive
                        ? ToonGlitterGrainMask(glitterGeom, c.specN, c.V, _GlitterScale, _GlitterSize, _GlitterSparsity)
                        : 0.0;
                c.specGrain = lerp(1.0, glitterGrain * _GlitterSpecular, saturate(_GlitterSpecular));
                c.rimGrain  = lerp(1.0, glitterGrain * _GlitterRim,      saturate(_GlitterRim));
                sp.glitter = glitterGeom; sp.glitterActive = glitterActive;
                sp.color   = glitterColor;
            #endif

                // 遮蔽量の画面変化率。**ここで取ること。** 光源ループの中は
                // Forward+ だと反復回数が実行時に決まるので微分が保証されない。
                // HQ 影を畳んだ後の最終値に対して取る。
                float mainAttenAA = fwidth(mainLight.shadowAttenuation);

                // 縁の光沢の「形」は視線だけで決まる（深度フェッチもここ 1 回）。
                // 「どの光が縁を照らすか」はライトごとに適用する（T-351）。
                // x = リム / y = 産毛（T-363）。
                float2 rimShape = ToonRimShape(s, c);

                if (mainLightMatches)
                {
                    ToonLightTerms mt = ToonShadeLight(s, c, mainLight, mainDiffuseDir,
                                                       1.0, mainAttenAA, rimShape, true, mainLit, mainCast);
                    // 主光源だけ環境光（SH）を粒のエネルギーに足す（影の中で粒が消えないように。T-378）。
                    // SH はこの分岐（粒が有効な材質）でしか評価されない。
                    float3 mainFlash;
                    color = ToonComposeLight(mt, c, mainLight,
                                             glitterActive ? SampleSH(c.N) * _AmbientIntensity : 0.0,
                                             sp, mainFlash);
                    color += mainFlash;
                }

                // ---- フィルライト（T-370。Doll から輸入）----------------------
                // 指定方向（Pitch / Yaw・ワールド）からのバウンス光を陰側に注ぐ。
                // 床の照り返しが典型。実ライトと独立した加算光。
                // Half-Lambert（wrap 0.5）で柔らかく回り込ませ、Shade Side Only で
                // 主光の陰側に限定する（照っている側まで足すと白飛び方向にしか
                // 働かないため既定 1）。
                UNITY_BRANCH
                if (_FillIntensity > 0.0)
                {
                    float pitchRad = radians(_FillPitch);
                    float yawRad   = radians(_FillYaw);
                    float3 fillDir = float3(cos(pitchRad) * sin(yawRad),
                                            sin(pitchRad),
                                            cos(pitchRad) * cos(yawRad));
                    float fillShade = saturate(dot(c.shadeN, fillDir) * 0.5 + 0.5);
                    float litSide   = mainLightMatches ? mainLit : 0.0;
                    float shadeSide = lerp(1.0, 1.0 - litSide, _FillShadeOnly);
                    color += s.albedo * _FillColor.rgb
                           * (_FillIntensity * fillShade * shadeSide);
                }


                // ---- 追加光源 -------------------------------------------------
                float3 addAccum = float3(0, 0, 0);
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS              = input.positionWS;
                    inputData.normalizedScreenSpaceUV = c.screenUV;

                    // **Forward+ では追加の Directional はクラスタに入らない。** ライト配列の先頭
                    // URP_FP_DIRECTIONAL_LIGHTS_COUNT 個に並び、URP の LIGHT_LOOP_BEGIN はそこを飛ばして
                    // クラスタの Point / Spot だけを回す（Lit.shader も別ループで先に回している）。
                    // これが無いと **2 灯目以降の Directional が PC（Forward+）で一切効かない**（T-438。
                    // IdolLookRig の Rim ライトを赤にしても赤くならなかった原因）。
                    #if USE_CLUSTER_LIGHT_LOOP
                        UNITY_LOOP
                        for (uint dirIndex = 0; dirIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); dirIndex++)
                            ToonAccumulateAdditionalLight(dirIndex, dirIndex, input.positionWS, meshRenderingLayers,
                                                          s, c, rimShape, addAccum);
                    #endif

                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        #if USE_CLUSTER_LIGHT_LOOP
                            int addShadowIndex = lightIndex;
                        #else
                            int addShadowIndex = GetPerObjectLightIndex(lightIndex);
                        #endif
                        ToonAccumulateAdditionalLight(lightIndex, addShadowIndex, input.positionWS, meshRenderingLayers,
                                                      s, c, rimShape, addAccum);
                    LIGHT_LOOP_END
                #endif
                // 追加光の合計を丸める（T-421）。Add 合成で何灯も重なったぶんの白飛び対策
                UNITY_BRANCH
                if (_AdditionalLightTotalLimit > 0.0)
                    addAccum = ToonSoftLuminanceLimit(addAccum, _AdditionalLightTotalLimit);
                color += addAccum;

                mainShadowAtten = mainLight.shadowAttenuation;
                return color;
            }

            // ---- 4. 環境と後処理（間接光 / MatCap / エミッシブ / 暗転 / フォグ）
            void ToonApplyEnvironmentAndPost(ToonSurface s, ToonContext c,
                                             float mainLit, float mainCast,
                                             float3 realLightDir, float fogFactor,
                                             inout float3 color)
            {
                // ---- 間接光 ---------------------------------------------------
                color += ToonShadeIndirect(s, c, mainLit, mainCast);

                // ---- MatCap ---------------------------------------------------
                // **加算だけ。** 物理の上に載せるアクセントで、環境光の主経路
                //（プローブ + SH）は置き換えない。既定 0 で分岐ごと飛ぶ。
            #if defined(_MATCAP_ON)
                UNITY_BRANCH
                if (_MatCapIntensity > 0.0)
                {
                    color += ToonMatCap(c.specN, realLightDir);
                }
            #endif

                // ---- 最終出力の輝度上限（T-421）-----------------------------------
                // 直接光＋間接光＋MatCap まで。**発光の手前**に置く ── 発光は Bloom のために
                // HDR のまま通したいので対象外。
                UNITY_BRANCH
                if (_OutputLuminanceLimit > 0.0)
                    color = ToonSoftLuminanceLimit(color, _OutputLuminanceLimit);

                // ---- エミッシブ -----------------------------------------------
                color += s.emission;

                // ---- 暗転（T-361）---------------------------------------------
                // **エミッシブの後に掛ける。** 前だと発光だけが残って暗転しない。
                // アルファは触らないので、半透明でも「透けて消える」のではなく
                // 黒く沈む（消したいときはディゾルブ側の役目）。
                color = lerp(color, float3(0.0, 0.0, 0.0), _BlackOut);

                color = MixFog(color, fogFactor);

            }

            half4 ToonFrag(Varyings input) : SV_Target
            {
                #ifdef LOD_FADE_CROSSFADE
                    LODFadeCrossFade(input.positionCS);
                #endif

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // **前髪透過にキーワードのゲートは持たない。** 以前は
                // `_HAIRSEETHROUGH_ON` で切っていたが、それだと
                // **ステンシルを設定しただけでは効かない** ── 髪が穴を空けて
                // 誰も埋めない状態になり、目と眉が素通しで出る（T-254）。
                //
                // ゲートはステンシルそのもの（`Ref 0 / ReadMask 6 / Comp NotEqual`）。
                // 眉と目がビットを書いていなければ 1 画素も描かれないので、
                // 設定していないマテリアルには影響しない。移植元も同じ作り。

                float2 uv = input.uv;

                float3 normalWS, baseNormalWS, tangentWS, bitangentWS, geomNormalWS;
                float  specAAKernel;
                ToonSurface s = ToonGatherSurface(input, uv, normalWS, baseNormalWS, tangentWS,
                                                  bitangentWS, geomNormalWS, specAAKernel);

                ToonContext c = ToonBuildContext(input, uv, s, normalWS, baseNormalWS, tangentWS,
                                                 bitangentWS, geomNormalWS, specAAKernel);

                float  mainLit, mainCast, mainShadowAtten;
                float3 realLightDir;
                float3 color = ToonShadeLights(input, s, c, geomNormalWS,
                                               mainLit, mainCast, realLightDir,
                                               mainShadowAtten);

                ToonApplyEnvironmentAndPost(s, c, mainLit, mainCast, realLightDir,
                                            input.fogFactor, color);

                // ---- デバッグ表示 ---------------------------------------------
                // **絵から逆算しにくい量を直接見るための出口。**
                // 曲率・遮蔽量・Cavity は、最終色に混ざった後では
                // 効いているのか判断できない。実際これらは「実装されているのに
                // 効いていない」状態を長期間見逃す原因になった。
                // 動的分岐でバリアントは増えない。既定 0 で何も起きない。
            #if defined(_DEBUG_ON)
                UNITY_BRANCH
                if (_DebugMode > 0.5)
                {
                    int mode = (int)(_DebugMode + 0.5);
                    float3 dbg = color;

                    if      (mode == 1)  dbg = s.albedo;
                    else if (mode == 2)  dbg = c.N      * 0.5 + 0.5;
                    else if (mode == 3)  dbg = c.shadeN * 0.5 + 0.5;
                    else if (mode == 4)  dbg = c.bentN  * 0.5 + 0.5;
                    else if (mode == 5)  dbg = mainLit;                    // トゥーンの伝達関数の出力
                    else if (mode == 6)  dbg = mainShadowAtten;            // HQ セルフシャドウ込み
                    else if (mode == 7)  dbg = c.curvature;                // 0=平面 1=参照半径以上
                    else if (mode == 8)  dbg = s.occlusion;
                    else if (mode == 9)  dbg = s.cavity;
                    else if (mode == 11) dbg = s.perceptualRoughness;
                    else if (mode == 13) dbg = s.shadowColor;
                    else if (mode == 14) dbg = s.specMask;

                    return half4(dbg, 1);
                }
            #endif

                // 前髪透過は**ライティングを一切変えず、アルファだけ差し替える。**
                // 髪の ForwardLit がステンシルで抜いた穴を、同じ色の半透明で埋める
                // 構成なので、ここで色を変えると穴の縁で色が段になる。
                //
                // 上書きするのはアルファテストの後の値。毛先の切り抜き（`_Cutoff`）は
                // 通ったままなので、**毛の形は残して濃さだけが変わる。**
                #if defined(TOON_HAIR_SEETHROUGH)
                    return half4(color, _HairSeeThroughAlpha);
                #endif

                return half4(color, s.alpha);
            }

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
// 切り出しは**1行も変えていない。** include を展開し直して元の `.shader` と
// バイト一致することを確かめてから分けた（T-210）。

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

                // ---- サーフェス ------------------------------------------------
                float4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
                float4 albedo  = baseTex * _BaseColor;

                // **アルファテストより前に掛ける。** HSV は alpha を触らないので
                // 切り抜きの結果は変わらないが、順序を後にすると
                // 「切り抜かれた画素だけ補正前の色」という食い違いが生まれうる。
                albedo.rgb = ToonAlbedoHSV(albedo.rgb);

                // ディテールマップ（T-368）: A の合成率でベースへ重ねる。
                // HSV 補正の**後**に置く ── ディテール（チークの赤等）は
                // 「その色で置く」意図なので、全体の色調補正に巻き込まない。
                UNITY_BRANCH
                if (_DetailOn > 0.5)
                {
                    float2 detailUV = uv * _DetailMap_ST.xy + _DetailMap_ST.zw;
                    float4 detail = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, detailUV)
                                  * _DetailColor;
                    albedo.rgb = lerp(albedo.rgb, detail.rgb, detail.a);
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
                // 中立値。R=鏡面フル / G=0.5（オフセット 0）/ B=リムフル / A=ランプ先頭。
                // **白テクスチャでは G が 1 になり、影が最大まで遅れて出なくなる。**
                // 仕様（REQUIREMENTS §6「G は 0.5 が基準」）と食い違うのでトグルで切る。
                float4 npr = float4(1.0, 0.5, 1.0, 0.0);
                UNITY_BRANCH
                if (_NPRMapOn > 0.5) npr = SAMPLE_TEXTURE2D(_NPRMap, sampler_NPRMap, uv);

                // 窪みの微細遮蔽。**アルベドと鏡面の両方に掛ける。**
                // EasyPBR はアルベドだけに掛けているが、それだと縫い目や皺の底に
                // 鏡面がそのまま残って、暗くしたはずの場所が逆に目立つ。
                float cavity = 1.0;
                UNITY_BRANCH
                if (_CavityStrength > 0.0)
                {
                    float raw = SAMPLE_TEXTURE2D(_CavityMap, sampler_CavityMap, uv).r;
                    cavity = lerp(1.0, raw, _CavityStrength);
                    albedo.rgb *= cavity;
                }

                ToonSurface s;
                s.cavity     = cavity;
                s.albedo     = albedo.rgb;
                s.alpha      = albedo.a;
                s.thickness  = mask.b;
                // **saturate すること。** Range は 0..1 だが、他シェーダーから移植した
                // マテリアルには範囲外の値がシリアライズされて残る（実際 5 件が 2 だった）。
                // Range 属性はインスペクタのスライダを縛るだけで、実行時の値は縛らない。
                // 強度 2 だと lerp が外挿になり `2*ao - 1`、AO 0.5 未満で**遮蔽が負**になる。
                // 負の遮蔽は多重バウンス補正・マイクロシャドウ・鏡面遮蔽の全部を狂わせる。
                s.occlusion  = saturate(lerp(1.0, mask.g, _OcclusionStrength));
                s.specMask   = npr.r;
                s.shadowOffset = (npr.g - 0.5) * 2.0;
                s.rimMask    = npr.b;
                s.rampIndex  = npr.a;

                float metallic   = mask.r * _Metallic;
                float smoothness = mask.a * _Smoothness;

                s.emission = dissolveEmission;
                UNITY_BRANCH
                if (_EmissionOn > 0.5)
                {
                    s.emission += SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, uv).rgb
                                * _EmissionColor.rgb;
                }

                // ---- 法線 -----------------------------------------------------
                float3 normalWS   = normalize(input.normalWS);
                float3 tangentWS  = normalize(input.tangentWS.xyz);
                float3 bitangentWS= normalize(cross(normalWS, tangentWS) * input.tangentWS.w);

                // 法線マップを掛ける前の幾何法線。ベイクしたマップは
                // この向きの接線空間で焼かれているので、戻すときもこれを使う。
                float3 geomNormalWS = normalWS;

                // ベースとディテールの法線は**接空間で合成してから 1 回だけ回す**。
                // TBN 回転を 2 回重ねると合成にならない（回転の連結は加算と違う）。
                float3 normalTS = float3(0.0, 0.0, 1.0);
                UNITY_BRANCH
                if (_NormalMapOn > 0.5)
                {
                    normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
                }
                UNITY_BRANCH
                if (_DetailOn > 0.5)
                {
                    float3 dTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap,
                                         uv * _DetailMap_ST.xy + _DetailMap_ST.zw),
                        _DetailNormalScale);
                    // whiteout ブレンド（xy 加算・z 乗算。Doll と同じ）
                    normalTS = normalize(float3(normalTS.xy + dTS.xy, normalTS.z * dTS.z));
                }
                UNITY_BRANCH
                if (_NormalMapOn > 0.5 || _DetailOn > 0.5)
                {
                    normalWS = normalize(mul(normalTS, float3x3(tangentWS, bitangentWS, normalWS)));
                }

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
                UNITY_BRANCH
                if (_StockingIntensity > 0.0)
                {
                    float3 viewWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                    ToonStockingLayer(uv, saturate(dot(normalWS, viewWS)), s.albedo);
                }

                s.diffuseColor = s.albedo * (1.0 - metallic);
                s.f0           = lerp(0.04, s.albedo, metallic);
                s.perceptualRoughness = 1.0 - smoothness;

                // 影色はライトに依存しないので1回だけ求める。**diffuseColor の確定後**に置くこと
                // （デカールが albedo と metallic を書き換えるため、その前だと値が違う）。
                s.shadowColor = ToonShadowAlbedo(s.diffuseColor);

                // ジオメトリックな法線変化から粗さを補正 (スペキュラのちらつき対策)
                //
                // **カーネルはここで1回だけ求める。** 微分を使うので光源ループの中では
                // 取れない（Forward+ は反復回数が実行時に決まる）。
                // ToonContext はまだ宣言されていないのでローカルに受け、後で載せる。
                float specAAKernel = ToonSpecAAKernel(normalWS);

                s.roughness = ToonApplyRoughnessKernel(
                                  s.perceptualRoughness * s.perceptualRoughness, specAAKernel);
                s.roughness = max(s.roughness, 0.002);

                // フィルタ後の粗さを perceptual 側にも戻す。戻さないと
                // ToonRoughnessToMip が AA 前の粗さで mip を引き、
                // **スペキュラ AA が環境反射に効かない**（金具のちらつきが残る）。
                s.perceptualRoughness = sqrt(s.roughness);

                // ---- コンテキスト ---------------------------------------------
                ToonContext c;
                c.positionWS = input.positionWS;
                c.N          = normalWS;
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
                c.curvature = 0.0;
                UNITY_BRANCH
                if (_CurvatureSoftness > 0.0)
                {
                    float baked = SAMPLE_TEXTURE2D(_CurvatureMap, sampler_CurvatureMap, uv).r;
                    c.curvature = abs(baked * 2.0 - 1.0);
                }
                c.uv         = uv;
                c.screenUV   = GetNormalizedScreenSpaceUV(input.positionCS);
                c.positionSS = input.positionCS.xy;
                c.eyeDepth   = LinearEyeDepth(input.positionCS.z, _ZBufferParams);

                // ディザが要る処理（影の PCF・コンタクトシャドウ）で共有する。
                // 別々に計算すると同じ式を2回踏むだけで、利点が無い。
                c.dither     = ToonIGN(input.positionCS.xy);

                // UV の画面微分。**ここで取ること。** 光源ループの中は Forward+ だと
                // 反復回数が実行時に決まるので暗黙 LOD が使えない。ミップを捨てずに
                // 済ませるため、ループ内のサンプルにはこれを渡して _GRAD で引く。
                c.uvDx       = ddx(uv);
                c.uvDy       = ddy(uv);

                // 顔 SDF の境界 AA。同じ理由でループ内では fwidth を呼べない。
                // Face 以外はフェッチごと発生しない。
                c.faceSdfAA  = 0.0;
                c.faceSdf    = 0.0;
                #if defined(_SURFACETYPE_FACE)
                    // 16bit 1ch（R×256+G）をデコードしてから変化率を取る。
                    // 上位バイトだけの fwidth だと 256 段の飛びを拾って AA が過大になる。
                    c.faceSdf   = ToonDecodeFaceSdf16(
                                      SAMPLE_TEXTURE2D(_FaceSDFMap, sampler_FaceSDFMap, uv).rg);
                    c.faceSdfAA = fwidth(c.faceSdf);
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
                #if defined(_SURFACETYPE_CLOTH)
                {
                    // **ここも光源に依存しない。** 以前はエネルギー保存の計算と
                    // ライトループの D 項とで**二重に**求めていた。1回に畳む。
                    c.sheenAlpha = ToonApplyRoughnessKernel(max(_SheenRoughness, 0.02), c.specAAKernel);

                    float3 sc = _SheenColor.rgb * _SheenIntensity;
                    c.sheenScale = saturate(1.0
                        - ToonSheenAlbedo(c.NdotV, c.sheenAlpha)
                        * max(max(sc.r, sc.g), sc.b) * _SheenEnergyConservation);
                }
                #endif

                // 前髪の影は位置だけで決まる。ここで1回引いて全光源で使い回す。
                // 光源ループの外なので、この値の微分を境界 AA の下限に使える。
                UNITY_BRANCH

                #if defined(_SCREEN_SPACE_OCCLUSION)
                    // URP の SSAO を遮蔽に畳む。DepthNormals パスを残しているのは
                    // これを成立させるため（REQUIREMENTS の NFR / パス構成の前提）。
                    //
                    // 取るのは indirect 側だけ。direct への効かせ方は _DirectOcclusion が
                    // 握っているので、URP 側の direct 係数まで掛けると二重になる。
                    AmbientOcclusionFactor ssao = GetScreenSpaceAmbientOcclusion(c.screenUV);
                    s.occlusion = min(s.occlusion, ssao.indirectAmbientOcclusion);
                #endif

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
                c.dNdx = ddx(normalWS);
                c.dNdy = ddy(normalWS);

                // 主光源基準の値。追加光源へは使わなくなったが、
                // 顔の SDF など主光源だけを見る箇所がまだ参照する。
                c.edgeAA = fwidth(dot(normalWS, mainLight.direction)) * 0.5;

                // レンダリングレイヤーが合わないライトは無視する。
                // 「背景用とキャラ用でライトを分ける」構成（README §6）を、
                // Culling Mask ではなく URP 標準のレイヤーで実現するための経路。
                #ifdef _LIGHT_LAYERS
                    uint meshRenderingLayers = GetMeshRenderingLayer();
                    bool mainLightMatches = IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers);
                #else
                    bool mainLightMatches = true;
                #endif

                // 拡散に使う向き。既定は実ライトと同じ。
                float3 mainDiffuseDir = mainLight.direction;

                // 上書きされる前の実ライト方向を控えておく。
                // シャドウマップは常に実ライトから焼かれているので、
                // 受け側バイアスの計算はこちらを使わないと押し出す量がずれる。
                float3 realLightDir = mainLight.direction;

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
                float  mainLit  = 1.0;
                float  mainCast = 0.0;   // 落ち影の量。間接光にも同じ着色を掛けるのに要る
                float3 color    = 0.0;

                // ---- グリッタ（ラメ・スパンコール。T-348）--------------------
                // ライト非依存の幾何（最近傍セル探索）を 1 回だけ計算し、各ライトで
                // フラッシュだけ乗せる（Doll と同じ 2 段構成・Core BRDF_Glitter 共有）。
                // Intensity 0 ではマスクのフェッチごと飛ぶ＝キーワード不要で実質無料。
                GlitterGeom glitterGeom = (GlitterGeom)0;
                bool glitterActive = false;
                UNITY_BRANCH
                if (_GlitterIntensity > 0.0)
                {
                    float glitterMask = SAMPLE_TEXTURE2D(_GlitterMask, sampler_GlitterMask, uv).r;
                    glitterActive = PrepareGlitter(c.N, c.V, uv,
                                                   _GlitterScale, _GlitterSize,
                                                   _GlitterTilt, glitterMask,
                                                   _GlitterIntensity, _GlitterSparsity,
                                                   glitterGeom);
                }

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
                    color = ToonShadeLight(s, c, mainLight, mainDiffuseDir,
                                           1.0, mainAttenAA, mainLit, mainCast);
                    color += ToonRimLight(rimShape, c, mainLight.direction,
                                          ToonDiffuseEnergy(ToonLightEnergy(mainLight)),
                                          mainCast);
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

                // グリッタのフラッシュは主光源の影・距離減衰で暗くする
                //（影の中で光り続けると粒だけ浮くため）。
                // **ただし環境光は足す（T-378）。** Core の ApplyGlitterLight は
                // フラッシュもベース反射も同じ光エネルギーに掛けるので、影の減衰
                // だけだと濃い影の中で**ベースまで消えて粒が無くなる**（利用者報告）。
                // スパンコールのベース反射は空や周囲を映すもので、影＝真っ暗は嘘。
                // Doll は direct + indirect を渡している（影の減衰は無し）。Idol は
                // 「影の中で強く光らない」を残しつつ、環境光ぶんは常に通す。
                // SH はこの分岐（グリッタ有効な材質）でしか評価しない。
                if (glitterActive && mainLightMatches)
                    color += ApplyGlitterLight(glitterGeom, mainLight.direction, c.V,
                                               _GlitterColor.rgb, _GlitterIntensity,
                                               _GlitterIridescence, _GlitterIridescenceShift,
                                               _GlitterBaseReflection,
                                               mainLight.color * (mainLight.distanceAttenuation
                                                                  * mainLight.shadowAttenuation)
                                               + SampleSH(c.N) * _AmbientIntensity);

                // ---- 追加光源 -------------------------------------------------
                float3 addAccum = float3(0, 0, 0);
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS              = input.positionWS;
                    inputData.normalizedScreenSpaceUV = c.screenUV;

                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light addLight = GetAdditionalLight(lightIndex, input.positionWS, half4(1,1,1,1));

                        #ifdef _LIGHT_LAYERS
                            if (!IsMatchingLightLayer(addLight.layerMask, meshRenderingLayers))
                                continue;
                        #endif

                        ToonConditionLight(addLight);

                        // 間接光の分岐は主光源基準に固定するので lit は捨てるが、
                        // 落ち影の量はリムの消灯に要る（T-351）。
                        float addLitUnused, addCast;
                        float3 addContrib = ToonShadeLight(s, c, addLight, addLight.direction,
                                                           _AddLightShadowColor, 0.0,
                                                           addLitUnused, addCast);

                        // **リムもこの光源で光る。** ステージのスポットで色を作る
                        // 使い方では、ここが無いとリムだけ主光源の色に取り残される。
                        addContrib += ToonRimLight(rimShape, c, addLight.direction,
                                                   ToonDiffuseEnergy(ToonLightEnergy(addLight)),
                                                   addCast);

                        // Add = 物理的な加算 / Max = 最も強い 1 灯だけを採る（T-350）。
                        // ステージのように何灯も浴びる絵では、加算だと肌が白へ寄って
                        // 彩度が飛ぶ。Max なら色が残る（アニメ的な嘘だが目的に適う）。
                        addAccum = (_AdditionalLightBlendMode > 0.5)
                                 ? max(addAccum, addContrib)
                                 : addAccum + addContrib;

                        if (glitterActive)
                            color += ApplyGlitterLight(glitterGeom, addLight.direction, c.V,
                                                       _GlitterColor.rgb, _GlitterIntensity,
                                                       _GlitterIridescence, _GlitterIridescenceShift,
                                                       _GlitterBaseReflection,
                                                       addLight.color * (addLight.distanceAttenuation
                                                                         * addLight.shadowAttenuation));
                    LIGHT_LOOP_END
                #endif
                color += addAccum;

                // ---- 間接光 ---------------------------------------------------
                color += ToonShadeIndirect(s, c, mainLit, mainCast);

                // ---- MatCap ---------------------------------------------------
                // **加算だけ。** 物理の上に載せるアクセントで、環境光の主経路
                //（プローブ + SH）は置き換えない。既定 0 で分岐ごと飛ぶ。
                UNITY_BRANCH
                if (_MatCapIntensity > 0.0)
                {
                    color += ToonMatCap(c.N, realLightDir);
                }

                // ---- エミッシブ -----------------------------------------------
                color += s.emission;

                // ---- 暗転（T-361）---------------------------------------------
                // **エミッシブの後に掛ける。** 前だと発光だけが残って暗転しない。
                // アルファは触らないので、半透明でも「透けて消える」のではなく
                // 黒く沈む（消したいときはディゾルブ側の役目）。
                color = lerp(color, float3(0.0, 0.0, 0.0), _BlackOut);

                color = MixFog(color, input.fogFactor);

                // ---- デバッグ表示 ---------------------------------------------
                // **絵から逆算しにくい量を直接見るための出口。**
                // 曲率・遮蔽量・Cavity は、最終色に混ざった後では
                // 効いているのか判断できない。実際これらは「実装されているのに
                // 効いていない」状態を長期間見逃す原因になった。
                // 動的分岐でバリアントは増えない。既定 0 で何も起きない。
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
                    else if (mode == 6)  dbg = mainLight.shadowAttenuation; // HQ / コンタクト込み
                    else if (mode == 7)  dbg = c.curvature;                // 0=平面 1=参照半径以上
                    else if (mode == 8)  dbg = s.occlusion;
                    else if (mode == 9)  dbg = s.cavity;
                    else if (mode == 11) dbg = s.perceptualRoughness;
                    else if (mode == 13) dbg = s.shadowColor;
                    else if (mode == 14) dbg = s.specMask;

                    return half4(dbg, 1);
                }

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

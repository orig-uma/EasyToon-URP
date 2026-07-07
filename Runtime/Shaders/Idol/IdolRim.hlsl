// =============================================================================
//  IdolRim.hlsl  (policy)
// -----------------------------------------------------------------------------
//  リムライト 3 種:
//   (1) スクリーンスペース深度リム
//       _CameraDepthTexture を「ライト方向のスクリーン投影向き」へ _RimWidthPx
//       ピクセルぶんオフセットした位置でサンプルし、線形深度差でリム判定。
//       画面上の幅がピクセル一定（距離・FOV 非依存）。
//       ※ カメラの Depth Texture（URP Asset の Depth Texture ON、または
//         Forward+ / SSAO 等で深度プリパスが走る構成）が必須。
//   (2) フレネルリム（従来式・Common の GetFresnelTerms / CalculateRimLight 流用）
//   (3) バックライトリム（ライブ演出用。ライトと独立した指定方向のシルエットリム）
//
//  前提: URP Core.hlsl / Lighting.hlsl / DeclareDepthTexture.hlsl、
//        EasyShaderCore Common（BRDF_RimFuzz.hlsl）、IdolInput.hlsl。
// =============================================================================
#ifndef IDOL_RIM_INCLUDED
#define IDOL_RIM_INCLUDED

// -----------------------------------------------------------------------------
//  ライト方向のスクリーン投影向き（2D 正規化）。
//  深度リムと髪スクリーン影の両方がこのヘルパを使う——スクリーン
//  Y 反転等の座標系修正が必要になった場合、直す箇所をここ 1 箇所にするため。
//  戻り値 false: ライトが画面法線方向（真正面/真後ろ）で向きが定義できない。
// -----------------------------------------------------------------------------
bool GetLightScreenDir(float3 lightDirWS, out float2 dirSS)
{
    float3 lightVS = mul((float3x3)GetWorldToViewMatrix(), lightDirWS);
    dirSS = lightVS.xy;
    float len = length(dirSS);
    if (len < 1e-4)
    {
        dirSS = float2(0.0, 0.0);
        return false;
    }
    dirSS /= len;
    return true;
}

// -----------------------------------------------------------------------------
//  深度リム: 自ピクセルの視深度(selfEyeDepth)と、ライト方向へオフセットした
//  位置の深度テクスチャ値を比較。オフセット先が「奥」ならライト側の輪郭。
// -----------------------------------------------------------------------------
half3 CalculateDepthRim(float2 screenUV, float selfEyeDepth,
                        half3 normalWS, float3 lightDirWS, half3 lightColor)
{
    // ライト方向をビュー空間へ投影し、画面内の向き（2D）を得る（共有ヘルパ）。
    float2 dirSS;
    if (!GetLightScreenDir(lightDirWS, dirSS))
    {
        return half3(0.0, 0.0, 0.0);
    }

    // ピクセル一定幅: _RimWidthPx / 画面解像度 で UV オフセット量を決める。
    float2 offsetUV = screenUV + dirSS * (_RimWidthPx * (_ScreenParams.zw - 1.0));

    // オフセット先の線形深度。自分より十分「奥」ならシルエット縁と判定。
    float offsetRaw = SampleSceneDepth(offsetUV);
    float offsetEyeDepth = LinearEyeDepth(offsetRaw, _ZBufferParams);
    float depthDiff = offsetEyeDepth - selfEyeDepth;

    // キャラ厚を考慮したしきい値。fwidth 相当の固定ソフトネスで縁を安定させる。
    float rim = smoothstep(_RimDepthThreshold, _RimDepthThreshold * 2.0, depthDiff);

    // 受光側マスク: NdotL で受光面に限定（_RimLightAlign 0 = 全周）。
    float ndotl = dot(normalWS, lightDirWS);
    float lightSide = lerp(1.0, saturate(ndotl * 2.0 + 0.5), _RimLightAlign);

    return _RimColor.rgb * lightColor * (rim * lightSide * _RimDepthIntensity);
}

// -----------------------------------------------------------------------------
//  髪→顔のスクリーンスペース落ち影（R9）。
//  顔ピクセルから「ライト方向のスクリーン投影向き」へ _HairShadowOffsetPx 先の
//  深度を参照し、自分より [Min, Max] だけ手前に遮蔽（前髪）があれば落ち影と判定。
//  深度差の窓で「近くの薄いもの」だけを拾い、壁や体など遠い遮蔽は無視する。
//  キャラ影（R4）より精細な、画面解像度そのままの前髪影をクローズアップで出す。
//  戻り値: 0(影)..1(影なし)。呼び出し側で castShadow と min() 合成する。
//  Face/Brow/Eye マテリアルで有効化する想定。深度リムとインフラ共有。
// -----------------------------------------------------------------------------
half CalculateHairScreenShadow(float2 screenUV, float selfEyeDepth, float3 lightDirWS)
{
    float2 dirSS;
    if (!GetLightScreenDir(lightDirWS, dirSS))
    {
        return 1.0; // 向きが定義できないフレームは影なし
    }

    // ライト方向へ _HairShadowOffsetPx ぶん先の深度をサンプル
    //（遮蔽物は光源側にオフセットして見えるため + 方向）。
    float2 offsetUV = screenUV + dirSS * (_HairShadowOffsetPx * (_ScreenParams.zw - 1.0));
    float offsetRaw = SampleSceneDepth(offsetUV);
    float offsetEyeDepth = LinearEyeDepth(offsetRaw, _ZBufferParams);

    // 手前の遮蔽なら正。窓 [Min, Max] 内だけを前髪影と判定
    //（Min 未満=自己面や連続面、Max 超=遠い遮蔽で除外）。
    float occluderDiff = selfEyeDepth - offsetEyeDepth;

    // 窓の両端に窓幅比例の小さなソフトネスを持たせて縁を安定させる。
    float feather = max((_HairShadowDepthMax - _HairShadowDepthMin) * 0.25, 1e-4);
    half hairShadow = smoothstep(_HairShadowDepthMin - feather, _HairShadowDepthMin, occluderDiff)
                    * (1.0 - smoothstep(_HairShadowDepthMax, _HairShadowDepthMax + feather, occluderDiff));

    return 1.0 - hairShadow * _HairShadowIntensity;
}

// -----------------------------------------------------------------------------
//  バックライトリム: pitch/yaw 指定方向のフレネルリム。ライト非連動の演出光。
// -----------------------------------------------------------------------------
half3 CalculateBackRim(half3 normalWS, half NdotV, half3 viewDirectionWS)
{
    float pitchRad = radians(_BackRimPitch);
    float yawRad   = radians(_BackRimYaw);
    // サーフェスから光源へ向かう方向（light.direction と同じ規約）。
    float3 backDirWS = float3(cos(pitchRad) * sin(yawRad), sin(pitchRad), cos(pitchRad) * cos(yawRad));

    // 指定方向の「向こう側」にある面のシルエット縁を光らせる。
    float fresnel = pow(saturate(1.0 - NdotV), _BackRimPower);
    float facing  = saturate(dot(normalWS, backDirWS));

    return _BackRimColor.rgb * (fresnel * facing);
}

// -----------------------------------------------------------------------------
//  リム統合（メインライトのみで呼ぶ）。
//   screenUV:     GetNormalizedScreenSpaceUV(positionCS) で取得
//   selfEyeDepth: positionCS.w（透視投影ではビュー空間深度）
// -----------------------------------------------------------------------------
half3 CalculateRimLighting(IdolSurfaceData s, float2 screenUV, float selfEyeDepth,
                           Light mainLight, half3 viewDirectionWS, half castShadow)
{
    half3 rim = half3(0.0, 0.0, 0.0);

    // (1) 深度リム: Intensity 0 でテクスチャサンプルごとスキップ。
    UNITY_BRANCH
    if (_RimDepthIntensity > 0.0)
    {
        rim += CalculateDepthRim(screenUV, selfEyeDepth,
                                 s.detailNormalWS, mainLight.direction, mainLight.color);
    }

    // (2) フレネルリム: rimFresnel は GatherSurface で 1 回算出済み。
    UNITY_BRANCH
    if (_RimIntensity > 0.0)
    {
        float ndotlSpec = dot(s.detailNormalWS, mainLight.direction);
        float3 lightEnergy = mainLight.color * mainLight.distanceAttenuation;
        rim += CalculateRimLight(_RimColor.rgb, s.rimFresnel, _RimIntensity,
                                 lightEnergy, ndotlSpec, castShadow);
    }

    // (3) バックライトリム: 既定 OFF のライブ演出光。
    UNITY_BRANCH
    if (_BackRimEnable > 0.5)
    {
        rim += CalculateBackRim(s.detailNormalWS, s.NdotV, viewDirectionWS);
    }

    return rim;
}

#endif // IDOL_RIM_INCLUDED

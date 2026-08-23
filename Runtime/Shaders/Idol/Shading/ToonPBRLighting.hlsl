// ライト方向の上書き・1 灯分のシェーディング・間接光
//
// `ToonPBRCommon.hlsl` から切り出した（T-212）。**1 行も変えていない**
// ── include を展開し直して元のファイルとバイト一致することを確認済み。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  ライト方向の手動上書き（FR-33）
//
//  これは物理から意図的に外れる機能。背景と影の向きを食い違わせて、
//  絵として都合の良い位置に影を置くためのもの。既定は OFF。
//
//  マテリアル単位の値なので、複数キャラで別々の向きを指定できる。
//  グローバルにすると1体調整するたびに全員動いてしまう。
// ----------------------------------------------------------------------------
float3 ToonOverrideLightDir()
{
    float yaw   = radians(_LightOverrideYaw);
    float pitch = radians(_LightOverridePitch);
    float cp    = cos(pitch);

    // URP の light.direction は「光源へ向かうベクトル」。ここも同じ向きで返す。
    return normalize(float3(sin(yaw) * cp, sin(pitch), cos(yaw) * cp));
}

// ----------------------------------------------------------------------------
//  1灯分のシェーディング
// ----------------------------------------------------------------------------
/// <param name="diffuseL">
/// 拡散の伝達関数と顔 SDF に使う光の向き。通常は light.direction と同じものを渡す。
/// 影の向きだけ絵の都合で回したいとき（FR-33）に別の向きを入れる。
/// </param>
/// <param name="litOut">
/// 拡散の伝達関数を通したあとの遮蔽量（0 = 影の中）。間接光を影の内外で
/// 別扱いにするために外へ出す。**主光源の値だけを使うこと。**
/// 追加光源のぶんで上書きすると、光源が増えるたびに影の環境光が変わってしまう。
/// </param>
/// <param name="shadowColorScale">
/// 影色の適用量。**追加光源には `_AddLightShadowColor` を渡す。**
/// この関数はライトごとに `lerp(影色, アルベド, lit)` を返して**加算**されるので、
/// 当たっていない光源も影色ぶんの下駄を足す。背面の暖色リムを置くと、
/// 正面の影がその色に転ぶ（実測で R/B が 1.00 → 1.43）。
/// 既定 1 は従来どおり。0 にすると追加光源は「当たった所だけ」寄与する。
/// </param>
// ----------------------------------------------------------------------------
//  1 灯ぶんの光のエネルギー。リム（ライトごと・T-351）と共有するので
//  ここが唯一の出所（書き写すと片方だけずれる ── T-107）。
// ----------------------------------------------------------------------------
float3 ToonLightEnergy(Light light)
{
    return light.color * light.distanceAttenuation;
}

// 白飛び防止（T-350）。**拡散・透過・リムにだけ**上限を掛ける ── 鏡面は
// 「強い光源ほど鋭く光る」のが正しく、抑えると金属や瞳が死ぬ。
// Doll は伝達関数を通した後の拡散光を抑えるが、こちらは**光源側**を
// 抑える: NdotL の階調がそのまま残るので、上限に張り付いた面が
// のっぺり潰れない（上限値そのものは同じ意味で効く）。
float3 ToonDiffuseEnergy(float3 lightEnergy)
{
    float3 e = lightEnergy;
    UNITY_BRANCH
    if (_DiffuseLightLimit > 0.0)
        e = ApplyLuminanceClamp(lightEnergy, _DiffuseLightLimit);
    return e;
}

float3 ToonShadeLight(ToonSurface s, ToonContext c, Light light, float3 diffuseL,
                      float shadowColorScale, float attenAA,
                      out float litOut, out float castOut)
{
    float3 L = light.direction;   // 鏡面・透過に使う実際の向き
    float3 Ld = diffuseL;         // 拡散と顔 SDF に使う向き
    float3 N = c.N;
    float3 V = c.V;
    float3 H = SafeNormalize(L + V);

    // 拡散の伝達関数だけ平滑法線を使う。シワやファセットが陰のグラデーションに
    // 入り込んで境界が汚く割れるのを防ぐ。鏡面・リム・透過はディテール法線のまま
    // なので質感は失われない（未使用時は shadeN == N）。
    float NdotL  = dot(c.shadeN, Ld);       // 拡散の伝達関数用
    float NdotLs = saturate(dot(N, L));     // 鏡面の幾何項用（上書きの影響を受けない）
    float NdotH = saturate(dot(N, H));
    float VdotH = saturate(dot(V, H));
    float NdotV = c.NdotV;

    // --- 拡散 ---------------------------------------------------------------
    // 細かい凹凸の自己遮蔽。伝達関数に入る前に掛ける。
    float microShadow = ToonMicroShadow(NdotL, s.occlusion);

    // 「何かに遮られた」ぶんだけを別に持つ。マイクロシャドウは面のディテールで
    // あって遮蔽物ではないので入れない（FR-70）。
    float castAtten = light.shadowAttenuation;

    // **この光源の向きで NdotL の画面変化率を取る。**
    // 以前は c.edgeAA（主光源の向きで求めた値）を全光源で使い回していた。
    // d(N·L)/dx = (dN/dx)·L なので、L が変われば変化率も変わる ──
    // 別方向から当たる追加光源では、境界 AA の下限が過大にも過小にもなっていた。
    // 過小ならターミネータがジャギり、過大なら不必要に甘くなる。
    //
    // 主光源については fwidth(dot(N, L)) と**同じ値**になる（恒等変形）。
    float lightEdgeAA = (abs(dot(c.dNdx, Ld)) + abs(dot(c.dNdy, Ld))) * 0.5;

    float softness, rawT, castShadow;
    float lit = ToonLightResponse(NdotL, c.curvature, s.shadowOffset,
                                  light.shadowAttenuation * microShadow,
                                  castAtten, lightEdgeAA, attenAA, softness, rawT, castShadow);

#if defined(_SURFACETYPE_FACE)
    // 顔だけは法線を捨てて SDF で境界を決める。
    // 鼻の凹凸で影が割れるのを根本的に避けるため。
    float faceLit;

    // **FaceDirectionBinder が無いと _HeadForward はゼロのまま来る。**
    // normalize(0) が NaN を返し、NaN は乗算も lerp も素通りして最終色まで届く
    // ＝顔が丸ごと破綻する。コンポーネントの付け忘れは必ず起きるので、
    // シェーダー側で検知して通常の法線経路へ落とす。ダミーの直交系を入れるのは、
    // 分岐の中で NaN を「作らない」ため（後段で 0 を掛けても NaN は消えない）。
    float3 headFwd   = _HeadForward.xyz;
    float3 headRight = _HeadRight.xyz;
    float  bound = (dot(headFwd, headFwd) > 1e-6 && dot(headRight, headRight) > 1e-6) ? 1.0 : 0.0;

    // **Binder が無いときはオブジェクトの軸で代用する。**
    // これが無いと「焼いた顔 SDF があるのにコンポーネントを付けるまで
    // 一度も使われない」状態になる（実際そうなっていた）。
    // 頭の回転には追従しないので、首を振る演出では Binder が要る。
    // Unity に取り込んだ Humanoid は素体の +Z が正面、+X が右なのが通例。
    float3 objFwd   = mul((float3x3)UNITY_MATRIX_M, float3(0, 0, 1));
    float3 objRight = mul((float3x3)UNITY_MATRIX_M, float3(1, 0, 0));

    // スケール 0 のオブジェクトで normalize が NaN を返すのを塞ぐ。
    objFwd   = (dot(objFwd, objFwd)     > 1e-8) ? normalize(objFwd)   : float3(0, 0, 1);
    objRight = (dot(objRight, objRight) > 1e-8) ? normalize(objRight) : float3(1, 0, 0);

    headFwd   = (bound > 0.5) ? headFwd   : objFwd;
    headRight = (bound > 0.5) ? headRight : objRight;

    // Binder があれば常に有効。無ければトグル次第（既定 ON）。
    float headValid = max(bound, _FaceUseObjectAxis);

    {
        // --- 16bit 1ch（左右ミラー）。Idol の顔 SDF はこの 1 方式だけ（T-382）----
        //  R×256+G の 16bit パッキング。8bit の R 単独では閾値が約 0.7 度刻みの
        //  階段になり、ライトを回すと影の線がカクつく（T-345）。4ch 方式
        //  （右/左/上/下のスイープ・非対称な顔向け）は、16bit 1ch ＋ 距離場ブレンド
        //  ＋ Cast Shadow のベイクで品質が上回ったため撤去した。Doll は 4ch のまま。
        //  焼き方は Core の Documentation~/FACE_SDF_BAKING.md。
        // XZ に潰した後もゼロになりうる（頭ボーンが真上を向いている場合）。
        float3 fwd   = normalize(float3(headFwd.x,   0, headFwd.z)   + float3(0, 0, 1e-5));
        float3 right = normalize(float3(headRight.x, 0, headRight.z) + float3(1e-5, 0, 0));
        // 顔の影の形は「絵として置きたい向き」に従う。ここは Ld を使う。
        float3 lXZ   = normalize(float3(Ld.x, 0, Ld.z) + 1e-5);

        float FdotL = dot(fwd, lXZ);
        float RdotL = dot(right, lXZ);
        float threshold = 1.0 - (FdotL * 0.5 + 0.5) + _FaceShadowOffset;

        // **左右の切替は硬い分岐にしない（T-371）。** `RdotL < 0` で U を
        // ミラーするだけだと、光が真正面を横切る瞬間に左右のサンプルが
        // 瞬時に入れ替わり、段差として見える。以前は「正面は顔全面が光るので
        // 見えない」前提で済ませていたが、Cast Shadow をベイクした SDF は
        // 正面光でも鼻の影が残るため**実際に見える**（利用者報告）。
        // 両側をサンプルし、正面付近（|RdotL| < 0.15 ≈ 8.6 度）でクロスフェード
        // する。非ミラー側はフラグメントで取った値（c.faceSdf）をそのまま使い、
        // ミラー側だけここで 1 フェッチ足す。
        float side = smoothstep(-0.15, 0.15, RdotL);  // 1 = 右光 / 0 = 左光
        if (_FaceSDFFlipU > 0.5) side = 1.0 - side;

        float2 uvB = float2(1.0 - c.uv.x, c.uv.y);     // ミラー（左光用）
        float sdfA = c.faceSdf;
        float sdfB = ToonDecodeFaceSdf16(
            SAMPLE_TEXTURE2D_GRAD(_FaceSDFMap, sampler_FaceSDFMap, uvB, c.uvDx, c.uvDy).rg);
        float sdf = lerp(sdfB, sdfA, side);
        // ミラーした UV の変化率は厳密には別位置のものだが、同じテクスチャの
        // 同じスケールなので下限として使うぶんには差が出ない。
        float soft = max(softness, c.faceSdfAA);
        faceLit = smoothstep(threshold - soft, threshold + soft, sdf);
    }

    // **遮蔽項も掛けること。** SDF が置き換えるのは「面の向きによる陰」であって、
    // 遮蔽ではない。以前はマイクロシャドウが抜けており、
    // **顔だけ鼻の脇や顎の下の落ち込みが出なかった**（他のサーフェスタイプは効いていた）。
    //
    // 影の抽象化（ぼかし→しきい値の opening。T-366）は試作の上**不採用**:
    // しきい値はぼかし幅より広い影にしか opening として働かず、ぼかしきれない
    // 細い影では**輪郭を締め直す方向（逆効果）**に働いた（利用者実測）。
    // 顔の影の整形は Penumbra（ぼかし）の調整だけで行う。
    float shadowAtten = lerp(1.0, light.shadowAttenuation, _ReceiveShadowStrength)
                      * microShadow;
    faceLit *= shadowAtten;

    // **下向きの面は SDF から法線の陰影へ戻す（T-376・Doll と同じ仕組み）。**
    // SDF のスイープは水平面内で回すので光の仰角を知らない。顎の裏は法線が
    // ほぼ真下で、わずかな前向き成分だけで「ほぼ全方位で照らされる」と焼かれる。
    // 隣接する首（Default）は N·L で上からの光に正しく陰るため、下から覗くと
    // 「顎裏＝明・首＝陰」の段差が出ていた（利用者報告）。
    // 判定はオブジェクト空間の法線 Y（素体は直立が通例）。既定 Min -1 / Max 0 で
    // 真下 → 0、水平以上 → 1。Min==Max は smoothstep が 0 除算になるので離す。
    float normalOSY = TransformWorldToObjectDir(c.N).y;
    float sdfMask   = smoothstep(_FaceSDFBlendNormalMin,
                                 max(_FaceSDFBlendNormalMax, _FaceSDFBlendNormalMin + 1e-4),
                                 normalOSY);

    // headValid で殺す。Binder が無いキャラは SDF を使わず通常の陰影で出る。
    float faceBlend = _FaceFlatness * headValid * sdfMask;

    lit  = lerp(lit, faceLit, faceBlend);
    rawT = lerp(rawT, faceLit, faceBlend);
#endif

    // 正面・上向きの陰を持ち上げる（FR-31）。**伝達関数の出力に掛ける** ──
    float band = ToonTerminatorBand(rawT, softness);
    float3 diffuse = ToonDiffuseColor(s.diffuseColor, s.shadowColor * shadowColorScale, lit, band,
                                      ToonTerminatorFade(c.eyeDepth));

UNITY_BRANCH
if (_UseRampMap > 0.5)
{
    float rows = max(1.0, _RampRowCount);
    float index = (_RampIndexOverride >= 0.0)
                ? _RampIndexOverride
                : round(saturate(s.rampIndex) * (rows - 1.0));
    float2 rampUV = float2(lit, (index + 0.5) / rows);
    // 同じ理由で LOD 0 固定。U は光の当たり具合で、画面上の微分に意味が無い。
    // ミップに落ちるとランプが滲んで境界が甘くなるので、そもそも 0 が正しい。
    float3 ramp = SAMPLE_TEXTURE2D_LOD(_RampMap, sampler_RampMap, rampUV, 0).rgb;
    diffuse = lerp(diffuse, s.diffuseColor * ramp, _RampStrength);
}

    // **落ち影だけを別の色で濃くする（FR-70）。**
    // NdotL 由来の陰（ターミネータ）と、他の物体に遮られた影は絵としての役割が違う。
    // 前者は形を見せるためのもので、濃くすると立体感が潰れる。後者は空間の
    // 前後関係を見せるもので、濃い方が PBR の背景と並べたときに芯が出る。
    // 既定は強度 0 ＝ 従来どおり両者が同じ影色になる。
    diffuse = lerp(diffuse, diffuse * _CastShadowColor.rgb,
                   saturate(castShadow * _CastShadowColorStrength));

// **Face も肌。** 顔だけ皮下散乱が効かないと、ターミネータが冷たいまま残って
// 指や耳と質感が揃わない。マテリアルには値が入っているのに分岐から外れていた。
#if defined(_SURFACETYPE_SKIN) || defined(_SURFACETYPE_FACE)
    // 境界の内側だけ皮下散乱の色を混ぜる。
    //
    // **係数は saturate すること。** _SubsurfaceStrength の Range は 0..2 なので、
    // 境界の芯（band=1）かつ厚い部位（thickness=1）で 1 を超え、lerp が外挿になる。
    // 赤めの SubsurfaceColor だと Strength 1.55 あたりから **G/B が負** に振れ、
    // 耳や指先に黒い縁が出る。1 を超えるぶんは色の飽和として頭打ちにする。
    diffuse = lerp(diffuse, diffuse * _SubsurfaceColor.rgb,
                   saturate(band * _SubsurfaceStrength * s.thickness));
#endif

    // --- 鏡面 ---------------------------------------------------------------
    float3 specular = 0;

    // 鏡面が持ち去ったエネルギーの割合（輝度換算）。**実際に足した量**を入れる。
    // 髪・布の経路では 0 のまま ── あちらは自前の強度と正規化を持っていて、
    // ここで一律に引くと二重に減衰する。**未定義にしないよう必ず初期化する。**
    float specTaken = 0.0;

#if defined(_SURFACETYPE_HAIR)
    // 繊維方向とずらした接線はライトに依らない。フラグメントで1回だけ求めてある
    // （毛流れマップとシフトマップのフェッチ2枚 + atan2/sincos がライト数ぶん減る）。
    float3 T1 = c.hairT1;
    float3 T2 = c.hairT2;

    UNITY_BRANCH
    if (_HairAnisoGGXOn > 0.5)
    {
        // **マスクは両方のローブに掛ける。** 以前は副ローブにしか掛かっておらず、
        // NPR マップの R で「ここは光らせない」と塗っても主バンドが残った。
        // 束感（sparkle）は副ローブだけ ── 主バンドは細い芯なので割ると消える。
        float g1 = ToonStrandSpecularGGX(T1, N, V, L, _HairSmoothness1, _HairAnisotropy, c.specAAKernel)
                 * s.specMask;
        float g2 = ToonStrandSpecularGGX(T2, N, V, L, _HairSmoothness2, _HairAnisotropy, c.specAAKernel)
                 * s.specMask * c.hairSparkle;

        // ここだけ Kajiya-Kay と違って F を掛ける。GGX にした以上、
        // Default の鏡面と同じ明るさの尺度に乗せないと部位間で浮く。
        // そのぶん Kajiya-Kay と同じ Intensity では暗くなるので調整が要る。
        float3 Fh = ToonF_Schlick(s.f0, VdotH);
        specular = (_HairSpecColor1.rgb * g1 + _HairSpecColor2.rgb * g2)
                 * Fh * _HairSpecIntensity;
    }
    else
    {
        // Blinn 指数にも同じ AA を掛けてある（フラグメントで1回だけ。ライト非依存）。
        float exp1  = c.hairExp.x;
        float exp2_ = c.hairExp.y;

        // マスクは両方のローブに掛ける（上と同じ理由）。
        float s1 = ToonStrandSpecular(T1, V, L, exp1) * s.specMask;
        float s2 = ToonStrandSpecular(T2, V, L, exp2_) * s.specMask;

        // **副バンドは縁で強める。** 参照実装（EasyPBR の CalculateAnisotropicSpecular）
        // が持っている項で、これが無いと2本目が「ただの広い帯」になり、
        // シルエット際で毛束がふわっと光る感じが出ない。
        // 0.5〜1.0 の範囲なので、正面では半分、縁で全開。
        float hairFresnel = lerp(0.5, 1.0, pow(1.0 - VdotH, 4.0));
        s2 *= hairFresnel * c.hairSparkle;

        specular = (_HairSpecColor1.rgb * s1 + _HairSpecColor2.rgb * s2) * _HairSpecIntensity;
    }
#else
    float D = ToonD_GGX(NdotH, s.roughness);
    float Vis = ToonV_SmithGGX(NdotV, NdotLs, s.roughness);
    float3 F = ToonF_Schlick(s.f0, VdotH);
    // **倍率を掛ける。** これが無いと base GGX が実質 1.0 で出っぱなしになり、
    // Metallic が 0 でも「濡れたプラスチック」に見える。
    // Hair は自前の _HairSpecIntensity を持つのでこの経路に来ない。
    specular = D * Vis * F * NdotLs * s.specMask * _SpecularIntensity;

    // 拡散から引く量。F は VdotH 依存なので光源ごとに変わる。
    //
    // **「実際に足した割合」にすること（T-379）。** 以前は F × 倍率をそのまま
    // 使っていたが、(1) 逆光（NdotL ≤ 0）では鏡面を 1 つも足していないのに
    // 引いていた (2) 光源の真反対から見ると V ≈ −L で H がゼロに潰れ
    // VdotH → 0・F → 1 に張り付く (3) _SpecularIntensity 4 なら 4 倍で 1 を超える。
    // 3 つが重なるローアングルの逆光で `saturate(1 − 4) = 0` となり、
    // **影色ごと拡散が消えて靴が真っ黒**になっていた（利用者報告）。
    // 光の当たる面だけ（NdotLs でゲート）・割合は 1 で頭打ち。
    specTaken = saturate(dot(F, float3(0.2126, 0.7152, 0.0722)) * s.specMask * _SpecularIntensity)
              * NdotLs;

    // 直接光にも同じ補償を掛ける。IBL 側だけ補うと、光源が動いたときに
    // 直接光と映り込みで金属の明るさが食い違う。
    specular *= c.energyComp;   // ライト非依存。フラグメントで前計算
    specular = lerp(specular, specular * _SpecularTint.rgb, _SpecularTintStrength);

    // 2 ローブ目（T-369。Doll のデュアルローブから輸入）: シャープな芯の下に
    // 広いマットなにじみを敷く。肌のハイライトが「点」でなく「面」で光る定番。
    // F（フレネル）は f0 が同じ物性なので主ローブと共有する。Tint は主ローブの
    // 色付けなので通さず、2 ローブ目は自前の色を持つ。
    UNITY_BRANCH
    if (_SecSpecularIntensity > 0.0)
    {
        float sr     = 1.0 - _SecSmoothness;
        float rough2 = max(sr * sr, 0.002);
        float D2   = ToonD_GGX(NdotH, rough2);
        float Vis2 = ToonV_SmithGGX(NdotV, NdotLs, rough2);
        specular += D2 * Vis2 * F * NdotLs * s.specMask
                  * _SecSpecularIntensity * _SecSpecularColor.rgb;
    }

    // クリアコート。下地の鏡面を (1 - Fc) で減衰させてエネルギーを保存する。
    // 強度 0 のときは分岐ごと飛ぶのでコストは掛からない（T-037 の方針）。
    UNITY_BRANCH
    if (_ClearcoatStrength > 0.0)
    {
        float coatPerceptual = saturate(1.0 - _ClearcoatSmoothness);
        float coatRoughness  = max(coatPerceptual * coatPerceptual, 0.002);

        float LdotH = saturate(dot(L, H));
        float Dc = ToonD_GGX(NdotH, coatRoughness);
        float Vc = ToonV_Kelemen(LdotH);
        float Fc = (0.04 + 0.96 * pow(1.0 - LdotH, 5.0)) * _ClearcoatStrength;

        float3 coatTint = ToonIridescence(NdotV, _IridescenceIntensity,
                                         _IridescenceThickness, _IridescenceShift);

        // **下地は鏡面も拡散も (1 - Fc) で減衰させる。**
        // コートに当たった光は Fc が反射し、残りの (1 - Fc) だけが下地へ届く。
        // 鏡面だけ減らして拡散を素通しにしていたので、
        // **コメントは「エネルギーを保存する」と書いてあるのに半分しか実装されていなかった。**
        //
        // 過剰だった量（強度 1 のとき）:
        //   LdotH  0度 → 4.0%   45度 → 4.2%   60度 → 7.0%
        //           75度 → 25.4%   85度 → 64.9%
        //
        // 該当は目の3マテリアル（白目・瞳・ハイライト）。
        // 正面から見るぶんには 4% だが、目尻など視線が浅くなる所ほど浮いていた。
        float coatAtten = 1.0 - Fc;

        diffuse  *= coatAtten;
        specular *= coatAtten;
        specular += Dc * Vc * Fc * NdotLs * coatTint;
    }
#endif

#if defined(_SURFACETYPE_CLOTH)
    // 布の毛羽立ち。白いドレスの縁がふわっと明るくなるのはこれ。
    //
    // 織り方向に伸ばすのは、ハーフベクトルの接線成分を縮めることで行う。
    // Charlie の分布式には手を入れずに済み、異方性 0 で従来と完全に一致する。
    // 接線はメッシュのものを使う。専用のフローマップも考えられるが、
    // テクスチャ側の契約が増えるうえ手元に該当アセットが無いので見送った。
    float3 weave  = (_ClothTangentSwap > 0.5) ? c.B : c.T;
    float3 Hcloth = normalize(H - weave * dot(H, weave) * _ClothAnisotropy);
    float  NdotHc = saturate(dot(N, Hcloth));

    // シーンにも同じカーネルを掛ける。**布の皺は法線が画素内で最も振れる場所**で、
    // ここが生の粗さのままだと白いシャツで斑点になる。
    // **値はフラグメントで1回だけ求めてある**（ライトに依存しないため）。
    float Dc = ToonD_Charlie(NdotHc, c.sheenAlpha);
    float Vc = ToonV_Ashikhmin(NdotV, NdotLs);

    float3 sheenColor = _SheenColor.rgb * _SheenIntensity;

    // **sheen が持っていくぶん、下地を縮めてから足す。** 縮めないと
    // 布だけエネルギーが増える。既定は 0（従来どおり足すだけ）で、
    // 1 にすると glTF KHR_materials_sheen と同じ挙動になる。
    float sheenScale = c.sheenScale;   // ライト非依存。フラグメントで前計算

    diffuse  *= sheenScale;
    specular *= sheenScale;
    specular += Dc * Vc * sheenColor * NdotLs;
#endif

    // 窪みの底に鏡面を残さない。アルベド側は既に掛けてある。
    specular *= s.cavity;

    // 影の中で光らせない。ただし完全に殺すと硬く見えるので少し残す。
    //
    // **その下駄は落ち影には残さない。** 0.1 の残しは「ターミネータの鏡面が
    // 唐突に切れて硬く見える」のを避けるための演出であって、**遮蔽物に遮られた
    // 影には理由が無い**（直接光が本当に届いていないので鏡面も出ない）。
    // 落ち影を濃くする設定のときだけ下駄を抜く。強度 0 なら従来どおり 0.1。
    // 環境反射（間接光側）はここでは触らない ── 影の中でも映り込みは起きる。
    // **既定 0.1 は従来の焼き込み値そのもの。** 移行元は同じことを
    // `_SpecularShadeInfluence` というノブでやっており、184 マテリアル中 92 が
    // 既定から動かしている（T-201）。機能ではなく可動域だけを出す。
    float specFloor = _SpecShadowFloor * (1.0 - saturate(castShadow * _CastShadowColorStrength));
    specular *= lerp(specFloor, 1.0, lit);

    // --- 透過 (耳・指・薄い布) ---------------------------------------------
    float3 transmission = 0;
// Face も含める。頭メッシュには耳が入っており、逆光で赤く抜けるのは顔の見せ場。
#if defined(_SURFACETYPE_SKIN) || defined(_SURFACETYPE_FACE) || defined(_SURFACETYPE_CLOTH)
    // 光を法線（またはベイクした透過方向）で曲げてから裏面成分を取る。
    // 曲げないと透過がのっぺり均一になる。耳や指の「縁だけ赤く抜ける」感じは
    // このディストーションで出る（Barré-Brisebois の近似）。
    // **打ち消しを塞ぐ。** Distortion を 1 まで上げると、光源に背を向けた面
    // （sssDir が L と正反対 ＝ **まさに透過が最も効く向き**）で L + S が
    // ゼロベクトルになり normalize が NaN を返す。1 手前でも長さが 0.05 まで
    // 縮んで向きが浮動小数の誤差に支配され、耳の先がちらつく。
    // 打ち消したときは曲げない生のライト方向に落とす。
    float3 ltRaw  = L + c.sssDir * _TransmissionDistortion;
    float  ltLen2 = dot(ltRaw, ltRaw);
    float3 ltLight = (ltLen2 > 1e-4) ? ltRaw * rsqrt(ltLen2) : L;
    float back = pow(saturate(dot(V, -ltLight)), max(_TransmissionPower, 0.01));
    transmission = back * _TransmissionColor.rgb * _TransmissionStrength
                 * s.thickness * s.diffuseColor;
#endif

    // **鏡面が持ち去ったぶんを拡散から引く（既定 0 で従来どおり）。**
    // 間接光（FR-74）と同じ考え方だが、こちらは縁で最大 23% と見える量なので
    // 自動では入れずノブにしてある。判断の材料:
    //   VdotH 60度 → 1.7% / 75度 → 6.4% / 85度 → 16.2% / 89度 → 23.0%
    //   （f0 = 0.04・_SpecularIntensity 0.25 のとき）
    //
    // **引く量は「実際に足した量」。** F そのものではなく
    // `F × _SpecularIntensity` ── 絵の都合で鏡面を絞ってあるので、
    // 理論値を引くと足していないエネルギーまで削ることになる。
    diffuse *= saturate(1.0 - specTaken * _SpecEnergyConservation);

    float directAO = lerp(1.0, s.occlusion, _DirectOcclusion);

    litOut  = lit;
    castOut = castShadow;

    float3 lightEnergy = ToonLightEnergy(light);

    return (diffuse * directAO + transmission) * ToonDiffuseEnergy(lightEnergy)
         + specular * lightEnergy;
}

// ----------------------------------------------------------------------------
//  ライト色の整形（T-350。Doll から輸入・実装は Core の ConditionLightColor）
//
//  ステージ照明の濃い色や強度がキャラの色設計を壊すのを防ぐ防御層。
//  シェーディングの前にライト構造体の色を直接書き換えるので、陰影・リム・
//  グリッタまで一貫して整形後の色を見る。既定（1 / 1 / 0）では素通し。
// ----------------------------------------------------------------------------
void ToonConditionLight(inout Light light)
{
    UNITY_BRANCH
    if (_LightColorInfluence < 1.0 || _LightSaturationLimit < 1.0 || _LightMinBrightness > 0.0)
    {
        light.color = ConditionLightColor(light.color, _LightColorInfluence,
                                          _LightSaturationLimit, _LightMinBrightness);
    }
}

// ----------------------------------------------------------------------------
//  間接光
// ----------------------------------------------------------------------------
/// <param name="mainLit">
/// 主光源の遮蔽量（ToonShadeLight の litOut）。影の中の環境光だけを
/// 別扱いにするために要る。追加光源のぶんを渡さないこと。
/// </param>
float3 ToonShadeIndirect(ToonSurface s, ToonContext c, float mainLit, float mainCast)
{
    // 拡散: 方向性を潰すほどセル塗りの平面感が保たれる。
    // 向きはベントノーマル（遮蔽されていない方向）。壁際や脇の下で、
    // 本来光が来ない方向から間接光が入るのを防ぐ。未使用時は法線と同じ。
    // 真下向きの法線（顎の下・足裏・スカートの内側）で _AmbientFlatten が 0.5 だと
    // lerp の結果がゼロベクトルになり normalize が NaN を返す。NaN は最終色まで伝播する。
    float3 flatN = lerp(c.bentN, float3(0, 1, 0), _AmbientFlatten);
    float3 shNormal = (dot(flatN, flatN) < 1e-8) ? c.bentN : normalize(flatN);

#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    // Adaptive Probe Volumes。単一の環境プローブと違い、キャラが動いた先の
    // 場所ごとの間接光が拾える。屋内外をまたぐ移動でここが一番効く。
    // APV が無効なシーンでは URP 側が自動で環境プローブに落とす。
    float3 sh = SampleProbeVolumePixel(0, GetAbsolutePositionWS(c.positionWS),
                                       shNormal, c.V, c.positionSS);
#else
    float3 sh = SampleSH(shNormal);
#endif
    // 遮蔽は多重バウンス補正を通す。暗部がアルベドの色を保つ。
    float3 aoDiffuse = ToonAOMultiBounce(s.occlusion, s.diffuseColor);
    float3 indirectDiffuse = sh * s.diffuseColor * _AmbientIntensity * aoDiffuse;

#if defined(_SURFACETYPE_CLOTH)
    // 直接光と同じ縮小を環境光にも掛ける。片方だけだと、
    // 屋外のように環境光が支配的な絵で布だけ明るいままになる。
    // 値はフラグメントで1回求めたものを共有する（直接光側と必ず一致させる）。
    indirectDiffuse *= c.sheenScale;
#endif

    // 影の中の環境光を別扱いにする。一律で乗ると影が環境光の色で持ち上がって濁る。
    // 既定は白 / 1.0 なので、設定しなければ従来と完全に同じ値になる。
    float3 shadowAmbient = indirectDiffuse * _ShadowAmbientTint.rgb * _ShadowAmbientIntensity;
    indirectDiffuse = lerp(shadowAmbient, indirectDiffuse, mainLit);

    // **落ち影は環境光にも色を掛ける（FR-70）。** 拡散だけ着色しても、環境光が
    // 支配的な構成では効果が洗い流される。実測（Ambient 2 / 影の中 0.5 / 肌）で、
    // 拡散だけだとターミネータ比 0.74 止まり。環境光にも掛けると 0.54 になり、
    // 「落ち影だけ明確に濃い」という絵になる。
    indirectDiffuse = lerp(indirectDiffuse, indirectDiffuse * _CastShadowColor.rgb,
                           saturate(mainCast * _CastShadowColorStrength));

    // 鏡面: プローブから。ここが背景と繋がる主経路。
    float pr = lerp(s.perceptualRoughness, 1.0, _EnvSpecFlatten);

    float3 R = reflect(-c.V, c.N);
#if defined(_SURFACETYPE_HAIR)
    // 髪だけ反射ベクトルを繊維に沿って寝かせる。Kajiya-Kay 版ではここは等方のまま。
    UNITY_BRANCH
    if (_HairAnisoGGXOn > 0.5)
    {
        float3 strandWS = ToonHairStrandDir(c.T, c.B, c.uv, c.uvDx, c.uvDy);
        R = ToonAnisoReflectVector(c.N, c.V, strandWS, _HairAnisotropy, pr);
    }
#endif

    // 法線マップで反射ベクトルが面の裏側を向くと、そこに無いはずの映り込みが漏れる。
    // 面の地平線より下を向いたぶんを落とす（物理的な補正なのでプロパティは持たない）。
    //
    // **ボックス投影の前に取ること。** 投影後のベクトルは向きではなく
    // プローブ中心からの位置で、長さが部屋の大きさになる。内積が桁で大きくなり
    // saturate が常に 1 に張り付くので、箱を持つプローブでは補正が消えていた。
    // 投影自体はプローブごとに箱が違うので ToonSampleEnvSpecular の中でやる。
    float horizon = saturate(1.0 + dot(R, c.N));
    horizon *= horizon;

    float3 env = ToonSampleEnvSpecular(R, pr, c.positionWS, c.screenUV) * horizon;
    float3 envBRDF = ToonEnvBRDFMultiScatter(s.f0, s.perceptualRoughness, c.NdotV);

    // AO 直掛けではなく鏡面遮蔽を使う。狭い場所でも映り込みが残る。
    // 環境反射にも同じ遮蔽を掛ける。拡散だけ暗くすると、狭い場所で
    // 映り込みだけが residual に残って浮く。
    float specOcclusion = ToonSpecularOcclusion(c.NdotV, s.occlusion, s.roughness) * s.cavity;
    float3 indirectSpecular = env * envBRDF * _EnvSpecIntensity * specOcclusion;

    // **鏡面が持ち去ったぶんを拡散から引く。**
    // Fdez-Agüera の多重散乱モデルは鏡面側（FssEss + Fms*Ems）だけでなく
    // 拡散側にも `Edss = 1 - (FssEss + Fms*Ems)` を掛ける形で対になっている。
    // 鏡面だけ取り入れて拡散を素通しにしていたので、合計が 1 を超えていた。
    //
    // **理論値を丸ごと引かないこと。** 実際に足しているのは `_EnvSpecIntensity` 倍で、
    // 布 0.20 / その他 0.25 と絵の都合で絞ってある。理論値を引くと
    // **足していないエネルギーまで削って暗くなりすぎる。** 足した量と同じ量を引く。
    //
    // 現在の設定での影響（f0 = 0.04 の誘電体）:
    //   Smoothness 0.15 × EnvSpec 0.20 → 正面 0.4% / 縁 0.5%
    //   Smoothness 0.25 × EnvSpec 0.25 → 正面 0.6% / 縁 1.0%
    // 目には見えない量だが、`_EnvSpecIntensity` を物理値の 1.0 に近づけるほど
    // 効いてくる（滑らかな面の縁では理論値で 41% に達する）。
    //
    // **ノブは付けない。** 足したぶんを引くのに選択の余地は無く、
    // ノブは保存を壊す方向にしか使えない（シーンの `_SheenEnergyConservation` は
    // アルベドのフィットが近似なので調整の余地があり、事情が違う）。
    float specTaken = dot(envBRDF, float3(0.2126, 0.7152, 0.0722)) * _EnvSpecIntensity;
    indirectDiffuse *= saturate(1.0 - specTaken * specOcclusion);
#if defined(_SURFACETYPE_CLOTH)
    indirectSpecular *= c.sheenScale;
#endif

    // コートの映り込み。下地より鋭いので別 mip を引く。
    UNITY_BRANCH
    if (_ClearcoatStrength > 0.0)
    {
        float coatPr = saturate(1.0 - _ClearcoatSmoothness);

        // **底は saturate で守る。** 根本原因（`c.NdotV` が 1 を超えていた）は
        // 生成側で直したが、負の底の pow は NaN になるので二重に守っておく
        // ── ここは NaN が出ると瞳の中心が黒or白に飛ぶ、最も目立つ場所。
        float Fc = (0.04 + 0.96 * pow(saturate(1.0 - c.NdotV), 5.0)) * _ClearcoatStrength;

        float3 coatTint = ToonIridescence(c.NdotV, _IridescenceIntensity,
                                         _IridescenceThickness, _IridescenceShift);

        // 直接光側と同じく**下地は鏡面も拡散も減衰させる**。
        // 片方だけ減らすと、コートを付けた瞬間に下地が明るくなる方向へ動く。
        float coatAtten = 1.0 - Fc;

        indirectDiffuse  *= coatAtten;
        indirectSpecular *= coatAtten;
        indirectSpecular += ToonSampleEnvSpecular(R, coatPr, c.positionWS, c.screenUV) * Fc * coatTint
                          * _EnvSpecIntensity * specOcclusion;
    }

    return indirectDiffuse + indirectSpecular;
}


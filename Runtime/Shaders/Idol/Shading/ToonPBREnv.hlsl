// 環境光（プローブのブレンド・多重散乱の補償・AO・鏡面遮蔽）
//
// `ToonPBRCommon.hlsl` から切り出した（T-212）。**1 行も変えていない**
// ── include を展開し直して元のファイルとバイト一致することを確認済み。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  環境
// ----------------------------------------------------------------------------
float ToonRoughnessToMip(float perceptualRoughness)
{
    return perceptualRoughness * (1.7 - 0.7 * perceptualRoughness) * TOON_SPECCUBE_LOD_STEPS;
}

// ----------------------------------------------------------------------------
//  リフレクションプローブのボックス投影
//
//  プローブを無限遠として扱うと、室内で壁や床の映り込みが視点を動かしても
//  ついてこない。結果としてキャラだけが空間に接地していないように見える。
//  URP が供給する箱の情報で、反射先を実際の交点に付け替える。
//
//  URP の関数を呼ばず自前で持つのは、この種の API がバージョンで揺れるため（NFR-07）。
// ----------------------------------------------------------------------------
// 箱の情報を引数で受け取る。プローブ 0/1 と Forward+ のアトラスで使い回すため。
float3 ToonBoxProjectReflection(float3 reflectDir, float3 positionWS,
                                float4 probePosition, float4 boxMin, float4 boxMax)
{
    // **単一 return に畳むこと。** UNITY_BRANCH の中から早期 return すると
    // fxc が「未初期化の可能性がある」と警告する（実機で実際に出た）。
    float3 result = reflectDir;

#if defined(_REFLECTION_PROBE_BOX_PROJECTION)
    // w は投影を持つプローブのときだけ正になる（URP 側がそう立てる）。
    UNITY_BRANCH
    if (probePosition.w > 0.0)
    {
        // 反射方向の成分がちょうど 0 だと、割った結果が -inf になって min を汚し、
        // 交点が飛ぶ。床の真上向き反射など軸に乗った場合に起きる。
        // URP 本体の BoxProjectedCubemapDirection も素で割っていて同じ穴がある。
        //
        // **床を張るときに符号を落とさないこと。** 以前は `< 1e-5 ? 1e-5 : ...` で、
        // -1e-6 のような**微小な負の成分が正に化けていた。** 符号は次の行で
        // 「箱のどちら側の面と交わるか」を決めるので、反転すると逆の面を見に行く。
        // 実害は出にくい（その軸の t は巨大になり min が別の軸を選ぶ）が、
        // 箱が薄い軸ではその巨大な t が最小になりうる。
        // 0 は正側に倒す ── sign() だと 0 が 0 のままで割り算が戻ってしまう。
        float3 sgn = (reflectDir >= 0.0) ? 1.0 : -1.0;
        float3 dir = sgn * max(abs(reflectDir), 1e-5);

        // 反射方向の符号で、箱のどちら側の面と交わるかが決まる。
        float3 boxMinMax = (dir > 0.0) ? boxMax.xyz : boxMin.xyz;
        float3 t    = (boxMinMax - positionWS) / dir;
        float  dist = min(min(t.x, t.y), t.z);

        // 進める向きは元のベクトル。逃がした dir は交点距離を出すためだけに使う。
        result = (positionWS - probePosition.xyz) + reflectDir * dist;
    }
#endif
    return result;
}

float3 ToonSampleCubeProbe(TEXTURECUBE_PARAM(cube, cubeSampler), float4 hdr,
                           float3 dir, float mip)
{
    float4 enc = SAMPLE_TEXTURECUBE_LOD(cube, cubeSampler, dir, mip);
#if defined(UNITY_USE_NATIVE_HDR)
    return enc.rgb;
#else
    return DecodeHDREnvironment(enc, hdr);
#endif
}

// ----------------------------------------------------------------------------
//  リフレクションプローブのブレンド (FR-64)
//
//  プローブを1つしか見ないと、影響範囲の境界を跨いだ瞬間に映り込みが丸ごと
//  差し替わる。室内から屋外へ歩かせると必ず目に付く。重なりで重み付けして混ぜる。
//
//  **URP 17 では経路が2つあり中身が別物。** Forward+ はプローブを八面体アトラスに
//  詰めてクラスタから引く。Forward は unity_SpecCube0/1 の2枚しか持たない。
//  NFR-05（両モードで同じ絵）を守るため両方書く。
//
//  **キーワードは足していない。** URP の _REFLECTION_PROBE_BLENDING を宣言すると
//  ForwardLit のバリアントが倍になる。常にこの経路を通し、プローブが1つしか
//  無ければ重み 1 の単一サンプルに畳まれる（従来と同じ結果）。
//
//  回転プローブ (REFLECTION_PROBE_ROTATION) は非対応。キーワードを増やす割に
//  キャラクターシェーダーで効く場面が無い。必要になったら足す。
// ----------------------------------------------------------------------------

// 影響範囲の内側で 1、境界の blendDistance ぶん手前から 0 へ落ちる。
float ToonProbeWeight(float3 positionWS, float4 boxMin, float4 boxMax)
{
    // URP 本体は素で割っている。blendDistance 0 のプローブで NaN になるので塞ぐ。
    float3 w = min(positionWS - boxMin.xyz, boxMax.xyz - positionWS) / max(boxMax.w, 1e-4);
    return saturate(min(w.x, min(w.y, w.z)));
}

#if USE_CLUSTER_LIGHT_LOOP && CLUSTER_HAS_REFLECTION_PROBES
// 八面体アトラスから1プローブぶん引く。
// アトラス自体はミップを持たず、粗さ違いが別の矩形として並んでいるので、
// 隣り合う2枚を引いて自前で混ぜる。1プローブあたり 7 段（URP 側の固定値）。
float3 ToonSampleProbeAtlas(uint probeIndex, float3 dir, float mip)
{
    float maxMip = abs(urp_ReflProbes_ProbePosition[probeIndex].w) - 1.0;
    float probeMip = clamp(mip, 0.0, max(maxMip, 0.0));

    float2 uv = saturate(PackNormalOctQuadEncode(dir) * 0.5 + 0.5);

    float mip0 = floor(probeMip);
    // mip0 が最上段のとき +1 はもう次のプローブの領域。重みは 0 だが読ませない。
    float mip1 = min(mip0 + 1.0, max(maxMip, 0.0));

    float4 so0 = urp_ReflProbes_MipScaleOffset[probeIndex * 7 + (uint)mip0];
    float4 so1 = urp_ReflProbes_MipScaleOffset[probeIndex * 7 + (uint)mip1];

    float3 c0 = SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, sampler_LinearClamp,
                                     uv * so0.xy + so0.zw, 0.0).rgb;
    float3 c1 = SAMPLE_TEXTURE2D_LOD(urp_ReflProbes_Atlas, sampler_LinearClamp,
                                     uv * so1.xy + so1.zw, 0.0).rgb;
    return lerp(c0, c1, probeMip - mip0);
}
#endif

float3 ToonSampleEnvSpecular(float3 reflectDir, float perceptualRoughness,
                             float3 positionWS, float2 screenUV)
{
    float  mip = ToonRoughnessToMip(perceptualRoughness);
    float3 irradiance = 0.0;
    float  totalWeight = 0.0;

#if USE_CLUSTER_LIGHT_LOOP && CLUSTER_HAS_REFLECTION_PROBES
    uint probeIndex;
    ClusterIterator it = ClusterInit(screenUV, positionWS, 1);

    // 重みが埋まったら打ち切る。URP 本体と同じで、重要度順に並んでいる前提。
    [loop] while (ClusterNext(it, probeIndex) && totalWeight < 0.99)
    {
        probeIndex -= URP_FP_PROBES_BEGIN;

        float weight = ToonProbeWeight(positionWS,
                                       urp_ReflProbes_BoxMin[probeIndex],
                                       urp_ReflProbes_BoxMax[probeIndex]);
        weight = min(weight, 1.0 - totalWeight);

        float3 dir = ToonBoxProjectReflection(reflectDir, positionWS,
                                              urp_ReflProbes_ProbePosition[probeIndex],
                                              urp_ReflProbes_BoxMin[probeIndex],
                                              urp_ReflProbes_BoxMax[probeIndex]);

        irradiance  += weight * ToonSampleProbeAtlas(probeIndex, dir, mip);
        totalWeight += weight;
    }
#else
    // Forward。2枚しか無いので、どちらが主役かを決めてから重みを配る。
    // 重要度が高い方、同じなら影響範囲が小さい方が主役（URP と同じ規則）。
    float3 size0 = unity_SpecCube0_BoxMax.xyz - unity_SpecCube0_BoxMin.xyz;
    float3 size1 = unity_SpecCube1_BoxMax.xyz - unity_SpecCube1_BoxMin.xyz;
    float  volumeDiff    = dot(size0, size0) - dot(size1, size1);
    float  importanceSign = unity_SpecCube1_BoxMin.w;

    bool dominant0 = importanceSign > 0.0 || (importanceSign == 0.0 && volumeDiff < -1e-4);
    bool dominant1 = importanceSign < 0.0 || (importanceSign == 0.0 && volumeDiff >  1e-4);

    float w0 = ToonProbeWeight(positionWS, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
    float w1 = ToonProbeWeight(positionWS, unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);

    // 主役でない方は、主役が空けた分しか取れない。
    w0 = dominant1 ? min(w0, 1.0 - w1) : w0;
    w1 = dominant0 ? min(w1, 1.0 - w0) : w1;

    // どちらも主役でないと合計が 1 を超えうる。超えたときだけ正規化する。
    float sum = max(w0 + w1, 1.0);
    w0 /= sum;
    w1 /= sum;

    UNITY_BRANCH
    if (w0 > 0.01)
    {
        float3 dir = ToonBoxProjectReflection(reflectDir, positionWS,
                                              unity_SpecCube0_ProbePosition,
                                              unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
        irradiance += w0 * ToonSampleCubeProbe(TEXTURECUBE_ARGS(unity_SpecCube0, samplerunity_SpecCube0),
                                               unity_SpecCube0_HDR, dir, mip);
    }

    UNITY_BRANCH
    if (w1 > 0.01)
    {
        float3 dir = ToonBoxProjectReflection(reflectDir, positionWS,
                                              unity_SpecCube1_ProbePosition,
                                              unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);
        // unity_SpecCube1 は専用サンプラを持たない。URP 本体も 0 番のを流用している。
        irradiance += w1 * ToonSampleCubeProbe(TEXTURECUBE_ARGS(unity_SpecCube1, samplerunity_SpecCube0),
                                               unity_SpecCube1_HDR, dir, mip);
    }

    totalWeight = w0 + w1;



#endif

    // 余った重みは空に返す。プローブの影響範囲の外側で真っ黒にならないように。
    // _GlossyEnvironmentCubeMap は URP が毎フレーム無条件に配っている
    // （中身は ReflectionProbe.defaultTexture ＝ スカイボックス）。
    UNITY_BRANCH
    if (totalWeight < 0.99)
    {
        irradiance += (1.0 - totalWeight)
                    * ToonSampleCubeProbe(TEXTURECUBE_ARGS(_GlossyEnvironmentCubeMap,
                                                           sampler_GlossyEnvironmentCubeMap),
                                          _GlossyEnvironmentCubeMap_HDR, reflectDir, mip);
    }

    return irradiance;
}

// 分割和近似の DFG 項（Karis の解析フィット）。LUT を持たずに済む。
//
// **渡すのは perceptualRoughness（アーティストが触る 0..1）。** alpha ではない。
// このフィットは UE4 の EnvBRDFApprox がそのまま出所で、UE4 の Roughness は
// 知覚粗さ。alpha（= perceptualRoughness²）を渡すと常により滑らかな面として
// 評価され、誘電体の環境鏡面が明るく出る。特に B 項（f0 に依らない下駄）が
// 効く**斜めの角度で酷く**、粗さ 0.5 の縁で 2.9 倍まで持ち上がっていた。
// 金属（f0=1）は多重散乱の項が必ず 1 に畳むので影響を受けない。
float2 ToonEnvBRDF_AB(float perceptualRoughness, float NdotV)
{
    const float4 c0 = float4(-1.0, -0.0275, -0.572,  0.022);
    const float4 c1 = float4( 1.0,  0.0425,  1.040, -0.040);
    float4 r = perceptualRoughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
    return float2(-1.04, 1.04) * a004 + r.zw;
}

float3 ToonEnvBRDFApprox(float3 f0, float perceptualRoughness, float NdotV)
{
    float2 AB = ToonEnvBRDF_AB(perceptualRoughness, NdotV);
    return max(f0 * AB.x + AB.y, 0.0);
}

// ----------------------------------------------------------------------------
//  多重散乱の補償 (Fdez-Agüera の単項近似)
//
//  GGX は微小面での反射を1回しか数えないので、粗い面ほどエネルギーが欠ける。
//  金属が粗くなるほど暗く濁るのはこれが原因で、補わないと背景の金属と質感が揃わない。
//  拡散側の様式化には一切触らないので、この設計方針とは衝突しない。
// ----------------------------------------------------------------------------
float3 ToonEnvBRDFMultiScatter(float3 f0, float perceptualRoughness, float NdotV)
{
    float2 AB = ToonEnvBRDF_AB(perceptualRoughness, NdotV);

    // 粗さ 0.9 を超えると B が -0.002 ほど負に沈む（フィットの都合）。
    // f0 を 0 まで下げたマテリアルで環境鏡面が負になるので床を張る。
    float3 FssEss = max(f0 * AB.x + AB.y, 0.0);

    float  Ess  = AB.x + AB.y;          // 単散乱で出ていくエネルギー
    float  Ems  = 1.0 - Ess;            // 取りこぼし
    float3 Favg = f0 + (1.0 - f0) / 21.0;
    float3 Fms  = FssEss * Favg / (1.0 - Ems * Favg);

    return FssEss + Fms * Ems * _EnergyCompensation;
}

// 直接光側の補償倍率。IBL と同じ DFG を使うので両者で明るさが揃う。
float3 ToonEnergyCompensation(float3 f0, float perceptualRoughness, float NdotV)
{
    float2 AB = ToonEnvBRDF_AB(perceptualRoughness, NdotV);

    // 割る相手は単散乱の指向性アルベド Ess = A + B。
    // B 項だけで割る書き方が出回っているが、この解析フィットでは B が
    // 正面付近で 0.001 台まで落ち、粗さ 1 では負になる。誘電体でも数十倍に
    // 跳ねて鏡面が白飛びする。Ess なら 1.00〜1.05 の範囲に収まる。
    float  Ess  = max(AB.x + AB.y, 1e-3);
    float3 comp = 1.0 + f0 * (1.0 / Ess - 1.0);

    return lerp(1.0, comp, _EnergyCompensation);
}

// ----------------------------------------------------------------------------
//  AO の多重バウンス補正 (Jimenez らの GTAO 論文の閉形式)
//
//  AO を素直に掛けると、遮蔽された場所が「灰色〜黒」に落ちる。実際には
//  周囲の面で何度も跳ね返るので、暗部はアルベドの色を帯びる。白い布は
//  灰色にならず白いまま暗くなり、赤い布は赤いまま暗くなる。
//
//  係数は論文の公開値。自前で当てはめたものではない。
// ----------------------------------------------------------------------------
/// <summary>
/// AO の多重バウンス補正（Jimenez et al. / Activision GTAO）。
/// 原典の係数と一致していることを確認済み。暗部がアルベドの色を保つ。
/// visibility は 0..1 の遮蔽量（1 = 遮蔽なし）。**負を入れないこと**（呼び出し側で saturate 済み）。
/// </summary>
float3 ToonAOMultiBounce(float visibility, float3 albedo)
{
    float3 a =  2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c =  2.7552 * albedo + 0.6903;

    float3 bounced = max(visibility, ((visibility * a + b) * visibility + c) * visibility);
    return lerp(visibility.xxx, bounced, _AOMultiBounce);
}

// ----------------------------------------------------------------------------
//  鏡面遮蔽 (Lagarde)
//
//  AO をそのまま鏡面に掛けると、狭い場所の映り込みが不自然に消える。
//  視線と粗さを見て「鏡面がどれだけ遮られるか」を別に出す。
// ----------------------------------------------------------------------------
/// <summary>
/// 鏡面遮蔽（Lagarde / Frostbite の式）。原典と一致していることを確認済み。
/// </summary>
/// <param name="roughness">
/// **alpha（= perceptualRoughness²）を渡す。** 原典の表記は `roughness` だけで
/// 単位が曖昧だが、この関数が表しているのは「鏡面ローブの広がりに応じて AO を
/// どれだけ効かせるか」で、ローブ幅を直接決めているのは alpha の方。
/// 粗い側ではどちらを渡しても結果は AO そのもので変わらない。差が出るのは
/// 滑らかな面だけで、Smoothness 0.9 で 0.863（alpha）対 0.757（perceptual）。
/// alpha の方が遮蔽が弱く、鏡のような面ほど AO の影響を受けないという理屈に合う。
/// </param>
float ToonSpecularOcclusion(float NdotV, float ao, float roughness)
{
    return saturate(pow(abs(NdotV + ao), exp2(-16.0 * roughness - 1.0)) - 1.0 + ao);
}

/// <summary>
/// MatCap（視線空間の法線で引く球状ライティング）。**加算のアクセントに限る。**
///
/// 入力: N はワールド法線、L は主光源のワールド方向。出力は加算する線形色。
///
/// **乗算モードは実装しない。** このシェーダーは「環境光はリフレクションプローブと
/// SH から取る。ここがキャラと背景を繋ぐ主経路」と決めている。乗算の MatCap は
/// その結果を上書きできてしまい、キャラだけが背景の環境から切り離される。
/// 加算なら**物理の上に載る**だけなので、主経路は保たれる。
/// 移植元は Add / Multiply を選べるが、Multiply の materials は移行時に落ちる。
///
/// **光の向きに合わせて回せる。** MatCap の古典的な弱点は「カメラに貼り付いて
/// 見える」こと ── 光源が動いてもハイライトの位置が変わらない。
/// 画面内の光の向きへ回すと、光源に追従しているように見える。0 で従来どおり。
/// </summary>
float3 ToonMatCap(float3 N, float3 L)
{
    float3x3 worldToView = (float3x3)GetWorldToViewMatrix();
    float2 nvs = mul(worldToView, N).xy;

    UNITY_BRANCH
    if (_MatCapLightAlign > 0.0)
    {
        float2 lvs = mul(worldToView, L).xy;
        float  len = length(lvs);
        // **長さ 0 を割らない。** 光源が真正面／真後ろだと画面内の向きが決まらない。
        if (len > 1e-4)
        {
            float2 dir = lvs / len;
            float2x2 rot = float2x2(dir.y, dir.x, -dir.x, dir.y);
            nvs = lerp(nvs, mul(rot, nvs), _MatCapLightAlign);
        }
    }

    float2 uv = nvs * 0.5 + 0.5;
    float3 tex = SAMPLE_TEXTURE2D(_MatCapTex, sampler_MatCapTex, uv).rgb;
    return tex * _MatCapColor.rgb * _MatCapIntensity;
}

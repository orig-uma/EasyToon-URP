// 鏡面 BRDF（GGX / Charlie / Kajiya-Kay）・クリアコート・スペキュラ AA
//
// `ToonPBRCommon.hlsl` から切り出した（T-212。当時バイト一致を確認）。
// T-340 で GGX の純関数 3 つ（D / V / F）の本体を EasyShaderCore へ寄せ、
// Toon* は 1 行の前方転送になった（実装が同値だったため）。名前を残すのは、
// 静的検査（E008〜E012 / W108）が Toon 接頭辞だけを検査対象にするためと、
// 呼び出し側を 1 文字も触らないため。Charlie / Kajiya-Kay / クリアコートは
// Core に受け皿が無いので自前のまま。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  鏡面反射 BRDF
// ----------------------------------------------------------------------------
/// <param name="roughness">**alpha（= perceptualRoughness²）。** GGX の分布式は
/// alpha でパラメータ化されている。perceptual を渡すと面が滑らかすぎる評価になる。</param>
float ToonD_GGX(float NdotH, float roughness)
{
    // 本体は EasyShaderCore の EasyPBR_D_GGX（同値実装）。分母を
    // max(PI·d², 1e-12) の下限で挟む経緯と数値検証（滑らかな面で 1e-7 の
    // 下駄が山を潰す表）は、あちらのコメントに同内容で残っている。T-340 で転送化。
    return EasyPBR_D_GGX(NdotH, roughness);
}

/// <summary>
/// Smith 可視項。**高さ相関の厳密形**（Heitz）。
///
/// **以前は Karis の近似形（sqrt 無し）だった。** 実測すると厳密形より
/// **常に暗く**、RMS で 13%・最悪 27% ずれていた（T-214。alpha 0.06〜0.25 ＝
/// Smoothness 0.5〜0.76 の帯が最も大きい）。
///
/// **同じシェーダーの中で髪と体の式が食い違っていた。**
/// `ToonV_SmithGGXAniso`（髪）は Filament の厳密形で、異方性 0 にすると
/// この式と 0.7% 以内で一致する。近似形のままだと**同じ物理状況で
/// 体だけ 13% 暗い**という、絵からは原因が読めない差になる。
///
/// 「BRDF は物理ベースのまま維持する」という設計思想に照らして厳密形を採った
/// （T-246）。代償は sqrt が 2 回 ── ライトごと・画素ごと。
///
/// **1/(4·NdotL·NdotV) を内包している**ので、呼び出し側で割らないこと。
/// D * V * F * NdotL がそのまま鏡面の値になる。
/// </summary>
/// <param name="roughness">**alpha。** D_GGX と同じ単位を渡すこと。</param>
float ToonV_SmithGGX(float NdotV, float NdotL, float roughness)
{
    // 本体は EasyShaderCore の EasyPBR_V_SmithGGX（同値実装）。**引数順が逆**
    // （あちらは NdotL, NdotV の順）。式は対称構造なので値は変わらないが、
    // 書き間違いは E011/E012 と hlsl_compile --cost の不一致が捕まえる。T-340 で転送化。
    return EasyPBR_V_SmithGGX(NdotL, NdotV, roughness);
}

// ----------------------------------------------------------------------------
//  クリアコート（二層目の鏡面）
//
//  漆・真珠・濡れた唇のような「透明な膜が上に乗っている」質感。
//  下地とは別の粗さを持つ薄い層を1枚重ねる。IOR は 1.5 固定で f0 = 0.04。
//
//  可視性項は Kelemen の近似。二層目は薄いので Smith まで要らない。
//  参考画像の真珠ビーズや金具の「濡れた」ハイライトはこれで出る。
// ----------------------------------------------------------------------------
/// <summary>
/// 薄膜干渉のティント。位相をコサインパレットで RGB に散らす。
/// 真珠・シャボン玉・玉虫塗りの「見る角度で色が回る」やつ。
/// intensity 0 で白（＝色が付かない）。
/// </summary>
float3 ToonIridescence(float NdotV, float intensity, float thickness, float shift)
{
    // 斜めから見るほど光路長が伸びる＝位相が進む。
    float phase = thickness * (1.0 - NdotV) + shift;
    float3 tint = 0.5 + 0.5 * cos(TOON_PI * 2.0 * (phase + float3(0.0, 0.33, 0.67)));
    return lerp(float3(1, 1, 1), tint, intensity);
}

/// <summary>Kelemen 可視項 1/(4·LdotH²)。クリアコート用。粗さを取らない。</summary>
float ToonV_Kelemen(float LdotH)
{
    return 0.25 / max(LdotH * LdotH, 1e-4);
}

float3 ToonF_Schlick(float3 f0, float VdotH)
{
    // 本体は EasyShaderCore の EasyPBR_F_Schlick（同値実装・**引数順が逆**）。T-340 で転送化。
    return EasyPBR_F_Schlick(VdotH, f0);
}

// 布用 (Estevez & Kulla の Charlie 分布)
float ToonD_Charlie(float NdotH, float roughness)
{
    float invAlpha = 1.0 / max(roughness, 0.002);
    float cos2h = NdotH * NdotH;
    float sin2h = max(1.0 - cos2h, 0.0078125);
    return (2.0 + invAlpha) * pow(sin2h, invAlpha * 0.5) / (2.0 * TOON_PI);
}

float ToonV_Ashikhmin(float NdotV, float NdotL)
{
    // **下駄ではなく下限**（E009 と同じ理由。実害は小さいが作法を揃える）。
    return 1.0 / max(4.0 * (NdotL + NdotV - NdotL * NdotV), 1e-5);
}

/// <summary>
/// Charlie sheen の指向性アルベド E(NdotV, roughness)。
/// 「その角度で sheen が入射エネルギーの何割を反射するか」。
///
/// **sheen は下地に足すだけだとエネルギーが増える。** レシピ値
/// （Roughness 0.30 / Intensity 0.7 / 白）でも縁で 0.43、つまり下地が
/// 100% 残ったまま 43% が上乗せされていた。glTF の KHR_materials_sheen も
/// Filament も、下地を (1 - sheenColor * E) で縮めてから足す。
///
/// 本来は LUT テクスチャで持つものだが、このリポジトリではバイナリを
/// 作らない方針なので、Charlie 分布 + Ashikhmin 可視項の半球積分を
/// 数値で解いて多項式に当てた。**最大誤差 0.044 / RMS 0.005**（粗さ 0.1〜1.0）。
/// </summary>
float ToonSheenAlbedo(float NdotV, float roughness)
{
    float x = 1.0 - saturate(NdotV);
    float q = 1.0 / (1.0 + roughness);

    // 各行が x の次数、各列が q の次数（0,1,2）。
    const float3 k0 = float3( 0.36875,  -0.49901,   0.10783);
    const float3 k1 = float3(-1.56054,   5.79214,  -4.68064);
    const float3 k2 = float3( 3.67103, -11.63878,  10.60749);
    const float3 k3 = float3(-7.33362,  22.62380, -19.77470);
    const float3 k4 = float3( 6.54125, -20.30629,  17.53108);

    float3 qv = float3(1.0, q, q * q);

    // x について Horner で畳む。
    float e = dot(k4, qv);
    e = dot(k3, qv) + x * e;
    e = dot(k2, qv) + x * e;
    e = dot(k1, qv) + x * e;
    e = dot(k0, qv) + x * e;

    return saturate(e);
}

/// <summary>
/// 髪の繊維方向。既定はメッシュの接線だが、**UV がミラーされている髪は
/// 接線の符号が反転してエンジェルリングが割れる。** 焼いた毛流れマップで
/// 上書きできるようにする。
///
/// マップは倍角エンコード（R=cos2θ, G=sin2θ を 0..1 に、B=信頼度）。
/// 倍角にするのは、ミラーで向きが 180 度反転しても同じ値になるため。
/// EasyPBR のベイカーが焼く HairFlow がそのまま使える。
/// </summary>
float3 ToonHairStrandDir(float3 T, float3 B, float2 uv, float2 uvDx, float2 uvDy)
{
    float2 fv = float2(1.0, 0.0);   // 強度 0 のときは接線そのもの

    UNITY_BRANCH
    if (_HairFlowStrength > 0.0)
    {
        // 光源ループから呼ばれるので暗黙 LOD は使えない（呼び出し側で取った微分を使う）。
        float3 hf = SAMPLE_TEXTURE2D_GRAD(_HairFlowMap, sampler_HairFlowMap,
                                          uv, uvDx, uvDy).rgb;
        fv = lerp(fv, float2(hf.r * 2.0 - 1.0, hf.g * 2.0 - 1.0),
                  saturate(hf.b * _HairFlowStrength));
    }

    // **向きが原点に潰れたときの `atan2(0,0)` は未定義。**
    //
    // 作り物の話ではない ── フローマップの「ここは向きが決まらない」は
    // **RG = (0.5, 0.5)**、つまり `dir = (0, 0)` で表す。旋毛の中心や
    // 毛流れが交差するところに必ず現れる。`_HairFlowStrength` は
    // Range(0,8) まで振れるので、信頼度 `hf.b` が 0.125 を超えれば
    // `saturate()` が 1 に飽和し、lerp が完全にそちらへ寄って長さ 0 になる。
    //
    // 返り値は環境依存で、0 が返る実装もあれば NaN が返る実装もある。
    // NaN なら sincos → 接線フレーム → **髪のハイライトに黒い穴が開く**。
    // しかも旋毛の一点だけなので、原因を辿るのがきわめて難しい。
    //
    // 潰れていたら接線そのもの（theta = 0）へ戻す ── 「向きが決まらない」の
    // 意味としても正しい。
    float theta = (dot(fv, fv) > 1e-12) ? (0.5 * atan2(fv.y, fv.x)) : 0.0;
    if (_HairTangentSwap > 0.5) theta += TOON_PI * 0.5;

    float s, cth;
    sincos(theta, s, cth);
    return normalize(T * cth + B * s);
}

// 髪用 (Kajiya-Kay)
float3 ToonShiftTangent(float3 T, float3 N, float shift)
{
    return normalize(T + N * shift);
}

float ToonStrandSpecular(float3 T, float3 V, float3 L, float exponent)
{
    // **SafeNormalize を使うこと。** L が V の真裏を向くと L + V がゼロになり、
    // 素の normalize は NaN を返す。NdotL が 0 でも **NaN は 0 を掛けても NaN**
    // なので、そのまま画面に出る。URP 本体の BRDF.hlsl も SafeNormalize を使っている。
    float3 H     = SafeNormalize(L + V);
    float  dotTH = dot(T, H);
    float  sinTH = sqrt(max(0.0, 1.0 - dotTH * dotTH));
    float  atten = smoothstep(-1.0, 0.0, dotTH);
    return atten * pow(sinTH, exponent);
}

// 髪用 (異方性 GGX)
//
//  Kajiya-Kay は直接光にしか効かない。異方性 GGX にすると同じ異方性を
//  環境反射にも掛けられるので、IBL まで筋状に伸びる（FR-23）。
//  ロブの構成（2層・シフト・色）は Kajiya-Kay 版と揃えてあるので、
//  キーワードを切り替えて同じ設定のまま見比べられる。
float ToonD_GGXAniso(float TdotH, float BdotH, float NdotH, float at, float ab)
{
    float  a2 = at * ab;
    float3 v  = float3(ab * TdotH, at * BdotH, a2 * NdotH);
    float  s  = dot(v, v);
    // **下限が大きすぎると山ごと消える。** `max(s, 1e-7)` だった。
    // 山（TdotH = BdotH = 0, NdotH = 1）では s = a2² なので、
    // 鋭いハイライトほど s が小さくなり **1e-7 のほうが支配的になる**:
    //
    //   Primary Smoothness 0.80 → 山 552.6 が 552.6      （素通り）
    //   Primary Smoothness 0.90 → 山 8,841.9 が **1.49** （0.0002 倍）
    //   Primary Smoothness 0.95 → 山 70,735 が **0.003** （0.00004 倍）
    //
    // **0.80 は無事で 0.90 で消える**ので、スライダを上げると
    // ハイライトが鋭くなるどころか**消える**という挙動になっていた。
    // 等方 GGX の同じ形（T-278）より境界が手前にあり、こちらの方が踏みやすい。
    //
    // at / ab の下限 0.001・alpha の下限 0.002 から a2 の最小は 4e-6、
    // そのとき s = 1.6e-11。1e-14 なら余裕 1600 倍で素通りし、
    // at = ab = 0 の退化だけを守れる。
    float  w2 = a2 / max(s, 1e-14);
    return a2 * w2 * w2 / TOON_PI;
}

float ToonV_SmithGGXAniso(float TdotV, float BdotV, float TdotL, float BdotL,
                          float NdotV, float NdotL, float at, float ab)
{
    float lambdaV = NdotL * length(float3(at * TdotV, ab * BdotV, NdotV));
    float lambdaL = NdotV * length(float3(at * TdotL, ab * BdotL, NdotL));
    // **下駄ではなく下限**（等方版と揃える。T-278 で片方だけ直していた）。
    return 0.5 / max(lambdaV + lambdaL, 1e-5);
}

/// <summary>1層分の異方性ハイライト。戻り値は D * Vis * NdotL（F は呼び出し側で掛ける）。</summary>
/// <param name="specAAKernel">法線分散（alpha² の次元）。ToonSpecAAKernel の返り値。
/// **異方性に分けた後の at / ab それぞれに足すこと。** 法線の振れは等方なので
/// 両軸に等しく加わる。alpha の段階で足してから分けると、
/// 異方性が強いほど狭い側の軸で効きが足りなくなる。</param>
float ToonStrandSpecularGGX(float3 T, float3 N, float3 V, float3 L,
                            float smoothness, float anisotropy, float specAAKernel)
{
    // T は接線平面で組むので普通は N と直交するが、**法線マップで N だけが
    // 振れる**ため平行に寄ることがある。cross がゼロなら異方性の軸が
    // 定義できないので、SafeNormalize でゼロを返させて下流を等方に倒す。
    float3 B = SafeNormalize(cross(N, T));
    float3 H = SafeNormalize(L + V);

    float perceptual = saturate(1.0 - smoothness);
    float alpha = max(perceptual * perceptual, 0.002);

    // **符号で伸びる向きが真逆になる。** 数値で確認済み（H を 0.2 ずらしたときの相対値）:
    //
    //   anisotropy = +0.8 : T 方向 0.746 / B 方向 0.003  -> **繊維に沿って伸びる**
    //   anisotropy = -0.8 : T 方向 0.003 / B 方向 0.746  -> **繊維を横切って伸びる**
    //
    // アニメ髪の「天使の輪」は毛の流れを**横切る**帯なので、それを狙うなら**負**。
    // 正にすると毛に沿った縦の筋になる（濡れ髪やリアル寄りの表現向け）。
    // Kajiya-Kay 経路（_HairAnisoGGXOn = 0）は pow(sin(T,H), n) なので、
    // 何も指定しなくても横切る帯になる ── 天使の輪だけが欲しいならそちらで足りる。
    //
    // D / V は Filament の D_GGX_Anisotropic / V_SmithGGXCorrelated_Anisotropic と一致。
    float at = max(alpha * (1.0 + anisotropy), 0.001);
    float ab = max(alpha * (1.0 - anisotropy), 0.001);

    // **毛束は法線が画素内で最も振れる場所。** ここに AA が掛かっていなかったので、
    // 髪のハイライトがカメラの微動でちらついていた（T-122 で主ローブとシーンだけ対応）。
    at = max(ToonApplyRoughnessKernel(at, specAAKernel), 0.001);
    ab = max(ToonApplyRoughnessKernel(ab, specAAKernel), 0.001);

    float NdotH = saturate(dot(N, H));
    // **下駄ではなく下限で挟む**（T-165）。ここは `1 - NdotV` を通らないので
    // NaN にはならないが、同じ書き方を残すと検査に恒久的な例外ができる。
    // 下限なら値域が [1e-5, 1] に収まり、ゼロ回避の意図はそのまま。
    float NdotV = max(saturate(dot(N, V)), 1e-5);
    float NdotL = saturate(dot(N, L));

    float D = ToonD_GGXAniso(dot(T, H), dot(B, H), NdotH, at, ab);
    float Vis = ToonV_SmithGGXAniso(dot(T, V), dot(B, V), dot(T, L), dot(B, L),
                                    NdotV, NdotL, at, ab);

    return D * Vis * NdotL;
}

/// <summary>
/// 環境反射用に法線を曲げる。繊維に垂直な方向へ反射ベクトルを寝かせることで、
/// プローブから拾う像が筋状に伸びる。Kajiya-Kay では作れない部分。
/// </summary>
float3 ToonAnisoReflectVector(float3 N, float3 V, float3 T,
                              float anisotropy, float perceptualRoughness)
{
    // **視線が従法線と平行になるとゼロベクトルが出る。**
    // `cross(B, V)` の長さは sin(なす角) なので、V が B と平行になる瞬間に
    // ぴたりと 0 になり、続く `cross(anisoT, B)` も 0 ── `normalize(0)` は NaN。
    //
    // **視線の向きだけで踏む。** 特殊なメッシュも極端な値も要らず、
    // カメラを回すと**一本の線として画素が壊れて走る**。
    // NaN は reflect → プローブの参照まで素通りするので、
    // 黒い（あるいは真っ白な）画素になって出る。
    //
    // 曲げる向きが定義できない場所なので、**素の法線へ落とす**のが正しい。
    // `SafeNormalize` はゼロを返すので、後段の lerp が N 側へ寄る。
    float3 B = SafeNormalize(cross(N, T));
    float3 anisoT = cross(B, V);
    float3 anisoN = SafeNormalize(cross(anisoT, B));

    // 粗さが低いうちに曲げすぎると、逆に像がねじれて見える。
    float bend = anisotropy * saturate(5.0 * perceptualRoughness);

    // `anisoN` がゼロに落ちたときは `lerp` が `N * (1 - bend)` になるので
    // 通常は非ゼロだが、bend が 1 まで来ると完全に潰れる。そこも塞ぐ。
    float3 blended = lerp(N, anisoN, bend);
    float3 bentNormal = (dot(blended, blended) > 1e-8) ? normalize(blended) : N;

    return reflect(-V, bentNormal);
}

// ----------------------------------------------------------------------------
//  スペキュラ AA (Tokuyoshi / Kaplanyan の法線フィルタリング)
//  小さい金属パーツやビーズのちらつきを抑える。動画にすると効果が分かる。
// ----------------------------------------------------------------------------
/// <param name="roughness">alpha（= perceptualRoughness²）。返り値も alpha。</param>
float ToonFilterRoughness(float3 normalWS, float roughness)
{
    return ToonApplyRoughnessKernel(roughness, ToonSpecAAKernel(normalWS));
}


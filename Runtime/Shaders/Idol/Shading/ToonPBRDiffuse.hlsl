// 拡散光の伝達関数（曲率駆動のソフトステップ）とスペキュラ AA のカーネル
//
// `ToonPBRCommon.hlsl` から切り出した（T-212）。**1 行も変えていない**
// ── include を展開し直して元のファイルとバイト一致することを確認済み。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  拡散光の伝達関数
// ----------------------------------------------------------------------------
float ToonWrapDiffuse(float NdotL, float wrap)
{
    return saturate((NdotL + wrap) / ((1.0 + wrap) * (1.0 + wrap)));
}

// 生の NdotL -> 0..1 の「光の当たり具合」
/// <param name="edgeAA">
/// 画面上での NdotL の変化率（1ピクセルあたり）。境界のソフトさの下限に使う。
/// 微分は光源ループの外で取ること。分岐の中で ddx/ddy を呼ぶと値が保証されない。
/// </param>
/// <param name="attenAA">
/// 遮蔽量の画面変化率（1ピクセルあたり）。**主光源のみ渡し、追加光源には 0 を渡す。**
/// 微分は光源ループの外で取る必要があるため（T-056）。
/// </param>
/// <param name="castShadow">
/// **落ち影ぶんの遮蔽量**（0 = 遮られていない）。シャドウマップ・前髪の影・
/// コンタクトシャドウ由来のぶんだけで、NdotL 由来の陰は含まない。
/// 「他の物体に遮られた影」だけを別の色で濃くするために分けて返す（FR-70）。
/// </param>
float ToonLightResponse(float NdotL, float curvature, float shadowOffset,
                        float shadowAtten, float castAtten, float edgeAA, float attenAA,
                        out float softness, out float rawT, out float castShadow)
{
    float wrapped = ToonWrapDiffuse(NdotL, _DiffuseWrap);
    rawT = saturate(wrapped + shadowOffset * _NPRShadowOffsetStrength);

    // リアルタイム影も同じ伝達関数を通す。
    // 別々に掛けると影の境界だけ硬くなって浮く。
    //
    // **強度は最後に一度だけ掛けること。** 以前は smoothstep の前後で二重に
    // 掛けており、前段の lerp が遮蔽量の下限を (1 - strength) まで持ち上げた結果、
    // 後段の smoothstep がそれをほぼ 1 へ押し戻していた。既定値で「完全に影の中」でも
    // 0.96 倍にしかならず、半影は 1.0 倍＝影が消えていた。
    // ここに畳まれている HQ 影・コンタクトシャドウ・前髪の影・マイクロシャドウが
    // まとめて効かなくなっていた。
    // **remap は 0.5 を中心に張ること。** 以前は smoothstep(0, softness, ...) で、
    // 遷移の「幅」と「どれだけ遮蔽されたら影とみなすか」が同じノブに乗っていた。
    // Softness を 0.05→1.0 に振ると中心が shadowAtten 0.024→0.475 まで動く一方、
    // 幅は 0.011→0.212 しか増えない。**動いていたのはほぼ影の大きさの方。**
    // 既定 0.35 では中心が 0.166 ＝「17% でも光が届いていれば光側」で、
    // リアルタイム影が幾何学的な半影の中心よりかなり内側に痩せていた。
    // 0.5 は PCF タップの半分が遮蔽された点＝半影の中心なので、ここに置くのが素直。
    float attenHalfWidth = _ShadowAttenSoftness * 0.5;
    float atten = smoothstep(0.5 - attenHalfWidth, 0.5 + attenHalfWidth, shadowAtten);

    // 影を掛ける前の値。後段の AA の下限を出すのに要る。
    // wrapped ではなくこちらを使うこと。NPR マップの G でオフセットが乗っていると
    // 実際に影が掛かる量が変わり、下限がその比率ぶんずれる。
    float rawTBase = rawT;
    float attenScaled = lerp(1.0, atten, _ReceiveShadowStrength);
    rawT *= attenScaled;

    // 落ち影の量。**マイクロシャドウを含まない castAtten から取ること。**
    // ToonMicroShadow は saturate(|NdotL| + 2*ao^2 - 1) で、**NdotL が 0 付近
    // ＝まさにターミネータで最も強く効く**。これを混ぜると「落ち影だけ濃くする」
    // はずが頬や鼻の陰にも落ち影の色が乗り、分離した意味が消える。
    // 強度を掛けた後で取るので、Receive を下げれば一緒に薄くなる。
    float castRemap = smoothstep(0.5 - attenHalfWidth, 0.5 + attenHalfWidth, castAtten);
    castShadow = 1.0 - lerp(1.0, castRemap, _ReceiveShadowStrength);

    softness = _ShadowSoftness * (1.0 + curvature * _CurvatureSoftness);

    // 境界が画面上で 1px を切ると必ずジャギる。硬いセル設定（Base Softness 0.03）
    // ほど起きやすい。変化率を下限にして、意図した硬さは保ったまま
    // 最低 1px 分だけ滑らかにする。絵の設計を変えるものではない。
    //
    // **単位を揃えてから比べること。** edgeAA は NdotL の画面変化率だが、
    // softness が乗るのは wrap を通した後の rawT で、こちらは
    // 1/(1+wrap)^2 に圧縮されている。素で比べると既定 wrap 0.25 で
    // 下限を 1.56 倍に見積もり、硬いセル設定ほど意図より甘くなる。
    float wrapToRawT = 1.0 / ((1.0 + _DiffuseWrap) * (1.0 + _DiffuseWrap));
    softness = max(softness, edgeAA * wrapToRawT * _ShadowEdgeAA);

    // **シャドウマップ由来の変化にも同じ下限を張る。** これが無いと、
    // ライトを回したときにシャドウマップのテクセルが幾何の上を滑り、
    // 遷移窓（既定 0.086）より粗い量子化がそのままステップを叩いて影が明滅する。
    // 解像度を上げると収まるのは変化率が下がるからで、原因は解像度ではなく
    // 「ステップの幅が入力の1ピクセル変化より狭い」こと。
    //
    // smoothstep の傾きは中心で最大 1.5/幅。遮蔽量の変化率をそこに掛けると
    // atten の変化率になり、rawT へは wrapped * 強度 で効く。
    // **解像度に依らず自動で釣り合う**のが要点で、8192 に上げれば変化率が
    // 半分になり、この下限も自動的に狭くなる（＝境界の切れは失われない）。
    float attenSlope = 1.5 / max(_ShadowAttenSoftness, 1e-4);
    float shadowAA   = rawTBase * _ReceiveShadowStrength * attenAA * attenSlope;
    softness = max(softness, shadowAA * 0.5 * _ShadowEdgeAA);
    softness = max(softness, 1e-4);

    // **下端は 0 で頭打ちにする（T-385）。** 幅を上げて 閾値 − 幅 が 0 を
    // 下回ると、最暗面（rawT = 0）でも smoothstep が 0 を返さなくなり、
    // **影の底が幅と連動して浮く**（利用者報告「Base Softness を上げると
    // 影の暗さの上限が下がる」）。ぼかしたいのは境界であって影の濃さでは
    // ないので、下端を 0 に留めて「rawT = 0 は常に完全な影色」を保証する。
    // 暗側の遷移が [0, 閾値] に圧縮されるが、それは元々届かない領域に
    // 窓を張るよりよい。明側のはみ出しは WarnDiffuseReach が別途警告する。
    return smoothstep(max(0.0, _ShadowThreshold - softness),
                      _ShadowThreshold + softness, rawT);
}

// 境界帯（境界の中心で 1、幅は伝達関数の幅）。**皮下散乱の重みにだけ使う。**
// 帯に色を乗せる Terminator 機能は T-392 で廃止した ── lit を入力にした色曲線は
// Ramp Override が完全に表現でき（生成 UI で調達不要・T-388）、Ramp 使用時は
// 上書きされて無意味になる位置に居たため。落ち方は旧 Sharpness 既定 2 と同じ 2 乗。
float ToonScatterBand(float rawT, float softness)
{
    float d = saturate(abs(rawT - _ShadowThreshold) / max(softness, 1e-4));
    float f = 1.0 - d;
    return f * f;
}

// 拡散色 = 影色とアルベドの補間。中間の色を置きたいときは Ramp Override
// （陰・影タブで生成できる）が後段でこれを上書きする。
float3 ToonDiffuseColor(float3 albedo, float3 shadowCol, float lit)
{
    return lerp(shadowCol, albedo, lit);
}

// ----------------------------------------------------------------------------
//  スペキュラ AA のカーネル
//
//  **BRDF より前に置くこと。** HLSL は宣言順に解析されるので、
//  髪やシーンのローブ（下で定義）から呼ぶにはここに無いといけない。
//  一度あとに置いて `undeclared identifier` でコンパイルに落ちた。
// ----------------------------------------------------------------------------
/// <summary>
/// 法線の分散から求めたフィルタカーネル。単位は alpha²。
///
/// **カーネルだけ切り出したのは、ローブごとに使い回すため。**
/// 以前は主 GGX ローブにしか掛かっておらず、**シーン（布）と髪のローブは
/// 生の粗さのまま**だった。白いシャツの皺や髪の毛束のような、
/// 法線が画素内で大きく振れる場所で狙い撃ちに効くはずの対策が、
/// **その2つに限って効いていなかった**。
///
/// 微分を使うので**フラグメントで1回だけ呼ぶこと。** 光源ループの中では
/// Forward+ の反復回数が実行時に決まるため微分が保証されない。
/// 結果は ToonContext に持たせて配る。
/// </summary>
float ToonSpecAAKernel(float3 normalWS)
{
    float3 dNdx = ddx(normalWS);
    float3 dNdy = ddy(normalWS);
    float  v = _SpecAAVariance * (dot(dNdx, dNdx) + dot(dNdy, dNdy));
    return min(2.0 * v, _SpecAAThreshold);
}

/// <param name="roughness">alpha。返り値も alpha。</param>
/// <param name="kernel">ToonSpecAAKernel の返り値（alpha² の次元）。</param>
float ToonApplyRoughnessKernel(float roughness, float kernel)
{
    // **kernel は alpha² の空間で足すこと。** これは法線の分散で、
    // NDF の傾きの分散＝alpha² と同じ次元にある（Kaplanyan / Tokuyoshi の導出）。
    // alpha に直接足していたので、**滑らかな面ほど効きが足りていなかった**:
    // Smoothness 0.9 で本来の 1/3.7、0.75 で 1/2.1 しか荒らせていない。
    // ビーズや金具のちらつき対策という用途そのものが効いていなかった。
    return saturate(sqrt(roughness * roughness + kernel));
}

/// <summary>
/// Blinn の指数にスペキュラ AA を掛ける。
///
/// 指数は粗さの逆数側の量なので、そのままではカーネルを足せない。
/// alpha² = 2/(n+2) の関係で粗さの空間へ移し、足してから戻す。
/// カーネルは alpha² の次元なのでこの形でそのまま加算できる。
/// </summary>
float ToonFilterBlinnExponent(float exponent, float kernel)
{
    float a2 = 2.0 / max(exponent + 2.0, 1e-4) + kernel;
    return max(2.0 / max(a2, 1e-4) - 2.0, 1.0);
}


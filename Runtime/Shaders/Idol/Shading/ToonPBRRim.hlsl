// リムライト（深度シルエット + フレネル / Fresnel PBR モード）
//
// `ToonPBRCommon.hlsl` から切り出した（T-212。当時バイト一致を確認）。
// T-343 で Fresnel (PBR) モードを追加 ── EasyPBR(Doll) と同じ Core の式
// （GetFresnelTerms / CalculateRimLight）へ委譲する。uniform 分岐で
// キーワードは増えない。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  リムライト
//  深度差でシルエットを取り、フレネルで太さを落とし、逆光度合いで強度を決める
// ----------------------------------------------------------------------------
//
//  **2 段構成（T-351）。** リムの「形」は視線だけで決まる（深度シルエットの
//  フェッチも含めて）ので frag で 1 回。「どの光がどれだけ縁を照らすか」は
//  ライトごとなので、主光源＋追加光源それぞれで適用する。
//  以前は主光源 1 灯ぶんしか計算していなかったため、**ステージのスポット
//  （追加光源）で色を作ってもリムが反応しない**という食い違いが出ていた
//  （Doll はライトごとに計算していた）。深度フェッチはライト数に依らず 1 回。
// ----------------------------------------------------------------------------

/// <summary>
/// 視線依存の「縁の光沢」の形。ライトに依存しないので frag で 1 回だけ呼ぶ。
/// <c>x</c> = リム / <c>y</c> = 産毛（ピーチファズ）。
/// **産毛はリムのモードに依らない** ── 深度シルエットは「縁を検出する」ための
/// 仕掛けで、産毛は面の傾きだけで決まる別物だから。
/// </summary>
float2 ToonRimShape(ToonSurface s, ToonContext c)
{
    // 単一 return に畳む（UNITY_BRANCH の中から return すると fxc が
    // 未初期化警告 X4000 を出す ── T-049 / T-073 の教訓）。
    float shape;

    UNITY_BRANCH
    if (_RimMode > 0.5)
    {
        // --- Fresnel (PBR) ── EasyPBR(Doll) と同じ Core の式（T-343） ---------
        // 深度テクスチャを読まないぶん従来モードより軽い。
        float rimFresnel, fuzzFresnel;
        GetFresnelTerms(saturate(c.NdotV), _RimIntensity, _RimFresnelThickness,
                        0.0, 1.0, rimFresnel, fuzzFresnel);
        shape = rimFresnel;
    }
    else
    {
    // --- Screen Silhouette（従来） -------------------------------------------
    // 深度差によるシルエット検出
    float3 nVS = TransformWorldToViewDir(c.N, true);
    float2 aspect = float2(_ScaledScreenParams.y / max(_ScaledScreenParams.x, 1.0), 1.0);
    float2 offsetUV = c.screenUV
                    + normalize(nVS.xy + 1e-5)
                    * (_RimWidth * 10.0 / max(_ScaledScreenParams.y, 1.0))
                    * aspect / max(c.eyeDepth, 0.1);

    // 画面外を引くと端のピクセルが延々と伸びて、画面際に偽のリムが出る。
    float2 sampleUV = saturate(offsetUV);
    float offsetDepth = LinearEyeDepth(SampleSceneDepth(sampleUV), _ZBufferParams);
    float depthDiff = offsetDepth - c.eyeDepth;

    // **幅の下限を入力の変化率で張る。** _RimSoftness は 0.05m 固定だが、
    // シルエットでは深度差が1ピクセルでメートル級に飛ぶので、固定幅では
    // 必ず 1px 未満の境界になってジャギる（影のちらつき T-067 と同じ形）。
    float depthSoft = max(_RimSoftness, fwidth(depthDiff));
    float depthRim = smoothstep(_RimThreshold, _RimThreshold + depthSoft, depthDiff);

    // フレネルによる連続的な縁
    float fresnelRim = pow(1.0 - saturate(c.NdotV), max(_RimFresnelPower, 0.01));

    shape = lerp(fresnelRim, depthRim * fresnelRim, _RimDepthBlend);
    }

    // 産毛のフレネル項。**リムと同じ関数の未使用の出力だった** ── 以前は
    // `GetFresnelTerms` に fuzzIntensity = 0 を渡して結果を捨てていた（T-363）。
    // ここは rimIntensity = 0 で呼ぶのでリム側の計算は分岐ごと飛ぶ。
    float rimUnused, fuzzFresnel;
    GetFresnelTerms(saturate(c.NdotV), 0.0, 0.0,
                    _FuzzIntensity, _FuzzPower, rimUnused, fuzzFresnel);

    // マスクは視線にもライトにも依らないのでここで掛けておく。
    // **産毛にはリムマスクを掛けない**（Doll と同じ）── 産毛は肌の全面に生えて
    // いるもので、リムマスク（NPR Map の B）は輪郭光の出方を描くためのもの。
    return float2(shape * s.rimMask, fuzzFresnel);
}

/// <param name="lightEnergy">
/// この光源のエネルギー（色 × 距離減衰）。**ステージ照明の色がそのまま縁に乗る。**
/// </param>
/// <param name="castShadow">
/// この光源の**落ち影の量**（0 = 遮られていない）。NdotL 由来の陰は含まない。
/// リムは光が回り込んだ縁に出るものなので、**何かに遮られていれば出ないのが筋**。
/// 向きの判定（Directionality）とは別で、あちらは「面が光の側を向いているか」、
/// こちらは「そこに光が届いているか」を見る。
/// </param>
float3 ToonRimLight(float2 shape, ToonContext c, float3 lightDir, float3 lightEnergy,
                    float castShadow)
{
    float3 rimOut;

    UNITY_BRANCH
    if (_RimMode > 0.5)
    {
        // 落ち影の扱いは従来モードと同じ意味論（castShadow = 落ち影の量、
        // _RimReceiveShadow で消灯の度合い）を Core の lit 側引数へ変換して渡す。
        float rimShadow = lerp(1.0, 1.0 - saturate(castShadow), _RimReceiveShadow);
        rimOut = CalculateRimLight(_RimColor.rgb, shape.x, _RimIntensity,
                                   lightEnergy, saturate(dot(c.N, lightDir)), rimShadow);
    }
    else
    {
    float rim = shape.x;

    // 逆光のときだけ強く出す。V と L が逆向き = 光源が被写体の裏。
    // 逆光度合い。カメラとライトの関係だけを見るので、**画面内のどこでも同じ値**。
    float backlight = saturate(-dot(c.V, lightDir) * 0.5 + 0.5);
    rim *= lerp(1.0, backlight, _RimBacklightBias);

    // **面がどちらを向いているかを見る。** これが無いと、フレネルも深度も
    // 視線にしか依存しないため**シルエットの全周に等しくリムが出る**。
    // 光源を動かしてもリムの位置が変わらず「光源と無関係に光っている」ように見える。
    //
    // シルエットでは N が視線と直交するので、光源側の縁は NdotL > 0、
    // 反対側の縁は NdotL < 0 になる。そこで切れば光が回り込んだ側だけに出る。
    // 帯を ±0.3 と広めに取るのは、境界が硬いとリムが唐突に途切れて見えるため。
    float rimNdotL = dot(c.N, lightDir);
    float facing   = smoothstep(-0.3, 0.3, rimNdotL);
    rim *= lerp(1.0, facing, _RimDirectionality);

    // 遮蔽されている場所ではリムを消す。前髪が肩に落とす影の中で
    // シルエットだけ光る、という破綻を防ぐ。
    rim *= lerp(1.0, 1.0 - saturate(castShadow), _RimReceiveShadow);

    rimOut = rim * _RimColor.rgb * _RimIntensity * lightEnergy;
    }

    // --- 産毛（ピーチファズ）------------------------------------------------
    // **リムとは向きが逆。** リムは光が回り込んだ縁（NdotL が小さい側）に出るが、
    // 産毛は**面が光源を向いているほど**強い ── 細かい毛が順光で white に
    // 散乱する現象なので、Core の式も `saturate(N·L)` を掛けている。
    // 影の中では出さない（光が届いていないので当然）。
    UNITY_BRANCH
    if (_FuzzIntensity > 0.0)
    {
        rimOut += CalculatePeachFuzz(_FuzzColor.rgb, shape.y, _FuzzIntensity,
                                     lightEnergy, saturate(dot(c.N, lightDir)),
                                     1.0 - saturate(castShadow));
    }

    return rimOut;
}

// リムライト（フレネル。EasyPBR(Doll) と同じ Core の式）
//
// `ToonPBRCommon.hlsl` から切り出した（T-212）。T-343 で Fresnel (PBR) モードを足し、
// T-416 で深度差方式（Screen Silhouette）を撤去した。Core の GetFresnelTerms /
// CalculateRimLight / CalculatePeachFuzz へ委譲する。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。
//
//  **2 段構成（T-351）。** リムの「形」は視線だけで決まるので frag で 1 回。
//  「どの光がどれだけ縁を照らすか」はライトごとなので、主光源＋追加光源それぞれで
//  適用する（ToonShadeLight が成分 rim として返す。T-410）。
// ----------------------------------------------------------------------------

/// <summary>
/// 視線依存の「縁の光沢」の形。ライトに依存しないので frag で 1 回だけ呼ぶ。
/// <c>x</c> = リム / <c>y</c> = 産毛（ピーチファズ）。
/// </summary>
float2 ToonRimShape(ToonSurface s, ToonContext c)
{
    // リムと産毛は同じ関数の 2 出力。強度が 0 の側は Core の中で分岐ごと飛ぶ。
    float rimFresnel, fuzzFresnel;
    GetFresnelTerms(saturate(c.NdotV), _RimIntensity, _RimFresnelThickness,
                    _FuzzIntensity, _FuzzPower, rimFresnel, fuzzFresnel);

    // リムマスク（旧 NPR Map の B）は T-419 で廃止した ── 描かれたアセットが無く、
    // 部位ごとの調整は材質の Rim Intensity で足りる。B は Detail Mask に転用。
    return float2(rimFresnel, fuzzFresnel);
}

/// <param name="lightEnergy">
/// この光源のエネルギー（色 × 距離減衰）。**ステージ照明の色がそのまま縁に乗る。**
/// </param>
/// <param name="castShadow">
/// この光源の**落ち影の量**（0 = 遮られていない）。NdotL 由来の陰は含まない。
/// リムは光が回り込んだ縁に出るものなので、**何かに遮られていれば出ないのが筋**。
/// </param>
float3 ToonRimLight(float2 shape, ToonContext c, float3 lightDir, float3 lightEnergy,
                    float castShadow)
{
    // 向き（光が回り込んだ側だけ）は Core の CalculateRimLight が saturate(N·L × 5) で絞る。
    // 落ち影は _RimReceiveShadow の度合いで消す（Core の lit 側引数へ変換して渡す）。
    float  NdotL     = saturate(dot(c.N, lightDir));
    float  rimShadow = lerp(1.0, 1.0 - saturate(castShadow), _RimReceiveShadow);
    float3 rimOut    = CalculateRimLight(_RimColor.rgb, shape.x, _RimIntensity,
                                         lightEnergy, NdotL, rimShadow);

    // --- 産毛（ピーチファズ）------------------------------------------------
    // **リムとは向きが逆。** リムは光が回り込んだ縁（NdotL が小さい側）に出るが、
    // 産毛は**面が光源を向いているほど**強い ── 細かい毛が順光で白く
    // 散乱する現象なので、Core の式も `saturate(N·L)` を掛けている。
    // 影の中では出さない（光が届いていないので当然）。
    UNITY_BRANCH
    if (_FuzzIntensity > 0.0)
    {
        rimOut += CalculatePeachFuzz(_FuzzColor.rgb, shape.y, _FuzzIntensity,
                                     lightEnergy, NdotL, 1.0 - saturate(castShadow));
    }

    return rimOut;
}

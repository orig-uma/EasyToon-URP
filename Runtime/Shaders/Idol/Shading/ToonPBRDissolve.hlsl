// ディゾルブ（消失演出）。
//
// **キーワードを使わない。** 移植元は `_DISSOLVE_ON` で切っているが、
// ToonPBR は百万超のバリアントを持ち（T-344 前の実測で 270 万。
// コンタクト影の廃止で半減したが桁は同じ）、キーワードを足すと
// ForwardLit の feature が 20 → 40 になって
// **シェーダー全体が倍**になる。演出用の機能にその代償は釣り合わない。
//
// 代わりに `_DissolveAmount > 0` の一様分岐で切る。ToonPBR は
// 曲率マップ・キャビティ・シアー生地など、既定 OFF の機能をすべて
// この形で持っている（`UNITY_BRANCH if (_Xxx > 0.0)`）ので作法も揃う。
// 移植元も「軸はバリアントを増やさないため一様分岐」と書いており、
// その理屈をそのまま最後まで適用した形。
//
// **勾配は頂点で求めて 1 float で運ぶ。** WorldY も LocalY も位置の一次式で、
// 線形補間で厳密に一致する。フラグメントへ positionWS と positionOS を
// 両方通すと、影・深度・法線の各パスに 6 float ずつ足すことになる。
//
// **ノイズは UV のみ。** 移植元は WorldY のとき三平面投影を使うが、
// それには positionWS と normalWS がフラグメントに要る。キャラは UV が
// 整っている前提のシェーダーなので、UV で引く。

#ifndef TOON_PBR_DISSOLVE_INCLUDED
#define TOON_PBR_DISSOLVE_INCLUDED

/// <summary>
/// 消失の進み具合を位置から作る。**頂点シェーダーで呼ぶこと。**
/// 戻り値は 0..1 の無次元量で、そのまま補間して良い。
/// _DissolveType: 0 = 使わない / 1 = ワールド Y / 2 = ローカル Y
/// </summary>
float ToonDissolveGradient(float3 positionOS, float3 positionWS)
{
    // **ゼロ除算を塞ぐ。** Start と End を同じ値にするのは操作として自然
    //（「一気に消す」）で、実際に起こる。
    float span = _DissolveEndY - _DissolveStartY;
    float invSpan = 1.0 / (abs(span) < 1e-4 ? 1e-4 : span);

    float y = (_DissolveType > 1.5) ? positionOS.y : positionWS.y;
    return saturate((y - _DissolveStartY) * invSpan);
}

/// <summary>
/// 消失の判定と縁の色。**clip を含む。**
/// uv は BaseMap と同じ UV、grad は ToonDissolveGradient の補間値。
/// albedo は縁の色へ寄せて上書きし、発光ぶんを emission に返す。
///
/// 呼ぶ側で `_DissolveAmount > 0` を確かめてから呼ぶこと ──
/// この関数は分岐を持たない（clip が分岐の中にあると
/// 一部のコンパイラで警告が出るため）。
/// </summary>
void ToonDissolve(float2 uv, float grad, inout float3 albedo, out float3 emission)
{
    emission = 0;

    bool noneType = (_DissolveType < 0.5);
    float noise = SAMPLE_TEXTURE2D(_DissolveTex, sampler_DissolveTex,
                                   uv * _DissolveNoiseScale).r;

    // 軸を使わないときはノイズだけで切る。使うときは高さにノイズを混ぜる。
    float value = noneType ? noise : grad + (noise - 0.5) * _DissolveNoiseStrength;

    // 値域の下端と上端。混ぜたぶんだけ外へ広がる。
    float lo = noneType ? 0.0 : -0.5 * _DissolveNoiseStrength;
    float hi = noneType ? 1.0 : 1.0 + 0.5 * _DissolveNoiseStrength;

    // **端で完全に出る／完全に消える**ように、縁の幅ぶん余裕を取る。
    float threshold = lerp(lo - _DissolveEdgeWidth - 0.01,
                           hi + _DissolveEdgeWidth + 0.01, _DissolveAmount);
    float d = value - threshold;

    // 反転は符号を返すだけ。分岐にしない
    d *= lerp(1.0, -1.0, saturate(_DissolveInvert));
    clip(d);

    float edge = 1.0 - smoothstep(0.0, max(_DissolveEdgeWidth, 1e-4), d);
    UNITY_BRANCH
    if (_DissolveEdgeStep > 0.5)
    {
        // トゥーン調に段を付ける。0 のところは 0 のまま残す
        edge = ceil(edge * 2.0) / 2.0 * step(0.01, edge);
    }

    albedo = lerp(albedo, _DissolveEdgeColor2.rgb, edge);

    float emissionMask = (_DissolveEdgeStep > 0.5) ? step(0.9, edge)
                                                   : smoothstep(0.5, 1.0, edge);
    emission = _DissolveEdgeColor.rgb * emissionMask;
}

/// <summary>
/// 影・深度・法線のパス用。**色は要らないが、切る場所は本体と一致させる。**
/// ここを省くと、消えたはずの部分が影と深度に残って輪郭だけが浮く。
/// </summary>
void ToonDissolveClip(float2 uv, float grad)
{
    float3 dummyAlbedo = 1;
    float3 dummyEmission;
    ToonDissolve(uv, grad, dummyAlbedo, dummyEmission);
}

#endif // TOON_PBR_DISSOLVE_INCLUDED

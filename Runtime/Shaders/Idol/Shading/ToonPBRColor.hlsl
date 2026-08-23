// HSV などの色ユーティリティと曲率推定
//
// `ToonPBRCommon.hlsl` から切り出した（T-212。当時バイト一致を確認）。
// T-340 で「Core へ寄せない」判断のコメントを追記した（コード本体は不変）。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  色ユーティリティ
// ----------------------------------------------------------------------------
// 当初 Core 版は `+ e` の下駄形・half 精度（E009 が戒めている形）だったため
// 寄せなかったが、T-340 で **Idol のこの実装を Core 側へ逆輸入**して同値になった。
// 本体は Core（max() 下限形・float）に置き、ここは前方転送。
float3 ToonRgbToHsv(float3 c)
{
    return EasyPBR_RgbToHsv(c);
}

float3 ToonHsvToRgb(float3 c)
{
    return EasyPBR_HsvToRgb(c);
}

/// <summary>
/// 素のアルベドの HSV 補正。テクスチャを描き直さずに色を振るための入口。
/// 入力・出力とも**線形空間の反射率**（0..1）。
///
/// 影側（ToonShadowAlbedo）とは別物。あちらは「影になった所だけ」の色で、
/// こちらは**元の色そのもの**を動かす。両方掛かる。
/// </summary>
float3 ToonAlbedoHSV(float3 albedo)
{
    float3 result;

    // 3つとも既定なら HSV に往復しない。既定で通る経路を重くしないため。
    UNITY_BRANCH
    if (abs(_AlbedoHueShift) < 1e-4 && abs(_AlbedoSaturation - 1.0) < 1e-4)
    {
        result = albedo * _AlbedoValue;
    }
    else
    {
        float3 hsv = ToonRgbToHsv(albedo);
        hsv.x = frac(hsv.x + _AlbedoHueShift);
        hsv.y = saturate(hsv.y * _AlbedoSaturation);
        hsv.z = hsv.z * _AlbedoValue;
        result = ToonHsvToRgb(hsv);
    }

    // **1 を超えさせない。** アルベドは反射率なので 1 を超えると
    // 「入った以上の光を返す」ことになり、多重散乱の補償（_EnergyCompensation）と
    // 鏡面のエネルギー保存が破綻する。移植元（Idol の _ValueMulti）は
    // 抑えていないので、**1 以上を入れたときだけ絵が食い違う。**
    // スライダの上限 2 は移植元と揃えてあるが、意味を持つのは暗い色を
    // 持ち上げるときだけ。
    return saturate(result);
}

/// <summary>
/// シアー生地（ストッキング・タイツ）を肌の上に手続き的に重ねる。
/// 布を別メッシュで持たずに済ませるための層。
///
/// 入力: uv（BaseMap と同じ UV）/ NdotV は [0,1] の無次元
/// 出力: 上書きされた線形アルベド（反射率 0..1）
///
/// **不透明度が視角で変わるのが要点。** 正面から見ると糸の隙間から肌が透け、
/// シルエットに近づくほど糸が視線方向に重なって密に見える。
/// `pow(1 - NdotV, _StockingPower)` はその近似で、
/// 糸密度の視角変化そのものを解いているわけではない。
///
/// **アルベドの段で掛けること。** 影色（ToonShadowAlbedo）は拡散色から作るので、
/// ここで乗せておけば 1影・落ち影にも布の色が自動で乗る。
/// 後段で乗せると、影の中だけ布が消える。
/// </summary>
void ToonStockingLayer(float2 uv, float NdotV, inout float3 albedo)
{
    float mask = SAMPLE_TEXTURE2D(_StockingMask, sampler_StockingMask, uv).r;

    // **底を負にしないこと。** NdotV は呼び出し側で saturate 済みだが、
    // 守りをここに置く ── 負の底の pow は NaN（T-165 で踏んだ形）。
    float graze = pow(max(1.0 - NdotV, 0.0), max(_StockingPower, 0.01));

    float opacity = saturate(lerp(_StockingFrontOpacity, 1.0, graze))
                  * mask * _StockingIntensity;

    // 正面は肌に布色が乗る（乗算）、縁は布そのものの色。
    float3 fabric = lerp(albedo * _StockingColor.rgb, _StockingColor.rgb, graze);
    albedo = lerp(albedo, fabric, opacity);
}

// 影側の色。単に暗くするのではなく色相を回して彩度を上げる。
// ここが「塗った絵」に見えるかどうかの分かれ目。
/// <summary>
/// 影側の色。ライトに依存しないので**フラグメントで1回だけ**求めること。
/// ここを ToonDiffuseColor の中に置くと、追加光源の数だけ HSV 往復が走る。
/// </summary>
float3 ToonShadowAlbedo(float3 albedo)
{
    // 色相も彩度も既定のままなら、HSV に往復する意味が無い。
    // 明度スケールは RGB 全成分の一様スケールと等価なので直接掛けられる。
    //
    // 早期 return にしないのは、UNITY_BRANCH と組み合わせると
    // 「未初期化の可能性がある」と警告されるため（単一 return に畳む）。
    float3 result;

    UNITY_BRANCH
    if (abs(_ShadowHueShift) < 1e-4 && abs(_ShadowSaturation - 1.0) < 1e-4)
    {
        result = albedo * _ShadowValue;
    }
    else
    {
        float3 hsv = ToonRgbToHsv(albedo);
        hsv.x = frac(hsv.x + _ShadowHueShift);
        hsv.y = saturate(hsv.y * _ShadowSaturation);
        hsv.z = hsv.z * _ShadowValue;
        result = ToonHsvToRgb(hsv);
    }

    result *= _ShadowTint.rgb;

    // 影色を _ShadowColor の色相へ寄せる。
    //
    // **なぜ掛け算では駄目か。** 乗算は減法混色で、色を持つ2色を掛けると
    // 互いの色相が打ち消し合って彩度が落ちる。肌（暖色）に紫の Tint を掛けても
    // 「紫の影」にはならず、灰茶の濁りになる。加えて白い布や銀髪のように
    // 元の彩度が 0 に近い面は、Saturation Scale を何倍にしても 0 のままなので
    // （0 × 1.79 = 0）、乗算だけでは**影に色を入れる手段が存在しない**。
    //
    // 寄せる方式なら albedo の彩度に依存せずこの色相が出る。
    // 明度（Rec.709 輝度）を合わせてから混ぜるので**影の濃さは変わらない**。
    // 濃さは _ShadowValue と _ShadowAmbientIntensity の担当で、ここは色味だけ。
    UNITY_BRANCH
    if (_ShadowColorMix > 1e-4)
    {
        const float3 kLumaRec709 = float3(0.2126, 0.7152, 0.0722);

        float  luma      = dot(result, kLumaRec709);
        float  targetLum = max(dot(_ShadowColor.rgb, kLumaRec709), 1e-4);
        float3 target    = _ShadowColor.rgb * (luma / targetLum);

        result = lerp(result, target, _ShadowColorMix);
    }

    return result;
}



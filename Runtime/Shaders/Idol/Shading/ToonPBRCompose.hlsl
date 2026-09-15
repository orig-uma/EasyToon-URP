// =============================================================================
//  ToonPBRCompose.hlsl — ライト応答の成分を 1 か所で畳む（T-410）
// -----------------------------------------------------------------------------
//  ToonShadeLight が返す成分（拡散 / 鏡面 / sheen / コート / リム）に、
//    ・粒への分解（Glitter Specular → 鏡面と sheen、Glitter Rim → リム）
//    ・スパンコール（Glitter）のフラッシュ
//  を掛けて足す。主光源も追加光源もここを通るので「掛け忘れ」が起きない。
//  以前は鏡面は Lighting、リムと粒は ForwardPass と 3 か所に散っていて、
//  粒を足すたびに後追いで掛けていた（T-408 / T-409 の経緯）。
//  環境反射は光の向きを持たないので ToonShadeIndirect 側で同じマスクを掛ける。
// =============================================================================
#ifndef TOONPBR_COMPOSE_INCLUDED
#define TOONPBR_COMPOSE_INCLUDED

// フラグメントで 1 回だけ用意する、粒・スパンコールの共有データ
struct ToonGlitterSet
{
    GlitterGeom     glitter;
    bool            glitterActive;
    float3          color;         // Glitter Color × Albedo Tint
};

// スパンコールで鏡面・リムを分解するマスク（平均 ≈ 1。T-413）。円盤の中（dotMask）だけ通し、
// 円盤ごとの傾きがカメラ寄りかで明暗を付け、円盤の被覆率で割って平均を 1 に戻す。
// 円盤の外では 0（呼び出し側が glitterActive で分ける）。
float ToonGlitterGrainMask(GlitterGeom g, float3 N, float3 V, float scale, float dotSize, float sparsity)
{
    float3 Hv     = normalize(N + V);
    float  facing = 0.5 + 0.5 * pow(saturate(dot(g.glitterNormal, Hv)), 8.0);
    float  rCell  = 0.85 * dotSize * scale;                       // 平均半径（セル比。円盤は 0.7〜1.0 倍）
    float  cover  = saturate(PI * rCell * rCell) * (1.0 - sparsity) * 0.6;   // 被覆率 × facing の平均
    return min(g.dotMask * facing / max(cover, 0.02), 6.0);
}

// extraEnergy: 主光源だけ環境光（SH）を足す（影の中でもスパンコールのベースが消えないように。T-378）。
// flash: スパンコールのフラッシュ。追加光源の Max 合成の対象にしないため別に返す
//（きらめきは物理的に加算なので、最も強い 1 灯だけを採る合成に巻き込まない）。
float3 ToonComposeLight(ToonLightTerms t, ToonContext c, Light light, float3 extraEnergy,
                        ToonGlitterSet sp, out float3 flash)
{
    float3 col = t.diffuse + t.coat;
    col += (t.specular + t.sheen) * c.specGrain;
    col += t.rim * c.rimGrain;

    flash = 0.0;
    float atten = light.distanceAttenuation;
    if (sp.glitterActive)
    {
        // スパンコールは主光源の影で暗くし、環境光ぶんは通す（T-378）
        float3 e = light.color * (atten * light.shadowAttenuation) + extraEnergy;
        flash += ApplyGlitterLight(sp.glitter, light.direction, c.V, sp.color, _GlitterIntensity,
                                   _GlitterIridescence, _GlitterIridescenceShift,
                                   _GlitterBaseReflection, e);
    }
    return col;
}

#endif // TOONPBR_COMPOSE_INCLUDED

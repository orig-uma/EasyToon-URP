// ToonSurface / ToonContext などの構造体
//
// `ToonPBRCommon.hlsl` から切り出した（T-212）。**1 行も変えていない**
// ── include を展開し直して元のファイルとバイト一致することを確認済み。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  構造体
// ----------------------------------------------------------------------------
struct ToonSurface
{
    float3 albedo;
    float  alpha;
    float3 diffuseColor;        // albedo * (1 - metallic)
    float3 f0;
    float  perceptualRoughness;
    float  roughness;
    float  occlusion;
    float  cavity;      // 窪みの微細遮蔽。1 = 遮蔽なし
    float  thickness;
    float  specMask;
    float  shadowOffset;        // -1 .. +1
    float  rimMask;
    float  rampIndex;
    float3 emission;
    float3 shadowColor;         // 影側の色。フラグメントで1回だけ求める
};

struct ToonContext
{
    float3 positionWS;
    float3 N;
    float3 bentN;               // 遮蔽されていない方向。未使用時は N と同じ
    float3 shadeN;              // 陰ランプ専用の平滑法線。未使用時は N と同じ
    float3 sssDir;              // 透過を曲げる方向。未使用時は N と同じ
    float3 V;
    float3 T;
    float3 B;
    float  NdotV;
    float  curvature;
    float2 uv;
    float2 screenUV;
    float2 positionSS;          // ピクセル座標。APV のディザに要る
    float  edgeAA;              // 主光源基準の NdotL の画面変化率。境界 AA の下限（後方互換）
    // 法線の画面微分。**光源ごとの edgeAA をループ内で求めるために持つ。**
    // fwidth(dot(N,L)) = |dot(ddx(N),L)| + |dot(ddy(N),L)|  ── L は光源ごとに定数なので、
    // 微分だけ外で取っておけばループ内で微分を取らずに正確な値が出せる
    // （Forward+ は反復回数が実行時に決まるのでループ内の微分は保証されない）。
    float3 dNdx;
    float3 dNdy;
    float  specAAKernel;        // 法線の分散（alpha²）。全鏡面ローブで共有する
    float  sheenAlpha;          // AA を掛けたシーンの粗さ。Cloth のみ。ライトに依存しない
    float2 hairExp;             // AA を掛けた Kajiya の指数（主/副）。Hair のみ
    float  dither;              // 画面座標の IGN。ディザが要る処理で共有する
    float  eyeDepth;
    float2 uvDx;                // UV の画面微分。光源ループ内のサンプルはこれで _GRAD を使う
    float2 uvDy;
    float  faceSdfAA;           // 顔 SDF（16bit デコード後）の画面変化率。Face 以外では 0

    // ---- 光源に依存しない前計算 --------------------------------------------
    // ToonShadeLight は**ライトの数だけ**呼ばれる。light に依らない量を
    // その中で毎回求めるのは、Forward+ で灯数ぶんの無駄になる。
    // 前髪の影を引き上げたとき（T-067）と同じ理由でここに置く。
    float  faceSdf;             // 顔 SDF（16bit 1ch・非ミラー側）。ミラー側はライトごとに引く
    float3 hairT1;              // ずらした繊維接線（1層目）
    float3 hairT2;              //             （2層目）
    float  hairSparkle;         // 毛束の粒。副バンドを割る 0..1
    float3 energyComp;          // 多重散乱の補償倍率
    // 正面・上向きの陰の持ち上げ（FR-31）。**法線と向きだけで決まるので光源非依存。**
    // 逆光で消す係数だけがライトごとに変わる。
    float  sheenScale;          // 布の下地の縮小率（sheen のエネルギー保存）
};


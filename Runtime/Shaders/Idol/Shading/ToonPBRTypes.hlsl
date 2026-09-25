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
    float  metalSpecBoost;      // lerp(1, _MetalSpecularBoost, metallic)。非金属では 1
    float  metalEnvBoost;       // lerp(1, _MetalEnvBoost, metallic)。同上
    float  metallic;            // Mask R × Metallic（sheen の金属染めに使う。T-420）
    float3 rimN;               // リム用の法線。サーフェス収集で作る（T-432）
    float3 sheenN;             // sheen 用（T-434）
    float3 specFlatN;          // 鏡面用。ベースを幾何法線へ寄せたもの（T-434）
    float  geomCurvature;      // Geometry Map の G（0.5 = 平坦）。コンテキストが読む（T-422）
    float3 f0;
    float  perceptualRoughness;
    float  roughness;
    float  occlusion;
    float  cavity;      // 窪みの微細遮蔽。1 = 遮蔽なし
    float  thickness;
    float  specMask;
    float  shadowOffset;        // -1 .. +1
    float  sheenMask;           // Fabric Map G。1 = 材質値のまま（T-419）
    float  coatMask;            // Fabric Map B
    float  iridMask;            // Fabric Map A
    float3 emission;
    float3 shadowColor;         // 影側の色。フラグメントで1回だけ求める
};

// ライト 1 灯の応答を成分ごとに持つ（T-410）。ToonShadeLight が返し、畳むのは
// ToonComposeLight だけ。「粒に分解する対象」「影の床」「スパンコール」のように
// 成分を選んで掛けたい処理が、ライトの種類（主・追加）に関係なく 1 か所で済む。
// 各成分は光のエネルギー込み。足せばそのライトの色になる。
struct ToonLightTerms
{
    float3 diffuse;      // 拡散 ＋ 透過（影色・ランプ込み）
    float3 specular;     // 鏡面（GGX・第 2 ローブ・髪）── 粒の対象
    float3 sheen;        // 布の毛羽 ── 粒の対象
    float3 coat;         // クリアコート ── 滑らか（粒の対象外。粒の上に載る薄膜）
    float3 rim;          // リム ── 粒の対象
};

struct ToonContext
{
    float3 positionWS;
    float3 N;
    float3 bentN;               // 遮蔽されていない方向。未使用時は N と同じ
    float3 sheenN;              // sheen の D 項用（T-434）
    float3 rimN;                // リム・産毛用（T-432）。ノーマルマップをぼかし、ディテールを減らせる
    float3 specN;               // 鋭いローブ（GGX・環境反射・MatCap・グリッター）用。ディテール法線を含まない（T-401）
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
    float  specGrain;           // 鏡面（GGX・sheen・髪・環境反射）をスパンコールに分解するマスク（平均 1）。未使用時 1（T-413）
    float  rimGrain;            // リムをスパンコールに分解するマスク（平均 1）。未使用時 1（T-413）
    float  sheenAlpha;          // AA を掛けたシーンの粗さ。Cloth のみ。ライトに依存しない
    float2 hairExp;             // AA を掛けた Kajiya の指数（主/副）。Hair のみ
    float  dither;              // 画面座標の IGN。ディザが要る処理で共有する
    float2 uvDx;                // UV の画面微分。光源ループ内のサンプルはこれで _GRAD を使う
    float2 uvDy;
    float  faceSdfAA;           // 顔 SDF（16bit デコード後）の画面変化率。Face 以外では 0

    // ---- 光源に依存しない前計算 --------------------------------------------
    // ToonShadeLight は**ライトの数だけ**呼ばれる。light に依らない量を
    // その中で毎回求めるのは、Forward+ で灯数ぶんの無駄になる。
    // 前髪の影を引き上げたとき（T-067）と同じ理由でここに置く。
    float  faceSdf;             // 顔 SDF（16bit 1ch・非ミラー側）。ミラー側はライトごとに引く
    float  faceSdfMask;         // 顔 SDF の顎裏フェード（法線・頭 up 軸の内積。T-376 / T-440）。Face 以外では 1
    float  faceTone;            // 顔の影トーンの網点しきい値 0..1（画面固定。Bayer かブルーノイズ。T-440）。Face 以外では 0
    float3 hairT1;              // ずらした繊維接線（1層目）
    float3 hairT2;              //             （2層目）
    float  hairSparkle;         // 毛束の粒。副バンドを割る 0..1
    float3 energyComp;          // 多重散乱の補償倍率
    // 正面・上向きの陰の持ち上げ（FR-31）。**法線と向きだけで決まるので光源非依存。**
    // 逆光で消す係数だけがライトごとに変わる。
    float  sheenScale;          // 布の下地の縮小率（sheen のエネルギー保存）
    float3 clothT;              // 織りの向き（Cloth）。接線か Anisotropy Map の向き。光源非依存（T-419）
    float  clothAniso;          // Cloth Anisotropy × Anisotropy Map の B
};


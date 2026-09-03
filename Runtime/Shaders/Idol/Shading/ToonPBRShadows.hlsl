// 影 2 種（HQ セルフ / マイクロ。コンタクト・前髪は T-344 で廃止）。**順序の都合で頬の赤みも含む** ── HLSL は宣言順に解析するので、切り出しで並びは変えられない
//
// `ToonPBRCommon.hlsl` から切り出した（T-212。当時バイト一致を確認）。
// T-340 で純関数の本体を EasyShaderCore へ寄せ、ToonIGN は 1 行の前方転送になった。
// Toon* の名前を残すのは、静的検査（E008〜E012 / W108）が Toon 接頭辞だけを
// 検査対象にするためと、呼び出し側を 1 文字も触らないため。
//
// **並び順を変えないこと。** HLSL は宣言順に解析するので、
// この include の順序がそのまま依存関係になっている。

// ----------------------------------------------------------------------------
//  高品質セルフシャドウ（主光源専用）
//
//  URP 標準の影をそのまま伝達関数に通すと、シャドウマップ由来のジャギーと
//  アクネが境界にそのまま乗る。曲率でソフトさを作っても入力が汚れていては意味が無い。
//  発生源で潰すために、受け側の法線オフセット + 回転 Vogel ディスク PCF を使う。
//
//  設計は EasyPBR (com.origuma.easypbr-urp) の Shadow_HQ_URP.hlsl に倣った。
//  Core の同関数は接触影・髪影・ディザ統合を持たない旧形なので、影本体は
//  自前のまま（命名は Toon 前置詞）。純関数（IGN）だけ Core と共有する（T-340）。
//  追加光源は URP 標準のまま（多灯時の負荷を増やさない）。
// ----------------------------------------------------------------------------
// タップ数はトゥーンステップの遷移窓と釣り合わせること。
// 8 タップだと1サンプルの重みが 0.125 で、既定設定の遷移窓 0.086 より**太い**。
// ライトを回すとサンプルが1つ入れ替わるだけで影が全反転する（＝ちらつき）。
#define TOON_SHADOW_TAPS 16

// 黄金角で回すディスク。少ないタップでも偏りが出にくい。
// 当初この最適化は Idol だけが持っていたが、T-340 で **Core 側へ逆輸入**して
// float2 オーバーロードになった（Doll の影も同じ削減を得る）。
// 同値になったので本体は Core に置き、ここは前方転送。
/// <summary>
/// 黄金角で回すディスク。**位相は呼び出し側で1回だけ sincos し、ここでは回転で当てる。**
///
/// 元は `sincos(i * 黄金角 + phi)` を毎タップ呼んでいた。phi が実行時の値なので、
/// 展開されたタップの数だけ sincos が残る ── 影 16 + ブロッカー 8 + 前髪 8 = **32 回**。
///
/// 位相を外に出すと `sincos(i * 黄金角)` は **i が定数のときコンパイル時に畳まれる**
/// （UNITY_UNROLL / [unroll] で展開しているので必ず定数）。
/// 残るのは 2x2 回転（積4・和2）だけになり、sincos は位相ごとに 1 回で済む。
///
/// **加法定理そのものなので結果は完全に等価**（全 24 タップ × 5 位相で数値検証、
/// 最大差 2.8e-15 = 浮動小数の丸め以下）。絵は 1 ビットも変わらない。
/// </summary>
/// <param name="sc">位相の (sin, cos)。呼び出し側で `sincos(phi, sc.x, sc.y)` として作る。</param>
float2 ToonVogelDisk(int i, int count, float2 sc)
{
    // 本体は EasyShaderCore の VogelDisk(int, int, float2)（この実装を逆輸入したもの）。
    return VogelDisk(i, count, sc);
}

/// <summary>位相の (sin, cos) を作る。ループの外で1回だけ呼ぶこと。</summary>
float2 ToonDiskPhase(float phi)
{
    float s, c;
    sincos(phi, s, c);
    return float2(s, c);
}

// 画面座標のインターリーブド勾配ノイズ。回転位相に使う。
// 本体は EasyShaderCore の IGN（実装がバイト同値だったため T-340 で転送化）。
float ToonIGN(float2 pix)
{
    return IGN(pix);
}

// ブロッカー探索。遮蔽物までの距離からペナンブラ幅を決める（接地硬化）。
// ブロッカー探索はフィルタほどタップが要らない。**平均の遮蔽物深度**を求めるだけで、
// その値は半影の幅を決めるのに使われた後、フィルタ側で改めてぼかされる。
// フィルタと同数にすると、PCSS を有効にした瞬間にシャドウマップのサンプルが倍になる。
#define TOON_BLOCKER_TAPS 8

void ToonFindBlocker(float2 baseUV, float receiverZ, float2 texel, float phi,
                     float searchRadius, out float avgBlockerZ)
{
    float sumZ = 0.0;
    float sumW = 0.0;

    // **タップを二値で数えない。**
    // 以前は `if (z > receiverZ) { sumZ += z; count++; }` と数え上げていたが、
    // これだとタップがしきい値をまたいだ瞬間に count が 1 段変わり、
    // `sumZ / count` が**不連続に飛ぶ**。半影の幅がそこで階段状に変わるので、
    // カメラやライトが少し動くだけでフィルタ幅が切り替わり、**ちらつきになる**。
    // 8 タップしかないので 1 本の増減が 12.5% 以上の変化になり、影響が大きい。
    //
    // 手前にある度合いで重み付けすれば、しきい値の出入りが連続になる。
    // 窓幅は半影スケールの逆数から取る ── そのスケールが「意味のある深度差」を
    // 定義しているので、その 1/10 なら推定値を歪めずに段差だけ均せる
    // （既定 200 で窓 0.0005。radius が動き出す深度差 0.0018 の 1/3.6）。
    float window = 0.1 / max(_ShadowPenumbraScale, 1e-4);

    // 位相はループの外で1回だけ。中で作ると意味が無い。
    float2 phase = ToonDiskPhase(phi);

    UNITY_UNROLL
    for (int i = 0; i < TOON_BLOCKER_TAPS; i++)
    {
        float2 o = ToonVogelDisk(i, TOON_BLOCKER_TAPS, phase) * texel * searchRadius;
        float  z = SAMPLE_TEXTURE2D_LOD(_MainLightShadowmapTexture, sampler_PointClamp,
                                        baseUV + o, 0).r;

        // 遮蔽物なら正。リバース Z では手前ほど z が大きい。
    #if UNITY_REVERSED_Z
        float d = z - receiverZ;
    #else
        float d = receiverZ - z;
    #endif

        float w = saturate(d / window);
        sumZ += z * w;
        sumW += w;
    }

    // 遮蔽が無ければ receiverZ を返す。半影 0 → radius は下限に落ちる。
    avgBlockerZ = (sumW > 1e-4) ? (sumZ / sumW) : receiverZ;
}

/// <summary>
/// 主光源の影を自前でサンプルする。戻り値 0（影）〜1（光）。
/// normalWS と NdotL は法線マップを掛ける前の値を渡すこと
/// （細部のノイズがオフセット量に乗るとアクネが戻る）。
/// </summary>
half ToonSampleMainShadowHQ(float3 positionWS, float3 normalWS, float NdotL, float dither,
                            half fallback)
{
// 自前サンプルが成立するのはシャドウマップを直接読める経路だけ。
//   - キーワードが無い          → 影そのものが無い
//   - _MAIN_LIGHT_SHADOWS_SCREEN → URP は画面空間の影テクスチャを読む。
//     シャドウマップを読んでも正しくないし、1 を返すと**影が消える**
// どちらの場合も URP が出した値をそのまま返す。
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN) ||     (!defined(_MAIN_LIGHT_SHADOWS) && !defined(_MAIN_LIGHT_SHADOWS_CASCADE))
    return fallback;
#else
    // 受け側の法線オフセット。傾いた面ほど強く押し出してアクネを消す。
    float slope = saturate(1.0 - NdotL);

    // **バイアスはメートル基準に戻した（T-124 で撤回）。**
    //
    // 「量子化を跨ぐのだからテクセル基準が正しい」という理屈は今も正しいが、
    // カスケードのテクセル寸法を実行時に安全に取る手段が無かった。試した2つ:
    //
    //   1. 画面微分の比      → 三角形ごとに一定になり**ポリゴン形の汚れ**（T-120）
    //   2. 5cm ずらした点の投影 → `TransformWorldToShadowCoord` はカスケード選択を含む。
    //      ずらした点が別カスケードへ落ちると UV がアトラス上を飛び、
    //      texelW が下限へ張り付いて**バイアスが消え、アクネが明滅する**（T-124）
    //
    // どちらもユーザーの実機で見えるレベルの破綻を出した。
    // カスケードの世界寸法は URP が公開しておらず、内部関数に踏み込むと
    // バージョン依存になる。**理屈の正しさより安定を優先する。**
    //
    // 遠景でバイアスが相対的に小さくなる問題は残るが、
    // それは診断（テクセル密度の表示）で数値を出して手で合わせる方針にした。
    float3 offsetPos = positionWS + normalWS * (_ReceiverNormalBias * (0.5 + slope)) * 0.01;

    float4 coord = TransformWorldToShadowCoord(offsetPos);
    float2 texel = _MainLightShadowmapSize.xy;
    float  phi   = dither * TOON_PI * 2.0;   // フィルタ用。画素ごとに回して縞をノイズへ散らす

    float radius = 1.0 + _HQShadowSoftness * 6.0;

    UNITY_BRANCH
    if (_ShadowContactHardening > 0.5)
    {
        // **回転は画素ごと。固定角も試したが撤回した（T-109 → T-124）。**
        //
        // ここの出力は最終的な影の値ではなく「半影の幅」という滑らかな量で、
        // フィルタ側で改めて 16 タップ平均される。8 タップの当たり外れが
        // そのまま半径のばらつきになるので、回し方で症状の出方が変わる:
        //
        //   画素ごとに回す → 半径が画素ごとにばらつく（**まだら**）
        //   固定角        → 全画素が同時にテクセル境界を踏む（**画面全体の明滅**）
        //
        // 実機で比べた結果、**明滅の方が目に付いた**ので画素ごとに戻した。
        // 不連続そのものはタップの重み付け（ToonFindBlocker の window）で
        // 潰してあるので、残差はノイズとして 16 タップ平均に吸収される。
        //
        // **そもそも接地硬化はここでは物理ではない。**
        // 平行光源の真の半影幅は「遮蔽物までの距離 × tan(視半角) × 2」で、
        // 太陽（視直径 0.53 度）なら距離 × 0.00925。キャラの自己遮蔽では:
        //
        //   鼻→頬  2cm → 0.19mm
        //   顎→首 10cm → 0.93mm
        //   腕→胴 20cm → 1.85mm
        //   頭→床 1.5m → 13.9mm   ← ここだけは物理的にも意味がある
        //
        // **テクセル寸法はここに書かない。** URP アセットの設定で変わるので、
        // 書き写すと必ず古くなる ── 実際「約 4.9mm / 顔 31 テクセル」と書いていたが、
        // カスケードの分割が変わって 2.93mm / 51 テクセルになっていた（T-155）。
        // 現在値は Unity 側の診断（テクセル密度）が設定から計算して出す。
        //
        // 判断の仕方: **上の半影幅が 1 テクセルに届いていないなら、
        // 接地硬化が作っている変化は物理ではなく演出。**
        // 揺れが気になるなら切って構わない ── 物理的に失うものは無い。
        //
        // **どちらも根本解決ではない。** 8 タップでブロッカー深度を推定する以上、
        // 推定値には必ず分散が残る。効くのは分散そのものより**半径の可動域**で、
        // `1.0 〜 (1 + _HQShadowSoftness * 6) * 3` テクセルまで振れる
        // （既定 0.3 なら 1.0〜8.4）。ここが気になるなら
        // `_ShadowPenumbraScale`（既定 200）を下げて可動域を狭めるのが直接的
        // ── 接地硬化は弱くなるが、半影の揺れも比例して収まる。
        float avgBlockerZ;
        ToonFindBlocker(coord.xy, coord.z, texel, phi, radius * 1.5, avgBlockerZ);

        // **遮蔽物の絶対深度で割らないこと。** 割るのは点光源の相似三角形の式で、
        // 平行光源のシャドウマップは**正射影なので深度差がそのまま距離に比例する**。
        // 割ると「ライトの近平面からどれだけ離れているか」という無関係な量で
        // 半影が変わり、キャラが歩くだけで影の柔らかさが揺れる
        // （同じ 10cm の隙間で、遮蔽物の深度 0.9 と 0.15 で半影が 6 倍違った）。
        //
        // 深度差を世界距離へ直すにはカスケードの奥行きが要るが、URP は
        // それを露出していない。倍率をプロパティに出して調整可能にする。
        float penumbra = abs(coord.z - avgBlockerZ) * _ShadowPenumbraScale;
        radius = clamp(penumbra * radius, 1.0, radius * 3.0);
    }

    half atten = 0.0h;

    // **決定的な格子フィルタ（7×7 テント）は試して撤回した（T-394）。**
    // 粒は消えるが、格子の形が半影に出て、Penumbra を上げると突起が並ぶ
    // （利用者評価）。Vogel＋画素ごとの回転は「構造をノイズに散らす」ための
    // 手法で、構造が見えるより粒の方がまし、という判断に戻った。
    float2 filterPhase = ToonDiskPhase(phi);

    UNITY_UNROLL
    for (int i = 0; i < TOON_SHADOW_TAPS; i++)
    {
        float2 o = ToonVogelDisk(i, TOON_SHADOW_TAPS, filterPhase) * texel * radius;
        atten += SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture,
                                         sampler_LinearClampCompare,
                                         float3(coord.xy + o, coord.z));
    }
    atten /= TOON_SHADOW_TAPS;

    // ライト側の Shadow Strength を掛ける。URP の SampleShadowmap は必ずこれを
    // 通しており、省くと HQ を ON にした瞬間スライダが効かなくなる。
    atten = LerpWhiteTo(atten, GetMainLightShadowParams().x);

    // **「遮蔽物が見つからなければ 1.0」という二値の上書きは置かない。**
    // 以前はここで `atten = fullyLit ? 1.0h : atten;` としていたが、
    // 判定元は 8 タップの点サンプルで、当たり外れが画素ごとに入れ替わる。
    // 半影の縁では 0 個か 1 個かがほぼコイン投げになり、
    // **16 タップかけて平均した値を捨てて 1.0 に飛ばす画素が斑に混ざる。**
    // コンタクトシャドウで「二値で取らない」と書いたのと同じ理由でここも駄目だった。
    //
    // 外しても実害は無い。遮蔽物が見つからなければ avgBlockerZ = receiverZ となり
    // 半影 0 → radius は下限の 1 テクセルに落ちる。探索半径（radius*1.5）の中に
    // 遮蔽が無いのだから、1 テクセルのフィルタは当然ほぼ 1.0 を返す。
    // 逆に、点サンプル 8 本が取りこぼした遮蔽をハードウェア PCF が拾った場合は、
    // 上書きを外したぶんだけ**正しい**部分遮蔽が残る。

    // 距離フェードは URP と同じ扱いにする。
    half fade = GetMainLightShadowFade(positionWS);
    return lerp(atten, 1.0h, fade);
#endif
}

// ----------------------------------------------------------------------------
//  マイクロシャドウ (Naughty Dog / The Order の手法)
//
//  シャドウマップは大きな遮蔽しか拾えないが、実際には布の織りや皺の谷、
//  細かい凹凸が「光が斜めから入るほど」自分自身を隠す。AO と NdotL の
//  組み合わせでその減衰を作る。テクスチャも追加パスも要らない。
//
//  AO が 1（＝マップ未設定）なら常に 1 を返すので、既存のマテリアルは変わらない。
//  SSAO を有効にしている場合はそちらの遮蔽にも反応する。
// ----------------------------------------------------------------------------
float ToonMicroShadow(float NdotL, float ao)
{
    float micro = saturate(abs(NdotL) + 2.0 * ao * ao - 1.0);
    return lerp(1.0, micro, _MicroShadow);
}

// コンタクトシャドウ（スクリーンスペース）は T-344 で廃止した。
// 画面外の遮蔽物が影を落とさない・ディザが TAA 無しで這うという
// 画面空間サンプリング原理の弱点（リムの深度差方式と同じ系統）が理由。
// 接地・近接の影は HQ セルフシャドウ（シャドウマップ）に一本化する。

// ----------------------------------------------------------------------------
//  頬の赤み（Blush）は T-349 で廃止した。肌・顔のバリアントでは
//  NdotV の帯と lerp が**強度 0 でも毎ライト毎ピクセル走っていた**うえ、
//  プロジェクト内 46 マテリアルすべてが強度 0 ＝ 誰も使っていなかった。
//  頬の色は肌テクスチャに描くか、Skin - Subsurface（皮下散乱）で出す。

// 前髪の影（頭上からの専用正射影）は T-344 で廃止した。
// 投影がライト方向と無関係（常に真下）で、動くライトでは原理的に
// 嘘が出るのが理由。前髪→顔の影は HQ セルフシャドウで出す。

// EasyPBR (Doll) のマテリアルを ToonPBR (Idol) へ移す。
//（旧 Cel からの移行経路は T-356 の Cel 廃止と同時に撤去した。
//  Cel 材質が残る旧プロジェクトは、Cel が同梱されていた版のパッケージで移行する。）
//
// **なぜスクリプトが要るか。**
// Unity はシェーダーを差し替えたとき、**名前と型が一致するプロパティだけ**を引き継ぐ。
// 実測した重なりはこれだけしかない:
//
//     Idol (EasyToon) 133 個のうち ToonPBR にも同名があるのは  29
//     Doll (EasyPBR)  175 個のうち ToonPBR にも同名があるのは  38
//
// 残りは「名前が違うだけで同じもの」と「ToonPBR に無い機能」が混ざっている。
// 手で差し替えると前者が**黙って既定値に戻る。** 絵は出るので気付きにくい。
//
// **黙って落とさないこと。** このツールの半分は移行そのものだが、
// もう半分は「何を持って来られなかったか」を並べることにある。
// 落ちたものを黙っているのは、このプロジェクトが繰り返し踏んでいる形
// （実装したのに効いていない / 設定したのに反映されていない）そのもの。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ToonNPR.EditorTools
{
    public sealed class ToonPBRMigrator : EditorWindow
    {
        private const string DollShader   = "Origuma/EasyPBR_URP/Doll";
        private const string TargetShader = "Origuma/EasyToon_URP/Idol";

        // ------------------------------------------------------------------
        //  対応表
        // ------------------------------------------------------------------

        private enum Kind { Tex, Color, Number }

        private readonly struct Rule
        {
            public readonly Kind Kind;
            public readonly string Src;
            public readonly string Dst;
            public readonly Func<float, float> Map;   // Number のみ。null なら素通し
            public readonly string Note;

            /// <summary>
            /// **移行元のトグル。** これが OFF のマテリアルではこのルールを飛ばす。
            ///
            /// 移行元に「値は入っているが機能は切ってある」状態がありうる
            /// （色補正がまさにそれで、Idol も Doll も `_UseColorCorrection` で
            /// 切っている）。値だけ運ぶと**移行しただけで絵が変わる。**
            /// 名前が一致していても意味が一致するとは限らない、の一種。
            /// </summary>
            public readonly string Gate;

            public Rule(Kind kind, string src, string dst,
                        Func<float, float> map = null, string note = null,
                        string gate = null)
            {
                Kind = kind; Src = src; Dst = dst; Map = map; Note = note; Gate = gate;
            }
        }

        // **値域の変換を忘れないこと。** 素通しすると `Range` の外に出て、
        // ToonPBR 側で lerp の外挿になる（param_check の「Range を外れた値」）。
        // Range はスライダを縛るだけで実行時は縛らないので、.mat に残った値は生きる。
        private static float Clamp(float v, float lo, float hi) => Mathf.Clamp(v, lo, hi);

        /// **正解データで裏が取れた変換。**
        ///
        /// 手で詰めた 46 マテリアルと、その移行元（`Materials 1` / `Materials 2`）を
        /// 突き合わせて確かめた。ただし**「一致した」だけでは意味が無い** ──
        /// 移行先が ToonPBR の既定値のままなら、どんな変換でも
        /// たまたま合うことがある（実際 `_SkinScatterWidth → _TerminatorSharpness` の
        /// 46/46 一致は、移行先が全件既定値だったための偶然だった）。
        ///
        /// ここに挙げるのは**移行先が既定値ではなく、かつ一致した**もの＝
        /// 前回の移行で実際に運ばれ、絵を見て残された値。
        ///
        /// 挙がっていない変換は**裏が取れていない。** 報告で名指しする。
        ///
        /// **再検証を試みて、増やせなかった（T-286）。** 移行元は残っている
        /// （`Materials 1` = Doll / `Materials 2` = Cel / `Materials 3` = Idol）ので
        /// 同じ方法を回したところ、素通しルール 46 件のうち 11 件が一致した。
        /// **だが 10 件は偽の確認だった。**
        ///
        /// 上のコメントは「移行先が既定値のままなら偶然合う」と警告しているが、
        /// **それだけでは足りない。** 実際に踏んだ形:
        ///
        ///   - `_IndirectIntensity → _AmbientIntensity` が 46/46 一致した。
        ///     だが Doll 側も 1、Idol 側も 1 で、**利用者が 2 から 1 へ戻した結果の偶然**。
        ///     移行が運んだ証拠にはならない
        ///   - `_AlphaClip → _AlphaClipOn` のような**二値**は 50/50 で当たる
        ///   - 46 件が全部同じ値なら、どんな対応でも 46/46 になる
        ///
        /// **値がばらついているものだけが証拠になる。**
        /// 「移行先が既定でない」に加えて「**移行元に 3 種類以上の値がある**」を
        /// 条件にすると、残ったのは `_ReceiveShadowStrength`（3 種類）**1 件だけ**で、
        /// それは既にここに挙がっていた。**増やせるものは無かった。**
        ///
        /// 逆向き（素通しなのに一定倍ずれている＝係数の誤り）も探したが、
        /// **1 件も無かった。**
        private static readonly HashSet<string> ConfirmedRules = new HashSet<string>
        {
            "_ReceiveShadowStrength", "_ShadowHueShift",
            "_ReceiverNormalBias", "_OutlineWidth",
        };

        /// **既定どうしを合わせて係数を決めたルール。**
        ///
        /// 尺度が違う量（効きの係数、厚みなど）は、絶対値を保っても意味が無い。
        /// 根拠になるのは「**移行元の既定が移行先の既定へ落ちる**」ことだけ
        /// ── そうしておけば「既定のまま触っていないマテリアル」が
        /// 移行で見た目を変えない（T-193 / T-194）。
        ///
        /// **意図をここに宣言させ、機械で確かめる。**
        /// 宣言しておかないと、あとでルールを足す人が係数を勘で置いても誰も気付かない。
        /// 挙げていない量（強度・角度・画素幅など同じ単位のもの）は
        /// 絶対値を保つのが正しいので、既定がずれていて構わない。
        ///
        /// **移行元が 2 つあるので、両方で成り立つものだけを宣言できる。**
        /// `_ToonFeather` は Idol の既定 0.12 が ToonPBR の 0.12 と一致するが、
        /// **Doll の既定は 0.2 で一致しない。** ここに宣言してしまい、
        /// 検査が Doll 側で撃った ── 「両者の既定はどちらも 0.12」という
        /// 私の読みが、**Idol しか見ていなかった。**
        /// 尺度が同じという結論（＝素通し）は変えないが、
        /// **既定が合うという主張は取り下げる。**
        private static readonly HashSet<string> AnchoredRules = new HashSet<string>
        {
            // lint:foreign-begin  ここは**移行元**のプロパティ名
            "_SpecularAA",            // → _SpecAAVariance        1.0 → 0.15
            "_RimThickness",          // → _RimFresnelPower       0.2 → 2.5
            "_IridescenceThickness",  // → 同名                   3.0 → 1.0
            // lint:foreign-end
        };

        // lint:foreign-begin
        // ここから先は**移行元**（EasyToon / EasyPBR）のプロパティ名を書く。
        // ToonPBR に無くて当然なので W107 の対象から外す。
        private static readonly Rule[] CommonRules =
        {
            new Rule(Kind.Tex,   "_MainTex",    "_BaseMap"),
            new Rule(Kind.Tex,   "_NormalMap",  "_BumpMap"),
            new Rule(Kind.Number,"_NormalScale","_BumpScale"),
            new Rule(Kind.Number,"_AlphaClip",  "_AlphaClipOn"),

            // 素のアルベドの HSV 補正（T-225 で ToonPBR 側に実装）。
            // **`_UseColorCorrection` で切ってあるマテリアルからは運ばない。**
            // Idol も Doll も値を残したまま機能だけ切れる作りなので、
            // 素通しすると「移行しただけで色が変わる」ものが出る。
            // 値域は両側とも -0.5..0.5 / 0..2 / 0..2 で一致しているので変換は不要。
            // MatCap（T-235）。**`_MatCapBlend` は運ばない** ── ToonPBR は加算しか
            // 持たないので、Multiply を選んでいたマテリアルは意味が変わる。
            // 下見の報告に「落ちる」として出るので、そこで気付ける。
            new Rule(Kind.Tex,   "_MatCapTex",       "_MatCapTex",       null, null, "_UseMatCap"),
            new Rule(Kind.Color, "_MatCapColor",     "_MatCapColor",     null, null, "_UseMatCap"),
            new Rule(Kind.Number,"_MatCapIntensity", "_MatCapIntensity", null, null, "_UseMatCap"),

            // 正面・上向きの陰の持ち上げ（旧 _FrontLift* 系）は T-370 で廃止した。
            // 移行先が無いため運ばない ── 落ちたことは下見の報告に出る。

            // ディゾルブ（T-233）。名前も値域も同じなので素通し。
            // **`_UseDissolve` は運ばない** ── ToonPBR はキーワードを持たず
            // `_DissolveAmount` で切るので、トグルに対応する行き先が無い。
            new Rule(Kind.Number,"_DissolveAmount",       "_DissolveAmount",       null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveInvert",       "_DissolveInvert",       null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveType",         "_DissolveType",         null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveStartY",       "_DissolveStartY",       null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveEndY",         "_DissolveEndY",         null, null, "_UseDissolve"),
            new Rule(Kind.Tex,   "_DissolveTex",          "_DissolveTex",          null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveNoiseScale",   "_DissolveNoiseScale",   null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveNoiseStrength","_DissolveNoiseStrength",null, null, "_UseDissolve"),
            new Rule(Kind.Color, "_DissolveEdgeColor",    "_DissolveEdgeColor",    null, null, "_UseDissolve"),
            new Rule(Kind.Color, "_DissolveEdgeColor2",   "_DissolveEdgeColor2",   null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveEdgeWidth",    "_DissolveEdgeWidth",    null, null, "_UseDissolve"),
            new Rule(Kind.Number,"_DissolveEdgeStep",     "_DissolveEdgeStep",     null, null, "_UseDissolve"),


            new Rule(Kind.Number,"_HueShift",   "_AlbedoHueShift",   null, null, "_UseColorCorrection"),
            new Rule(Kind.Number,"_Saturation", "_AlbedoSaturation", null, null, "_UseColorCorrection"),
            // **明度だけ挙動が違う。** ToonPBR は反射率として 1 で頭打ちにする
            // （エネルギー保存のため）。移植元は抑えていないので、
            // 1 を超える設定のマテリアルだけ暗く出る。
            new Rule(Kind.Number,"_ValueMulti", "_AlbedoValue",      null,
                     "ToonPBR 側は結果を 1 で頭打ちにする（アルベドは反射率のため）",
                     "_UseColorCorrection"),

            // 拡散の伝達関数。名前が総入れ替えになっている。
            new Rule(Kind.Number,"_ToonStep",   "_ShadowThreshold"),
            // **値域が違うからといって尺度が違うとは限らない。**
            // 最初「Feather は 0〜1、Softness は 0.001〜0.5 だから半分」と書いたが、
            // **両者の既定値はどちらも 0.12 で一致していた。**
            // 同じ量に同じ既定を置いているということなので、尺度は同じ。
            // `× 0.5` はすべての影の境界を半分に細めてしまう。
            // 値域の違いは上限の余裕の差でしかなく、切り詰めるだけでよい。
            new Rule(Kind.Number,"_ToonFeather","_ShadowSoftness",
                     v => Clamp(v, 0.001f, 0.5f),
                     "上限 0.5 で切り詰めるだけ（既定はどちらも 0.12 で一致）"),

            new Rule(Kind.Tex,   "_ShadeNormalMap",      "_ShadeNormalMap"),
            new Rule(Kind.Number,"_ShadeNormalStrength", "_ShadeNormalStrength"),
            new Rule(Kind.Number,"_HalfLambertWrap",     "_DiffuseWrap"),
            new Rule(Kind.Number,"_ReceiveShadowStrength","_ReceiveShadowStrength"),

            // 影色。**ToonPBR では `_ShadowColor` だけでは何も起きない。**
            // 色相を混ぜる率 `_ShadowColorMix` が 0 のままだと乗らないので、
            // 移行時に明示的に立てる（下の ApplyOne で処理）。
            new Rule(Kind.Color, "_ShadowColor",     "_ShadowColor"),
            new Rule(Kind.Number,"_ShadowHueShift",  "_ShadowHueShift",
                     v => Clamp(v, -0.2f, 0.2f), "±0.5 を ±0.2 へ丸めた"),
            new Rule(Kind.Number,"_ShadowSaturation","_ShadowSaturation"),
            new Rule(Kind.Color, "_CastShadowColor", "_CastShadowColor"),

            // Doll の Skin Scatter（境界帯の色付け）は運ばない。Idol 側の受け皿だった
            // Terminator は T-392 で廃止 ── 境界の色は Ramp Override（生成 UI）で置く。

            // 鏡面
            new Rule(Kind.Number,"_Smoothness",         "_Smoothness"),
            // 同名だが**値域が違う**（移行元 0〜5 / ToonPBR 0〜4）。
            // 素通しすると Range の外に出て、`check_ranges` が撃つ値になる。
            // 実データに 5.0 のマテリアルが 1 個あった。
            new Rule(Kind.Number,"_SpecularIntensity",  "_SpecularIntensity",
                     v => Clamp(v, 0.0f, 4.0f), "0〜5 を 0〜4 へ丸めた"),
            new Rule(Kind.Color, "_SpecularColor",      "_SpecularTint"),
            // **ToonPBR に `_ReflectionStrength` は無い。** 同じ意味の受け皿は
            // `_EnvSpecIntensity`（どちらも 0〜2）。同名だと思い込んで書いていたが、
            // `.mat` に `_ReflectionStrength` が残っているのは**旧シェーダーの遺物**で、
            // ToonPBR は読んでいない ── 移行検査（T-186）が拾った。
            new Rule(Kind.Number,"_ReflectionStrength", "_EnvSpecIntensity"),
            // SpecularAA は「効き」、ToonPBR の Variance は法線分散の係数で尺度が違う。
            // **係数は既定どうしを合わせて決める**（移行元 1.0 → ToonPBR 0.15）。
            // 最初 1/5 と置いていたが、それだと既定のままのマテリアルが
            // 0.2 になって ToonPBR の既定より強く荒れる。
            new Rule(Kind.Number,"_SpecularAA",         "_SpecAAVariance",
                     v => Clamp(v * 0.15f, 0.0f, 1.0f), "既定 1.0 が 0.15 に落ちる係数"),

            // 顔 SDF。**テクスチャは運ばない（T-382）。** Doll の SDF は 4ch
            // （R/G/B/A = 右/左/上/下・8bit）、Idol は 16bit 1ch（R×256+G）で
            // 形式が違う。運ぶと G（左光）が下位バイトとして読まれて顔が壊れる。
            // Idol 側の Baking タブで焼き直すこと（白のままなら SDF 無しと同じ絵）。
            new Rule(Kind.Number,"_FaceSDFFlip", "_FaceSDFFlipU"),

            // 髪の流れ
            new Rule(Kind.Tex,   "_HairFlowMap",     "_HairFlowMap"),
            new Rule(Kind.Number,"_HairFlowStrength","_HairFlowStrength"),

            // --- リム ------------------------------------------------------
            //
            // **移行元は「深度リム」と「フレネルリム」を別々に持っている。**
            // 最初 `_RimThickness → _RimWidth` と当てていたが、**別物だった。**
            //
            //   Idol `_RimWidthPx`   深度リムの**画素幅**（0.5〜16 px）
            //   Idol `_RimThickness` フレネルリムの**厚み**（0〜1）
            //   Doll `_RimThickness` 同上（Doll に深度リムは無い）
            //
            // ToonPBR 側:
            //   `_RimWidth`        `_RimWidth * 10 / 画面高` が画素数 ── **画面幅**
            //   `_RimFresnelPower` `pow(1 - NdotV, power)` ── **フレネルの落ち**
            //
            // フレネルの厚みを画面幅に流し込んでいたので、意味が通っていなかった。
            new Rule(Kind.Color, "_RimColor",     "_RimColor"),
            new Rule(Kind.Number,"_RimIntensity", "_RimIntensity"),


            // 厚み → フレネルの指数。**厚いほど指数は小さい**（緩く落ちる）ので逆数。
            //
            // **既定どうしが一致する形に合わせる。** 移行元の既定 0.2 が
            // ToonPBR の既定 2.5 に落ちるよう係数を 0.5 に取った（0.5 / 0.2 = 2.5）。
            // 「既定のまま触っていない」マテリアルが移行で見た目を変えないことは、
            // 変換を推測で書くときの唯一まともな足がかり。
            //
            // 最初 `Lerp(8, 0.5, v)` と書いて「既定 0.2 が 2.5」とコメントしたが、
            // **実際は 6.5 だった。** 数字を書いたら必ず通して確かめること。
            new Rule(Kind.Number,"_RimThickness", "_RimFresnelPower",
                     v => Clamp(0.5f / Mathf.Max(v, 0.0625f), 0.1f, 8.0f),
                     "厚みの逆数をフレネル指数へ（既定 0.2 → 2.5 で一致）"),



            // 環境光
            new Rule(Kind.Number,"_IndirectFlatten", "_AmbientFlatten"),
            new Rule(Kind.Number,"_IndirectIntensity","_AmbientIntensity"),

            // 発光・輪郭
            new Rule(Kind.Number,"_UseEmission",  "_EmissionOn"),
            new Rule(Kind.Tex,   "_EmissionMap",  "_EmissionMap"),
            new Rule(Kind.Color, "_EmissionColor","_EmissionColor"),
            new Rule(Kind.Number,"_UseOutline",   "_OutlineOn"),
            new Rule(Kind.Color, "_OutlineColor", "_OutlineColor"),
            // **Idol と Doll で値域が違う**（Idol 0〜20 mm / Doll 0〜10）。
            // ToonPBR は 0〜10 なので、Idol からの移行だけがはみ出す。
            // 実データの最大は 1.0 なので実害は無いが、上限を触れば出る。
            // **静的検査は Idol と Doll の Range をマージしていて見逃していた**
            // ── 実際に走らせて初めて出た（T-189）。
            new Rule(Kind.Number,"_OutlineWidth", "_OutlineWidth",
                     v => Clamp(v, 0.0f, 10.0f), "Idol は 0〜20 なので上限で丸めた"),
            new Rule(Kind.Number,"_OutlineAlbedoBlend","_OutlineAlbedoBlend"),
        };

        // Doll (EasyPBR) にしか無いもの。
        private static readonly Rule[] DollRules =
        {
            new Rule(Kind.Number,"_ShadowMapSoftness", "_ShadowAttenSoftness",
                     v => Clamp(v, 0.001f, 1.0f)),
            new Rule(Kind.Number,"_ReceiverNormalBias","_ReceiverNormalBias"),

            new Rule(Kind.Tex,   "_BentNormalMap",     "_BentNormalMap"),
            new Rule(Kind.Number,"_BentNormalStrength","_BentNormalOn",
                     v => v > 0.0f ? 1.0f : 0.0f, "強度をトグルへ畳んだ"),
            new Rule(Kind.Tex,   "_CavityMap",         "_CavityMap"),
            new Rule(Kind.Number,"_CavityStrength",    "_CavityStrength",
                     v => Clamp(v, 0.0f, 1.0f)),
            new Rule(Kind.Tex,   "_CurvatureMap",      "_CurvatureMap"),
            // Doll の Strength（0..1 の混合率）は Idol では Influence（境界幅の倍率）。
            // 0 なら 0、使っていれば 1 に畳む（T-381 でマップが唯一の供給源になった）。
            new Rule(Kind.Number,"_CurvatureStrength", "_CurvatureSoftness",
                     v => v > 0.0f ? 1.0f : 0.0f, "混合率を倍率 1 へ畳んだ"),
            new Rule(Kind.Number,"_OcclusionStrength", "_OcclusionStrength",
                     v => Clamp(v, 0.0f, 1.0f)),

            // 透過（SSS）
            new Rule(Kind.Tex,   "_SSSMap",       "_SSSMap"),
            new Rule(Kind.Color, "_SSSColor",     "_TransmissionColor"),
            new Rule(Kind.Number,"_SSSIntensity", "_TransmissionStrength",
                     v => Clamp(v, 0.0f, 4.0f)),
            new Rule(Kind.Number,"_SSSPower",     "_TransmissionPower",
                     v => Clamp(v, 1.0f, 16.0f)),
            new Rule(Kind.Number,"_SSSDistortion","_TransmissionDistortion"),

            // クリアコート・虹色
            new Rule(Kind.Number,"_ClearcoatStrength",   "_ClearcoatStrength"),
            new Rule(Kind.Number,"_ClearcoatSmoothness", "_ClearcoatSmoothness"),
            new Rule(Kind.Number,"_IridescenceIntensity","_IridescenceIntensity"),
            // 0〜8 を 0〜4 へ。**係数は既定どうしを合わせて 1/3**（3.0 → 1.0）。
            // 単純に半分にすると既定のままで 1.5 になり、ToonPBR の既定より厚くなる。
            new Rule(Kind.Number,"_IridescenceThickness","_IridescenceThickness",
                     v => Clamp(v / 3.0f, 0.0f, 4.0f), "既定 3.0 が 1.0 に落ちる係数"),
            new Rule(Kind.Number,"_IridescenceShift",    "_IridescenceShift"),

            // 髪の異方性。Doll は Aniso*、ToonPBR は HairSpec*。
            new Rule(Kind.Color, "_AnisoColor",         "_HairSpecColor1"),
            new Rule(Kind.Number,"_AnisoOffset",        "_HairShift1"),
            // Thickness が厚いほどハイライトは鈍い ＝ Smoothness は低い。
            new Rule(Kind.Number,"_AnisoThickness",     "_HairSmoothness1",
                     v => Clamp(1.0f - v, 0.0f, 1.0f), "Thickness の補数を Smoothness に"),
            new Rule(Kind.Color, "_AnisoSecColor",      "_HairSpecColor2"),
            new Rule(Kind.Number,"_AnisoSecOffset",     "_HairShift2"),
            new Rule(Kind.Number,"_AnisoSecThickness",  "_HairSmoothness2",
                     v => Clamp(1.0f - v, 0.0f, 1.0f), "同上"),
            new Rule(Kind.Number,"_AnisoStrandScale",   "_HairStrandScale",
                     v => Clamp(v, 0.0f, 200.0f)),
            new Rule(Kind.Number,"_AnisoStrandStrength","_HairStrandSparkle"),
        };

        // lint:foreign-end

        // ------------------------------------------------------------------
        //  移行本体
        // ------------------------------------------------------------------

        private sealed class Report
        {
            public Material Material;
            public string SourceShader;
            public readonly List<string> Mapped  = new List<string>();
            public readonly List<string> Kept    = new List<string>();
            // **「落ちる」を2つに分ける。** 実データで測ったら、落ちる 103 個のうち
            // アーティストが実際に動かしていたのは 27 個＋割当済みテクスチャ 19 個だけだった。
            // 全部並べると読めないし、読めない報告は読まれない。
            public readonly List<string> LostChanged = new List<string>();  // 既定から動いていた
            public readonly List<string> LostDefault = new List<string>();  // 既定のまま
            public readonly List<string> Notes   = new List<string>();

            // **書き込んだはずの値。** 適用後に読み直して突き合わせるために持つ
            // （`RunApplyCI` が使う）。読めたのに書けていない、を検出する唯一の手段。
            public readonly Dictionary<string, float>   Numbers  = new Dictionary<string, float>();
            public readonly Dictionary<string, Color>   Colors   = new Dictionary<string, Color>();
            public readonly Dictionary<string, Texture> Textures = new Dictionary<string, Texture>();
            public float SurfaceType;
        }

        /// <summary>
        /// そのプロパティが**既定値から動かされているか**。
        /// 比べる相手は「同じシェーダーで作りたてのマテリアル」。
        /// 既定値をここに書き写すと必ず古くなるので、Unity に作らせて読む。
        /// </summary>
        private static bool DiffersFromDefault(Material mat, Material fresh,
                                               Shader s, int index)
        {
            string name = s.GetPropertyName(index);
            if (!mat.HasProperty(name) || !fresh.HasProperty(name)) return false;

            switch (s.GetPropertyType(index))
            {
                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    // **テクスチャは「割り当てがあるか」で見る。**
                    // 既定テクスチャ（white / bump）と比べても意味が無い。
                    return mat.GetTexture(name) != null;
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                    return mat.GetColor(name) != fresh.GetColor(name);
                case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    return mat.GetVector(name) != fresh.GetVector(name);
                default:
                    return Mathf.Abs(mat.GetFloat(name) - fresh.GetFloat(name)) > 1e-6f;
            }
        }

        /// <summary>
        /// **シェーダーを差し替える前に値を読み切ること。**
        /// 差し替えた瞬間、新しいシェーダーに無いプロパティは Unity が捨てる。
        /// 読んでから差し替え、差し替えてから書く、の順でなければ移行にならない。
        /// </summary>
        private static Report Migrate(Material mat, Shader target, bool apply)
        {
            var rep = new Report { Material = mat, SourceShader = mat.shader.name };
            var rules = CommonRules.Concat(DollRules).ToArray();

            // --- (1) 読む -------------------------------------------------
            var texVal  = new Dictionary<string, Texture>();
            var texST   = new Dictionary<string, Vector4>();
            var colVal  = new Dictionary<string, Color>();
            var numVal  = new Dictionary<string, float>();

            foreach (var r in rules)
            {
                if (!mat.HasProperty(r.Src)) continue;

                // **移行元で機能が切ってあるなら値も運ばない。**
                // 運ぶと「移行しただけで絵が変わる」── 移行の目的に反する。
                if (r.Gate != null && mat.HasProperty(r.Gate) && mat.GetFloat(r.Gate) < 0.5f)
                    continue;

                switch (r.Kind)
                {
                    case Kind.Tex:
                        var t = mat.GetTexture(r.Src);
                        if (t == null) continue;              // 未割当は移さない
                        texVal[r.Dst] = t;
                        texST[r.Dst] = new Vector4(
                            mat.GetTextureScale(r.Src).x, mat.GetTextureScale(r.Src).y,
                            mat.GetTextureOffset(r.Src).x, mat.GetTextureOffset(r.Src).y);
                        break;
                    case Kind.Color:
                        colVal[r.Dst] = mat.GetColor(r.Src);
                        break;
                    case Kind.Number:
                        float v = mat.GetFloat(r.Src);
                        numVal[r.Dst] = r.Map != null ? r.Map(v) : v;
                        break;
                }

                string line = r.Src == r.Dst ? r.Src : $"{r.Src} → {r.Dst}";
                if (!string.IsNullOrEmpty(r.Note)) line += $"（{r.Note}）";
                rep.Mapped.Add(line);
            }

            // **ラップの正規化が違うので、しきい値と柔らかさを引き直す。**
            //
            //   移行元  `(NdotL + w) / (1 + w)`
            //   ToonPBR `(NdotL + w) / (1 + w)²`   ← エネルギー保存ぶんの余分な 1/(1+w)
            //
            // 伝達関数の形（`smoothstep(t - s, t + s, x)`）は**両者まったく同じ**なので、
            // 違うのは入力の尺度だけ。境界が同じ NdotL に来る条件を解くと:
            //
            //   移行元  : (N* + w)/(1 + w)  = t   →  N* = t(1 + w) - w
            //   ToonPBR : (N* + w)/(1 + w)² = T   →  N* = T(1 + w)² - w
            //   等号   : T = t / (1 + w)          柔らかさも同じ係数
            //
            // **既定はどちらも 0.5 / 0.12 で同じ数字なので、素通しでよく見える。**
            // 数字が同じでも入れる先の式が違えば別の絵になる（T-202 と同じ形）。
            // 既定の wrap 0.25 なら係数 0.8 ── 境界が 20% ぶん手前にずれる。
            //
            // `Rule` は値を 1 つしか見られないので、**読み取りの段階でここだけ引き直す。**
            // 適用後の照合（`RunApplyCI`）が控えた値と突き合わせるので、
            // 書いてから直すと検証が食い違う。
            // lint:foreign-begin  _HalfLambertWrap は移行元の名前
            if (mat.HasProperty("_HalfLambertWrap"))
            {
                float k = 1.0f / Mathf.Max(1.0f + mat.GetFloat("_HalfLambertWrap"), 1e-4f);

                if (numVal.TryGetValue("_ShadowThreshold", out var th))
                    numVal["_ShadowThreshold"] = Clamp(th * k, 0.0f, 1.0f);
                if (numVal.TryGetValue("_ShadowSoftness", out var so))
                    numVal["_ShadowSoftness"] = Clamp(so * k, 0.001f, 0.5f);

                rep.Notes.Add($"ラップの正規化差ぶん、しきい値と柔らかさを ×{k:0.###} した"
                            + "（移行元は 1/(1+w)、ToonPBR は 1/(1+w)²）");
            }
            // lint:foreign-end

            // サーフェスタイプは元シェーダーのトグルから決める。
            // 移行元は Doll のみ（旧 Cel 経路は T-356 で撤去）。
            float surface = DetectSurfaceType(mat, isDoll: true, rep);
            rep.SurfaceType = surface;

            // 適用後の突き合わせ用に控える。
            foreach (var kv in numVal) rep.Numbers[kv.Key] = kv.Value;
            foreach (var kv in colVal) rep.Colors[kv.Key] = kv.Value;
            foreach (var kv in texVal) rep.Textures[kv.Key] = kv.Value;

            // --- 落ちるものを数える（差し替える前でないと元の一覧が取れない）---
            var srcShader = mat.shader;
            var mappedSrc = new HashSet<string>(rules.Select(x => x.Src));
            var targetProps = new HashSet<string>(EnumerateProperties(target));

            // 既定値の比較相手。**作りたてのマテリアルを Unity に作らせる。**
            var fresh = new Material(srcShader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                int n = srcShader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    string p = srcShader.GetPropertyName(i);
                    if (mappedSrc.Contains(p)) continue;
                    if (targetProps.Contains(p)) { rep.Kept.Add(p); continue; }

                    if (DiffersFromDefault(mat, fresh, srcShader, i)) rep.LostChanged.Add(p);
                    else rep.LostDefault.Add(p);
                }
            }
            finally { DestroyImmediate(fresh); }

            // **AO は自動では移せない。** Doll / Idol は `_OcclusionMap` を単体で持つが、
            // ToonPBR は `_MaskMap` にパックしてある。詰め替えにはテクスチャを
            // 作る必要があり、このリポジトリはバイナリを生成しない方針。
            // 実データでは 30 マテリアルが `_OcclusionMap` を割り当てていた ──
            // **黙って落とすと AO がまるごと消える。**
            // lint:foreign-begin  _OcclusionMap は移行元の名前
            var aoTex = mat.HasProperty("_OcclusionMap") ? mat.GetTexture("_OcclusionMap") : null;
            if (aoTex != null)
            {
                if (_reuseAoAsMask)
                {
                    texVal["_MaskMap"] = aoTex;
                    texST["_MaskMap"] = new Vector4(
                        mat.GetTextureScale("_OcclusionMap").x,
                        mat.GetTextureScale("_OcclusionMap").y,
                        mat.GetTextureOffset("_OcclusionMap").x,
                        mat.GetTextureOffset("_OcclusionMap").y);
                    // **R を読ませない。** AO がそのまま金属度になるのを塞ぐ。
                    numVal["_Metallic"] = 0.0f;
                    rep.Notes.Add("AO を _MaskMap に流用した（G で遮蔽が効く）。"
                                + "**R も B も AO になる**ので _Metallic を 0 にした。"
                                + "透過を入れるなら厚みが AO になる点に注意");
                }
                else
                {
                    rep.Notes.Add("**_OcclusionMap が割り当ててある。** ToonPBR は AO を "
                                + "_MaskMap にパックして読むので、そのままでは移せない。"
                                + "AO を _MaskMap の G へ詰めたマップを焼くか、"
                                + "上の「AO を _MaskMap に流用する」を使うこと");
                }
            }
            // lint:foreign-end

            if (!apply) return rep;

            // --- (2) 差し替える -------------------------------------------
            Undo.RecordObject(mat, "ToonPBR へ移行");
            mat.shader = target;

            // --- (3) 書く -------------------------------------------------
            foreach (var kv in texVal)
            {
                if (!mat.HasProperty(kv.Key)) continue;
                mat.SetTexture(kv.Key, kv.Value);
                var st = texST[kv.Key];
                mat.SetTextureScale(kv.Key, new Vector2(st.x, st.y));
                mat.SetTextureOffset(kv.Key, new Vector2(st.z, st.w));
            }
            foreach (var kv in colVal)
                if (mat.HasProperty(kv.Key)) mat.SetColor(kv.Key, kv.Value);
            foreach (var kv in numVal)
                if (mat.HasProperty(kv.Key)) mat.SetFloat(kv.Key, kv.Value);

            // **ラップの正規化が違うので、しきい値と柔らかさを引き直す。**
            //
            //   移行元  `(NdotL + w) / (1 + w)`
            //   ToonPBR `(NdotL + w) / (1 + w)²`   ← エネルギー保存ぶんの余分な 1/(1+w)
            //
            // 伝達関数の形（`smoothstep(t - s, t + s, x)`）は**同じ**なので、
            // 違うのは入力の尺度だけ。境界が同じ NdotL に来る条件を解くと:
            //
            //   移行元  : (N* + w)/(1 + w)  = t   →  N* = t(1 + w) - w
            //   ToonPBR : (N* + w)/(1 + w)² = T   →  N* = T(1 + w)² - w
            //   等号   : T = t / (1 + w)          柔らかさも同じ係数
            //
            // **既定はどちらも 0.5 / 0.12 で同じ数字なので、素通しでよさそうに見える。**
            // 数字が同じでも、入れる先の式が違えば別の絵になる（T-202 と同じ形）。
            // 既定の wrap 0.25 なら係数 0.8 ── 境界が 20% ぶん手前にずれていた。
            //
            // 法線マップは「割り当てたら ON」。元シェーダーにトグルが無いので導く。
            if (mat.HasProperty("_NormalMapOn"))
                mat.SetFloat("_NormalMapOn", mat.GetTexture("_BumpMap") != null ? 1f : 0f);

            // **影色は Mix を立てないと効かない。**
            // `_ShadowColor` だけ移しても `_ShadowColorMix` が 0 のままなら
            // 一切乗らない ── 「設定したのに反映されない」の典型なのでここで立てる。
            if (mat.HasProperty("_ShadowColor") && mat.HasProperty("_ShadowColorMix") &&
                mat.GetFloat("_ShadowColorMix") <= 0.0001f)
            {
                mat.SetFloat("_ShadowColorMix", 0.35f);
                rep.Notes.Add("_ShadowColorMix を 0.35 に立てた（0 のままだと _ShadowColor が効かない）");
            }

            // **鏡面の色も同じ形。** ToonPBR は
            // `specular = lerp(specular, specular * _SpecularTint, _SpecularTintStrength)` で、
            // **強度の既定が 0。** 色だけ移しても一切効かない。
            //
            // 実データ 184 個は全部が白なので今は無害だが、
            // **色を付けた材料が来たときに黙って落ちる。**
            // 白のときは立てない ── 白 × 強度 1 は恒等なので意味が無く、
            // インスペクタに「効いていそうな 1」が残るだけ紛らわしい。
            if (mat.HasProperty("_SpecularTint") && mat.HasProperty("_SpecularTintStrength"))
            {
                Color tint = mat.GetColor("_SpecularTint");
                bool tinted = Mathf.Abs(tint.r - 1f) > 0.01f
                           || Mathf.Abs(tint.g - 1f) > 0.01f
                           || Mathf.Abs(tint.b - 1f) > 0.01f;
                if (tinted && mat.GetFloat("_SpecularTintStrength") <= 0.0001f)
                {
                    mat.SetFloat("_SpecularTintStrength", 1.0f);
                    rep.Notes.Add("_SpecularTintStrength を 1 に立てた"
                                + "（0 のままだと _SpecularTint が効かない）");
                }
            }

            mat.SetFloat("_SurfaceType", surface);
            ApplyKeywords(mat, surface);

            EditorUtility.SetDirty(mat);
            return rep;
        }

        /// <summary>
        /// サーフェスタイプを元マテリアルの機能から推測する。
        /// **顔 → 髪 → 布 の順で見る。** 顔の SDF が立っているものは顔で確定、
        /// 異方性ハイライトを使っているものは髪、という優先順位。
        /// </summary>
        private static float DetectSurfaceType(Material mat, bool isDoll, Report rep)
        {
            // lint:foreign-begin  元シェーダーのトグル名
            //
            // **トグルより「マップが割り当ててあるか」のほうが当てになる。**
            // 正解データで確かめたら、顔マテリアルは `_UseFaceSDF = 0` のままで
            // **トグルだけ見ていると発火しなかった**（ToonPBR へ移したときに
            // 手で ON にしたらしい）。一方 `_FaceSDFMap` は 46 個中 1 個だけに
            // 割り当てられていて、それがまさに顔だった ── 取りこぼしも誤検出も無い。
            bool faceSdf =
                (mat.HasProperty("_UseFaceSDF")    && mat.GetFloat("_UseFaceSDF")    > 0.5f) ||
                (mat.HasProperty("_FaceSDFEnable") && mat.GetFloat("_FaceSDFEnable") > 0.5f) ||
                (mat.HasProperty("_FaceSDFMap")    && mat.GetTexture("_FaceSDFMap") != null);
            if (faceSdf) { rep.Notes.Add("Surface Type = Face（顔 SDF のマップかトグルがあった）"); return 2f; }

            // **手がかりは2つ。どちらかが立てば髪。**
            // 既に手で詰めた 46 マテリアルを正解データとして突き合わせた結果:
            //
            //   `_AnisoColor` が黒でない … 髪 7 個のうち 5 個で立つ。他のタイプでは 0 個
            //   `_HairFlowMap` が割当済み … 同じ 5 個。独立した手がかりになる
            //   `_AnisoStrandStrength`   … **既定値のまま 0.2 が 138 個**。手がかりにならない
            //
            // 残り 2 個（`16.kisakikami` / `36.kami_2`）は移行元に**何の痕跡も無い。**
            // Default や Cloth と値が完全に同じなので、これ以上は判定できない。
            bool hair =
                (mat.HasProperty("_AnisoColor")
                 && mat.GetColor("_AnisoColor").maxColorComponent > 0.001f) ||
                (mat.HasProperty("_HairFlowMap") && mat.GetTexture("_HairFlowMap") != null);
            if (hair) { rep.Notes.Add("Surface Type = Hair（異方性か毛流れが有効だった）"); return 3f; }

            // **一度 `_SSSIntensity` を肌の手がかりに足したが、正解データが否定した。**
            // 実データで 5 件立っていて、手で詰めた Skin が 4 件だったので
            // 「近いから同じもの」と考えたが、突き合わせたら
            // **その 5 件は髪 2 個ほかで、Skin 4 個には 1 つも付いていなかった。**
            // 入れたままだと `16.kisakikami`（髪）が Skin に化ける。
            // **件数が近いことは、対応していることを何も意味しない。**
            bool skin = mat.HasProperty("_SkinScatterIntensity")
                        && mat.GetFloat("_SkinScatterIntensity") > 0.001f;
            // lint:foreign-end
            if (skin) { rep.Notes.Add("Surface Type = Skin（肌の散乱が有効だった）"); return 1f; }

            // **Skin と Cloth は判定できない。** 正解データで確かめた:
            // 肌 4 個・布 10 個は、移行元での値が Default と**完全に同じ**だった
            // （`_SkinScatterIntensity` も `_StockingIntensity` も全件 0）。
            // 判別できる情報がそもそも .mat に無い。
            // **黙って Default にせず、報告に書いて手で直させる。**
            rep.Notes.Add("Surface Type = Default（判定材料が無い。"
                        + "肌と布は移行元の値が Default と同じで、原理的に判別できない）");
            return 0f;
        }

        /// <summary>
        /// **トグルとキーワードを必ず一緒に立てること。**
        /// プロパティだけ変えてキーワードを忘れると、**インスペクタは ON に見えるのに
        /// 効かない**という形になる。param_check がこの食い違いを見ているが、
        /// 作る側で揃えるのが本筋。
        /// </summary>
        private static void ApplyKeywords(Material mat, float surface)
        {
            string[] surfaceKw =
            {
                "_SURFACETYPE_DEFAULT", "_SURFACETYPE_SKIN", "_SURFACETYPE_FACE",
                "_SURFACETYPE_HAIR", "_SURFACETYPE_CLOTH",
            };
            foreach (var k in surfaceKw) mat.DisableKeyword(k);
            mat.EnableKeyword(surfaceKw[Mathf.Clamp((int)surface, 0, 4)]);

            SetToggle(mat, "_AlphaClipOn",     "_ALPHATEST_ON");
            SetToggle(mat, "_HQShadowOn",      "_HQ_SHADOW_ON");
            SetToggle(mat, "_OutlineOn",       "_OUTLINE_ON");
        }

        private static void SetToggle(Material mat, string prop, string keyword)
        {
            if (!mat.HasProperty(prop)) return;
            if (mat.GetFloat(prop) > 0.5f) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        /// <summary>
        /// **対応表そのものを検算する。** 下見のたびに 1 回だけ回す。
        ///
        /// `param_check` は「変換を書いていない行」の値域しか見られない
        /// ── C# のラムダを Python から評価できないので。
        /// **変換を書いた行こそ間違えやすい**（尺度を 10 倍にする、逆数を取る、
        /// 補数を取る、といった変換を手で書いている）ので、
        /// ここで Unity の Range を読んで実際に通してみる。
        ///
        /// 外れていても移行は止めない。**報告に出すだけ。**
        /// 対応表は絵を見ながら詰めるものなので、途中で止めるほうが邪魔になる。
        /// </summary>
        private static List<string> ValidateRules(Shader source, Shader target)
        {
            var bad = new List<string>();
            var srcRange = RangeLimits(source);
            var dstRange = RangeLimits(target);

            var rules = CommonRules.Concat(DollRules);

            foreach (var r in rules)
            {
                if (r.Kind != Kind.Number) continue;
                if (!srcRange.TryGetValue(r.Src, out var s)) continue;
                if (!dstRange.TryGetValue(r.Dst, out var d)) continue;

                // 端と中間を通す。単調でない変換は想定していないが、
                // 11 点も見れば手書きの取り違えは十分に出る。
                float worstIn = 0f, worstOut = 0f;
                bool over = false;
                for (int i = 0; i <= 10; i++)
                {
                    float v = Mathf.Lerp(s.x, s.y, i / 10f);
                    float o = r.Map != null ? r.Map(v) : v;
                    if (o < d.x - 1e-4f || o > d.y + 1e-4f)
                    {
                        over = true;
                        worstIn = v; worstOut = o;
                    }
                }
                if (over)
                    bad.Add($"{r.Src} [{s.x}, {s.y}] → {r.Dst} [{d.x}, {d.y}]"
                          + $" ── {worstIn} を通すと {worstOut} になる");
            }

            // **「既定どうしを合わせた」と宣言したルールが、実際に合っているか。**
            // 係数を触れば真っ先に崩れるのがここ。宣言だけあって成り立っていない状態は、
            // **コメントだけ正しくてコードが違う**という一番たちの悪い形になる。
            var srcFresh = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            var dstFresh = new Material(target) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var r in rules)
                {
                    if (r.Kind != Kind.Number || !AnchoredRules.Contains(r.Src)) continue;
                    if (!srcFresh.HasProperty(r.Src) || !dstFresh.HasProperty(r.Dst)) continue;

                    float sd = srcFresh.GetFloat(r.Src);
                    float dd = dstFresh.GetFloat(r.Dst);
                    float got = r.Map != null ? r.Map(sd) : sd;
                    if (Mathf.Abs(got - dd) > 1e-3f)
                        bad.Add($"**既定が合っていない** {r.Src} → {r.Dst}"
                              + $"（{source.name}）── 移行元の既定 {sd} を通すと {got} だが、"
                              + $"移行先の既定は {dd}。"
                              + "既定のままのマテリアルが移行で見た目を変える");
                }
            }
            finally { DestroyImmediate(srcFresh); DestroyImmediate(dstFresh); }

            return bad;
        }

        private static Dictionary<string, Vector2> RangeLimits(Shader s)
        {
            var map = new Dictionary<string, Vector2>();
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (s.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Range)
                    continue;
                map[s.GetPropertyName(i)] =
                    new Vector2(s.GetPropertyRangeLimits(i).x,
                                s.GetPropertyRangeLimits(i).y);
            }
            return map;
        }

        private static List<string> EnumerateProperties(Shader s)
        {
            var list = new List<string>();
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++) list.Add(s.GetPropertyName(i));
            return list;
        }

        // ------------------------------------------------------------------
        //  ウィンドウ
        // ------------------------------------------------------------------

        private Vector2 _scroll;
        private string _log = "";
        private bool _onlySelection = true;

        /// <summary>
        /// AO を `_MaskMap` に流用するか。**既定は OFF。**
        ///
        /// ToonPBR の `_MaskMap` は R=Metallic / G=Occlusion / B=Thickness / A=Smoothness で、
        /// **本来はパック済みのマップを入れる場所。** 単体の AO を入れるのは筋が悪い。
        ///
        /// ただし AO はグレースケールなので G にも同じ値が入り、**遮蔽だけは正しく効く。**
        /// 前回の移行が実際にこれをやっていて、30 マテリアルが今そう動いている（T-196）。
        /// AO を丸ごと捨てるよりはよいので、**条件を明示したうえで選べるようにする。**
        ///
        /// 危ないのは R（金属度）と B（厚み）まで AO で埋まること。
        /// **流用するときは `_Metallic` を 0 に落とす**ので、R が読まれることは無くなる。
        /// B は透過を入れたときだけ効くので、そちらは `check_maskmap_packing` が見る。
        /// </summary>
        private static bool _reuseAoAsMask;

        [MenuItem("Tools/Idol/EasyToon・EasyPBR から移行")]
        private static void Open()
        {
            GetWindow<ToonPBRMigrator>("ToonPBR 移行").minSize = new Vector2(560, 420);
        }

        /// <summary>
        /// batchmode から下見だけを回す。**GUI を開かずに実際のコードを通す。**
        ///
        /// このプロジェクトは「実装したのに一度も動いていなかった」を何度も踏んでいる
        /// （T-155 のプリセット窓は、押しても何も起きないボタンを案内し続けた）。
        /// **コンパイルが通ることと、動くことは別。**
        /// 静的検査で名前と値域は守れるが、`Migrate` を一度も通していなければ
        /// null 参照ひとつで全部止まる。
        ///
        /// Unity -batchmode -quit -nographics -projectPath . \
        ///       -executeMethod ToonNPR.EditorTools.ToonPBRMigrator.RunDryRunCI
        /// </summary>
        public static void RunDryRunCI()
        {
            var target = Shader.Find(TargetShader);
            if (target == null)
            {
                Debug.LogError($"[Migrator] 移行先が見つからない: {TargetShader}");
                EditorApplication.Exit(1);
                return;
            }

            var mats = new List<Material>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (m != null && IsSource(m)) mats.Add(m);
            }

            if (mats.Count == 0)
            {
                Debug.LogError("[Migrator] 移行元のマテリアルが 1 個も無い。"
                             + "**下見が空振りする状態では検証にならない。**");
                EditorApplication.Exit(1);
                return;
            }

            int mapped = 0, lost = 0;
            foreach (var m in mats)
            {
                var rep = Migrate(m, target, apply: false);
                mapped += rep.Mapped.Count;
                lost += rep.LostChanged.Count;
            }

            foreach (var srcName in new[] { DollShader })
            {
                var src = Shader.Find(srcName);
                if (src == null) continue;
                foreach (var b in ValidateRules(src, target))
                    Debug.LogWarning($"[Migrator] 値域: {b}");
            }

            // **`Run` そのものを通す。** ここまでは `Migrate` を直接呼んでいるので、
            // 報告の組み立て・サーフェスタイプの内訳・ファイル書き出しという
            // **ウィンドウ経由でしか通らない経路が一度も動かない。**
            // 「コンパイルは通るが実行されたことが無い」を作らないための一手。
            var probe = CreateInstance<ToonPBRMigrator>();
            try
            {
                probe._onlySelection = false;
                probe.Run(apply: false);
                if (string.IsNullOrEmpty(probe._log))
                {
                    Debug.LogError("[Migrator] 報告が空。Run が実質何もしていない");
                    EditorApplication.Exit(1);
                    return;
                }
                if (!probe._log.Contains("サーフェスタイプの判定"))
                {
                    Debug.LogError("[Migrator] 報告にサーフェスタイプの内訳が無い");
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log($"[Migrator] 報告を生成: {probe._log.Length} 文字");
            }
            finally { DestroyImmediate(probe); }

            Debug.Log($"[Migrator] {mats.Count} 個を下見: "
                    + $"移した {mapped} / 実際に失われる {lost}");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// batchmode から**実際に適用し、書けたことを読み直して確かめる。**
        ///
        /// 下見は読むだけなので、**書き込み経路は一度も通らない。**
        /// ところがデータが失われるとしたらそこ ──
        /// 「シェーダーを差し替える前に読み切る」という順序を間違えると、
        /// 読めているのに書けていない、という形で静かに落ちる。
        ///
        /// 検証用プロジェクトで回すこと。**本番のマテリアルを書き換える。**
        /// </summary>
        public static void RunApplyCI()
        {
            var target = Shader.Find(TargetShader);
            if (target == null) { Fail($"移行先が見つからない: {TargetShader}"); return; }

            var mats = new List<Material>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (m != null && IsSource(m)) mats.Add(m);
            }
            if (mats.Count == 0) { Fail("移行元のマテリアルが 1 個も無い"); return; }

            int checked_ = 0, bad = 0;

            foreach (var m in mats)
            {
                // 先に下見で「書くはずの値」を控え、そのあと適用する。
                var want = Migrate(m, target, apply: false);
                Migrate(m, target, apply: true);

                if (m.shader != target)
                {
                    Debug.LogError($"[Migrator] {m.name}: シェーダーが差し替わっていない");
                    bad++; continue;
                }

                foreach (var kv in want.Numbers)
                {
                    if (!m.HasProperty(kv.Key)) continue;
                    checked_++;
                    if (Mathf.Abs(m.GetFloat(kv.Key) - kv.Value) > 1e-4f)
                    {
                        Debug.LogError($"[Migrator] {m.name}: {kv.Key} が "
                                     + $"{kv.Value} でなく {m.GetFloat(kv.Key)}");
                        bad++;
                    }
                }
                foreach (var kv in want.Textures)
                {
                    if (!m.HasProperty(kv.Key)) continue;
                    checked_++;
                    if (m.GetTexture(kv.Key) != kv.Value)
                    {
                        Debug.LogError($"[Migrator] {m.name}: {kv.Key} のテクスチャが違う");
                        bad++;
                    }
                }

                // **トグルとキーワードが揃っているか。**
                // ここが食い違うと「インスペクタは ON に見えるのに効かない」になる。
                checked_++;
                string wantKw = new[]
                {
                    "_SURFACETYPE_DEFAULT", "_SURFACETYPE_SKIN", "_SURFACETYPE_FACE",
                    "_SURFACETYPE_HAIR", "_SURFACETYPE_CLOTH",
                }[Mathf.Clamp((int)want.SurfaceType, 0, 4)];
                if (!m.IsKeywordEnabled(wantKw))
                {
                    Debug.LogError($"[Migrator] {m.name}: キーワード {wantKw} が立っていない");
                    bad++;
                }

                checked_++;
                bool clipOn = m.HasProperty("_AlphaClipOn") && m.GetFloat("_AlphaClipOn") > 0.5f;
                if (clipOn != m.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    Debug.LogError($"[Migrator] {m.name}: _AlphaClipOn とキーワードが食い違う");
                    bad++;
                }
            }

            Debug.Log($"[Migrator] 適用検証: {mats.Count} 個 / {checked_} 項目を照合 / 不一致 {bad}");
            EditorApplication.Exit(bad == 0 ? 0 : 1);
        }

        /// <summary>
        /// **AO の流用経路を実際に通す。** 既定 OFF の分岐なので、
        /// 放っておくと「書いたが一度も実行されていない」ままになる。
        ///
        /// 確かめるのは 2 つ ── `_MaskMap` に AO が入ったか、`_Metallic` が 0 に落ちたか。
        /// 後者を怠ると**金属度が AO で変調される**（T-196）。
        /// </summary>
        public static void RunAoReuseCI()
        {
            var target = Shader.Find(TargetShader);
            if (target == null) { Fail($"移行先が見つからない: {TargetShader}"); return; }

            var mats = new List<Material>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(
                    AssetDatabase.GUIDToAssetPath(guid));
                // lint:foreign-begin  _OcclusionMap は移行元の名前
                if (m != null && IsSource(m) && m.HasProperty("_OcclusionMap")
                    && m.GetTexture("_OcclusionMap") != null) mats.Add(m);
                // lint:foreign-end
            }
            if (mats.Count == 0) { Fail("AO を割り当てた移行元が 1 個も無い"); return; }

            _reuseAoAsMask = true;
            int bad = 0;
            foreach (var m in mats)
            {
                // lint:foreign-begin
                var ao = m.GetTexture("_OcclusionMap");
                // lint:foreign-end
                Migrate(m, target, apply: true);

                if (m.GetTexture("_MaskMap") != ao)
                {
                    Debug.LogError($"[Migrator] {m.name}: _MaskMap に AO が入っていない");
                    bad++;
                }
                if (Mathf.Abs(m.GetFloat("_Metallic")) > 1e-4f)
                {
                    Debug.LogError($"[Migrator] {m.name}: _Metallic が 0 に落ちていない"
                                 + $"（{m.GetFloat("_Metallic")}）── AO が金属度になる");
                    bad++;
                }
            }
            _reuseAoAsMask = false;

            Debug.Log($"[Migrator] AO 流用の検証: {mats.Count} 個 / 不一致 {bad}");
            EditorApplication.Exit(bad == 0 ? 0 : 1);
        }

        private static void Fail(string message)
        {
            Debug.LogError($"[Migrator] {message}");
            EditorApplication.Exit(1);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "EasyToon (Idol) / EasyPBR (Doll) のマテリアルを ToonPBR へ移します。\n\n" +
                "同名のプロパティは Unity が自動で引き継ぎますが、それは Idol で 29 / 133、" +
                "Doll で 38 / 175 しかありません。残りの「名前が違うだけで同じもの」を" +
                "移すのがこのツールの仕事です。\n\n" +
                "**まず「下見」を押して、何が落ちるかを読んでください。**",
                MessageType.Info);

            _onlySelection = EditorGUILayout.ToggleLeft(
                "選択中のマテリアルだけを対象にする（外すとプロジェクト全体）", _onlySelection);

            _reuseAoAsMask = EditorGUILayout.ToggleLeft(
                "AO を _MaskMap に流用する（本来はパック済みマップを入れる場所）",
                _reuseAoAsMask);
            if (_reuseAoAsMask)
                EditorGUILayout.HelpBox(
                    "_MaskMap は R=Metallic / G=Occlusion / B=Thickness / A=Smoothness です。\n"
                    + "AO はグレースケールなので G にも入り、**遮蔽だけは正しく効きます**。\n"
                    + "R まで AO になるのを防ぐため、**_Metallic を 0 にします**。\n"
                    + "透過を使うなら厚み（B）も AO になるので、AO を G に詰めたマップを"
                    + "焼き直してください。",
                    MessageType.Warning);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("下見（変更しない）", GUILayout.Height(30))) Run(false);
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.75f, 0.75f);
                if (GUILayout.Button("移行を実行", GUILayout.Height(30))) Run(true);
                GUI.backgroundColor = prev;
            }

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void Run(bool apply)
        {
            var target = Shader.Find(TargetShader);
            if (target == null)
            {
                _log = $"移行先のシェーダーが見つからない: {TargetShader}\n" +
                       "ToonPBR.shader がプロジェクトに入っているか確認すること。";
                return;
            }

            var mats = Collect();
            if (mats.Count == 0)
            {
                _log = _onlySelection
                    ? "選択中に EasyToon / EasyPBR のマテリアルがありません。"
                    : "プロジェクトに EasyToon / EasyPBR のマテリアルが見つかりません。";
                return;
            }

            // **取り消しを 1 手にまとめる。**
            // `Undo.RecordObject` はマテリアルごとに 1 段積むので、
            // 184 個を移行すると**取り消すのに 184 回 Ctrl+Z が要る。**
            // 一括操作の取り消しが実質できないのは、破壊的な道具として危ない。
            int undoGroup = -1;
            if (apply)
            {
                Undo.SetCurrentGroupName($"ToonPBR へ移行（{mats.Count} 個）");
                undoGroup = Undo.GetCurrentGroup();
            }

            var sb = new StringBuilder();
            sb.AppendLine(apply ? "=== 移行を実行した ===" : "=== 下見（何も変更していない）===");
            sb.AppendLine($"対象 {mats.Count} 個\n");

            // **対応表そのものを先に検算する。** 表が壊れていれば、
            // 個々のマテリアルの報告を読む意味が無い。
            foreach (var srcName in new[] { DollShader })
            {
                var src = Shader.Find(srcName);
                if (src == null) continue;
                var bad = ValidateRules(src, target);
                if (bad.Count == 0) continue;

                sb.AppendLine($"**対応表の値域がはみ出している（{srcName}）**");
                foreach (var b in bad) sb.AppendLine($"   {b}");
                sb.AppendLine("   移行は止めないが、移した値が Range の外に出る。"
                            + "Range は実行時には縛らないので lerp が外挿になる。\n");
            }

            var droppedAll = new Dictionary<string, int>();
            var surfaceCount = new int[5];

            foreach (var m in mats)
            {
                var rep = Migrate(m, target, apply);
                surfaceCount[Mathf.Clamp((int)rep.SurfaceType, 0, 4)]++;
                sb.AppendLine($"── {m.name}  （{rep.SourceShader}）");
                sb.AppendLine($"   移した       {rep.Mapped.Count} 個");
                sb.AppendLine($"   同名で残る   {rep.Kept.Count} 個");
                sb.AppendLine($"   **落ちる（実際に設定されていた）** {rep.LostChanged.Count} 個"
                            + $"　／ 既定のまま落ちる {rep.LostDefault.Count} 個");
                foreach (var n in rep.Notes) sb.AppendLine($"   ・{n}");
                foreach (var d in rep.LostChanged)
                    droppedAll[d] = droppedAll.TryGetValue(d, out var c) ? c + 1 : 1;
                sb.AppendLine();
            }

            // **裏が取れていない変換を名指しする。**
            // 尺度を掛ける・逆数を取る・補数を取る、といった変換を手で書いており、
            // **正解データで確かめられたのは 4 つだけ。** 残りは絵を見て詰める前提。
            // 黙っていると「移行できた ＝ 値も正しい」と読まれる。
            var unverified = CommonRules.Concat(DollRules)
                .Where(r => r.Map != null && !ConfirmedRules.Contains(r.Src))
                .Select(r => $"{r.Src} → {r.Dst}"
                           + (string.IsNullOrEmpty(r.Note) ? "" : $"（{r.Note}）"))
                .Distinct().ToList();
            if (unverified.Count > 0)
            {
                sb.AppendLine("=== 変換係数の裏が取れていないもの ===");
                sb.AppendLine("移せてはいるが、**その値が絵として妥当かは未確認。**"
                            + " 尺度・逆数・補数を手で決めている。\n");
                foreach (var u in unverified) sb.AppendLine($"   {u}");
                sb.AppendLine();
            }

            // **サーフェスタイプの内訳を出す。** 判定できるのは Face と Hair、
            // それに透過が立っている Skin だけで、**Cloth は移行元に手がかりが無い。**
            // Default に落ちたものは手で見直す前提なので、件数を先に見せる。
            sb.AppendLine("=== サーフェスタイプの判定 ===");
            string[] names = { "Default", "Skin", "Face", "Hair", "Cloth" };
            for (int i = 0; i < 5; i++)
                if (surfaceCount[i] > 0) sb.AppendLine($"   {names[i],-8} {surfaceCount[i]} 個");
            if (surfaceCount[0] > 0)
                sb.AppendLine($"   **Default の {surfaceCount[0]} 個は手で見直すこと。**"
                            + " とくに布は移行元に手がかりが無く、Cloth を自動で付けられない。");
            sb.AppendLine();

            // **落ちたものを必ず並べる。** これがこのツールの半分。
            // ただし**実際に設定されていたものだけ。** 既定のまま落ちるものを混ぜると
            // 100 行を超えて読めなくなる（実データで 103 → 27 になる）。
            sb.AppendLine("=== ToonPBR に持って来られなかったもの（実際に設定されていた分だけ）===");
            sb.AppendLine("値は .mat に残るが ToonPBR は読まない。絵から消える。\n");
            if (droppedAll.Count == 0)
                sb.AppendLine("   なし。既定から動かされていたものはすべて移せた。");
            foreach (var kv in droppedAll.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                sb.AppendLine($"   {kv.Key,-30} {kv.Value} 個のマテリアルで設定されていた");

            if (apply)
            {
                Undo.CollapseUndoOperations(undoGroup);   // Ctrl+Z 一発で全部戻る
                AssetDatabase.SaveAssets();
                sb.AppendLine("\n**移行後は Tools > Idol > セットアップ診断 を回すこと。**");
                sb.AppendLine("値の整合（Range 外・トグルとキーワードの食い違い）はそちらが見る。");
                sb.AppendLine("取り消しは Ctrl+Z 一回で全件戻る（1 手にまとめてある）。");
            }
            else
            {
                sb.AppendLine("\n下見なので何も変更していない。実行するには「移行を実行」を押すこと。");
            }

            _log = sb.ToString();
            Debug.Log(_log);

            // **報告をファイルにも残す。** 184 マテリアルぶんはウィンドウにも
            // コンソールにも収まらない（Unity のコンソールは 1 件あたりで切る）。
            // **読めない報告は読まれない**ので、腰を据えて読める形を用意する。
            try
            {
                string dir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ToonPBRMigration");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(
                    dir, apply ? "migration_applied.txt" : "migration_dryrun.txt");
                System.IO.File.WriteAllText(file, sb.ToString());
                _log = $"報告を書き出した: {file}\n\n" + _log;
                Debug.Log($"[Migrator] 報告: {file}");
            }
            catch (System.Exception e)
            {
                // 書けなくても移行そのものは終わっている。**黙らずに続ける。**
                Debug.LogWarning($"[Migrator] 報告を書き出せなかった: {e.Message}");
            }
        }

        private List<Material> Collect()
        {
            var result = new List<Material>();

            if (_onlySelection)
            {
                foreach (var o in Selection.objects)
                    if (o is Material m && IsSource(m)) result.Add(m);
                return result;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (m != null && IsSource(m)) result.Add(m);
            }
            return result;
        }

        private static bool IsSource(Material m) =>
            m.shader != null && m.shader.name == DollShader;
    }
}

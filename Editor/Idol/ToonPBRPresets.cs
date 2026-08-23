using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// 選択したマテリアルにまとめて値を当てる（FR-25）。
    ///
    /// なぜ要るか:
    ///   絵の判断は**振って比べないと決まらない**。「影が濃すぎるか」は
    ///   薄いほうを見て初めて分かる。ところがキャラ 1 体で 46 マテリアルあるので、
    ///   1 つ試すのにインスペクタを 46 回触ることになり、事実上比較できない。
    ///
    ///   ここは「絵の方向」を軸で持つ。**部位ごとの差はサーフェスタイプから決める**ので、
    ///   選択に肌と布と髪が混ざっていても、それぞれに合った値が入る。
    ///
    /// 元の値は Undo で戻せる。プリセットは上書きであって、
    /// テクスチャの割り当てや Surface Type には触らない。
    /// </summary>
    public class ToonPBRPresets : EditorWindow
    {
        private enum SurfaceType { Default = 0, Skin = 1, Face = 2, Hair = 3, Cloth = 4 }

        [MenuItem("Tools/Idol/プリセットを適用")]
        private static void Open()
        {
            var w = GetWindow<ToonPBRPresets>("ToonPBR プリセット");
            w.minSize = new Vector2(460, 320);
        }

        private Vector2 _scroll;

        private void OnGUI()
        {
            var mats = Selection.objects.OfType<Material>()
                .Concat(Selection.gameObjects
                    .SelectMany(g => g.GetComponentsInChildren<Renderer>(true))
                    .SelectMany(r => r.sharedMaterials))
                // **シェーダー名は "Origuma/EasyToon_URP/Idol"。ファイル名 ToonPBR.shader とは別物。**
                // 以前は Contains("ToonPBR") で判定しており **一度も一致していなかった** ──
                // このウィンドウも診断のマテリアル検査も、対象 0 件のまま黙って何もしていなかった。
                // 「対象が無い」と表示されるので気付けそうだが、
                // 「選択が違うのだろう」と解釈されるだけで原因に辿り着かない。
                .Where(m => m != null && m.shader != null && m.shader.name.Contains("Idol"))
                .Distinct().ToArray();

            EditorGUILayout.HelpBox(
                mats.Length == 0
                    ? "ToonPBR のマテリアル、またはキャラのルートを選択してください。"
                    : $"対象: {mats.Length} 件（サーフェスタイプごとに違う値が入ります）",
                mats.Length == 0 ? MessageType.Warning : MessageType.Info);

            if (mats.Length == 0) return;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            Section("影の濃さ",
                "**振って比べるための軸。** 落ち影・影色・影の中の環境光をまとめて動かす。" +
                "個別に触ると3つの効果が混ざって判断できなくなる。");

            // 括弧内は主光源 1.0 / 環境光 Intensity 2 での「影／光」の比。
            // **段階が診断の判定と揃うように計算して決めた値。** 目分量で置くと
            // 「標準」が既に濃い側の境界に来ていた（実際そうなっていた）。
            Row(mats, "浅め", "影／光 ≒ 0.69・落ち影 0.53。背景が明るいとき、線で見せる絵向け",
                ShadowPreset(0.75f, 0.62f, 0.30f));
            Row(mats, "標準", "影／光 ≒ 0.54・落ち影 0.35。実用域の中央",
                ShadowPreset(0.62f, 0.45f, 0.45f));
            Row(mats, "濃いめ", "影／光 ≒ 0.42・落ち影 0.23。PBR の背景と並べると芯が出る",
                ShadowPreset(0.52f, 0.32f, 0.60f));

            EditorGUILayout.Space(8);
            Section("影の色味",
                "**濃さとは別の軸。** 影色を各マテリアルの Shadow Hue へどれだけ寄せるか。" +
                "Rec.709 輝度を合わせてから混ぜるので**濃さは1ミリも変わらず、色相だけ**動く。" +
                "乗算の Tint と違い、白い布や銀髪のように元が無彩色に近い面にも色が入る " +
                "── そこは彩度スケールでは何倍しても 0 のままなので、この軸でしか動かせない。");

            // **色相そのものは触らない。** マテリアルごとに違う値が入っていて
            // （瞳の赤い影など、移植元のアーティストが指定したもの）、
            // 一律で塗り潰すと戻せない。ここが振るのは「どれだけ寄せるか」だけ。
            Row(mats, "無し", "従来どおり。乗算だけの影色。比較の基準として",
                ShadowHuePreset(0f));
            Row(mats, "控えめ", "言われないと気付かない程度。背景が無彩色のとき",
                ShadowHuePreset(0.2f));
            Row(mats, "標準", "Hue Mix 0.35。灰色に沈んでいた影に色が戻る",
                ShadowHuePreset(0.35f));
            Row(mats, "強め", "影がはっきり色を持つ。PBR の背景と並べたとき馴染みやすい",
                ShadowHuePreset(0.55f));

            EditorGUILayout.Space(8);
            Section("鏡面の強さ",
                "**金属的に見えるかどうかはここで決まる。** Metallic ではない。" +
                "直接鏡面と環境反射を動かす。Smoothness（部位ごとの質感の差）には触らない。" +
                "タイプ内の個別差は均される点に注意（0 = 意図的に消してある個体は保つ）。");

            Row(mats, "ほぼ無し", "直接鏡面を切り、環境反射も最小に。プラスチック感を完全に消す",
                SpecPreset(0));
            // **「現在の設定」と書かない。** 書いた瞬間から腐る ── 実際、
            // 46 マテリアルすべてが `_SpecularIntensity = 4`（この行の 20 倍）に
            // なっていたのに「現在の設定」と名乗り続けていた（T-285）。
            Row(mats, "控えめ", "布 0.10 / 肌 0.25 / 顔 0.25 / 髪・その他 0.20。既定に近い側",
                SpecPreset(1));
            Row(mats, "強め", "エナメルや濡れた質感。金具を目立たせたいとき",
                SpecPreset(2));

            EditorGUILayout.Space(8);
            Section("リムの強さ",
                "逆光の縁。**まず「無し」と比べる**のが早い ── 有る状態だけ見ていると、" +
                "リムが絵に効いているのか浮いているのか判断できない。" +
                "向き（Directionality）と落ち影の反映は常に有効のまま。");

            Row(mats, "無し", "リムを完全に切る。比較の基準として",
                RimPreset(0f, 0.7f));
            Row(mats, "控えめ", "シルエットを起こす程度。背景と馴染ませたいとき",
                RimPreset(0.8f, 0.85f));
            Row(mats, "標準", "Intensity 1.59 / Backlight Bias 0.70",
                RimPreset(1.59f, 0.7f));
            Row(mats, "強め", "明確な逆光を作るとき。白飛びに注意",
                RimPreset(2.6f, 0.6f));

            EditorGUILayout.Space(12);
            Section("ちらつき対策（前髪の細い影）",
                "**原因は影マップの粒に対して毛束が細すぎること。** " + ShadowTexelNote() +
                "カメラや頭が動くたびにテクセル境界を跨いで影の有無が入れ替わる。" +
                "**セルの硬さ（Base Softness）は変えない** ── " +
                "ここで触るのはリアルタイム影の側だけなので、絵の様式は保たれる。");

            Row(mats, "① 接地硬化を切る（まず これ）",
                "**半影の幅を固定にする。** 接地硬化は 8 タップでブロッカー深度を推定するが、"
                + StrandTexelPhrase()
                + "**半径が画素ごとに 1.0〜8.4 テクセルの間で振れて「まだら」になる。**"
                + "キャラのスケールでは真の半影が 1 テクセルに届かないので、"
                + "**物理的に失うものは無い**（頭が床に落とす影だけは別）。触るのはこれ 1 つ",
                FlickerPreset(hardening: false));
            Row(mats, "② 可動域を狭める（硬化は残す）",
                "接地硬化は残したまま Penumbra Scale を 200 → 60 へ。"
                + "半径の振れ幅が狭まるので、まだらが減りつつ接地感は残る",
                FlickerPreset(penumbraScale: 60f));
            Row(mats, "③ 影の境界を柔らかくする",
                "Realtime Shadow Softness を 0.4 → 0.55 へ。**中心は「半分遮蔽」に固定**なので"
                + "影の大きさは変わらず柔らかさだけが変わる。①②で足りないときに足す",
                FlickerPreset(attenSoftness: 0.55f));
            Row(mats, "④ 元に戻す",
                "接地硬化 ON・Penumbra Scale 200・Realtime Shadow Softness 0.4",
                FlickerPreset(hardening: true, penumbraScale: 200f, attenSoftness: 0.4f));


            EditorGUILayout.Space(12);
            Section("切り分け（ちらつきの原因を特定する）",
                "**一度に1つだけ切って、絵がどう変わるかを見る。** ちらつきは複数の機構が" +
                "重なって出るので、全部入れたまま値を触っても何が効いたか分からない。" +
                "46 マテリアルを手で往復するのが現実的でないから、ここに置いてある。" +
                "戻すときは Undo か、もう一度『全部 ON』を押す。");

            // **「既定の状態」ではない。** シェーダーの既定は 2 つとも 0（OFF）で、
            // ここは「切り分けを終えて全部戻す」ための行（T-285）。
            Row(mats, "全部 ON", "2 つとも有効にする（切り分けを終えたあとの復帰用）。"
                + "**シェーダーの既定はどれも OFF** なので、初期状態とは違う",
                ToggleSet(hardening: true, hq: true));
            Row(mats, "接地硬化 OFF", "PCSS を切る。半影の幅が固定になり**揺れが止まる**。ここで止まれば原因は PCSS",
                ToggleSet(hardening: false, hq: true));
            Row(mats, "自前の影 OFF", "HQ 影ごと切って URP 標準の影に戻す。ここでも残るならシェーダー外が原因",
                ToggleSet(hardening: false, hq: false));

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "顔の自己影だけを切る（FR-26）。シャドウマップの1テクセルは顔の上で約5mm、"
                + "鼻の高さは約4テクセルしかないので、鼻や眉が作る自己影は形を保てず"
                + "まだらな塊として顔に乗る。SDF が面の向きによる陰を作っているので、"
                + "そこに重ねる意味も薄い。\n"
                + "引き換えに、顎から首・肩への落ち影も消える（頭部は同じマテリアル）。"
                + "元の設計は「首の落ち影は NPR Map の G に描く」前提だが、"
                + "現状 NPR Map は無効なので、その代替は入っていない。",
                MessageType.None);

            Row(mats, "顔の影を落とさない", "Surface Type = Face のマテリアルだけシャドウキャスタから外す",
                FaceCasterPreset(true));
            Row(mats, "顔の影を戻す", "既定の状態（顔も影を落とす）",
                FaceCasterPreset(false));


            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 前髪の細い影のちらつき対策（T-287）。
        ///
        /// **セルの硬さ（`_ShadowSoftness`）には触らない。** あれは絵の様式そのもので、
        /// 触ると「ちらつきは止まったが別のシェーダーになった」になる。
        /// ここで動かすのは**リアルタイム影の側だけ**:
        ///
        ///   `_HQShadowOn`          16 タップの Vogel を画素ごとに回す。
        ///                          テクセルの段差がノイズに変わり、境界 AA が吸収する
        ///   `_ShadowAttenSoftness` 遮蔽量に掛ける smoothstep の幅。
        ///                          **中心は 0.5（半分遮蔽）に固定**なので影の大きさは変わらない
        ///
        /// `hq` が null のときは HQ に触らない ── 「まず柔らかくするだけ試す」ための段。
        /// </summary>

        /// <summary>
        /// 影マップの粒の実寸（T-344 の修復で再建。式は SetupCheck.TryMainShadowTexel と共有）。
        /// 読めない環境では黙って空を返し、文としては成立させる。
        /// </summary>
        private static string ShadowTexelNote()
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (!ToonPBRSetupCheck.TryMainShadowTexel(asset, out _, out _, out float mm))
                return "";
            return $"いまの URP 設定では影マップの 1 テクセルが約 {mm:0.0#}mm。";
        }

        /// <summary>毛束幅を約 4mm と置いた注記（BACKLOG の実測記録と同じ仮定）。</summary>
        private static string StrandTexelPhrase()
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (!ToonPBRSetupCheck.TryMainShadowTexel(asset, out _, out _, out float mm))
                return "毛束は数テクセル幅しかなく推定が安定しない ── ";
            return $"毛束（約 4mm）は約 {4f / mm:0.#} テクセル幅しかなく推定が安定しない ── ";
        }
        private static System.Action<Material, SurfaceType> FlickerPreset(
            bool? hardening = null, float? penumbraScale = null, float? attenSoftness = null)
        {
            return (m, st) =>
            {
                // **渡されたものだけ触る。** 「接地硬化 OFF」と書いてあるのに
                // コンタクトシャドウまで ON になる、という行が既にある
                // （切り分け用に「全部 ON からの差分」で組んであるため）。
                // ちらつき対策の側は**名前どおり 1 つだけ**動かす。
                if (hardening.HasValue)
                    Set(m, "_ShadowContactHardening", hardening.Value ? 1f : 0f);
                if (penumbraScale.HasValue)
                    Set(m, "_ShadowPenumbraScale", penumbraScale.Value);
                if (attenSoftness.HasValue)
                    Set(m, "_ShadowAttenSoftness", attenSoftness.Value);
            };
        }

        /// <summary>
        /// 影まわりの機能を一括で切り替える。**切り分け専用で、絵の方向の軸ではない。**
        ///
        /// キーワードとプロパティの両方を動かすこと。片方だけだと
        /// インスペクタの表示と実際の分岐が食い違って、余計に分からなくなる。
        /// </summary>
        private static System.Action<Material, SurfaceType> ToggleSet(
            bool hardening, bool hq)
        {
            return (m, st) =>
            {
                SetToggle(m, "_HQShadowOn",      "_HQ_SHADOW_ON",      hq);

                // 接地硬化はキーワードを持たない（動的分岐）。プロパティだけで足りる。
                Set(m, "_ShadowContactHardening", hardening ? 1f : 0f);
            };
        }

        /// <summary>
        /// 顔だけシャドウキャスタから外す（FR-26）。
        ///
        /// **Face タイプ以外には触らない。** 体まで影を落とさなくなると
        /// 立ち位置が分からない絵になる。切り分けたいのは顔の自己影だけ。
        /// </summary>
        private static System.Action<Material, SurfaceType> FaceCasterPreset(bool off)
        {
            return (m, st) =>
            {
                if (st != SurfaceType.Face) return;
                Set(m, "_ShadowCasterOff", off ? 1f : 0f);
            };
        }

        private static void SetToggle(Material m, string prop, string keyword, bool on)
        {
            if (!m.HasFloat(prop)) return;

            m.SetFloat(prop, on ? 1f : 0f);

            if (on) m.EnableKeyword(keyword);
            else    m.DisableKeyword(keyword);
        }

        /// <summary>
        /// リムの強さと逆光への寄り。**向きと落ち影の反映（T-103）は触らない** ──
        /// あれは「光源と無関係に出る」という不具合の修正であって、絵の方向の軸ではない。
        /// </summary>
        private static System.Action<Material, SurfaceType> RimPreset(
            float intensity, float backlightBias)
        {
            return (m, st) =>
            {
                Set(m, "_RimIntensity", intensity);
                Set(m, "_RimBacklightBias", backlightBias);
            };
        }

        /// <summary>
        /// 影色をマテリアル固有の Shadow Hue へどれだけ寄せるか（FR-73）。
        ///
        /// **`_ShadowColor` には触らない。** マテリアルごとに違う色が入っていて、
        /// 瞳の赤い影のように移植元のアーティストが意図して指定したものがある。
        /// 一律に塗り潰すと戻せないので、この軸が動かすのは混ぜ具合だけ。
        ///
        /// **`_ShadowTint` にも触らない。** あちらは乗算で「濃さ」を担当していて、
        /// 色味の軸と混ぜると何が効いたか分からなくなる。
        /// </summary>
        private static System.Action<Material, SurfaceType> ShadowHuePreset(float mix)
        {
            return (m, st) => Set(m, "_ShadowColorMix", mix);
        }

        private static void Section(string title, string help)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(help.Replace("**", ""), MessageType.None);
        }

        private void Row(Material[] mats, string label, string desc,
                         System.Action<Material, SurfaceType> apply)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(label, GUILayout.Width(90)))
                {
                    Undo.RecordObjects(mats, "ToonPBR Preset");
                    foreach (var m in mats)
                    {
                        var st = (SurfaceType)Mathf.RoundToInt(
                            m.HasFloat("_SurfaceType") ? m.GetFloat("_SurfaceType") : 0f);
                        apply(m, st);
                        EditorUtility.SetDirty(m);
                    }
                    AssetDatabase.SaveAssets();
                }
                EditorGUILayout.LabelField(desc, EditorStyles.wordWrappedMiniLabel);
            }
        }

        // ------------------------------------------------------------------
        //  プリセット本体
        // ------------------------------------------------------------------

        /// <summary>影色の明度 / 影の中の環境光 / 落ち影の強さ をまとめて当てる。</summary>
        private static System.Action<Material, SurfaceType> ShadowPreset(
            float shadowValue, float ambientInShadow, float castStrength)
        {
            return (m, st) =>
            {
                Set(m, "_ShadowValue", shadowValue);
                Set(m, "_ShadowAmbientIntensity", ambientInShadow);

                // 落ち影の色を持っていないマテリアル（移植元で無効にしていた個体）は
                // 強度だけ上げても白のままなので触らない。
                if (m.HasColor("_CastShadowColor") && m.GetColor("_CastShadowColor").maxColorComponent < 0.99f)
                    Set(m, "_CastShadowColorStrength", castStrength);
            };
        }

        /// <summary>
        /// 直接鏡面と環境反射を**サーフェスタイプごとの絶対値**で当てる。
        ///
        /// **倍率にしてはいけない。** 現在値に掛ける形だと同じボタンを2回押した時に
        /// 2乗され、押した回数で結果が変わる。プリセットは**何回押しても同じ**であること。
        ///
        /// **`_Smoothness` には触らない。** あれは部位ごとの質感の差
        /// （T-088 で移植元から復元した Glossiness の2段階など）で、
        /// 「全体をどれだけ光らせるか」とは別の軸。潰すと戻せない。
        /// </summary>
        private static System.Action<Material, SurfaceType> SpecPreset(int level)
        {
            // level: 0 = ほぼ無し / 1 = 控えめ / 2 = 強め
            return (m, st) =>
            {
                float spec, env;
                switch (st)
                {
                    case SurfaceType.Cloth:
                        spec = new[] { 0f, 0.10f, 0.30f }[level];
                        env  = new[] { 0.05f, 0.20f, 0.45f }[level];
                        break;
                    case SurfaceType.Skin:
                        spec = new[] { 0f, 0.25f, 0.60f }[level];
                        env  = new[] { 0.08f, 0.25f, 0.50f }[level];
                        break;
                    case SurfaceType.Face:
                        spec = new[] { 0f, 0.10f, 0.35f }[level];
                        env  = new[] { 0.08f, 0.25f, 0.50f }[level];
                        break;
                    case SurfaceType.Hair:
                        // 髪の鏡面は _HairSpecIntensity が握っているので直接鏡面には触らない。
                        spec = -1f;
                        env  = new[] { 0.08f, 0.25f, 0.50f }[level];
                        break;
                    default:
                        spec = new[] { 0f, 0.20f, 0.60f }[level];
                        env  = new[] { 0.08f, 0.25f, 0.50f }[level];
                        break;
                }

                // **0 は「意図的に消してある」印なので復活させない。**
                // 移植元でアーティストが鏡面を切っていた個体（瞳・白目など）があり、
                // プリセットで一律に戻すとそこだけ光り出す。
                bool intentionallyOff = Get(m, "_SpecularIntensity", 1f) <= 1e-4f;

                if (spec >= 0f && !intentionallyOff) Set(m, "_SpecularIntensity", spec);
                Set(m, "_EnvSpecIntensity", env);
            };
        }

        private static float Get(Material m, string name, float fallback)
            => m.HasFloat(name) ? m.GetFloat(name) : fallback;

        private static void Set(Material m, string name, float v)
        {
            if (m.HasFloat(name)) m.SetFloat(name, v);
        }
    }
}

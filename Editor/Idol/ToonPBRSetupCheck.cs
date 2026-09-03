using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// ToonPBR が「実装したのに効いていない」状態を洗い出す診断。
    ///
    /// このシェーダーの機能はマテリアルの値だけでは完結しない。
    /// Renderer Feature、シーンのコンポーネント、URP Asset の設定のどれかが
    /// 欠けると**エラーも警告も出ないまま黙って無効になる。**
    /// 実際に踏んだ例:
    ///   - 焼いたマップは割り当て済みなのに強度が 0（T-063）
    ///   - 環境光が主光源と同量で影が埋まる（T-061）
    ///   - Face マテリアルがあるのに FaceDirectionBinder が無い（T-064）
    /// いずれも絵を見ても原因が分からない。ここで名指しする。
    /// </summary>
    public class ToonPBRSetupCheck : EditorWindow
    {
        private enum Level { Error, Warning, Info, Ok }


        private class Finding
        {
            public Level level;
            public string title;
            public string detail;
            public Object context;
            public string fixLabel;
            public System.Action fix;
        }

        private readonly List<Finding> _findings = new List<Finding>();
        private Vector2 _scroll;
        private bool _ranOnce;

        [MenuItem("Tools/Idol/セットアップ診断")]
        private static void Open()
        {
            var w = GetWindow<ToonPBRSetupCheck>("ToonPBR 診断");
            w.minSize = new Vector2(520, 320);
            w.Run();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("再診断", GUILayout.Width(90))) Run();
                EditorGUILayout.LabelField(
                    Selection.activeGameObject != null
                        ? $"対象: {Selection.activeGameObject.name}"
                        : "対象: (キャラを選択するとシーン側も見ます)");
            }

            EditorGUILayout.Space(4);

            if (!_ranOnce) { EditorGUILayout.HelpBox("「再診断」を押してください。", MessageType.Info); return; }

            if (_findings.Count == 0)
            {
                EditorGUILayout.HelpBox("問題は見つかりませんでした。", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var f in _findings) DrawFinding(f);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawFinding(Finding f)
        {
            var type = f.level switch
            {
                Level.Error   => MessageType.Error,
                Level.Warning => MessageType.Warning,
                Level.Ok      => MessageType.None,
                _             => MessageType.Info,
            };

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.HelpBox($"{f.title}\n{f.detail}", type);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (f.context != null && GUILayout.Button("選択", GUILayout.Width(60)))
                        Selection.activeObject = f.context;

                    if (f.fix != null && GUILayout.Button(f.fixLabel, GUILayout.Width(180)))
                    {
                        f.fix();
                        // 直したら状態が変わるので、開いているウィンドウを取り直す。
                        GetWindow<ToonPBRSetupCheck>().Run();
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // ====================================================================

        private void Run()
        {
            _ranOnce = true;
            _findings.Clear();

            CheckPipelineAsset();
            CheckSceneLights();
            CheckSelection();

            Repaint();
        }

        /// <summary>
        /// batchmode から回す入口。**検査そのものを検証するために要る。**
        ///
        /// この診断は EditorWindow なので、Editor を開かないと1行も見られなかった。
        /// つまり**追加した検査が意図した文言を出すかを確認せずに増やしていた** ──
        /// 実際 T-119 では条件を2つとも間違えており、正しい設定に警告を出していた。
        ///
        /// シーンを読まないので、シーン依存の検査（ライト・カメラ・コンポーネント）は
        /// 出ない。マテリアルと URP アセットに関する検査だけが対象。
        /// **何が回っていないかを出力に明記する** ── 「エラー 0」を
        /// 「全部確認した」と読み替えるのが一番危ない。
        ///
        /// 使い方:
        ///   Unity -batchmode -quit -nographics -projectPath . \
        ///         -executeMethod ToonNPR.EditorTools.ToonPBRSetupCheck.RunCI -logFile -
        /// </summary>
        public static void RunCI()
        {
            var w = CreateInstance<ToonPBRSetupCheck>();

            w._findings.Clear();
            w.CheckPipelineAsset();

            // シーンは読めないので、プロジェクト内の ToonPBR マテリアルを直接集める。
            var mats = AssetDatabase.FindAssets("t:Material")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<Material>)
                .Where(m => m != null && m.shader != null && m.shader.name.Contains("Idol"))
                .ToArray();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[SetupCheck] batchmode");
            sb.AppendLine($"  ToonPBR マテリアル: {mats.Length} 件");

            if (mats.Length > 0) w.CheckMaterialValues(mats);

            foreach (var f in w._findings)
                sb.AppendLine($"  {f.level,-7} {f.title}");

            int errors = w._findings.Count(f => f.level == Level.Error);

            sb.AppendLine($"  合計 {w._findings.Count} 件（エラー {errors}）");
            sb.AppendLine("  **シーン依存の検査は回していない**"
                        + "（ライト・カメラ・Renderer Feature・コンポーネントの有無）。"
                        + "batchmode ではシーンを読まないため。");

            Debug.Log(sb.ToString().TrimEnd());
            DestroyImmediate(w);

            EditorApplication.Exit(errors > 0 ? 1 : 0);
        }

        private void Add(Level level, string title, string detail,
                         Object context = null, string fixLabel = null, System.Action fix = null)
        {
            _findings.Add(new Finding
            {
                level = level, title = title, detail = detail,
                context = context, fixLabel = fixLabel, fix = fix,
            });
        }

        // --------------------------------------------------------------------
        //  URP Asset
        // --------------------------------------------------------------------
        private void CheckPipelineAsset()
        {
            var asset = (GraphicsSettings.defaultRenderPipeline
                      ?? QualitySettings.renderPipeline) as UniversalRenderPipelineAsset;

            if (asset == null)
            {
                Add(Level.Error, "URP Asset が見つからない",
                    "Graphics / Quality の Render Pipeline Asset が未設定。ToonPBR は URP 専用。");
                return;
            }

            if (!asset.supportsMainLightShadows)
                Add(Level.Error, "主光源の影が無効",
                    "URP Asset の Shadows > Main Light を ON にすること。" +
                    "リアルタイム影が一切出ないので、影の設定を触っても何も変わらない。", asset);

            // リムライトとコンタクトシャドウは深度テクスチャが前提（FR-43 / リム）。
            if (!asset.supportsCameraDepthTexture)
                Add(Level.Warning, "Depth Texture が無効",
                    "リムライトとコンタクトシャドウが動かない。URP Asset の Depth Texture を ON に。",
                    asset);

            if (asset.shadowDistance > 60f)
                Add(Level.Warning, $"Shadow Distance が長い ({asset.shadowDistance:0} m)",
                    "シャドウマップの1テクセルが太くなり、キャラの自己影が潰れる。" +
                    "近景のキャラを見るなら 20〜40m。引きの絵で必要なら Cascade を増やす。", asset);

            // **追加光源が「頂点単位」だと黙って消える。**
            // このシェーダーは `_ADDITIONAL_LIGHTS`（画素単位）しか宣言していない。
            // URP が Per Vertex を選ぶと `_ADDITIONAL_LIGHTS_VERTEX` の方が立ち、
            // どちらの分岐にも入らないので**追加光源が一切描かれない。**
            // エラーも警告も出ず、ライトを置いても何も起きないという形で出る。
            //
            // Forward+ は常にクラスタ経由の画素単位なので、この設定は無視される。
            if (asset.additionalLightsRenderingMode == LightRenderingMode.PerVertex)
                Add(Level.Error, "追加光源が「頂点単位」になっている",
                    "ToonPBR は画素単位（_ADDITIONAL_LIGHTS）しか実装していないので、"
                    + "**追加光源が一切描かれない**。ライトを置いても何も起きない形で出る。"
                    + " URP Asset の Lighting > Additional Lights を Per Pixel にするか、"
                    + " Renderer を Forward+ にすること（Forward+ はこの設定を無視して"
                    + "常に画素単位で処理する）。",
                    asset);

            CheckShadowTexelDensity(asset);
            CheckAntiAliasing(asset);
            CheckRendererFeatures(asset);
        }

        /// <summary>
        /// アンチエイリアスが1つも掛かっていない状態を見る。
        ///
        /// **トゥーンは特に AA に敏感。** 明暗の境界を意図的に狭く保つ設計なので、
        /// 1 画素で 0→1 に変わる箇所が絵の中に大量にある。そこに AA が無いと、
        /// カメラや光源が少し動くだけで境界の画素が入れ替わり、
        /// 「細かい部分がちらつく」という形で出る。
        ///
        /// シェーダー側は境界に AA の下限を張って受けているが（T-067 / T-113）、
        /// **あれはシェーディングの階段を均すもので、ジオメトリのシルエットには効かない。**
        /// 髪の毛束や襟の縁のような細い形状は、パイプライン側の AA が要る。
        /// </summary>
        private void CheckAntiAliasing(UniversalRenderPipelineAsset asset)
        {
            int msaa = asset.msaaSampleCount;

            var cam = Camera.main;
            var camData = cam != null ? cam.GetUniversalAdditionalCameraData() : null;
            var postAA = camData != null ? camData.antialiasing : AntialiasingMode.None;

            bool hasMsaa = msaa > 1;
            bool hasPost = postAA != AntialiasingMode.None;

            if (hasMsaa || hasPost)
            {
                Add(Level.Info, "アンチエイリアスは有効",
                    $"MSAA {(hasMsaa ? msaa + "x" : "オフ")} / ポスト {postAA}。", asset);
                return;
            }

            Add(Level.Warning, "アンチエイリアスが1つも掛かっていない",
                "URP Asset の MSAA がオフで、メインカメラのポスト AA も None。"
                + " トゥーンは明暗の境界を狭く保つ設計なので、AA が無いと境界の画素が"
                + " カメラの微動で入れ替わり、細部が常にちらつく。"
                + " **MSAA 4x が第一候補**（ジオメトリのシルエットにも効く）。",
                asset);
        }

        /// <summary>
        /// カスケード0 のテクセルが顔の上で何 mm になるかを出す。
        ///
        /// **影の明滅の原因はほぼここ。** トゥーンのステップは遷移窓が狭いので、
        /// シャドウマップのテクセルが幾何の上を滑るとその量子化がそのまま
        /// ステップを叩いて明滅する。解像度を上げると収まるのは変化率が下がるからで、
        /// 「解像度が足りない」のではなく「ステップの幅が入力の1画素変化より狭い」が本質。
        ///
        /// シェーダー側は AA の下限（T-067 / T-113）で受けているが、
        /// 元のテクセルが太すぎると下限が広がりすぎて影の切れが失われる。
        /// 数字を出しておけば「解像度を上げる」以外の手（カスケードを増やす）も選べる。
        /// </summary>
        /// <summary>
        /// カスケード0 の 1 テクセル実寸を出す。読めなければ false。
        /// **式の唯一の出所**（ToonPBRPresets のちらつき対策の文面も
        /// ここから読む ── 書き写した数字は必ずずれる。T-155 / T-107）。
        /// </summary>
        internal static bool TryMainShadowTexel(UniversalRenderPipelineAsset asset,
                                                out float radius0, out int tile, out float texelMm)
        {
            radius0 = 0f; tile = 0; texelMm = -1f;
            if (asset == null || !asset.supportsMainLightShadows) return false;

            int cascades = Mathf.Max(1, asset.shadowCascadeCount);

            // カスケード0 が覆う半径[m]。URP は分割比を距離の割合で持つ。
            float split0 = cascades == 1 ? 1f
                         : cascades == 2 ? asset.cascade2Split
                         : cascades == 3 ? asset.cascade3Split.x
                                         : asset.cascade4Split.x;
            radius0 = asset.shadowDistance * split0;

            // アトラスは 2x2 に切られる（カスケード 2 以上）。1 なら丸ごと使う。
            int atlas = (int)asset.mainLightShadowmapResolution;
            tile = cascades == 1 ? atlas : atlas / 2;

            // 正射影はカスケード球の直径を覆う。
            texelMm = (radius0 * 2f) / tile * 1000f;
            return true;
        }

        private void CheckShadowTexelDensity(UniversalRenderPipelineAsset asset)
        {
            if (!TryMainShadowTexel(asset, out float radius0, out int tile, out float texelMm))
                return;

            // 顔の幅を 15cm として、何テクセルで覆えるか。
            float texelsAcrossFace = 150f / Mathf.Max(texelMm, 1e-4f);

            string detail =
                $"カスケード0 は {radius0:0.#}m を {tile} テクセルで覆うので、1テクセル ≒ {texelMm:0.0}mm。"
                + $"顔（15cm）は約 {texelsAcrossFace:0} テクセル。";

            // 顔が 40 テクセルを切ると、鼻や顎の自己影がテクセル単位でばたつく。
            if (texelsAcrossFace < 40f)
            {
                Add(Level.Warning,
                    $"シャドウマップが粗い（顔が約 {texelsAcrossFace:0} テクセル）",
                    detail
                    + " 顔の自己影がテクセル単位でばたつき、ライトやカメラを動かすと明滅する。"
                    + " **解像度を上げるより Cascade を増やす方が効く** ── "
                    + $"Shadow Distance {asset.shadowDistance:0}m を保ったまま近景だけ密にできる。"
                    + $"現在 {Mathf.Max(1, asset.shadowCascadeCount)} 段。4 段にすればカスケード0 が狭くなり、メモリは同じまま密度が上がる。",
                    asset);
            }
            else
            {
                Add(Level.Info, $"シャドウマップの密度は十分（顔が約 {texelsAcrossFace:0} テクセル）",
                    detail, asset);
            }
        }

        private void CheckRendererFeatures(UniversalRenderPipelineAsset asset)
        {
            // 公開 API から Renderer Data を辿る方法がバージョンで揺れるので
            // SerializedObject 経由で読む（NFR-07）。
            var so = new SerializedObject(asset);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null || !list.isArray) return;

            var features = new List<ScriptableRendererFeature>();
            var datas = new List<ScriptableRendererData>();

            for (int i = 0; i < list.arraySize; i++)
            {
                var data = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
                if (data == null) continue;
                datas.Add(data);
                if (data.rendererFeatures != null)
                    features.AddRange(data.rendererFeatures.Where(f => f != null));
            }

            if (datas.Count == 0) return;

            bool ssaoActive = features.Any(f => f.isActive && f.GetType().Name == "ScreenSpaceAmbientOcclusion");
            if (!ssaoActive)
                Add(Level.Info, "SSAO が無効",
                    "ToonPBR は URP の SSAO を受け取れる（FR-44）が、Renderer Feature が無効だと " +
                    "キーワードが立たず何も起きない。首の下や袖の内側の落ち込みはここで出る。",
                    datas[0]);

            // 以下は「そのマテリアル機能を使うなら」必要なもの。使っていなければ黙る。
            var mats = CollectToonMaterials();

            if (mats.Any(m => m.HasFloat("_OutlineOn") && m.GetFloat("_OutlineOn") > 0.5f)
             && !features.Any(f => f is ToonOutlineFeature && f.isActive))
                Add(Level.Error, "アウトラインが有効なのに ToonOutlineFeature が無い",
                    // **名前は T-249 で ToonOutline から振り直している。**
                    // 機能側（ShaderTagId）は追随したが、この案内文だけ古いままだった
                    // ── 探しても見つからない名前を案内していた（T-267）。
                    "Outline パスは独自 LightMode \"IdolOutline\" にあり、URP は既定で描かない（FR-57）。" +
                    "Feature を入れないと輪郭は一切出ない。", datas[0]);
        }

        // --------------------------------------------------------------------
        //  シーン
        // --------------------------------------------------------------------
        private void CheckSceneLights()
        {
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var dir = lights.Where(l => l.type == LightType.Directional && l.enabled).ToArray();

            if (dir.Length == 0)
            {
                Add(Level.Error, "Directional Light が無い",
                    "拡散の伝達関数は主光源の NdotL で駆動する。光源が無ければ影の境界も生まれない。");
                return;
            }

            if (dir.All(l => l.shadows == LightShadows.None))
                Add(Level.Warning, "影を落とす Directional Light が無い",
                    "全ての Directional Light の Shadow Type が No Shadows。" +
                    "トゥーンの陰影（NdotL 由来）は出るが、腕から胴への落ち影は出ない。",
                    dir[0].gameObject);
        }

        // --------------------------------------------------------------------
        //  選択中のキャラ
        // --------------------------------------------------------------------
        private void CheckSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var mats = go.GetComponentsInChildren<Renderer>(true)
                         .SelectMany(r => r.sharedMaterials)
                         .Where(IsToonPBR)
                         .Distinct()
                         .ToArray();

            if (mats.Length == 0)
            {
                Add(Level.Info, "選択中のオブジェクトに ToonPBR のマテリアルが無い",
                    "キャラのルートを選択して再診断すると、コンポーネントの過不足まで見ます。", go);
                return;
            }

            // Face は _HeadForward をスクリプトから貰わないと SDF が使えない（T-064）。
            if (mats.Any(m => m.HasFloat("_SurfaceType") && Mathf.Approximately(m.GetFloat("_SurfaceType"), 2f))
             && go.GetComponentInChildren<FaceDirectionBinder>(true) == null)
                Add(Level.Warning, "Surface Type = Face のマテリアルがあるが FaceDirectionBinder が無い",
                    "顔の SDF 影は頭の向きを外から貰って成立する。無い場合は自動で通常の陰影に" +
                    "落ちるので壊れはしないが、SDF は効かない。",
                    go, "Binder を追加", () =>
                    {
                        Undo.AddComponent<FaceDirectionBinder>(go);
                    });


            // 平滑法線は頂点カラー RGB に焼く。焼かれていないメッシュだと
            // Unity が (1,1,1,1) を供給し、デコード結果は斜め方向の定数になる
            // ＝ 輪郭が全方向へ均一に膨らまず破綻する。
            if (mats.Any(m => m.HasFloat("_OutlineOn") && m.GetFloat("_OutlineOn") > 0.5f
                           && m.HasFloat("_UseSmoothNormal") && m.GetFloat("_UseSmoothNormal") > 0.5f))
            {
                var noColor = go.GetComponentsInChildren<Renderer>(true)
                    .Select(GetSharedMesh).Where(mesh => mesh != null)
                    .Where(mesh => mesh.colors == null || mesh.colors.Length == 0)
                    .Distinct().ToArray();

                if (noColor.Length > 0)
                    Add(Level.Error, $"平滑法線が有効だが頂点カラーが焼かれていない: {noColor.Length} メッシュ",
                        "Use Baked Smooth Normal は頂点カラー RGB を読む。未ベイクのメッシュでは " +
                        "Unity が (1,1,1) を返し、押し出し方向が斜め固定になって輪郭が破綻する。" +
                        "Tools > Idol > Bake Smooth Normals を実行するか、トグルを OFF に。",
                        noColor[0]);
            }

            ReportShadowContrast(mats);
            CheckMaterialValues(mats);
        }

        /// <summary>
        /// **影が光の何倍の明るさになるか**を実際の値から計算して出す。
        ///
        /// 「影が薄い」は原因が3つに分かれる ── 影色そのもの、影の中の環境光、
        /// 環境光と主光源の強度比。どれが効いているかは値を見ても分からないので、
        /// 最終的な比を出してしまう方が早い。実際 T-061 では
        /// `_ShadowValue 0.75` が正常に見えて、環境光が 2 だったせいで
        /// 影／光が 0.875（12% しか暗くない）になっていた。
        /// </summary>
        private void ReportShadowContrast(Material[] mats)
        {
            var key = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(l => l.type == LightType.Directional && l.enabled)
                .OrderByDescending(l => l.intensity).FirstOrDefault();
            if (key == null) return;

            var m = mats.FirstOrDefault(x => x.HasFloat("_ShadowValue")
                                          && x.HasFloat("_AmbientIntensity")
                                          && x.HasFloat("_ShadowAmbientIntensity"));
            if (m == null) return;

            // 環境光の明るさは SH の平均輝度で見る。空の色から取れる。
            float sh = RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox
                     ? 0.5f
                     : (RenderSettings.ambientLight.r + RenderSettings.ambientLight.g
                      + RenderSettings.ambientLight.b) / 3f;

            float amb    = m.GetFloat("_AmbientIntensity") * sh;
            float ambSh  = amb * m.GetFloat("_ShadowAmbientIntensity");
            float lit    = key.intensity + amb;
            float shadow = key.intensity * m.GetFloat("_ShadowValue") + ambSh;
            float ratio  = lit > 1e-4f ? shadow / lit : 1f;

            string verdict =
                ratio > 0.80f ? "**ほとんど差が出ない。** 影として認識されない" :
                ratio > 0.70f ? "薄い。トゥーン影としては弱い" :
                ratio > 0.45f ? "実用域" :
                                "濃い。潰れていないか確認すること";

            Add(ratio > 0.70f ? Level.Warning : Level.Info,
                $"影の濃さ: 影は光の {ratio:0.00} 倍 — {verdict}",
                $"主光源 {key.intensity:0.##} / 環境光 {amb:0.##}（影の中 {ambSh:0.##}） / " +
                $"Shadow Value {m.GetFloat("_ShadowValue"):0.##}。" +
                "薄いときは Intensity in Shadow を下げるのが一番副作用が少ない" +
                "（全体の明るさを保ったまま影だけ沈む）。",
                m);
        }

        // --------------------------------------------------------------------
        //  マテリアルの値
        // --------------------------------------------------------------------
        private void CheckMaterialValues(Material[] mats)
        {
            // テクスチャは入れたのに強度 0、という組み合わせ（T-063 で実際に全滅していた）。
            (string tex, string gate, string label)[] pairs =
            {
                ("_BumpMap",        "_NormalMapOn",          "Normal Map"),
                ("_NPRMap",         "_NPRMapOn",             "NPR Map"),
                ("_BentNormalMap",  "_BentNormalOn",         "Bent Normal"),
                ("_ShadeNormalMap", "_ShadeNormalStrength",  "Shade Normal"),
                ("_CurvatureMap",   "_CurvatureSoftness",    "Curvature Map"),
                ("_SSSMap",         "_SSSMapStrength",       "SSS Map"),
                ("_HairFlowMap",    "_HairFlowStrength",     "Hair Flow"),
            };

            foreach (var (tex, gate, label) in pairs)
            {
                var off = mats.Where(m => m.HasTexture(tex) && m.GetTexture(tex) != null
                                       && m.HasFloat(gate) && m.GetFloat(gate) <= 0f).ToArray();
                if (off.Length == 0) continue;

                Add(Level.Warning, $"{label} が割り当て済みなのに無効: {off.Length} 件",
                    $"テクスチャは入っているが {gate} が 0 なので1枚も使われていない。",
                    off[0], "まとめて有効化", () =>
                    {
                        Undo.RecordObjects(off, "Enable " + label);
                        foreach (var m in off) m.SetFloat(gate, 1f);
                        foreach (var m in off) EditorUtility.SetDirty(m);
                        AssetDatabase.SaveAssets();
                    });
            }

            // **逆向きの事故: 強度は入っているのにテクスチャが無い。**
            // 既定テクスチャが中立なマップ（bump / gray / 白の Cavity）なら実害は無いが、
            // 下の2つは中立ではないので、未割当のまま有効にすると黙って絵が狂う。
            (string tex, string gate, string label, string harm)[] nonNeutral =
            {
                ("_HairFlowMap", "_HairFlowStrength", "Hair Flow",
                 "既定の白は cos2θ=1 / sin2θ=1 と解釈され、繊維方向が 22.5 度回る。" +
                 "エンジェルリングの角度がずれる"),
                ("_SSSMap", "_SSSMapStrength", "SSS Map",
                 "既定の bump はアルファが 1 なので、厚みが最大で固定される。" +
                 "MaskMap の B に焼いた厚みが無視され、透過が均一に出る"),
            };

            foreach (var (tex, gate, label, harm) in nonNeutral)
            {
                var orphan = mats.Where(m => m.HasFloat(gate) && m.GetFloat(gate) > 0f
                                          && m.HasTexture(tex) && m.GetTexture(tex) == null).ToArray();
                if (orphan.Length == 0) continue;

                Add(Level.Warning, $"{label} が未割当のまま有効: {orphan.Length} 件",
                    $"{harm}。テクスチャを割り当てるか {gate} を 0 にすること。",
                    orphan[0], "強度を 0 にする", () =>
                    {
                        Undo.RecordObjects(orphan, "Disable " + label);
                        foreach (var m in orphan) m.SetFloat(gate, 0f);
                        foreach (var m in orphan) EditorUtility.SetDirty(m);
                        AssetDatabase.SaveAssets();
                    });
            }

            // **ゲートが OFF なのに、その先の値だけ調整されている。**
            // 「設定したのに効かない」の裏返しで、**触った本人は効いていると思っている。**
            // 値を見ても分からないので、ゲートとセットで名指しする。
            // **サーフェスタイプで絞ること。** プロパティはシェーダー全体で共有なので、
            // 絞らないと髪以外の 39 マテリアルにも「異方性が設定されている」と出る
            // ── 実際に batchmode で回して 46 件と出た。**関係ないマテリアルに出る
            // 指摘は誤検出と同じ**で、本当に見るべき 7 件が埋もれる。
            (string gate, string tuned, float neutral, int type, string label, string note)[] tunedButOff =
            {
                ("_HairAnisoGGXOn", "_HairAnisotropy", 0f, 3, "髪の異方性",
                 "髪は Kajiya-Kay 経路で動いており、Anisotropy は異方性 GGX 経路でしか読まれない。"
                 + " **さらに符号に注意** ── 正は毛に沿った縦の筋（濡れ髪向け）、"
                 + "アニメ髪の「天使の輪」は毛を横切る帯なので**負**が要る。"
                 + " GGX を有効にするなら符号も見直すこと"),
            };

            foreach (var (gate, tuned, neutral, type, label, note) in tunedButOff)
            {
                var hit = mats.Where(m =>
                    m.HasFloat("_SurfaceType")
                    && Mathf.RoundToInt(m.GetFloat("_SurfaceType")) == type
                    && m.HasFloat(gate) && m.GetFloat(gate) <= 0.5f
                    && m.HasFloat(tuned) && Mathf.Abs(m.GetFloat(tuned) - neutral) > 1e-4f).ToArray();

                if (hit.Length == 0) continue;

                Add(Level.Info, $"{label}: ゲートが OFF なのに値が設定されている: {hit.Length} 件",
                    $"{tuned} = {hit[0].GetFloat(tuned)} が入っているが {gate} が OFF なので読まれない。"
                    + $" {note}。",
                    hit[0]);
            }

            // **ブルーノイズが欠けている / 小さすぎる。** 影フィルタの回転角に使う
            // ので（T-390）、無いと gray = 一定角 → 画面全体が同時にテクセル境界を
            // 踏んで**明滅**する（T-124 の症状）。既定は .shader.meta が包内の 256²
            // を指すが、他パッケージのテクスチャを参照したまま消えた材質を拾う。
            var badNoise = mats.Where(m =>
                {
                    if (!m.HasTexture("_BlueNoiseTex")) return false;
                    var t = m.GetTexture("_BlueNoiseTex");
                    return t != null && (t.width < 64 || t.height < 64);
                }).ToArray();
            if (badNoise.Length > 0)
                Add(Level.Warning, $"ブルーノイズが小さすぎる: {badNoise.Length} 件",
                    "_BlueNoiseTex に 64px 未満のテクスチャが入っている。影フィルタの"
                    + "回転角が偏り、縞や明滅になる。空にすればシェーダー既定（包内の 256²）に戻る。",
                    badNoise[0]);

            // **サーフェスタイプは設定されているのに、そのタイプの機能が全部 0。**
            //
            // タイプを分けている意味が無い状態で、描画は Default と同一になる。
            // 「タイプを設定したのだから効いているはず」と思い込みやすく、
            // 見た目が変わらない理由が最後まで分からない類の事故。
            //
            // **一括操作で起きる。** T-117 で散乱を既定 OFF にしたとき、
            // Skin タイプの機能は皮下散乱・透過・頬の赤みの3つしか無かったので、
            // **Skin が丸ごと Default と同じになった**（4 マテリアルが該当）。
            // 個別に切ったつもりが型ごと無効化していた、という形で出る。
            (int type, string label, string[] gates)[] typeFeatures =
            {
                (1, "Skin",  new[] { "_SubsurfaceStrength", "_TransmissionStrength" }),
                (3, "Hair",  new[] { "_HairSpecIntensity" }),
                (4, "Cloth", new[] { "_SheenIntensity" }),
            };

            foreach (var (type, label, gates) in typeFeatures)
            {
                var inert = mats.Where(m =>
                    m.HasFloat("_SurfaceType")
                    && Mathf.RoundToInt(m.GetFloat("_SurfaceType")) == type
                    && gates.All(g => !m.HasFloat(g) || m.GetFloat(g) <= 0f)).ToArray();

                if (inert.Length == 0) continue;

                Add(Level.Warning,
                    $"Surface Type = {label} だが固有の効果が全部 0: {inert.Length} 件",
                    $"{label} を分けている意味が無く、描画は Default と同一になる。"
                    + $"効くのは {string.Join(" / ", gates)} だけで、そのすべてが 0。"
                    + "意図的なら Surface Type を Default に戻す方が誤解が無い。"
                    + "そうでなければどれかを上げること。",
                    inert[0]);
            }

            // **アルファテストで全ピクセルが落ちるマテリアル。**
            // BaseMap 未割当だと既定の白が使われるので、アルファは _BaseColor.a そのもの。
            // それが Cutoff を下回っていると **1ピクセルも描かれない**。
            // 他シェーダーから移植したとき、alpha の意味が違って起きる（実際に1件あった）。
            var invisible = mats.Where(m =>
                m.IsKeywordEnabled("_ALPHATEST_ON")
                && m.HasTexture("_BaseMap") && m.GetTexture("_BaseMap") == null
                && m.HasColor("_BaseColor") && m.HasFloat("_Cutoff")
                && m.GetColor("_BaseColor").a < m.GetFloat("_Cutoff")).ToArray();

            if (invisible.Length > 0)
                Add(Level.Error, $"アルファテストで完全に消えるマテリアル: {invisible.Length} 件",
                    "Alpha Clip が ON で BaseMap が未割当、かつ Base Color のアルファが Cutoff 未満。" +
                    "アルファは _BaseColor.a そのものになるので、全ピクセルが clip される。" +
                    "意図的に消しているのでなければ、テクスチャを割り当てるかアルファを上げること。",
                    invisible[0]);

            // **Range を外れた値。** `Range` 属性はインスペクタのスライダを縛るだけで、
            // 実行時の値は縛らない。他シェーダーから移植したマテリアルには範囲外の値が
            // そのまま残り、lerp が外挿になって**色や遮蔽が負に振れる**（T-076 / T-098）。
            var outOfRange = new List<string>();
            Material sample = null;

            foreach (var m in mats)
            {
                var sh = m.shader;
                int count = sh.GetPropertyCount();

                for (int i = 0; i < count; i++)
                {
                    if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Range) continue;

                    string name = sh.GetPropertyName(i);
                    if (!m.HasFloat(name)) continue;

                    float v  = m.GetFloat(name);
                    float lo = sh.GetPropertyRangeLimits(i).x;
                    float hi = sh.GetPropertyRangeLimits(i).y;

                    if (v < lo - 1e-4f || v > hi + 1e-4f)
                    {
                        outOfRange.Add($"{m.name}.{name} = {v}（許容 {lo}〜{hi}）");
                        if (sample == null) sample = m;
                    }
                }
            }

            if (outOfRange.Count > 0)
                Add(Level.Error, $"Range を外れた値: {outOfRange.Count} 件",
                    "Range 属性は実行時の値を縛らない。範囲外だと lerp が外挿になり、" +
                    "色や遮蔽が負に振れることがある: " +
                    string.Join(" / ", outOfRange.Take(6)),
                    sample, "範囲内に丸める", () =>
                    {
                        Undo.RecordObjects(mats, "Clamp Out Of Range");
                        foreach (var m in mats)
                        {
                            var sh = m.shader;
                            int c2 = sh.GetPropertyCount();
                            for (int i = 0; i < c2; i++)
                            {
                                if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Range) continue;
                                string nm = sh.GetPropertyName(i);
                                if (!m.HasFloat(nm)) continue;
                                float lo = sh.GetPropertyRangeLimits(i).x;
                                float hi = sh.GetPropertyRangeLimits(i).y;
                                m.SetFloat(nm, Mathf.Clamp(m.GetFloat(nm), lo, hi));
                            }
                            EditorUtility.SetDirty(m);
                        }
                        AssetDatabase.SaveAssets();
                    });

            // 環境光が主光源と同量まで来ると、影は環境光ぶん持ち上がって読めなくなる（T-061）。
            var washed = mats.Where(m => m.HasFloat("_AmbientIntensity")
                                      && m.GetFloat("_AmbientIntensity") >= 1.5f
                                      && m.HasFloat("_ShadowAmbientIntensity")
                                      && m.GetFloat("_ShadowAmbientIntensity") >= 0.9f).ToArray();
            if (washed.Length > 0)
                Add(Level.Warning, $"環境光が影を埋めている可能性: {washed.Length} 件",
                    "Ambient Intensity が高く、かつ Intensity in Shadow が 1 のまま。" +
                    "環境光は影の中にも一律で乗るので、影が『わずかに暗い』程度にしかならない。" +
                    "全体の明るさを保ったまま影だけ沈めるには Intensity in Shadow を下げる。",
                    washed[0], "Intensity in Shadow = 0.5", () =>
                    {
                        Undo.RecordObjects(washed, "Reduce Ambient In Shadow");
                        foreach (var m in washed) m.SetFloat("_ShadowAmbientIntensity", 0.5f);
                        foreach (var m in washed) EditorUtility.SetDirty(m);
                        AssetDatabase.SaveAssets();
                    });
        }

        // --------------------------------------------------------------------

        private static Mesh GetSharedMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private static bool IsToonPBR(Material m)
        {
            return m != null && m.shader != null && m.shader.name.Contains("Idol");
        }

        /// <summary>プロジェクト全体の ToonPBR マテリアル。Feature の要否判定に使う。</summary>
        private static Material[] CollectToonMaterials()
        {
            return AssetDatabase.FindAssets("t:Material")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<Material>)
                .Where(IsToonPBR)
                .ToArray();
        }
    }
}

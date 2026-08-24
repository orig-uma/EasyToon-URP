// =============================================================================
//  ToonPBRShaderGUI.cs
// -----------------------------------------------------------------------------
//  Origuma/EasyToon_URP/Idol のカスタムインスペクタ。
//
//  描画プリミティブ（ツールバー・日英切替・セクションバー・⚡注記・プロパティ
//  キャッシュ）は EasyShaderCore の ShaderGuiKit に委譲する。Doll と
//  同じ見た目になり、2 つを行き来する人が同じ操作で使えるようにするため。
//
//  **自前のキャッシュは持たない。** 以前は EditorPrefs とプロパティ検索を
//  自前で辞書化していたが、Kit が同じことをしているので二重に持つ意味が無い
//  （Idol が Assets 直下にあった頃の「パッケージに依存させない」方針の名残）。
//
//  この GUI にしか無い仕事は3つ:
//    1. Surface Type に関係ないセクションを消す（髪の設定が肌マテリアルに見えていた）
//    2. テクスチャを割り当てたらトグルを自動で ON にする（付け忘れの事故を防ぐ）
//    3. ステンシルの組み合わせをボタンにする（5 つ揃って初めて機能するため）
//
//  状態はすべてマテリアルのプロパティとキーワードに入るので、
//  この ShaderGUI を外しても既存マテリアルはそのまま動く。
// =============================================================================
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Origuma.EasyShaderCore.Editor;

namespace ToonNPR.EditorTools
{
    public class ToonPBRShaderGUI : ShaderGUI
    {
        private const string KeyPrefix = "Origuma.EasyToon.URP.Idol.";
        private const string TabKey = KeyPrefix + "tab";

        // GitHub 上のドキュメント（GUI からリンクで開く）。⚡ の tooltip が
        // SRP_BATCHER.md を案内するので、ツールバーからも同じ文書へ飛べるようにする。
        private const string DocBaseUrl = "https://github.com/orig-uma/EasyToon-URP/blob/main/Documentation~/";
        private const string SrpBatcherDocUrl = DocBaseUrl + "SRP_BATCHER.md";

        // KeywordEnum の並びと一致させること。ずれると別セクションが出る。
        private enum ToonSurfaceType { Default = 0, Skin = 1, Face = 2, Hair = 3, Cloth = 4 }

        private ShaderGuiKit _kit;
        private ToonPBRBakingPanel _baking;
        private MaterialEditor _editor;

        // タブ選択（EditorPrefs で永続化。Doll と同方式）。
        private int _tab = -1;

        // 検索文字列（セッション内のみ保持。Doll と同方式）。
        private string _search = "";

        // **タブは Doll と同じ 8 つ・同名・同順**（T-352 / T-354。利用者の要望
        // 「なるべく Doll に揃えて」）。棚割りも Doll の分類に従う: リムは質感タブ
        // の「縁」、クリアコート・グリッタは質感タブの「コートとグリッター」。
        // レンダーステート等は詳細タブ（Baking に同居させたら「Baking タブに
        // レンダーステートがいる」と利用者に叱られた ── T-354 で独立に戻した。
        // Doll 側も同時に 8 タブ化して同一を保っている）。
        private static readonly string[] s_TabsEn =
            { "Base", "Shading", "Lighting", "Specular", "Effects", "FX", "Advanced", "Baking" };
        private static readonly string[] s_TabsJp =
            { "基本", "陰・影", "ライト", "スペキュラ", "質感", "演出", "詳細", "Baking" };

        // ================================================================
        //  エントリポイント
        // ================================================================
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            _editor = materialEditor;
            _kit ??= new ShaderGuiKit(KeyPrefix);
            _baking ??= new ToonPBRBakingPanel();
            _kit.LoadPrefs();
            _kit.RebuildPropCache(properties);
            _kit.DrawToolbar("EasyToon / Idol",
                _kit.Jp ? "SRP Batcher ガイド" : "SRP Batcher guide", SrpBatcherDocUrl);

            if (!_kit.UseCustomUI)
            {
                base.OnGUI(materialEditor, properties);
                return;
            }

            // --- 検索（入力中はタブを無視して一致プロパティをフラット表示）---
            EditorGUILayout.Space(2);
            _search = EditorGUILayout.TextField(GUIContent.none, _search, EditorStyles.toolbarSearchField);
            if (!string.IsNullOrWhiteSpace(_search))
            {
                DrawSearchResults(materialEditor, properties);
                return;
            }

            // --- タブバー（4 列グリッド・選択を永続化）---
            if (_tab < 0) _tab = EditorPrefs.GetInt(TabKey, 0);
            EditorGUI.BeginChangeCheck();
            _tab = GUILayout.SelectionGrid(Mathf.Clamp(_tab, 0, s_TabsEn.Length - 1),
                _kit.Jp ? s_TabsJp : s_TabsEn, 4, EditorStyles.miniButtonMid);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(TabKey, _tab);
            EditorGUILayout.Space(4);

            // **タブに関係なく出す。** 割り当て忘れは別のタブで起きるので、
            // そのタブを開いたときだけ見えても手遅れになる。
            WarnMissingTextures();

            switch (_tab)
            {
                case 0: DrawTabBase(materialEditor); break;
                case 1: DrawTabShading(materialEditor); break;
                case 2: DrawTabLighting(materialEditor); break;
                case 3: DrawTabSpecular(materialEditor); break;
                case 4: DrawTabEffects(materialEditor); break;
                case 5: DrawTabFx(materialEditor); break;
                case 6: DrawTabAdvanced(materialEditor); break;
                case 7: _baking.Draw(materialEditor, _kit); break;
            }
        }

        // ================================================================
        //  検索: 表示名 / プロパティ名の部分一致（大文字小文字無視）
        // ================================================================
        private void DrawSearchResults(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            var query = _search.Trim();
            var hits = 0;

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var prop in properties)
                {
                    // MaterialProperty.propertyFlags は新しい 6000.x で追加された API。
                    // それ以前のエディタでは従来の flags で判定する。
#if UNITY_6000_3_OR_NEWER
                    if ((prop.propertyFlags & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0)
                        continue;
#else
                    if ((prop.flags & MaterialProperty.PropFlags.HideInInspector) != 0)
                        continue;
#endif
                    if (prop.displayName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0 &&
                        prop.name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    materialEditor.ShaderProperty(prop, prop.displayName);
                    hits++;
                }

                if (hits == 0)
                    EditorGUILayout.LabelField(
                        _kit.Jp ? $"\"{query}\" に一致するプロパティはありません（英語の表示名 / プロパティ名で検索）。"
                                : $"No properties match \"{query}\" (searches English display names / property names).",
                        EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField(
                _kit.Jp ? $"{hits} 件ヒット。検索を消すとタブ表示に戻ります。"
                        : $"{hits} match(es). Clear the search to return to tabs.",
                EditorStyles.miniLabel);
        }

        // ================================================================
        //  タブ 0: 基本
        // ================================================================
        private void DrawTabBase(MaterialEditor e)
        {
            DrawSurfaceType(e);
            DrawBase(e);
            DrawMaskMap(e);
            DrawNPRMap(e);
            DrawEmission(e);

            // 輪郭線は「キャラの基本の見た目」（マテリアルごとの恒久設定）で
            // あって演出ではないので基本タブに置く（T-353。Doll も同じ棚）。
            DrawOutline(e);
        }

        // 描画モード。Doll の Render Mode と同じ 3 択（T-358）。
        private static readonly string[] s_RenderModeEn = { "Opaque", "Cutout", "Transparent" };
        private static readonly string[] s_RenderModeJp = { "不透明", "カットアウト", "半透明" };

        private void DrawSurfaceType(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("surface", true, "Surface", "サーフェス")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    SubHeader("Surface Type (Part)", "サーフェスタイプ（部位）");

                    // ここで以降のタブに出るセクションが全部変わる。
                    EditorGUI.BeginChangeCheck();
                    Pv(e, "_SurfaceType", "Surface Type",
                        "Selects which material features are compiled and shown",
                        "どの質感機能をコンパイルして表示するかを決めます");
                    if (EditorGUI.EndChangeCheck()) ApplyKeywordsToTargets();

                    // **行き先をタイプごとに正しく書く。** 以前は「質感タブに増えます」と
                    // 一括で案内していたが、T-347 の棚割り変更で顔は陰・影タブ、
                    // 髪と布のシーンはスペキュラタブへ移っており**嘘になっていた**
                    //（利用者が最初に読む案内なので実害が大きい）。
                    Note("Skin = Effects tab (subsurface / transmission / sheer). "
                        + "Face = Shading tab (Face SDF). Hair = Specular tab (anisotropic). "
                        + "Cloth = Specular tab (sheen) + Effects tab (transmission / sheer).",
                        "**Skin** = 質感タブ（表面下散乱・透過・シアー生地）／"
                        + "**Face** = 陰・影タブ（顔 SDF）／"
                        + "**Hair** = スペキュラタブ（異方性ハイライト）／"
                        + "**Cloth** = スペキュラタブ（シーン）＋質感タブ（透過・シアー生地）。");

                    // **Default は「まだ選んでいない」状態とほぼ同義。** 実プロジェクトで
                    // 34 件が Default のまま放置されていた（param_check が検出）。
                    // 部位を選ぶまで肌・顔・髪・布の機能は 1 つも出ないので、
                    // 気付けるようにここで言う。
                    if (Mathf.RoundToInt(GetFloat("_SurfaceType")) == (int)ToonSurfaceType.Default)
                        EditorGUILayout.HelpBox(
                            _kit.Jp
                                ? "Surface Type が Default です。肌・顔・髪・布の機能は"
                                  + "**部位を選ぶまで 1 つも表示されません**（絵にも出ません）。"
                                : "Surface Type is Default. None of the skin / face / hair / cloth "
                                  + "features appear - or render - until you pick a part.",
                            MessageType.Info);

                    SubHeader("Render Mode", "描画モード");
                    DrawRenderMode(e);
                }
            }
        }

        /// <summary>
        /// 不透明 / カットアウト / 半透明の 3 択（Doll の Render Mode と同型）。
        /// ブレンド・ZWrite・キュー・RenderType タグ・アルファクリップを
        /// **まとめて**設定する ── 個別に触ると必ず食い違うため。
        /// </summary>
        private void DrawRenderMode(MaterialEditor e)
        {
            var mat = e.target as Material;
            if (mat == null) return;

            int mode = DetectRenderMode(mat);
            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.Popup(
                Label("Render Mode",
                      "Opaque / Cutout (alpha clip) / Transparent (alpha blend). "
                      + "Sets blending, ZWrite, render queue and RenderType together",
                      "不透明 / カットアウト（アルファクリップ）/ 半透明（アルファブレンド）。"
                      + "ブレンド・ZWrite・レンダーキュー・RenderType をまとめて設定します"),
                mode, _kit.Jp ? s_RenderModeJp : s_RenderModeEn);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var t in _editor.targets)
                {
                    var m = t as Material;
                    if (m == null) continue;
                    Undo.RecordObject(m, "Change Render Mode");
                    ApplyRenderMode(m, next);
                    ApplyKeywords(m);
                    EditorUtility.SetDirty(m);
                }
                mode = next;
            }

            if (mode == 1)
                using (new EditorGUI.IndentLevelScope())
                    P(e, "_Cutoff", "Cutoff",
                        "Pixels below this alpha are discarded",
                        "このアルファ未満の画素を捨てます");

            if (mode == 2)
            {
                EditorGUILayout.HelpBox(
                    _kit.Jp
                        ? "半透明は**不透明キューの外**へ出ます。深度プリパスに載らないので "
                          + "**深度モードのリム（Rim Mode = Screen Silhouette）と SSAO が効きません**"
                          + "（リムは Fresnel モードなら効きます）。"
                          + "ZWrite が切れるためキャラ内部の前後関係も描画順任せになります ── "
                          + "髪や睫毛のように重なる部位は Render Queue で順序を作ってください。"
                        : "Transparent leaves the opaque queue. It is not in the depth prepass, "
                          + "so the depth-based rim (Rim Mode = Screen Silhouette) and SSAO stop "
                          + "working (the Fresnel rim mode still works). ZWrite is off, so parts "
                          + "that overlap inside the character sort by draw order - use the "
                          + "Render Queue to order hair and lashes.",
                    MessageType.Info);

                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_SrcBlend", "Source Blend", null, null);
                    P(e, "_DstBlend", "Destination Blend", null, null);
                    P(e, "_ZWrite", "ZWrite",
                        "Off is the usual choice for transparent",
                        "半透明では通常 Off のままにします");
                }
            }
        }

        /// <summary>マテリアルの現在値から Render Mode を読み戻す。</summary>
        private static int DetectRenderMode(Material m)
        {
            if (Fl(m, "_SurfaceTransparent") > 0.5f) return 2;
            return IsOn(m, "_AlphaClipOn") ? 1 : 0;
        }

        /// <summary>Doll の <c>DollMaterialSetup.ApplyRenderMode</c> と同じ設定内容。</summary>
        private static void ApplyRenderMode(Material m, int mode)
        {
            switch (mode)
            {
                case 1:   // カットアウト
                    m.SetFloat("_SurfaceTransparent", 0f);
                    m.SetFloat("_AlphaClipOn", 1f);
                    m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                    m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                    m.SetFloat("_ZWrite", 1f);
                    m.renderQueue = 2450;
                    m.SetOverrideTag("RenderType", "TransparentCutout");
                    break;
                case 2:   // 半透明
                    m.SetFloat("_SurfaceTransparent", 1f);
                    m.SetFloat("_AlphaClipOn", 0f);
                    m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    m.SetFloat("_ZWrite", 0f);
                    m.renderQueue = 3000;
                    m.SetOverrideTag("RenderType", "Transparent");
                    break;
                default:  // 不透明
                    m.SetFloat("_SurfaceTransparent", 0f);
                    m.SetFloat("_AlphaClipOn", 0f);
                    m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                    m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                    m.SetFloat("_ZWrite", 1f);
                    m.renderQueue = 2000;
                    m.SetOverrideTag("RenderType", "Opaque");
                    break;
            }
        }

        private void DrawBase(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("base", true, "Base", "ベース")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var baseMap = Prop("_BaseMap");
                    if (baseMap != null)
                    {
                        e.TexturePropertySingleLine(
                            Label("Base Map (RGB / Alpha)",
                                "Albedo. Alpha is used for clipping",
                                "アルベド。アルファはクリップに使用"),
                            baseMap, Prop("_BaseColor"));
                        e.TextureScaleOffsetProperty(baseMap);
                    }

                    SubHeader("Normal Map", "ノーマルマップ (凹凸)");
                    // 法線マップは割り当てた時点で ON にする。
                    // 付け忘れは「法線が効かない」という分かりにくい形で出る。
                    DrawToggleWithTexture(e, "_NormalMapOn", "_BumpMap");
                    if (IsOn("_NormalMapOn"))
                        using (new EditorGUI.IndentLevelScope())
                            P(e, "_BumpScale", "Normal Scale",
                                "Strength of the tangent-space normal",
                                "接空間ノーマルの強さ");

                    SubHeader("Detail Map", "ディテールマップ（タトゥーやチーク等）");
                    P(e, "_DetailOn", "Use Detail Map",
                        "Overlay layer with its own tiling - tattoos, blush prints, "
                        + "fabric weave. RGB = colour, A = blend amount",
                        "独立したタイリングを持つ重ねレイヤー ── タトゥー・チークの印刷・"
                        + "布地の織り目など。RGB = 色 / A = 合成率");
                    if (IsOn("_DetailOn"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_DetailMap", "Detail Map (RGB=color A=blend)", null, null);
                            P(e, "_DetailColor", "Detail Color", null, null);
                            P(e, "_DetailNormalMap", "Detail Normal Map", null, null);
                            P(e, "_DetailNormalScale", "Detail Normal Scale",
                                "Whiteout-blended on top of the base normal",
                                "ベースのノーマルの上に whiteout 合成されます");
                        }

                    SubHeader("Color Correction", "色調補正 (HSV)");
                    // テクスチャを描き直さずに色を振る。**影側の HSV とは別物** ──
                    // あちらは影になった所だけ、こちらは素の色そのもの。両方掛かる。
                    Note("Applies to the albedo itself. The shadow HSV in the Shading tab is separate and both apply.",
                        "アルベドそのものに掛かります。「陰・影」タブの影 HSV とは別物で、両方掛かります。");
                    P(e, "_AlbedoHueShift", "Albedo Hue Shift", "Rotates the hue", "色相を回します");
                    using (new EditorGUI.IndentLevelScope())
                    {
                        P(e, "_AlbedoSaturation", "Albedo Saturation", "Vividness", "鮮やかさ");
                        P(e, "_AlbedoValue", "Albedo Value", "Brightness", "明るさ");
                    }
                }
            }
        }

        private void DrawMaskMap(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("mask", true, "Mask Map", "マスクマップ")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("R = Metallic, G = Occlusion, B = Thickness, A = Smoothness.",
                        "R = Metallic / G = Occlusion / B = Thickness / A = Smoothness。");

                    P(e, "_MaskMap", "Mask Map", "Packed RGBA mask", "パック済みの RGBA マスク");
                    P(e, "_Metallic", "Metallic Scale", "Scales the R channel", "R チャンネルを倍率で調整");
                    P(e, "_Smoothness", "Smoothness Scale", "Scales the A channel", "A チャンネルを倍率で調整");
                    P(e, "_OcclusionStrength", "Occlusion Strength",
                        "How much G darkens the indirect light", "G が間接光をどれだけ落とすか");
                    P(e, "_DirectOcclusion", "Apply AO to Direct Light",
                        "Physically AO is for indirect only. Raise to taste",
                        "物理的には AO は間接光だけのもの。絵として要るときだけ上げる");
                    P(e, "_MicroShadow", "Micro Shadow",
                        "Occlusion-driven shadowing on grazing direct light",
                        "斜めから当たる直接光を遮蔽量で削る");

                }
            }
        }

        private void DrawNPRMap(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("nprmap", false, "NPR Map", "NPR マップ")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("R = Specular mask, G = Shadow offset, B = Rim mask, A = Ramp index.",
                        "R = スペキュラマスク / G = 影のオフセット / B = リムマスク / A = ランプ番号。");

                    DrawToggleWithTexture(e, "_NPRMapOn", "_NPRMap");
                    if (IsOn("_NPRMapOn"))
                        using (new EditorGUI.IndentLevelScope())
                            P(e, "_NPRShadowOffsetStrength", "Shadow Offset Strength",
                                "How far G pushes the shadow boundary",
                                "G が影の境界をどれだけずらすか");
                }
            }
        }

        private void DrawEmission(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("emission", true, "Emission", "発光")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawToggleWithTexture(e, "_EmissionOn", "_EmissionMap");
                    if (IsOn("_EmissionOn"))
                        using (new EditorGUI.IndentLevelScope())
                            P(e, "_EmissionColor", "Emission Color", "HDR", "HDR");
                }
            }
        }

        // ================================================================
        //  タブ 1: 陰・影
        // ================================================================
        private void DrawTabShading(MaterialEditor e)
        {
            DrawDiffuseTransfer(e);
            DrawShadowColor(e);
            DrawTerminator(e);
            DrawRamp(e);
            DrawHQShadow(e);

            // 顔 SDF は「陰の境界をどう決めるか」の機能であって質感ではないので、
            // 陰・影タブに置く（Doll の「顔（SDF / 陰補正）」と同じ棚。T-347）。
            var type = (ToonSurfaceType)Mathf.RoundToInt(GetFloat("_SurfaceType"));
            if (type == ToonSurfaceType.Face) DrawFace(e);
        }

        private void DrawDiffuseTransfer(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("diffuse", true, "Diffuse Transfer", "拡散の伝達関数")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("The only stylized part of the BRDF. Specular stays physically based.",
                        "BRDF のうち様式化しているのはここだけ。鏡面反射は物理ベースのまま。");

                    P(e, "_ShadowThreshold", "Shadow Threshold",
                        "Where the light-to-shadow transition sits",
                        "明暗の境界の位置");
                    // **2 つのスライダの組み合わせでしか起きない破綻がある。**
                    // Wrap を上げると rawT の上限が 1/(1+wrap) まで下がるので、
                    // 閾値がそこを超えると光を当てても全面が影のままになる。
                    // 絵は「暗い」ではなく「影色で塗られたまま動かない」ので、
                    // ライト側を疑い続けることになる。ここで先に言う。
                    // 依存する Softness / Wrap はこの下にあるが、ドラッグ中は
                    // LayoutFrozen が前回の箱を保つのでスライダーはずれない（T-377）。
                    WarnDiffuseReach();
                    P(e, "_ShadowSoftness", "Base Softness",
                        "Width of the transition before curvature widens it",
                        "曲率で広げる前の、境界の基本の幅");
                    // 曲率の供給源は焼いたマップだけ（T-381）。画面微分の推定は
                    // 三角形ごとに一定で陰に面が並ぶため撤去した。
                    P(e, "_CurvatureSoftness", "Curvature Influence",
                        "How much curved areas widen the transition: "
                        + "width = Base Softness x (1 + curvature x Influence). "
                        + "Curvature comes from the baked Curvature Map (Effects tab > Baked Maps); "
                        + "without one this does nothing",
                        "曲がった面ほど境界を広げる度合い。"
                        + "幅 = Base Softness × (1 + 曲率 × Influence)。"
                        + "曲率は焼いた Curvature Map（質感タブ > ベイクしたマップ）から取ります ── "
                        + "無ければ何も起きません");

                    SubHeader("Shade Normal", "シェーディング法線");
                    P(e, "_ShadeNormalMap", "Shade Normal Map",
                        "A smoother normal used only for the diffuse transfer",
                        "拡散の伝達だけに使う、なめらかな法線");
                    P(e, "_ShadeNormalStrength", "Shade Normal Strength", null, null);
                    P(e, "_DiffuseWrap", "Diffuse Wrap",
                        "Wraps light past the terminator. Energy-conserving, so the transfer "
                        + "tops out at 1/(1+wrap) - raising it lowers the ceiling",
                        "光を明暗境界の先まで回り込ませます。エネルギー保存形なので"
                        + "伝達の上限が 1/(1+wrap) まで下がります（上げるほど天井が下がる）");

                    SubHeader("Realtime Shadow", "リアルタイム影の受け");
                    P(e, "_ReceiveShadowStrength", "Receive Realtime Shadow",
                        "Applied once, at the end. HQ shadow and micro shadow are folded in "
                        + "here, so lowering it fades both together",
                        "最後に一度だけ掛かります。HQ 影とマイクロシャドウが"
                        + "ここに畳まれているので、下げるとまとめて薄くなります");
                    P(e, "_ShadowAttenSoftness", "Realtime Shadow Softness",
                        "Width of the transition, centred on half-occluded. "
                        + "The centre does not move, so this changes softness and not shadow size",
                        "遷移の幅。中心は「半分遮蔽」に固定なので、"
                        + "影の大きさは変わらず柔らかさだけが変わります");
                    P(e, "_ShadowEdgeAA", "Edge Anti-Aliasing",
                        "Widens the boundary by one pixel to hide stair-stepping",
                        "境界を 1 画素ぶん広げてジャギを隠します");
                }
            }
        }

        private void DrawShadowColor(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("shadowcolor", true, "Shadow Color (HSV)", "影の色 (HSV)")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Reference art rotates the hue and raises saturation in shadow, "
                        + "instead of only darkening it.",
                        "参考にしている絵では、影は暗くなるだけでなく色相が回り彩度が上がっている。");

                    P(e, "_ShadowHueShift", "Hue Shift",
                        "Rotates the shadow hue. Together with Saturation at 1 the whole "
                        + "HSV conversion is skipped, so leaving both at default costs nothing",
                        "影の色相を回します。Saturation が 1 のまま両方とも既定なら"
                        + "HSV 変換ごと飛ぶので、触らなければコストはゼロです");
                    P(e, "_ShadowSaturation", "Saturation Scale",
                        "Reference art raises saturation in shadow rather than only darkening it",
                        "参考にしている絵では、影は暗くなるだけでなく彩度が上がります");
                    P(e, "_ShadowValue", "Value Scale",
                        "Lower for deeper shadows. Ambient in the Lighting tab also lifts them",
                        "下げると影が濃くなります。「ライト」タブの環境光も影を持ち上げます");
                    P(e, "_AddLightShadowColor", "Shadow Color from Add. Lights",
                        "How much of the shadow colouring additional lights get. "
                        + "Full strength on every point light usually reads as dirty",
                        "追加光源の影にどれだけ影色を掛けるか。"
                        + "点光源すべてに全量掛けると濁って見えがちです");
                    P(e, "_ShadowTint", "Tint (multiply)",
                        "Multiplied onto the shadow after the HSV step",
                        "HSV の後に影へ乗算されます");
                    P(e, "_ShadowColor", "Shadow Hue (mix toward)",
                        "A hue to pull the shadow toward. Its brightness is normalised away, "
                        + "so only the hue is taken - picking a dark colour does not darken",
                        "影を寄せたい色相。明るさは正規化して落とすので**色相だけ**が効きます"
                        + "（暗い色を選んでも暗くはなりません）");
                    P(e, "_ShadowColorMix", "Hue Mix",
                        "0 skips this step entirely", "0 でこの処理ごと飛びます");

                    SubHeader("Cast Shadow Color", "落ち影の色");
                    Note("Colour only. Whether a shadow lands at all is Receive Realtime Shadow "
                        + "(Diffuse Transfer). Keep these equal across skin materials "
                        + "(face / neck / body) or the seam shows as a colour step.",
                        "**色の話だけ**です。影が落ちるかどうかは「拡散の伝達関数 > Receive "
                        + "Realtime Shadow」が決めます。肌が続くマテリアル（顔・首・体）では"
                        + "値を揃えないと、境目で影の色に段差が出ます。");
                    P(e, "_CastShadowColor", "Cast Shadow Color",
                        "Tint applied only to shadows cast from the shadow map (hair, hands) - "
                        + "the NdotL shade keeps the normal shadow colour",
                        "シャドウマップ由来の落ち影（髪・手）だけに掛ける色。"
                        + "NdotL の陰は通常の影色のままです");
                    P(e, "_CastShadowColorStrength", "Cast Shadow Color Strength",
                        "How much of the tint to apply. 0 = cast shadows use the normal shadow "
                        + "colour. Also lowers the specular floor and ambient inside the cast "
                        + "shadow so it reads as a separate event from the NdotL shade",
                        "色をどれだけ掛けるか。0 で落ち影も通常の影色。"
                        + "影の中の鏡面と環境光も同時に落とすので、落ち影が"
                        + "NdotL の陰とは別の出来事として見えます");
                }
            }
        }

        private void DrawTerminator(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("terminator", false, "Terminator", "Terminator（明暗境界）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_TerminatorColor", "Terminator Color",
                        "The warm band right at the light-shadow boundary",
                        "明暗の境目に出る暖色の帯");
                    P(e, "_TerminatorStrength", "Strength",
                        "Applies to every surface type, not just skin. Keeping the boundary "
                        + "colour consistent across the body is a deliberate stylisation",
                        "肌だけでなく全部の質感に掛かります。境界の色を全身で揃えるのは"
                        + "意図的な様式化です");
                    P(e, "_TerminatorSharpness", "Sharpness",
                        "How fast the band falls off from its centre",
                        "帯の芯からどれだけ速く落ちるか");
                    P(e, "_TerminatorFadeStart", "Fade Start (m)",
                        "Distance where the band starts to fade out",
                        "帯が消え始める距離（メートル）");
                    P(e, "_TerminatorFadeEnd", "Fade End (m)", null, null);
                }
            }
        }

        private void DrawRamp(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("ramp", false, "Ramp Override", "ランプで上書き")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawToggleWithTexture(e, "_UseRampMap", "_RampMap");
                    if (IsOn("_UseRampMap"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_RampRowCount", "Ramp Row Count",
                                "How many ramps are stacked vertically in the texture",
                                "テクスチャに縦へ何本のランプを並べてあるか");
                            P(e, "_RampIndexOverride", "Ramp Index Override (-1 = use NPR.a)",
                                "-1 picks the row per-pixel from the A channel of the NPR Map",
                                "-1 で NPR マップの A から画素ごとに行を選びます");
                            P(e, "_RampStrength", "Blend",
                                "Blends between the curvature-driven step and the ramp. "
                                + "The ramp is never mandatory",
                                "曲率駆動のステップとランプの間を混ぜます。ランプは必須ではありません");
                        }
                }
            }
        }

        private void DrawHQShadow(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("hqshadow", false, "HQ Self Shadow", "HQ セルフシャドウ")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Pv(e, "_HQShadowOn", "Enable HQ Self Shadow",
                        "Main light only. Costs the most texture fetches of any feature",
                        "主光源のみ。全機能の中でテクスチャフェッチが一番多い");

                    if (IsOn("_HQShadowOn"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            Note("Tune the URP Asset shadow resolution and cascades together with this.",
                                "URP Asset のシャドウ解像度とカスケードも合わせて詰めること。");

                            P(e, "_HQShadowSoftness", "Penumbra (texels)",
                                "In shadow-map texels, not metres",
                                "単位はシャドウマップのテクセル。メートルではありません");
                            P(e, "_ShadowPenumbraScale", "Penumbra Scale", null, null);
                            P(e, "_ReceiverNormalBias", "Receiver Normal Bias", null, null);
                            P(e, "_ShadowContactHardening", "Contact Hardening (PCSS)",
                                "Narrows the penumbra where the caster is close",
                                "遮蔽物が近いところで半影を狭めます");
                        }
                }
            }
        }

        // ================================================================
        // Doll の Effects タブと同じ構成（T-352）:
        //   肌の質感（散乱・透過。部位ゲート）→ 縁の質感（リム）→ コートとグリッター。
        // リム・コート・グリッターは部位を選ばないので常に出る＝空のタブにならない。
        private void DrawTabEffects(MaterialEditor e)
        {
            var type = (ToonSurfaceType)Mathf.RoundToInt(GetFloat("_SurfaceType"));

            if (type == ToonSurfaceType.Skin) DrawSubsurface(e);
            // 透過は肌と布の両方が使う（ToonPBRCommon.hlsl の SKIN || CLOTH 分岐）。
            if (type == ToonSurfaceType.Skin || type == ToonSurfaceType.Cloth) DrawTransmission(e);
            // シアー生地は**肌と布の両方**に出す。ストッキングは肌の上に乗るので、
            // 脚のメッシュを Skin のまま使う組み方もあれば、布として独立させる組み方もある。
            // HLSL 側はタイプで分岐していない。
            if (type == ToonSurfaceType.Skin || type == ToonSurfaceType.Cloth) DrawStocking(e);

            DrawRim(e);
            DrawCoatAndGlitter(e);
            DrawBakedMaps(e);
        }

        /// <summary>
        /// 焼いたマップのうち「部位を選ばない面の質感」だけを 1 箇所に集める
        /// （Doll の「ベイクマップ」と同じ棚。T-359）。以前は Cavity=基本タブ /
        /// 曲率=陰・影タブ / ベントノーマル=ライトタブと 3 タブに散っており、
        /// **焼いた後どこを見ればいいのかが分からなかった**。
        ///
        /// 部位や機能に固有のマップ（陰用ノーマル・顔 SDF・毛流れ・SSS）は
        /// Doll と同じくそれぞれの機能のセクションに残す ── そちらは
        /// 「その機能を設定しに行った先にある」方が探しやすい。
        /// </summary>
        private void DrawBakedMaps(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("bakedmaps", true, "Baked Maps", "ベイクマップ（Bent / Cavity / 曲率）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Bake these from the Baking tab; it assigns them here automatically. "
                        + "Occlusion is not here - it lives in the G channel of the Mask Map "
                        + "(Base tab).",
                        "Baking タブで焼くと自動でここへ入ります。"
                        + "**遮蔽（AO）だけはここにありません** ── "
                        + "Mask Map の G チャンネル（基本タブ）に入ります。");

                    SubHeader("Bent Normal", "ベント法線マップ");
                    P(e, "_BentNormalOn", "Use Bent Normal",
                        "Aims the indirect diffuse away from occluded directions",
                        "間接拡散の向きを、遮蔽されていない方向へ寄せます");
                    P(e, "_BentNormalMap", "Bent Normal Map", null, null);

                    SubHeader("Cavity", "キャビティ（くぼみの微細遮蔽）");
                    P(e, "_CavityMap", "Cavity Map (R)", "Fine crevices", "細かい窪み");
                    P(e, "_CavityStrength", "Cavity Strength", null, null);

                    SubHeader("Curvature", "曲率マップ");
                    P(e, "_CurvatureMap", "Curvature Map (R)",
                        "Baked curvature (0.5 = flat). The only curvature source - "
                        + "Curvature Influence (Shading tab) reads it to widen the transition "
                        + "on curved areas. Continuous across triangles, so no facets",
                        "焼いた曲率（0.5 = 平坦）。曲率の唯一の供給源で、"
                        + "Curvature Influence（陰・影タブ）がこれを読んで曲がった面の境界を広げます。"
                        + "三角形をまたいで連続なので面は出ません");
                }
            }
        }

        private void DrawSubsurface(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("sss", true, "SSS (Subsurface)", "SSS（表面下散乱）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_SubsurfaceColor", "Subsurface Color",
                        "Bleeds into the shadow side of the transition",
                        "境界の影側へにじむ色");
                    P(e, "_SubsurfaceStrength", "Strength", null, null);
                }
            }
        }

        private void DrawTransmission(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("transmission", true, "Transmission", "透過")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("The B channel of the Mask Map is the thickness. Paint ears and thin cloth white.",
                        "MaskMap の B（Thickness）が透過量。耳や薄い布を白く塗ります。");

                    P(e, "_TransmissionColor", "Transmission Color",
                        "Multiplied by the albedo, so it tints rather than replaces",
                        "アルベドに乗算されるので、置き換えではなく色付けになります");
                    P(e, "_TransmissionPower", "Power",
                        "How tightly the glow hugs the direction straight through the surface. "
                        + "Higher means you only see it looking almost into the light",
                        "光が抜けてくる向きにどれだけ絞るか。上げるほど"
                        + "ほぼ光源を覗き込む角度でしか見えなくなります");
                    P(e, "_TransmissionStrength", "Strength", null, null);
                    P(e, "_TransmissionDistortion", "Distortion",
                        "Bends the through-light by the SSS direction. If it cancels the light "
                        + "vector out, the raw light direction is used instead of a NaN",
                        "抜ける光を SSS の向きへ曲げます。ライトベクトルを打ち消したときは"
                        + "NaN にせず生のライト方向へ落とします");
                    P(e, "_SSSMap", "SSS Map (RGB=dir A=thickness)",
                        "Baked scatter direction. Without it the shading normal is used",
                        "焼いた散乱方向。無い場合はシェーディング法線を使います");
                    P(e, "_SSSMapStrength", "SSS Map Strength", null, null);
                }
            }
        }

        private void DrawSheen(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("sheen", true, "Sheen (Cloth)", "Sheen（布の光沢）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Energy Conservation 1 shrinks the base layer by what the sheen reflects "
                        + "(same as glTF KHR_materials_sheen). 0 just adds.",
                        "Energy Conservation を 1 にすると、sheen が反射するぶん下地を縮めます"
                        + "（glTF KHR_materials_sheen と同じ）。0 は従来どおり足すだけ。");

                    P(e, "_SheenColor", "Sheen Color",
                        "Velvet / satin rim sheen. Charlie distribution, not a second GGX lobe",
                        "ベルベットやサテンの縁の光沢。Charlie 分布で、GGX の 2 本目ではありません");
                    P(e, "_SheenRoughness", "Sheen Roughness",
                        "Independent of the base roughness. Lower makes a tighter rim",
                        "下地の粗さとは独立です。下げるほど縁が細くなります");
                    P(e, "_SheenIntensity", "Intensity", null, null);
                    P(e, "_SheenEnergyConservation", "Energy Conservation",
                        "Shrinks the base by the sheen's directional albedo before adding. "
                        + "At 0 it just adds, which can exceed the incoming light at the rim",
                        "sheen の指向性アルベドぶん下地を縮めてから足します。"
                        + "0 は足すだけなので、縁で入射より多く返ることがあります");
                    P(e, "_ClothAnisotropy", "Anisotropy",
                        "Stretches the sheen along the weave direction",
                        "織りの方向へ光沢を伸ばします");
                    P(e, "_ClothTangentSwap", "Use Bitangent as Weave Dir",
                        "Flip when the sheen runs across the weave instead of along it",
                        "光沢が織りと直交して出るときに切り替えます");
                }
            }
        }

        private void DrawStocking(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("stocking", false, "Sheer Fabric", "シアー生地 (ストッキング)")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_StockingIntensity", "Stocking Intensity",
                        "0 skips the whole branch. Lays sheer cloth over skin with view-dependent "
                        + "opacity instead of a second mesh",
                        "0 で分岐ごと飛びます。布を別メッシュで重ねずに、視角依存の不透明度で"
                        + "肌の上へ乗せます");
                    if (IsPositive("_StockingIntensity"))
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Note("Use Cloth - Sheen for the gloss; the additive hem gloss from the "
                            + "source shader would double up and is not included.",
                            "光沢は Cloth - Sheen（物理ベースの Charlie sheen）で出すこと。"
                            + "移植元にある加算の「すそ光沢」は二重になるので入れていません。");
                        P(e, "_StockingColor", "Stocking Color",
                            "Multiplied onto the skin when facing the camera, and used neat "
                            + "at the silhouette - so the same colour reads as sheer and as solid",
                            "正面では肌に乗算され、シルエットでは布そのものの色になります。"
                            + "同じ色が「透けた布」と「布地」の両方に見えます");
                        P(e, "_StockingMask", "Stocking Mask (R)",
                            "Where the fabric sits. Paint the thigh boundary here",
                            "布のある場所。太ももの境目はここに描きます");
                        P(e, "_StockingFrontOpacity", "Front Opacity",
                            "How much skin shows through when facing the camera",
                            "正面を向いた面でどれだけ肌が透けるか");
                        P(e, "_StockingPower", "Graze Power", null, null);
                    }
                }
            }
        }

        private void DrawHair(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("hair", true, "Anisotropic (Hair)", "異方性ハイライト（髪）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_HairAnisoGGXOn", "Use Anisotropic GGX (off = Kajiya-Kay)", null, null);
                    if (IsOn("_HairAnisoGGXOn"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            // 尺度が変わるので、切り替えた直後は必ず Intensity を触ることになる。
                            Note("GGX goes through Fresnel so it reads darker than Kajiya-Kay. Retake Intensity.",
                                "GGX は Fresnel を通すため Kajiya-Kay より暗く出ます。Intensity の取り直しが要ります。");
                            P(e, "_HairAnisotropy", "Anisotropy", null, null);
                        }

                    P(e, "_HairTangentSwap", "Use Bitangent as Strand Dir",
                        "Flip when the highlight runs along the strand instead of across it",
                        "ハイライトが毛の流れと直交して出るときに切り替えます");

                    SubHeader("Flow", "流れ");
                    P(e, "_HairShiftMap", "Shift Noise (R)",
                        "Breaks up both bands. R is read as -0.5..0.5 and scaled by 0.3, "
                        + "so it nudges the shift rather than replacing it",
                        "2 本のバンドを崩します。R を -0.5〜0.5 として読み 0.3 倍するので、"
                        + "シフトを置き換えるのではなく揺らします");
                    P(e, "_HairFlowMap", "Hair Flow (RG=dir B=conf)",
                        "Overrides the mesh tangent. Double-angle encoded (R=cos2θ, G=sin2θ) "
                        + "so mirrored UVs give the same direction. B is confidence",
                        "メッシュの接線を上書きします。倍角エンコード（R=cos2θ, G=sin2θ）なので"
                        + "UV がミラーでも同じ向きになります。B は信頼度");
                    P(e, "_HairFlowStrength", "Hair Flow Strength",
                        "0 uses the mesh tangent as-is. Raise when mirrored UVs split the angel ring",
                        "0 でメッシュの接線そのまま。UV ミラーで天使の輪が割れるときに上げます");

                    SubHeader("Highlights", "ハイライト");
                    P(e, "_HairSpecColor1", "Primary Color",
                        "The sharp inner band", "内側の細いバンド");
                    P(e, "_HairShift1", "Primary Shift",
                        "Slides the band along the normal. Negative moves it toward the roots",
                        "バンドを法線方向へずらします。負で根元側へ動きます");
                    P(e, "_HairSmoothness1", "Primary Smoothness",
                        "Width of the band. Kajiya-Kay maps it to exponent 2^(10x+1)",
                        "バンドの幅。Kajiya-Kay では指数 2^(10x+1) になります");
                    P(e, "_HairSpecColor2", "Secondary Color",
                        "The wide outer band. This is the one that gets the strand grain",
                        "外側の広いバンド。束感が乗るのはこちらだけです");
                    P(e, "_HairShift2", "Secondary Shift", null, null);
                    P(e, "_HairSmoothness2", "Secondary Smoothness", null, null);
                    P(e, "_HairSpecIntensity", "Intensity",
                        "Scales both bands. Hair does not use the shared Specular Intensity",
                        "2 本まとめて倍率を掛けます。髪は共通の Specular Intensity を通りません");
                    P(e, "_HairStrandScale", "Strand Scale",
                        "Frequency of the strand grain along U (3 stacked sines). "
                        + "Fades out on its own once one period drops under a pixel",
                        "束の粒の U 方向の細かさ（3 オクターブのサイン）。"
                        + "画面上で 1 周期が 1 画素を切ると自動で効かなくなります");
                    P(e, "_HairStrandSparkle", "Strand Sparkle",
                        "How much the grain cuts into the secondary band. "
                        + "The primary band is left alone - it is a thin core and would vanish",
                        "粒が副バンドをどれだけ削るか。主バンドには掛かりません"
                        + "（細い芯なので割ると消えるため）");
                }
            }
        }

        private void DrawFace(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("face", true, "Face (SDF)", "顔（SDF）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    // _HeadForward / _HeadRight はスクリプトから供給される前提のプロパティ。
                    // **「破綻する」は誤り。** シェーダーは段階的に劣化する設計で、
                    // `faceBlend = _FaceFlatness * max(bound, _FaceUseObjectAxis)` なので
                    // 最悪でも通常の陰影に落ちる。実際の 3 状態を書く（T-282）。
                    const string kNl = "\n";
                    EditorGUILayout.HelpBox(
                        _kit.Jp
                            ? "顔の影境界は頭ボーンの向きに依存します。供給元で挙動が変わります:" + kNl
                              + "・Binder あり … 頭の回転に追従（本来の姿）" + kNl
                              + "・無し ＋ Fallback to Object Axis ON … オブジェクトの軸で代用。"
                              + "立ちポーズなら成立しますが頭の回転には追従しません" + kNl
                              + "・無し ＋ Fallback も OFF … SDF は使われず通常の陰影に落ちます"
                              + "（壊れはしません）" + kNl
                              + "首を振る演出では FaceDirectionBinder を付けること。SETUP.md §1 を参照。"
                            : "The face shadow boundary depends on the head bone axes:" + kNl
                              + "- With a FaceDirectionBinder: follows head rotation (intended)" + kNl
                              + "- Without one, Fallback to Object Axis ON: uses the object axes. "
                              + "Fine for a standing pose, but does NOT follow head rotation" + kNl
                              + "- Without one, Fallback OFF: the SDF is not used at all; "
                              + "normal shading takes over (nothing breaks)" + kNl
                              + "Add a Binder for anything that turns its head. See SETUP.md section 1.",
                        MessageType.Info);

                    P(e, "_FaceSDFMap", "Face SDF (16-bit R*256+G)",
                        "Boundary angles swept 180 degrees from the front, packed as "
                        + "R*256+G. Bake it from the Baking tab. Requires an UNCOMPRESSED, "
                        + "sRGB-off texture - BC compression breaks the RG continuity",
                        "正面から 180 度スイープした境界の角度を R×256+G に詰めたもの。"
                        + "Baking タブで焼きます。**非圧縮・sRGB OFF 必須**"
                        + "（BC 圧縮は RG の連続性を壊します）");
                    P(e, "_FaceSDFFlipU", "Flip SDF U",
                        "Matches the left/right convention the texture was baked with",
                        "焼いたときの左右の取り決めに合わせます");
                    P(e, "_FaceShadowOffset", "Shadow Offset",
                        "Slides the whole boundary. Positive keeps the face lit further round",
                        "境界ぜんたいをずらします。正で顔がより回り込んでも明るいまま");
                    P(e, "_FaceFlatness", "SDF Blend",
                        "0 uses the normal-based transfer, 1 uses the SDF alone",
                        "0 は法線による伝達、1 は SDF だけ");
                    P(e, "_FaceSDFBlendNormalMin", "SDF Blend Normal Min",
                        "Local Y normal threshold where Face SDF influence reaches zero. Fades the SDF out on downward-facing areas like the neck or under-chin",
                        "顔の SDF の影響がゼロになるローカル Y 法線のしきい値。顎下や首など下向きの面で SDF をフェードアウトさせる");
                    P(e, "_FaceSDFBlendNormalMax", "SDF Blend Normal Max",
                        "Local Y normal threshold where Face SDF influence is fully applied. Normals between Min and Max fade smoothly",
                        "顔の SDF の影響が 100% になるローカル Y 法線のしきい値。Min と Max の間は滑らかにフェード");


                    P(e, "_FaceUseObjectAxis", "Fallback to Object Axis",
                        "Used when no binder supplies the head axes",
                        "頭ボーンの向きを供給するものが無いときの代替");
                }
            }
        }

        // ================================================================
        //  タブ 3: ライト
        // ================================================================
        private void DrawTabLighting(MaterialEditor e)
        {
            // リムは質感タブの「縁の質感」へ（Doll の Skin and Edge と同じ棚。T-352）。
            DrawLightConditioning(e);
            DrawEnvironment(e);
            DrawFillLight(e);
            DrawLightOverride(e);
            DrawAntiBlowout(e);
        }

        /// <summary>
        /// ステージ照明からキャラの可読性を守る防御層（Doll から輸入・T-350）。
        /// 入力側（ライト色の整形）と出力側（白飛び防止）で節を分けてある。
        /// </summary>
        private void DrawLightConditioning(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("lightcond", true, "Light Conditioning", "ライト色の整形")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Defends the character's colour design from the stage lighting. "
                        + "All three pass through at their defaults.",
                        "ステージ照明からキャラの色設計を守る防御層です。"
                        + "3 つとも既定値では素通しします。");

                    P(e, "_LightColorInfluence", "Light Color Influence",
                        "1 = use the light colour as is. 0 = treat every light as white "
                        + "of the same brightness, so a deep red spot no longer stains the skin",
                        "1 = ライト色をそのまま使います。0 = **同じ明るさの白色光として扱い**、"
                        + "濃い赤のスポットでも肌が真っ赤に染まりません");
                    P(e, "_LightSaturationLimit", "Light Saturation Limit",
                        "Caps the light's saturation while keeping its hue - a softer "
                        + "version of Influence",
                        "色相は保ったままライトの彩度に上限を掛けます（Influence の穏やかな版）");
                    P(e, "_LightMinBrightness", "Light Min Brightness",
                        "Floor on the light's brightness so the character stays readable "
                        + "in dark moments. 0 = off",
                        "ライトの明るさの下限。暗転寄りの演出でもキャラが見える状態を保ちます。0 で OFF");
                }
            }
        }

        private void DrawAntiBlowout(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("antiblowout", true, "Anti-Blowout", "白飛び防止")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_DiffuseLightLimit", "Diffuse Light Limit (0 = Off)",
                        "Luminance cap per light, applied to the diffuse and transmission "
                        + "only - specular still sharpens with strong lights. The NdotL "
                        + "gradient survives, so capped surfaces do not flatten out",
                        "1 灯あたりの拡散光の輝度上限。**拡散と透過にだけ**掛かります"
                        + "（鏡面は強い光ほど鋭く光るのが正しいので対象外）。"
                        + "NdotL の階調は残るので、上限に当たった面がのっぺり潰れません");
                    P(e, "_AdditionalLightBlendMode", "Additional Light Blend",
                        "Add: physical - overlapping lights blow out to white. "
                        + "Max: only the strongest light counts, so saturation survives "
                        + "(an anime-friendly lie for stages with many lights)",
                        "Add: 物理的 ── 何灯も重なると白へ飛びます。"
                        + "Max: 最も強い 1 灯だけが効くので**彩度が残ります**"
                        + "（ライトの多いステージ向けのアニメ的な嘘）");
                }
            }
        }

        private void DrawEnvironment(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("env", true, "Environment", "環境光")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Reflection probes and SH are the main path that ties the character to the background.",
                        "リフレクションプローブと SH。キャラと背景を繋ぐ主経路です。");

                    SubHeader("Ambient", "アンビエント");
                    P(e, "_AmbientIntensity", "Ambient (SH) Intensity",
                        "Raising this lifts the shadows too. Lower it first when shadows look washed out",
                        "上げると影も一緒に持ち上がります。影が浅いときはまずここを下げること");
                    P(e, "_AmbientFlatten", "Flatten",
                        "Bends the lookup direction toward straight up. "
                        + "Flattening the indirect keeps the painted-cel look",
                        "参照する向きを真上へ寄せます。間接光の方向性を潰すほど"
                        + "セル塗りの平面感が保たれます");
                    P(e, "_AOMultiBounce", "AO Multi Bounce",
                        "Tints occlusion by the albedo instead of darkening toward grey, "
                        + "so dark areas keep their hue",
                        "遮蔽を灰色へ落とさずアルベドの色で染めます。暗部が色を保ちます");
                    P(e, "_ShadowAmbientTint", "Tint in Shadow", null, null);
                    P(e, "_ShadowAmbientIntensity", "Intensity in Shadow",
                        "Ambient reaching the shadow side. Lower for deeper shadows",
                        "影の中に届く環境光。下げると影が濃くなります");

                    SubHeader("Env Specular", "環境反射（Reflection Probe）");
                    P(e, "_EnvSpecIntensity", "Env Specular Intensity",
                        "How much of the reflection probe is added. The diffuse is shrunk by "
                        + "exactly what is added here, not by the theoretical amount",
                        "リフレクションプローブをどれだけ足すか。拡散はここで実際に足した量だけ"
                        + "縮みます（理論値ではなく）");
                    P(e, "_EnvSpecFlatten", "Roughness Push",
                        "Pushes the sampled mip toward fully rough. Blurs the reflection without "
                        + "changing the material's actual roughness",
                        "参照する mip を粗い側へ寄せます。素材の粗さを変えずに映り込みだけ鈍らせます");
                }
            }
        }

        private void DrawRim(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("rim", true, "Rim / Peach Fuzz", "リムライト / Peach Fuzz")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("There is no outline pass by default, so the rim is what separates the silhouette.",
                        "既定でアウトラインを持たないので、シルエットを抜くのはリムの仕事です。");

                    // モード別に効くパラメータだけを出す（Doll の Self Shadow Mode と同じ流儀）。
                    P(e, "_RimMode", "Rim Mode",
                        "Screen Silhouette: depth-edge rim (anime backlight look). "
                        + "Fresnel PBR: the same Core formula as EasyPBR's Doll - the rim scales "
                        + "with light energy, so stage lighting colours the edge. Skips the depth read",
                        "Screen Silhouette: 深度差の縁取り（アニメ的な逆光リム）。"
                        + "Fresnel PBR: EasyPBR(Doll) と同じ Core の式。リムがライトのエネルギーに"
                        + "比例し、ステージ照明の色が縁に乗ります。深度読みを飛ばすぶん軽量");

                    bool pbrRim = IsOn("_RimMode");

                    P(e, "_RimColor", "Rim Color", null, null);
                    P(e, "_RimIntensity", "Intensity",
                        "Also scaled by the B channel of the NPR Map, so you can mask it per region",
                        "NPR マップの B でも絞られるので、部位ごとにマスクできます");
                    if (pbrRim)
                        P(e, "_RimFresnelThickness", "Fresnel Thickness",
                            "0 razor-thin (exponent 12), 1 broad (0.5). Same mapping as Doll",
                            "0 で極細（指数 12）、1 で極太（0.5）。Doll と同じ写像です");
                    if (!pbrRim) {
                    P(e, "_RimWidth", "Width",
                        "How far the depth probe reaches, in screen pixels at 1 m. "
                        + "Divided by distance, so the rim keeps its world-space thickness",
                        "深度を読みに行く距離。1m での画素数で、距離で割るので"
                        + "遠近によらず実寸の太さが保たれます");
                    P(e, "_RimThreshold", "Depth Threshold",
                        "Depth gap that counts as an edge", "縁とみなす深度の差");
                    P(e, "_RimSoftness", "Depth Softness",
                        "Lower bound only. At a silhouette the depth jumps by metres in one pixel, "
                        + "so the real width comes from the on-screen rate of change",
                        "下限としてだけ効きます。シルエットでは深度が 1 画素でメートル級に飛ぶので、"
                        + "実際の幅は画面上の変化率から決まります");
                    P(e, "_RimFresnelPower", "Fresnel Falloff",
                        "Higher values pull the rim tighter to the silhouette",
                        "上げるほどリムがシルエットへ寄って細くなります");
                    P(e, "_RimBacklightBias", "Backlight Bias",
                        "Weights the rim by how much the light faces the camera. "
                        + "This is a per-frame scalar - it does not vary across the screen",
                        "ライトがカメラを向いている度合いで重み付けします。"
                        + "画面内では一様な値です（どこでも同じ）");
                    P(e, "_RimDirectionality", "Directionality",
                        "Without this the rim appears all the way around the silhouette and "
                        + "does not move when the light moves. Cuts it to the lit side",
                        "これが無いとシルエットの全周に等しく出て、ライトを動かしても"
                        + "リムの位置が変わりません。光が回り込んだ側だけに切ります");
                    }
                    P(e, "_RimReceiveShadow", "Receive Cast Shadow",
                        "Kills the rim inside cast shadows. Uses only shadow-map "
                        + "occlusion, not the NdotL shade - the rim is about light reaching there",
                        "落ち影の中でリムを消します。見るのは落ち影だけで NdotL の陰は含みません"
                        + "（リムは「そこに光が届いているか」の話なので）");

                    if (!pbrRim)
                        P(e, "_RimDepthBlend", "Depth Blend",
                            "0 is Fresnel only (needs no depth texture), 1 gates it by the depth edge",
                            "0 でフレネルのみ（深度テクスチャ不要）、1 で深度の縁でも絞ります");

                    // Doll も同じ棚（肌と縁の質感）にリムと産毛を並べている。
                    SubHeader("Peach Fuzz", "Peach Fuzz（縁の柔らかい光沢）");
                    Note("The opposite direction to the rim: fuzz is strongest where the "
                        + "surface faces the light, because fine hairs scatter it forward. "
                        + "Skin, velvet and felt.",
                        "**リムとは向きが逆**で、面が光源を**向いている**ほど強く出ます"
                        + "（細かい毛が順光で散乱するため）。肌・ベルベット・フェルト向け。");
                    P(e, "_FuzzColor", "Peach Fuzz Color (HDR)", null, null);
                    P(e, "_FuzzIntensity", "Peach Fuzz Intensity",
                        "0 skips the whole feature (uniform branch - no variant)",
                        "0 で機能ごとスキップします（一様分岐・バリアント非増）");
                    if (IsPositive("_FuzzIntensity"))
                        using (new EditorGUI.IndentLevelScope())
                            P(e, "_FuzzPower", "Peach Fuzz Width",
                                "Lower = wider band that creeps toward the lit side. "
                                + "Higher = a thin line hugging the silhouette",
                                "小さいほど帯が広く、光の当たる側まで回り込みます。"
                                + "大きいほどシルエットに張り付いた細い線になります");
                }
            }
        }

        // 旧「陰の持ち上げ（Procedural Shadow Lift）」は T-370 で廃止し、
        // フィルライトへ置き換えた ── 用途（顔の自己陰の消去）は SDF が受け持ち、
        // 実プロジェクトでの使用は 0/46 件だった。
        private void DrawFillLight(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("fill", true, "Fill Light (Bounce)", "フィルライト（照り返し）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_FillColor", "Color (HDR)",
                        "Bounce light tint (e.g. warm from the floor, cool from the sky)",
                        "照り返しの色（床からの暖色、空からの寒色など）");
                    P(e, "_FillIntensity", "Intensity (0 = Off)",
                        "Directional bounce light poured into the shaded side (floor bounce "
                        + "is the classic use). Independent of the main light's brightness. 0 = off",
                        "陰側に注ぐ方向性のあるバウンス光（床の照り返しが典型）。"
                        + "メインライトの明るさから独立。0 で OFF");
                    if (IsPositive("_FillIntensity"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_FillPitch", "Pitch",
                                "-90 = straight up from the floor", "-90 で床から真上へ");
                            P(e, "_FillYaw", "Yaw", null, null);
                            P(e, "_FillShadeOnly", "Shade Side Only",
                                "1 limits the fill to the main light's shaded side "
                                + "(adding it to the lit side only pushes toward blowout)",
                                "1 で主光の陰側に限定します（照っている側まで足すと"
                                + "白飛び方向にしか働きません）");
                        }
                }
            }
        }

        private void DrawLightOverride(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("lightoverride", false, "Light Direction Override", "光源方向の上書き")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_LightOverrideOn", "Override Light Direction", null, null);
                    if (IsOn("_LightOverrideOn"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            EditorGUILayout.HelpBox(
                                _kit.Jp
                                    ? "背景と影の向きが意図的に食い違います。主光源にだけ効き、追加光源は素通しです。"
                                    : "The character's shading deliberately disagrees with the scene. "
                                      + "Main light only; additional lights pass through unchanged.",
                                MessageType.Warning);

                            P(e, "_LightOverrideYaw", "Yaw (deg)", null, null);
                            P(e, "_LightOverridePitch", "Pitch (deg)", null, null);
                            P(e, "_LightOverrideSpecular", "Rotate Specular Too", null, null);
                        }
                }
            }
        }

        // ================================================================
        //  タブ 4: スペキュラ
        // ================================================================
        private void DrawTabSpecular(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("specular", true, "Specular", "スペキュラ")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Physically based and never stepped: GGX, Charlie sheen and Kajiya-Kay stay as they are.",
                        "物理ベースのまま。GGX / Charlie sheen / Kajiya-Kay はステップ化しません。");

                    // ツヤ（ハイライトの締まり）を決めるのはローブ幅 = _Smoothness だが、
                    // そのダイヤルは標準 PBR の流儀どおり表面属性として Base タブ
                    // （Mask Map の A チャンネル倍率）にある。「Specular タブを触っても
                    // ツヤが出ない」という迷いが実際に起きたので、同じプロパティを
                    // ここにも再掲する（実体は 1 つ。どちらで動かしても同じ値が動く）。
                    P(e, "_Smoothness", "Smoothness Scale (= Base tab)",
                        "The gloss dial. Sets the width of the specular lobe: low = broad "
                        + "faint sheen, high = tight sparkle. Same property as "
                        + "Base > Mask Map > Smoothness Scale (scales the A channel)",
                        "**ツヤのダイヤル。**鏡面ローブの幅で、低いと広くうっすら・"
                        + "高いと締まった光沢になります。Base タブ > Mask Map > "
                        + "Smoothness Scale と同一プロパティです（A チャンネルの倍率）");
                    // 金属部だけの上書き（T-383）。Smoothness のノブは意図的に無い ──
                    // metallic を持てている時点で Mask Map を用意できているので、
                    // ツヤの描き分けは同じテクスチャの A チャンネルの仕事（利用者判断）。
                    DrawMetalOverride(e);
                    P(e, "_SpecularIntensity", "Specular Intensity",
                        "Strength only - the tightness of the highlight comes from "
                        + "Smoothness above. Hair and cloth do not go through this - "
                        + "they have their own intensity",
                        "強さだけを変えます ── ハイライトの締まり（ツヤ）は上の "
                        + "Smoothness 側です。髪と布はここを通りません"
                        + "（それぞれ自前の強度を持っています）");
                    P(e, "_SpecEnergyConservation", "Energy Conservation",
                        "Shrinks the diffuse by the fraction the specular lobe reflects "
                        + "(Fresnel x Specular Intensity, lit side only). Keeps the total "
                        + "energy from exceeding the incoming light at grazing angles. "
                        + "0 = add specular on top as before",
                        "鏡面が反射した割合（Fresnel × Specular Intensity、光の当たる面だけ）"
                        + "だけ拡散を縮めます。縁で拡散＋鏡面が入射光を超えないようにする保存則。"
                        + "0 で従来どおり鏡面を上乗せするだけ");
                    P(e, "_SpecularTint", "Specular Tint", null, null);
                    P(e, "_SpecularTintStrength", "Tint Strength", null, null);
                    P(e, "_EnergyCompensation", "Energy Compensation",
                        "Puts back the energy lost to single-scatter GGX on rough metal. "
                        + "At 1 a perfect mirror reflects exactly what came in (white furnace)",
                        "粗い金属で単散乱 GGX が失うエネルギーを戻します。"
                        + "1 のとき完全反射体は入射をちょうど全部返します（白炉試験）");

                    SubHeader("Secondary Lobe (Matte)", "Secondary Lobe（マット）");
                    P(e, "_SecSpecularIntensity", "2nd Lobe Intensity",
                        "A wide matte sheen under the sharp primary highlight - skin and "
                        + "silk read as 'lit as a surface' instead of a single dot. 0 = off",
                        "シャープな芯の下に敷く広いマットなにじみ。肌やシルクが「点」でなく"
                        + "「面」で光るようになります。0 で OFF（分岐ごとスキップ）");
                    if (IsPositive("_SecSpecularIntensity"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_SecSpecularColor", "2nd Lobe Color (HDR)", null, null);
                            P(e, "_SecSmoothness", "2nd Lobe Smoothness",
                                "Keep it well below the primary smoothness",
                                "主ローブの Smoothness よりだいぶ低くしておくのが定石です");
                        }

                    // クリアコート・虹色・グリッタは質感タブの
                    // 「コートとグリッター」へ（Doll と同じ棚。T-352）。

                    SubHeader("In Shadow / Anti-Aliasing", "影の中・アンチエイリアス");
                    P(e, "_SpecShadowFloor", "Specular in Shadow",
                        "How much specular survives on the shadow side",
                        "影側にどれだけ鏡面を残すか");
                    P(e, "_SpecAAVariance", "Spec AA Variance",
                        "Widens the lobe by the normal's screen-space variance",
                        "法線の画面上のばらつきぶん、ローブを広げます");
                    P(e, "_SpecAAThreshold", "Spec AA Threshold", null, null);
                }
            }

            // 髪の異方性ハイライトと布のシーンはどちらも鏡面ローブ（Kajiya-Kay /
            // 異方性 GGX / Charlie sheen）＝ツヤの仲間なので、このタブに集める
            // （Doll も異方性を Specular タブに置く。T-347）。表示は部位ゲート
            // （SurfaceType）のままなので、該当部位のマテリアルにしか出ない。
            var type = (ToonSurfaceType)Mathf.RoundToInt(GetFloat("_SurfaceType"));
            if (type == ToonSurfaceType.Hair)  DrawHair(e);
            if (type == ToonSurfaceType.Cloth) DrawSheen(e);

            // MatCap はビュー空間のハイライト＝スペキュラの仲間（Doll も Specular タブ）。
            // 演出タブに置いていたのは移植時の名残（T-340 で移動）。
            DrawMatCap(e);
        }

        // ================================================================
        //  タブ 5: 演出（Doll の FX タブと同じ構成・同じ順: Outline → Dissolve）
        // ================================================================
        private void DrawTabFx(MaterialEditor e)
        {
            // アウトラインは基本タブへ移動した（T-353）。演出＝時間で変化する
            // 効果（ディゾルブ）だけが残る。
            DrawDissolve(e);
        }

        /// <summary>
        /// コート層とグリッタ（Doll の「コートとグリッター」と同じ棚。T-352）。
        /// どれも下地の上へ重ねる層＝部位を選ばないので SurfaceType ではゲートしない。
        /// </summary>
        private void DrawCoatAndGlitter(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("coat_glitter", true, "Coat and Glitter", "コートとグリッター")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    SubHeader("Clearcoat", "クリアコート");
                    P(e, "_ClearcoatStrength", "Clearcoat",
                        "A second thin layer with its own roughness. IOR is fixed at 1.5 (f0 = 0.04). "
                        + "Lacquer, pearl, wet lips",
                        "別の粗さを持つ薄い層を 1 枚重ねます。IOR は 1.5 固定（f0 = 0.04）。"
                        + "漆・真珠・濡れた唇");
                    P(e, "_ClearcoatSmoothness", "Clearcoat Smoothness", null, null);

                    SubHeader("Iridescence", "イリデッセンス");
                    P(e, "_IridescenceIntensity", "Iridescence",
                        "Thin-film tint that rotates with view angle. 0 leaves it white",
                        "見る角度で色が回る薄膜のティント。0 で白（色が付かない）");
                    P(e, "_IridescenceThickness", "Iridescence Thickness",
                        "How fast the hue rotates as the surface turns away",
                        "面が傾くにつれ色相がどれだけ速く回るか");
                    P(e, "_IridescenceShift", "Iridescence Shift",
                        "Offsets the starting hue", "開始の色相をずらします");

                    SubHeader("Glitter", "グリッター");
                    P(e, "_GlitterIntensity", "Glitter Intensity",
                        "0 skips the whole feature (uniform branch - no variant, no fetch). "
                        + "Flash strength of each sequin",
                        "0 で機能ごとスキップします（一様分岐 ── バリアント非増・"
                        + "フェッチも無し）。粒のきらめきの強さです");
                    if (IsPositive("_GlitterIntensity"))
                    {
                        var tex = Prop("_GlitterMask");
                        if (tex != null)
                            e.TexturePropertySingleLine(
                                Label("Glitter Mask (R)", "Where sequins appear (white = on)",
                                      "ラメを乗せる範囲（白 = 有効）"),
                                tex, Prop("_GlitterColor"));
                        P(e, "_GlitterScale", "Density (Scale)",
                            "Cells per UV - higher packs more, smaller sequins",
                            "UV あたりのセル数。上げるほど細かく密に");
                        P(e, "_GlitterSize", "Dot Size",
                            "Radius of each sequin inside its cell",
                            "セル内の粒の半径");
                        P(e, "_GlitterTilt", "Normal Tilt Strength",
                            "Random facet tilt - stronger flashes from more angles",
                            "粒ごとの法線の傾け。強いほど色々な角度でフラッシュします");
                        P(e, "_GlitterSparsity", "Sparsity",
                            "Thins out the sequins randomly", "粒をランダムに間引きます");
                        P(e, "_GlitterIridescence", "Iridescence Amount",
                            "Rainbow tint per sequin (hologram sequins)",
                            "粒ごとの虹色（ホログラムスパンコール）");
                        P(e, "_GlitterIridescenceShift", "Iridescence Shift", null, null);
                        P(e, "_GlitterBaseReflection", "Base Reflection",
                            "Faint reflection on non-flashing sequins so the fabric "
                            + "still reads as sequined",
                            "光っていない粒にも残す薄い反射。生地がラメ物だと分かる下地です");
                    }
                }
            }
        }

        private void DrawMatCap(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("matcap", true, "MatCap", "MatCap")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_MatCapIntensity", "MatCap Intensity",
                        "0 skips the whole branch. Additive accent only (no multiply mode - it could "
                        + "overwrite the probe + SH path that ties the character to the scene). "
                        + "Leaving it up with no texture assigned adds nothing but still pays the cost",
                        "0 で分岐ごと飛びます。加算のアクセント専用（乗算は持ちません ── 環境光の"
                        + "主経路であるプローブ + SH を上書きできてしまうため）。テクスチャ未割り当てのまま"
                        + "上げておくと**加算は 0 なのにコストだけ払います**");
                    // 効いていない間は説明も詰め物も出さない（Doll の条件展開と同じ流儀）。
                    if (IsPositive("_MatCapIntensity"))
                    using (new EditorGUI.IndentLevelScope())
                    {
                        var tex = Prop("_MatCapTex");
                        if (tex != null)
                            e.TexturePropertySingleLine(
                                Label("MatCap (RGB)", "View-space lit sphere", "ビュー空間のライティング球"),
                                tex, Prop("_MatCapColor"));
                        P(e, "_MatCapLightAlign", "Align to Light",
                            "Rotates the lookup toward the on-screen light direction, so the "
                            + "highlight stops being stuck to the camera",
                            "参照の向きを画面内の光の向きへ回します。"
                            + "ハイライトがカメラに貼り付いて見える弱点が減ります");
                    }
                }
            }
        }

        private void DrawDissolve(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("dissolve", true, "Dissolve / Black Out", "ディゾルブ / 暗転")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_DissolveAmount", "Dissolve Progress",
                        "0 fully present (skips the whole branch; keywordless, so no extra variants), "
                        + "1 fully gone. Both ends are guaranteed - "
                        + "the threshold is widened by the edge width so nothing is left over",
                        "0 で全部出ています（分岐ごと飛び、キーワードレスなのでバリアントも増えません）。"
                        + "1 で全部消えます。両端は保証されています"
                        + "（縁の幅ぶん閾値を広げてあるので消え残りません）");
                    // 進行中にだけ意味のある説明は、進行中にだけ出す（0 のときは出さない）。
                    if (IsPositive("_DissolveAmount"))
                        Note("Shadow, depth and normal passes cut with the same expression, "
                            + "so dissolved parts leave no shadow behind.",
                            "**形を持つ 7 パスすべて**が同じ式で切ります ── 影・深度・法線に加え、"
                            + "髪の落ち影・速度（TAA）・輪郭も。消えた部分に何も残りません。");
                    using (new EditorGUI.IndentLevelScope())
                    {
                        P(e, "_DissolveInvert", "Invert",
                            "Flips the sign of the test, so it dissolves from the other end",
                            "判定の符号を反転します。反対の端から消えます");
                        P(e, "_DissolveType", "Axis",
                            "0 none (noise only), 1 world Y, 2 object Y. "
                            + "Object Y follows the character when it moves",
                            "0 = 使わない（ノイズだけ）/ 1 = ワールド Y / 2 = ローカル Y。"
                            + "ローカルはキャラが動いても一緒に動きます");
                        P(e, "_DissolveStartY", "Start Y",
                            "Height where the gradient is 0. Same value as End is safe "
                            + "(it means dissolve everything at once)",
                            "勾配が 0 になる高さ。End と同じ値でも安全です"
                            + "（「一気に消す」の意味になります）");
                        P(e, "_DissolveEndY", "End Y", null, null);

                        SubHeader("Noise", "ノイズ");
                        P(e, "_DissolveTex", "Noise (R)",
                            "Sampled by UV only - no triplanar. This shader assumes clean character UVs",
                            "UV だけで引きます（三平面投影はしません）。キャラの UV が整っている前提です");
                        P(e, "_DissolveNoiseScale", "Noise Scale", null, null);
                        P(e, "_DissolveNoiseStrength", "Noise Strength",
                            "How much the noise breaks up the height boundary. "
                            + "0 gives a clean horizontal line",
                            "高さの境界をノイズがどれだけ崩すか。0 で水平な直線になります");

                        SubHeader("Edge", "縁");
                        P(e, "_DissolveEdgeColor", "Edge Glow (HDR)",
                            "Added as emission on the inner part of the edge band",
                            "縁の帯の内側に発光として足されます");
                        P(e, "_DissolveEdgeColor2", "Edge Char Color (HDR)",
                            "Replaces the albedo across the whole edge band (the scorched look)",
                            "縁の帯ぜんたいでアルベドを置き換えます（焦げの表現）");
                        P(e, "_DissolveEdgeWidth", "Edge Width", null, null);
                        P(e, "_DissolveEdgeStep", "Step Edge (toon)",
                            "Quantises the edge to 2 steps and makes the glow a hard cut "
                            + "instead of a gradient",
                            "縁を 2 段に量子化し、発光もグラデーションでなく硬く切ります");
                    }

                    SubHeader("Black Out", "暗転エフェクト");
                    Note("Drive it per character with the Black Out Controller component "
                        + "(Add Component > Origuma > EasyShaderCore) - the slider here is "
                        + "for one material only. Its amount can be keyed from Timeline.",
                        "キャラ単位で動かすには **Black Out Controller** コンポーネント"
                        + "（Add Component > Origuma > EasyShaderCore）を使います"
                        + "（ここのスライダーはこのマテリアル 1 枚ぶんだけ）。"
                        + "Timeline の Animation Track から amount に直接キーを打てます。");
                    P(e, "_BlackOut", "Black Out Amount",
                        "Fades the final colour to black - emission included, and the outline "
                        + "goes with it. Alpha is untouched, so the character sinks into black "
                        + "rather than disappearing (use Dissolve to remove it)",
                        "最終色を黒へ落とします（**発光も含めて**。輪郭線も一緒に沈みます）。"
                        + "アルファは触らないので、消えるのではなく黒く沈みます"
                        + "（消したいときはディゾルブ）");
                }
            }
        }

        private void DrawOutline(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("outline", true, "Outline", "アウトライン")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Pv(e, "_OutlineOn", "Enable Outline",
                        "Back-face extrusion pass on a separate LightMode. Off by default: the "
                        + "reference art has no outlines and separates the silhouette with "
                        + "backlit rim and value contrast instead",
                        "背面法線押し出しの輪郭を別 LightMode で描きます。既定は OFF ── "
                        + "参考にしている絵には輪郭線が無く、逆光リムと明度差でシルエットを抜いています");

                    if (IsOn("_OutlineOn"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_UseSmoothNormal", "Use Baked Smooth Normal",
                                "Needs SmoothNormalBaker to have run on the mesh",
                                "SmoothNormalBaker をメッシュに通してあることが前提");
                            P(e, "_UseVertexWidth", "Width Mask from vertex color A",
                                "Lets you thin the line per-vertex (eyelashes, thin straps)",
                                "頂点ごとに線を細くできます（睫毛や細いベルトなど）");
                            P(e, "_OutlineColor", "Color", null, null);
                            P(e, "_OutlineAlbedoBlend", "Blend with Albedo",
                                "1 tints the line by the surface colour instead of one flat colour",
                                "1 で線を単色でなく表面の色で染めます");
                            P(e, "_OutlineAlbedoDarken", "Albedo Darken", null, null);
                            P(e, "_OutlineWidth", "Width", null, null);
                            P(e, "_OutlineZOffset", "Z Offset",
                                "Pushes the line away from the camera so it does not poke through",
                                "線をカメラから遠ざけて、本体を突き抜けないようにします");
                            P(e, "_OutlineMaxDistance", "Fade Distance",
                                "Where the line stops widening in screen space. "
                                + "Matters for pulled-back live shots",
                                "画面上での太りを止める距離。引きの画で効きます");

                            // Feature が無いとこのパス（LightMode = IdolOutline）は
                            // 一度も描かれない。既定 OFF の機能なので、ガードは
                            // **ON にした人にだけ**出す（OFF の人への警告は誤誘導）。
                            EditorGUILayout.Space(2);
                            FeatureSetup.DrawFeatureGuard<ToonOutlineFeature>(
                                _kit.Jp ? "ToonOutlineFeature は追加済みです。"
                                        : "ToonOutlineFeature is set up.",
                                _kit.Jp ? "ToonOutlineFeature が Renderer に追加されていません。輪郭は一度も描かれません（ForwardLit のバッチング維持のため独自 LightMode 化）。"
                                        : "ToonOutlineFeature is NOT on the active Renderer. Outlines never draw (separate LightMode keeps ForwardLit batching).",
                                _kit.Jp ? "Idol Setup を開く" : "Open Idol Setup",
                                IdolSetupWindow.Open);
                        }
                }
            }
        }

        // ================================================================
        //  タブ 6: 詳細（レンダーステート / ステンシル / デバッグ。Doll と同じ棚）
        // ================================================================
        private void DrawTabAdvanced(MaterialEditor e)
        {
            DrawRenderState(e);
            DrawStencil(e);
            DrawDebug(e);
        }

        private void DrawRenderState(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("renderstate", true, "Render State", "レンダーステート")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_Cull", "Cull",
                        "Applies to every pass, so the shadow silhouette matches the colour pass",
                        "全パスに掛かるので、影のシルエットが本体と一致します");
                    P(e, "_ZTest", "Z Test",
                        "Never makes the material completely invisible with no warning from Unity. "
                        + "Only the forward pass follows this - depth and normals stay LEqual",
                        "Never にすると Unity は何も言わずに**完全に消えます**。"
                        + "従うのは本体のパスだけで、深度と法線は LEqual のままです");
                    // 深度オフセット（lilToon の Offset Factor / Units 相当。T-348）。
                    P(e, "_OffsetFactor", "Offset Factor",
                        "Polygon depth offset (slope term). Negative pulls toward the "
                        + "camera - float brows / lashes above the face. Applied to the "
                        + "forward, see-through, depth and normals passes; not the shadow",
                        "ポリゴン深度オフセット（傾き項）。負でカメラ側に寄ります ── "
                        + "眉・睫毛を顔の上に浮かせる用途。本体・前髪透過・深度・法線の"
                        + "各パスに掛かります（影には掛かりません）");
                    P(e, "_OffsetUnits", "Offset Units",
                        "Constant term of the depth offset (in minimal depth steps)",
                        "深度オフセットの定数項（最小深度刻み単位）");


                    // **前は「専用の髪影パスが引き続き焼く」と案内していた。**
                    // そのパスは T-344 で廃止済みで、今 ON にすると影が単に消える。
                    P(e, "_ShadowCasterOff", "Exclude from Shadow Map",
                        "This material stops casting shadows entirely. Useful for eyes and "
                        + "lashes that would otherwise self-shadow the face, but hair set to "
                        + "this no longer drops any shadow onto the face or body",
                        "このマテリアルが**影を落とすのをやめます**（落ちる影が消えるだけで、"
                        + "受ける影は残ります）。顔に自己影を落とす瞳・睫毛には有効ですが、"
                        + "**髪に使うと顔にも体にも髪の影が落ちなくなります**");
                    if (IsOn("_ShadowCasterOff"))
                        Note("Shadows onto the neck and under the chin go away too. "
                            + "Paint them into the G channel of the NPR Map if you need them.",
                            "首や顎の落ち影も一緒に消えます。必要なら NPRMap の G に描くこと。");

                    EditorGUILayout.Space(4);
                    e.RenderQueueField();
                    e.EnableInstancingField();
                    // Idol は multi_compile_instancing を意図的に宣言していない
                    // （Idol.shader の ForwardLit 冒頭コメント参照）。印を入れても
                    // instanced 描画にはならず、SRP Batcher から外れるだけ。
                    var anyInstancing = false;
                    foreach (Material mat in e.targets)
                        if (mat != null && mat.enableInstancing) { anyInstancing = true; break; }
                    if (anyInstancing)
                        EditorGUILayout.HelpBox(
                            _kit.Jp ? "Idol は multi_compile_instancing を宣言していないため、Enable GPU Instancing はこのレンダラーを SRP Batcher から外すだけで得るものがありません。OFF を推奨（→ SRP_BATCHER.md）。"
                                    : "Idol does not declare multi_compile_instancing, so Enable GPU Instancing only removes this renderer from the SRP Batcher with nothing gained. Keep it OFF (see SRP_BATCHER.md).",
                            MessageType.Warning);
                    e.DoubleSidedGIField();
                }
            }
        }

        private void DrawStencil(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("stencil", false, "Stencil", "ステンシル")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    // Ref・Comp・マスク・ZTest・キューの5つが噛み合って初めて機能する。
                    // 手で入れると必ずどれかを落とすので、使う組み合わせをボタンにする。
                    Note("Ref, Comp, the masks, ZTest and the queue all have to agree. "
                        + "Use the buttons instead of setting them by hand.",
                        "Ref・Comp・マスク・ZTest・キューの5つが噛み合って初めて機能します。"
                        + "手で入れると必ずどれかを落とすのでボタンを使うこと。");

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(_kit.Jp ? "髪 (書き込む)" : "Hair (write)"))
                            ApplyStencilPreset(1, CompareFunction.Always, StencilOp.Replace, 0x0F, 0x01,
                                               CompareFunction.LessEqual, -1);

                        if (GUILayout.Button(_kit.Jp ? "瞳 (前髪を抜く)" : "Eye (punch through hair)"))
                            ApplyStencilPreset(1, CompareFunction.Equal, StencilOp.Keep, 0x01, 0x00,
                                               CompareFunction.Always, 2010);

                        if (GUILayout.Button(_kit.Jp ? "使わない" : "None"))
                            ApplyStencilPreset(0, CompareFunction.Always, StencilOp.Keep, 0x0F, 0x0F,
                                               CompareFunction.LessEqual, -1);
                    }

                    P(e, "_StencilRef", "Ref",
                        "The value written (with Replace) or compared against (with Equal)",
                        "Replace なら書き込む値、Equal なら比べる値");
                    P(e, "_StencilComp", "Comp",
                        "Never draws nothing at all, with no warning from Unity",
                        "Never にすると Unity は何も言わずに 1 画素も描きません");
                    P(e, "_StencilPass", "Pass Op",
                        "Replace with a Write Mask of 0 writes nothing - "
                        + "anything relying on it silently stops working",
                        "Replace でも Write Mask が 0 だと**何も書きません**。"
                        + "それを当てにしている材質が黙って成立しなくなります");
                    P(e, "_StencilReadMask", "Read Mask",
                        "Which bits the comparison looks at", "比較で見るビット");
                    P(e, "_StencilWriteMask", "Write Mask",
                        "Which bits may be written", "書き込んでよいビット");

                    SubHeader("Hair See-Through", "前髪透過");
                    // Feature が無いと透過パスは一度も描かれない（T-341 で Feature 化）。
                    FeatureSetup.DrawFeatureGuard<HairSeeThroughFeature>(
                        _kit.Jp ? "HairSeeThroughFeature は追加済みです。"
                                : "HairSeeThroughFeature is set up.",
                        _kit.Jp ? "HairSeeThroughFeature が Renderer に追加されていません。前髪透過は一切描かれません（SetPass 削減のため独自 LightMode 化）。"
                                : "HairSeeThroughFeature is NOT on the active Renderer. Hair see-through never draws (separate LightMode keeps SetPass low).",
                        _kit.Jp ? "Idol Setup を開く" : "Open Idol Setup",
                        IdolSetupWindow.Open);

                    // 上の「瞳 (前髪を抜く)」とは**別の方式**。あちらは瞳が不透明で手前に出る。
                    // こちらは髪が半透明で透け、下の眉・睫毛が見える。**併用しないこと。**
                    //
                    // 3 つの部位が揃って初めて成立する（眉と目がビットを書き、髪がそこを
                    // 抜いて別パスで埋める）。1 つでも欠けると絵が壊れるので、
                    // ステンシルと同じくボタンにする。
                    Note("Only works once all three of brow, eye and hair are set. "
                        + "This is a different method from Eye (punch through hair) above - do not mix them.",
                        "眉・目・髪の3つすべてに設定して初めて機能します。"
                        + "上の「瞳 (前髪を抜く)」とは別方式なので併用しないこと。");

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // 眉はビット 2、目はビット 4。髪が使うビット 1（上のプリセット）とは
                        // 別のビットなので、従来の運用のマテリアルには当たらない。
                        if (GUILayout.Button(_kit.Jp ? "眉 (bit 2 を書く)" : "Brow (write bit 2)"))
                            ApplyStencilPreset(2, CompareFunction.Always, StencilOp.Replace, 0x0F, 0x02,
                                               CompareFunction.LessEqual, 2000);

                        if (GUILayout.Button(_kit.Jp ? "目 (bit 4 を書く)" : "Eye (write bit 4)"))
                            ApplyStencilPreset(4, CompareFunction.Always, StencilOp.Replace, 0x0F, 0x04,
                                               CompareFunction.LessEqual, 2000);

                        // 髪は「眉・目が書いていない画素だけ」描く。空いた穴は
                        // HairSeeThrough パスが半透明で埋める。
                        // **Queue を眉・目より後ろにする** ── 先に書かれていないと抜けない。
                        if (GUILayout.Button(_kit.Jp ? "髪 (透過を有効化)" : "Hair (enable see-through)"))
                            ApplyHairSeeThrough();
                    }

                    P(e, "_HairSeeThroughAlpha", "See-Through Alpha",
                        "Opacity of the hair over the brow and eyes",
                        "眉・目の上にかかる髪の不透明度");
                }
            }
        }

        private void DrawDebug(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("debug", false, "Debug View", "デバッグ表示")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    Note("Curvature, occlusion and cavity cannot be judged once they are mixed into the "
                        + "final colour. Anything other than Off disables every other output.",
                        "曲率・遮蔽量・Cavity は最終色に混ざると効いているか判断できません。"
                        + "Off 以外にすると他の表示は全て無効になります。");

                    // 自前 Popup（T-375）。[Enum] ドロワーは 7 組までなので使えない。
                    // 値は ForwardPass のデバッグ分岐と対応（10 は欠番 ── 廃止したコンタクト影）。
                    var prop = Prop("_DebugMode");
                    if (prop != null)
                    {
                        int cur = Mathf.RoundToInt(prop.floatValue);
                        int idx = System.Array.IndexOf(s_DebugValues, cur);
                        if (idx < 0) idx = 0;
                        EditorGUI.BeginChangeCheck();
                        EditorGUI.showMixedValue = prop.hasMixedValue;
                        int next = EditorGUILayout.Popup(Label("Debug View", null, null), idx, s_DebugNames);
                        EditorGUI.showMixedValue = false;
                        if (EditorGUI.EndChangeCheck())
                            prop.floatValue = s_DebugValues[next];
                    }
                }
            }
        }

        private static readonly string[] s_DebugNames =
        {
            "Off", "Albedo", "Normal", "ShadeNormal", "BentNormal", "Lit", "ShadowAtten",
            "Curvature", "Occlusion", "Cavity", "Roughness", "ShadowColor", "SpecMask",
        };
        private static readonly int[] s_DebugValues =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 13, 14,   // 10 = 旧コンタクト影 / 12 = 旧材質 ID の欠番
        };

        /// <summary>
        /// 髪側の設定。**ステンシルだけで成立する** ──
        /// キーワードのトグルは持たない（T-254）。
        /// 以前はトグルで切っていたが、**ステンシルを設定しただけでは効かず**、
        /// 髪が穴を空けて誰も埋めない状態になっていた。
        /// </summary>
        /// <summary>
        /// HairSeeThrough パスの LightMode。T-341 で独自タグへ改名し、
        /// `HairSeeThroughFeature` が描く形になった（SRPDefaultUnlit 時代は
        /// [本体][透過] の交互描画で SetPass が跳ねていた）。
        /// 定数の実体はパス停止ツールと共有（書き写しは必ずずれる ── T-107）。
        /// </summary>
        private const string SeeThroughPass = ToonPBRSurfaceTypeFromName.kSeeThroughPass;

        private void ApplyHairSeeThrough()
        {
            ApplyStencilPreset(0, CompareFunction.Equal, StencilOp.Keep, 0x06, 0x00,
                               CompareFunction.LessEqual, 2010, seeThrough: true);
        }

        private void ApplyStencilPreset(int reference, CompareFunction comp, StencilOp pass,
                                        int readMask, int writeMask, CompareFunction zTest, int queue,
                                        bool seeThrough = false)
        {
            Undo.RecordObjects(_editor.targets, "Stencil Preset");

            foreach (var t in _editor.targets)
            {
                var m = t as Material;
                if (m == null) continue;

                // **透過を使う髪だけ、この重ね描きを有効にする。**
                // 他の部位で有効なままだと、絵は変わらないのに draw だけ増える。
                m.SetShaderPassEnabled(SeeThroughPass, seeThrough);

                m.SetFloat("_StencilRef", reference);
                m.SetFloat("_StencilComp", (int)comp);
                m.SetFloat("_StencilPass", (int)pass);
                m.SetFloat("_StencilReadMask", readMask);
                m.SetFloat("_StencilWriteMask", writeMask);
                m.SetFloat("_ZTest", (int)zTest);

                // -1 はシェーダー既定（Geometry）に戻す意味。
                m.renderQueue = queue;

                EditorUtility.SetDirty(m);
            }
        }

        // ================================================================
        //  キーワード同期
        // ================================================================

        /// <summary>
        /// シェーダーを差し替えたときに呼ばれる。プロパティ値からキーワードを作り直して、
        /// 手で組んだマテリアルや別シェーダーから来たマテリアルが壊れないようにする。
        /// </summary>
        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            base.AssignNewShaderToMaterial(material, oldShader, newShader);
            ApplyKeywords(material);
        }

        /// <summary>スクリプトからプロパティを触られた場合もここでキーワードが揃う。</summary>
        public override void ValidateMaterial(Material material)
        {
            ApplyKeywords(material);
        }

        private static void ApplyKeywords(Material m)
        {
            SetKeyword(m, "_ALPHATEST_ON",           IsOn(m, "_AlphaClipOn"));
            SetKeyword(m, "_HQ_SHADOW_ON",           IsOn(m, "_HQShadowOn"));
            SetKeyword(m, "_OUTLINE_ON",             IsOn(m, "_OutlineOn"));

            int type = Mathf.RoundToInt(Fl(m, "_SurfaceType"));
            SetKeyword(m, "_SURFACETYPE_DEFAULT", type == (int)ToonSurfaceType.Default);
            SetKeyword(m, "_SURFACETYPE_SKIN",    type == (int)ToonSurfaceType.Skin);
            SetKeyword(m, "_SURFACETYPE_FACE",    type == (int)ToonSurfaceType.Face);
            SetKeyword(m, "_SURFACETYPE_HAIR",    type == (int)ToonSurfaceType.Hair);
            SetKeyword(m, "_SURFACETYPE_CLOTH",   type == (int)ToonSurfaceType.Cloth);
        }

        // ValidateMaterial は他シェーダーから来たマテリアルにも呼ばれうる。
        // 存在しないプロパティを GetFloat すると警告が出るので必ず存在確認を通す。
        private static float Fl(Material m, string name)
        {
            return m.HasFloat(name) ? m.GetFloat(name) : 0f;
        }

        private static bool IsOn(Material m, string name)
        {
            return Fl(m, name) > 0.5f;
        }

        private static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else    m.DisableKeyword(keyword);
        }

        private void ApplyKeywordsToTargets()
        {
            foreach (var t in _editor.targets)
            {
                var m = t as Material;
                if (m != null) ApplyKeywords(m);
            }
        }

        // ================================================================
        //  描画プリミティブの委譲（実装と状態は ShaderGuiKit が所有）
        // ================================================================
        private bool Section(string id, bool defaultOpen, string titleEn, string titleJp,
                             string descEn = "", string descJp = "")
            => _kit.Section(id, defaultOpen, titleEn, titleJp, descEn, descJp);
        private void SubHeader(string en, string jp) => _kit.SubHeader(en, jp);
        private GUIContent Label(string label, string tipEn, string tipJp) => _kit.Label(label, tipEn, tipJp);
        private MaterialProperty Prop(string name) => _kit.Prop(name);
        private void P(MaterialEditor e, string name, string label, string tipEn, string tipJp)
            => _kit.P(e, name, label, tipEn ?? "", tipJp ?? "");
        private void Pv(MaterialEditor e, string name, string label, string tipEn, string tipJp)
            => _kit.Pv(e, name, label, tipEn ?? "", tipJp ?? "");

        /// <summary>言語に追従する補足。Kit の tooltip に載らない「節ぜんたいの理由」を書く。</summary>
        private void Note(string en, string jp)
        {
            EditorGUILayout.HelpBox(_kit.Jp ? jp : en, MessageType.None);
        }

        /// <summary>
        /// 拡散が明側に届くかを見る。**Threshold と Diffuse Wrap の組み合わせでしか
        /// 起きない破綻**なので、片方のスライダを見ているだけでは気付けない。
        ///
        /// 伝達関数は rawT = (NdotL + wrap) / (1 + wrap)² なので、
        /// NdotL = 1 でも上限は 1/(1 + wrap)。既定 wrap 0.25 で 0.80 しか出ない。
        /// 閾値 + 幅 がそこを超えると完全な明側に入らず、
        /// 閾値 − 幅 まで超えると**どんなライトでも全面が影**になる。
        ///
        /// `param_check` の check_diffuse_reach と同じ判定。**あちらは出荷前、
        /// こちらはスライダを動かしているその場**で、届く速さが違う。
        /// </summary>
        /// <summary>
        /// **機能を有効にしたのにテクスチャが無い**状態を、その場で言う。
        ///
        /// Unity は未割り当てのテクスチャに既定の単色（white / black / gray / bump）を
        /// 返す。**その色が中立とは限らない** ── 中立値がチャンネルごとに違う
        /// マップでは、白は「無変化」ではなく**特定の壊れ方**になる:
        ///
        ///   NPR マップ  白 → G = 1（基準は 0.5）→ 影が最大まで遅れて出ない
        ///   ランプ      白 → 拡散が全面明るくなり**陰影が消える**
        ///   顔 SDF      白 → 顔が常に明るいまま
        ///
        /// **例外も警告も出ない。** 絵が変わるだけなので、テクスチャの
        /// 割り当て忘れが原因だと気付ける形になっていない。
        ///
        /// テクスチャを割り当てる／外すとトグルは追従する（DrawToggleWithTexture）が、
        /// **トグルは手で直接 ON にもできる**ので、そこは埋まらない。
        /// 移行スクリプトやプリセットが値を書く経路もある。
        ///
        /// 既定が中立なものは「絵は変わらないがコストだけ払う」で、別の重さで出す。
        /// </summary>
        /// <summary>
        /// **スライダーのドラッグ中はレイアウトを変えない（T-377）。**
        /// IMGUI のスライダーは制御 ID を矩形位置から作るので、上に HelpBox が
        /// 現れて矩形がずれると、ドラッグ中のまま**別のプロパティを掴む**。
        /// 値に応じて出入りする警告は、ドラッグが終わるまで前回の表示を保つ。
        /// hotControl はマウスを押してから離すまで 0 にならない。
        /// </summary>
        private static bool LayoutFrozen => GUIUtility.hotControl != 0;

        /// <summary>
        /// 凍結中にキャッシュを使ったら、次の描画を予約しておく。
        /// マウスを離した瞬間の MouseUp パスでは、警告（スライダーより上）を描く
        /// 時点でまだ hotControl が残っているのでキャッシュのまま出る。その後
        /// 値が変わらなければ Inspector は再描画されず、**警告が出ないまま止まる**
        /// （利用者報告）。ドラッグ中は再描画が続くので予約のコストは無い。
        /// </summary>
        private void RepaintWhenThawed()
        {
            if (Event.current.type == EventType.Repaint && _editor != null)
                _editor.Repaint();
        }

        private (string broken, string wasted, string sleeping)? _missingTexCache;
        private (bool show, string text, MessageType type)? _diffuseReachCache;

        private void WarnMissingTextures()
        {
            // (ゲート, テクスチャ, 絵が壊れるか, 日本語, 英語)
            var gates = new[]
            {
                ("_NPRMapOn", "_NPRMap", true,
                 "NPR マップ ── 白が返るので **G が 1**（基準は 0.5）になり、影が最大まで遅れて出なくなります",
                 "NPR Map - white gives **G = 1** (neutral is 0.5), delaying shading to its maximum so it never appears"),
                ("_UseRampMap", "_RampMap", true,
                 "ランプマップ ── 白が返るので拡散が全面明るくなり、**陰影が消えてべた塗り**になります",
                 "Ramp Map - white makes the diffuse fully lit, so **all shading disappears**"),
                ("_StockingIntensity", "_StockingMask", true,
                 "ストッキングマスク ── 白が返るので **材質の全面**にかかります",
                 "Stocking Mask - white applies the effect **over the whole material**"),
                ("_DissolveAmount", "_DissolveTex", true,
                 "ディゾルブのノイズ ── 一様な白なので模様が出ず、**全体が一斉に消えます**",
                 "Dissolve texture - uniform white gives no pattern, so **the whole surface vanishes at once**"),
                ("_MatCapIntensity", "_MatCapTex", false,
                 "MatCap ── 既定が黒なので **加算値が 0**。絵は変わらないのにフェッチと約 26 命令を払います",
                 "MatCap - the default is black so it **adds nothing**; you pay a fetch and ~26 instructions for no change"),
                ("_CavityStrength", "_CavityMap", false,
                 "キャビティマップ ── 既定が白なので **窪みが 1（無変化）**。絵は変わらないのにフェッチを払います",
                 "Cavity Map - the default is white so it **changes nothing**; you pay a fetch for no change"),
            };

            string broken = "", wasted = "", sleeping = "";
            foreach (var (gateName, texName, breaks, jp, en) in gates)
            {
                var gate = Prop(gateName);
                var tex = Prop(texName);
                if (gate == null || tex == null) continue;
                if (gate.hasMixedValue || tex.hasMixedValue) continue;

                // **逆向きも言う。** テクスチャを割り当てたのに強度が 0 だと
                // 「入れたのに出ない」に見え、原因に辿り着けない。
                // `Tools > Idol > 絵に出ない計算を止める` は未割り当てのものを
                // 0 にするので、後からテクスチャを足すとこの状態になる
                // ── **自分で作った罠は自分で塞ぐこと。**
                if (gate.floatValue <= 0f && tex.textureValue != null)
                {
                    sleeping += "・" + gateName + "\n";
                    continue;
                }

                if (gate.floatValue <= 0f || tex.textureValue != null) continue;

                string line = "・" + (_kit.Jp ? jp : en) + "\n";
                if (breaks) broken += line;
                else wasted += line;
            }

            // 顔 SDF はトグルではなくサーフェスタイプで決まる
            var st = Prop("_SurfaceType");
            var sdf = Prop("_FaceSDFMap");
            if (st != null && sdf != null && !st.hasMixedValue && !sdf.hasMixedValue
                // KeywordEnum(Default, Skin, Face, Hair, Cloth) の 3 番目
                && Mathf.RoundToInt(st.floatValue) == 2
                && sdf.textureValue == null)
            {
                broken += "・" + (_kit.Jp
                    ? "顔 SDF ── 白が返るので **顔が常に明るいまま**になり、SDF の陰が出ません"
                    : "Face SDF - white reads as fully lit, so **the face never gets its SDF shading**") + "\n";
            }

            // ドラッグ中は前回の箱をそのまま出す（LayoutFrozen の理由を参照）
            if (LayoutFrozen && _missingTexCache.HasValue)
            {
                (broken, wasted, sleeping) = _missingTexCache.Value;
                RepaintWhenThawed();
            }
            else
                _missingTexCache = (broken, wasted, sleeping);

            if (broken.Length > 0)
                EditorGUILayout.HelpBox(
                    (_kit.Jp
                        ? "**機能が有効なのにテクスチャがありません。絵が壊れます。**\n"
                        : "**A feature is enabled but its texture is missing. The image is wrong.**\n")
                    + broken.TrimEnd(), MessageType.Warning);

            if (wasted.Length > 0)
                EditorGUILayout.HelpBox(
                    (_kit.Jp
                        ? "**効果が無いのにコストだけ払っています。**\n"
                        : "**Paying the cost for no effect.**\n")
                    + wasted.TrimEnd(), MessageType.Info);

            if (sleeping.Length > 0)
                EditorGUILayout.HelpBox(
                    (_kit.Jp
                        ? "**テクスチャはありますが強度が 0 なので出ません。**\n"
                        : "**The texture is assigned but the strength is 0, so nothing shows.**\n")
                    + sleeping.TrimEnd(), MessageType.Info);
        }

        /// <summary>
        /// 金属部（Mask Map R × Metallic）だけの鏡面倍率。Metallic 0 の材質では
        /// 何にも効かないので、セクションごと出さない（値で出入りするのは
        /// SubHeader 以下のまとまりだが、スライダーより上には現れないので
        /// T-377 の制御 ID ずれは起きない）。
        /// </summary>
        private void DrawMetalOverride(MaterialEditor e)
        {
            if (!IsPositive("_Metallic")) return;
            SubHeader("Metal Override", "Metal Override（金属部の上書き）");
            P(e, "_MetalSpecularBoost", "Metal Specular Boost",
                "Multiplies Specular Intensity on metallic areas only "
                + "(Mask Map R x Metallic). 1 = same as before. Lets you tame skin "
                + "highlights without killing metal accents, and vice versa",
                "金属部（Mask Map R × Metallic）だけ Specular Intensity に掛かる倍率。"
                + "1 で従来どおり。肌のハイライトを絞っても金具を殺さない（逆も）ための分離です");
            P(e, "_MetalEnvBoost", "Metal Env Boost",
                "Multiplies Env Specular Intensity on metallic areas only. "
                + "Metal reads mostly from reflections, so raise this when metal "
                + "looks dead in dim stages. Does not affect the clearcoat layer",
                "金属部だけ Env Specular Intensity に掛かる倍率。金属の見た目はほぼ映り込みで"
                + "決まるので、暗いステージで金具が死ぬときに上げます。クリアコート層には掛かりません");
            EditorGUILayout.Space(2);
        }

        private void WarnDiffuseReach()
        {
            // ドラッグ中は前回の箱をそのまま出す（LayoutFrozen の理由を参照）
            if (LayoutFrozen && _diffuseReachCache.HasValue)
            {
                var c = _diffuseReachCache.Value;
                if (c.show) EditorGUILayout.HelpBox(c.text, c.type);
                RepaintWhenThawed();
                return;
            }
            _diffuseReachCache = (false, "", MessageType.None);

            var th = Prop("_ShadowThreshold");
            var wrap = Prop("_DiffuseWrap");
            var soft = Prop("_ShadowSoftness");
            if (th == null || wrap == null || soft == null) return;
            // 複数選択で値が混ざっているときは判定できない
            if (th.hasMixedValue || wrap.hasMixedValue || soft.hasMixedValue) return;

            float cap = 1f / (1f + wrap.floatValue);
            if (cap >= th.floatValue + soft.floatValue) return;

            bool dead = cap <= th.floatValue - soft.floatValue;
            string jp = dead
                ? $"**光を当てても全面が影のまま。** Diffuse Wrap {wrap.floatValue:0.##} だと "
                  + $"NdotL が 1 でも {cap:0.###} までしか届かないが、明側に入るには "
                  + $"{th.floatValue - soft.floatValue:0.###} が要る。"
                  + $"Threshold を {cap - soft.floatValue:0.##} 以下にするか Wrap を下げること。"
                : $"最も明るい面でも完全には明るくならない（Wrap {wrap.floatValue:0.##} で上限 "
                  + $"{cap:0.###}、完全な明側には {th.floatValue + soft.floatValue:0.###} が要る）。"
                  + "全体が薄く曇る。";
            string en = dead
                ? $"Nothing will ever be lit. With Diffuse Wrap {wrap.floatValue:0.##} the transfer "
                  + $"tops out at {cap:0.###} even at NdotL = 1, but reaching the lit side needs "
                  + $"{th.floatValue - soft.floatValue:0.###}. Lower Threshold below "
                  + $"{cap - soft.floatValue:0.##}, or lower Wrap."
                : $"Even the brightest surface never becomes fully lit (Wrap {wrap.floatValue:0.##} "
                  + $"tops out at {cap:0.###}, full lit needs {th.floatValue + soft.floatValue:0.###}). "
                  + "Everything reads slightly hazy.";

            _diffuseReachCache = (true, _kit.Jp ? jp : en,
                dead ? MessageType.Error : MessageType.Warning);
            EditorGUILayout.HelpBox(_diffuseReachCache.Value.text, _diffuseReachCache.Value.type);
        }

        /// <summary>
        /// トグルとテクスチャを並べて描き、テクスチャを割り当てた／外したら
        /// トグルを追従させる。付け忘れ・外し忘れが一番多い操作なので自動化する。
        ///
        /// **キーワードは扱わない。** ここに来るトグル（法線・NPR・ランプ・発光）は
        /// どれも uniform の動的分岐で、バリアントを生まない。
        /// </summary>
        private void DrawToggleWithTexture(MaterialEditor e, string toggleName, string textureName)
        {
            var toggle = Prop(toggleName);
            if (toggle != null)
                e.ShaderProperty(toggle, toggle.displayName);

            var tex = Prop(textureName);
            if (tex == null) return;

            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.IndentLevelScope())
                e.ShaderProperty(tex, tex.displayName);
            bool changed = EditorGUI.EndChangeCheck();

            if (!changed || tex.hasMixedValue) return;

            bool on = tex.textureValue != null;
            if (toggle == null || toggle.floatValue > 0.5f == on) return;

            toggle.floatValue = on ? 1f : 0f;
        }

        /// <summary>強度スライダーが実質トグルの機能用（0 で分岐ごと飛ぶ系）。</summary>
        private bool IsPositive(string name)
        {
            var p = Prop(name);
            return p != null && p.floatValue > 0f;
        }

        private bool IsOn(string name)
        {
            var p = Prop(name);
            return p != null && p.floatValue > 0.5f;
        }

        private float GetFloat(string name)
        {
            var p = Prop(name);
            return p != null ? p.floatValue : 0f;
        }
    }
}

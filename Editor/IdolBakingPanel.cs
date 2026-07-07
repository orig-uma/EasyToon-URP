// =============================================================================
//  IdolBakingPanel.cs
// -----------------------------------------------------------------------------
//  Idol マテリアル Inspector の「Baking」セクションを描く自己完結パネル
//  （DollBakingPanel と同方式の薄い UI 層）。ベイク本体は EasyShaderCore の
//  public Baker 群（Origuma.EasyShaderCore.Editor）へ委譲する。マップの
//  プロパティ名は Baker と同名のため、自動アサインがそのまま機能する。
//
//  Idol が使うのは AO / Shade Normal / Hair Flow / Face SDF の 4 種。
//  Cavity / Curvature / SSS は Idol 未対応のため対象外（将来 Idol 側に
//  プロパティを足せば同方式で追加できる）。
//
//  ベイク成功時の自動有効化（Idol のプロパティ名に合わせ Panel 側で行う）:
//   - AO         → _OcclusionToShadow が 0 なら 0.5（影しきい値オフセットが本命）。
//                  Baker 側は同名の _OcclusionStrength(アルベド暗化) を 1 に
//                  自動設定してしまうため、ベイク前の値へ復元して二重適用を防ぐ
//   - ShadeNormal→ Baker が _ShadeNormalStrength を 1 に（同名なのでそのまま機能）
//   - HairFlow   → Baker が _HairFlowStrength を 1 に（同上）
//   - FaceSDF    → Baker の自動有効化は Doll 名（_UseFaceSDF）のため Idol では
//                  効かない。Panel が _FaceSDFEnable を 1 に設定する
// =============================================================================
using System;
using UnityEditor;
using UnityEngine;
using Origuma.EasyShaderCore.Editor;

namespace Origuma.EasyToon.URP.Editor
{
    public class IdolBakingPanel
    {
        private GameObject _bakeRoot;
        private bool _aoOpen, _shadeNormalOpen, _hairFlowOpen, _sdfOpen;

        private EasyPbrAoBaker.Settings          _aoSettings          = EasyPbrAoBaker.Default;
        private EasyPbrShadeNormalBaker.Settings _shadeNormalSettings = EasyPbrShadeNormalBaker.Default;
        private EasyPbrHairFlowBaker.Settings    _hairFlowSettings    = EasyPbrHairFlowBaker.Default;
        private EasyPbrFaceSdfBaker.Settings     _sdfSettings         = EasyPbrFaceSdfBaker.Default;

        private static readonly int[] s_BakeRes = { 512, 1024, 2048 };
        private static readonly string[] s_BakeResLabels = { "512", "1024", "2048" };

        private ShaderGuiKit _kit; // Draw 中だけ有効

        public void Draw(MaterialEditor materialEditor, ShaderGuiKit kit)
        {
            _kit = kit;
            var material = materialEditor.target as Material;
            if (material == null) return;
            bool jp = kit.Jp;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!kit.Section("baking", false, "Baking (Map Generator)", "ベイク（マップ生成）", "", ""))
                    return;

                using (new EditorGUI.IndentLevelScope())
                {
                    if (_bakeRoot == null && Selection.activeGameObject != null)
                        _bakeRoot = Selection.activeGameObject;

                    _bakeRoot = (GameObject)EditorGUILayout.ObjectField(
                        kit.Label("Source Root",
                            "Root GameObject. ALL meshes under it that use this material are baked into one texture. Auto-filled from the Hierarchy selection",
                            "Root の GameObject。配下でこのマテリアルを使う全メッシュを1枚に焼く。Hierarchy の選択から自動補完"),
                        _bakeRoot, typeof(GameObject), true);

                    if (_bakeRoot == null)
                        EditorGUILayout.HelpBox(
                            jp ? "Source Root を指定してください（Hierarchy でキャラを選択すると自動で入ります）。"
                               : "Assign a Source Root (selecting the character in the Hierarchy auto-fills it).",
                            MessageType.Info);

                    int matCount = materialEditor.targets.Length;
                    if (matCount > 1)
                        EditorGUILayout.HelpBox(
                            jp ? $"{matCount} 個のマテリアルを選択中。ベイクは選択中の全マテリアルに対して実行されます。"
                               : $"{matCount} materials selected. Baking runs for ALL of them.",
                            MessageType.Info);

                    using (new EditorGUI.DisabledScope(_bakeRoot == null))
                    {
                        // --- Ambient Occlusion（→ 影しきい値オフセット）---
                        _aoOpen = EditorGUILayout.Foldout(_aoOpen,
                            jp ? "Ambient Occlusion（→ Occlusion Map・影しきい値オフセット）"
                               : "Ambient Occlusion (→ Occlusion Map, shadow threshold offset)", true);
                        if (_aoOpen)
                            using (new EditorGUI.IndentLevelScope())
                            {
                                _aoSettings.resolution     = ResField(_aoSettings.resolution);
                                _aoSettings.rayCount       = EditorGUILayout.IntSlider(kit.Label("Samples", "Rays per vertex", "頂点あたりのレイ数"), _aoSettings.rayCount, 16, 256);
                                _aoSettings.maxDistance    = EditorGUILayout.Slider(kit.Label("Max Distance", "Occlusion reach (m). Smaller = local cavity", "遮蔽の届く距離(m)。小さいほど局所的"), _aoSettings.maxDistance, 0.02f, 3.0f);
                                _aoSettings.intensity      = EditorGUILayout.Slider(kit.Label("Intensity", "AO strength", "AO の強さ"), _aoSettings.intensity, 0.1f, 2.0f);
                                _aoSettings.enclosedCutoff = EditorGUILayout.Slider(kit.Label("Ignore Enclosed", "Snap near-fully-occluded faces to white", "ほぼ完全遮蔽の面を白へ（黒つぶれ除去）"), _aoSettings.enclosedCutoff, 0.5f, 1.0f);
                                _aoSettings.floor          = EditorGUILayout.Slider(kit.Label("Floor", "Lift dark areas", "暗部の下限"), _aoSettings.floor, 0.0f, 0.5f);
                                _aoSettings.smooth         = EditorGUILayout.IntSlider(kit.Label("Smooth", "Reduce facets", "ファセット低減"), _aoSettings.smooth, 0, 8);
                                _aoSettings.blur           = EditorGUILayout.IntSlider(kit.Label("Blur", "Texture blur", "ブラー"), _aoSettings.blur, 0, 4);
                                if (BakeButton(jp ? "AO をベイク" : "Bake AO"))
                                    BakeAllTargets(materialEditor, BakeAo);
                                EditorGUILayout.HelpBox(
                                    jp ? "Idol では AO を「影になりやすさ」（陰しきい値の局所オフセット）として使う。ベイク成功で AO To Shadow Threshold を 0.5 に自動設定（0 のときのみ）。アルベド暗化（Occlusion Strength）は変更しない。"
                                       : "Idol uses AO as 'shadow-proneness' (local shade-threshold offset, Gakumas style). A successful bake sets AO To Shadow Threshold to 0.5 (only if 0). Albedo darkening (Occlusion Strength) is left unchanged.",
                                    MessageType.None);
                            }

                        // --- Shade Normal ---
                        _shadeNormalOpen = EditorGUILayout.Foldout(_shadeNormalOpen,
                            jp ? "Shade Normal（→ Shade Normal Map）" : "Shade Normal (→ Shade Normal Map)", true);
                        if (_shadeNormalOpen)
                            using (new EditorGUI.IndentLevelScope())
                            {
                                _shadeNormalSettings.resolution       = ResField(_shadeNormalSettings.resolution);
                                _shadeNormalSettings.smoothIterations = EditorGUILayout.IntSlider(kit.Label("Smooth Normals", "Laplacian smoothing iterations. Higher = softer, cleaner shade gradation", "ラプラシアン平滑化回数。高いほど陰のグラデーションが滑らか"), _shadeNormalSettings.smoothIterations, 0, 64);
                                _shadeNormalSettings.blur             = EditorGUILayout.IntSlider(kit.Label("Blur", "Texture blur", "ブラー"), _shadeNormalSettings.blur, 0, 4);
                                if (BakeButton(jp ? "Shade Normal をベイク" : "Bake Shade Normal"))
                                    BakeAllTargets(materialEditor, m => EasyPbrShadeNormalBaker.Bake(_bakeRoot, m, _shadeNormalSettings));
                                EditorGUILayout.HelpBox(
                                    jp ? "平滑化した法線で拡散の陰ランプだけを駆動し、陰の輪郭を一本の綺麗な曲線にする。ベイク成功で Strength が自動的に 1 に（0 のときのみ）。服・髪で特に効く。"
                                       : "Drives only the diffuse shade ramp with smoothed normals so the shade boundary reads as one clean curve. A successful bake auto-sets Strength to 1 (only if 0). Most visible on clothes and hair.",
                                    MessageType.None);
                            }

                        // --- Hair Flow ---
                        _hairFlowOpen = EditorGUILayout.Foldout(_hairFlowOpen,
                            jp ? "Hair Flow（→ Hair Flow Map・髪マテリアル）" : "Hair Flow (→ Hair Flow Map, hair material)", true);
                        if (_hairFlowOpen)
                            using (new EditorGUI.IndentLevelScope())
                            {
                                _hairFlowSettings.resolution   = ResField(_hairFlowSettings.resolution);
                                _hairFlowSettings.useCurvature = EditorGUILayout.Toggle(kit.Label("Curvature Mode", "Use min-normal-change dir (sculpted hair). Off = longest-edge (hair cards)", "最小法線変化方向（彫刻髪）。OFF=最長エッジ（カード髪）"), _hairFlowSettings.useCurvature);
                                _hairFlowSettings.smooth       = EditorGUILayout.IntSlider(kit.Label("Smooth", "Orientation smoothing", "毛流れ平滑化"), _hairFlowSettings.smooth, 0, 8);
                                _hairFlowSettings.blur         = EditorGUILayout.IntSlider(kit.Label("Blur", "Texture blur", "ブラー"), _hairFlowSettings.blur, 0, 4);
                                if (BakeButton(jp ? "Hair Flow をベイク" : "Bake Hair Flow"))
                                    BakeAllTargets(materialEditor, m => EasyPbrHairFlowBaker.Bake(_bakeRoot, m, _hairFlowSettings));
                                EditorGUILayout.HelpBox(
                                    jp ? "髪マテリアルで焼く。形状から毛流れを推定し、天使の輪をミラーUVでも安定させる（倍角エンコード）。ベイク成功で Flow Strength が自動的に 1 に（0 のときのみ）。"
                                       : "Bake on the hair material. Shape-based flow stabilizes the Angel Ring across mirrored UVs (double-angle encoded). A successful bake auto-sets Flow Strength to 1 (only if 0).",
                                    MessageType.None);
                            }

                        // --- Face SDF Shadow ---
                        _sdfOpen = EditorGUILayout.Foldout(_sdfOpen,
                            jp ? "Face SDF Shadow（→ Face SDF Map・顔マテリアル）" : "Face SDF Shadow (→ Face SDF Map, face material)", true);
                        if (_sdfOpen)
                            using (new EditorGUI.IndentLevelScope())
                            {
                                _sdfSettings.resolution    = ResField(_sdfSettings.resolution);
                                _sdfSettings.flipForward   = EditorGUILayout.Toggle(kit.Label("Flip Forward", "Enable if the face looks along -Z", "顔が-Z向きならON"), _sdfSettings.flipForward);
                                _sdfSettings.angleSteps    = EditorGUILayout.IntSlider(kit.Label("Angle Steps", "Sweep resolution", "スイープ分割数"), _sdfSettings.angleSteps, 30, 180);
                                _sdfSettings.useCastShadow = EditorGUILayout.Toggle(kit.Label("Cast Shadow", "Include nose/brow cast shadows", "鼻・眉の落ち影を含める"), _sdfSettings.useCastShadow);
                                using (new EditorGUI.DisabledScope(!_sdfSettings.useCastShadow))
                                    _sdfSettings.castDistance = EditorGUILayout.Slider(kit.Label("Cast Distance", "Cast ray length (m)", "落ち影レイ長(m)"), _sdfSettings.castDistance, 0.02f, 0.5f);
                                _sdfSettings.smooth = EditorGUILayout.IntSlider(kit.Label("Smooth", "Vertex smoothing", "頂点平滑化"), _sdfSettings.smooth, 0, 6);
                                _sdfSettings.blur   = EditorGUILayout.IntSlider(kit.Label("Blur", "Texture blur", "ブラー"), _sdfSettings.blur, 0, 4);
                                if (BakeButton(jp ? "顔 SDF をベイク" : "Bake Face SDF"))
                                    BakeAllTargets(materialEditor, BakeFaceSdf);
                                EditorGUILayout.HelpBox(
                                    jp ? "顔マテリアルで焼く。R/G/B/A=右/左/上/下の4chで焼くので左右非対称の顔もOK。ベイク成功で Enable Face SDF Shadow を自動 ON。"
                                       : "Bake on the face material. 4 channels (R/G/B/A = right/left/up/down) so asymmetric faces work. A successful bake auto-enables Face SDF Shadow.",
                                    MessageType.None);
                            }
                    }

                    EditorGUILayout.Space(2);
                    EditorGUILayout.HelpBox(
                        jp ? "生成 PNG はマテリアル隣の Baked/ に保存され、該当スロットへ自動アサインされます（非破壊・再ベイク可）。Cavity / Curvature / SSS は Idol 未対応のため対象外です。"
                           : "PNGs are saved next to the material in Baked/ and auto-assigned (non-destructive, re-bakeable). Cavity / Curvature / SSS are not supported by Idol and excluded here.",
                        MessageType.None);
                }
            }
        }

        // ------------------------------------------------------------------
        //  ベイク実行（Idol 向けの自動有効化を含む）
        // ------------------------------------------------------------------

        // AO: Baker は同名の _OcclusionStrength（アルベド暗化）を 1 に自動設定して
        //  しまうため、ベイク前の値へ復元し、Idol 本命の _OcclusionToShadow を
        //  0.5 に設定する（0 のときのみ・二重適用防止）。
        private bool BakeAo(Material m)
        {
            float prevStrength = m.HasProperty("_OcclusionStrength") ? m.GetFloat("_OcclusionStrength") : 0f;
            bool ok = EasyPbrAoBaker.Bake(_bakeRoot, m, _aoSettings);
            if (ok)
            {
                if (m.HasProperty("_OcclusionStrength"))
                    m.SetFloat("_OcclusionStrength", prevStrength);
                if (m.HasProperty("_OcclusionToShadow") && m.GetFloat("_OcclusionToShadow") <= 0f)
                    m.SetFloat("_OcclusionToShadow", 0.5f);
            }
            return ok;
        }

        // Face SDF: Baker の自動有効化は Doll 名（_UseFaceSDF）のため Idol では
        //  効かない。Panel が _FaceSDFEnable を ON にする。
        private bool BakeFaceSdf(Material m)
        {
            bool ok = EasyPbrFaceSdfBaker.Bake(_bakeRoot, m, _sdfSettings);
            if (ok && m.HasProperty("_FaceSDFEnable"))
                m.SetFloat("_FaceSDFEnable", 1f);
            return ok;
        }

        private static bool BakeButton(string label)
        {
            EditorGUILayout.Space(2);
            return GUILayout.Button(label, GUILayout.Height(24));
        }

        // 解像度ポップアップ（512 / 1024 / 2048）。
        private int ResField(int current)
        {
            int idx = Mathf.Max(0, Array.IndexOf(s_BakeRes, current));
            idx = EditorGUILayout.Popup(_kit.Label("Resolution", "Output texture size", "出力テクスチャの解像度"),
                idx, s_BakeResLabels);
            return s_BakeRes[Mathf.Clamp(idx, 0, s_BakeRes.Length - 1)];
        }

        // 選択中のマテリアル全部にベイクを実行（マルチ編集対応）。
        private void BakeAllTargets(MaterialEditor editor, Func<Material, bool> bakeOne)
        {
            int ok = 0, total = 0;
            foreach (var o in editor.targets)
            {
                if (!(o is Material m)) continue;
                total++;
                if (bakeOne(m)) ok++;
            }
            if (total > 1)
                EditorUtility.DisplayDialog("EasyToon Baker",
                    _kit.Jp ? $"{total} 個のマテリアル中 {ok} 個にベイクしました（詳細は Console）。"
                            : $"Baked {ok} of {total} selected materials (see Console).", "OK");
        }
    }
}

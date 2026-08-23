// =============================================================================
//  ToonPBRBakingPanel.cs
// -----------------------------------------------------------------------------
//  Idol マテリアル Inspector の「Baking」タブを描く自己完結パネル。
//  ベイク本体は EasyShaderCore の public Baker 群へ委譲する。
//
//  **なぜ要るか。** Idol が読むマップのうち 7 種は Core に Baker がある:
//
//      _CavityMap / _HairFlowMap / _ShadeNormalMap / _BentNormalMap
//      _CurvatureMap / _FaceSDFMap / _SSSMap
//
//  ところが Idol 側に入口が無く、文書にも書いていなかったため、
//  **導入した人は「自分で描くしかない」と思い込む**状態だった（T-277）。
//  異方性の髪・顔 SDF・曲率駆動・ベントノーマルは、
//  どれもこれらのマップがあって初めて本領を出す機能なので影響が大きい。
//
//  **プロパティ名の対応（Baker 側 → Idol 側）:**
//
//    Baker が渡すスロット   Idol   Baker の強度名        Idol の強度名
//    _CavityMap             ○      _CavityStrength       ○ 同名（自動で入る）
//    _HairFlowMap           ○      _HairFlowStrength     ○ 同名（自動で入る）
//    _ShadeNormalMap        ○      _ShadeNormalStrength  ○ 同名（自動で入る）
//    _BentNormalMap         ○      _BentNormalStrength   × → _BentNormalOn をここで立てる
//    _CurvatureMap          ○      _CurvatureStrength    × → _CurvatureSoftness をここで
//    _FaceSDFMap            ○      _UseFaceSDF           × → _FaceFlatness をここで
//    _SSSMap                ○      _SSSIntensity         × → _SSSMapStrength をここで
//    _OcclusionMap          ×      _OcclusionStrength    ○  ── AO だけ扱いが違う（下記）
//
//  **AO は自動アサインできない。** Idol は遮蔽を単体テクスチャではなく
//  `_MaskMap` の G に詰める設計なので、`_OcclusionMap` を持たない。
//  Baker 側は `HasProperty` で守られているため**保存だけされて割り当ては飛ぶ**
//  ── 壊れはしないが、焼いた画像を自分で MaskMap の G へ合成する必要がある。
//  黙って「焼けました」と出すと誤解するので、パネルで明示する。
// =============================================================================
using System;
using UnityEditor;
using UnityEngine;
using Origuma.EasyShaderCore.Editor;

namespace ToonNPR.EditorTools
{
    public class ToonPBRBakingPanel
    {
        private GameObject _bakeRoot;
        private bool _shadeNormalOpen, _hairFlowOpen, _sdfOpen, _bentOpen;
        private bool _curvatureOpen, _cavityOpen, _sssOpen, _aoOpen;

        private EasyPbrShadeNormalBaker.Settings _shadeNormal = EasyPbrShadeNormalBaker.Default;
        private EasyPbrHairFlowBaker.Settings    _hairFlow    = EasyPbrHairFlowBaker.Default;
        private EasyPbrFaceSdfBaker.Settings     _faceSdf     = EasyPbrFaceSdfBaker.Default;
        private EasyPbrBentNormalBaker.Settings  _bentNormal  = EasyPbrBentNormalBaker.Default;
        private EasyPbrCurvatureBaker.Settings   _curvature   = EasyPbrCurvatureBaker.Default;
        private EasyPbrCavityBaker.Settings      _cavity      = EasyPbrCavityBaker.Default;
        private EasyPbrSssBaker.Settings         _sss         = EasyPbrSssBaker.Default;
        private EasyPbrAoBaker.Settings          _ao          = EasyPbrAoBaker.Default;

        private static readonly int[] s_Res = { 512, 1024, 2048 };
        private static readonly string[] s_ResLabels = { "512", "1024", "2048" };

        private ShaderGuiKit _kit;   // Draw 中だけ有効

        public void Draw(MaterialEditor editor, ShaderGuiKit kit)
        {
            _kit = kit;
            if (!(editor.target is Material)) return;
            bool jp = kit.Jp;

            DrawRootField(editor, jp);

            using (new EditorGUI.DisabledScope(_bakeRoot == null))
            {
                DrawShadeNormal(editor, jp);
                DrawHairFlow(editor, jp);
                DrawFaceSdf(editor, jp);
                DrawBentNormal(editor, jp);
                DrawCurvature(editor, jp);
                DrawCavity(editor, jp);
                DrawSss(editor, jp);
                DrawAo(editor, jp);
            }
        }

        // ------------------------------------------------------------------
        //  共通の入口
        // ------------------------------------------------------------------
        private void DrawRootField(MaterialEditor editor, bool jp)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_kit.Section("bakeroot", true, "Bake Target", "ベイクの対象", "", ""))
                    return;

                using (new EditorGUI.IndentLevelScope())
                {
                    if (_bakeRoot == null && Selection.activeGameObject != null)
                        _bakeRoot = Selection.activeGameObject;

                    _bakeRoot = (GameObject)EditorGUILayout.ObjectField(
                        _kit.Label("Source Root",
                            "Root GameObject. Every mesh under it that uses this material is "
                            + "baked into one texture. Auto-filled from the Hierarchy selection",
                            "Root の GameObject。配下でこのマテリアルを使う全メッシュを 1 枚に焼きます。"
                            + "Hierarchy の選択から自動で入ります"),
                        _bakeRoot, typeof(GameObject), true);

                    if (_bakeRoot == null)
                        EditorGUILayout.HelpBox(
                            jp ? "Source Root を指定してください（Hierarchy でキャラを選ぶと自動で入ります）。"
                               : "Assign a Source Root (selecting the character in the Hierarchy auto-fills it).",
                            MessageType.Info);

                    int n = editor.targets.Length;
                    if (n > 1)
                        EditorGUILayout.HelpBox(
                            jp ? $"{n} 個のマテリアルを選択中。ベイクは全部に対して走ります。"
                               : $"{n} materials selected. Baking runs for all of them.",
                            MessageType.Info);

                    // **メッシュの読み書きが要る。** ここで言わないと
                    // 「押しても何も起きない」で終わる。
                    EditorGUILayout.HelpBox(
                        jp ? "モデルの Read/Write Enabled が必要です。"
                             + "焼いた画像は Source Root の隣に保存されます。"
                           : "The model needs Read/Write Enabled. "
                             + "Baked images are saved next to the Source Root.",
                        MessageType.None);
                }
            }
        }

        // ------------------------------------------------------------------
        //  名前がそのまま噛み合うもの（強度も Baker が入れる）
        // ------------------------------------------------------------------
        private void DrawShadeNormal(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _shadeNormalOpen, jp,
                    "Shade Normal（陰用のなめらかな法線）", "Shade Normal")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _shadeNormal.resolution = ResField(_shadeNormal.resolution);
                _shadeNormal.smoothIterations = EditorGUILayout.IntSlider(
                    _kit.Label("Smooth", "How much the normal is flattened",
                               "法線をどれだけ均すか"), _shadeNormal.smoothIterations, 0, 16);
                _shadeNormal.dilate = Dilate(_shadeNormal.dilate);
                _shadeNormal.blur = Blur(_shadeNormal.blur);

                Note(jp, "顔の陰から鼻や眉の細かい凹凸を落とすためのもの。"
                       + "_ShadeNormalStrength まで Baker が入れます。",
                        "Removes nose and brow detail from the face's shade. "
                      + "The Baker sets _ShadeNormalStrength too.");

                if (BakeButton(jp ? "Shade Normal をベイク" : "Bake Shade Normal"))
                    BakeAll(e, m => EasyPbrShadeNormalBaker.Bake(_bakeRoot, m, _shadeNormal));
            }
        }

        private void DrawHairFlow(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _hairFlowOpen, jp, "Hair Flow（毛流れ）", "Hair Flow")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _hairFlow.resolution = ResField(_hairFlow.resolution);
                _hairFlow.useCurvature = EditorGUILayout.Toggle(
                    _kit.Label("Use Curvature", "Derive the flow from curvature instead of UV",
                               "UV ではなく曲率から流れを求める"), _hairFlow.useCurvature);
                _hairFlow.dilate = Dilate(_hairFlow.dilate);
                _hairFlow.blur = Blur(_hairFlow.blur);

                Note(jp, "UV がミラーされた髪で天使の輪が割れるときに効きます"
                       + "（倍角エンコードなので向きの反転に強い）。_HairFlowStrength まで入ります。",
                        "Fixes the angel ring splitting on mirrored hair UVs "
                      + "(double-angle encoded). The Baker sets _HairFlowStrength too.");

                if (BakeButton(jp ? "Hair Flow をベイク" : "Bake Hair Flow"))
                    BakeAll(e, m => EasyPbrHairFlowBaker.Bake(_bakeRoot, m, _hairFlow));
            }
        }

        private void DrawCavity(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _cavityOpen, jp, "Cavity（窪みの微細遮蔽）", "Cavity")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _cavity.resolution = ResField(_cavity.resolution);
                _cavity.intensity = EditorGUILayout.Slider(
                    _kit.Label("Intensity", "Crevice strength", "窪みの強さ"),
                    _cavity.intensity, 0.1f, 2.0f);
                _cavity.smooth = Smooth(_cavity.smooth);
                _cavity.dilate = Dilate(_cavity.dilate);
                _cavity.blur = Blur(_cavity.blur);

                Note(jp, "_CavityStrength まで Baker が入れます。",
                        "The Baker sets _CavityStrength too.");

                if (BakeButton(jp ? "Cavity をベイク" : "Bake Cavity"))
                    BakeAll(e, m => EasyPbrCavityBaker.Bake(_bakeRoot, m, _cavity));
            }
        }

        // ------------------------------------------------------------------
        //  マップ名は合うが、強度の名前が違うもの（パネルが立てる）
        // ------------------------------------------------------------------
        private void DrawFaceSdf(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _sdfOpen, jp, "Face SDF（顔の影境界）", "Face SDF")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _faceSdf.resolution = ResField(_faceSdf.resolution);
                _faceSdf.angleSteps = EditorGUILayout.IntSlider(
                    _kit.Label("Angle Steps", "Sweep resolution", "スイープの刻み"),
                    _faceSdf.angleSteps, 8, 128);
                _faceSdf.flipForward = EditorGUILayout.Toggle(
                    _kit.Label("Flip Forward", "When the head faces -Z", "頭が -Z を向いているとき"),
                    _faceSdf.flipForward);

                // 水平スイープのままだと顎下〜首の境界が実際のライト（通常は上方から）と
                // ずれる。モデルによってはそこで首まわりの影が不自然になるので、
                // 左右（R/G）チャンネルだけ仰角を付けて焼けるようにしてある。
                // 0 は従来と同じ水平。上下（B/A）は対象外。
                _faceSdf.xAxisTilt = EditorGUILayout.Slider(
                    _kit.Label("X Axis Tilt", "Elevation of the left/right sweep light (deg)",
                               "左右スイープ光の仰角（度）。首まわりが不自然なときに上げる"),
                    _faceSdf.xAxisTilt, -45f, 45f);

                _faceSdf.useCastShadow = EditorGUILayout.Toggle(
                    _kit.Label("Cast Shadow", "Include nose/brow cast shadows via raycasts",
                               "鼻・眉の落ち影をレイキャストで含める"),
                    _faceSdf.useCastShadow);
                using (new EditorGUI.DisabledScope(!_faceSdf.useCastShadow))
                    _faceSdf.castDistance = EditorGUILayout.Slider(
                        _kit.Label("Cast Distance", "Cast ray length (m)", "落ち影レイ長（m）"),
                        _faceSdf.castDistance, 0.02f, 0.5f);

                // 距離場ブレンド（T-346）: 頂点補間の等値線はポリゴン割りと法線ノイズで
                // ガタつく。等値線ごとの符号付き距離場で丸め直すと、外部の SDF 生成
                // ツールを介さなくても滑らかな線になる。
                _faceSdf.dfBlend = EditorGUILayout.Toggle(
                    _kit.Label("DF Blend", "Reshape shadow-boundary iso-lines with signed "
                               + "distance fields. Smooth lines without external tools",
                               "距離場ブレンド。影境界の等値線を距離場で丸め直し、"
                               + "外部ツール無しで滑らかな線にする"),
                    _faceSdf.dfBlend);
                using (new EditorGUI.DisabledScope(!_faceSdf.dfBlend))
                {
                    _faceSdf.dfSpread = EditorGUILayout.Slider(
                        _kit.Label("Line Softness", "Rounding radius in texels. "
                                   + "Higher = smoother, loses fine detail",
                                   "線の丸め半径（texel）。大きいほど滑らか・細部が消える"),
                        _faceSdf.dfSpread, 1f, 16f);
                    // Idol は 16bit 1ch の一方式だけ（T-382）。常に pack16 で焼く。
                    _faceSdf.pack16 = true;
                }

                // **_FaceFlatness を立てないと焼いても絵が変わらない。**
                // Baker が立てるのは Doll 名（_UseFaceSDF）で Idol には無い。
                Note(jp, "16bit 1ch（R×256+G）で焼き、SDF Blend（_FaceFlatness）を立てます。"
                       + "**シーンに FaceDirectionBinder が要ります** ── "
                       + "頭ボーンの向きが無いと顔だけ破綻します。",
                        "Bakes a 16-bit 1ch (R*256+G) SDF and sets SDF Blend (_FaceFlatness). "
                      + "A FaceDirectionBinder must exist in the scene, or the face alone breaks.");

                if (BakeButton(jp ? "Face SDF をベイク" : "Bake Face SDF"))
                    BakeAll(e, BakeSdfAndRoute);
            }
        }

        /// <summary>Face SDF を 16bit 1ch で焼き、SDF Blend を立てる。</summary>
        private bool BakeSdfAndRoute(Material m)
        {
            _faceSdf.pack16 = true;
            if (!EasyPbrFaceSdfBaker.Bake(_bakeRoot, m, _faceSdf)) return false;
            SetIfUnset(m, "_FaceFlatness", 1f);
            return true;
        }

        private void DrawBentNormal(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _bentOpen, jp, "Bent Normal（遮蔽を避けた法線）", "Bent Normal")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _bentNormal.resolution = ResField(_bentNormal.resolution);
                _bentNormal.rayCount = Rays(_bentNormal.rayCount);
                _bentNormal.maxDistance = Distance(_bentNormal.maxDistance);
                _bentNormal.strength = EditorGUILayout.Slider(
                    _kit.Label("Strength", "How far the normal bends", "法線をどれだけ曲げるか"),
                    _bentNormal.strength, 0.1f, 2.0f);
                _bentNormal.smooth = Smooth(_bentNormal.smooth);
                _bentNormal.dilate = Dilate(_bentNormal.dilate);
                _bentNormal.blur = Blur(_bentNormal.blur);

                Note(jp, "壁際や脇の下で、本来光が来ない方向から間接光が入るのを防ぎます。"
                       + "焼いた後に Use Bent Normal を ON にします。",
                        "Stops indirect light arriving from occluded directions. "
                      + "Turns Use Bent Normal on after baking.");

                if (BakeButton(jp ? "Bent Normal をベイク" : "Bake Bent Normal"))
                    BakeAll(e, m => BakeThenSet(
                        () => EasyPbrBentNormalBaker.Bake(_bakeRoot, m, _bentNormal),
                        m, "_BentNormalOn", 1f));
            }
        }

        private void DrawCurvature(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _curvatureOpen, jp, "Curvature（曲率）", "Curvature")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _curvature.resolution = ResField(_curvature.resolution);
                _curvature.intensity = EditorGUILayout.Slider(
                    _kit.Label("Intensity", "Curvature contrast", "曲率のコントラスト"),
                    _curvature.intensity, 0.1f, 2.0f);
                _curvature.smooth = Smooth(_curvature.smooth);
                _curvature.dilate = Dilate(_curvature.dilate);
                _curvature.blur = Blur(_curvature.blur);

                Note(jp, "曲率の唯一の供給源。Curvature Influence（陰・影タブ）が"
                       + "これを読んで曲がった面の境界を広げます。"
                       + "焼いた後、Influence が 0 なら 1 にします。",
                        "The only curvature source. Curvature Influence (Shading tab) "
                      + "reads it to widen the transition on curved areas. "
                      + "Sets Influence to 1 after baking if it is 0.");

                if (BakeButton(jp ? "Curvature をベイク" : "Bake Curvature"))
                    BakeAll(e, m => BakeThenSet(
                        () => EasyPbrCurvatureBaker.Bake(_bakeRoot, m, _curvature),
                        m, "_CurvatureSoftness", 1f));
            }
        }

        private void DrawSss(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _sssOpen, jp, "SSS（散乱の向きと厚み）", "SSS")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _sss.resolution = ResField(_sss.resolution);
                _sss.rayCount = Rays(_sss.rayCount);
                _sss.maxDistance = Distance(_sss.maxDistance);
                _sss.intensity = EditorGUILayout.Slider(
                    _kit.Label("Intensity", "Scatter strength", "散乱の強さ"),
                    _sss.intensity, 0.1f, 2.0f);
                _sss.smooth = Smooth(_sss.smooth);
                _sss.dilate = Dilate(_sss.dilate);
                _sss.blur = Blur(_sss.blur);

                Note(jp, "RGB が散乱の向き、A が厚み。透過（Transmission）が使います。"
                       + "焼いた後に SSS Map Strength を 1 にします。",
                        "RGB is the scatter direction, A the thickness. Used by Transmission. "
                      + "Sets SSS Map Strength to 1 after baking.");

                if (BakeButton(jp ? "SSS をベイク" : "Bake SSS"))
                    BakeAll(e, m => BakeThenSet(
                        () => EasyPbrSssBaker.Bake(_bakeRoot, m, _sss),
                        m, "_SSSMapStrength", 1f));
            }
        }

        // ------------------------------------------------------------------
        //  自動アサインできないもの
        // ------------------------------------------------------------------
        private void DrawAo(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _aoOpen, jp, "Ambient Occlusion（**手で合成が要る**）",
                    "Ambient Occlusion (needs manual compositing)")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                // **ここだけ自動で入らない。** 黙って「焼けました」と出すと、
                // 割り当たっていないことに気付かないまま強度だけ上げることになる。
                EditorGUILayout.HelpBox(
                    jp ? "Idol は遮蔽を単体テクスチャではなく **Mask Map の G** に詰める設計なので、"
                         + "焼いた画像は**保存されるだけで自動では割り当たりません**。"
                         + "画像編集で Mask Map の G チャンネルへ合成してください"
                         + "（R:Metallic / G:Occlusion / B:Thickness / A:Smoothness）。"
                       : "Idol packs occlusion into the G channel of the Mask Map rather than a "
                         + "standalone texture, so the baked image is saved but NOT assigned. "
                         + "Composite it into the Mask Map's G channel "
                         + "(R:Metallic / G:Occlusion / B:Thickness / A:Smoothness).",
                    MessageType.Warning);

                _ao.resolution = ResField(_ao.resolution);
                _ao.rayCount = Rays(_ao.rayCount);
                _ao.maxDistance = Distance(_ao.maxDistance);
                _ao.intensity = EditorGUILayout.Slider(
                    _kit.Label("Intensity", "AO strength", "AO の強さ"), _ao.intensity, 0.1f, 2.0f);
                _ao.floor = EditorGUILayout.Slider(
                    _kit.Label("Floor", "Lift dark areas", "暗部の下限"), _ao.floor, 0.0f, 0.5f);
                _ao.smooth = Smooth(_ao.smooth);
                _ao.dilate = Dilate(_ao.dilate);
                _ao.blur = Blur(_ao.blur);

                if (BakeButton(jp ? "AO をベイク（保存のみ）" : "Bake AO (save only)"))
                    BakeAll(e, m => EasyPbrAoBaker.Bake(_bakeRoot, m, _ao));
            }
        }

        // ------------------------------------------------------------------
        //  ヘルパ
        // ------------------------------------------------------------------

        /// <summary>ベイクが成功したときだけ、Idol 側の有効化プロパティを立てる。</summary>
        private static bool BakeThenSet(Func<bool> bake, Material m, string prop, float value)
        {
            bool ok = bake();
            if (ok) SetIfUnset(m, prop, value);
            return ok;
        }

        /// <summary>ユーザーが自分で切っている値は尊重する（0 のときだけ立てる）。</summary>
        private static void SetIfUnset(Material m, string prop, float value)
        {
            if (m.HasProperty(prop) && m.GetFloat(prop) <= 0f)
                m.SetFloat(prop, value);
        }

        private bool Foldout(ref bool state, bool jp, string labelJp, string labelEn)
        {
            state = EditorGUILayout.Foldout(state, jp ? labelJp : labelEn, true);
            return state;
        }

        private void Note(bool jp, string textJp, string textEn)
        {
            EditorGUILayout.HelpBox(jp ? textJp : textEn, MessageType.None);
        }

        private static bool BakeButton(string label)
        {
            EditorGUILayout.Space(2);
            return GUILayout.Button(label, GUILayout.Height(24));
        }

        private int ResField(int current)
        {
            int idx = Mathf.Max(0, Array.IndexOf(s_Res, current));
            idx = EditorGUILayout.Popup(
                _kit.Label("Resolution", "Output texture size", "出力テクスチャの解像度"),
                idx, s_ResLabels);
            return s_Res[Mathf.Clamp(idx, 0, s_Res.Length - 1)];
        }

        private int Rays(int v) => EditorGUILayout.IntSlider(
            _kit.Label("Samples", "Rays per vertex", "頂点あたりのレイ数"), v, 16, 256);

        private float Distance(float v) => EditorGUILayout.Slider(
            _kit.Label("Max Distance", "Reach in metres. Smaller is more local",
                       "届く距離（m）。小さいほど局所的"), v, 0.02f, 3.0f);

        private int Smooth(int v) => EditorGUILayout.IntSlider(
            _kit.Label("Smooth", "Reduce facets", "ファセット低減"), v, 0, 8);

        private int Dilate(int v) => EditorGUILayout.IntSlider(
            _kit.Label("Dilate", "Bleed past UV seams", "UV の継ぎ目を埋める"), v, 0, 16);

        private int Blur(int v) => EditorGUILayout.IntSlider(
            _kit.Label("Blur", "Texture blur", "ブラー"), v, 0, 4);

        /// <summary>選択中のマテリアル全部に実行（マルチ編集対応）。</summary>
        private void BakeAll(MaterialEditor editor, Func<Material, bool> bakeOne)
        {
            int ok = 0, total = 0;
            foreach (var o in editor.targets)
            {
                if (!(o is Material m)) continue;
                total++;
                if (bakeOne(m)) ok++;
            }
            if (total > 1)
                EditorUtility.DisplayDialog("EasyToon / Idol Baker",
                    _kit.Jp ? $"{total} 個のマテリアル中 {ok} 個にベイクしました（詳細は Console）。"
                            : $"Baked {ok} of {total} selected materials (see Console).", "OK");
        }
    }
}

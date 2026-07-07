// =============================================================================
//  IdolShaderGUI.cs
// -----------------------------------------------------------------------------
//  Idol（EasyToon 本命シェーダー）のカスタムインスペクター。
//  描画プリミティブ（折りたたみ・日英ラベル・⚡注記）は EasyShaderCore の ShaderGuiKit を
//  再利用し、状態変更は IdolMaterialSetup、ベイク UI は IdolBakingPanel に委譲。
//  セクション構成は Idol.shader の Properties ブロック順に合わせる。
//
//  Chara Part プリセット（前髪透過のステンシル運用）はこの GUI が唯一の入口:
//   - _CharaPart 変更で Stencil / Render Queue / HairSeeThrough パス有効化を即時適用
//   - Hair 以外は SetShaderPassEnabled("SRPDefaultUnlit", false) で透過パスを無効化
// =============================================================================
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Origuma.EasyShaderCore.Editor;

namespace Origuma.EasyToon.URP.Editor
{
    public class IdolShaderGUI : ShaderGUI
    {
        private const string KeyPrefix = "Origuma.EasyToon.URP.Idol.";
        private const string TabKey = KeyPrefix + "tab";

        private ShaderGuiKit _kit;
        private IdolBakingPanel _baking;
        private bool _jp;

        // タブ選択（EditorPrefs で永続化。DollShaderGUI と同方式）。
        private int _tab = -1;

        private static readonly string[] s_TabsEn = { "Base", "Shading", "Face & Hair", "Lighting", "Specular", "FX", "Baking" };
        private static readonly string[] s_TabsJp = { "基本", "陰・影", "顔・髪", "ライト", "スペキュラ", "演出", "Baking" };

        private static readonly string[] s_RenderModeEn = { "Opaque", "Cutout" };
        private static readonly string[] s_RenderModeJp = { "Opaque (不透明)", "Cutout (くり抜き)" };
        private static readonly string[] s_CharaPartEn = { "Body", "Face", "Brow (Brow / Eyelash)", "Hair", "Eye" };
        private static readonly string[] s_CharaPartJp = { "Body (体)", "Face (顔)", "Brow (眉・まつ毛)", "Hair (髪)", "Eye (瞳)" };

        // ================================================================
        //  エントリポイント
        // ================================================================
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            _kit ??= new ShaderGuiKit(KeyPrefix);
            _baking ??= new IdolBakingPanel();
            _kit.LoadPrefs();
            _kit.RebuildPropCache(properties);
            _kit.DrawToolbar("EasyToon / Idol", null, null);
            _jp = _kit.Jp;

            if (!_kit.UseCustomUI)
            {
                base.OnGUI(materialEditor, properties);
                return;
            }

            // --- タブバー（4 列グリッド・選択を永続化。DollShaderGUI 踏襲）---
            if (_tab < 0) _tab = EditorPrefs.GetInt(TabKey, 0);
            EditorGUI.BeginChangeCheck();
            _tab = GUILayout.SelectionGrid(Mathf.Clamp(_tab, 0, s_TabsEn.Length - 1),
                _jp ? s_TabsJp : s_TabsEn, 4, EditorStyles.miniButtonMid);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetInt(TabKey, _tab);
            EditorGUILayout.Space(4);

            switch (_tab)
            {
                case 0: DrawTabBase(materialEditor); break;
                case 1: DrawTabShading(materialEditor); break;
                case 2: DrawTabFaceHair(materialEditor); break;
                case 3: DrawTabLighting(materialEditor); break;
                case 4: DrawTabSpecular(materialEditor); break;
                case 5: DrawTabFx(materialEditor); break;
                case 6: DrawTabBaking(materialEditor); break;
            }
        }

        // ================================================================
        //  タブ（既存の Draw* メソッドをグルーピングして呼ぶだけ）
        // ================================================================
        private void DrawTabBase(MaterialEditor e)
        {
            DrawSurface(e);
            DrawBase(e);
            DrawEmission(e);
        }

        private void DrawTabShading(MaterialEditor e)
        {
            DrawShading(e);
            DrawTwoBandShadow(e);
            DrawCastShadow(e);
            DrawOcclusion(e);
        }

        private void DrawTabFaceHair(MaterialEditor e)
        {
            DrawFaceSDF(e);
            DrawHairShadow(e);
            DrawHairSeeThrough(e);
            DrawCharaPart(e);
            DrawAngelRing(e);
        }

        private void DrawTabLighting(MaterialEditor e)
        {
            DrawIndirect(e);
            DrawLightConditioning(e);
            DrawAdditionalLights(e);
        }

        private void DrawTabSpecular(MaterialEditor e)
        {
            DrawCelSpecular(e);
            DrawMatCap(e);
            DrawRim(e);
            DrawStocking(e);
        }

        private void DrawTabFx(MaterialEditor e)
        {
            DrawOutline(e);
            DrawDissolve(e);
            DrawLive(e);
        }

        private void DrawTabBaking(MaterialEditor e)
        {
            DrawBaking(e);
            DrawAdvanced(e);
        }

        // マテリアル読み込み/検証時に float からキーワード等を復元（stale 耐性）。
        public override void ValidateMaterial(Material material)
        {
            base.ValidateMaterial(material);
            IdolMaterialSetup.SyncKeywords(material);
        }

        // ================================================================
        //  Surface Options
        // ================================================================
        private void DrawSurface(MaterialEditor e)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("surface", true, "Surface Options", "サーフェス設定")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var renderModeProp = Prop("_RenderMode");
                    if (renderModeProp != null)
                    {
                        EditorGUI.BeginChangeCheck();
                        var lbl = _kit.VariantLabel("Render Mode (Preset)",
                            "Opaque / Cutout. Sets the _ALPHATEST_ON keyword, RenderType and Render Queue (queue base comes from the Chara Part preset to keep the Brow/Eye -> Hair order)",
                            "Opaque / Cutout。_ALPHATEST_ON キーワード・RenderType・Render Queue を自動設定（Queue の基準は Chara Part プリセット側が持ち、Brow/Eye→Hair の描画順を保つ）");
                        var cur = renderModeProp.floatValue > 0.5f ? 1 : 0;
                        var next = EditorGUILayout.Popup(lbl, cur, _jp ? s_RenderModeJp : s_RenderModeEn);
                        if (EditorGUI.EndChangeCheck())
                        {
                            e.RegisterPropertyChangeUndo("Idol Render Mode");
                            foreach (Material mat in e.targets)
                                IdolMaterialSetup.ApplyRenderMode(mat, next);
                        }
                    }

                    var alphaClipProp = Prop("_AlphaClip");
                    if (alphaClipProp != null && alphaClipProp.floatValue > 0.5f)
                        using (new EditorGUI.IndentLevelScope())
                            P(e, "_Cutoff", "Alpha Cutoff", "Clipping threshold", "クリッピングの閾値");

                    P(e, "_Cull", "Cull Mode",
                        "Which faces to render (Off / Front / Back)",
                        "描画する面 (Off / Front / Back)");
                }
            }
        }

        // ================================================================
        //  Base
        // ================================================================
        private void DrawBase(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("base", true, "Base", "基本設定")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var baseMap = Prop("_MainTex");
                    if (baseMap != null)
                    {
                        e.TexturePropertySingleLine(
                            Label("Base Map (RGB / Alpha)",
                                "Albedo. Alpha is used for clipping",
                                "アルベド。アルファはクリップに使用"),
                            baseMap, Prop("_BaseColor"));
                        e.TextureScaleOffsetProperty(baseMap);
                    }

                    EditorGUILayout.Space(4);
                    SubHeader("Color Correction", "色調補正 (HSV)");
                    var ccProp = Prop("_UseColorCorrection");
                    P(e, ccProp, "Enable Color Correction",
                        "Skips HSV conversion when off (cheaper)",
                        "OFFのとき HSV 変換をスキップします（軽量）");
                    if (ccProp != null && ccProp.floatValue > 0.5f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_HueShift", "Hue Shift", "Rotates the hue", "色合いを回転させます");
                            P(e, "_Saturation", "Saturation", "Adjusts vividness", "鮮やかさを調整します");
                            P(e, "_ValueMulti", "Value Multiplier", "Adjusts brightness", "明るさを調整します");
                        }

                    EditorGUILayout.Space(4);
                    SubHeader("Normal Map", "ノーマルマップ (凹凸)");
                    var bumpMap = Prop("_NormalMap");
                    if (bumpMap != null)
                        e.TexturePropertySingleLine(
                            Label("Normal Map",
                                "Tangent-space normal map. Drives specular / rim detail (shade ramp can use Shade Normal instead)",
                                "接空間ノーマルマップ。スペキュラ・リムのディテールに使用（陰は Shade Normal で置換可）"),
                            bumpMap, Prop("_NormalScale"));
                }
            }
        }

        // ================================================================
        //  Shading
        // ================================================================
        private void DrawShading(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("shading", true, "Shading", "シェーディング")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var modeProp = Prop("_ShadingMode");
                    P(e, modeProp, "Shading Mode",
                        "TwoBand: numeric 1st/2nd shadow control / Ramp: color design via a ramp texture. Uniform branch, no variant",
                        "TwoBand: 数値制御の1影・2影 / Ramp: Ramp テクスチャに色設計を委ねる。uniform 分岐でバリアントなし");

                    if (modeProp != null && modeProp.floatValue > 0.5f)
                    {
                        var rampMap = Prop("_ShadeRampMap");
                        if (rampMap != null)
                            e.TexturePropertySingleLine(
                                Label("Shade Ramp (HalfLambert 0..1)",
                                    "Horizontal = HalfLambert 0..1, sampled with linear clamp",
                                    "横軸 = HalfLambert 0..1。linear clamp でサンプル"),
                                rampMap);
                    }

                    P(e, "_HalfLambertWrap", "Light Wrap",
                        "Lifts the shaded side to soften shading. 0 = Lambert",
                        "陰側を持ち上げて陰影を柔らかくする。0でランバート");

                    EditorGUILayout.Space(2);
                    SubHeader("Shade Normal", "シェーディング法線");
                    var shadeNormalMap = Prop("_ShadeNormalMap");
                    if (shadeNormalMap != null)
                        e.TexturePropertySingleLine(
                            Label("Shade Normal Map",
                                "Baked smoothed normal that drives ONLY the diffuse shade ramp (specular / rim keep the detail normal). Bake in the Baking section. bump (default) = off",
                                "拡散の陰ランプだけを駆動するベイク済み平滑化法線（スペキュラ・リムはディテール法線のまま）。Baking セクションで焼く。bump（既定）で無効"),
                            shadeNormalMap);
                    P(e, "_ShadeNormalStrength", "Strength",
                        "0 = off (detail normal). Baking auto-enables to 1",
                        "0=無効（ディテール法線）。ベイクで自動的に1に");
                }
            }
        }

        // ================================================================
        //  Two Band Shadow
        // ================================================================
        private void DrawTwoBandShadow(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("twoband", true, "Two Band Shadow", "2段影（1影・2影）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_ShadowColor", "1st Shadow Color",
                        "Tint multiplied into the base color in the 1st shadow band",
                        "1影でベースカラーに乗算する色味");
                    P(e, "_ToonStep", "1st Shadow Threshold",
                        "Lit/shadow boundary position", "明→1影が切り替わるしきい値");
                    P(e, "_ToonFeather", "1st Shadow Softness",
                        "Blur width of the boundary. 0 = crisp (always at least 1px AA)",
                        "境界のぼかし幅。0でくっきり（最低1pxのAAは確保）");

                    EditorGUILayout.Space(2);
                    var shadow2Prop = Prop("_Shadow2Color");
                    P(e, shadow2Prop, "2nd Shadow Color (A = Enable)",
                        "Deeper second band below the 1st (classic two-band anime shading). Alpha 0 = off",
                        "1影より深い位置の2段目の陰（アニメの1影・2影構成）。アルファ0でOFF");
                    if (shadow2Prop != null && shadow2Prop.colorValue.a > 0f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_Shadow2Step", "2nd Shadow Threshold",
                                "Keep below the 1st threshold: lit -> 1st -> 2nd",
                                "1影より低くすると 明→1影→2影 の順に重なる");
                            P(e, "_Shadow2Feather", "2nd Shadow Softness",
                                "Blur width of the 2nd boundary", "2影境界のぼかし幅");
                        }

                    EditorGUILayout.Space(2);
                    P(e, "_ShadowHueShift", "Shadow Hue Shift",
                        "Rotates the hue of the shaded side (skin shadows toward red-purple). Keeps shadows rich instead of just dark. 0 = off",
                        "陰側の色相を回す（肌の陰を赤紫側へ等）。ただ暗い影を禁止するための色設計。0でOFF");
                    P(e, "_ShadowSaturation", "Shadow Saturation Boost",
                        "Saturation multiplier for the shaded side. Slightly above 1 keeps color alive in shadow. 1 = off",
                        "陰側の彩度倍率。1より少し上げると影の中でも色が沈まない。1でOFF");
                }
            }
        }

        // ================================================================
        //  Cast Shadow
        // ================================================================
        private void DrawCastShadow(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("castshadow", true, "Cast Shadow", "落ち影")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_CastShadowColor", "Cast Shadow Color (A = Enable)",
                        "Paints cast shadows (shadow map / char shadow) in their own tint, separate from the angle-based shade. Alpha 0 = folded into the shade ramp (default)",
                        "落ち影（シャドウマップ／キャラ影）を角度の陰と分離して専用色で塗る。アルファ0で陰ランプに折り込み（既定）");
                    P(e, "_ReceiveShadowStrength", "Receive Shadow Strength",
                        "Strength of darkening from cast shadows. 0 = no cast shadow",
                        "落ち影で暗くする強さ。0で落ち影なし");
                    P(e, "_CharShadowFaceBias", "Char Shadow Face Bias",
                        "Extra receiver depth bias for the character-dedicated shadow map. Raise slightly on Face / Eye materials to remove self-shadow acne",
                        "キャラ専用シャドウの受影側追加深度バイアス。Face / Eye マテリアルでアクネ（自己影の縞）が出るとき少し上げる");

                    EditorGUILayout.Space(2);
                    FeatureSetup.DrawFeatureGuard<IdolCharShadowFeature>(
                        _jp ? "Idol Char Shadow Feature は追加済みです。キャラのルートに IdolCharacter コンポーネントを付けると専用セルフシャドウ（髪→顔の落ち影）が有効になります。"
                            : "Idol Char Shadow Feature is set up. Add a IdolCharacter component to the character root to enable the dedicated self shadow (hair-on-face).",
                        _jp ? "Idol Char Shadow Feature が Renderer に追加されていません。キャラ専用セルフシャドウ（髪→顔の落ち影）を使うには Setup Window から追加してください。未使用時は URP 標準影で動作します。"
                            : "Idol Char Shadow Feature is NOT on the active Renderer. Add it via the Setup Window for the dedicated self shadow (hair-on-face). Without it, URP main-light shadows are used.",
                        _jp ? "Setup Window を開く" : "Open Setup Window",
                        IdolSetupWindow.Open);
                }
            }
        }

        // ================================================================
        //  Occlusion
        // ================================================================
        private void DrawOcclusion(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("occlusion", true, "Occlusion", "オクルージョン（影しきい値オフセット）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var occMap = Prop("_OcclusionMap");
                    if (occMap != null)
                        e.TexturePropertySingleLine(
                            Label("Occlusion Map (R)",
                                "Baked AO used as 'shadow-proneness': 0 = always shadowed, 0.5 = neutral, 1 = always lit. Bake in the Baking section. White (default) = no effect",
                                "ベイク AO を「影になりやすさ」として使う: 0=常影 / 0.5=ニュートラル / 1=常明。Baking セクションで焼く。白（既定）で無効"),
                            occMap);
                    P(e, "_OcclusionToShadow", "AO To Shadow Threshold",
                        "How strongly the AO map offsets the shade threshold (Gakumas style). 0 = off",
                        "AO マップで陰しきい値を局所オフセットする強さ（AO を影の付きやすさに流用）。0でOFF");
                    P(e, "_OcclusionStrength", "Occlusion Strength (Albedo Darken)",
                        "Classic AO albedo darkening (separate axis from the threshold offset). 0 = off",
                        "従来の AO によるアルベド暗化（しきい値オフセットとは別軸）。0でOFF");
                }
            }
        }

        // ================================================================
        //  Cel Specular
        // ================================================================
        private void DrawCelSpecular(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("specular", true, "Cel Specular", "セルスペキュラ")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var intensityProp = Prop("_SpecularIntensity");
                    P(e, intensityProp, "Intensity (0 = Off)",
                        "Stylized cel specular strength. 0 = skipped entirely",
                        "様式的セルスペキュラの強度。0で計算ごとスキップ");
                    if (intensityProp != null && intensityProp.floatValue > 0f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_SpecularColor", "Color (HDR)",
                                "Specular tint. HDR values can trigger Bloom", "スペキュラの色味。HDRでBloomを誘発可");
                            var specMask = Prop("_SpecularMask");
                            if (specMask != null)
                                e.TexturePropertySingleLine(
                                    Label("Specular Mask (R)",
                                        "R channel masks specular intensity", "Rチャンネルでスペキュラ強度をマスク"),
                                    specMask);
                            P(e, "_ToonSpecularStep", "Threshold",
                                "Brightness where the highlight edge is cut", "縁を切る輝度しきい値");
                            P(e, "_ToonSpecularFeather", "Softness",
                                "Edge blur of the cut (always at least 1px AA)", "切り口のぼかし幅（最低1pxのAAは確保）");
                            P(e, "_Smoothness", "Smoothness",
                                "Higher = tighter, sharper highlight", "高いほど締まった鋭いハイライト");
                            P(e, "_SpecularShadeInfluence", "Shade Dimming",
                                "Dims specular on faces inside the shade ramp. 1 = no highlight in shade",
                                "陰ランプに入った面のスペキュラを沈める。1で陰の中は完全消灯");
                            P(e, "_SpecularAA", "Specular Anti-Aliasing",
                                "Geometric specular AA. Suppresses highlight shimmer under motion. Recommended on",
                                "幾何スペキュラAA。モーション時のハイライトのチラつきを抑える。基本ON推奨");
                        }
                }
            }
        }

        // ================================================================
        //  Face SDF
        // ================================================================
        private void DrawFaceSDF(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("facesdf", true, "Face SDF Shadow", "顔 SDF シャドウ")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var enableProp = Prop("_FaceSDFEnable");
                    P(e, enableProp, "Enable Face SDF Shadow",
                        "Drives the face shadow from a baked 4ch SDF (R/G/B/A = right/left/up/down) so it sweeps smoothly with the light. Bake in the Baking section. Face material only",
                        "ベイクした4ch SDF（R/G/B/A=右/左/上/下）で顔影を駆動し、光に合わせて滑らかに動かす。Baking セクションで焼く。顔マテリアル専用");
                    if (enableProp != null && enableProp.floatValue > 0.5f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            var sdfMap = Prop("_FaceSDFMap");
                            if (sdfMap != null)
                                e.TexturePropertySingleLine(
                                    Label("Face SDF Map",
                                        "Baked face SDF (auto-assigned when baked)", "ベイクした顔SDF（焼くと自動アサイン）"),
                                    sdfMap);
                            P(e, "_FaceSDFFlip", "Flip Forward",
                                "Enable if the shadow moves the wrong way (face looks along -Z)",
                                "陰が逆方向に動くとき ON（顔が -Z 向き）");
                            P(e, "_FaceSDFSoftness", "Softness",
                                "Width of the shadow transition. Low = crisp anime edge",
                                "陰の境界のぼかし幅。低いほどパキッとしたアニメ調");
                            P(e, "_FaceSDFShadowMix", "External Shadow Mix",
                                "Mixes external cast shadows (hair on face) back into the SDF region. 0 = pure SDF",
                                "SDF 領域に外部落ち影（髪→顔）を混ぜ戻す量。0で完全SDF");
                            P(e, "_FaceSDFBlendNormalMin", "SDF Blend Normal Min",
                                "Local Y normal below which the SDF is fully disabled (under-chin fade-out)",
                                "SDF が完全無効になるローカルY法線しきい値（顎下のフェードアウト）");
                            P(e, "_FaceSDFBlendNormalMax", "SDF Blend Normal Max",
                                "Local Y normal above which the SDF is fully applied",
                                "SDF が100%適用されるローカルY法線しきい値");
                        }
                }
            }
        }

        // ================================================================
        //  Hair Screen-Space Shadow
        // ================================================================
        private void DrawHairShadow(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("hairshadow", true, "Hair Screen Shadow", "髪→顔のスクリーン影")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var intensityProp = Prop("_HairShadowIntensity");
                    P(e, intensityProp, "Intensity (0 = Off)",
                        "Screen-space bangs shadow: samples the depth a few px toward the light and darkens if a thin occluder (bangs) is just in front. Sharper than the char shadow map at close-up. Enable on Face / Brow / Eye materials. Needs the camera Depth Texture",
                        "スクリーンスペース前髪影。ライト方向へ数 px 先の深度を参照し、手前に薄い遮蔽（前髪）があれば落ち影にする。クローズアップでキャラ影より精細。Face / Brow / Eye マテリアルで有効化。カメラの Depth Texture が必要");
                    if (intensityProp != null && intensityProp.floatValue > 0f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_HairShadowOffsetPx", "Offset (px)",
                                "Screen-space sample distance toward the light (shadow width)",
                                "ライト方向へのサンプル距離（影の幅・画面ピクセル）");
                            P(e, "_HairShadowDepthMin", "Depth Min (m)",
                                "Occluders closer than this are ignored (excludes the surface itself)",
                                "これより浅い深度差は無視（自己面・連続面の除外）");
                            P(e, "_HairShadowDepthMax", "Depth Max (m)",
                                "Occluders farther than this are ignored (only thin nearby occluders like bangs)",
                                "これより深い深度差は無視（前髪のような近くの薄い遮蔽だけを拾う）");
                        }
                }
            }
        }

        // ================================================================
        //  Stocking / Sheer Fabric
        // ================================================================
        private void DrawStocking(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("stocking", true, "Stocking (Sheer Fabric)", "ストッキング（シアー生地）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var intensityProp = Prop("_StockingIntensity");
                    P(e, intensityProp, "Intensity (0 = Off)",
                        "Procedural sheer-fabric layer over the skin: front-facing areas show skin through, silhouettes look denser (thread-density approximation). Applied before shade colors so shadows pick up the fabric tint too",
                        "肌の上に視角依存の布レイヤを手続き的に重ねる。正面は肌が透け、シルエットは布が密（糸密度の近似）。陰色算出より前に合成するので影にも布色が乗る");
                    if (intensityProp != null && intensityProp.floatValue > 0f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_StockingColor", "Fabric Color",
                                "Fabric tint (skin-friendly beige by default)", "布の色（既定は肌馴染みのベージュ）");
                            var maskProp = Prop("_StockingMask");
                            if (maskProp != null)
                                e.TexturePropertySingleLine(
                                    Label("Stocking Mask (R)",
                                        "Fabric appears only where the R channel is white",
                                        "Rチャンネルの白い部分にだけ布が乗る"),
                                    maskProp);
                            P(e, "_StockingFrontOpacity", "Front Opacity",
                                "Fabric opacity on front-facing areas (how much skin shows through)",
                                "正面の布不透明度（肌の透け具合）");
                            P(e, "_StockingPower", "Graze Power",
                                "Exponent of the view-angle response (higher = denser only near silhouettes)",
                                "視角応答の指数（高いほどシルエット際だけ密になる）");
                            P(e, "_StockingSheenColor", "Sheen Color (HDR)",
                                "Additive glancing-angle sheen (sheer shine). Black = off",
                                "すそ光沢（シアーシーン）の加算色。黒でOFF");
                            P(e, "_StockingSheenPower", "Sheen Power",
                                "Higher = narrower sheen band on the silhouette",
                                "高いほどシルエット際の細い光沢になる");
                        }
                }
            }
        }

        // ================================================================
        //  Rim Light
        // ================================================================
        private void DrawRim(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("rim", true, "Rim Light", "リムライト")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_RimColor", "Rim Color (HDR)",
                        "Shared tint for depth rim and fresnel rim", "深度リム・フレネルリム共通の色");

                    SubHeader("Depth Rim", "深度リム");
                    var depthIntProp = Prop("_RimDepthIntensity");
                    P(e, depthIntProp, "Intensity (0 = Off)",
                        "Screen-space depth rim: constant pixel width regardless of distance / FOV. Needs the camera Depth Texture. 0 = skipped",
                        "スクリーンスペース深度リム。距離・FOV 非依存のピクセル一定幅。カメラの Depth Texture が必要。0でスキップ");
                    if (depthIntProp != null && depthIntProp.floatValue > 0f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_RimWidthPx", "Width (px)",
                                "Rim width in screen pixels", "画面ピクセル単位のリム幅");
                            P(e, "_RimDepthThreshold", "Depth Threshold (m)",
                                "Linear depth difference to count as an edge (consider character thickness)",
                                "エッジとみなす線形深度差（キャラの厚みを考慮）");
                        }

                    P(e, "_RimLightAlign", "Light Align",
                        "0 = all around, 1 = lit side only", "0=全周 / 1=受光側のみ");

                    SubHeader("Fresnel Rim", "フレネルリム（従来式）");
                    var fresnelProp = Prop("_RimIntensity");
                    P(e, fresnelProp, "Intensity (0 = Off)",
                        "Classic fresnel rim, usable together with the depth rim", "従来式フレネルリム。深度リムと併用可");
                    if (fresnelProp != null && fresnelProp.floatValue > 0f)
                        using (new EditorGUI.IndentLevelScope())
                            P(e, "_RimThickness", "Thickness", "Higher = thicker rim", "高いほど縁が太い");

                    SubHeader("Back Rim (Live)", "バックライトリム（ライブ演出）");
                    var backProp = Prop("_BackRimEnable");
                    P(e, backProp, "Enable Back Rim",
                        "Light-independent silhouette rim from a fixed direction (Gakumas live style). Can be driven per character via IdolCharacter",
                        "ライトと独立した方向指定のシルエットリム（ライブ演出のシルエット確保用）。IdolCharacter からキャラ単位で一括制御可");
                    if (backProp != null && backProp.floatValue > 0.5f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_BackRimColor", "Color (HDR)", "Back rim tint", "バックリムの色");
                            P(e, "_BackRimPitch", "Pitch", "Vertical source direction", "光源の上下方向");
                            P(e, "_BackRimYaw", "Yaw", "Horizontal source direction (180 = behind)", "光源の水平方向（180=真後ろ）");
                            P(e, "_BackRimPower", "Power", "Higher = narrower rim", "高いほど縁が細い");
                        }
                }
            }
        }

        // ================================================================
        //  Angel Ring
        // ================================================================
        private void DrawAngelRing(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("angelring", true, "Angel Ring (Hair Highlight)", "天使の輪（ヘアハイライト）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var intProp = Prop("_AngelRingIntensity");
                    P(e, intProp, "Intensity (0 = Off)",
                        "Anisotropic toon hair band. Hair material only. 0 = skipped",
                        "異方性バンドをトゥーン化したヘアハイライト。髪マテリアル専用。0でスキップ");
                    if (intProp != null && intProp.floatValue > 0f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_AngelRingColor", "Color (HDR)", "Band tint", "バンドの色");
                            P(e, "_AngelRingThreshold", "Threshold", "Band cut threshold", "バンドを切るしきい値");
                            P(e, "_AngelRingSoftness", "Softness", "Band edge blur", "バンド縁のぼかし");
                            P(e, "_AngelRingShift", "Shift", "Moves the band along the hair", "バンド位置のオフセット");
                            P(e, "_AngelRingViewFollow", "View Follow",
                                "0 = follows the light, 1 = follows the camera", "0=ライト追従 / 1=カメラ追従");

                            var flowMap = Prop("_HairFlowMap");
                            if (flowMap != null)
                                e.TexturePropertySingleLine(
                                    Label("Hair Flow Map",
                                        "R/G = double-angle flow, B = confidence. Bake in the Baking section",
                                        "R/G=倍角毛流れ、B=信頼度。Baking セクションで焼く"),
                                    flowMap);
                            P(e, "_HairFlowStrength", "Flow Strength",
                                "0 = off (UV tangent only). Baking auto-enables to 1",
                                "0=無効（UV接線のみ）。ベイクで自動的に1に");
                        }
                }
            }
        }

        // ================================================================
        //  Hair See-Through
        // ================================================================
        private void DrawHairSeeThrough(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("seethrough", true, "Hair See-Through", "前髪透過")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_HairSeeThroughAlpha", "See-Through Alpha",
                        "Opacity of hair drawn over Brow / Eye stencil bits (lower = more see-through). Only effective when Chara Part = Hair. Can be driven per character via IdolCharacter",
                        "眉・目のステンシルビット上に重ね描きする髪の不透明度（低いほど透ける）。Chara Part = Hair のときのみ有効。IdolCharacter から一括制御可");
                    EditorGUILayout.HelpBox(
                        _jp ? "前髪透過は Chara Part プリセット（下の Chara Part & Stencil セクション）で Brow / Eye / Hair を設定すると機能します。"
                            : "Hair see-through works once Brow / Eye / Hair materials are set up via the Chara Part preset (see the Chara Part & Stencil section below).",
                        MessageType.None);
                }
            }
        }

        // ================================================================
        //  Indirect / Light Conditioning / Additional Lights
        // ================================================================
        private void DrawIndirect(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("indirect", true, "Indirect Light", "間接光（アンビエント）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_IndirectFlatten", "Flatten",
                        "Flattens the directional component of ambient SH so the character sits in a uniform ambient",
                        "環境光（SH）の方向成分を潰し、キャラ全体を均一なアンビエントで包む");
                    P(e, "_IndirectIntensity", "Intensity",
                        "Ambient contribution multiplier. 1 = as-is", "間接光の寄与倍率。1でそのまま");
                    P(e, "_IndirectTint", "Tint",
                        "Ambient tint color. White = no change", "間接光の色補正。白で無変化");
                }
            }
        }

        private void DrawLightConditioning(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("conditioning", true, "Light Conditioning", "キャラ用ライト整形")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_LightColorInfluence", "Light Color Influence",
                        "How much the main light's color tints the character. Lower keeps the color design under saturated stage lighting. 1 = physical",
                        "メインライトの色がキャラに乗る度合い。下げると原色照明でも色設計が保たれる。1で物理どおり");
                    P(e, "_LightSaturationLimit", "Light Saturation Limit",
                        "Caps the main light's saturation (hue kept). 1 = no limit",
                        "メインライトの彩度上限（色相は保持）。1で制限なし");
                    P(e, "_LightMinBrightness", "Light Min Brightness",
                        "Minimum light brightness so the character never goes fully black. 0 = off",
                        "ライト輝度の下限。暗所でもキャラが完全黒に沈まない。0でOFF");
                }
            }
        }

        private void DrawAdditionalLights(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("additional", true, "Additional Lights", "追加ライト")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_AdditionalLightBlendMode", "Blend Mode",
                        "Add: physical (can blow out) / Max: anime-friendly (keeps saturation)",
                        "Add: 物理的（白飛びしやすい）/ Max: アニメ向け（彩度を保つ）");
                    P(e, "_AdditionalBlowoutLimit", "Blowout Limit",
                        "Luminance cap per additional light", "追加ライト1灯あたりの輝度上限");
                }
            }
        }

        // ================================================================
        //  MatCap / Emission
        // ================================================================
        private void DrawMatCap(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("matcap", true, "MatCap", "MatCap（金属表現）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var enableProp = Prop("_UseMatCap");
                    P(e, enableProp, "Enable MatCap",
                        "View-space MatCap (stylized metal). Add or Multiply", "ビュー空間 MatCap（様式的な金属表現）。加算/乗算");
                    if (enableProp != null && enableProp.floatValue > 0.5f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_MatCapBlend", "Blend Mode", "Add or Multiply", "Add（加算）/ Multiply（乗算）");
                            var tex = Prop("_MatCapTex");
                            if (tex != null)
                                e.TexturePropertySingleLine(
                                    Label("MatCap Texture (RGB)", "Sphere lighting texture", "球状ライティングテクスチャ"),
                                    tex);
                            P(e, "_MatCapColor", "Tint (HDR)", "MatCap tint", "MatCapの色味");
                            P(e, "_MatCapIntensity", "Intensity", "MatCap strength", "MatCapの強度");
                        }
                }
            }
        }

        private void DrawEmission(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("emission", true, "Emission", "発光")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var enableProp = Prop("_UseEmission");
                    P(e, enableProp, "Enable Emission",
                        "HDR emission (neon-style; intended to trigger Bloom)", "HDR エミッション（ネオン表現。Bloom 誘発を意図）");
                    if (enableProp != null && enableProp.floatValue > 0.5f)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            var tex = Prop("_EmissionMap");
                            var col = Prop("_EmissionColor");
                            if (tex != null && col != null)
                                e.TexturePropertySingleLine(
                                    Label("Emission Map & Color", "Emission texture (RGB) x HDR color", "発光テクスチャ(RGB) × HDRカラー"),
                                    tex, col);
                            P(e, "_EmissionIntensity", "Intensity", "Emission strength", "発光の強度");
                        }
                }
            }
        }

        // ================================================================
        //  Dissolve
        // ================================================================
        private void DrawDissolve(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("dissolve", true, "Dissolve", "ディゾルブ（消失）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var useProp = Prop("_UseDissolve");
                    Pv(e, useProp, "Enable Dissolve",
                        "Enables the dissolve effect (works in all passes incl. shadows / outline / char shadow)",
                        "ディゾルブを有効化（影・アウトライン・キャラ影含む全パスで消える）");

                    bool dissolveOn = useProp != null && useProp.floatValue > 0.5f;
                    if (dissolveOn)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_DissolveAmount", "Progress",
                                "0 = fully visible, 1 = fully dissolved. Timeline-friendly", "0で完全表示、1で完全消失。Timeline から直キー可");
                            P(e, "_DissolveInvert", "Invert",
                                "Reverses the dissolve direction", "消失方向を逆転");
                            P(e, "_DissolveType", "Axis",
                                "None = noise only / WorldY / LocalY. Uniform branch, no variant",
                                "None=ノイズのみ / WorldY / LocalY。uniform 分岐でバリアントなし");

                            var axisProp = Prop("_DissolveType");
                            if (axisProp != null && axisProp.floatValue > 0.5f)
                            {
                                P(e, "_DissolveStartY", "Start Y", "Y where the fade starts", "フェードが始まるY座標");
                                P(e, "_DissolveEndY", "End Y", "Y where the fade ends", "フェードが終わるY座標");
                            }

                            EditorGUILayout.Space(2);
                            var tex = Prop("_DissolveTex");
                            if (tex != null)
                                e.TexturePropertySingleLine(
                                    Label("Dissolve Noise (R)", "Noise texture that perturbs the edge", "境界を揺らすノイズテクスチャ"),
                                    tex);
                            P(e, "_DissolveNoiseScale", "Noise Scale", "Noise frequency", "ノイズの細かさ");
                            P(e, "_DissolveNoiseStrength", "Noise Strength", "Edge perturbation amount", "境界の揺れ幅");

                            EditorGUILayout.Space(2);
                            P(e, "_DissolveEdgeColor", "Edge Outer Color (HDR)",
                                "Glow at the dissolving frontline", "消失の最前線の輝き");
                            P(e, "_DissolveEdgeColor2", "Edge Inner Color (HDR)",
                                "Replacement color just inside the edge", "少し内側の置換色");
                            P(e, "_DissolveEdgeWidth", "Edge Width", "Thickness of the glowing edge", "発光する境界線の太さ");
                            P(e, "_DissolveEdgeStep", "Step Edge (Toon Style)",
                                "Quantizes the edge into crisp toon bands", "縁をパキッとした階調にする");
                        }

                    // shader_feature キーワードを float から冪等同期（EnableKeyword は冪等）。
                    foreach (Material mat in e.targets)
                        if (dissolveOn) mat.EnableKeyword("_DISSOLVE_ON");
                        else mat.DisableKeyword("_DISSOLVE_ON");
                }
            }
        }

        // ================================================================
        //  Live
        // ================================================================
        private void DrawLive(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("live", true, "Live", "ライブ演出")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    P(e, "_BlackOut", "Black Out",
                        "Darkens the final color toward black", "最終色を黒へ暗転させます");
                    EditorGUILayout.Space(2);
                    EditorGUILayout.HelpBox(
                        _jp ? "Black Out / Back Rim / 前髪透過 / 仮想ライト方向は IdolCharacter コンポーネントでキャラ単位に一括制御できます（Timeline 対応）。"
                            : "Black Out / Back Rim / hair see-through / virtual light direction can be driven per character via the IdolCharacter component (Timeline-friendly).",
                        MessageType.None);
                }
            }
        }

        // ================================================================
        //  Chara Part & Stencil（前髪透過プリセット）
        // ================================================================
        private void DrawCharaPart(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("charapart", true, "Chara Part and Stencil", "部位プリセット（前髪透過）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var partProp = Prop("_CharaPart");
                    if (partProp == null) return;

                    EditorGUI.BeginChangeCheck();
                    var lbl = Label("Chara Part (Preset)",
                        "Applies the stencil layout, Render Queue and HairSeeThrough pass toggle for the hair see-through setup. Draw order requirement: Brow/Eye before Hair",
                        "前髪透過のためのステンシル・Render Queue・HairSeeThrough パス有効化を一括適用。描画順の前提: Brow/Eye → Hair");
                    var cur = Mathf.Clamp((int)partProp.floatValue, 0, 4);
                    var next = EditorGUILayout.Popup(lbl, cur, _jp ? s_CharaPartJp : s_CharaPartEn);
                    if (EditorGUI.EndChangeCheck())
                    {
                        e.RegisterPropertyChangeUndo("Idol Chara Part");
                        partProp.floatValue = next;
                        foreach (Material mat in e.targets)
                            IdolMaterialSetup.ApplyCharaPart(mat, next);
                    }

                    // 適用内容の説明。
                    EditorGUILayout.HelpBox(IdolMaterialSetup.DescribeCharaPart(cur, _jp), MessageType.Info);

                    // プリセットの再適用（手動で Stencil を触ったあとの復元用）。
                    if (GUILayout.Button(_jp ? "プリセットを再適用" : "Reapply Preset"))
                    {
                        e.RegisterPropertyChangeUndo("Idol Chara Part Reapply");
                        foreach (Material mat in e.targets)
                            IdolMaterialSetup.ApplyCharaPart(mat, cur);
                    }

                    EditorGUILayout.Space(4);
                    if (_kit.Foldout("stencil.manual", false, _jp ? "Stencil（手動調整）" : "Stencil (Manual)"))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            P(e, "_StencilRef", "Stencil Ref", "Stencil reference value", "ステンシルの参照値");
                            P(e, "_StencilComp", "Compare Function", "Stencil compare function", "比較条件");
                            P(e, "_StencilPass", "Pass Operation", "Operation when the test passes", "テスト通過時の処理");
                            P(e, "_StencilFail", "Fail Operation", "Operation when the test fails", "テスト失敗時の処理");
                            P(e, "_StencilZFail", "ZFail Operation", "Operation when depth test fails", "Zテスト失敗時の処理");
                            P(e, "_StencilReadMask", "Read Mask", "Stencil read mask", "読み取りマスク");
                            P(e, "_StencilWriteMask", "Write Mask", "Stencil write mask", "書き込みマスク");
                        }
                }
            }
        }

        // ================================================================
        //  Outline
        // ================================================================
        private void DrawOutline(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("outline", true, "Outline", "アウトライン（輪郭線）")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    FeatureSetup.DrawFeatureGuard<IdolOutlineFeature>(
                        _jp ? "Idol Outline Feature は追加済みです。太さは頂点カラー R、Zオフセットは G で部位制御できます。"
                            : "Idol Outline Feature is set up. Vertex color R = width multiplier, G = Z offset.",
                        _jp ? "Idol Outline Feature が Renderer に追加されていません。アウトラインの表示には Setup Window から追加してください（ForwardLit のバッチング維持のため独自パス化）。"
                            : "Idol Outline Feature is NOT on the active Renderer. Add it via the Setup Window to draw outlines (separated pass keeps ForwardLit batching).",
                        _jp ? "Setup Window を開く" : "Open Setup Window",
                        IdolSetupWindow.Open);
                    EditorGUILayout.Space(2);
                    P(e, "_OutlineColor", "Color",
                        "Outline color. With Albedo Blend it acts as a multiplier over the albedo",
                        "輪郭線の色。Albedo Blend 使用時はアルベドへの乗算色として働く");
                    P(e, "_OutlineAlbedoBlend", "Albedo Blend",
                        "Blends the line color toward (albedo x Color) so lines match each part. 0 = fixed color",
                        "線の色を（アルベド×Color）側へブレンドし部位に馴染ませる。0で固定色");
                    P(e, "_OutlineWidth", "Width (mm)",
                        "Outline thickness in millimeters (distance / FOV normalized)",
                        "輪郭線の太さ（mm。距離・FOV 正規化済み）");
                    P(e, "_OutlineMaxScreenPx", "Max Screen Px",
                        "Screen-space width cap so close-ups don't get fat lines",
                        "近接で太りすぎないための画面ピクセル上限");
                    P(e, "_OutlineZOffset", "Z Offset",
                        "Pushes the line away to clean up overlaps (scaled by vertex color G)",
                        "線を奥に押して重なりを整える（頂点カラー G で倍率）");
                }
            }
        }

        // ================================================================
        //  Baking / Advanced
        // ================================================================
        private void DrawBaking(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            _baking.Draw(e, _kit);
        }

        private void DrawAdvanced(MaterialEditor e)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Section("advanced", false, "Advanced Options", "高度な設定")) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    e.RenderQueueField();
                    e.EnableInstancingField();
                    e.DoubleSidedGIField();
                }
            }
        }

        // ================================================================
        //  描画プリミティブの委譲（実装と状態は ShaderGuiKit が所有）
        // ================================================================
        private bool Section(string id, bool defaultOpen, string titleEn, string titleJp)
            => _kit.Section(id, defaultOpen, titleEn, titleJp, "", "");
        private void SubHeader(string en, string jp) => _kit.SubHeader(en, jp);
        private GUIContent Label(string label, string tipEn, string tipJp) => _kit.Label(label, tipEn, tipJp);
        private MaterialProperty Prop(string name) => _kit.Prop(name);
        private void P(MaterialEditor e, MaterialProperty prop, string label, string tipEn, string tipJp)
            => _kit.P(e, prop, label, tipEn, tipJp);
        private void P(MaterialEditor e, string name, string label, string tipEn, string tipJp)
            => _kit.P(e, name, label, tipEn, tipJp);
        private void Pv(MaterialEditor e, MaterialProperty prop, string label, string tipEn, string tipJp)
            => _kit.Pv(e, prop, label, tipEn, tipJp);
    }

    // =========================================================================
    //  Idol マテリアルの「状態変更」ロジック（描画ではない）を集約した純粋ユーティリティ。
    //   - Render Mode プリセット（Opaque / Cutout）
    //   - Chara Part プリセット（Stencil / Queue / HairSeeThrough パス有効化）
    //   - キーワードの float 同期（ValidateMaterial から）
    //  GUI（IdolShaderGUI）と検証（ValidateMaterial）の双方から呼ばれる。
    // =========================================================================
    internal static class IdolMaterialSetup
    {
        private static readonly int RenderMode = Shader.PropertyToID("_RenderMode");
        private static readonly int AlphaClip  = Shader.PropertyToID("_AlphaClip");
        private static readonly int CharaPart  = Shader.PropertyToID("_CharaPart");
        private static readonly int StencilRef       = Shader.PropertyToID("_StencilRef");
        private static readonly int StencilComp      = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilPass      = Shader.PropertyToID("_StencilPass");
        private static readonly int StencilFail      = Shader.PropertyToID("_StencilFail");
        private static readonly int StencilZFail     = Shader.PropertyToID("_StencilZFail");
        private static readonly int StencilReadMask  = Shader.PropertyToID("_StencilReadMask");
        private static readonly int StencilWriteMask = Shader.PropertyToID("_StencilWriteMask");

        // HairSeeThrough パスの LightMode（Idol.shader と一致させること）。
        private const string SeeThroughPassName = "SRPDefaultUnlit";

        // 部位ごとの Render Queue（Brow/Eye → Hair の描画順を Queue で保証）。
        //  Render Mode（Opaque/Cutout）に依らずこの値を使う。Cutout を AlphaTest
        //  帯（2450+）へ動かすと Brow(Cutout) が Hair(Opaque) より後になり
        //  前髪透過が壊れるため、部位 Queue を常に優先する。
        private static readonly int[] s_PartQueue = { 2000, 2000, 2002, 2010, 2002 };

        // Render Mode プリセット（Opaque / Cutout）。Queue は Chara Part 由来。
        public static void ApplyRenderMode(Material mat, int mode)
        {
            bool cutout = mode == 1;
            mat.SetFloat(RenderMode, cutout ? 1f : 0f);
            mat.SetFloat(AlphaClip, cutout ? 1f : 0f);
            mat.SetOverrideTag("RenderType", cutout ? "TransparentCutout" : "Opaque");
            if (cutout) mat.EnableKeyword("_ALPHATEST_ON");
            else mat.DisableKeyword("_ALPHATEST_ON");
            ApplyPartQueue(mat);
        }

        // Chara Part プリセット（Stencil レイアウト / Queue / HairSeeThrough パス）。
        //  ビットレイアウト: bit1=2: Brow, bit2=4: Eye（ReadMask/WriteMask 6）。
        public static void ApplyCharaPart(Material mat, int part)
        {
            mat.SetFloat(CharaPart, part);

            switch (part)
            {
                case 2: // Brow（眉・まつ毛）: Ref=2 を書き込む
                    SetStencil(mat, 2, CompareFunction.Always, StencilOp.Replace, 255, 6);
                    break;
                case 4: // Eye（瞳）: Ref=4 を書き込む
                    SetStencil(mat, 4, CompareFunction.Always, StencilOp.Replace, 255, 6);
                    break;
                case 3: // Hair: Brow/Eye 済みピクセル（bit 2|4）を避けて本体を描く
                    SetStencil(mat, 0, CompareFunction.Equal, StencilOp.Keep, 6, 255);
                    break;
                default: // Body / Face: 既定値（ステンシル影響なし）
                    SetStencil(mat, 0, CompareFunction.Always, StencilOp.Keep, 255, 255);
                    break;
            }

            ApplyPartQueue(mat);

            // HairSeeThrough パス（半透明の重ね描き）は Hair のみ有効化。
            //  非髪マテリアルが眉・目の手前に来たとき誤描画されるのを防ぐ。
            mat.SetShaderPassEnabled(SeeThroughPassName, part == 3);
        }

        // 適用内容の説明（HelpBox 表示用）。
        public static string DescribeCharaPart(int part, bool jp)
        {
            switch (part)
            {
                case 2:
                    return jp ? "Brow: Stencil Ref=2 / Comp=Always / Pass=Replace / WriteMask=6、Queue=2002、HairSeeThrough パス無効。眉・まつ毛が前髪より先に描かれ、ステンシルビットを立てます。"
                              : "Brow: Stencil Ref=2 / Comp=Always / Pass=Replace / WriteMask=6, Queue=2002, HairSeeThrough pass disabled. Draws before hair and marks the stencil bit.";
                case 4:
                    return jp ? "Eye: Stencil Ref=4 / Comp=Always / Pass=Replace / WriteMask=6、Queue=2002、HairSeeThrough パス無効。瞳が前髪より先に描かれ、ステンシルビットを立てます。"
                              : "Eye: Stencil Ref=4 / Comp=Always / Pass=Replace / WriteMask=6, Queue=2002, HairSeeThrough pass disabled. Draws before hair and marks the stencil bit.";
                case 3:
                    return jp ? "Hair: Stencil Ref=0 / Comp=Equal / ReadMask=6、Queue=2010、HairSeeThrough パス有効。眉・目の上には本体を描かず、透過パスが半透明で重ね描きします。"
                              : "Hair: Stencil Ref=0 / Comp=Equal / ReadMask=6, Queue=2010, HairSeeThrough pass ENABLED. Skips pixels over Brow/Eye; the see-through pass fills them semi-transparently.";
                default:
                    return jp ? "Body / Face: Stencil 既定値（影響なし）、Queue=2000、HairSeeThrough パス無効。"
                              : "Body / Face: default stencil (no effect), Queue=2000, HairSeeThrough pass disabled.";
            }
        }

        // マテリアル読み込み/検証時のキーワード復元（float が正・stale 耐性）。
        public static void SyncKeywords(Material mat)
        {
            if (mat.HasProperty(AlphaClip))
            {
                if (mat.GetFloat(AlphaClip) > 0.5f) mat.EnableKeyword("_ALPHATEST_ON");
                else mat.DisableKeyword("_ALPHATEST_ON");
            }
            if (mat.HasProperty("_UseDissolve"))
            {
                if (mat.GetFloat("_UseDissolve") > 0.5f) mat.EnableKeyword("_DISSOLVE_ON");
                else mat.DisableKeyword("_DISSOLVE_ON");
            }
        }

        private static void ApplyPartQueue(Material mat)
        {
            if (!mat.HasProperty(CharaPart)) return;
            int part = Mathf.Clamp((int)mat.GetFloat(CharaPart), 0, s_PartQueue.Length - 1);
            mat.renderQueue = s_PartQueue[part];
        }

        private static void SetStencil(Material mat, int reference, CompareFunction comp, StencilOp pass,
                                       int readMask, int writeMask)
        {
            mat.SetFloat(StencilRef, reference);
            mat.SetFloat(StencilComp, (float)comp);
            mat.SetFloat(StencilPass, (float)pass);
            mat.SetFloat(StencilFail, (float)StencilOp.Keep);
            mat.SetFloat(StencilZFail, (float)StencilOp.Keep);
            mat.SetFloat(StencilReadMask, readMask);
            mat.SetFloat(StencilWriteMask, writeMask);
        }
    }
}

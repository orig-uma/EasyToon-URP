// =============================================================================
//  ToonPBRBakingPanel.cs
// -----------------------------------------------------------------------------
//  Idol マテリアル Inspector の「Baking」タブを描く自己完結パネル。
//  ベイク本体は EasyShaderCore の public Baker 群へ委譲する。
//
//  **なぜ要るか。** Idol が読むマップのうち 8 種は Core に Baker がある:
//
//      Cavity / Curvature / AO（→ Geometry Map）/ _ShadeNormalMap / _BentNormalMap
//      _SSSMap / _FaceSDFMap / _HairFlowMap
//
//  ところが Idol 側に入口が無く、文書にも書いていなかったため、
//  **導入した人は「自分で描くしかない」と思い込む**状態だった（T-277）。
//  異方性の髪・顔 SDF・曲率駆動・ベントノーマルは、
//  どれもこれらのマップがあって初めて本領を出す機能なので影響が大きい。
//
//  **プロパティ名の対応（Baker 側 → Idol 側）:**
//
//    Baker が渡すスロット   Idol   Baker の強度名        Idol の強度名
//    （Cavity）             × → Geometry Map の R へパネルが詰める（T-422/T-423）。_CavityStrength は同名
//    _HairFlowMap           ○      _HairFlowStrength     ○ 同名（自動で入る）
//    _ShadeNormalMap        ○      _ShadeNormalStrength  ○ 同名（自動で入る）
//    _BentNormalMap         ○      _BentNormalStrength   × → _BentNormalOn をここで立てる
//    （Curvature）          × → Geometry Map の G へパネルが詰める。_CurvatureSoftness をここで立てる
//    _FaceSDFMap            ○      _UseFaceSDF           × → _FaceFlatness をここで
//    _SSSMap                ○      _SSSIntensity         × → _SSSMapStrength をここで
//    （AO）                 × → Geometry Map の B へパネルが詰める。Occlusion Source を Both にする
//
//  **Geometry 系（Cavity / Curvature / AO）は Idol に個別スロットが無い。** Core のベイカーは
//  `HasProperty` で守られているので保存だけして割り当てを飛ばし、このパネルが 3 種を 1 枚
//  （Geometry Map）に詰めて、同じ Base Map を使う材質ぜんぶに割り当てる（T-422 / T-425）。
//
//  **タブの並び（T-426 / T-427）**: 対象（Share By Base Map）→ Geometry Map → 向きのマップ
//  （Shade Normal / Bent Normal / SSS。どちらも同じ Base Map の材質で 1 枚を共有できる）→ 部位専用
//  （Face SDF / Hair Flow。常に材質ごと）。
// =============================================================================
using System;
using System.IO;
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
        // Geometry 系（Cavity / Curvature / AO）を、同じ Base Map（＝ UV が重ならない）を共有する材質で
        // 1 枚にまとめる（T-425）。材質ごとに焼くと各テクスチャの大半が空白のまま材質の数だけ増える。
        private bool _shareByBaseMap = true;
        private bool _deleteGeometrySources = true;        // 詰めた後、元の *_Cavity / *_Curvature / *_AO を消す
        private readonly System.Collections.Generic.HashSet<string> _bakedGroups = new System.Collections.Generic.HashSet<string>();
        // 1 回のボタンで実際に焼いたテクスチャの数と、割り当てた材質の数（完了のポップアップ用）。
        // Share By Base Map では「選択した材質の数」と「焼いた枚数」が一致しないので、分けて数える。
        private int _runTextures, _runAssigned;
        private bool _runDeduped;   // 直前の呼び出しが「このグループは処理済み」で抜けた

        private EasyPbrShadeNormalBaker.Settings _shadeNormal = EasyPbrShadeNormalBaker.Default;
        private EasyPbrHairFlowBaker.Settings    _hairFlow    = EasyPbrHairFlowBaker.Default;
        private EasyPbrFaceSdfBaker.Settings     _faceSdf     = MakeFaceSdfDefault();
        // 顔 SDF のプロキシ（T-414）。既定は自動（楕円体を焼く頂点に最小二乗で合わせ、Blend 0.7）。
        private bool       _sdfProxyManual;                  // 中心・半径を手で指定する（Proxy Manual Fit）
        private Transform  _sdfProxyCenterTransform;                  // 指定があれば中心 = Transform ＋ Offset
        private Vector3    _sdfProxyOffset = Vector3.zero;
        private Vector3    _sdfProxyCenterWS = Vector3.zero; // Transform 無しのときの中心（ワールド、m）
        private Vector3    _sdfProxyRadii  = Vector3.zero;   // 0 なら自動
        private GameObject _sdfProxyObject;
        private int        _sdfProxyShape;                   // 0 楕円体 / 1 卵型 / 2 前を平ら / 3 円盤 / 4 カスタム

        // Idol の既定: 卵型プロキシ・Blend 0.7（鼻・眉の形を少し残す）。ローポリの顔でも
        // 何も設定せずに滑らかな境界が出る。従来の「メッシュの法線」は Proxy で選べる。
        private static EasyPbrFaceSdfBaker.Settings MakeFaceSdfDefault()
        {
            var s = EasyPbrFaceSdfBaker.Default;
            s.proxyMode = 1; s.proxyBlend = 0.7f;
            s.proxyTaper = 0.3f; // 卵型（顎を細く）。楕円体より顔の明暗境界が自然（利用者確認済み）
            return s;
        }
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
                // 1) 共有できるもの: 形状由来のグレー 3 種を 1 枚に（同じ Base Map の材質で共有）
                DrawGeometryGroup(editor, jp);

                // 2) 向きを持つマップ（スロットあり。同じ Base Map で共有できる）
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (_kit.Section("bakedirection", true, "Direction Maps", "向きのマップ（Shade Normal / Bent Normal / SSS）", "", ""))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            DrawShadeNormal(editor, jp);
                            DrawBentNormal(editor, jp);
                            DrawSss(editor, jp);
                        }
                }

                // 3) 部位専用（その Surface Type の材質でだけ意味がある）
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (_kit.Section("bakepart", true, "Part-Specific Maps", "部位専用のマップ", "", ""))
                        using (new EditorGUI.IndentLevelScope())
                        {
                            DrawFaceSdf(editor, jp);
                            DrawHairFlow(editor, jp);
                        }
                }
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
                            "Root GameObject of the character. Meshes under it that use the selected "
                            + "materials are baked. Auto-filled from the Hierarchy selection",
                            "キャラのルートの GameObject。配下で選択中のマテリアルを使うメッシュを焼きます。"
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
                            jp ? $"{n} 個のマテリアルを選択中。ベイクは全部に対して走ります"
                                 + "（Share By Base Map が ON なら、同じ Base Map のグループごとに 1 回だけ）。"
                               : $"{n} materials selected. Baking runs for all of them "
                                 + "(once per Base Map group while Share By Base Map is on).",
                            MessageType.Info);

                    _shareByBaseMap = EditorGUILayout.Toggle(
                        _kit.Label("Share By Base Map", "Geometry Map / Shade Normal / Bent Normal / SSS: bake one texture for all "
                                   + "materials under the Source Root that use the same Base Map (their UVs do not overlap) and assign "
                                   + "it to all of them. Features are switched on only for the selected materials. "
                                   + "Off = one texture per material (mostly empty, one per material). "
                                   + "Face SDF and Hair Flow are always per material",
                                   "Geometry Map / Shade Normal / Bent Normal / SSS を、Source Root 配下で同じ Base Map を使う材質"
                                   + "（UV が重ならない）ぜんぶで 1 枚に焼いて割り当てます。機能を ON にするのは選択中の材質だけです。"
                                   + "OFF = 材質ごとに 1 枚（大半が空白のテクスチャが材質の数だけできます）。"
                                   + "Face SDF と Hair Flow は常に材質ごとです"),
                        _shareByBaseMap);

                    // **メッシュの読み書きが要る。** ここで言わないと
                    // 「押しても何も起きない」で終わる。
                    EditorGUILayout.HelpBox(
                        jp ? "モデルの Read/Write Enabled が必要です。"
                             + "焼いた画像はマテリアルの隣の Baked フォルダに保存されます。"
                           : "The model needs Read/Write Enabled. "
                             + "Baked images are saved in a Baked folder next to the material.",
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
                {
                    _bakedGroups.Clear();
                    // 強度は Core が入れる（_ShadeNormalStrength）。選択していない材質のぶんは BakeShared が元に戻す
                    BakeAll(e, m => BakeShared(e, m, "ShadeNormal",
                        g => EasyPbrShadeNormalBaker.Bake(_bakeRoot, g, _shadeNormal), null, "_ShadeNormalStrength",
                        "_ShadeNormalMap"));
                }
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

                Note(jp, "Geometry Map の R に入ります。Cavity Strength が 0 なら 1 にします。",
                        "Goes into the R channel of the Geometry Map. Sets Cavity Strength to 1 if it is 0.");

                if (BakeButton(jp ? "Cavity をベイク" : "Bake Cavity"))
                {
                    _bakedGroups.Clear();
                    BakeAll(e, m => BakeGeometry(m, "Cavity", g => EasyPbrCavityBaker.Bake(_bakeRoot, g, _cavity), -1f));
                }
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
                    _kit.Label("Use Cast Shadow", "Include nose/brow cast shadows via raycasts",
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
                        _kit.Label("DF Spread", "Rounding radius in texels. "
                                   + "Higher = smoother, loses fine detail",
                                   "線の丸め半径（texel）。大きいほど滑らか・細部が消える"),
                        _faceSdf.dfSpread, 1f, 16f);
                    // Idol は 16bit 1ch の一方式だけ（T-382）。常に pack16 で焼く。
                    _faceSdf.pack16 = true;
                }

                // ---- プロキシ法線（T-414）--------------------------------------
                // ローポリの顔は法線がポリゴンごとに折れて等値線がガタつく。頭に合わせた楕円体か
                // プロキシメッシュの法線で遷移角を求めれば、線は完全に滑らかになる。UV は顔の
                // ものをそのまま使う（頂点位置からプロキシの法線を引くだけ）ので UV 合わせは不要。
                EditorGUILayout.Space(2);
                // 表示順は 自動（楕円体）/ メッシュの法線 / プロキシメッシュ。内部の mode は 1 / 0 / 2
                int[] modeOfIndex = { 1, 0, 2 };
                int curIndex = System.Array.IndexOf(modeOfIndex, _faceSdf.proxyMode);
                if (curIndex < 0) curIndex = 0;
                curIndex = EditorGUILayout.Popup(
                    _kit.Label("Proxy Mode", "Which normals define the shadow transition",
                               "影の遷移角をどの法線で決めるか"),
                    curIndex,
                    jp ? new[] { "自動（楕円体を顔に合わせる）", "メッシュの法線（従来）", "プロキシメッシュ" }
                       : new[] { "Auto (ellipsoid fitted to the face)", "Mesh normals (legacy)", "Proxy mesh" });
                _faceSdf.proxyMode = modeOfIndex[curIndex];
                if (_faceSdf.proxyMode != 0)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        _faceSdf.proxyBlend = EditorGUILayout.Slider(
                            _kit.Label("Proxy Blend", "1 = proxy normals only (smoothest). Lower to keep some "
                                       + "of the nose/brow shape from the real mesh",
                                       "1 でプロキシの法線だけ（最も滑らか）。下げると鼻や眉の形が少し戻ります"),
                            _faceSdf.proxyBlend, 0f, 1f);
                        if (_faceSdf.proxyMode == 1)
                        {
                            // 形のプリセット。選ぶと Taper / Flatten を入れ、下のスライダーで微調整できる
                            //（手で動かすと「カスタム」表示になる）
                            int shape = ShapeOfParams(_faceSdf.proxyTaper, _faceSdf.proxyFlatten);
                            int newShape = EditorGUILayout.Popup(
                                _kit.Label("Proxy Shape", "Ellipsoid = plain. Egg = narrower chin. Flat Front = front half "
                                           + "flattened, back stays round. Disc = front nearly planar with a rounded rim",
                                           "楕円体 = 素のまま。卵型 = 顎を細く。前を平ら = 前半分だけ平らに（後ろは丸いまま）。"
                                           + "円盤 = 正面がほぼ平面で縁が丸い"),
                                shape,
                                jp ? new[] { "楕円体", "卵型", "前を平ら", "円盤", "カスタム" }
                                   : new[] { "Ellipsoid", "Egg", "Flat Front", "Disc", "Custom" });
                            if (newShape != shape && newShape < 4)
                            {
                                _faceSdf.proxyTaper   = s_shapeTaper[newShape];
                                _faceSdf.proxyFlatten = s_shapeFlatten[newShape];
                            }
                            _faceSdf.proxyTaper = EditorGUILayout.Slider(
                                _kit.Label("Proxy Taper", "Egg shape: + narrows the chin and widens the top, - the opposite",
                                           "卵型。+ で顎が細く上が広く、− でその逆"),
                                _faceSdf.proxyTaper, -0.5f, 0.5f);
                            _faceSdf.proxyFlatten = EditorGUILayout.Slider(
                                _kit.Label("Proxy Flatten", "Flattens the front half (0 = round, 1 = disc-like). "
                                           + "The back half stays round",
                                           "前半分を平らに（0 = 丸い、1 = 円盤に近い）。後ろ半分は丸いまま"),
                                _faceSdf.proxyFlatten, 0f, 1f);
                        }
                        _faceSdf.proxyDetail = EditorGUILayout.Slider(
                            _kit.Label("Proxy Detail", "Brings the real mesh normals back only where they differ "
                                       + "strongly from the proxy (nose, brow, lips). Cheeks and forehead stay smooth",
                                       "プロキシから大きくずれる場所（鼻・眉・唇）だけ実際のメッシュの法線に戻します。"
                                       + "頬・額は滑らかなまま"),
                            _faceSdf.proxyDetail, 0f, 1f);
                        if (_faceSdf.proxyDetail > 0f)
                            _faceSdf.proxyDetailAngle = EditorGUILayout.Slider(
                                _kit.Label("Proxy Detail Angle", "Angle (deg) between mesh and proxy normal from which "
                                           + "the mesh normal is used. Smaller = more of the face uses the mesh",
                                           "メッシュとプロキシの法線の角度差がこれ（度）を超える場所からメッシュの法線を使う。"
                                           + "小さいほど顔の広い範囲がメッシュになる"),
                                _faceSdf.proxyDetailAngle, 10f, 80f);
                        if (_faceSdf.proxyMode == 2)
                        {
                            _sdfProxyObject = (GameObject)EditorGUILayout.ObjectField(
                                _kit.Label("Proxy Mesh", "A smooth head proxy (MeshFilter or SkinnedMeshRenderer) "
                                           + "placed over the head. Its own UVs are not used",
                                           "頭に重ねた滑らかなプロキシ（MeshFilter か SkinnedMeshRenderer）。プロキシ側の UV は使いません"),
                                _sdfProxyObject, typeof(GameObject), true);
                        }
                        bool wasManual = _sdfProxyManual;
                        _sdfProxyManual = EditorGUILayout.Toggle(
                            _kit.Label("Proxy Manual Fit", "Off = centre and radii are fitted to the face vertices "
                                       + "(least squares, outliers dropped). On = set them yourself, "
                                       + "starting from the auto-fitted values",
                                       "OFF = 中心と半径を顔の頂点に自動で合わせます（最小二乗・外れ値除去）。"
                                       + "ON = 手で指定（自動の値が最初に入ります）"),
                            _sdfProxyManual);
                        // ON にした瞬間に自動の値を入れておく（空から入力させない）
                        if (_sdfProxyManual && !wasManual) AutoFillSdfProxy(e.target as Material);
                    }
                }
                if (_faceSdf.proxyMode != 0 && _sdfProxyManual)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        _sdfProxyCenterTransform = (Transform)EditorGUILayout.ObjectField(
                            _kit.Label("Proxy Center Transform", "Transform at the head's centre (head bone). "
                                       + "Empty = fitted from the face mesh vertices (usually right)",
                                       "頭の中心の Transform（頭ボーン）。空なら顔メッシュの頂点から自動で合わせます（通常はこれで十分）"),
                            _sdfProxyCenterTransform, typeof(Transform), true);
                        if (_sdfProxyCenterTransform != null)
                            _sdfProxyOffset = EditorGUILayout.Vector3Field(
                                _kit.Label("Proxy Center Offset", "Offset from Proxy Center Transform, in its local axes (m)",
                                           "Proxy Center Transform からのずらし（そのローカル軸、m）"),
                                _sdfProxyOffset);
                        else
                            _sdfProxyCenterWS = EditorGUILayout.Vector3Field(
                                _kit.Label("Proxy Center WS", "Ellipsoid centre in world space (m)",
                                           "楕円体の中心（ワールド座標、m）"),
                                _sdfProxyCenterWS);
                        if (_faceSdf.proxyMode == 1)
                        {
                            _sdfProxyRadii = EditorGUILayout.Vector3Field(
                                _kit.Label("Proxy Radii", "Ellipsoid radii in metres (x = width, y = height, z = depth). "
                                           + "0 = fitted from the face mesh vertices when baking",
                                           "楕円体の半径（x = 幅、y = 高さ、z = 奥行き）。0 なら焼くときに顔メッシュの頂点から合わせます"),
                                _sdfProxyRadii);
                        }
                        if (GUILayout.Button(jp ? "自動算出（今の顔メッシュから）" : "Auto-fit from the face mesh"))
                            AutoFillSdfProxy(e.target as Material);
                    }
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
            ResolveSdfProxy(m);
            if (!EasyPbrFaceSdfBaker.Bake(_bakeRoot, m, _faceSdf)) return false;
            SetIfUnset(m, "_FaceFlatness", 1f);
            return true;
        }

        // 形のプリセット（Taper, Flatten）。楕円体 / 卵型 / 前を平ら / 円盤
        private static readonly float[] s_shapeTaper   = { 0f, 0.3f, 0f,   0f };
        private static readonly float[] s_shapeFlatten = { 0f, 0f,   0.5f, 1f };
        private static int ShapeOfParams(float taper, float flatten)
        {
            for (int i = 0; i < s_shapeTaper.Length; i++)
                if (Mathf.Abs(taper - s_shapeTaper[i]) < 1e-4f && Mathf.Abs(flatten - s_shapeFlatten[i]) < 1e-4f) return i;
            return 4;
        }

        /// <summary>
        /// 自動フィットの値を手動欄に入れる（T-414）。Baker と同じ頂点（バインドポーズをワールドへ写したもの）に
        /// 最小二乗の楕円体を当てる。Proxy Center Transform が指定済みならその Offset に、無ければワールド中心に入れる。
        /// </summary>
        private void AutoFillSdfProxy(Material m)
        {
            if (m == null || _bakeRoot == null) return;
            var pts = new System.Collections.Generic.List<Vector3>();
            foreach (var r in _bakeRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (System.Array.IndexOf(r.sharedMaterials, m) < 0) continue;
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                foreach (var v in mesh.vertices) pts.Add(r.transform.TransformPoint(v));
            }
            if (pts.Count == 0)
            {
                Debug.LogWarning("[EasyToon] この材質を使うメッシュが Source Root の下に見つかりません（Read/Write Enabled も確認）。");
                return;
            }
            if (!EasyPbrFaceSdfBaker.FitEllipsoid(pts.ToArray(), out var c, out var rad)) return;
            if (_sdfProxyCenterTransform != null) _sdfProxyOffset = _sdfProxyCenterTransform.InverseTransformPoint(c);
            else _sdfProxyCenterWS = c;
            _sdfProxyRadii = rad;
        }

        /// <summary>プロキシの中心・半径・メッシュをワールド空間で Settings に入れる（T-414）。</summary>
        // 中心・半径は Baker が「焼く頂点そのもの」から合わせるのが既定（Renderer.bounds はスキン後の
        // 姿勢の箱で、Baker が使うバインドポーズの頂点と 6 cm ずれた）。Proxy Center Transform を指定したときだけ
        // 中心をそれにする。**注意**: バインドポーズと今の姿勢がずれるモデルでは、頭ボーンの位置も
        // ずれるので自動のほうが正しい。
        private void ResolveSdfProxy(Material m)
        {
            if (_faceSdf.proxyMode == 0 || _bakeRoot == null) return;

            bool manualCenter = _sdfProxyManual && (_sdfProxyCenterTransform != null || _sdfProxyCenterWS.sqrMagnitude >= 1e-8f);
            bool manualRadii  = _sdfProxyManual && _sdfProxyRadii.sqrMagnitude >= 1e-8f;
            _faceSdf.proxyAutoCenter = !manualCenter;
            _faceSdf.proxyCenterWS   = !manualCenter ? Vector3.zero
                                     : (_sdfProxyCenterTransform != null ? _sdfProxyCenterTransform.TransformPoint(_sdfProxyOffset) : _sdfProxyCenterWS);
            _faceSdf.proxyAutoRadii  = !manualRadii;
            _faceSdf.proxyRadii      = manualRadii ? _sdfProxyRadii : Vector3.zero;

            _faceSdf.proxyMesh = null;
            _faceSdf.proxyMatrix = Matrix4x4.identity;
            if (_faceSdf.proxyMode == 2 && _sdfProxyObject != null)
            {
                var smr = _sdfProxyObject.GetComponentInChildren<SkinnedMeshRenderer>();
                var mf  = _sdfProxyObject.GetComponentInChildren<MeshFilter>();
                if (smr != null)
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked, true);
                    _faceSdf.proxyMesh = baked;
                    _faceSdf.proxyMatrix = smr.transform.localToWorldMatrix;
                }
                else if (mf != null && mf.sharedMesh != null)
                {
                    _faceSdf.proxyMesh = mf.sharedMesh;
                    _faceSdf.proxyMatrix = mf.transform.localToWorldMatrix;
                }
                else
                {
                    Debug.LogWarning("[EasyToon] Proxy Mesh に MeshFilter / SkinnedMeshRenderer が無いので、楕円体で焼きます。");
                    _faceSdf.proxyMode = 1;
                }
            }
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
                {
                    _bakedGroups.Clear();
                    BakeAll(e, m => BakeShared(e, m, "BentNormal",
                        g => EasyPbrBentNormalBaker.Bake(_bakeRoot, g, _bentNormal), "_BentNormalOn", null,
                        "_BentNormalMap"));
                }
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

                Note(jp, "Geometry Map の G に入ります。曲率の唯一の供給源で、Curvature Softness（陰・影タブ）が"
                       + "これを読んで曲がった面の境界を広げます。Curvature Softness が 0 なら 1 にします。",
                        "Goes into the G channel of the Geometry Map. The only curvature source - "
                      + "Curvature Softness (Shading tab) reads it to widen the transition on curved areas. "
                      + "Sets Curvature Softness to 1 if it is 0.");

                if (BakeButton(jp ? "Curvature をベイク" : "Bake Curvature"))
                {
                    _bakedGroups.Clear();
                    BakeAll(e, m => BakeGeometry(m, "Curvature",
                        g => EasyPbrCurvatureBaker.Bake(_bakeRoot, g, _curvature), -1f, "_CurvatureSoftness"));
                }
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
                {
                    _bakedGroups.Clear();
                    BakeAll(e, m => BakeShared(e, m, "SSS",
                        g => EasyPbrSssBaker.Bake(_bakeRoot, g, _sss), "_SSSMapStrength", null));
                }
            }
        }

        // ------------------------------------------------------------------
        //  自動アサインできないもの
        // ------------------------------------------------------------------
        private void DrawAo(MaterialEditor e, bool jp)
        {
            if (!Foldout(ref _aoOpen, jp, "Ambient Occlusion（遮蔽）", "Ambient Occlusion")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                // 焼いた AO は Geometry Map の B に自動で入る（T-422）。Mask Map（InstaMAT などで作ったもの）は上書きしない。
                Note(jp, "Geometry Map の B に入ります。Occlusion Source が Mask G のままなら Both（Mask Map の G と掛け合わせ）に"
                       + "します。Mask Map は上書きしません。",
                        "Goes into the B channel of the Geometry Map. If Occlusion Source is still Mask G it becomes Both "
                      + "(multiplied with the Mask Map's G). The Mask Map is never overwritten.");

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

                if (BakeButton(jp ? "AO をベイク" : "Bake AO"))
                {
                    _bakedGroups.Clear();
                    BakeAll(e, m => BakeGeometry(m, "AO", g => EasyPbrAoBaker.Bake(_bakeRoot, g, _ao), 2f));
                }
            }
        }

        // ------------------------------------------------------------------
        //  Geometry Map の節（Cavity / Curvature / AO を 1 枚に。T-426）
        // ------------------------------------------------------------------
        private void DrawGeometryGroup(MaterialEditor e, bool jp)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_kit.Section("bakegeometry", true, "Geometry Map", "Geometry Map（Cavity / Curvature / AO）", "", ""))
                    return;

                using (new EditorGUI.IndentLevelScope())
                {
                    Note(jp, "形状から決まるグレー 3 種（R Cavity / G Curvature / B Ambient Occlusion）を 1 枚に焼いて割り当てます。"
                           + "全マテリアルを選択して「まとめてベイク」を押すのが一番楽です。",
                            "Bakes the three shape-derived greys (R Cavity / G Curvature / B Ambient Occlusion) into one texture "
                          + "and assigns it. Selecting every material and pressing Bake All is the easiest way.");

                    _deleteGeometrySources = EditorGUILayout.Toggle(
                        _kit.Label("Delete Source Files", "After packing, delete the intermediate *_Cavity / *_Curvature / *_AO "
                                   + "images in the Baked folder. Re-baking one kind keeps the other channels from the existing "
                                   + "Geometry Map, so nothing is lost",
                                   "詰めた後、Baked フォルダの中間ファイル（*_Cavity / *_Curvature / *_AO）を削除します。"
                                   + "1 種類だけ焼き直しても、他のチャンネルは今の Geometry Map から引き継ぐので失われません"),
                        _deleteGeometrySources);

                    if (BakeButton(jp ? "Cavity / Curvature / AO をまとめてベイク" : "Bake All (Cavity + Curvature + AO)"))
                    {
                        _bakedGroups.Clear();
                        BakeAll(e, BakeGeometryAll);
                    }

                    EditorGUILayout.Space(4);
                    DrawCavity(e, jp);
                    DrawCurvature(e, jp);
                    DrawAo(e, jp);

                    EditorGUILayout.Space(4);
                    if (GUILayout.Button(jp ? "残っている中間ファイルから詰め直す（焼かない）"
                                            : "Repack from leftover source files (no baking)"))
                    {
                        _bakedGroups.Clear();
                        BakeAll(e, m => BakeGeometry(m, null, null, -1f));
                    }
                }
            }
        }

        /// <summary>
        /// スロットを持つマップ（Shade Normal / Bent Normal / SSS）を、同じ Base Map のグループで 1 枚に焼く（T-427）。
        /// テクスチャはグループ全員に割り当てる（Core の GroupScope）が、**機能を ON にするのは選択中の材質だけ**:
        /// 顔だけ選んで Shade Normal を焼いたら、同じ Base Map の耳や首で勝手に効き始める、を避ける。
        ///   enableProp   … パネルが立てる有効化プロパティ（0 のときだけ 1 に。選択中の材質のみ）
        ///   coreStrength … Core が全員に立ててしまう強度プロパティ（選択していない材質は元の値へ戻す）
        /// </summary>
        ///   normalMapSlot … 法線として読むスロット（Shade / Bent）。焼いた後に取り込みを Normal Map へ直す
        private bool BakeShared(MaterialEditor e, Material m, string kind, Func<Material, bool> bake,
                                string enableProp, string coreStrength, string normalMapSlot = null)
        {
            var group = _shareByBaseMap ? BaseMapGroupOf(m) : new[] { m };
            string groupName = group.Length > 1 ? BaseMapGroupNameOf(m) : null;
            if (!_bakedGroups.Add(kind + ":" + (groupName ?? ("mat:" + m.name)))) { _runDeduped = true; return true; }   // このボタンで処理済み

            var selected = new System.Collections.Generic.HashSet<UnityEngine.Object>(e.targets);
            var keep = new System.Collections.Generic.Dictionary<Material, float>();
            if (coreStrength != null)
                foreach (var g in group)
                    if (!selected.Contains(g) && g.HasProperty(coreStrength)) keep[g] = g.GetFloat(coreStrength);

            bool ok;
            using (EasyPbrBakeCore.GroupScope(group, groupName)) ok = bake(m);
            foreach (var kv in keep) kv.Key.SetFloat(coreStrength, kv.Value);
            if (!ok) return false;

            _runTextures++; _runAssigned += group.Length;
            if (normalMapSlot != null) ImportAsNormalMap(m, normalMapSlot);
            if (enableProp != null)
                foreach (var g in group) if (selected.Contains(g)) SetIfUnset(g, enableProp, 1f);
            if (group.Length > 1)
                Debug.Log($"[EasyToon] {kind}: Base Map '{groupName}' を共有する {group.Length} 材質に 1 枚を割り当てました"
                        + "（機能を ON にしたのは選択中の材質だけ）"
                        + (EasyPbrBakeCore.LastGroupOverlap > 0.02f
                            ? $"。**UV の重なり {EasyPbrBakeCore.LastGroupOverlap * 100f:0.#}%** ── Share By Base Map を切るか材質の Base Map を確認"
                            : "") + "。");
            return true;
        }

        /// <summary>
        /// 焼いた法線（Shade Normal / Bent Normal）の取り込みを Normal Map にする。
        /// Core のベイカーは全種類を Default・非圧縮で取り込むが、Idol は `UnpackNormal` で読み、プロパティも
        /// `[Normal]` なので、Default のままだとインスペクタに「Normal Map として取り込まれていません」の
        /// 警告が出る（利用者指摘）。Normal Map にすれば圧縮（PC は BC5）も効き、非圧縮の 1/4 になる。
        /// ベイカーの出力は RGB = 接空間の xyz（0.5 基準）で、通常の法線マップと同じ並び。
        /// </summary>
        private static void ImportAsNormalMap(Material m, string slot)
        {
            if (!m.HasProperty(slot)) return;
            var path = AssetDatabase.GetAssetPath(m.GetTexture(slot));
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter imp)) return;
            if (imp.textureType == TextureImporterType.NormalMap) return;
            imp.textureType = TextureImporterType.NormalMap;
            imp.sRGBTexture = false;
            imp.textureCompression = TextureImporterCompression.Compressed;
            imp.SaveAndReimport();
        }

        /// <summary>3 種をまとめて焼いて 1 回だけ詰める（グループごとに 1 度）。</summary>
        private bool BakeGeometryAll(Material m)
        {
            var group = _shareByBaseMap ? BaseMapGroupOf(m) : new[] { m };
            string groupName = group.Length > 1 ? BaseMapGroupNameOf(m) : null;
            if (!_bakedGroups.Add(groupName ?? ("mat:" + m.name))) { _runDeduped = true; return true; }

            bool ok;
            using (EasyPbrBakeCore.GroupScope(group, groupName))
                ok = EasyPbrCavityBaker.Bake(_bakeRoot, m, _cavity)
                  && EasyPbrCurvatureBaker.Bake(_bakeRoot, m, _curvature)
                  && EasyPbrAoBaker.Bake(_bakeRoot, m, _ao);
            if (!ok) return false;
            foreach (var g in group) SetIfUnset(g, "_CurvatureSoftness", 1f);
            if (!PackGeometryMap(group, groupName, 2f, _deleteGeometrySources)) return false;
            _runTextures++; _runAssigned += group.Length;   // Geometry Map は 3 種を詰めた 1 枚
            return true;
        }

        // ------------------------------------------------------------------
        //  ヘルパ
        // ------------------------------------------------------------------

        /// <summary>ベイクが成功したときだけ、Idol 側の有効化プロパティを立てる。</summary>
        // ------------------------------------------------------------------
        //  Geometry Map（R Cavity / G Curvature / B AO）に詰める（T-422）
        // ------------------------------------------------------------------
        /// <summary>
        /// 材質の `_CavityMap` / `_CurvatureMap` と、材質の隣の Baked フォルダにある `*_&lt;材質名&gt;_AO.png` を
        /// 1 枚（`*_Geometry.png`）に詰めて `_GeometryMap` に割り当て、`Geometry Map On` とキーワードを立てる。
        /// 無いチャンネルは中立（R 1 / G 0.5 / B 1）。Core のベイカーの出力はそのまま残す
        ///（1 つだけ焼き直したときに、他のチャンネルをここから拾い直せるように）。
        /// </summary>
        /// <summary>
        /// Geometry 系を 1 種類焼いて（bake が null なら焼かずに）Geometry Map に詰め直す。
        /// Share By Base Map のときは、m と同じ Base Map を使う Source Root 配下の Idol 材質をまとめて 1 枚にする。
        /// 同じグループは 1 回のボタンで 1 度だけ処理する（複数選択で同じアトラスを何度も焼かない）。
        /// </summary>
        private bool BakeGeometry(Material m, string kind, Func<Material, bool> bake, float occlusionSource,
                                  string setIfUnsetProp = null)
        {
            var group = _shareByBaseMap ? BaseMapGroupOf(m) : new[] { m };
            string groupName = group.Length > 1 ? BaseMapGroupNameOf(m) : null;
            string key = groupName ?? ("mat:" + m.name);
            if (!_bakedGroups.Add(key)) { _runDeduped = true; return true; }   // このボタンで処理済み

            if (bake != null)
            {
                bool ok;
                using (EasyPbrBakeCore.GroupScope(group, groupName)) ok = bake(m);
                if (!ok) return false;
                if (group.Length > 1)
                    Debug.Log($"[EasyToon] {kind}: Base Map '{groupName}' を共有する {group.Length} 材質を 1 枚に焼きました"
                            + (EasyPbrBakeCore.LastGroupOverlap > 0.02f
                                ? $"（**UV の重なり {EasyPbrBakeCore.LastGroupOverlap * 100f:0.#}%** ── Share By Base Map を切るか材質の Base Map を確認）"
                                : "") + "。");
            }
            if (setIfUnsetProp != null) foreach (var g in group) SetIfUnset(g, setIfUnsetProp, 1f);
            if (!PackGeometryMap(group, groupName, occlusionSource, _deleteGeometrySources)) return false;
            _runTextures++; _runAssigned += group.Length;
            return true;
        }

        /// <summary>m と同じ Base Map を使う、Source Root 配下の Idol 材質（m を含む）。Base Map が無ければ m だけ。</summary>
        private Material[] BaseMapGroupOf(Material m)
        {
            var atlas = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
            if (atlas == null || _bakeRoot == null) return new[] { m };
            var list = new System.Collections.Generic.List<Material> { m };
            foreach (var r in _bakeRoot.GetComponentsInChildren<Renderer>(true))
                foreach (var sm in r.sharedMaterials)
                    if (sm != null && sm != m && !list.Contains(sm) && sm.shader == m.shader
                        && sm.HasProperty("_BaseMap") && sm.GetTexture("_BaseMap") == atlas)
                        list.Add(sm);
            return list.ToArray();
        }

        private static string BaseMapGroupNameOf(Material m)
            => "Shared_" + (m.GetTexture("_BaseMap") != null ? m.GetTexture("_BaseMap").name : m.name);

        internal static bool PackGeometryMap(Material m, bool setOcclusionBoth = false)
            => PackGeometryMap(new[] { m }, null, setOcclusionBoth ? 2f : -1f, false);

        /// <summary>
        /// グループ（1 材質でもよい）の Cavity / Curvature / AO を 1 枚に詰めて、全材質に割り当てる。
        /// 元は Baked フォルダの `<メッシュ>_<グループ名 or 材質名>_<種類>.png`（Core のベイカーの出力）。
        /// 無ければ先頭の材質に残っている旧 `_CavityMap` / `_CurvatureMap` の参照（T-423 の残骸）から拾う。
        /// </summary>
        internal static bool PackGeometryMap(Material[] group, string groupName, float occlusionSource,
                                             bool deleteSources)
        {
            var m = group[0];
            if (m == null || !m.HasProperty("_GeometryMap")) return true;   // 旧シェーダーなら何もしない
            var bakedDir = BakedDirOf(m);
            string name = groupName ?? m.name;
            string cavityPath = FindBaked(bakedDir, name, "Cavity")    ?? LegacyTexPath(m, "_CavityMap");
            string curvPath   = FindBaked(bakedDir, name, "Curvature") ?? LegacyTexPath(m, "_CurvatureMap");
            string aoPath     = FindBaked(bakedDir, name, "AO");
            bool ok = PackGeometryMapFromPaths(group, cavityPath, curvPath, aoPath, occlusionSource);
            // 詰め終わったら中間ファイルを消す（Baked フォルダの中のものだけ。他所にある旧参照のファイルは触らない）
            if (ok && deleteSources)
                foreach (var p in new[] { cavityPath, curvPath, aoPath })
                    if (p != null && p.StartsWith(bakedDir + "/", StringComparison.Ordinal)) AssetDatabase.DeleteAsset(p);
            return ok;
        }

        private static string BakedDirOf(Material m)
        {
            var matPath = AssetDatabase.GetAssetPath(m);
            var dir = string.IsNullOrEmpty(matPath) ? "Assets" : Path.GetDirectoryName(matPath).Replace('\\', '/');
            return dir + "/Baked";
        }

        /// <summary>テクスチャから詰める（Migrator 用）。occlusionSource &lt; 0 なら触らない。</summary>
        internal static bool PackGeometryMapFromTextures(Material m, Texture cavity, Texture curvature, Texture ao,
                                                      float occlusionSource)
            => PackGeometryMapFromPaths(new[] { m }, PngPath(cavity), PngPath(curvature), PngPath(ao), occlusionSource);

        private static bool PackGeometryMapFromPaths(Material[] group, string cavityPath, string curvPath, string aoPath,
                                                  float occlusionSource)
        {
            var m = group[0];
            if (m == null || !m.HasProperty("_GeometryMap")) return true;
            var bakedDir = BakedDirOf(m);
            var dir = bakedDir.Substring(0, bakedDir.Length - "/Baked".Length);
            if (cavityPath == null && curvPath == null && aoPath == null)
            {
                Debug.LogWarning($"[EasyToon] '{m.name}': 詰める元（Cavity / Curvature / AO）が見つかりません。");
                return false;
            }

            var cav = LoadGray(cavityPath); var cur = LoadGray(curvPath); var ao = LoadGray(aoPath);
            // 元が無いチャンネルは、今の Geometry Map から引き継ぐ（中間ファイルを消していても、
            // 1 種類だけ焼き直して他を失わないため）。それも無ければ中立（R 1 / G 0.5 / B 1）。
            var prev = LoadGray(PngPath(m.GetTexture("_GeometryMap")));
            int size = Mathf.Max(cav?.width ?? 0, Mathf.Max(cur?.width ?? 0, ao?.width ?? 0));
            if (size == 0 && prev != null) size = prev.width;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    var old = prev != null ? prev.GetPixelBilinear(u, v) : new Color(1f, 0.5f, 1f, 1f);
                    px[y * size + x] = new Color(
                        cav != null ? cav.GetPixelBilinear(u, v).r : old.r,
                        cur != null ? cur.GetPixelBilinear(u, v).r : old.g,
                        ao  != null ? ao.GetPixelBilinear(u, v).r  : old.b, 1f);
                }
            foreach (var t in new[] { cav, cur, ao, prev }) if (t != null) UnityEngine.Object.DestroyImmediate(t);

            var outTex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            outTex.SetPixels(px); outTex.Apply(false, false);
            if (!AssetDatabase.IsValidFolder(bakedDir)) AssetDatabase.CreateFolder(dir, "Baked");
            string stem = Path.GetFileNameWithoutExtension(cavityPath ?? curvPath ?? aoPath);
            int cut = stem.LastIndexOf('_');
            string outPath = $"{bakedDir}/{(cut > 0 ? stem.Substring(0, cut) : stem)}_Geometry.png";
            // 既に Geometry Map があるならその場所へ上書きする（GUID を保ち、割り当て済みの参照を生かす）
            var existing = PngPath(m.GetTexture("_GeometryMap"));
            if (existing != null && existing.StartsWith(bakedDir + "/", StringComparison.Ordinal)) outPath = existing;
            File.WriteAllBytes(outPath, outTex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(outTex);
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(outPath) is TextureImporter imp)
            {
                imp.sRGBTexture = false;
                imp.textureType = TextureImporterType.Default;
                // 低周波のグレー 3 種なので高品質圧縮（BC7）で足りる。非圧縮の 1/4（T-425）
                imp.textureCompression = TextureImporterCompression.CompressedHQ;
                imp.SaveAndReimport();
            }

            var packed = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            foreach (var t in group)
            {
                if (t == null || !t.HasProperty("_GeometryMap")) continue;
                Undo.RecordObject(t, "Pack Geometry Map");
                t.SetTexture("_GeometryMap", packed);
                t.SetFloat("_GeometryMapOn", 1f);
                t.EnableKeyword("_GEOMETRYMAP_ON");
                // AO を入れた直後だけ出どころを切り替える（自分で選んでいる人の値は触らない = Mask G のときだけ）
                if (occlusionSource >= 0f && aoPath != null && t.HasProperty("_OcclusionSource")
                    && t.GetFloat("_OcclusionSource") < 0.5f)
                    t.SetFloat("_OcclusionSource", occlusionSource);
                EditorUtility.SetDirty(t);
            }
            Debug.Log($"[EasyToon] Geometry Map を詰めました（{group.Length} 材質に割り当て）→ {outPath}"
                    + $"（R Cavity {(cavityPath != null ? "○" : "中立")} / G Curvature {(curvPath != null ? "○" : "中立")}"
                    + $" / B AO {(aoPath != null ? "○" : "中立")}）");
            return true;
        }

        private static string PngPath(Texture t)
        {
            var p = t != null ? AssetDatabase.GetAssetPath(t) : null;
            return string.IsNullOrEmpty(p) || !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? null : p;
        }

        // シェーダーから消えたプロパティでも、材質ファイルには参照が残る（m_SavedProperties.m_TexEnvs）。
        // GetTexture は HasProperty が false だと使えないので、シリアライズ済みの値を直接読む。
        private static string LegacyTexPath(Material m, string slot)
        {
            var envs = new SerializedObject(m).FindProperty("m_SavedProperties.m_TexEnvs");
            if (envs == null) return null;
            for (int i = 0; i < envs.arraySize; i++)
            {
                var e = envs.GetArrayElementAtIndex(i);
                if (e.FindPropertyRelative("first").stringValue != slot) continue;
                return PngPath(e.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture);
            }
            return null;
        }

        /// <summary>
        /// 個別の Cavity Map / Curvature Map を使っていた Idol の材質を、まとめて Geometry Map へ移す（T-423）。
        /// 既に Geometry Map がある材質は触らない。
        /// </summary>
        [MenuItem("Tools/Idol/Cavity・Curvature を Geometry Map へ移行")]
        private static void MigrateLegacyToGeometryMap()
        {
            int done = 0, skipped = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m == null || !m.HasProperty("_GeometryMap")) continue;
                if (m.GetTexture("_GeometryMap") != null) { skipped++; continue; }
                var bakedDir = BakedDirOf(m);
                bool any = LegacyTexPath(m, "_CavityMap") != null || LegacyTexPath(m, "_CurvatureMap") != null
                        || FindBaked(bakedDir, m.name, "Cavity") != null || FindBaked(bakedDir, m.name, "Curvature") != null
                        || FindBaked(bakedDir, m.name, "AO") != null;
                if (any && PackGeometryMap(m)) done++;
            }
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("EasyToon / Idol",
                $"{done} 個の材質を Geometry Map へ移行しました（既に Geometry Map がある {skipped} 個は対象外）。詳細は Console。", "OK");
        }

        // Core のベイカーの命名: `<メッシュ名>_<材質名>_<種類>.png`（Sanitize 済み）
        private static string FindBaked(string bakedDir, string matName, string kind)
        {
            if (!AssetDatabase.IsValidFolder(bakedDir)) return null;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { bakedDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var stem = Path.GetFileNameWithoutExtension(p);
                // Core と同じ規則（ファイル名に使えない文字だけ '_'）で材質名を直し、末尾一致で見る
                //（`31._2` と `31._2x` を取り違えない）
                string safe = matName;
                foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                if (stem.EndsWith("_" + safe + "_" + kind, StringComparison.Ordinal))
                    return p;
            }
            return null;
        }

        // インポート設定（Read/Write）に依らず読めるよう、PNG をファイルから直接読む
        private static Texture2D LoadGray(string assetPath)
        {
            if (assetPath == null) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            return t.LoadImage(File.ReadAllBytes(assetPath)) ? t : null;
        }

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
            // **「何個の材質に焼いたか」ではなく「何枚焼いて、何個に割り当てたか」を言う。**
            // Share By Base Map では 46 材質を選んでも焼くのは 8 枚で、以前の「46 個中 46 個にベイクしました」は
            // テクスチャが 46 枚できたように読めた（利用者指摘）。
            int total = 0, failed = 0;
            _runTextures = 0; _runAssigned = 0;
            foreach (var o in editor.targets)
            {
                if (!(o is Material m)) continue;
                total++;
                int texBefore = _runTextures;
                _runDeduped = false;
                bool ok = bakeOne(m);
                if (!ok) { failed++; continue; }
                // グループを使わないベイカー（Face SDF / Hair Flow）は自分では数えない → 1 枚・1 材質
                if (!_runDeduped && _runTextures == texBefore) { _runTextures++; _runAssigned++; }
            }
            if (total > 1 || _runAssigned > 1)
                EditorUtility.DisplayDialog("EasyToon / Idol Baker",
                    _kit.Jp ? $"テクスチャを {_runTextures} 枚焼き、{_runAssigned} 個のマテリアルに割り当てました"
                              + $"（選択 {total} 個" + (failed > 0 ? $"、失敗 {failed} 個" : "") + "）。詳細は Console。"
                            : $"Baked {_runTextures} texture(s) and assigned them to {_runAssigned} material(s) "
                              + $"({total} selected" + (failed > 0 ? $", {failed} failed" : "") + "). See Console.", "OK");
        }
    }
}

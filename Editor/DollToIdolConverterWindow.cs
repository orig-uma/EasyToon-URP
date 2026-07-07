// =============================================================================
//  DollToIdolConverterWindow.cs
// -----------------------------------------------------------------------------
//  EasyPBR(Doll) マテリアルを EasyToon(Idol) へ変換する EditorWindow。
//  メニュー: Window > Origuma > Doll to Idol Converter
//
//  変換内容（1 マテリアルずつ・Undo 対応）:
//   1. シェーダーを Origuma/EasyToon_URP/Idol へ差し替え
//      （Doll と同名のプロパティは Unity が名前で自動保持 → MIGRATION.md）
//   2. Doll の Alpha Clip 状態（_AlphaClip / _ALPHATEST_ON）を Idol の
//      Render Mode（Opaque/Cutout）へ IdolMaterialSetup.ApplyRenderMode で変換
//   3. マテリアル名から推定した Chara Part（変換前にプレビュー・個別修正可）で
//      IdolMaterialSetup.ApplyCharaPart（Stencil / Queue / HairSeeThrough パス）
//   4. キーワード引き継ぎ（_UseDissolve の値から _DISSOLVE_ON を再同期）
//
//  「複製を作って変換」（元マテリアル温存・_Idol サフィックス）と
//  「その場で変換」を選択できる。
//
//  ※ EasyPBR パッケージへのコード依存はない（Doll はシェーダー名文字列
//    "Origuma/EasyPBR_URP/Doll" でのみ参照）。EasyPBR がプロジェクトに無い場合は
//    変換対象が見つからないだけで、コンパイル・動作に支障はない。
// =============================================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Origuma.EasyToon.URP.Editor
{
    public class DollToIdolConverterWindow : EditorWindow
    {
        private const string DollShaderName = "Origuma/EasyPBR_URP/Doll";
        private const string IdolShaderName = "Origuma/EasyToon_URP/Idol";

        // 1 マテリアルぶんの変換予定エントリ（Part はドロップダウンで修正可能）。
        private class Entry
        {
            public Material material;
            public int charaPart;     // 0=Body,1=Face,2=Brow,3=Hair,4=Eye
            public string result;     // 変換後の結果表示
        }

        private static readonly string[] s_PartLabels = { "Body", "Face", "Brow", "Hair", "Eye" };

        private readonly List<Entry> _entries = new List<Entry>();
        private GameObject _root;
        private bool _duplicate = true; // true=複製を作って変換 / false=その場で変換
        private Vector2 _scroll;

        [MenuItem("Window/Origuma/Doll to Idol Converter")]
        public static void Open()
        {
            var window = GetWindow<DollToIdolConverterWindow>(false, "Doll to Idol");
            window.minSize = new Vector2(460, 360);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Doll → Idol マテリアル変換", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Doll と同名のプロパティ（色・テクスチャ・数値）はシェーダー差し替えで自動的に引き継がれます" +
                "（対応表: Documentation~/MIGRATION.md）。Chara Part はマテリアル名から推定されるので、" +
                "変換前にリストで確認・修正してください。",
                MessageType.Info);

            // --- 対象の収集 ---
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                _root = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Root (GameObject)", "配下の Renderer が持つ Doll マテリアルを列挙"),
                    _root, typeof(GameObject), true);
                if (GUILayout.Button("Root から収集", GUILayout.Width(110)))
                    CollectFromRoot();
            }
            if (GUILayout.Button("選択中のマテリアルから収集"))
                CollectFromSelection();

            // --- 変換予定リスト（Part 修正可） ---
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"変換対象: {_entries.Count} 件", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var entry in _entries)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(entry.material, typeof(Material), false);
                    entry.charaPart = EditorGUILayout.Popup(entry.charaPart, s_PartLabels, GUILayout.Width(70));
                    EditorGUILayout.LabelField(entry.result ?? "", GUILayout.Width(120));
                }
            }
            EditorGUILayout.EndScrollView();

            // --- 変換モード / 実行 ---
            EditorGUILayout.Space(4);
            _duplicate = GUILayout.Toolbar(_duplicate ? 0 : 1,
                new[] { "複製を作って変換（元を温存）", "その場で変換" }) == 0;

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(_entries.Count == 0))
            {
                if (GUILayout.Button($"変換を実行（{_entries.Count} 件）", GUILayout.Height(30)))
                    ConvertAll();
            }
        }

        // ------------------------------------------------------------------
        //  対象収集
        // ------------------------------------------------------------------
        private void CollectFromSelection()
        {
            _entries.Clear();
            foreach (var obj in Selection.objects)
                if (obj is Material mat && IsDoll(mat))
                    AddEntry(mat);
            if (_entries.Count == 0)
                ShowNotification(new GUIContent("選択中に Doll マテリアルがありません"));
        }

        private void CollectFromRoot()
        {
            _entries.Clear();
            if (_root == null)
            {
                ShowNotification(new GUIContent("Root を指定してください"));
                return;
            }
            foreach (var renderer in _root.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in renderer.sharedMaterials)
                    if (IsDoll(mat))
                        AddEntry(mat);
            if (_entries.Count == 0)
                ShowNotification(new GUIContent("配下に Doll マテリアルがありません"));
        }

        private void AddEntry(Material mat)
        {
            foreach (var e in _entries)
                if (e.material == mat) return; // 重複除外
            _entries.Add(new Entry { material = mat, charaPart = GuessCharaPart(mat.name) });
        }

        private static bool IsDoll(Material mat)
            => mat != null && mat.shader != null && mat.shader.name == DollShaderName;

        // マテリアル名から Chara Part を推定（プレビューで修正可能な初期値）。
        //  ※ "EYELASH" 等は "EYE" を含むため、Brow 系キーワードを Eye より先に判定する。
        internal static int GuessCharaPart(string materialName)
        {
            var n = materialName.ToUpperInvariant();
            if (n.Contains("BROW") || n.Contains("EYELASH") || n.Contains("EYELINE") || n.Contains("MAYU"))
                return 2; // Brow
            if (n.Contains("EYE") || n.Contains("HITOMI"))
                return 4; // Eye
            if (n.Contains("HAIR") || n.Contains("KAMI"))
                return 3; // Hair
            if (n.Contains("FACE"))
                return 1; // Face
            return 0;     // Body
        }

        // ------------------------------------------------------------------
        //  変換本体
        // ------------------------------------------------------------------
        private void ConvertAll()
        {
            var idolShader = Shader.Find(IdolShaderName);
            if (idolShader == null)
            {
                EditorUtility.DisplayDialog("Doll to Idol",
                    $"Idol シェーダーが見つかりません: {IdolShaderName}", "OK");
                return;
            }

            int ok = 0;
            foreach (var entry in _entries)
            {
                if (entry.material == null) { entry.result = "失敗（null）"; continue; }

                Material target = entry.material;
                if (_duplicate)
                {
                    target = DuplicateMaterial(entry.material);
                    if (target == null) { entry.result = "失敗（複製）"; continue; }
                }

                ConvertOne(target, idolShader, entry.charaPart);
                entry.result = _duplicate ? $"複製OK → {target.name}" : "変換OK";
                ok++;
                Debug.Log($"[EasyToon] Doll→Idol 変換: {entry.material.name} → {target.name} " +
                          $"(Chara Part: {s_PartLabels[entry.charaPart]}, " +
                          $"{(_duplicate ? "複製" : "その場")}) : {AssetDatabase.GetAssetPath(target)}");
            }

            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Doll to Idol",
                $"{_entries.Count} 件中 {ok} 件を変換しました（詳細は Console）。", "OK");
        }

        private static void ConvertOne(Material mat, Shader idolShader, int charaPart)
        {
            Undo.RecordObject(mat, "Convert Doll to Idol");

            // シェーダー差し替え前に Doll 側の状態を控える。
            bool cutout = (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f)
                          || mat.IsKeywordEnabled("_ALPHATEST_ON");
            bool dissolveOn = mat.HasProperty("_UseDissolve") && mat.GetFloat("_UseDissolve") > 0.5f;

            // 1. シェーダー差し替え（同名プロパティは Unity が名前で自動保持）。
            mat.shader = idolShader;

            // 2. Render Mode（Opaque/Cutout。キーワード・RenderType を含む）。
            IdolMaterialSetup.ApplyRenderMode(mat, cutout ? 1 : 0);

            // 3. Chara Part プリセット（Stencil / Queue / HairSeeThrough パス有効化）。
            IdolMaterialSetup.ApplyCharaPart(mat, charaPart);

            // 4. キーワード引き継ぎ（_UseDissolve は同名なので値は保持済み。
            //    シェーダー差し替えでキーワードが落ちる場合に備えて再同期）。
            if (mat.HasProperty("_UseDissolve"))
                mat.SetFloat("_UseDissolve", dissolveOn ? 1f : 0f);
            IdolMaterialSetup.SyncKeywords(mat);

            EditorUtility.SetDirty(mat);
        }

        // 元マテリアルの隣に "_Idol" サフィックスで複製アセットを作る。
        private static Material DuplicateMaterial(Material source)
        {
            var srcPath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(srcPath))
            {
                Debug.LogWarning($"[EasyToon] アセットではないため複製できません: {source.name}");
                return null;
            }
            var dir = System.IO.Path.GetDirectoryName(srcPath).Replace('\\', '/');
            var newPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{source.name}_Idol.mat");

            var copy = new Material(source) { name = System.IO.Path.GetFileNameWithoutExtension(newPath) };
            AssetDatabase.CreateAsset(copy, newPath);
            Undo.RegisterCreatedObjectUndo(copy, "Duplicate Doll Material");
            return copy;
        }
    }
}

// =============================================================================
//  ToonRampAssetEditor.cs — ランプアセットの Inspector（T-398）
// -----------------------------------------------------------------------------
//  ToonRampAsset を Project ビューで選んだときの編集画面。Gradient の編集は
//  マテリアル側と同じく即時反映（埋め込みテクスチャを焼き直す）。
//  「PNG に書き出す」はここにだけ置く ── 書き出しはアセットの機能であって、
//  材質の Inspector に居る理由が無い（利用者判断）。
// =============================================================================
using UnityEditor;
using UnityEngine;

namespace ToonNPR.EditorTools
{
    [CustomEditor(typeof(ToonRampAsset))]
    internal class ToonRampAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var asset = (ToonRampAsset)target;
            bool jp = EditorPrefs.GetBool("Origuma.EasyToon.URP.Idol.lang.jp", true);

            EditorGUILayout.LabelField(jp ? "プリセット" : "Presets", EditorStyles.miniLabel);
            var presets = ToonPBRRampGenerator.Presets;
            for (int i = 0; i < presets.Length; i += 4)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int j = i; j < i + 4 && j < presets.Length; j++)
                    {
                        var pr = presets[j];
                        if (GUILayout.Button(jp ? pr.Jp : pr.En, EditorStyles.miniButton))
                            ToonPBRRampGenerator.SetGradient(asset, pr.Build());
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            var edited = EditorGUILayout.GradientField(
                new GUIContent("Gradient (left = shadow)"), asset.gradient);
            if (EditorGUI.EndChangeCheck())
                ToonPBRRampGenerator.SetGradient(asset, edited);

            if (asset.texture != null)
            {
                var r = GUILayoutUtility.GetRect(10, 14, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(r, asset.texture, ScaleMode.StretchToFill);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button(jp ? "PNG に書き出す（他ツール・他プロジェクト用）"
                                   : "Export PNG (for other tools / projects)"))
            {
                var p = ToonPBRRampGenerator.ExportPng(asset);
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(p));
            }
            EditorGUILayout.HelpBox(jp
                ? "編集はこのランプを参照する全マテリアルにその場で反映され、Undo できます。"
                : "Edits apply immediately to every material referencing this ramp and can be undone.",
                MessageType.None);
        }
    }
}

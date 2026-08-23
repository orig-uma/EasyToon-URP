using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Origuma.EasyShaderCore.Editor;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// T-340 のアセット一括適用を batchmode で行う入口（Selection 非依存）。
    ///
    ///   Unity -batchmode -quit -nographics -projectPath . \
    ///         -executeMethod ToonNPR.EditorTools.IdolBatchApplyCI.RunAllCI -logFile -
    ///
    /// メニュー版（ToonPBRSurfaceTypeFromName / ToonPBRDropDeadWork）と同じ判定を
    /// **同じコードで**通す（internal 共有。複製は必ずずれる ── T-107）。
    /// メニュー版が確認ダイアログで見せる「目覚める値」一覧は、こちらでは
    /// **ログに出す**（適用後にレビューする前提。Editor を開けない環境向け）。
    ///
    /// 実行順は「絵が変わらないものが先」（BACKLOG の鉄則）:
    ///   A. HairSeeThrough の空振り停止（絵は不変・draw 減）
    ///   B. _OutlineOn を 0 へ（Feature 未導入で元々描かれていない ── 絵は不変）
    ///   C. ToonOutlineFeature を PC_Renderer へ追加（材質側が全 0 なので絵は不変）
    ///   D. サーフェスタイプを名前から設定（**ここだけ絵が変わる**）
    ///   E. 絵に出ない計算を止める（数学的にゼロの計算 ── 絵は不変）
    ///   F. 旧世代ツールの残骸をゴミ箱へ（Assets/ShaderTools・Assets/ToonPBR）
    /// </summary>
    public static class IdolBatchApplyCI
    {
        private const string Marker = "[IdolBatchApply]";
        private const string PcRendererPath = "Assets/Settings/PC_Renderer.asset";

        public static void RunAllCI()
        {
            int failures;
            try
            {
                failures = RunAll();
            }
            catch (Exception e)
            {
                Debug.LogError($"{Marker} 例外で中断: {e}");
                EditorApplication.Exit(2);
                return;
            }

            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static int RunAll()
        {
            int failures = 0;

            // Hidden シェーダーは対象外。メニュー版は Selection 経由なので
            // 混入しないが、全走査ではここで弾かないと blit 用の材質まで触りうる。
            var mats = AssetDatabase.FindAssets("t:Material")
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null && m.shader != null
                            && m.shader.name.Contains("Idol")
                            && !m.shader.name.StartsWith("Hidden/"))
                .Distinct().ToArray();

            if (mats.Length == 0)
            {
                Debug.LogError($"{Marker} Idol のマテリアルが 1 個も見つからない。" +
                               "0 件適用を成功と言わない（T-252 と同じ理由）。");
                return 1;
            }
            Debug.Log($"{Marker} 対象 {mats.Length} 件（Idol シェーダー使用・プロジェクト全域）");

            // ---- A. HairSeeThrough の空振り停止（メニュー版と同じ判定・同じ関数） ----
            var off = mats.Where(m =>
                    m.GetShaderPassEnabled(ToonPBRSurfaceTypeFromName.kSeeThroughPass)
                    && !ToonPBRSurfaceTypeFromName.UsesSeeThrough(m)).ToArray();
            foreach (var m in off)
            {
                m.SetShaderPassEnabled(ToonPBRSurfaceTypeFromName.kSeeThroughPass, false);
                EditorUtility.SetDirty(m);
            }
            Debug.Log($"{Marker} A: HairSeeThrough を {off.Length} 件で停止" +
                      $"（透過を使う髪 {mats.Count(ToonPBRSurfaceTypeFromName.UsesSeeThrough)} 件は維持）");

            // ---- B. _OutlineOn を 0 へ（Feature 導入前に揃える ── 見た目を変えないため） ----
            int outlineZeroed = 0;
            foreach (var m in mats)
            {
                if (!m.HasFloat("_OutlineOn") || m.GetFloat("_OutlineOn") <= 0.5f) continue;
                m.SetFloat("_OutlineOn", 0f);
                m.DisableKeyword("_OUTLINE_ON");   // 値とキーワードは常に一緒に動かす
                EditorUtility.SetDirty(m);
                outlineZeroed++;
            }
            Debug.Log($"{Marker} B: _OutlineOn を {outlineZeroed} 件で 0 にした");

            // ---- C. ToonOutlineFeature を PC_Renderer へ ----
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(PcRendererPath);
            if (data == null)
            {
                Debug.LogError($"{Marker} C: Renderer が見つからない: {PcRendererPath}");
                failures++;
            }
            else if (FeatureSetup.FindFeature<ToonOutlineFeature>(data) != null)
            {
                Debug.Log($"{Marker} C: ToonOutlineFeature は導入済み（何もしない）");
            }
            else
            {
                FeatureSetup.AddFeature<ToonOutlineFeature>(data, "Toon Outline");
                bool ok = FeatureSetup.FindFeature<ToonOutlineFeature>(data) != null;
                if (!ok) failures++;
                Debug.Log($"{Marker} C: ToonOutlineFeature を {PcRendererPath} へ追加 " +
                          $"{(ok ? "済み" : "**失敗**")}（Mobile は全材質 OFF のため対象外）");
            }

            // ---- D. サーフェスタイプを名前から設定（メニュー版と同じ判定・同じ Guess） ----
            var plan = new List<(Material Mat, int Want)>();
            int alreadySet = 0, noHint = 0;
            foreach (var m in mats)
            {
                int want = ToonPBRSurfaceTypeFromName.Guess(m.name);
                if (want < 0 || !m.HasProperty("_SurfaceType")) { noHint++; continue; }
                int got = Mathf.RoundToInt(m.GetFloat("_SurfaceType"));
                if (got == want || got != 0) { alreadySet++; continue; }   // Default 以外は意図とみなす
                plan.Add((m, want));
            }

            if (plan.Count > 0)
            {
                // メニュー版がダイアログで見せる「目覚める値／止まる値」をログへ。
                // 適用後の見た目レビューの手引きになる（batchmode では事前確認ができない）。
                Debug.Log($"{Marker} D: 目覚める値の一覧（メニュー版の確認ダイアログと同内容）:" +
                          ToonPBRSurfaceTypeFromName.WakeWarning(plan));

                foreach (var (mat, want) in plan)
                {
                    mat.SetFloat("_SurfaceType", want);
                    foreach (var k in ToonPBRSurfaceTypeFromName.kSurfaceKw) mat.DisableKeyword(k);
                    mat.EnableKeyword(ToonPBRSurfaceTypeFromName.kSurfaceKw[Mathf.Clamp(want, 0, 4)]);
                    EditorUtility.SetDirty(mat);
                }
            }
            Debug.Log($"{Marker} D: サーフェスタイプを {plan.Count} 件設定" +
                      $"（設定済み {alreadySet} / 名前に手掛かり無し {noHint}）**絵が変わる操作はここだけ**");

            // ---- E. 絵に出ない計算を止める（メニュー版と同じゲート表） ----
            int dropped = 0, kept = 0;
            foreach (var m in mats)
            {
                foreach (var g in ToonPBRDropDeadWork.kGates)
                {
                    if (!m.HasProperty(g.Strength) || !m.HasProperty(g.Tex)) continue;
                    if (m.GetFloat(g.Strength) <= 0f) continue;
                    if (m.GetTexture(g.Tex) != null) { kept++; continue; }   // 効いている設定は触らない
                    m.SetFloat(g.Strength, 0f);
                    EditorUtility.SetDirty(m);
                    dropped++;
                }
            }
            Debug.Log($"{Marker} E: 絵に出ない計算を {dropped} 件止めた（効いている設定 {kept} 件は維持）");

            // ---- F. 旧世代ツールの残骸 ── ゴミ箱へ（復元可能な削除。利用者の承認済み） ----
            foreach (var path in new[] { "Assets/ShaderTools", "Assets/ToonPBR" })
            {
                if (!AssetDatabase.IsValidFolder(path))
                {
                    Debug.Log($"{Marker} F: {path} は既に無い");
                    continue;
                }
                bool trashed = AssetDatabase.MoveAssetToTrash(path);
                if (!trashed) failures++;
                Debug.Log($"{Marker} F: {path} をゴミ箱へ {(trashed ? "移動" : "**失敗**")}");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"{Marker} 完了: 対象 {mats.Length} 件 / A:{off.Length} B:{outlineZeroed} " +
                      $"D:{plan.Count} E:{dropped} / 失敗 {failures}");
            return failures;
        }
    }
}

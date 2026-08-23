using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// **絵に一切出ない計算**を止める（T-336）。
    ///
    /// 強度が 0 でないのにテクスチャが未割り当ての機能は、
    /// 既定テクスチャが中立色なので**結果が数学的にゼロ**になる。
    /// それでも `UNITY_BRANCH` の条件は真なので、**中身は毎画素実行される** ──
    /// テクスチャフェッチを含めて、絵に出ないまま代金だけ払っている。
    ///
    /// 実測（`hlsl_compile.py --branch-cost`、ForwardLit フラグメント）:
    ///
    ///   _MatCapIntensity   26 命令   46 件すべて ON なのに 66 件で未割り当て
    ///   _CavityStrength    12 命令   28 件 ON のうち 20 件が未割り当て
    ///
    /// **絵は 1 bit も変わらない。** 式で確かめてある:
    ///
    ///   MatCap: `tex * _MatCapColor * _MatCapIntensity`
    ///           `_MatCapTex` の既定は `"black"` → `tex = 0` → 強度に関わらず 0
    ///   Cavity: `cavity = lerp(1.0, raw, _CavityStrength); albedo *= cavity`
    ///           `_CavityMap` の既定は `"white"` → `raw = 1` → 常に ×1
    ///
    /// **テクスチャが割り当ててあるものには触らない。** 効いている設定を
    /// 勝手に 0 にすると「機能を殺す安全装置」になる。
    ///
    /// 逆向きの注意も要る ── ここで 0 にした後にテクスチャを割り当てると
    /// 「割り当てたのに出ない」に見える。インスペクタ側で
    /// **テクスチャが在るのに強度が 0** を警告している（ToonPBRShaderGUI）。
    ///
    /// Undo で戻せる。
    /// </summary>
    public static class ToonPBRDropDeadWork
    {
        /// <summary>強度プロパティ → 対になるテクスチャ／説明。</summary>
        // internal: IdolBatchApplyCI（batchmode 一括適用）と共有する（T-107 対策）。
        internal struct Gate
        {
            public string Strength;
            public string Tex;
            public string Why;
            public int Cost;
        }

        internal static readonly Gate[] kGates =
        {
            new Gate { Strength = "_MatCapIntensity", Tex = "_MatCapTex",  Cost = 26,
                       Why = "既定 \"black\" を掛けるので寄与は 0" },
            new Gate { Strength = "_CavityStrength",  Tex = "_CavityMap",  Cost = 12,
                       Why = "既定 \"white\" なので乗算は常に ×1" },
        };

        [MenuItem("Tools/Idol/絵に出ない計算を止める")]
        private static void DropDeadWork()
        {
            var mats = Selection.objects.OfType<Material>()
                .Concat(Selection.gameObjects
                    .SelectMany(g => g.GetComponentsInChildren<Renderer>(true))
                    .SelectMany(r => r.sharedMaterials))
                .Where(m => m != null && m.shader != null && m.shader.name.Contains("Idol"))
                .Distinct().ToArray();

            if (mats.Length == 0)
            {
                EditorUtility.DisplayDialog("絵に出ない計算",
                    "Idol のマテリアル、またはキャラのルートを選択してください。", "OK");
                return;
            }

            // (マテリアル, ゲート) の組。**テクスチャが在るものは入れない。**
            var plan = new List<(Material Mat, Gate G)>();
            int assigned = 0;
            foreach (var m in mats)
            {
                foreach (var g in kGates)
                {
                    if (!m.HasProperty(g.Strength) || !m.HasProperty(g.Tex))
                        continue;
                    if (m.GetFloat(g.Strength) <= 0f)
                        continue;
                    if (m.GetTexture(g.Tex) != null)
                    {
                        assigned++;        // 効いている設定 ── 触らない
                        continue;
                    }
                    plan.Add((m, g));
                }
            }

            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("絵に出ない計算",
                    $"止めるものはありません。\n\n対象 {mats.Length} 件 / "
                    + $"テクスチャが割り当ててあって効いている設定 {assigned} 件", "OK");
                return;
            }

            var byGate = plan.GroupBy(p => p.G.Strength)
                             .Select(gr => (Name: gr.Key, Count: gr.Count(),
                                            Cost: gr.First().G.Cost,
                                            Why: gr.First().G.Why))
                             .OrderByDescending(x => x.Count * x.Cost).ToArray();
            string detail = string.Join("\n", byGate.Select(
                x => $"  {x.Name} → 0 　{x.Count} 件（{x.Cost} 命令 / {x.Why}）"));

            if (!EditorUtility.DisplayDialog("絵に出ない計算を止める",
                    $"{plan.Count} 件の設定を 0 にします。\n\n{detail}\n\n"
                    + "**絵は変わりません。** テクスチャが未割り当てなので、"
                    + "既定テクスチャとの計算結果が数学的にゼロ（または ×1）になり、"
                    + "それでも中身は毎画素実行されています。\n\n"
                    + $"テクスチャが割り当ててある {assigned} 件には触りません。\n"
                    + "Undo で戻せます。", "実行", "やめる"))
                return;

            Undo.RecordObjects(plan.Select(p => (Object)p.Mat).Distinct().ToArray(),
                               "Drop Dead Work");
            foreach (var (mat, g) in plan)
            {
                mat.SetFloat(g.Strength, 0f);
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Idol] 絵に出ない計算を {plan.Count} 件止めた"
                      + $"（対象 {mats.Length} 件 / 効いている設定 {assigned} 件は維持）");
        }
    }
}

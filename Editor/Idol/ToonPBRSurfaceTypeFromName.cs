using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// マテリアル名の**大文字トークン**からサーフェスタイプを決める（T-290）。
    ///
    /// なぜ要るか:
    ///   このシェーダーの部位別の中身 ── 髪の異方性 2 バンド、顔の SDF、
    ///   肌の SSS、布の Charlie sheen ── は**すべて `_SurfaceType` の裏**にある。
    ///   Default のままだと 1 命令も走らない。
    ///
    ///   **例外も警告も出ない。**「PBR 寄りの絵」に見えるだけなので、
    ///   シェーダーの出来を評価している最中でも気付けない。
    ///   実際、利用者が見ていたキャラの **20 件中 17 件**が Default のままで、
    ///   髪の異方性も顔の SDF も止まったまま、影のちらつきや鏡面を議論していた。
    ///
    ///   移行スクリプトを通したキャラは 46 件すべて正しかった。あちらは
    ///   **移行元のプロパティ**から判定している（正解データで検証済み）ので、
    ///   移行を経ずに作られた材質だけが落ちる。そこをここが埋める。
    ///
    /// **Default のものしか触らない。** 意図して別のタイプにしてある
    /// （髪の房を Cloth で扱う、など）ものを名前で塗り潰さない。
    /// Undo で戻せる。
    /// </summary>
    public static class ToonPBRSurfaceTypeFromName
    {
        // VRoid の書き出しは部位を**大文字のトークン**で持つ。
        // 小文字混じり（`Face_00_SKIN` の `Face`）と混同しないよう
        // 大文字のトークンだけを見る ── 緩めると顔の肌が Face に化ける。
        private static readonly Dictionary<string, int> kTokens = new Dictionary<string, int>
        {
            { "SKIN", 1 }, { "FACE", 2 }, { "HAIR", 3 }, { "CLOTH", 4 },
        };

        // internal: IdolBatchApplyCI（batchmode 一括適用）と共有する。
        // ロジックを複製すると必ずずれる（T-107）ので、公開範囲だけ広げる。
        internal static readonly string[] kSurfaceKw =
        {
            "_SURFACETYPE_DEFAULT", "_SURFACETYPE_SKIN", "_SURFACETYPE_FACE",
            "_SURFACETYPE_HAIR", "_SURFACETYPE_CLOTH",
        };

        internal static readonly string[] kNames = { "Default", "Skin", "Face", "Hair", "Cloth" };

        [MenuItem("Tools/Idol/サーフェスタイプを名前から設定")]
        private static void Run()
        {
            var mats = Selection.objects.OfType<Material>()
                .Concat(Selection.gameObjects
                    .SelectMany(g => g.GetComponentsInChildren<Renderer>(true))
                    .SelectMany(r => r.sharedMaterials))
                .Where(m => m != null && m.shader != null && m.shader.name.Contains("Idol"))
                .Distinct().ToArray();

            if (mats.Length == 0)
            {
                EditorUtility.DisplayDialog("サーフェスタイプ",
                    "Idol のマテリアル、またはキャラのルートを選択してください。", "OK");
                return;
            }

            var plan = new List<(Material Mat, int Want)>();
            int alreadySet = 0, noHint = 0;
            foreach (var m in mats)
            {
                int want = Guess(m.name);
                if (want < 0) { noHint++; continue; }
                if (!m.HasProperty("_SurfaceType")) { noHint++; continue; }

                int got = Mathf.RoundToInt(m.GetFloat("_SurfaceType"));
                if (got == want) { alreadySet++; continue; }
                // Default 以外なら意図して設定してあるとみなす
                if (got != 0) { alreadySet++; continue; }
                plan.Add((m, want));
            }

            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("サーフェスタイプ",
                    $"変えるものはありません。\n\n"
                    + $"対象 {mats.Length} 件 / 既に設定済み {alreadySet} 件 / "
                    + $"名前に手掛かり無し {noHint} 件", "OK");
                return;
            }

            var preview = string.Join("\n", plan.Take(12)
                .Select(p => $"  {p.Mat.name}  →  {kNames[p.Want]}"));
            if (plan.Count > 12) preview += $"\n  … 他 {plan.Count - 12} 件";

            // **眠っている値がここで目を覚ます。**
            // 部位別の機能はサーフェスタイプの裏にあるので、Default の間は
            // どれだけ大きい値が入っていても絵に出ない。タイプを直した瞬間に
            // **その値のまま効き始める**ので、押す前に見せる。
            // 実際 `_TransmissionStrength` が 20 件すべて 4（既定 0・上限）で、
            // 直せば耳や指が逆光で強く赤く抜ける（T-298）。
            string wake = WakeWarning(plan);

            if (!EditorUtility.DisplayDialog("サーフェスタイプを名前から設定",
                    $"{plan.Count} 件を変更します。**絵が変わります**"
                    + "（髪の異方性・顔の SDF・肌の SSS・布の光沢が動き出します）。\n\n"
                    + preview + wake + "\n\nUndo で戻せます。",
                    "実行", "やめる"))
                return;

            Undo.RecordObjects(plan.Select(p => (Object)p.Mat).ToArray(),
                               "Set Surface Type From Name");
            foreach (var (mat, want) in plan)
            {
                mat.SetFloat("_SurfaceType", want);
                // **キーワードも一緒に。** 値だけ書くと分岐が切り替わらない。
                foreach (var k in kSurfaceKw) mat.DisableKeyword(k);
                mat.EnableKeyword(kSurfaceKw[Mathf.Clamp(want, 0, 4)]);
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();

            Debug.Log($"[Idol] サーフェスタイプを {plan.Count} 件設定した"
                    + $"（既に設定済み {alreadySet} / 手掛かり無し {noHint}）");
        }

        /// <summary>
        /// タイプを直した瞬間に効き始める値のうち、**大きすぎるもの**を挙げる。
        ///
        /// Default の間は部位別の機能が 1 命令も走らないので、値がいくつでも
        /// 絵に出ない。だから「気付かないまま大きい値が入っている」状態が
        /// 普通に起きる ── 実際 `_TransmissionStrength` が 20 件すべて
        /// **4（既定 0・値域の上限）**だった。押してから驚くより先に見せる。
        /// </summary>
        internal static string WakeWarning(List<(Material Mat, int Want)> plan)
        {
            // (プロパティ, 既定, これを超えたら言う, 何が起きるか, 効くタイプ)
            // **手書きの一覧は数えられない。** 以前ここは 4 項目の表だったが、
            // 実際に目を覚ますのは **13 項目**だった（`_SpecularIntensity` が
            // 43 件で上限の 4、`_IridescenceThickness` が 24 件で 3、など）。
            // 表に無いものは何も言わないので、**完全に見えて部分的**という
            // 一番たちの悪い形になる。書き間違いで黙って死んだ実績もある。
            //
            // シェーダーの `#if defined(_SURFACETYPE_*)` を読んで導出する。
            var shader = plan[0].Mat.shader;
            var gated = GatedProperties(shader);
            if (gated == null)
                return "\n\n**眠っている値を調べられませんでした。**"
                     + "（シェーダーのソースを読めない）"
                     + "実行後の見た目を必ず確認してください。";
            if (gated.Count == 0)
                return "\n\n**サーフェスタイプで囲まれたプロパティが 1 つも"
                     + "見つかりませんでした。** 解析が壊れている可能性があります"
                     + "（0 件を「問題なし」と読まないこと）。";

            // プロパティ -> (件数, 最小, 最大, 既定) を、目覚める側と止まる側で別々に。
            var wakes = new SortedDictionary<string, (int N, float Lo, float Hi, float D)>();
            var stops = new SortedDictionary<string, (int N, float Lo, float Hi, float D)>();

            foreach (var kv in gated)
            {
                string prop = kv.Key;
                int idx = shader.FindPropertyIndex(prop);
                if (idx < 0) continue;
                var ptype = shader.GetPropertyType(idx);
                if (ptype != ShaderPropertyType.Float && ptype != ShaderPropertyType.Range)
                    continue;                       // 色・テクスチャは値で比べない
                float d = shader.GetPropertyDefaultFloatValue(idx);

                foreach (var (mat, want) in plan)
                {
                    if (!mat.HasFloat(prop)) continue;
                    float v = mat.GetFloat(prop);
                    if (Mathf.Abs(v - d) < 1e-6f) continue;     // 既定なら驚きは無い

                    var bag = kv.Value.On.Contains(want) ? wakes
                            : kv.Value.Off.Contains(want) ? stops : null;
                    if (bag == null) continue;
                    if (bag.TryGetValue(prop, out var cur))
                        bag[prop] = (cur.N + 1, Mathf.Min(cur.Lo, v), Mathf.Max(cur.Hi, v), d);
                    else
                        bag[prop] = (1, v, v, d);
                }
            }
            if (wakes.Count == 0 && stops.Count == 0) return "";

            string Lines(SortedDictionary<string, (int N, float Lo, float Hi, float D)> src)
            {
                string s = "";
                foreach (var kv in src.OrderByDescending(x => x.Value.N).Take(10))
                {
                    var (n, lo, hi, d) = kv.Value;
                    string range = Mathf.Approximately(lo, hi)
                        ? $"{lo:0.##}" : $"{lo:0.##}〜{hi:0.##}";
                    s += $"\n  {kv.Key}　既定 {d:0.##} → {range}　{n} 件";
                }
                if (src.Count > 10) s += $"\n  …ほか {src.Count - 10} 個";
                return s;
            }

            string body = "";
            if (wakes.Count > 0)
                body += "\n\n**今まで眠っていた値が効き出します。**"
                      + "Default の間は 1 命令も走らないので、既定から離れた値が"
                      + "気付かれずに残ります:" + Lines(wakes)
                      + "\n  強すぎるようなら、実行後にインスペクタで下げてください。";

            // **逆向きも出す。** ここを省いていたが、`_SpecularIntensity` が
            // 43 件で上限の 4 のまま**今まさに効いていて**、HAIR にすると
            // 異方性の経路に置き換わる ── 「押したら鏡面が変わった」の正体は
            // 目覚める値ではなくこちらだった。
            if (stops.Count > 0)
                body += "\n\n**今効いている値が止まります。**"
                      + "タイプ別の経路に置き換わるぶん、"
                      + "これらで作っていた見た目は変わります:" + Lines(stops);

            return body;
        }

        /// <summary>
        /// `#if defined(_SURFACETYPE_*)` で囲まれた中でだけ使われるプロパティを
        /// **シェーダーのソースから導く**。プロパティ名 → 効くタイプ番号の集合。
        ///
        /// **手で書かないこと。** 以前ここは 4 項目の表で、実際に眠っていた
        /// 13 項目のうち 1 つしか言えていなかった。表は増改築に追随しない。
        ///
        /// 読めなかったら `null`。**空の一覧を返して「該当なし」に見せない** ──
        /// 解析が壊れたときと本当に何も無いときを、呼び出し側が区別できなくなる。
        /// </summary>
        /// <summary>プロパティごとの「効き出すタイプ」と「止まるタイプ」。</summary>
        private struct Gates
        {
            public HashSet<int> On;      // `#if _SURFACETYPE_X` の中 → X で効き出す
            public HashSet<int> Off;     // その `#else` の中 → X で止まる
        }

        private static Dictionary<string, Gates> GatedProperties(Shader shader)
        {
            string shaderPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(shaderPath)) return null;
            string dir = System.IO.Path.GetDirectoryName(shaderPath);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return null;

            var files = System.IO.Directory.GetFiles(dir, "*.hlsl",
                                                     System.IO.SearchOption.AllDirectories);
            if (files.Length == 0) return null;

            // KeywordEnum(Default, Skin, Face, Hair, Cloth) の並び順がタイプ番号。
            var toIndex = new Dictionary<string, int>
            {
                { "SKIN", 1 }, { "FACE", 2 }, { "HAIR", 3 }, { "CLOTH", 4 },
            };
            var gated = new Dictionary<string, Gates>();
            var reCond = new System.Text.RegularExpressions.Regex(
                @"^\s*#\s*(if|ifdef|elif|else|endif)\b(.*)$");
            var reType = new System.Text.RegularExpressions.Regex(@"_SURFACETYPE_(\w+)");
            var reProp = new System.Text.RegularExpressions.Regex(@"\b(_[A-Z]\w+)");

            foreach (var f in files)
            {
                // `#if` の入れ子を追う。**`#else` は「反転」であって「無条件」ではない。**
                // `#if defined(_SURFACETYPE_HAIR)` の else 側は Default でも動いており、
                // **タイプを付けると止まる**側。ここを無条件扱いにすると
                // 「今は効いていて止まる値」が丸ごと見えなくなる。
                var pos = new List<HashSet<string>>();   // その枠で効き出すタイプ
                var neg = new List<HashSet<string>>();   // その枠で止まるタイプ
                foreach (var line in System.IO.File.ReadLines(f))
                {
                    var mc = reCond.Match(line);
                    if (mc.Success)
                    {
                        string kind = mc.Groups[1].Value;
                        if (kind == "if" || kind == "ifdef")
                        {
                            var kws = new HashSet<string>();
                            foreach (System.Text.RegularExpressions.Match t
                                     in reType.Matches(mc.Groups[2].Value))
                                kws.Add(t.Groups[1].Value.ToUpperInvariant());
                            pos.Add(kws);
                            neg.Add(new HashSet<string>());
                        }
                        else if (kind == "endif")
                        {
                            if (pos.Count > 0)
                            {
                                pos.RemoveAt(pos.Count - 1);
                                neg.RemoveAt(neg.Count - 1);
                            }
                        }
                        else if (kind == "else" && pos.Count > 0)
                        {
                            // 条件が入れ替わる ── 直前の positive が negative になる。
                            neg[neg.Count - 1] = pos[pos.Count - 1];
                            pos[pos.Count - 1] = new HashSet<string>();
                        }
                        continue;
                    }

                    var on = new HashSet<int>();
                    foreach (var frame in pos)
                        foreach (var k in frame)
                            if (toIndex.TryGetValue(k, out int i)) on.Add(i);
                    var off = new HashSet<int>();
                    foreach (var frame in neg)
                        foreach (var k in frame)
                            if (toIndex.TryGetValue(k, out int i)) off.Add(i);
                    if (on.Count == 0 && off.Count == 0) continue;

                    foreach (System.Text.RegularExpressions.Match p in reProp.Matches(line))
                    {
                        string name = p.Groups[1].Value;
                        if (!gated.TryGetValue(name, out var g))
                            g = new Gates { On = new HashSet<int>(), Off = new HashSet<int>() };
                        g.On.UnionWith(on);
                        g.Off.UnionWith(off);
                        gated[name] = g;
                    }
                }
            }
            return gated;
        }

        /// <summary>
        /// **使っていない重ね描きパスを止める。**
        ///
        /// `HairSeeThrough` は独自 LightMode `IdolHairSeeThrough` にあり、
        /// `HairSeeThroughFeature` がまとめて描く（T-341）。Feature は**タグを持つ
        /// パスを全部描く**ので、透過を使わないマテリアルのぶんも draw が走る。
        /// ステンシル（眉 2 / 目 4 のビット）で画素は落ちるが、
        /// **描画コールと頂点処理は走る。**止めれば Feature の対象から外れる。
        ///
        /// 実測: 3 体のうち 1 体（46 件）は 39 件で止めてあったのに、
        /// 残り 2 体（各 20 件）は**1 つも止まっていなかった** ──
        /// インスペクタのステンシルのボタンを通していないマテリアルは
        /// 止まらないため。**絵は変わらず draw だけが倍**なので気付けない。
        /// </summary>
        [MenuItem("Tools/Idol/使っていない重ね描きパスを止める")]
        private static void DisableUnusedSeeThrough()
        {
            var mats = Selection.objects.OfType<Material>()
                .Concat(Selection.gameObjects
                    .SelectMany(g => g.GetComponentsInChildren<Renderer>(true))
                    .SelectMany(r => r.sharedMaterials))
                .Where(m => m != null && m.shader != null && m.shader.name.Contains("Idol"))
                .Distinct().ToArray();

            if (mats.Length == 0)
            {
                EditorUtility.DisplayDialog("重ね描きパス",
                    "Idol のマテリアル、またはキャラのルートを選択してください。", "OK");
                return;
            }

            // **前髪透過を使っている髪は残す。**
            // 見分けは「眉・目が書いたビット(6)だけを読んで、等しい所に描く」設定。
            var off = mats.Where(m => m.GetShaderPassEnabled(kSeeThroughPass) && !UsesSeeThrough(m))
                          .ToArray();
            int keep = mats.Count(UsesSeeThrough);

            if (off.Length == 0)
            {
                EditorUtility.DisplayDialog("重ね描きパス",
                    $"止めるものはありません。\n\n対象 {mats.Length} 件 / "
                    + $"透過を使っている髪 {keep} 件", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("使っていない重ね描きパスを止める",
                    $"{off.Length} 件で HairSeeThrough パスを止めます。\n\n"
                    + "**絵は変わりません。** ステンシルで画素が落ちるだけの空振りを、"
                    + "描画コールごと止めます（前方描画の draw が減ります）。\n\n"
                    + $"透過を使っている髪 {keep} 件はそのままにします。\n"
                    + "Undo で戻せます。", "実行", "やめる"))
                return;

            Undo.RecordObjects(off.Select(m => (Object)m).ToArray(), "Disable See-Through Pass");
            foreach (var m in off)
            {
                m.SetShaderPassEnabled(kSeeThroughPass, false);
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Idol] HairSeeThrough を {off.Length} 件で止めた（髪 {keep} 件は維持）");
        }

        // LightMode タグ = パス停止の名前空間。T-341 で SRPDefaultUnlit から改名した。
        // 旧タグで止めた `m_DisabledShaderPasses` の記録は不活性化する（旧タグの
        // パスはもう存在しない）ので、改名後は**このツールを再実行**すること。
        internal const string kSeeThroughPass = "IdolHairSeeThrough";

        /// <summary>前髪透過の受け皿になっているか（`ApplyHairSeeThrough` の設定）。</summary>
        internal static bool UsesSeeThrough(Material m)
        {
            return m.HasFloat("_StencilComp") && m.HasFloat("_StencilReadMask")
                && Mathf.RoundToInt(m.GetFloat("_StencilComp")) == (int)CompareFunction.Equal
                && Mathf.RoundToInt(m.GetFloat("_StencilReadMask")) == 0x06;
        }

        /// <summary>名前の**最後の**大文字トークンを採る。無ければ -1。</summary>
        internal static int Guess(string name)
        {
            int found = -1;
            foreach (var token in name.Split('_', ' ', '.', '(', ')'))
                if (kTokens.TryGetValue(token, out int kind))
                    found = kind;
            return found;
        }
    }
}

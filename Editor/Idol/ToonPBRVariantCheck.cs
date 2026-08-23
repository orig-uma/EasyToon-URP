using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// キーワードの組み合わせを**明示的にコンパイルして**検証する。
    ///
    /// なぜ `ShaderCompileCheck` だけでは足りないか:
    ///   あちらは `ImportAsset` 後の `ShaderUtil.GetShaderMessages` を読む。
    ///   Unity がインポート時に流すバリアントしか見ないので、
    ///   **マテリアルもシーンも無い検証用プロジェクトでは実プロジェクトと中身が違う。**
    ///   実際これで `maximum ps_4_0 sampler register index (16) exceeded` を
    ///   取りこぼし、ユーザーの実機で初めて出た（T-072）。
    ///
    /// `ShaderData.Pass.CompileVariant` はキーワードを指定して**その場でコンパイルする**。
    /// リソース上限のようなバリアント固有の失敗がそのまま返る。
    /// **`TextureBindings` からサンプラ本数を数えるのは試したが採用しなかった。**
    /// 空配列なのに正の値が返るなど、値が信用できなかった。
    /// 合否とメッセージだけを見る。これは実コンパイラの出力そのものなので確実。
    /// </summary>
    public static class ToonPBRVariantCheck
    {
        private const string Marker = "[VariantCheck]";
        private const string ShaderName = "Origuma/EasyToon_URP/Idol";

        private static readonly string[] SurfaceTypes =
        {
            "_SURFACETYPE_DEFAULT", "_SURFACETYPE_SKIN", "_SURFACETYPE_FACE",
            "_SURFACETYPE_HAIR", "_SURFACETYPE_CLOTH",
        };

        // 同時に立てられるものはまとめる。実プロジェクトで起きうる組み合わせを狙う。
        private static readonly (string label, string[] keywords)[] FeatureSets =
        {
            ("素", new string[0]),
            ("Forward+", new[] { "_CLUSTER_LIGHT_LOOP" }),
            ("全部盛り", new[]
            {
                "_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT",
                "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHT_SHADOWS",
                "_REFLECTION_PROBE_BOX_PROJECTION", "_SCREEN_SPACE_OCCLUSION",
                "_LIGHT_COOKIES", "_LIGHT_LAYERS", "LOD_FADE_CROSSFADE",
            }),
            ("デカール", new[] { "_CLUSTER_LIGHT_LOOP", "_HQ_SHADOW_ON", "_DBUFFER_MRT3" }),
            ("APV L2", new[] { "_CLUSTER_LIGHT_LOOP", "_HQ_SHADOW_ON", "PROBE_VOLUMES_L2" }),
            ("画面空間の影", new[] { "_CLUSTER_LIGHT_LOOP", "_MAIN_LIGHT_SHADOWS_SCREEN", "_SHADOWS_SOFT" }),
            ("Forward(非クラスタ)+全部", new[]
            {
                "_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT",
                "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHT_SHADOWS",
                "_REFLECTION_PROBE_BOX_PROJECTION", "LOD_FADE_CROSSFADE",
            }),
        };

        /// <summary>
        /// ForwardLit 以外のパスが宣言しているキーワードの組み合わせ。
        ///
        /// **なぜ表が要るか。** ここは長い間 ForwardLit 以外へ空配列しか渡しておらず、
        /// `_OUTLINE_ON` で囲まれた Outline の押し出しコードと、
        /// `_ALPHATEST_ON` を立てた ShadowCaster / DepthOnly / DepthNormals /
        /// HairShadow が**一度もコンパイルされていなかった**。
        /// Unity の既定インポートも既定バリアントしか流さないので、
        /// 誰も見ていない状態だった ── T-056 / T-070 と同じ穴。
        ///
        /// **そのパスが宣言していないキーワードを渡すと CompileVariant が失敗する**ため、
        /// 中身は `.shader` の `#pragma` を実際に読んで作ってある。
        /// パスに pragma を足したらここも足すこと。表に無いパスは警告で知らせる。
        /// </summary>
        private static readonly Dictionary<string, (string label, string[] keywords)[]> PassSets =
            new Dictionary<string, (string, string[])[]>
            {
                ["Outline"] = new[]
                {
                    ("素", new string[0]),
                    ("輪郭ON", new[] { "_OUTLINE_ON" }),
                    ("輪郭ON+アルファ", new[] { "_OUTLINE_ON", "_ALPHATEST_ON" }),
                },
                ["ShadowCaster"] = new[]
                {
                    ("素", new string[0]),
                    ("アルファ", new[] { "_ALPHATEST_ON" }),
                    ("点光源", new[] { "_CASTING_PUNCTUAL_LIGHT_SHADOW" }),
                    ("全部", new[]
                    {
                        "_ALPHATEST_ON", "_CASTING_PUNCTUAL_LIGHT_SHADOW", "LOD_FADE_CROSSFADE",
                    }),
                },
                ["DepthOnly"] = new[]
                {
                    ("素", new string[0]),
                    ("アルファ", new[] { "_ALPHATEST_ON" }),
                    ("全部", new[] { "_ALPHATEST_ON", "LOD_FADE_CROSSFADE" }),
                },
                ["DepthNormals"] = new[]
                {
                    ("素", new string[0]),
                    ("アルファ", new[] { "_ALPHATEST_ON" }),
                    ("全部", new[] { "_ALPHATEST_ON", "LOD_FADE_CROSSFADE" }),
                },
                ["HairShadow"] = new[]
                {
                    ("素", new string[0]),
                    ("アルファ", new[] { "_ALPHATEST_ON" }),
                },
                // 前髪透過（T-223）。ForwardPass.hlsl を define 違いで使い回すので、
                // **キーワードが OFF の側も必ず通すこと** ── OFF のときは
                // フラグメントが clip(-1) に畳まれる別のコードになる。
                // 「ON だけ通して OFF が壊れている」は絵に出ないまま残る。
                ["HairSeeThrough"] = new[]
                {
                    ("素", new string[0]),
                    ("髪", new[] { "_SURFACETYPE_HAIR" }),
                    ("全部", new[]
                    {
                        "_SURFACETYPE_HAIR", "_ALPHATEST_ON",
                        "_ADDITIONAL_LIGHTS",
                    }),
                },
                // TAA が読む速度バッファを埋めるパス（T-175）。
                // このパスが無いと TAA はアニメーションしたキャラを静止物と見なす。
                ["MotionVectors"] = new[]
                {
                    ("素", new string[0]),
                    ("アルファ", new[] { "_ALPHATEST_ON" }),
                    ("全部", new[] { "_ALPHATEST_ON", "LOD_FADE_CROSSFADE" }),
                },
            };

        // **プラットフォームで落ちるものが違う。** レジスタ数の上限も、
        // 暗黙の型変換の許容度も、微分の扱いも同じではない。
        // D3D だけ見ていると Vulkan / Metal で初めて出る誤りを逃す。
        private static readonly ShaderCompilerPlatform[] Platforms =
        {
            ShaderCompilerPlatform.D3D,
            ShaderCompilerPlatform.Vulkan,
        };

        [MenuItem("Tools/Idol/バリアントを実コンパイル検証")]
        private static void Run()
        {
            var result = Execute();
            Debug.Log(result.report);

            if (result.failures > 0)
                Debug.LogError($"{Marker} {result.total} 組中 {result.failures} 組が失敗");
            else
                Debug.Log($"{Marker} {result.total} 組すべて成功");
        }

        /// <summary>batchmode から呼ぶ入口。失敗があれば終了コード 1。</summary>
        public static void RunCI()
        {
            var result = Execute();
            Debug.Log(result.report);

            if (result.failures > 0)
            {
                Debug.LogError($"{Marker} {result.total} 組中 {result.failures} 組が失敗");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"{Marker} {result.total} 組すべて成功");
            EditorApplication.Exit(0);
        }

        private struct Result
        {
            public string report;
            public int total, failures;
        }

        private static Result Execute()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Marker} {ShaderName}");

            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                sb.AppendLine($"  ERR  シェーダーが見つからない: {ShaderName}");
                return new Result { report = sb.ToString(), total = 0, failures = 1 };
            }

            var data = ShaderUtil.GetShaderData(shader);
            int total = 0, failures = 0;

            // **表にあってシェーダーに無いパスを見つけるため、通ったパス名を控える。**
            // ここまでの検査は逆向き（表に無いパス）しか見ていなかった。
            // パスを消すと、そのパスはループに現れないので**黙って検証対象から外れる。**
            // 組数が 53 → 50 に減るだけで、読む人はまず気付かない。
            // CLAUDE.md の「既存パスを消さない」は文章で書いてあるだけで
            // 何も強制していなかった（DepthOnly はリムライトの前提、
            // DepthNormals は SSAO の前提、MotionVectors は TAA の前提）。
            var seenPasses = new HashSet<string>();

            for (int si = 0; si < data.SubshaderCount; si++)
            {
                var sub = data.GetSubshader(si);

                for (int pi = 0; pi < sub.PassCount; pi++)
                {
                    var pass = sub.GetPass(pi);
                    seenPasses.Add(pass.Name);

                    bool isForward = pass.Name == "ForwardLit";

                    // ForwardLit だけがサーフェスタイプを宣言している。
                    // 他のパスは表から組み合わせを引き、タイプ軸は回さない。
                    (string label, string[] keywords)[] sets;

                    if (isForward)
                    {
                        sets = FeatureSets;
                    }
                    else if (!PassSets.TryGetValue(pass.Name, out sets))
                    {
                        // **表に無いパスを黙って既定バリアントだけで通さない。**
                        // それをやったせいで Outline と ShadowCaster が長期間
                        // 未検証のままだった。新しいパスを足したら表にも足すこと。
                        sets = new[] { ("素", new string[0]) };
                        sb.AppendLine(
                            $"  WARN {pass.Name} は PassSets に無い。既定バリアントしか検証していない");
                    }

                    foreach (var (label, features) in sets)
                    {
                        foreach (var st in SurfaceTypes)
                        {
                            if (!isForward && st != SurfaceTypes[0]) continue;

                            var keywords = isForward
                                ? features.Concat(new[] { st }).ToArray()
                                : features;
                            total++;

                            // 頂点とフラグメントを別々に見る。
                            // サンプラ上限はフラグメント側で当たるが、
                            // 頂点側だけで落ちる誤りもあるので両方回す。
                            foreach (var platform in Platforms)
                            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                            {
                                var info = pass.CompileVariant(
                                    stage, keywords, platform,
                                    BuildTarget.StandaloneWindows64);

                                bool bad = !info.Success
                                        || (info.Messages != null && info.Messages.Length > 0);

                                if (bad)
                                {
                                    failures++;

                                    // タイプ軸を回していないパスで `_SURFACETYPE_DEFAULT` と
                                    // 出すと「タイプ別に見た」と誤読されるので出さない。
                                    string where = isForward
                                        ? $"{pass.Name} / {label} / {st}"
                                        : $"{pass.Name} / {label}";

                                    sb.AppendLine($"  ERR  {where} / {platform} / {stage}");

                                    if (info.Messages != null)
                                        foreach (var m in info.Messages)
                                            sb.AppendLine($"        {m.severity}: {m.message.Trim()}");
                                }
                            }
                        }
                    }
                }
            }

            // **表にあるパスがシェーダーから消えていないか。**
            // これを見ないと、パスを消したときに組数が静かに減るだけで通る。
            // ForwardLit は PassSets に無い（FeatureSets を使う）ので明示的に足す。
            var required = new HashSet<string>(PassSets.Keys) { "ForwardLit" };
            required.ExceptWith(seenPasses);

            foreach (var missing in required)
            {
                failures++;
                sb.AppendLine(
                    $"  ERR  パス '{missing}' が PassSets にあるのにシェーダーに無い。"
                    + "消したのなら PassSets からも消すこと。"
                    + "消していないつもりなら、そのパスは今まったく描かれていない");
            }

            sb.AppendLine($"  {total} 組 / 失敗 {failures}");
            return new Result
            {
                report = sb.ToString().TrimEnd(),
                total = total, failures = failures,
            };
        }
    }
}

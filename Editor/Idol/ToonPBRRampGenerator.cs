// =============================================================================
//  ToonPBRRampGenerator.cs
// -----------------------------------------------------------------------------
//  ランプの編集・焼き・書き出し・取り込み（T-388 → T-396 でアセット化）。
//
//  構成:
//    ToonRampAsset（Runtime）= Gradient ＋ 埋め込み Texture2D（256×1・sRGB）
//    マテリアルは埋め込みテクスチャを _RampMap で参照する。
//
//  なぜアセットにしたか（T-396）: PNG を焼く方式は「書き出し → 再インポート」が
//  重く、編集中に反映できず、Gradient も Undo の対象外だった（利用者「やって
//  みないと分からない感が消費カロリー高い」）。ScriptableObject の Gradient は
//  Undo / Redo が効き、変更のたびに埋め込みテクスチャの画素を書き換えれば
//  シーンにその場で反映される（ファイルもインポートも介在しない）。
//
//  PNG は交換フォーマット: 外部ツール・他プロジェクト・lilToon 系へ持ち出す
//  「書き出し」と、配布物や旧生成物（T-388 の PNG）を取り込む「取り込み」を持つ。
//  シェーダーは _RampMap にテクスチャが刺さっていれば何でも読むので、
//  PNG を直接挿す運用も残る。
// =============================================================================
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ToonNPR.EditorTools
{
    internal static class ToonPBRRampGenerator
    {
        // lit（伝達関数の出力）が U なので、この解像度で 1/256 刻み。
        // 段差を作る用途（2影風）でも Fixed モードのキーで境界が 1 texel に収まる。
        private const int Width = 256;

        // 旧方式（T-388）の PNG が importer.userData に持っていたグラデーション。
        [System.Serializable]
        private class GradientHolder
        {
            public Gradient gradient = new Gradient();
        }

        static ToonPBRRampGenerator()
        {
            // Undo / Redo で Gradient が戻っても画素は自動では戻らない。
            // 読み込まれているランプアセットを全部焼き直す（数個なので安い）。
            Undo.undoRedoPerformed += () =>
            {
                foreach (var a in Resources.FindObjectsOfTypeAll<ToonRampAsset>())
                    if (a != null && a.texture != null) Bake(a);
            };
        }

        // ---- 既定・プリセット ---------------------------------------------------

        /// <summary>
        /// 左 = 影 / 右 = 明。**部位を選ばない出発点**。
        /// 以前は暖色の中間キーを挟んだ肌向けの色だったが、服・髪・金具にも同じ既定が
        /// 出るので「肌色すぎる」と指摘され、利用者確認の上で**弱セピア**に落ち着いた
        /// （暖色に寄った灰。ランプはアルベドに乗算される）。
        /// 寒色の影は肌を病的に見せ、強い暖色は白い布をベージュにし青を濁らせる。
        /// 「白に掛けて気付く程度に暖かく、青を濁らせない」上限がこの値。
        /// ステージ照明（暖色スポット主体）とも喧嘩しない。明側は白 = 素通し。
        /// </summary>
        public static Gradient DefaultGradient()
            => Make(GradientMode.Blend, (new Color(0.66f, 0.60f, 0.57f), 0f), (Color.white, 0.5f));

        // 出発点をワンクリックで選ぶ（T-397）。Gradient を白紙から作らせない。
        // どれも「左 = 影 / 右 = 明（白 = 素通し）」の乗算ランプ。
        // Fixed モードのキーを使うものは硬い段（セル・2影）になる。
        public readonly struct Preset
        {
            public readonly string Jp, En;
            public readonly System.Func<Gradient> Build;
            public Preset(string jp, string en, System.Func<Gradient> build)
            { Jp = jp; En = en; Build = build; }
        }

        private static Gradient Make(GradientMode mode, params (Color c, float t)[] keys)
        {
            var g = new Gradient { mode = mode };
            var ck = new GradientColorKey[keys.Length];
            for (int i = 0; i < keys.Length; i++) ck[i] = new GradientColorKey(keys[i].c, keys[i].t);
            g.SetKeys(ck, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        public static readonly Preset[] Presets =
        {
            new Preset("セピア", "Sepia", DefaultGradient),
            new Preset("中立", "Neutral", () => Make(GradientMode.Blend,
                (new Color(0.62f, 0.62f, 0.68f), 0f), (Color.white, 0.5f))),
            new Preset("寒色（屋外）", "Cool (outdoor)", () => Make(GradientMode.Blend,
                (new Color(0.55f, 0.60f, 0.72f), 0f), (Color.white, 0.5f))),
            // 影の底まで赤いと「日焼け」に見える（利用者指摘）。底は弱セピアより少し
            // 暖かい程度に留め、赤みは中間の芯だけに持たせる。
            new Preset("暖色（肌）", "Warm (skin)", () => Make(GradientMode.Blend,
                (new Color(0.70f, 0.59f, 0.55f), 0f), (new Color(0.95f, 0.80f, 0.75f), 0.4f),
                (Color.white, 0.55f))),
            new Preset("紫影", "Violet shade", () => Make(GradientMode.Blend,
                (new Color(0.60f, 0.52f, 0.70f), 0f), (new Color(0.90f, 0.84f, 0.92f), 0.4f),
                (Color.white, 0.55f))),
            new Preset("広い階調", "Wide", () => Make(GradientMode.Blend,
                (new Color(0.66f, 0.60f, 0.57f), 0f), (Color.white, 0.8f))),
            new Preset("セル（1 段）", "Cel (1 step)", () => Make(GradientMode.Fixed,
                (new Color(0.66f, 0.60f, 0.57f), 0f), (Color.white, 0.5f))),
            new Preset("2影（2 段）", "2nd shadow (2 steps)", () => Make(GradientMode.Fixed,
                (new Color(0.55f, 0.50f, 0.52f), 0f), (new Color(0.78f, 0.72f, 0.72f), 0.25f),
                (Color.white, 0.5f))),
        };

        // ---- アセットの探索・作成 -----------------------------------------------

        /// <summary>マテリアルの _RampMap が指す埋め込みテクスチャの親アセット。無ければ null。</summary>
        public static ToonRampAsset FindAsset(Material mat)
        {
            var tex = mat != null && mat.HasTexture("_RampMap") ? mat.GetTexture("_RampMap") : null;
            if (tex == null) return null;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            var asset = AssetDatabase.LoadAssetAtPath<ToonRampAsset>(path);
            return (asset != null && asset.texture == tex) ? asset : null;
        }

        /// <summary>
        /// 保存先はベイク産と同じ `<先頭マテリアルのフォルダ>/Baked/`。名前は
        ///   単独 … `<マテリアル名>_Ramp`
        ///   複数 … `Ramp_Shared_<ハッシュ8桁>`（選択マテリアル名のソート済み FNV-1a）
        /// 同じグループで作り直せば選択順に依らず同じアセットへ、別グループは別アセット。
        /// </summary>
        private static string BasePath(Material[] mats)
        {
            var mat = mats[0];
            string matPath = AssetDatabase.GetAssetPath(mat);
            string dir = (string.IsNullOrEmpty(matPath) ? "Assets" : Path.GetDirectoryName(matPath))
                         .Replace('\\', '/');
            string bakedDir = $"{dir}/Baked";
            if (!AssetDatabase.IsValidFolder(bakedDir))
                AssetDatabase.CreateFolder(dir, "Baked");

            string fileName;
            if (mats.Length == 1)
            {
                fileName = $"{mat.name}_Ramp";
            }
            else
            {
                var names = new List<string>();
                foreach (var m in mats)
                    if (m != null) names.Add(m.name);
                names.Sort(System.StringComparer.Ordinal);
                uint hash = 2166136261u;
                foreach (var n in names)
                    foreach (char ch in n + "|")
                        hash = (hash ^ ch) * 16777619u;
                fileName = $"Ramp_Shared_{hash:x8}";
            }
            return $"{bakedDir}/{fileName}";
        }

        /// <summary>
        /// ランプアセットを作って（同名があれば上書きではなく再利用して）全マテリアルへ割り当てる。
        /// 1 つを共有する ── 個別に分岐したい材質は単独選択で作り直す（別名になる）。
        /// </summary>
        public static ToonRampAsset CreateAndAssign(Material[] mats, Gradient gradient)
        {
            string path = BasePath(mats) + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<ToonRampAsset>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<ToonRampAsset>();
                AssetDatabase.CreateAsset(asset, path);
            }
            Undo.RecordObject(asset, "Create Ramp");
            asset.gradient = CloneGradient(gradient);
            EnsureTexture(asset);
            Bake(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            Assign(mats, asset);
            return asset;
        }

        /// <summary>現在のランプを複製して、選択中の材質だけ独自の 1 枚に分岐させる。</summary>
        public static ToonRampAsset DuplicateAndAssign(Material[] mats, ToonRampAsset src)
            => CreateAndAssign(mats, src.gradient);

        public static void Assign(Material[] mats, ToonRampAsset asset)
        {
            Undo.RecordObjects(mats, "Assign Ramp");
            foreach (var m in mats)
            {
                if (m == null) continue;
                m.SetTexture("_RampMap", asset.texture);
                m.SetFloat("_UseRampMap", 1f);
                // 生成物は常に 1 行。多段ランプ（NPR.a で行選択）は外部テクスチャの領分。
                m.SetFloat("_RampRowCount", 1f);
                EditorUtility.SetDirty(m);
            }
        }

        // ---- 焼く（即時反映の実体）----------------------------------------------

        private static void EnsureTexture(ToonRampAsset asset)
        {
            if (asset.texture != null) return;
            // 高さ 1 で足りる（シェーダーは V = (行 + 0.5) / 行数 で引く）。
            // linear:false = sRGB。Gradient エディタで見た色をそのまま焼く。
            var tex = new Texture2D(Width, 1, TextureFormat.RGBA32, false, false)
            {
                name = "Ramp",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            AssetDatabase.AddObjectToAsset(tex, asset);
            asset.texture = tex;
            EditorUtility.SetDirty(asset);
        }

        /// <summary>Gradient → 埋め込みテクスチャ。ファイルもインポートも介在しないので毎フレーム呼べる。</summary>
        public static void Bake(ToonRampAsset asset)
        {
            if (asset == null || asset.texture == null) return;
            var px = new Color32[Width];
            for (int x = 0; x < Width; x++)
                px[x] = asset.gradient.Evaluate((x + 0.5f) / Width);
            asset.texture.SetPixels32(px);
            asset.texture.Apply(false, false);
            EditorUtility.SetDirty(asset.texture);
            EditorUtility.SetDirty(asset);
        }

        /// <summary>Inspector から: Undo に載せて gradient を差し替え、即座に焼く。</summary>
        public static void SetGradient(ToonRampAsset asset, Gradient g)
        {
            Undo.RecordObject(asset, "Edit Ramp");
            asset.gradient = g;
            Bake(asset);
        }

        private static Gradient CloneGradient(Gradient g)
        {
            var c = new Gradient { mode = g.mode };
            c.SetKeys(g.colorKeys, g.alphaKeys);
            return c;
        }

        // ---- PNG（交換フォーマット）--------------------------------------------

        /// <summary>
        /// 外部ツール・他プロジェクトへ持ち出す。アセットと同じフォルダに
        /// `<アセット名>.png`（sRGB・非圧縮・Clamp・ミップ無し）。旧方式と同じく
        /// importer.userData に Gradient を残すので、取り込みで完全に復元できる。
        /// </summary>
        public static string ExportPng(ToonRampAsset asset)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            string path = Path.ChangeExtension(assetPath, ".png").Replace('\\', '/');
            Bake(asset);
            File.WriteAllBytes(path, asset.texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.userData = "ToonPBRRamp" + EditorJsonUtility.ToJson(new GradientHolder { gradient = asset.gradient });
            importer.SaveAndReimport();
            return path;
        }

        /// <summary>
        /// 既存のランプテクスチャ（旧生成物・配布物・手描き）から Gradient を作る。
        /// 旧生成物なら userData の Gradient をそのまま、無ければ画素から推定する。
        /// </summary>
        public static Gradient GradientFromTexture(Texture tex)
        {
            if (tex == null) return null;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && importer.userData != null && importer.userData.StartsWith("ToonPBRRamp"))
            {
                try
                {
                    var holder = new GradientHolder();
                    EditorJsonUtility.FromJsonOverwrite(importer.userData.Substring("ToonPBRRamp".Length), holder);
                    return holder.gradient;
                }
                catch { /* 壊れていれば画素から推定する */ }
            }

            // 画素から推定。ファイルを直接読むので isReadable に依らない。
            var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!raw.LoadImage(File.ReadAllBytes(path))) { Object.DestroyImmediate(raw); return null; }
            int w = raw.width;
            var row = new Color[w];
            for (int x = 0; x < w; x++) row[x] = raw.GetPixel(x, 0);
            Object.DestroyImmediate(raw);
            return FitGradient(row);
        }

        /// <summary>
        /// 1 行の色列を Gradient（キー最大 8）に落とす。
        /// 段（隣接差が大きく間が平ら）が少なければ Fixed、そうでなければ Blend で
        /// 誤差最大点にキーを足していく（Ramer–Douglas–Peucker の色版）。
        /// </summary>
        private static Gradient FitGradient(Color[] row)
        {
            int n = row.Length;
            if (n < 2) return DefaultGradient();

            // --- 段の検出 ---
            var jumps = new List<int>();
            for (int x = 1; x < n; x++)
                if (ColorDist(row[x], row[x - 1]) > 0.08f) jumps.Add(x);
            bool flatBetween = true;
            int prev = 0;
            foreach (var j in jumps)
            {
                for (int x = prev + 1; x < j; x++)
                    if (ColorDist(row[x], row[prev]) > 0.03f) { flatBetween = false; break; }
                if (!flatBetween) break;
                prev = j;
            }
            if (jumps.Count > 0 && jumps.Count <= 7 && flatBetween)
            {
                var keys = new List<(Color, float)> { (row[0], 0f) };
                foreach (var j in jumps) keys.Add((row[j], j / (float)n));
                return Make(GradientMode.Fixed, keys.ToArray());
            }

            // --- 滑らかな近似 ---
            var idx = new List<int> { 0, n - 1 };
            while (idx.Count < 8)
            {
                idx.Sort();
                float worst = 0f; int worstX = -1;
                for (int k = 0; k + 1 < idx.Count; k++)
                {
                    int a = idx[k], b = idx[k + 1];
                    for (int x = a + 1; x < b; x++)
                    {
                        float t = (x - a) / (float)(b - a);
                        var approx = Color.Lerp(row[a], row[b], t);
                        float err = ColorDist(row[x], approx);
                        if (err > worst) { worst = err; worstX = x; }
                    }
                }
                if (worstX < 0 || worst < 2f / 255f) break;
                idx.Add(worstX);
            }
            idx.Sort();
            var ck = new (Color, float)[idx.Count];
            for (int k = 0; k < idx.Count; k++)
                ck[k] = (row[idx[k]], idx[k] / (float)(n - 1));
            return Make(GradientMode.Blend, ck);
        }

        private static float ColorDist(Color a, Color b)
            => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));
    }
}

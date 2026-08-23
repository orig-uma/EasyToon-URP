using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ToonNPR.EditorTools
{
    /// <summary>
    /// アウトライン用の平滑法線を頂点カラーへベイクする。
    ///
    /// 押し出しに素の法線を使うと、UV シームやハードエッジで頂点が分裂している場所で
    /// 輪郭が裂ける。同じ位置にある頂点の法線を平均したものを別に持たせて解決する。
    ///
    /// 格納形式はシェーダー側（ToonPBR.shader の OutlineVert）と対になっている:
    ///   頂点カラー RGB = 接線空間に変換した平滑法線を 0..1 へエンコード
    ///   頂点カラー A   = 幅マスク（_OUTLINE_VERTEX_WIDTH 用）。ここでは触らない
    ///
    /// オブジェクト空間ではなく接線空間に入れるのは、スキニングで回転しても
    /// TBN が一緒に回るため、変形するメッシュでも破綻しないから。
    /// </summary>
    public static class SmoothNormalBaker
    {
        private const string Marker = "[SmoothNormalBaker]";

        // 位置の一致判定に使う量子化の細かさ。FBX の書き出し誤差を吸収しつつ、
        // 隣接した別頂点を巻き込まない程度に取る。
        private const float PositionGrid = 10000f;

        [MenuItem("Tools/Idol/Bake Smooth Normals", true)]
        private static bool ValidateBake()
        {
            return Selection.gameObjects.Length > 0;
        }

        [MenuItem("Tools/Idol/Bake Smooth Normals")]
        private static void Bake()
        {
            int baked = 0, skipped = 0, shared = 0, rebaked = 0;

            // 同じメッシュを共有する Renderer が複数あるとき、素直に回すと
            // **同じ内容のアセットが Renderer の数だけできる。**
            // 衣装を分割したアバターでは普通に起きるので、1回の実行の中では
            // 1メッシュ 1アセットに畳む。
            var cache = new Dictionary<Mesh, Mesh>();

            // StartAssetEditing 中は作ったアセットが AssetDatabase から見えず、
            // GenerateUniqueAssetPath が同じ名前を2回返す。自前で押さえる。
            var claimedPaths = new HashSet<string>();

            int undoGroup = Undo.GetCurrentGroup();

            // 1件ごとに SaveAssets を呼ぶとその都度インポータが回り、
            // メッシュ数が多いアバターで待たされる。まとめて最後に1回にする。
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var go in Selection.gameObjects)
                {
                    foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh source = GetSharedMesh(renderer);
                        if (source == null) continue;

                        if (cache.TryGetValue(source, out var done))
                        {
                            AssignMesh(renderer, done);
                            shared++;
                            continue;
                        }

                        if (!source.isReadable)
                        {
                            Debug.LogError(
                                $"{Marker} {source.name} は Read/Write が無効。" +
                                "モデルのインポート設定で Read/Write Enabled を ON にすること。", renderer);
                            skipped++;
                            continue;
                        }

                        Mesh result = BakeMesh(source);
                        if (result == null) { skipped++; continue; }

                        // 焼き直しの場合は既存アセットに書き戻す。新しく作ると
                        // 元を参照している他のシーンやプレハブが取り残される。
                        if (IsBakedAsset(source))
                        {
                            source.colors = result.colors;
                            EditorUtility.SetDirty(source);
                            Object.DestroyImmediate(result);

                            cache[source] = source;
                            AssignMesh(renderer, source);
                            Debug.Log($"{Marker} {source.name} を焼き直した", renderer);
                            rebaked++;
                            continue;
                        }

                        string path = SaveAsset(result, source, claimedPaths);
                        if (path == null)
                        {
                            // **保存できなかったメッシュは割り当てないこと。**
                            // 非永続のメッシュを Renderer に入れるとドメインリロードで
                            // 参照が null になり、キャラがまるごと消える。
                            Object.DestroyImmediate(result);
                            skipped++;
                            continue;
                        }

                        cache[source] = result;
                        AssignMesh(renderer, result);

                        Debug.Log($"{Marker} {source.name} → {path}", renderer);
                        baked++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            // Renderer への割り当ては1操作として畳む。畳まないと
            // メッシュの数だけ Ctrl+Z を押す羽目になる。
            Undo.SetCurrentGroupName("Bake Smooth Normals");
            Undo.CollapseUndoOperations(undoGroup);

            if (baked == 0 && rebaked == 0 && shared == 0 && skipped == 0)
                Debug.LogWarning($"{Marker} 選択の中にメッシュを持つ Renderer が無い。");
            else
                Debug.Log($"{Marker} 新規 {baked} 件 / 焼き直し {rebaked} 件 / " +
                          $"共有の再利用 {shared} 件 / スキップ {skipped} 件");
        }

        private static Mesh GetSharedMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private static Mesh BakeMesh(Mesh source)
        {
            // 元のメッシュは FBX の中にあって書き換えられないので、複製に対して行う。
            // Instantiate ならボーンウェイト・バインドポーズ・ブレンドシェイプも保たれる。
            var mesh = Object.Instantiate(source);
            mesh.name = source.name;

            var vertices = mesh.vertices;
            var normals  = mesh.normals;

            if (normals == null || normals.Length != vertices.Length)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }

            var tangents = mesh.tangents;
            if (tangents == null || tangents.Length != vertices.Length)
            {
                // 接線が無いと接線空間へ変換できない。UV があれば作れる。
                if (mesh.uv == null || mesh.uv.Length != vertices.Length)
                {
                    Debug.LogError($"{Marker} {source.name} に UV も接線も無いのでベイクできない。");
                    Object.DestroyImmediate(mesh);
                    return null;
                }

                mesh.RecalculateTangents();
                tangents = mesh.tangents;
            }

            var smooth = AverageByPosition(vertices, normals);

            // A は幅マスクとして使われる。既存値があれば残し、無ければ 1（全幅）にする。
            var colors = mesh.colors;
            bool hasColors = colors != null && colors.Length == vertices.Length;

            var result = new Color[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 n = normals[i].normalized;
                Vector3 t = ((Vector3)tangents[i]).normalized;

                // 従接線の作り方はシェーダー側と厳密に一致させること。
                // 符号がずれると輪郭が内側に潜る。
                Vector3 b = (Vector3.Cross(normals[i], (Vector3)tangents[i]) * tangents[i].w).normalized;

                Vector3 s  = smooth[i];
                Vector3 ts = new Vector3(Vector3.Dot(s, t), Vector3.Dot(s, b), Vector3.Dot(s, n));

                result[i] = new Color(
                    ts.x * 0.5f + 0.5f,
                    ts.y * 0.5f + 0.5f,
                    ts.z * 0.5f + 0.5f,
                    hasColors ? colors[i].a : 1f);
            }

            mesh.colors = result;
            return mesh;
        }

        /// <summary>
        /// 同じ位置にある頂点の法線を足し合わせて正規化する。
        /// 面積や角度で重み付けしないのは、インポート時点の法線が既にその重みを含んでいるため。
        /// </summary>
        private static Vector3[] AverageByPosition(Vector3[] vertices, Vector3[] normals)
        {
            var accum = new Dictionary<Vector3, Vector3>(vertices.Length);

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 key = Quantize(vertices[i]);
                accum[key] = accum.TryGetValue(key, out var sum) ? sum + normals[i] : normals[i];
            }

            var smooth = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 sum = accum[Quantize(vertices[i])];

                // 背中合わせのポリゴンで打ち消し合った場合は元の法線に戻す。
                smooth[i] = sum.sqrMagnitude > 1e-8f ? sum.normalized : normals[i].normalized;
            }

            return smooth;
        }

        private static Vector3 Quantize(Vector3 v)
        {
            return new Vector3(
                Mathf.Round(v.x * PositionGrid),
                Mathf.Round(v.y * PositionGrid),
                Mathf.Round(v.z * PositionGrid));
        }

        private const string Suffix = "_SmoothNormals";

        // 元のメッシュが書き込めない場所にあるときの逃がし先。
        private const string FallbackFolder = "Assets/SmoothNormals";

        /// <summary>このツールが以前に作ったアセットか。焼き直しかどうかの判定に使う。</summary>
        private static bool IsBakedAsset(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);

            return !string.IsNullOrEmpty(path)
                && path.EndsWith($"{Suffix}.asset", System.StringComparison.OrdinalIgnoreCase)
                && AssetDatabase.LoadMainAssetAtPath(path) == mesh;
        }

        /// <summary>保存に失敗したら null を返す。呼び出し側はその場合メッシュを割り当てない。</summary>
        private static string SaveAsset(Mesh mesh, Mesh source, HashSet<string> claimed)
        {
            string directory = ResolveOutputFolder(source);
            if (directory == null) return null;

            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{directory}/{mesh.name}{Suffix}.asset".Replace('\\', '/'));

            // GenerateUniqueAssetPath は StartAssetEditing 中に作ったものを見ないので、
            // 同名メッシュが2つあると同じパスを返してくる。手前で連番を足す。
            if (!claimed.Add(path))
            {
                string basePath = path.Substring(0, path.Length - ".asset".Length);
                for (int i = 1; !claimed.Add(path); i++) path = $"{basePath} {i}.asset";
            }

            try
            {
                AssetDatabase.CreateAsset(mesh, path);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"{Marker} {path} に保存できなかった: {e.Message}");
                return null;
            }

            // CreateAsset は失敗しても例外を出さないことがある。
            // 永続化を確認できないまま Renderer に入れると参照が消える。
            if (!AssetDatabase.Contains(mesh))
            {
                Debug.LogError($"{Marker} {path} への保存が反映されなかった。");
                return null;
            }

            return path;
        }

        /// <summary>
        /// 書き込めるフォルダを決める。
        ///
        /// 元のメッシュは Assets の外にあることがある:
        ///   Cube などの組み込みメッシュ → "Library/unity default resources"
        ///   パッケージ同梱のモデル       → "Packages/..."（多くは変更不可）
        ///   スクリプトで生成したメッシュ → 空文字列
        /// そのまま Path.GetDirectoryName に渡すと Library や Packages に
        /// 書きにいって失敗する。Assets 配下でなければ逃がし先へ回す。
        /// </summary>
        private static string ResolveOutputFolder(Mesh source)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source).Replace('\\', '/');

            if (sourcePath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                string directory = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
                if (AssetDatabase.IsValidFolder(directory)) return directory;
            }

            // StartAssetEditing 中に作ったフォルダは AssetDatabase にまだ映らない。
            // IsValidFolder だけで判定すると2件目以降で作り直そうとして失敗する。
            if (!AssetDatabase.IsValidFolder(FallbackFolder) && !Directory.Exists(FallbackFolder))
            {
                string guid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(FallbackFolder));
                if (string.IsNullOrEmpty(guid))
                {
                    Debug.LogError($"{Marker} {FallbackFolder} を作れなかった。");
                    return null;
                }
            }

            return FallbackFolder;
        }

        private static void AssignMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                Undo.RecordObject(skinned, "Bake Smooth Normals");
                skinned.sharedMesh = mesh;
                EditorUtility.SetDirty(skinned);
                return;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null) return;

            Undo.RecordObject(filter, "Bake Smooth Normals");
            filter.sharedMesh = mesh;
            EditorUtility.SetDirty(filter);
        }
    }
}

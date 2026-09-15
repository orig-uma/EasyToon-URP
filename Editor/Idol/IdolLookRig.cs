// =============================================================================
//  IdolLookRig.cs — ルック用ライティング（キー・リム・フィル・環境光・ポスト）を 1 手で置く（T-405）
// -----------------------------------------------------------------------------
//  なぜ要るか: 同じシェーダー・同じ材質でも、正面からの弱い光と平坦な環境光の下では
//  何を当てても平坦に見える。参考にした実機のライブ映像は「斜め上からの強いキー・
//  背面からの冷たいリム・弱いフィル・暗い環境光・Bloom ＋ ACES」で、質感の差の大半は
//  ここにあった（Editor から直接描いて確認。BACKLOG T-405）。
//
//  置くもの: "Idol Look Rig" の下に Key / Rim / Fill の Directional Light と、
//  グローバル Volume（Bloom・Tonemapping ACES・Color Adjustments）。Volume の
//  プロファイルは Assets/IdolLookRig/IdolLook.volumeprofile に保存する（編集して育てる前提）。
//  環境光は Flat の暗い青に切り替える（Undo 可）。既存のライトは触らない ── 消すかどうかは
//  利用者が決める（Console に本数を出す）。
// =============================================================================
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ToonNPR.EditorTools
{
    internal static class IdolLookRig
    {
        private const string RootName = "Idol Look Rig";
        private const string ProfileDir = "Assets/IdolLookRig";
        private const string ProfilePath = ProfileDir + "/IdolLook.volumeprofile";

        [MenuItem("Tools/Idol/ルック用ライティングを配置", false, 30)]
        public static void Place()
        {
            var existing = GameObject.Find(RootName);
            if (existing != null)
            {
                EditorGUIUtility.PingObject(existing);
                Debug.Log("[EasyToon] Idol Look Rig は既に置かれています。値を変えるならその中のライトと Volume を編集してください。");
                return;
            }

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Idol Look Rig");

            // キー: 斜め上・やや前から。影を落とす唯一の光。
            MakeLight(root, "Key", 2.2f, new Vector3(35f, 210f, 0f), new Color(1.00f, 0.96f, 0.90f), LightShadows.Soft);
            // リム: 背面から冷たく。輪郭を起こす。影は落とさない。
            MakeLight(root, "Rim", 1.5f, new Vector3(20f, 30f, 0f), new Color(0.80f, 0.90f, 1.00f), LightShadows.None);
            // フィル: 反対側から弱く。影側を真っ黒にしない。
            MakeLight(root, "Fill", 0.3f, new Vector3(10f, 150f, 0f), new Color(0.70f, 0.80f, 1.00f), LightShadows.None);

            // ポスト
            var profile = LoadOrCreateProfile();
            var vg = new GameObject("Post (Volume)");
            vg.transform.SetParent(root.transform, false);
            var vol = vg.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;
            vol.sharedProfile = profile;

            // 環境光: 暗い青の Flat。スカイボックス由来の明るい環境光は影側を平坦にする。
            // RenderSettings は Undo に載らないので、元の値を Console に残す。
            Debug.Log($"[EasyToon] 環境光を Flat (0.06, 0.07, 0.10) にしました。元: mode={RenderSettings.ambientMode} color={RenderSettings.ambientLight}");
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.06f, 0.07f, 0.10f);

            int others = 0;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.enabled && l.transform.root != root.transform) others++;
            if (others > 0)
                Debug.Log($"[EasyToon] Idol Look Rig を置きました。シーンに元からあるライトが {others} 本あります。" +
                          "リグの絵を見るときは元のライトを無効にしてください（自動では触りません）。");
            else
                Debug.Log("[EasyToon] Idol Look Rig を置きました。");

            Selection.activeGameObject = root;
        }

        private static Light MakeLight(GameObject root, string name, float intensity, Vector3 euler, Color color, LightShadows shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.eulerAngles = euler;
            var li = go.AddComponent<Light>();
            li.type = LightType.Directional;
            li.intensity = intensity;
            li.color = color;
            li.shadows = shadows;
            li.shadowStrength = 1f;
            // URP の追加データ（ソフトシャドウの品質など）は既定のまま
            go.AddComponent<UniversalAdditionalLightData>();
            return li;
        }

        private static VolumeProfile LoadOrCreateProfile()
        {
            var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (p != null) return p;
            if (!Directory.Exists(ProfileDir)) Directory.CreateDirectory(ProfileDir);
            p = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(p, ProfilePath);

            // 各コンポーネントはプロファイルのサブアセットとして保存する（Volume の作法）
            var bloom = p.Add<Bloom>(true);
            bloom.intensity.Override(0.25f);
            bloom.threshold.Override(0.9f);
            AssetDatabase.AddObjectToAsset(bloom, p);

            var tone = p.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            AssetDatabase.AddObjectToAsset(tone, p);

            var ca = p.Add<ColorAdjustments>(true);
            ca.contrast.Override(10f);
            ca.saturation.Override(10f);
            AssetDatabase.AddObjectToAsset(ca, p);

            AssetDatabase.SaveAssets();
            return p;
        }
    }
}

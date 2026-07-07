// =============================================================================
//  EasyShaderCoreInstaller.cs
// -----------------------------------------------------------------------------
//  依存パッケージ EasyShaderCore（com.origuma.easyshader-core）の不在を起動時に
//  検知し、ワンクリックインストールを案内する EditorWindow。
//
//  設計の核: Core 不在時は本体の Editor asmdef（Origuma.EasyShaderCore.Editor を
//  参照）がコンパイルエラーで無効化されるため、このインストーラーは
//  **参照ゼロの独立 asmdef**（Origuma.EasyToon.URP.Installer）に分離してあり、
//  Core 不在でも必ずコンパイル・実行される。UnityEditor / UnityEngine のみで完結。
//
//  挙動:
//   - 起動時（ドメインリロード毎）に Client.List(offline) で Core の有無を確認
//   - 不在のときだけウィンドウを表示。存在すれば何もしない（ログも出さない）
//   - [インストール] は Client.Add(git URL)。[後で] / ウィンドウを閉じると
//     SessionState により同一セッション中は再表示しない（Unity 再起動で再案内）
//   - バッチモードでは何もしない
// =============================================================================
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Origuma.EasyToon.URP.Installer
{
    public class EasyShaderCoreInstaller : EditorWindow
    {
        private const string PackageDisplayName = "EasyToon";
        private const string WindowTitle = "EasyToon Setup";
        private const string CorePackageName = "com.origuma.easyshader-core";
        private const string CoreGitUrl = "https://github.com/orig-uma/EasyShaderCore-URP.git";
        private const string SessionDismissKey = "Origuma.EasyToon.URP.Installer.Dismissed";

        private static ListRequest s_ListRequest;

        private AddRequest _addRequest;
        private string _errorMessage;
        private bool _installed;

        // ------------------------------------------------------------------
        //  起動時チェック（ドメインリロード毎。Core があれば完全に無音）
        // ------------------------------------------------------------------
        [InitializeOnLoadMethod]
        private static void CheckOnLoad()
        {
            if (Application.isBatchMode) return;

            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool(SessionDismissKey, false)) return;
                if (s_ListRequest != null && !s_ListRequest.IsCompleted) return;

                // オフラインのインストール済み一覧（間接依存を含む）で Core を探す。
                s_ListRequest = Client.List(true, true);
                EditorApplication.update += PollListRequest;
            };
        }

        private static void PollListRequest()
        {
            if (s_ListRequest == null || !s_ListRequest.IsCompleted) return;
            EditorApplication.update -= PollListRequest;

            if (s_ListRequest.Status != StatusCode.Success)
                return; // 一覧取得に失敗した場合は何もしない（次回リロードで再試行）

            foreach (var package in s_ListRequest.Result)
                if (package.name == CorePackageName)
                    return; // Core あり: 何もしない（ウィンドウもログも出さない）

            Open();
        }

        private static void Open()
        {
            var window = GetWindow<EasyShaderCoreInstaller>(true, WindowTitle, true);
            window.minSize = new Vector2(440, 240);
            window.Show();
        }

        // ------------------------------------------------------------------
        //  UI
        // ------------------------------------------------------------------
        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"{PackageDisplayName} には EasyShaderCore が必要です",
                EditorStyles.boldLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"本パッケージ（{PackageDisplayName}）は共通基盤 EasyShaderCore" +
                $"（{CorePackageName}）が必要です。未インストールのため、現在シェーダー・" +
                "エディタ拡張が動作していません。下のボタンでインストールできます（git が必要）。",
                MessageType.Warning);

            EditorGUILayout.Space(6);

            bool installing = _addRequest != null && !_addRequest.IsCompleted;

            if (_installed)
            {
                EditorGUILayout.HelpBox("インストールしました。再コンパイル後に有効になります。",
                    MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(_errorMessage))
            {
                EditorGUILayout.HelpBox(
                    $"インストールに失敗しました: {_errorMessage}\n" +
                    "git がインストールされ PATH が通っているか確認するか、下の手動手順を使用してください。",
                    MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(installing || _installed))
            {
                if (GUILayout.Button(
                        installing ? "インストール中..." : "EasyShaderCore をインストール",
                        GUILayout.Height(32)))
                {
                    _errorMessage = null;
                    _addRequest = Client.Add(CoreGitUrl);
                    EditorApplication.update += PollAddRequest;
                }

                EditorGUILayout.Space(2);
                if (GUILayout.Button("後で（このセッション中は表示しない）"))
                {
                    Close(); // OnDestroy が SessionState を立てる
                }
            }

            // 手動手順（コピー可能な git URL）。
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("手動でインストールする場合", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(
                "Window > Package Manager > + > Add package from git URL... に以下を入力:",
                EditorStyles.miniLabel);
            EditorGUILayout.TextField(CoreGitUrl);
        }

        private void PollAddRequest()
        {
            if (_addRequest == null || !_addRequest.IsCompleted)
            {
                Repaint();
                return;
            }
            EditorApplication.update -= PollAddRequest;

            if (_addRequest.Status == StatusCode.Success)
            {
                _installed = true;
                Repaint();
                // 成功を明示してから自動クローズ（この後 UPM 解決→再コンパイルが走る）。
                EditorUtility.DisplayDialog(WindowTitle,
                    "EasyShaderCore をインストールしました。再コンパイル後に有効になります。", "OK");
                Close();
            }
            else
            {
                _errorMessage = _addRequest.Error != null ? _addRequest.Error.message : "unknown error";
                _addRequest = null;
                Repaint();
            }
        }

        // どの経路で閉じても同一セッション中は再表示しない（ポップアップ抑止）。
        private void OnDestroy()
        {
            SessionState.SetBool(SessionDismissKey, true);
            EditorApplication.update -= PollAddRequest;
        }
    }
}

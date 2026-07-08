// =============================================================================
//  EasyShaderCoreInstaller.cs
// -----------------------------------------------------------------------------
//  依存パッケージ EasyShaderCore（com.origuma.easyshader-core）の不在を起動時に
//  検知し、**自動でインストール**する（ゼロクリック）。失敗時のみ手動手順つきの
//  案内ウィンドウを表示する。
//
//  設計の核:
//   - package.json の dependencies に Core を宣言しない。宣言すると UPM が
//     レジストリ解決に失敗して本パッケージの git URL インストール自体を拒否
//     するため、「URL 1 つで入れて依存は自動導入」という体験が成立しなくなる
//   - Core 不在時は本体の Editor asmdef（Origuma.EasyShaderCore.Editor を参照）
//     がコンパイルエラーで無効化されるため、このインストーラーは**参照ゼロの
//     独立 asmdef**（Origuma.EasyToon.URP.Installer）に分離してあり、Core 不在
//     でも必ずコンパイル・実行される。UnityEditor / UnityEngine のみで完結
//
//  挙動:
//   - 起動時（ドメインリロード毎）に Client.List(offline) で Core の有無を確認
//   - 不在なら Client.Add(タグ固定の git URL) を自動実行（1 セッション 1 回）
//     → 成功すれば UPM 解決→再コンパイルで全機能が有効になる
//   - 自動インストール失敗時のみウィンドウ表示（手動手順・エラー内容つき）。
//     [後で] / ウィンドウを閉じると同一セッション中は再表示しない
//   - Core 存在時・バッチモードでは何もしない（ログも出さない）
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
        private const string CoreGitUrl = "https://github.com/orig-uma/EasyShaderCore.git";
        // 動作検証済みバージョンにピン留めした自動インストール用 URL。
        private const string CoreGitUrlPinned = CoreGitUrl + "#v0.2.0";
        private const string SessionDismissKey = "Origuma.EasyToon.URP.Installer.Dismissed";
        private const string SessionAutoAddKey = "Origuma.EasyToon.URP.Installer.AutoAddAttempted";

        private static ListRequest s_ListRequest;
        private static AddRequest s_AutoAddRequest;

        private AddRequest _addRequest;
        private string _errorMessage;
        private bool _installed;

        // ------------------------------------------------------------------
        //  起動時チェック（ドメインリロード毎。Core があれば完全に無音）
        //
        //  Core 不在時はまず Client.Add による自動インストールを試みる（ゼロクリック）。
        //  ※ package.json の dependencies に Core を宣言しないのはこのため:
        //    宣言すると UPM がレジストリ解決に失敗して本パッケージの追加自体を
        //    拒否するため、この自動インストールが実行される機会がなくなる。
        //  自動インストールに失敗した場合のみ案内ウィンドウ（手動手順）を開く。
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

            // --- 自動インストール（1 セッション 1 回だけ試行。失敗時はウィンドウへ） ---
            if (!SessionState.GetBool(SessionAutoAddKey, false))
            {
                SessionState.SetBool(SessionAutoAddKey, true);
                Debug.Log($"[{PackageDisplayName}] 依存パッケージ EasyShaderCore が見つからないため、" +
                          $"自動インストールします: {CoreGitUrlPinned}");
                s_AutoAddRequest = Client.Add(CoreGitUrlPinned);
                EditorApplication.update += PollAutoAddRequest;
                return;
            }

            Open();
        }

        private static void PollAutoAddRequest()
        {
            if (s_AutoAddRequest == null || !s_AutoAddRequest.IsCompleted) return;
            EditorApplication.update -= PollAutoAddRequest;

            if (s_AutoAddRequest.Status == StatusCode.Success)
            {
                // 成功: この後 UPM の解決→再コンパイルが走り、全機能が有効になる。
                Debug.Log($"[{PackageDisplayName}] EasyShaderCore をインストールしました。再コンパイル後に有効になります。");
            }
            else
            {
                var msg = s_AutoAddRequest.Error != null ? s_AutoAddRequest.Error.message : "unknown error";
                Debug.LogWarning($"[{PackageDisplayName}] EasyShaderCore の自動インストールに失敗しました: {msg}");
                Open(); // フォールバック: 手動手順つきの案内ウィンドウ
            }
            s_AutoAddRequest = null;
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
                    _addRequest = Client.Add(CoreGitUrlPinned);
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
            EditorGUILayout.TextField(CoreGitUrlPinned);
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

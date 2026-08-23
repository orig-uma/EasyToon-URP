// =============================================================================
//  EasyShaderCoreInstaller.cs
// -----------------------------------------------------------------------------
//  依存パッケージ EasyShaderCore（com.origuma.easyshader-core）が無ければ自動で
//  インストールする（ゼロクリック）。失敗時のみ手動手順つきのウィンドウを出す。
//
//  ハマりやすい設計上の 2 点:
//   - package.json の dependencies に Core を書かない。書くと UPM がレジストリ
//     解決に失敗し、本パッケージの git URL インストール自体が拒否される。
//   - 本体 Editor asmdef は Core 不在時にコンパイルエラーにしない（asmdef の
//     versionDefines + defineConstraints で除外）。エラーがあると Unity はドメイン
//     リロードを完了せず、PM 追加直後に InitializeOnLoad が走らない（＝再起動まで
//     自動インストールされない）。このインストーラー自身は参照ゼロの独立 asmdef
//     なので Core 不在でもコンパイル・実行できる。
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
        private const string CoreGitUrlPinned = CoreGitUrl + "#v0.3.1";
        // **必要最低バージョン。** これより古い Core が入っていると本体 Editor が
        // Core の新 API を参照してコンパイルできない。本体 Editor asmdef の
        // versionDefines は同じ下限（[0.3.0,)）で、古い Core では本体を除外して
        // コンパイルエラーを出さず、このインストーラーが更新に進めるようにしてある。
        // **両方を同時に上げること。** 片方だけ上げると「本体は除外されたのに
        // インストーラーは何もしない」か、その逆になる。
        private const string CoreMinVersion = "0.3.0";
        private const string SessionDismissKey = "Origuma.EasyToon.URP.Installer.Dismissed";
        private const string SessionAutoAddKey = "Origuma.EasyToon.URP.Installer.AutoAddAttempted";

        private static ListRequest s_ListRequest;
        private static AddRequest s_AutoAddRequest;

        private AddRequest _addRequest;
        private string _errorMessage;
        private bool _installed;

        // 起動時・PM 追加直後（ドメインリロード毎）に Core の有無を確認する入口。
        // Core があれば完全に無音。無ければ PollListRequest で自動インストールへ進む。
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

            string installedVersion = null;
            foreach (var package in s_ListRequest.Result)
                if (package.name == CorePackageName)
                {
                    // 十分新しい Core あり: 何もしない（ウィンドウもログも出さない）。
                    // 古い Core は「無い」と同じ扱いで、ピン留め URL への差し替えに進む
                    // （Client.Add は同名パッケージの更新として働く）。本パッケージを
                    // 更新した利用者が旧 Core のまま取り残されるのを防ぐ。
                    if (!IsOlderThan(package.version, CoreMinVersion)) return;
                    installedVersion = package.version;
                    break;
                }

            // --- 自動インストール / 更新（1 セッション 1 回だけ試行。失敗時はウィンドウへ） ---
            if (!SessionState.GetBool(SessionAutoAddKey, false))
            {
                SessionState.SetBool(SessionAutoAddKey, true);
                Debug.Log(installedVersion == null
                    ? $"[{PackageDisplayName}] 依存パッケージ EasyShaderCore が見つからないため、" +
                      $"自動インストールします: {CoreGitUrlPinned}"
                    : $"[{PackageDisplayName}] EasyShaderCore {installedVersion} は古いため" +
                      $"（必要 {CoreMinVersion} 以上）、更新します: {CoreGitUrlPinned}");
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

        /// <summary>"x.y.z" 同士の比較。数字以外の接尾辞（-preview 等）は無視する。</summary>
        private static bool IsOlderThan(string version, string minimum)
        {
            var a = ParseVersion(version);
            var b = ParseVersion(minimum);
            for (int i = 0; i < 3; i++)
            {
                if (a[i] < b[i]) return true;
                if (a[i] > b[i]) return false;
            }
            return false;
        }

        private static int[] ParseVersion(string version)
        {
            var parts = new int[3];
            if (string.IsNullOrEmpty(version)) return parts;
            var core = version.Split('-', '+')[0].Split('.');
            for (int i = 0; i < 3 && i < core.Length; i++)
                int.TryParse(core[i], out parts[i]);
            return parts;
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

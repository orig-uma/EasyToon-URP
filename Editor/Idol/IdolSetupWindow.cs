// =============================================================================
//  IdolSetupWindow.cs
// -----------------------------------------------------------------------------
//  Idol の RendererFeature 3 種（ToonOutlineFeature / HairSeeThroughFeature /
//  ）を UniversalRendererData に追加 / 削除する
//  セットアップ用 EditorWindow。描画とロジックは EasyShaderCore の
//  FeatureSetupWindowBase / FeatureSetup に委譲し、ここではタイトルと
//  Feature エントリの宣言のみを行う。
//
//  **なぜ要るか。** 基底の FeatureSetupWindowBase はそもそも旧 IdolSetupWindow
//  の UI を汎用化して Core へ移管したものだが、移管後に Idol 用の派生が
//  再作成されておらず、Idol の Feature だけワンクリック導入の入口が無かった
//  （ToonPBRSetupCheck が事後に「無い」と叱るだけ。T-340）。
// =============================================================================
using UnityEditor;
using UnityEngine;
using Origuma.EasyShaderCore.Editor;

namespace ToonNPR.EditorTools
{
    public class IdolSetupWindow : FeatureSetupWindowBase
    {
        [MenuItem("Window/Origuma/Idol Setup")]
        public static void Open()
        {
            var window = GetWindow<IdolSetupWindow>(false, "Idol Setup");
            window.minSize = new Vector2(420, 320);
            window.Show();
        }

        protected override string HeaderLabel => "Idol RendererFeature セットアップ";

        protected override string Description =>
            "Idol のアウトライン（LightMode = \"IdolOutline\"）と前髪透過" +
            "（LightMode = \"IdolHairSeeThrough\"）は RendererFeature が" +
            "描画します。画面空間輪郭も同様です。対象の Universal Renderer Data に" +
            "必要な Feature を追加してください（使わない Feature は追加不要）。";

        protected override FeatureEntry[] Entries => s_Entries;

        private static readonly FeatureEntry[] s_Entries =
        {
            new FeatureEntry(typeof(ToonOutlineFeature), "Toon Outline",
                "アウトライン描画に必須（マテリアルの Enable Outline とセット）"),
            new FeatureEntry(typeof(HairSeeThroughFeature), "Hair See-Through",
                "前髪透過（眉・目が髪越しに見える）に必須"),
        };
    }
}

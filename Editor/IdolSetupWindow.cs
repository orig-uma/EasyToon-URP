// =============================================================================
//  IdolSetupWindow.cs
// -----------------------------------------------------------------------------
//  Idol の RendererFeature 2 種（IdolOutlineFeature / IdolCharShadowFeature）を
//  UniversalRendererData に追加 / 削除するセットアップ用 EditorWindow。
//  描画とロジックは EasyShaderCore の FeatureSetupWindowBase / FeatureSetup に
//  委譲し、ここではタイトルと Feature エントリの宣言のみを行う。
// =============================================================================
using UnityEditor;
using UnityEngine;
using Origuma.EasyShaderCore.Editor;

namespace Origuma.EasyToon.URP.Editor
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
            "Idol のアウトライン（LightMode = \"IdolOutline\"）とキャラ専用セルフシャドウ" +
            "（LightMode = \"IdolCharShadow\"）は RendererFeature が描画します。" +
            "対象の Universal Renderer Data に必要な Feature を追加してください。";

        protected override FeatureEntry[] Entries => s_Entries;

        private static readonly FeatureEntry[] s_Entries =
        {
            new FeatureEntry(typeof(IdolOutlineFeature), "Idol Outline",
                "アウトライン描画に必須"),
            new FeatureEntry(typeof(IdolCharShadowFeature), "Idol Char Shadow",
                "キャラ専用セルフシャドウ（髪→顔の落ち影）"),
        };
    }
}

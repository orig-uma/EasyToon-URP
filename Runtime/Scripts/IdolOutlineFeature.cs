// =============================================================================
//  IdolOutlineFeature.cs
//  EasyToon/Idol のアウトライン（LightMode = "IdolOutline"）を描画する
//  ScriptableRendererFeature。
//
//  Idol シェーダーの Outline パスは独自 LightMode タグを持ち、URP の既定の
//  不透明描画には含まれない。これにより ForwardLit と Outline が交互描画されず、
//  ForwardLit 同士が SRP Batcher でまとまる。アウトラインはこの Feature が
//  別の DrawRenderers として後段でまとめて描くため、Outline 同士もバッチされる。
//
//  対象の UniversalRendererData に手動で追加すること。
//
//  ※ Render Graph 前提（URP 17 / Unity 6）。Compatibility Mode では動作しない。
// =============================================================================
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Origuma.EasyToon.URP
{
    public class IdolOutlineFeature : ScriptableRendererFeature
    {
        [Tooltip("アウトラインを描画するタイミング。通常は不透明描画の後でよい。")]
        [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingOpaques;

        private IdolOutlinePass _pass;

        public override void Create()
        {
            _pass = new IdolOutlinePass { renderPassEvent = _injectionPoint };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(_pass);
        }

        // ---------------------------------------------------------------------
        //  描画パス（Render Graph）
        // ---------------------------------------------------------------------
        private class IdolOutlinePass : ScriptableRenderPass
        {
            // シェーダー側の Outline パスの LightMode タグと一致させること。
            private static readonly ShaderTagId s_OutlineTag = new ShaderTagId("IdolOutline");

            private class PassData
            {
                public RendererListHandle rendererList;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData  = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData    = frameData.Get<UniversalCameraData>();

                using var builder = renderGraph.AddRasterRenderPass<PassData>("Idol Outline", out var passData);

                // アウトラインは per-object ライトデータを必要としないので DrawingSettings を
                // 公開 API で手組みする。SRP Batcher はマテリアル CBUFFER 互換なら自動で効く。
                var sortingSettings = new SortingSettings(cameraData.camera) { criteria = cameraData.defaultOpaqueSortFlags };
                var drawSettings = new DrawingSettings(s_OutlineTag, sortingSettings)
                {
                    perObjectData         = PerObjectData.None,
                    enableDynamicBatching = renderingData.supportsDynamicBatching,
                    enableInstancing      = true,
                };
                var filterSettings = new FilteringSettings(RenderQueueRange.opaque);

                var listParams = new RendererListParams(renderingData.cullResults, drawSettings, filterSettings);
                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                // 既存の不透明描画結果（カラー・深度）に対して描く。
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}

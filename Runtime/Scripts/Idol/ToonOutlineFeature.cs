using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ToonNPR
{
    /// <summary>
    /// アウトライン（背面法線押し出し）をまとめて描く Renderer Feature。
    ///
    /// なぜ必要か:
    ///   URP の不透明描画は UniversalForward と SRPDefaultUnlit を同じ描画パスで処理する。
    ///   アウトラインを SRPDefaultUnlit に置くと [本体][輪郭][本体][輪郭] と交互に描かれ、
    ///   **ForwardLit が SRP Batcher でまとまらなくなる。** しかもこれは
    ///   アウトラインを使っていないマテリアルにも波及する（パスが存在するだけで起きる）。
    ///
    ///   そこで Outline パスには独自の LightMode "IdolOutline" を付けてある。
    ///   URP は既定でこのタグを描かないので、ForwardLit は素でバッチングされ、
    ///   アウトラインはこの Feature がまとめて描くのでアウトライン同士もバッチされる。
    ///
    /// 代償として、**この Feature を Renderer Data に追加しないとアウトラインが出ない。**
    /// 設計は EasyPBR の DollOutlineFeature に倣った。
    /// </summary>
    public class ToonOutlineFeature : ScriptableRendererFeature
    {
        [SerializeField]
        private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingOpaques;

        private ToonOutlinePass _pass;

        public override void Create()
        {
            _pass = new ToonOutlinePass { renderPassEvent = _injectionPoint };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(_pass);
        }

        private class ToonOutlinePass : ScriptableRenderPass
        {
            // シェーダー側の Outline パスの LightMode タグと一致させること。
            private static readonly ShaderTagId OutlineTag = new ShaderTagId("IdolOutline");

            private class PassData
            {
                public RendererListHandle rendererList;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData  = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData    = frameData.Get<UniversalCameraData>();

                // サムネイルやプローブの焼き込みに輪郭は要らない。
                if (cameraData.cameraType == CameraType.Preview ||
                    cameraData.cameraType == CameraType.Reflection) return;

                using var builder = renderGraph.AddRasterRenderPass<PassData>("Toon Outline", out var passData);

                // アウトラインは per-object のライトデータを使わないので、
                // DrawingSettings を公開 API だけで手組みする。
                var sorting = new SortingSettings(cameraData.camera)
                {
                    criteria = cameraData.defaultOpaqueSortFlags,
                };

                var drawSettings = new DrawingSettings(OutlineTag, sorting)
                {
                    perObjectData         = PerObjectData.None,
                    enableDynamicBatching = renderingData.supportsDynamicBatching,
                    enableInstancing      = true,
                };

                var filterSettings = new FilteringSettings(RenderQueueRange.opaque);
                var listParams = new RendererListParams(renderingData.cullResults, drawSettings, filterSettings);

                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                // 任意のシェーダーを描くパスなので、グローバルテクスチャを読む
                // マテリアルが混ざっても RenderGraph が生存期間を誤らないようにする
                // （URP 本体の DrawObjectsPass も同じ宣言をしている）。
                builder.UseAllGlobalTextures(true);

                // 既に描かれた不透明の結果に対して重ねる。
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

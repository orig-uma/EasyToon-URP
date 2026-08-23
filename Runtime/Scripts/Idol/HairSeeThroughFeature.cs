using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ToonNPR
{
    /// <summary>
    /// 前髪透過（HairSeeThrough）をまとめて描く Renderer Feature。
    ///
    /// なぜ必要か（T-341。診断と処方は T-040 のアウトラインと同一）:
    ///   URP の不透明描画は UniversalForward と SRPDefaultUnlit を同じ描画パスで処理する。
    ///   前髪透過を SRPDefaultUnlit に置くと [本体][透過][本体][透過] と交互に描かれ、
    ///   **SetPass が跳ね上がって ForwardLit が SRP Batcher でまとまらない。**
    ///   しかも透過を使っていないマテリアルにも波及する（パスが存在するだけで起きる）。
    ///
    ///   そこで HairSeeThrough パスには独自の LightMode "IdolHairSeeThrough" を付けた。
    ///   URP は既定でこのタグを描かないので ForwardLit は素でバッチングされ、
    ///   透過はこの Feature が不透明の後にまとめて描く（透過同士もバッチされる）。
    ///   「眉・目 → 髪透過」の描画順は Queue 頼みから**構造の保証**に変わる
    ///   （不透明が全部終わってから描くため）。
    ///
    /// 代償として、**この Feature を Renderer Data に追加しないと前髪透過が出ない**
    /// （Window &gt; Origuma &gt; Idol Setup から 1 クリックで追加できる）。
    /// 使わないマテリアルのパス停止（Tools &gt; Idol &gt; 使っていない重ね描きパスを止める）は
    /// 引き続き有効 ── 止めていない材質はこの Feature が描いてしまうため。
    /// 設計は ToonOutlineFeature（T-040）に倣った。**Render Graph 前提。**
    /// </summary>
    public class HairSeeThroughFeature : ScriptableRendererFeature
    {
        [SerializeField]
        private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingOpaques;

        private HairSeeThroughPass _pass;

        public override void Create()
        {
            _pass = new HairSeeThroughPass { renderPassEvent = _injectionPoint };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(_pass);
        }

        private class HairSeeThroughPass : ScriptableRenderPass
        {
            // シェーダー側の HairSeeThrough パスの LightMode タグと一致させること。
            private static readonly ShaderTagId SeeThroughTag = new ShaderTagId("IdolHairSeeThrough");

            private class PassData
            {
                public RendererListHandle rendererList;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData  = frameData.Get<UniversalResourceData>();
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData    = frameData.Get<UniversalCameraData>();

                // サムネイルやプローブの焼き込みに前髪透過は要らない（Outline と同じ扱い）。
                if (cameraData.cameraType == CameraType.Preview ||
                    cameraData.cameraType == CameraType.Reflection) return;

                using var builder = renderGraph.AddRasterRenderPass<PassData>("Hair See-Through", out var passData);

                var sorting = new SortingSettings(cameraData.camera)
                {
                    criteria = cameraData.defaultOpaqueSortFlags,
                };

                // Outline と違い**フル シェーディング**のパスなので、per-object の
                // ライトデータ（SH・プローブ・追加光源のインデックス）を素通しで渡す。
                // None にすると穴の縁で本体と明るさが割れる（シェーダー側コメントの
                // 「同じ髪なのに明るさが違う」がここでも起きる）。
                var drawSettings = new DrawingSettings(SeeThroughTag, sorting)
                {
                    perObjectData         = renderingData.perObjectData,
                    enableDynamicBatching = renderingData.supportsDynamicBatching,
                    enableInstancing      = true,
                };

                var filterSettings = new FilteringSettings(RenderQueueRange.opaque);
                var listParams = new RendererListParams(renderingData.cullResults, drawSettings, filterSettings);

                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                builder.UseAllGlobalTextures(true);

                // 不透明の結果へブレンドで重ねる（パス側が Blend SrcAlpha / ZWrite Off）。
                // ステンシル（眉 2 / 目 4）の判定があるので深度は読み書き両方を宣言する。
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

// =============================================================================
//  IdolCharShadowFeature.cs
// -----------------------------------------------------------------------------
//  キャラ専用セルフシャドウ（キャラ限定深度描画方式）を描く ScriptableRendererFeature。
//  IdolCharacter.ActiveCharacters の合成 Bounds を、メインライト方向から包む
//  正射影 VP でクリーンな深度マップ（_IdolCharShadowMap）へ描く。シェーダー側
//  は _IDOL_CHARSHADOW 有効時にこれを 3x3 PCF でサンプルする。
//
//  v1: 全キャラを 1 枚に描く（アトラス割当なし）。LightMode = "IdolCharShadow"。
//
//  グローバル供給:
//    _IdolCharShadowMap    : 専用深度マップ（SetGlobalTextureAfterPass）
//    _IdolCharShadowMatrix : ライト VP（受影・キャスターで共有）
//    _IdolCharShadowParams : (1/解像度, 強度, 有効フラグ, 0)
//    _IdolCharShadowBias   : (深度バイアス, 法線バイアス)  ← キャスター側
//
//  グローバルキーワード _IDOL_CHARSHADOW は登録キャラ>0 かつメインライト在で ON。
//  マテリアルプレビュー/リフレクションプローブのカメラではキーワードに一切
//  触れず即 return する（それらのカメラでライト/キャラ不在と判定されると
//  ゲーム/シーンビューの影がフレーム途中で明滅するため）。
//
//  v1 制限: RendererList はカメラの cullResults ベースのため、カメラ視錐台の
//  外にいるキャラはキャスターとして描かれない（画面外キャラの影が画面内に
//  落ちない）。ライト視点の独立カリング or アトラス割当と合わせて将来対応。
//
//  対象の UniversalRendererData に手動で追加すること。
//  ※ Render Graph 前提（URP 17 / Unity 6）。Compatibility Mode では動作しない。
// =============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Origuma.EasyToon.URP
{
    public class IdolCharShadowFeature : ScriptableRendererFeature
    {
        public enum ShadowResolution { _1024 = 1024, _2048 = 2048, _4096 = 4096 }
        public enum ShadowDepthBits { D16 = 16, D32 = 32 }

        [Header("Char Shadow Map")]
        [SerializeField] private ShadowResolution _resolution = ShadowResolution._2048;
        [SerializeField] private ShadowDepthBits _depthBits = ShadowDepthBits.D32;

        [Header("Bias")]
        [Tooltip("キャスター側の深度バイアス（クリップ空間 z）")]
        [SerializeField] private float _depthBias = 0.0015f;
        [Tooltip("キャスター側の法線バイアス（ワールド長）")]
        [SerializeField] private float _normalBias = 0.02f;

        [Header("Sampling")]
        [Range(0f, 1f)]
        [Tooltip("受影側の影の強さ（0=無効, 1=完全）")]
        [SerializeField] private float _intensity = 1f;

        [Tooltip("バウンディングをライト方向に押し出す余白（近クリップの抜け防止）")]
        [SerializeField] private float _depthPadding = 2f;

        [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.BeforeRenderingShadows;

        private CharShadowPass _pass;

        // グローバルキーワード名（全カメラで共通の状態）。GlobalKeyword.Create は
        // 型初期化子 / ScriptableObject コンストラクタからの呼び出しが禁止されている
        // （初回型参照が ScriptableRenderer のコンストラクタ内で走るため）。文字列
        // オーバーロードの Shader.*Keyword を使い、生成タイミング制約を回避する。
        private const string k_CharShadowKeyword = "_IDOL_CHARSHADOW";

        public override void Create()
        {
            _pass = new CharShadowPass();
            SyncPassSettings();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Create() 時の 1 回コピーだと、アセット追加直後やドメインリロードの
            // タイミングによってはシリアライズ値の反映前の既定値(0)が残り、
            // 解像度 0 のテクスチャ生成で Render Graph が例外を投げる。
            // 毎フレーム同期して常にシリアライズ状態を正とする（コピーは安価）。
            SyncPassSettings();
            renderer.EnqueuePass(_pass);
        }

        private void SyncPassSettings()
        {
            _pass.renderPassEvent = _injectionPoint;
            // enum 値が未初期化(0)で入ってきても安全なようクランプする。
            _pass.resolution   = Mathf.Max((int)_resolution, 256);
            _pass.depthBits    = (int)_depthBits >= 16 ? (int)_depthBits : 32;
            _pass.depthBias    = _depthBias;
            _pass.normalBias   = _normalBias;
            _pass.intensity    = _intensity;
            _pass.depthPadding = _depthPadding;
        }

        // ---------------------------------------------------------------------
        //  描画パス（Render Graph）
        // ---------------------------------------------------------------------
        private class CharShadowPass : ScriptableRenderPass
        {
            private static readonly ShaderTagId s_CharShadowTag = new ShaderTagId("IdolCharShadow");

            private static readonly int s_MatrixId = Shader.PropertyToID("_IdolCharShadowMatrix");
            private static readonly int s_ParamsId = Shader.PropertyToID("_IdolCharShadowParams");
            private static readonly int s_BiasId   = Shader.PropertyToID("_IdolCharShadowBias");
            private static readonly int s_MapId    = Shader.PropertyToID("_IdolCharShadowMap");

            public int resolution;
            public int depthBits;
            public float depthBias;
            public float normalBias;
            public float intensity;
            public float depthPadding;

            private class PassData
            {
                public RendererListHandle rendererList;
                public Matrix4x4 viewProj;
                public Vector4 shadowParams;
                public Vector4 shadowBias;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var renderingData = frameData.Get<UniversalRenderingData>();
                var cameraData    = frameData.Get<UniversalCameraData>();
                var lightData     = frameData.Get<UniversalLightData>();

                // プレビュー / リフレクションプローブのカメラではグローバル
                // キーワードに触れない（メインカメラの状態を明滅させない）。
                if (cameraData.cameraType == CameraType.Preview ||
                    cameraData.cameraType == CameraType.Reflection)
                {
                    return;
                }

                // メインライト（Directional）不在フレーム or 登録キャラ 0 は無効化して終了。
                Vector3 lightDir;
                if (!TryGetMainLightDirection(lightData, out lightDir) ||
                    !TryBuildLightMatrix(lightDir, out Matrix4x4 viewProj))
                {
                    DisableCharShadow();
                    return;
                }

                EnableCharShadow();

                using var builder = renderGraph.AddRasterRenderPass<PassData>("Idol Char Shadow", out var passData);

                // 専用深度テクスチャ（Shadowmap フォーマット・深度のみ）。URP の
                // MainLightShadowCasterPass と同一の作り方。clear=true で far に初期化。
                var depthDesc = new RenderTextureDescriptor(resolution, resolution,
                    RenderTextureFormat.Shadowmap, depthBits)
                {
                    msaaSamples = 1,
                    dimension = TextureDimension.Tex2D,
                };
                TextureHandle shadowMap = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, depthDesc, "_IdolCharShadowMap", true, FilterMode.Bilinear);

                // LightMode="IdolCharShadow" の RendererList（cullResults ベース）。
                var sortingSettings = new SortingSettings { criteria = SortingCriteria.None };
                var drawSettings = new DrawingSettings(s_CharShadowTag, sortingSettings)
                {
                    perObjectData         = PerObjectData.None,
                    enableDynamicBatching = renderingData.supportsDynamicBatching,
                    enableInstancing      = true,
                };
                var filterSettings = new FilteringSettings(RenderQueueRange.all);
                var listParams = new RendererListParams(renderingData.cullResults, drawSettings, filterSettings);
                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                passData.viewProj = viewProj;
                passData.shadowParams = new Vector4(1f / resolution, intensity, 1f, 0f);
                passData.shadowBias   = new Vector4(depthBias, normalBias, 0f, 0f);

                // 専用マップへ深度描画。
                builder.SetRenderAttachmentDepth(shadowMap, AccessFlags.Write);

                // 描画後に全シェーダーへグローバルテクスチャとして供給。
                builder.SetGlobalTextureAfterPass(shadowMap, s_MapId);

                // SetRenderFunc 内で SetGlobalMatrix/Vector（受影側との行列共有）を
                // 呼ぶため、Render Graph にグローバル状態の変更を明示的に許可させる
                //（未許可だと RasterCommandBuffer が InvalidOperationException を投げる）。
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // ライト VP・パラメータ・バイアスをグローバル供給（受影/キャスター共有）。
                    context.cmd.SetGlobalMatrix(s_MatrixId, data.viewProj);
                    context.cmd.SetGlobalVector(s_ParamsId, data.shadowParams);
                    context.cmd.SetGlobalVector(s_BiasId, data.shadowBias);
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }

            // メインライト方向（面→光源）を取得。Directional のみ対象。
            private bool TryGetMainLightDirection(UniversalLightData lightData, out Vector3 dir)
            {
                dir = Vector3.up;
                int idx = lightData.mainLightIndex;
                if (idx < 0) return false;

                var visibleLights = lightData.visibleLights;
                if (idx >= visibleLights.Length) return false;

                var vl = visibleLights[idx];
                if (vl.lightType != LightType.Directional) return false;

                // visibleLight.localToWorldMatrix の forward が「光の進行方向」。
                //  面→光源方向はその逆。
                dir = -((Vector3)vl.localToWorldMatrix.GetColumn(2)).normalized;
                return true;
            }

            // 全キャラの合成 Bounds を包む正射影 VP を組む。
            private bool TryBuildLightMatrix(Vector3 lightDirToLight, out Matrix4x4 viewProj)
            {
                viewProj = Matrix4x4.identity;

                Bounds combined = default;
                bool any = false;
                foreach (var c in IdolCharacter.ActiveCharacters)
                {
                    if (c == null) continue;
                    if (!c.TryGetWorldBounds(out Bounds b)) continue;
                    if (!any) { combined = b; any = true; }
                    else combined.Encapsulate(b);
                }
                if (!any) return false;

                Vector3 center = combined.center;
                float radius = combined.extents.magnitude; // 球で包む（回転に不変）
                if (radius <= 1e-4f) return false;

                // ライト方向から見下ろすビュー行列。光源側から中心を見る。
                Vector3 eye = center + lightDirToLight * (radius + depthPadding);
                Vector3 up = Mathf.Abs(Vector3.Dot(lightDirToLight, Vector3.up)) > 0.99f
                    ? Vector3.forward : Vector3.up;
                Matrix4x4 view = Matrix4x4.LookAt(eye, center, up).inverse;
                // Unity のビュー空間は -Z 前方のため z を反転。
                view.SetRow(2, -view.GetRow(2));

                float extent = radius;
                float near = 0.01f;
                float far = 2f * radius + 2f * depthPadding;
                Matrix4x4 proj = Matrix4x4.Ortho(-extent, extent, -extent, extent, near, far);
                proj = GL.GetGPUProjectionMatrix(proj, true); // RT 描画のため renderIntoTexture=true

                viewProj = proj * view;
                return true;
            }

            private void EnableCharShadow()
            {
                if (!Shader.IsKeywordEnabled(k_CharShadowKeyword))
                    Shader.EnableKeyword(k_CharShadowKeyword);
            }

            private void DisableCharShadow()
            {
                if (Shader.IsKeywordEnabled(k_CharShadowKeyword))
                    Shader.DisableKeyword(k_CharShadowKeyword);
                // 有効フラグも 0 に（キーワード残留時の保険）。
                Shader.SetGlobalVector(s_ParamsId, new Vector4(0f, 0f, 0f, 0f));
            }
        }
    }
}

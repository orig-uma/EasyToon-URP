// =============================================================================
//  IdolCharacter.cs
// -----------------------------------------------------------------------------
//  Idol キャラクター 1 体ぶんの一括制御コンポーネント（キャラ専用セルフシャドウ登録＋仮想ライト方式）。
//
//  役割:
//   (1) 配下 Renderer の自動収集（+手動リスト）と、合成 Bounds の提供。
//       IdolCharShadowFeature が static レジストリ ActiveCharacters から全キャラを
//       集めて 1 枚のキャラ影マップを組む。
//   (2) 仮想ライト方向オーバーライド（_VirtualLightDir へ書き込み）。
//   (3) 演出一括制御（BlackOut / BackRim / HairSeeThroughAlpha）を配下 Idol
//       マテリアルへ一括反映。
//
//  設計（DollLiveDirector 踏襲）:
//   - Play 中はマテリアルインスタンス経由で書く（SRP Batcher 維持・MPB 不使用）。
//   - Edit 中は MaterialPropertyBlock による非破壊プレビュー（資産を汚さない）。
//   - Timeline / Animation は public フィールドを直キーで駆動可能。
//   - キャラ影 Feature 未使用でも単体で動作する（仮想ライト・演出は Feature 非依存）。
// =============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace Origuma.EasyToon.URP
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("EasyToon/Idol Character")]
    public class IdolCharacter : MonoBehaviour
    {
        private const string ShaderPrefix = "Origuma/EasyToon_URP/";

        [Header("Renderers")]
        [Tooltip("配下 Renderer を自動収集する（無効時は manualRenderers のみ使用）")]
        public bool autoCollectRenderers = true;
        [Tooltip("手動で追加する Renderer（autoCollect と併用可）")]
        public List<Renderer> manualRenderers = new List<Renderer>();

        [Header("Virtual Light Override")]
        [Tooltip("ON でメインライト方向をキャラ専用の仮想方向へブレンドする")]
        public bool overrideLightDirection = false;
        [Range(-90f, 90f)] public float lightPitch = 30f;
        [Range(-180f, 180f)] public float lightYaw = 0f;
        [Tooltip("メインライト→仮想方向のブレンド（0=素通し, 1=完全に仮想方向）")]
        [Range(0f, 1f)] public float lightBlend = 1f;

        [Header("Live Control")]
        [Tooltip("ON のあいだ BlackOut を上書き（OFF でマテリアルの元値へ復元）")]
        public bool overrideBlackOut = false;
        [Range(0f, 1f)] public float blackOut = 0f;

        [Tooltip("ON のあいだ Back Rim を上書き（ライブのシルエット確保用）")]
        public bool overrideBackRim = false;
        public bool backRimEnable = true;
        [ColorUsage(true, true)] public Color backRimColor = Color.white;

        [Tooltip("ON のあいだ 前髪透過アルファを上書き")]
        public bool overrideHairSeeThrough = false;
        [Range(0f, 1f)] public float hairSeeThroughAlpha = 0.6f;

        // --- static レジストリ（Feature が参照） ---------------------------------
        private static readonly HashSet<IdolCharacter> s_Active = new HashSet<IdolCharacter>();
        public static IReadOnlyCollection<IdolCharacter> ActiveCharacters => s_Active;

        // --- Property IDs -------------------------------------------------------
        private static readonly int VirtualLightDirId    = Shader.PropertyToID("_VirtualLightDir");
        private static readonly int BlackOutId           = Shader.PropertyToID("_BlackOut");
        private static readonly int BackRimEnableId      = Shader.PropertyToID("_BackRimEnable");
        private static readonly int BackRimColorId       = Shader.PropertyToID("_BackRimColor");
        private static readonly int HairSeeThroughAlphaId = Shader.PropertyToID("_HairSeeThroughAlpha");

        // Play 中の Idol マテリアルインスタンスと、Override 解除時の復元用元値。
        private struct TargetMat
        {
            public Material mat;
            public Vector4 origVirtualLightDir;
            public float origBlackOut;
            public float origBackRimEnable;
            public Color origBackRimColor;
            public float origHairSeeThroughAlpha;
        }

        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly List<TargetMat> _targets = new List<TargetMat>();
        private MaterialPropertyBlock _mpb;   // Edit プレビュー専用
        private bool _instancesReady;

        private void OnEnable()
        {
            s_Active.Add(this);
            CollectRenderers();
            _instancesReady = false;
        }

        private void OnDisable()
        {
            s_Active.Remove(this);

            if (Application.isPlaying)
            {
                RestoreAll();
            }
            else
            {
                foreach (var r in _renderers)
                    if (r != null) r.SetPropertyBlock(null); // プレビューを非破壊解除
            }
            _targets.Clear();
            _instancesReady = false;
        }

        private void LateUpdate()
        {
            if (_renderers.Count == 0) CollectRenderers();
            if (_renderers.Count == 0) return;

            if (Application.isPlaying)
            {
                if (!_instancesReady) CollectInstances();
                ApplyToInstances();
            }
            else
            {
                ApplyEditPreview();
            }
        }

        // ------------------------------------------------------------------
        //  Renderer 収集
        // ------------------------------------------------------------------
        private void CollectRenderers()
        {
            _renderers.Clear();
            if (autoCollectRenderers)
            {
                GetComponentsInChildren(true, _renderers);
            }
            if (manualRenderers != null)
            {
                foreach (var r in manualRenderers)
                    if (r != null && !_renderers.Contains(r)) _renderers.Add(r);
            }
        }

        // ------------------------------------------------------------------
        //  合成 Bounds（キャラ影 Feature が使用）。ワールド空間・enable renderer のみ。
        //  有効な Renderer が無ければ false。
        // ------------------------------------------------------------------
        public bool TryGetWorldBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in _renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        // 仮想ライト方向（ワールド）。overrideLightDirection が OFF なら blend=0。
        // xyz=正規化方向, w=ブレンド。
        public Vector4 GetVirtualLightDir()
        {
            if (!overrideLightDirection || lightBlend <= 0f)
                return new Vector4(0f, 0f, 1f, 0f);

            float pitch = lightPitch * Mathf.Deg2Rad;
            float yaw = lightYaw * Mathf.Deg2Rad;
            // ライト「進行方向」ではなく、シェーダーが使う「面→光源」方向を返す。
            //  URP の mainLight.direction は光源へ向かうベクトル。それに合わせる。
            Vector3 dir = new Vector3(
                Mathf.Cos(pitch) * Mathf.Sin(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Cos(yaw));
            dir.Normalize();
            return new Vector4(dir.x, dir.y, dir.z, Mathf.Clamp01(lightBlend));
        }

        // ------------------------------------------------------------------
        //  Play: マテリアルインスタンス経由（SRP Batcher 維持）
        // ------------------------------------------------------------------
        private void CollectInstances()
        {
            _targets.Clear();
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                bool hasIdol = false;
                foreach (var m in r.sharedMaterials)
                    if (IsIdol(m)) { hasIdol = true; break; }
                if (!hasIdol) continue;

                var mats = r.materials; // スロット全体をインスタンス化（初回のみ）
                foreach (var m in mats)
                {
                    if (!IsIdol(m)) continue;
                    _targets.Add(new TargetMat
                    {
                        mat = m,
                        origVirtualLightDir     = m.HasProperty(VirtualLightDirId) ? m.GetVector(VirtualLightDirId) : new Vector4(0, 0, 1, 0),
                        origBlackOut            = m.HasProperty(BlackOutId) ? m.GetFloat(BlackOutId) : 0f,
                        origBackRimEnable       = m.HasProperty(BackRimEnableId) ? m.GetFloat(BackRimEnableId) : 0f,
                        origBackRimColor        = m.HasProperty(BackRimColorId) ? m.GetColor(BackRimColorId) : Color.white,
                        origHairSeeThroughAlpha = m.HasProperty(HairSeeThroughAlphaId) ? m.GetFloat(HairSeeThroughAlphaId) : 0.6f,
                    });
                }
            }
            _instancesReady = true;
        }

        private void ApplyToInstances()
        {
            Vector4 vld = GetVirtualLightDir();
            foreach (var t in _targets)
            {
                if (t.mat == null) continue;

                // 仮想ライトは常に反映（OFF 時は blend=0 の素通し値）。
                t.mat.SetVector(VirtualLightDirId, vld);

                t.mat.SetFloat(BlackOutId, overrideBlackOut ? blackOut : t.origBlackOut);

                if (overrideBackRim)
                {
                    t.mat.SetFloat(BackRimEnableId, backRimEnable ? 1f : 0f);
                    t.mat.SetColor(BackRimColorId, backRimColor);
                }
                else
                {
                    t.mat.SetFloat(BackRimEnableId, t.origBackRimEnable);
                    t.mat.SetColor(BackRimColorId, t.origBackRimColor);
                }

                t.mat.SetFloat(HairSeeThroughAlphaId,
                    overrideHairSeeThrough ? hairSeeThroughAlpha : t.origHairSeeThroughAlpha);
            }
        }

        private void RestoreAll()
        {
            foreach (var t in _targets)
            {
                if (t.mat == null) continue;
                t.mat.SetVector(VirtualLightDirId, t.origVirtualLightDir);
                t.mat.SetFloat(BlackOutId, t.origBlackOut);
                t.mat.SetFloat(BackRimEnableId, t.origBackRimEnable);
                t.mat.SetColor(BackRimColorId, t.origBackRimColor);
                t.mat.SetFloat(HairSeeThroughAlphaId, t.origHairSeeThroughAlpha);
            }
        }

        // ------------------------------------------------------------------
        //  Edit: MaterialPropertyBlock プレビュー（非破壊）
        // ------------------------------------------------------------------
        private void ApplyEditPreview()
        {
            Vector4 vld = GetVirtualLightDir();
            bool any = overrideLightDirection || overrideBlackOut || overrideBackRim
                       || overrideHairSeeThrough;
            _mpb ??= new MaterialPropertyBlock();

            foreach (var r in _renderers)
            {
                if (r == null) continue;
                if (!any) { r.SetPropertyBlock(null); continue; }

                bool hasIdol = false;
                foreach (var m in r.sharedMaterials)
                    if (IsIdol(m)) { hasIdol = true; break; }
                if (!hasIdol) continue;

                _mpb.Clear();
                if (overrideLightDirection) _mpb.SetVector(VirtualLightDirId, vld);
                if (overrideBlackOut) _mpb.SetFloat(BlackOutId, blackOut);
                if (overrideBackRim)
                {
                    _mpb.SetFloat(BackRimEnableId, backRimEnable ? 1f : 0f);
                    _mpb.SetColor(BackRimColorId, backRimColor);
                }
                if (overrideHairSeeThrough) _mpb.SetFloat(HairSeeThroughAlphaId, hairSeeThroughAlpha);
                r.SetPropertyBlock(_mpb);
            }
        }

        private static bool IsIdol(Material m)
            => m != null && m.shader != null && m.shader.name.StartsWith(ShaderPrefix, System.StringComparison.Ordinal);

        // ------------------------------------------------------------------
        //  スクリプト API（Timeline の Animation Track はフィールド直キーで可）
        // ------------------------------------------------------------------
        public void SetBlackOut(float value) { overrideBlackOut = true; blackOut = Mathf.Clamp01(value); }
        public void SetVirtualLight(float pitch, float yaw, float blend)
        {
            overrideLightDirection = true;
            lightPitch = pitch; lightYaw = yaw; lightBlend = Mathf.Clamp01(blend);
        }
        public void ClearOverrides()
        {
            overrideLightDirection = overrideBlackOut = overrideBackRim = overrideHairSeeThrough = false;
        }
    }
}

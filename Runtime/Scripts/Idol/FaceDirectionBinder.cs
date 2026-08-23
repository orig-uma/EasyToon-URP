using System.Collections.Generic;
using UnityEngine;

namespace ToonNPR
{
    /// <summary>
    /// 頭ボーンの向きをシェーダーの _HeadForward / _HeadRight に転送する。
    ///
    /// Surface Type = Face は法線を捨てて SDF で影境界を決めるので、
    /// 「顔がどちらを向いているか」を外から与えないと成立しない。
    /// 未設定だとシェーダー側が normalize(0) を踏んで顔だけ壊れる。
    ///
    /// 書き込み経路は Doll（DollLiveDirector）と同じ二層:
    ///
    ///   Play — Renderer.materials で初回だけインスタンス化し、Face の
    ///          インスタンスへ直接書く。MPB を付けた Renderer は SRP Batcher
    ///          から外れる（顔の Renderer は目・眉・睫毛・口も抱えて実測
    ///          8 マテリアル＝巻き添えが大きい）ため、Play 中は使わない。
    ///          インスタンスへの書き込みなら共有マテリアル資産も汚れない。
    ///   Edit — 非破壊の MaterialPropertyBlock プレビュー。共有マテリアルに
    ///          触れないので .mat に差分が出ない（プレビューのバッチングは
    ///          問題にならない）。
    ///
    /// OnDisable で Play はインスタンスへ元値を復元、Edit は自分が当てた
    /// Renderer だけ MPB を外す。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Toon NPR/Face Direction Binder")]
    public class FaceDirectionBinder : MonoBehaviour
    {
        /// <summary>
        /// 頭ボーンのローカル軸は素体の規格や DCC の出力設定で揃っていない。
        /// どの軸を顔の正面として使うかをここで選ばせる。
        /// </summary>
        public enum BoneAxis
        {
            Right, Up, Forward, Left, Down, Back
        }

        [Tooltip("空なら Animator（Humanoid）の Head から自動で拾う")]
        [SerializeField] private Transform _headBone;

        [Tooltip("空なら子階層から Surface Type = Face のマテリアルを持つ Renderer を自動収集する")]
        [SerializeField] private Renderer[] _targets;

        [SerializeField] private BoneAxis _forwardAxis = BoneAxis.Forward;
        [SerializeField] private BoneAxis _rightAxis   = BoneAxis.Right;

        private static readonly int HeadForwardId = Shader.PropertyToID("_HeadForward");
        private static readonly int HeadRightId   = Shader.PropertyToID("_HeadRight");
        private static readonly int SurfaceTypeId = Shader.PropertyToID("_SurfaceType");

        private const float FaceSurfaceType = 2f;   // KeywordEnum の Face の位置

        // ---- Play: マテリアルインスタンス ----
        private struct FaceMat
        {
            public Material mat;
            public Vector4  origForward;
            public Vector4  origRight;
        }

        private readonly List<FaceMat> _instances = new List<FaceMat>();
        private bool _collected;

        // ---- Edit: MPB プレビュー ----
        private MaterialPropertyBlock _block;

        // 自分がプレビューを当てた Renderer。解除時にここへ入っているものだけ
        // SetPropertyBlock(null) する。無差別に null を撒くと、同じ Renderer に
        // 他コンポーネントが当てたプレビューまで消してしまう。
        private readonly HashSet<Renderer> _previewed = new HashSet<Renderer>();

        private readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>
        /// 頭ボーンの解決規則の唯一の出所（外部コンポーネント向けの公開 API）。
        /// 旧・前髪影の HairShadowCaster（T-344 で廃止）が参照していた経緯で
        /// public にしてある。解決規則を複数箇所に持つと片方だけずれるため維持。
        /// </summary>
        public Transform HeadBone
        {
            get
            {
                if (_headBone == null) Resolve();
                return _headBone;
            }
        }

        private void OnEnable()
        {
            _warned.Clear();
            Resolve();
            Apply();
        }

        private void OnDisable()
        {
            if (Application.isPlaying) RestoreInstances();
            else ClearPreview();
        }

        /// <summary>
        /// アニメーションが頭を動かした後でなければ意味が無いので LateUpdate で読む。
        /// </summary>
        private void LateUpdate()
        {
            Apply();
        }

        /// <summary>インスペクタや外部ツールから明示的に更新したいとき用。</summary>
        public void Refresh()
        {
            Resolve();
            Apply();
        }

        private void Resolve()
        {
            if (_headBone == null)
            {
                var animator = GetComponentInParent<Animator>();
                if (animator != null && animator.isHuman)
                    _headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            if (_targets == null || _targets.Length == 0)
                _targets = CollectFaceRenderers();
        }

        /// <summary>
        /// 顔以外の Renderer にまで書き込む（Edit では MPB を付ける）と
        /// その分だけ無駄が増えるので、対象を顔だけに絞る。
        /// </summary>
        private Renderer[] CollectFaceRenderers()
        {
            var found = new List<Renderer>();

            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !m.HasFloat(SurfaceTypeId)) continue;
                    if (!Mathf.Approximately(m.GetFloat(SurfaceTypeId), FaceSurfaceType)) continue;

                    found.Add(r);
                    break;
                }
            }

            return found.ToArray();
        }

        private void Apply()
        {
            if (_headBone == null)
            {
                // 毎フレーム再試行する。ランタイム生成のキャラは OnEnable の時点で
                // Animator の Avatar が未確定なことがあり、そこで諦めると
                // _HeadForward が 0 のまま固定＝顔が NaN になる。
                Resolve();

                if (_headBone == null)
                {
                    WarnOnce("head-bone",
                             "頭ボーンが見つからない。Surface Type = Face のマテリアルは正しく描画されない。");
                    return;
                }
            }

            if (_targets == null || _targets.Length == 0)
            {
                WarnOnce("targets",
                         "対象 Renderer が無い。Surface Type = Face のマテリアルを持つメッシュを割り当てること。");
                return;
            }

            Vector3 forward = AxisToVector(_headBone, _forwardAxis);
            Vector3 right   = AxisToVector(_headBone, _rightAxis);

            // シェーダー側は XZ 平面に潰してから normalize する
            // （頭を傾けても影の左右が反転しないように）。
            // その XZ 成分がほぼ 0 だと normalize が壊れて顔だけ NaN になる。
            // 原因はほぼ軸の選び間違いなので、絵が壊れる前にここで名指しする。
            if (new Vector2(forward.x, forward.z).sqrMagnitude < 1e-4f ||
                new Vector2(right.x, right.z).sqrMagnitude < 1e-4f)
            {
                WarnOnce("axis",
                         "Forward / Right Axis が真上か真下を向いている。" +
                         "シェーダーは XZ 平面で正規化するため顔が破綻する。軸の設定を見直すこと。");
            }

            var forward4 = new Vector4(forward.x, forward.y, forward.z, 0f);
            var right4   = new Vector4(right.x,   right.y,   right.z,   0f);

            if (Application.isPlaying) ApplyToInstances(forward4, right4);
            else                       ApplyEditPreview(forward4, right4);
        }

        // ------------------------------------------------------------------
        //  Play: マテリアルインスタンス経由（SRP Batcher 維持・資産非汚染）
        // ------------------------------------------------------------------

        private void ApplyToInstances(Vector4 forward4, Vector4 right4)
        {
            if (!_collected) CollectInstances();

            // 頭ボーンは毎フレーム動くので前回値スキップは持たない
            //（一致比較がほぼヒットせず、状態変数の複雑さだけが残る）。
            foreach (var t in _instances)
            {
                if (t.mat == null) continue;
                t.mat.SetVector(HeadForwardId, forward4);
                t.mat.SetVector(HeadRightId,   right4);
            }
        }

        private void CollectInstances()
        {
            _collected = true;
            _instances.Clear();

            foreach (var r in _targets)
            {
                if (r == null) continue;

                // .materials アクセスでスロット全体がインスタンス化される（初回のみ）。
                // 別マテリアル同士でも同一バリアントなら SRP Batcher でまとまるので、
                // 顔以外のスロットが巻き添えでインスタンス化されても害は無い
                // （DollLiveDirector と同じ理屈）。
                foreach (var m in r.materials)
                {
                    if (m == null || !m.HasVector(HeadForwardId) || !m.HasFloat(SurfaceTypeId))
                        continue;

                    // 書くのは Face のインスタンスだけ。_HeadForward は CBUFFER の
                    // 都合で Idol の全マテリアルが持っているが、Face 以外は読まない
                    // ── 書いても毎フレームその CBUFFER を送り直させるだけになる。
                    if (!Mathf.Approximately(m.GetFloat(SurfaceTypeId), FaceSurfaceType))
                        continue;

                    _instances.Add(new FaceMat
                    {
                        mat         = m,
                        origForward = m.GetVector(HeadForwardId),
                        origRight   = m.GetVector(HeadRightId),
                    });
                }
            }
        }

        private void RestoreInstances()
        {
            foreach (var t in _instances)
            {
                if (t.mat == null) continue;

                // 元値の XZ 成分が退化していない（＝実値が焼いてある）ときだけ復元する。
                // 既定値は (0,0,0,0)（Binder が供給する前提の未設定値）で、これを
                // 無条件に戻すとシェーダーが normalize(0) を踏み、Play 中に disable
                // した瞬間に顔が NaN で壊れる。未設定のマテリアルは最後の書き込み値を
                // 残す（Play 終了時はインスタンスごと破棄されるので実害は無い）。
                if (new Vector2(t.origForward.x, t.origForward.z).sqrMagnitude >= 1e-4f)
                    t.mat.SetVector(HeadForwardId, t.origForward);
                if (new Vector2(t.origRight.x, t.origRight.z).sqrMagnitude >= 1e-4f)
                    t.mat.SetVector(HeadRightId, t.origRight);
            }

            _instances.Clear();
            _collected = false;   // 再有効化で収集し直す（Renderer 構成が変わっていても追従）
        }

        // ------------------------------------------------------------------
        //  Edit: 非破壊 MPB プレビュー（共有マテリアルを汚さない）
        // ------------------------------------------------------------------

        private void ApplyEditPreview(Vector4 forward4, Vector4 right4)
        {
            _block ??= new MaterialPropertyBlock();

            foreach (var r in _targets)
            {
                if (r == null) continue;

                // 他のスクリプトが入れた値を消さないよう、既存のブロックを読んでから上書きする。
                r.GetPropertyBlock(_block);
                _block.SetVector(HeadForwardId, forward4);
                _block.SetVector(HeadRightId,   right4);
                r.SetPropertyBlock(_block);
                _previewed.Add(r);
            }
        }

        private void ClearPreview()
        {
            foreach (var r in _previewed)
                if (r != null) r.SetPropertyBlock(null);
            _previewed.Clear();
        }

        private static Vector3 AxisToVector(Transform t, BoneAxis axis)
        {
            switch (axis)
            {
                case BoneAxis.Right:   return t.right;
                case BoneAxis.Up:      return t.up;
                case BoneAxis.Forward: return t.forward;
                case BoneAxis.Left:    return -t.right;
                case BoneAxis.Down:    return -t.up;
                default:               return -t.forward;
            }
        }

        /// <summary>
        /// 種別ごとに1回だけ警告する。単一フラグにすると、たとえば軸の警告が
        /// 1フレーム出ただけで「頭ボーンが無い」が二度と出なくなる。
        /// </summary>
        private void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            Debug.LogWarning($"[FaceDirectionBinder] {message}", this);
        }
    }
}

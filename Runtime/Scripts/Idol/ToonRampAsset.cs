// =============================================================================
//  ToonRampAsset.cs — ランプの元データ（T-396）
// -----------------------------------------------------------------------------
//  Gradient（編集の元）と、そこから焼いた Texture2D（マテリアルが参照する物）を
//  1 つのアセットに抱える。テクスチャはサブアセットとして同じファイルに埋め込む。
//
//  なぜ Runtime に置くか: マテリアルはサブアセットの Texture2D を参照するので、
//  主アセットの型がプレイヤーに存在しないと参照解決が壊れうる。
//  ロジック（焼く・書き出す・取り込む）は全部 Editor 側（ToonPBRRampGenerator）。
//  このクラスはデータの入れ物で、ランタイムでは何もしない。
//
//  Gradient は ScriptableObject のフィールドなので Undo / Redo が普通に効き、
//  Editor は変更のたびに埋め込みテクスチャの画素を書き換えて即時反映する。
//  PNG は「交換フォーマット」で、書き出し／取り込みは Editor の機能。
// =============================================================================
using UnityEngine;

namespace ToonNPR
{
    public class ToonRampAsset : ScriptableObject
    {
        [Tooltip("左 = 影 / 右 = 明。ランプはアルベドに乗算される（白 = 素通し）")]
        public Gradient gradient = new Gradient();

        [Tooltip("gradient から焼いた 256×1 のテクスチャ。マテリアルはこれを参照する")]
        public Texture2D texture;
    }
}

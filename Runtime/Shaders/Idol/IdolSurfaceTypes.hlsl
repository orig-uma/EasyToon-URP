// =============================================================================
//  IdolSurfaceTypes.hlsl
//  IdolSurfaceData 型定義のみ（URP の SurfaceData と名前衝突しないよう分離）。
// =============================================================================
#pragma once

#ifndef IDOL_SURFACE_TYPES_INCLUDED
#define IDOL_SURFACE_TYPES_INCLUDED

struct IdolSurfaceData
{
    // 陰色はライト非依存に 1 回だけ算出して保持（per-light 再計算を排除）。
    half3 albedo;           // ライト面の最終アルベド
    half3 shadow1Albedo;    // 1影の最終色（Hue Shift / Sat Boost + Shadow1 Color 適用済み）
    half3 shadow2Albedo;    // 2影の最終色
    half3 castShadowAlbedo; // 落ち影の最終色

    half3 cleanNormalWS;    // ジオメトリ法線（落ち影バイアス等の基準）
    half3 detailNormalWS;   // ディテール法線（スペキュラ・リムに使用）
    half3 shadeNormalWS;    // 拡散陰専用の平滑化法線（未ベイク時は detailNormalWS と同一）

    half  halfLambertOffset; // _OcclusionMap 由来の HalfLambert オフセット（0=ニュートラル）
    half  specMask;
    half  specAAVariance;
    half  NdotV;

    half3 indirectLight;    // SH 整形済み間接光

    // --- リム / 天使の輪（ライト非依存の前計算） ---
    half  rimFresnel;          // フレネルリム項（GetFresnelTerms で 1 回算出）
    AnisoPrecomp anisoPrecomp; // 天使の輪の接線前計算（BRDF_Anisotropic.hlsl の型）

    // --- ストッキング/シアー生地 ---
    half3 stockingSheen;       // すそ光沢（HDR・ライト非依存。ApplyPostEffects で加算）
};

#endif // IDOL_SURFACE_TYPES_INCLUDED

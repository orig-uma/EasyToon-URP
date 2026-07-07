# EasyPBR(Doll) → EasyToon(Idol) 移行ガイド

## 命名規約

**意味・単位・レンジが一致するプロパティは Doll と同名にする**(後発の Idol が Doll に寄せる)。
Unity はシェーダー差し替え時にプロパティ値を「名前」で保持するため、同名なら Doll → Idol の
シェーダー差し替えだけで色・テクスチャ・数値が引き継がれる。Timeline / Animation の
バインディングも維持される。**意味が異なるものは意図的に別名にする**(黙って間違った値が
入るのを防ぐ。例: Doll `_ShadingStyle`=Toon/Smooth と Idol `_ShadingMode`=2Band/Ramp)。

## 意図的に別名のプロパティ(意味が異なる)

| Doll | Idol | 理由 |
| :--- | :--- | :--- |
| `_ShadingStyle`(Toon/Smooth) | `_ShadingMode`(2Band/Ramp) | 選択肢の意味が違う |
| `_SurfaceTransparent` + Alpha Clip | `_RenderMode`(Opaque/Cutout) | サーフェス制御の構造が違う |
| `_DiffuseLightLimit` 等 | `_AdditionalBlowoutLimit` | Doll は全ライト輝度上限、Idol は追加ライト限定 |
| (なし) | 深度リム / Angel Ring / BackRim / CharaPart / キャラ影 / `_OcclusionToShadow` / `_ShadeRampMap` | Idol 固有機能 |

## マテリアル移行手順

1. **同名プロパティ**: Doll マテリアルのシェーダーを `Origuma/EasyToon_URP/Idol` に差し替えるだけで値が引き継がれる
2. **変換ツール**(`Window > Origuma > Doll to Idol Converter`): シェーダー差し替えに加え、
   マテリアル名から Chara Part を推定してプリセット(Stencil/Queue/HairSeeThrough)を適用し、
   Doll の Alpha Clip / キーワード状態を Idol の Render Mode へ変換する

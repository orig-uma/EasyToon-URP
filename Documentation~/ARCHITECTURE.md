# EasyToon for URP — アーキテクチャ設計

要件は [REQUIREMENTS.md](REQUIREMENTS.md) を参照。本書は実装のための設計を定める。

## 命名

- パッケージ: `com.origuma.easytoon-urp`
- シェーダー: `Origuma/EasyToon_URP/Idol`（3D ライブの主役＝アイドルを想定したキャラクター本命シェーダー。Doll の Toon 版対抗）
- asmdef: `Origuma.EasyToon.URP.Runtime` / `Origuma.EasyToon.URP.Editor`
- LightMode: アウトライン `IdolOutline` / キャラシャドウキャスター `IdolCharShadow`
- プロパティ接頭辞なし（EasyPBR と同じ命名慣習）。**ベイクマップのプロパティ名は EasyPBR と同名**（Baker 再利用のため必須）:
  `_OcclusionMap` `_OcclusionStrength` `_CavityMap` `_CurvatureMap` `_ShadeNormalMap` `_SSSMap` `_HairFlowMap` `_FaceSDFMap`
- **Doll と意味・単位・レンジが一致するプロパティは同名にする**（シェーダー差し替えで値が引き継がれる）。対応表は [MIGRATION.md](MIGRATION.md)。意味が異なるものは意図的に別名（例: `_ShadingStyle` ≠ `_ShadingMode`）

## パッケージ間依存

```
com.origuma.easyshader-core (共通基盤)
    ↑                      ↑
com.origuma.easypbr-urp   com.origuma.easytoon-urp（本パッケージ）
(>= 0.6.0)                 ・HLSL: Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/** を絶対パス include
                           ・依存宣言は package.json に置かず、PM 追加直後（および起動時）に
                             Installer が自動導入（UPM は git 依存を解決できないため。→ Editor/Installer/）
                           ・本体 Editor asmdef は versionDefines + defineConstraints（EASYSHADERCORE_PRESENT）で
                             Core 不在時にコンパイル対象から除外。コンパイルエラーでドメインリロードが
                             止まらず、PM 追加直後に Installer が走れる（＝再起動不要でゼロクリック導入）
                           ・C# Editor: asmdef 参照 Origuma.EasyShaderCore.Editor で
                             Baker 群 (EasyPbr*Baker, public) / ShaderGuiKit を再利用
```

- **EasyToon は EasyPBR に依存しない**（依存は `com.origuma.easyshader-core` のみ）。
  EasyPBR は Doll→Idol 変換（DollToIdolConverterWindow）の変換**対象**としてのみ関係し、
  コード依存はない（シェーダー名文字列で参照。EasyPBR 不在でも動作する）
- **EasyShaderCore への変更ルール**: Common HLSL は「純粋関数のみ・特定シェーダー非依存」を維持する。
  Idol 固有ポリシー（陰ランプ・ステンシル運用等）を core に入れるのは禁止。
  Baker の呼び出し面（`Bake(root, material, Settings)`）は互換維持
- EasyPBR の `Doll/` 配下（ポリシー層）の include は引き続き禁止 — Toon 固有ポリシーは Idol 側に書く

## ディレクトリ構成

```text
com.origuma.easytoon-urp/
  package.json
  README.md / CHANGELOG.md / LICENSE.md
  Documentation~/
    REQUIREMENTS.md  ARCHITECTURE.md  MIGRATION.md  VERIFICATION.md
  Runtime/
    Scripts/
      Origuma.EasyToon.URP.Runtime.asmdef
      IdolOutlineFeature.cs        LightMode "IdolOutline" を後段一括描画 (Render Graph)
      IdolCharShadowFeature.cs     キャラ専用シャドウマップ (Render Graph)
      IdolCharacter.cs             キャラ登録・仮想ライト方向・演出一括制御
    Shaders/
      Idol/
        Idol.shader                Properties / Pass 定義 / Stencil
        IdolInput.hlsl             CBUFFER・テクスチャ宣言（単一 CBUFFER 厳守）
        IdolSurfaceTypes.hlsl      IdolSurfaceData 型定義のみ
        IdolSurface.hlsl           サーフェス収集（GatherSurface / Varyings 定義後 include）
        IdolLighting.hlsl          Toon ライティング統合（陰ランプ・セルスペキュラ・SDF 合成）
        IdolRim.hlsl               深度リム / フレネルリム / バックライトリム / 髪スクリーン影
        IdolHair.hlsl              天使の輪 / ヘアフロー
        IdolShadows.hlsl           キャラシャドウ + URP 影フォールバックのラッパー
        IdolDissolve.hlsl          Dissolve のサンプリング/プロパティ解決（Fx_Dissolve 委譲）
        IdolFabric.hlsl            ストッキング/シアー生地
        Passes/
          ForwardPass.hlsl / ShadowPass.hlsl / DepthOnlyPass.hlsl /
          DepthNormalsPass.hlsl / OutlinePass.hlsl / CharShadowPass.hlsl
  Editor/
    Origuma.EasyToon.URP.Editor.asmdef   (references: EasyToon.Runtime, EasyShaderCore.Editor, URP /
                                          defineConstraints: EASYSHADERCORE_PRESENT — Core 不在時は除外)
    IdolShaderGUI.cs               カスタムインスペクター。ShaderGuiKit(EasyShaderCore) を再利用し、
                                    Render Mode / Chara Part プリセット / キーワード同期を担う
                                    （状態変更ロジックは同ファイル内 IdolMaterialSetup に分離）
    IdolBakingPanel.cs             Baker 呼び出し UI（EasyShaderCore の public Baker へ委譲。
                                    AO / Shade Normal / Hair Flow / Face SDF の 4 種）
    Installer/
      Origuma.EasyToon.URP.Installer.asmdef   参照ゼロの独立 asmdef（Core 不在でも必ずコンパイル）
      EasyShaderCoreInstaller.cs    Core 不在を検知し Client.Add で自動インストール（ゼロクリック）。
                                    失敗時のみ手動手順つきの案内ウィンドウを表示（参照ゼロの独立 asmdef）
    IdolSetupWindow.cs             RendererFeature 2 種のセットアップ Window
                                    （EasyShaderCore の FeatureSetupWindowBase ベース。
                                    エントリ宣言のみで描画/ロジックは Core 委譲）
                                    （Window > Origuma > Idol Setup。アクティブ URP Asset の
                                    Renderer Data を自動収集し追加/削除・Compat Mode 警告）
```

## シェーディングモデル設計

### 陰の決定パイプライン

```
NdotL(ShadeNormal 優先) ── HalfLambert ─┐
_OcclusionMap → しきい値オフセット ──────┤
                                        ├─ ramp = ToonRamp2Band or RampTexture
CastShadow(キャラシャドウ or URP 影) ────┤     (uniform 分岐 _ShadingMode)
Face SDF(顔マテリアルのみ) ─────────────┘
albedo / shadow1Albedo / shadow2Albedo (色相シフト・彩度適用済み, ライト非依存に 1 回算出)
→ 最終色 = lerp 3 段。落ち影は専用色 _CastShadowColor で別塗り分け
```

- EasyPBR `BRDF_Diffuse.hlsl` の `ToonRamp` / `ShadeRamp` / `ShadedAlbedo` / `ResolveCastShadow` を再利用し、しきい値オフセットと Ramp テクスチャ対応は Idol 側ポリシー層で実装
- 追加ライト: Forward+ ループ。トゥーン化（同ランプ適用）と Anti-Blowout(Max/Add) を通す
- スペキュラ: `BlinnPhongLobe` → `smoothstep(threshold±softness)` でセル化。`ApplySpecularAA` 適用

### キーワード方針（EasyPBR 哲学の踏襲）

| 静的 keyword（⚡バリアント） | 種別 | 理由 |
| :--- | :--- | :--- |
| `_ALPHATEST_ON` | shader_feature | 早期 Z 喪失防止 |
| `_IDOL_CHARSHADOW` | multi_compile_fragment（グローバル） | キャラ専用影サンプルの全経路コンパイルによる occupancy 低下防止。IdolCharShadowFeature が有効/無効を切替 |
| `_DISSOLVE_ON` | shader_feature | 常時ノイズサンプル化防止 |

静的キーワードは上表の 3 つのみ。Dissolve の軸（None/WorldY/LocalY）はキーワードにせず uniform `_DissolveType` の動的分岐にしてバリアント数を抑える。

それ以外（Shading Mode / Ramp / MatCap / Emission / 深度リム / SDF / 各ベイクマップ有無 / 仮想ライト方向）はすべて uniform 動的分岐。未ベイク時は Intensity 0 でサンプルスキップ。

### ステンシルレイアウト（前髪透過）

Material Type（enum プロパティ `_CharaPart`: Body / Face / Brow(眉・まつ毛) / Hair / Eye）で Pass の Stencil ブロックを切り替える:

| 部位 | Stencil 動作 |
| :--- | :--- |
| Brow / Eye | 描画時に Ref=2 (Brow) / 4 (Eye) を書き込み |
| Hair | Ref & ReadMask で Brow/Eye 済みピクセルを検出し、`_HairSeeThroughAlpha` でブレンド描画（2nd pass or 同 pass 内 Comp）|
| Face / Body | 影響なし（Ref=1 書き込みのみ・将来の picking 用）|

描画順は Render Queue 微調整（Face < Brow/Eye < Hair）で保証。実装は
「Hair を 2 パス（不透明 Comp Equal ＋ 透過の重ね描き）」方式とする。

### スクリーンスペース深度リム

1. `_CameraDepthTexture` を自ピクセルとライト方向（スクリーン空間投影）へ `_RimWidthPx / 画面解像度` オフセットした位置でサンプル
2. 線形深度差 > `_RimDepthThreshold`（キャラ厚を考慮）でリム判定
3. `rimColor(HDR) × ライト色 × 受光マスク` を加算。フレネルリムと個別強度で併用可

### キャラ専用シャドウ

- `IdolCharShadowFeature`（Render Graph）: `IdolCharacter.ActiveCharacters` の合成 Bounds を、メインライト方向から包む正射影 VP で専用深度テクスチャ（Shadowmap フォーマット・D16/D32 選択・解像度 1024/2048/4096 選択）へ `IdolCharShadow` パスで描画。v1 は全キャラで 1 枚（アトラスなし）
- グローバル供給（per-material CBUFFER には入れない＝SRP Batcher 維持）:
  - `_IdolCharShadowMap`（`SetGlobalTextureAfterPass`）
  - `_IdolCharShadowMatrix`（ライト VP。受影・キャスターで共有）
  - `_IdolCharShadowParams`（x=1/解像度, y=強度, z=有効フラグ, w=0）
  - `_IdolCharShadowBias`（x=深度, y=法線。キャスター側）
- キーワード: Feature 有効かつ登録キャラ>0 かつメインライト（Directional）在で `_IDOL_CHARSHADOW`（グローバル）を Enable、それ以外で Disable。メインライト不在フレームは何もしない
- 受影サンプル: `IdolShadows.hlsl` が `_IDOL_CHARSHADOW` 定義時に positionWS を `_IdolCharShadowMatrix` で射影し、3x3 PCF（`SAMPLER_CMP`）でサンプルして URP 標準影と `min()` 合成（髪→顔の落ち影がこれで出る）。範囲外は 1（影なし）
- 受影側追加バイアス `_CharShadowFaceBias`（マテリアル CBUFFER、Face/Eye のアクネ追い込み用・既定 0）
- 未使用時（Feature 未追加 or 無効）: `_IDOL_CHARSHADOW` 未定義で URP メインライト影のみのフォールバック

### 仮想ライト方向・演出一括制御

- `IdolCharacter`（ExecuteAlways）: 配下 Renderer を自動収集（+手動リスト）し、`ActiveCharacters` static レジストリへ OnEnable/OnDisable で登録/解除。合成 Bounds を Feature へ提供
- 仮想ライト方向オーバーライド: bool + Pitch/Yaw/Blend を `_VirtualLightDir`（float4: xyz=正規化方向, w=ブレンド）へ書く。ForwardPass で `mainLight.direction = normalize(lerp(mainLight.direction, _VirtualLightDir.xyz, w))` に差し替え（陰・スペキュラ・リム・SDF すべてに効く）。既定 w=0 で素通し
- 演出一括制御（BlackOut / BackRim / HairSeeThroughAlpha）を配下 Idol マテリアルへ一括反映。`DollLiveDirector` 方式踏襲: Play=マテリアルインスタンス（SRP Batcher 維持・MPB 不使用）、Edit=MPB 非破壊プレビュー。Timeline から public フィールド直キー可
- キャラ影 Feature 未使用でも単体で動作（仮想ライト・演出は Feature 非依存）

### Dissolve（EasyShaderCore の Fx_Dissolve を流用）

- `_DISSOLVE_ON`（shader_feature）を全パス（ForwardLit / HairSeeThrough / ShadowCaster / DepthOnly / DepthNormals / Outline / CharShadow）に追加
- `IdolDissolve.hlsl` が `_DissolveTex` をサンプルし、EasyPBR `Fx_Dissolve.hlsl` の `ResolveDissolve` へ委譲（`Doll/` は include せず Common のみ）。軸 None/WorldY/LocalY は uniform `_DissolveType` の動的分岐でバリアント増加を回避。クリップは各パス、エッジ発光は ForwardLit で Emission に加算
- プロパティ（Idol 側 CBUFFER）: `_DissolveTex` `_DissolveAmount` `_DissolveEdgeColor` `_DissolveEdgeColor2` `_DissolveEdgeWidth` `_DissolveEdgeStep` `_DissolveType`（+ `_DissolveInvert` `_DissolveStartY` `_DissolveEndY` `_DissolveNoiseScale` `_DissolveNoiseStrength`）

### 表現拡張 R9 — 2 機能とも既定 OFF・新規キーワードなし（uniform 分岐）


**髪→顔のスクリーンスペース落ち影**（`IdolRim.hlsl` の `CalculateHairScreenShadow`）:

```
occluderDiff = selfEyeDepth - offsetEyeDepth      // ライト方向へ _HairShadowOffsetPx 先の深度
hairShadow   = window(occluderDiff, [_HairShadowDepthMin, _HairShadowDepthMax])  // 薄い近接遮蔽のみ
castShadow   = min(castShadow, 1 - hairShadow × _HairShadowIntensity)
```

- 「ライト方向のスクリーン投影向き」は深度リムと共通の `GetLightScreenDir` に集約
  （スクリーン Y 反転等の座標系修正を 1 箇所にするため）
- 合成順: URP 影・キャラ影 → 髪影 → SDF（`_FaceSDFShadowMix` が後段で castShadow に掛かる）。
  Face/Brow/Eye マテリアルで有効化する運用

**ストッキング/シアー生地**（`IdolFabric.hlsl` の `ApplyStocking`）:

```
graze   = pow(1 - NdotV, _StockingPower)                          // シルエットほど 1
opacity = lerp(_StockingFrontOpacity, 1, graze) × mask × intensity
fabric  = lerp(albedo × _StockingColor, _StockingColor, graze)    // 正面=透け / 縁=布密
albedo  = lerp(albedo, fabric, opacity)
sheen   = _StockingSheenColor × pow(graze, _StockingSheenPower) × mask × intensity  // 加算
```

- GatherSurface のアルベド確定直後（陰色算出より前）に適用 → 陰色にも布色が自動で乗る。
  すそ光沢（sheen）はライト非依存の加算（ApplyPostEffects・HDR）。適用範囲は `_StockingMask`(R)

## Pass / LightMode

| Pass | LightMode | 備考 |
| :--- | :--- | :--- |
| ForwardLit | UniversalForward | メイン。Hair は Stencil Comp Equal で眉・目の上を描かない |
| HairSeeThrough | SRPDefaultUnlit | 前髪透過。Brow/Eye のステンシル bit 上にのみ半透明で重ね描き。中身は ForwardPass.hlsl を `IDOL_HAIR_SEETHROUGH` define で流用 |
| ShadowCaster | ShadowCaster | URP 標準落ち影用 |
| DepthOnly / DepthNormals | 同名 | Forward+ / SSAO / 深度リム前提のため必須。ForwardLit と同一のプロパティ駆動 Stencil を敷き、Depth Priming 構成でも前髪透過ビットを一致させる |
| Outline | IdolOutline | `IdolOutlineFeature` が後段一括描画 |
| CharShadowCaster | IdolCharShadow | `IdolCharShadowFeature` が専用深度マップへ描画。頂点変換はグローバル `_IdolCharShadowMatrix`、バイアスは `_IdolCharShadowBias` |

前髪透過のステンシル運用は IdolShaderGUI の **Chara Part プリセット**が唯一の入口
（`IdolMaterialSetup.ApplyCharaPart` が Stencil / Render Queue / HairSeeThrough パス有効化を一括適用）:

| 部位 | Stencil 設定 | Render Queue | HairSeeThrough パス |
| :--- | :--- | :--- | :--- |
| Body / Face | 既定値（Comp=Always, Keep, Mask 255）| 2000 | 無効 |
| Brow | Ref=2, Comp=Always, Pass=Replace, WriteMask=6 | 2002 | 無効 |
| Eye  | Ref=4, Comp=Always, Pass=Replace, WriteMask=6 | 2002 | 無効 |
| Hair | Ref=0, Comp=Equal, ReadMask=6 | 2010 | **有効**（`SetShaderPassEnabled("SRPDefaultUnlit", true)`。穴を半透明で埋める）|

描画順 Brow/Eye → Hair は部位 Queue で保証する。Render Mode（Opaque/Cutout）は
キーワード・RenderType のみを切り替え、Queue には触れない — Cutout を AlphaTest 帯
（2450+）へ動かすと Brow(Cutout) が Hair(Opaque) より後になり前髪透過が壊れるため、
部位 Queue を常に優先する。

## 設計ルール

1. CBUFFER 単一・全プロパティ包含（SRP Batcher 互換）— `UnityPerMaterial` 外の per-material uniform 禁止
2. キーワードは上表のみ。`shader_feature` の安易な追加は禁止
3. EasyShaderCore の Common HLSL は「純粋関数のみ・特定シェーダー非依存」を維持（Idol 固有ポリシーの持ち込み禁止）
4. include は URP Core → EasyShaderCore Common → Idol 型 → Idol ポリシーの順。EasyPBR `Doll/` の include 禁止
5. 未ベイク・既定値で全機能が安全にスキップされること必須（テクスチャ既定値 white/bump の明示）
6. RendererFeature は Render Graph API（非 Compatibility）で書くこと必須

# Changelog

## [Unreleased]

### Fixed
- **Package Manager からの追加直後にも EasyShaderCore の自動インストールが走るように修正**: 本体 Editor asmdef（`Origuma.EasyToon.URP.Editor`）を versionDefines + defineConstraints（シンボル `EASYSHADERCORE_PRESENT`）で Core 不在時にコンパイル対象から除外した。従来は Core 不在時のコンパイルエラーでドメインリロードが完了せず、PM 追加直後に `InitializeOnLoad`（Installer）が走らないため、エディタを再起動するまで Core が自動導入されなかった。除外により PM 追加直後（同一エディタセッション内・再起動不要）に Installer が走り、ゼロクリックで Core が導入される。

## [0.1.0] - 2026-07-08

初期実装。

### Added

- **Toon シェーディングコア**: 2段影（色相シフト/彩度制御付き陰色）/ Ramp テクスチャモード / ベイク AO による影しきい値オフセット / Shade Normal / 落ち影の分離塗り分け / セルスペキュラ（Specular AA 付き）
- **顔**: Face SDF 顔影（4ch・左右非対称対応）/ 前髪透過（ステンシル bit1=Brow, bit2=Eye + HairSeeThrough パス）/ 髪→顔のスクリーンスペース落ち影（深度差の窓判定）
- **髪・質感**: 天使の輪（ヘアフローマップ駆動・カメラ追従率制御）/ ストッキング・シアー生地（視角依存の布レイヤ＋すそ光沢）
- **リムライト**: スクリーンスペース深度リム（ピクセル幅一定）/ フレネルリム / バックライトリム
- **キャラ専用セルフシャドウ**: `IdolCharShadowFeature`（Render Graph・専用深度マップ・3x3 PCF・髪→顔の落ち影）
- **アウトライン**: 背面法線拡張（頂点カラー制御・距離/FOV 正規化・スクリーン幅クランプ・Albedo ブレンド）+ `IdolOutlineFeature`
- **演出**: `IdolCharacter`（仮想ライト方向オーバーライド / BlackOut / BackRim / 前髪透過の一括制御・Timeline 対応）/ Dissolve / Light Conditioning / Anti-Blowout / SH 整形
- **Editor**: タブ式カスタムインスペクター（Chara Part プリセット・⚡バリアント可視化）/ ベイク統合（EasyShaderCore の Baker を利用: AO / Shade Normal / Hair Flow / Face SDF）/ `Idol Setup` ウィンドウ（RendererFeature ワンクリック追加）/ `Doll to Idol Converter`（マテリアル変換）
- **Doll(EasyPBR) 互換のプロパティ命名**: 意味が一致するプロパティは Doll と同名（シェーダー差し替えで値が引き継がれる → [MIGRATION](Documentation~/MIGRATION.md)）
- SRP Batcher 互換（単一 CBUFFER・静的キーワードは `_ALPHATEST_ON` / `_DISSOLVE_ON` / `_IDOL_CHARSHADOW` のみ）

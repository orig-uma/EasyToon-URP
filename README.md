# EasyToon for URP

シェーダー名: `Origuma/EasyToon_URP/Idol`

Toon を本命とする URP 向け高品質キャラクターシェーダーです。BaseMap 一枚＋既定値で成立し、
質感はマップベイクで積み増します。[EasyPBR](https://github.com/orig-uma/EasyPBR-URP) の姉妹パッケージで、
共通基盤 [EasyShaderCore](https://github.com/orig-uma/EasyShaderCore-URP)（`com.origuma.easyshader-core`）の
Common ライブラリとマップベイク資産を共有します。

## 特徴

* **Toon シェーディングコア:** 2段影（色相シフト・彩度制御付きの陰色）と Ramp テクスチャモードを切替可能。ベイク AO による影しきい値の局所オフセット（常影・影になりやすさ）、平滑化法線（Shade Normal）による綺麗な陰の輪郭、落ち影の専用色塗り分けに対応しています。
* **顔:** ベイク 4ch SDF によるライトに滑らかに追従する顔影（左右非対称対応）、ステンシルベースの前髪透過（眉・目が前髪越しに透ける）、髪→顔のスクリーンスペース落ち影を搭載しています。
* **キャラ専用セルフシャドウ:** RendererFeature がキャラだけを専用深度マップへ描画。シーン影の解像度に左右されないクリーンな自己影と、髪→顔の自然な落ち影を実現します。
* **リムライト:** 画面上の幅がピクセル一定のスクリーンスペース深度リム、フレネルリム、演出用バックライトリムの3系統。
* **髪・質感:** ヘアフローマップ駆動の天使の輪（アンジェラリング）、セルスペキュラ、金属 MatCap、ストッキング/シアー生地（視角依存の布レイヤ）。
* **アウトライン:** 背面法線拡張。頂点カラーによる部位制御、カメラ距離・FOV 正規化、スクリーン幅上限クランプ、アルベドブレンドの線色。
* **ライブ運用:** キャラ単位一括制御（`IdolCharacter`: 仮想ライト方向 / Black Out / バックリム / 前髪透過）、Light Conditioning、白飛び防止、Dissolve。Timeline から直接キー打ち可能です。
* **DCC 不要のマップベイク:** AO / Shade Normal / Hair Flow / Face SDF を Editor 上で焼いて自動アサイン（EasyShaderCore の Baker を共有）。
* **SRP Batcher を意識した設計:** 全プロパティ単一 CBUFFER。静的キーワードは 3 つのみ（`_ALPHATEST_ON` / `_DISSOLVE_ON` / `_IDOL_CHARSHADOW`）で、それ以外は uniform 動的分岐。多人数同時描画でもバッチが分断されにくい構成です。

## 動作環境

* Unity 6 (6000.3) 以降
* Universal RP 17.3 以降 / Forward+
* Render Graph 有効（既定）。Compatibility Mode では RendererFeature（アウトライン・キャラ影）が動作しません
* **[EasyShaderCore for URP](https://github.com/orig-uma/EasyShaderCore-URP) 0.2.0 以降（必須依存）**
* [EasyPBR for URP](https://github.com/orig-uma/EasyPBR-URP) は**任意**（Doll からの移行変換にのみ必要。コード依存なし）

## インストール

> ⚠ **先に EasyShaderCore をインストールしてください。** UPM は git URL の依存を自動解決できないため、
> EasyShaderCore が無い状態で本パッケージを入れると依存エラーになります。
> EasyToon を先に入れてしまった場合は、エディタ起動時に EasyShaderCore のインストール案内ウィンドウが表示され、ワンクリックで導入できます。

`Window > Package Manager > + > Add package from git URL...` に**順番に**入力する（core → toon）:

```
https://github.com/orig-uma/EasyShaderCore-URP.git
https://github.com/orig-uma/EasyToon-URP.git
```

Embedded package として使う場合は `Packages/com.origuma.easytoon-urp` に配置する。

## セットアップ

1. **RendererFeature の追加** — メニュー `Window > Origuma > Idol Setup` の Setup Window で、アクティブな Renderer Data へワンクリック追加（マテリアル Inspector の Outline / Cast Shadow セクションからも開ける）
   - `Idol Outline Feature` — アウトライン描画（LightMode `IdolOutline` の後段一括描画）
   - `Idol Char Shadow Feature` — キャラ専用セルフシャドウ（髪→顔の落ち影。解像度・深度ビット・バイアスは Feature 側で設定）
2. **IdolCharacter コンポーネント**をキャラのルートに追加
   - キャラ影 Feature へのキャラ登録（配下 Renderer の自動収集）
   - 仮想ライト方向オーバーライド / BlackOut / BackRim / 前髪透過アルファの一括制御（Timeline 直キー可）
3. **Chara Part プリセット**（マテリアル Inspector の「顔・髪」タブ）
   - Body / Face / Brow / Hair / Eye を選ぶと、前髪透過に必要な Stencil・Render Queue・HairSeeThrough パス有効化を一括適用
4. **ベイク**（マテリアル Inspector の「Baking」タブ）
   - Source Root（キャラの GameObject）を指定し、AO / Shade Normal / Hair Flow / Face SDF を各マテリアルで焼く
   - 生成 PNG は自動アサインされ、対応する Strength / Enable が自動有効化される

EasyPBR(Doll) のマテリアルからは `Window > Origuma > Doll to Idol Converter` でワンクリック変換できます
（同名プロパティは自動引き継ぎ。詳細 → [MIGRATION](Documentation~/MIGRATION.md)）。

## ドキュメント

| ドキュメント | 内容 |
| :--- | :--- |
| [REQUIREMENTS](Documentation~/REQUIREMENTS.md) | 要件定義（採用技術と機能要件） |
| [ARCHITECTURE](Documentation~/ARCHITECTURE.md) | 内部構成・設計方針（Pass / キーワード / キャラ影） |
| [MIGRATION](Documentation~/MIGRATION.md) | EasyPBR(Doll) からの移行ガイド |
| [VERIFICATION](Documentation~/VERIFICATION.md) | 動作検証チェックリスト |

## ライセンス

[MIT License](LICENSE.md)

## 作者

Origuma — [https://github.com/orig-uma](https://github.com/orig-uma)

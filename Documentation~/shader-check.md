---
description: シェーダーの静的検査を実行し、指摘があれば原因まで特定する
---

`Assets/ToonNPR/Shaders` の静的検査を実行してください。

```bash
python3 tools/shader_lint.py Assets/ToonNPR/Shaders --strict
```

結果の扱い方:

**エラー 0・警告 0 の場合**
静的検査は通ったが実コンパイルは未検証であることを明記して報告を終える。「動きます」とは書かない。

**指摘があった場合**
コードごとに対応が違うので、それぞれ原因を特定してから直すこと。機械的に黙らせる修正はしない。

- **E001 / E002** — `Properties` ブロック、`CBUFFER_START(UnityPerMaterial)`、実際に読む箇所の3箇所が揃っているか確認する。スクリプトから設定する値なら CBUFFER の宣言行末に `// lint:script-set` を付ける
- **E003** — `TEXTURE2D(_X)` と `SAMPLER(sampler_X)` の両方が必要
- **E004** — 依存ヘッダを使用行より前へ移す。パス側ではなく、そのシンボルを使っているファイル自身に include を置く
- **E005** — CBUFFER は全パス共通で1箇所だけ。分割すると SRP Batcher が壊れる
- **E006** — `TRANSFORM_TEX` を使うテクスチャには `_XXX_ST` が要る
- **E007** — 深度を読むなら DepthOnly パスを消してはいけない
- **W101** — キーワードを宣言する `#pragma shader_feature_local` が抜けている。宣言し忘れか、その分岐がもう不要かのどちらか
- **W102** — キーワードを ON にする手段が無い。`[Toggle(...)]` を足すか、カスタム ShaderGUI で設定する

**誤検出だと判断した場合**
黙って無視せず、なぜ誤検出なのかを説明したうえで `tools/shader_lint.py` 側を直す。検査の精度が落ちると仕組み全体が形骸化する。

Unity が使える環境なら、静的検査を通した後に実コンパイルも回してください。

```bash
Unity -batchmode -quit -nographics -projectPath . \
      -executeMethod ToonNPR.EditorTools.ShaderCompileCheck.RunCI -logFile -
```

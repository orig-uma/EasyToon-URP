# EasyToon for URP — 動作検証チェックリスト

セットアップ後の動作検証チェックリスト。以下を順に確認し、問題があれば末尾の
「修正の窓口」から着手する。

## 0. コンパイル

- [ ] Console にシェーダー/C# のコンパイルエラーがないこと
- [ ] `GetFresnelTerms` の out 引数（half フィールドを out float へ）で警告が出る場合は `IdolSurfaceData.rimFresnel` を float 化

## 1. 基本描画

- [ ] BaseMap 一枚＋既定値で 2 段影が出る（1影しきい値 0.5）。陰色に彩度が残る（Hue Shift +0.02 / Sat Boost 1.15）
- [ ] Ramp モード（Shading Mode = Ramp）で Ramp テクスチャの色設計が**落ち影にも**効く
- [ ] 追加ライト(Forward+)を複数置いても間接光が二重加算されない・白飛びしない（Anti-Blowout）
- [ ] Frame Debugger で同シェーダーのマテリアルが SRP Batcher でまとまる

## 2. アウトライン

- [ ] `Idol Setup` ウィンドウから IdolOutlineFeature を追加して線が出る
- [ ] **mm 指定と実表示幅のスケール**: `_OutlineWidth`=2mm がカメラ距離を変えても画面上ほぼ一定か。太さの体感が合わない場合は `OutlinePass.hlsl` の `worldPerScreenHeight` 係数を調整
- [ ] 近接で `_OutlineMaxScreenPx` クランプが効く（太りすぎない）
- [ ] 頂点カラー未設定メッシュ（頂点カラー=白）で線が出る

## 3. 深度リム

- [ ] URP Asset の **Depth Texture を ON**。不透明描画中に `_CameraDepthTexture` が有効になる構成（Depth Priming ON または深度プリパスが走る設定）を推奨。リムが真っ黒/前フレーム残像になる場合は CopyDepth のタイミング問題
- [ ] **リムの Y 方向**: ライトを上に置いたとき上縁が光るか。上下が逆なら `IdolRim.hlsl` の `dirSS.y` に `-1`（または `_ProjectionParams.x` 連動）を乗算
- [ ] リム幅がカメラ距離・FOV に依らずピクセル一定
- [ ] エッジ品質: `smoothstep(threshold, threshold*2)` の固定比ソフトネスで縁が汚い場合はソフトネスを独立プロパティ化

## 4. Face SDF / 前髪透過

- [ ] Face SDF ベイク → `_FaceSDFEnable` 自動 ON → ライトを左右に回すと顔影が滑らかに追従。**顔の正面軸**（オブジェクト空間 +Z、`_FaceSDFFlip` で反転）がモデルと合っているか
- [ ] Chara Part プリセット適用後（Brow=2002 / Eye=2002 / Hair=2010 / Hair のみ HairSeeThrough 有効）、前髪越しに眉・目が `_HairSeeThroughAlpha` で透ける
- [ ] **Depth Priming ON の構成でも**透けが機能する（DepthOnly/DepthNormals にも同一の Stencil を敷いてある。壊れる場合はプリパスの描画順を Frame Debugger で確認）
- [ ] 非髪マテリアルで HairSeeThrough パスが無効化されている（Chara Part プリセット未適用のマテリアルに注意）

## 5. 天使の輪

- [ ] Hair Flow ベイク → 天使の輪がミラー UV でも連続する
- [ ] `_AngelRingViewFollow`=1 で頭を回しても輪が視覚的定位置に留まる
- [ ] バンドの太さ表現が足りない場合は `IdolHair.hlsl` の `kRingThickness`(0.4 固定) をプロパティ化

## 6. キャラ専用シャドウ（最重要）

- [ ] IdolCharShadowFeature 追加 + IdolCharacter 登録で影が出る
- [ ] **行列規約**: 影が上下反転/半画面ずれする場合は `IdolShadows.hlsl` の UV 変換（現在は追加 Y 反転なし）を `shadowUV.y = 1 - shadowUV.y` に変更して比較
- [ ] **深度比較の向き**: 全面影/全面光になる場合は reversed-Z と SAMPLER_CMP の比較方向が原因。`depthRef` の符号・バイアス（caster: `-=`, receiver faceBias: `+=`）を反転して確認
- [ ] 髪→顔の落ち影が出る。顔のアクネは `_CharShadowFaceBias` で追い込める
- [ ] キャラが画面外に出ると影が消える（v1 制限・cullResults 依存）。運用上問題があれば独自カリングの導入を検討
- [ ] マテリアルプレビュー/Reflection Probe 描画で影が明滅しない（cameraType でガードしてある）
- [ ] 複数キャラで 1 枚 2048 の解像度が足りるか（不足なら 4096 またはアトラス化を検討）

## 7. 仮想ライト / 演出

- [ ] IdolCharacter の Override Light Direction ON、Pitch=30/Yaw=0 で「正面上から」の陰になる（符号が逆なら `GetVirtualLightDir` の軸定義を修正）
- [ ] Play 中に BlackOut / BackRim / HairSeeThroughAlpha の一括制御が効き、SRP Batcher が維持される（Frame Debugger）
- [ ] Edit 中は MPB プレビューで、解除時にマテリアル資産が汚れていない

## 8. Dissolve

- [ ] エッジ 2 色の発光が出る。エッジの陰色寄せ（`edgeLerp` ヒューリスティック）が `_DissolveEdgeColor2` とアルベドが近い色のときに破綻しないか
- [ ] Outline / 影(ShadowCaster / キャラ影) が Dissolve と同期して消える

## 9. ベイク統合

- [ ] AO ベイク後: `_OcclusionToShadow`=0.5、`_OcclusionStrength` がベイク前の値のまま（Baker の自動有効化を Panel 側で打ち消す設計）
- [ ] 4 種のベイクが `Baked/` に出力され自動アサインされる

## 10. 表現拡張

髪→顔スクリーン影（Face/Brow/Eye マテリアルで有効化）:

- [ ] `_HairShadowIntensity` を上げると前髪の細い落ち影が顔に出る（キャラ影より精細・画面解像度そのまま）
- [ ] **影の Y 方向**: ライト位置に影の向きが追従する。方向が逆の場合、深度リム（3.）も同時に逆のはず → 修正は `IdolRim.hlsl` の `GetLightScreenDir` 1 箇所（両機能で共有。片方だけ直る場合は共有が壊れている）
- [ ] Depth Min/Max の窓: 体・壁など遠い遮蔽で影が出ない／自己面（頬の連続面）で影が出ない
- [ ] SDF 顔影（`_FaceSDFShadowMix`）・落ち影色（`_CastShadowColor`）と自然に合成される（合成順: URP影・キャラ影 → 髪影 → SDF）

ストッキング（肌マテリアルで有効化）:

- [ ] `_StockingIntensity` を上げると、正面は肌が透け・シルエット際は布色が密になる（視角応答）。カメラ/脚を回すと透け具合が追従する
- [ ] 陰側にも布色が乗る（陰色算出より前に合成）
- [ ] `_StockingMask` の黒部分に布が乗らない
- [ ] Sheen Color（HDR）でシルエット際に光沢が出て Bloom を誘発できる

## 修正の窓口

| 症状の系統 | 見るファイル |
| :--- | :--- |
| 陰・ランプ・落ち影 | `IdolLighting.hlsl` |
| キャラ影のずれ・反転 | `IdolShadows.hlsl`(受影) / `IdolCharShadowFeature.cs`(行列) / `CharShadowPass.hlsl`(キャスター) |
| リムの方向・幅 | `IdolRim.hlsl` |
| 髪スクリーン影の方向・窓 | `IdolRim.hlsl`（`GetLightScreenDir` / `CalculateHairScreenShadow`） |
| ストッキングの視角応答 | `IdolFabric.hlsl` |
| アウトラインの太さ | `Passes/OutlinePass.hlsl` |
| 前髪透過 | `Idol.shader`(Stencil/Queue) / `IdolShaderGUI.cs`(プリセット) |

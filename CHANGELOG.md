# Changelog

## [Unreleased]

## [0.2.1] - 2026-08-25

### Changed

- **既定値の変更（利用者判断・T-384）**: `Specular Intensity` 0.2 → **0**、`Terminator - Strength` 0.35 → **0**。新規マテリアルはハイライトとターミネータ暖色が無効の素のトゥーンから始まり、使う人だけが立てる。**既存マテリアルは保存値を持つため変わらない。** 2nd Lobe（既定 0）・環境反射・グリッタは従来どおり。

### Added

- **金属部だけ鏡面の倍率を上書きする `Metal Specular Boost` / `Metal Env Boost` を追加**（スペキュラタブ、T-383）。`Specular Intensity` / `Env Specular Intensity` はマテリアル全体に掛かるため、肌向けに絞ると金具まで死に、金具向けに上げると肌がテカる ── 金属マスク（Mask Map R × Metallic）で倍率を lerp して分離した。**ローブは増やさない**（マスクはほぼ二値なので、パラメータの lerp で結果の合成と同じ絵になる。追加コストはフラグメント 1 回の lerp 2 個）。Smoothness の上書きノブは意図的に無い ── metallic を持てている時点で Mask Map があるので、ツヤの描き分けは A チャンネルの仕事。クリアコート層（誘電体）には掛からない。既定 1 = 既存の絵は不変。

### Fixed

- **本体 Editor asmdef の `versionDefines` 式 `[0.3.0,)` が Unity に無効と判定され（`ExpressionNotValidException`）、Editor アセンブリごとコンパイルされず Inspector のカスタム UI が出なかった問題を修正。** Unity の式は開区間を受け付けず、素の `0.3.0` が「0.3.0 以上」を意味する。0.2.0 で入れた Core 最低バージョン連動の意図はそのまま。

## [0.2.0] - 2026-08-23

### Fixed

- **旧 EasyShaderCore が入ったまま本パッケージを更新すると、Core が更新されず本体 Editor がコンパイルエラーになる問題を修正。** Installer は「Core が存在するか」しか見ておらず、0.2.0 が要求する Core 0.3.0 の新 API が無い 0.2.0 のままでも無音だった。Installer に必要最低バージョン（0.3.0）の比較を入れ、古ければピン留め URL（`#v0.3.1`）へ差し替える。本体 Editor asmdef の `versionDefines` も `0.3.0`（= 0.3.0 以上）に揃え、古い Core では本体を除外してコンパイルエラーを出さず Installer が走れるようにした。

### Changed (Breaking)

- **顔 SDF を 16bit 1ch（R×256+G）の一方式に統一**（T-382）。4ch 方式（R/G/B/A = 右/左/上/下）と 8bit 1ch 方式を撤去し、`_FaceSDF4Ch` / `_FaceSDF16Bit` のトグルを廃止。16bit 1ch ＋ 距離場ブレンド ＋ Cast Shadow のベイク（Core の `FACE_SDF_BAKING.md`）が品質で上回り、方式を 3 つ抱える理由が無くなった。非ミラー側のフェッチはフラグメントで 1 回に畳み（ライトごとの再フェッチを削減）、AA はデコード後の値の変化率に改めた（上位バイトだけの fwidth は 256 段の飛びを拾って過大だった）。Baking タブは常に 16bit で焼く。**移行**: 既存の 4ch / 8bit テクスチャは読めない（G が下位バイトとして解釈される）ので Baking タブで焼き直すこと。Doll からの移行で `_FaceSDFMap` は運ばない。EasyPBR Doll は 4ch のまま（Core のベイカーも両形式を保持）。
- **曲率の画面空間推定を撤去し、焼いた Curvature Map を唯一の供給源にした**（T-381）。法線の画面微分から作る曲率は三角形の中で一定・辺で不連続なので、Curvature Influence を上げると低ポリで陰にポリゴンの面が並んだ（段差の量は Base Softness × Influence に比例）。ベイカーが DCC 不要で焼けるようになった今、推定経路は「焼かずに上げると面が出る」罠でしかなく、マップ 1 枚（R）のメモリはこのシェーダーの対象環境では問題にならない。撤去: `ToonCurvature` / `_CurvatureReferenceRadius` / `_CurvatureMapStrength`（マップが唯一の供給源なので Influence 1 本で足りる）。Curvature Influence が 0 より大きい材質だけマップを読む。**移行**: Curvature Map Blend を使っていた材質は Influence がそのまま効く（Blend 0 で眠っていた罠も消える）。Doll からの移行は `_CurvatureStrength` > 0 を Influence 1 に畳む。
- **画面空間輪郭（ScreenSpaceOutlineFeature）を廃止**（T-380）。実プロジェクトの Renderer に未導入のまま、MSAA と両立しない制約と材質 ID の運用（DepthNormals の A チャンネル・0.1 刻み）だけが残っていた。輪郭は押し出し方式の `ToonOutlineFeature` で足りる。撤去: `ScreenSpaceOutlineFeature.cs` / `IdolScreenOutline.shader` / `_MaterialId` プロパティ / GUI の Material ID 節と Feature ガード / Debug View の MaterialId / Presets の「材質 ID をタイプで振る」/ SetupCheck の材質 ID 診断 / Idol Setup の項目。DepthNormals の A は URP 本体と同じ 0 に戻した。FR-21 は廃止扱い。既存マテリアルの `_MaterialId` は Unity が無視するだけで壊れない。
- **`_CastShadowStrength` を `_CastShadowColorStrength` に改名**（T-374）。「落ち影をどれだけ受けるか」と誤読されていたが、実体は **Cast Shadow Color をどれだけ掛けるかという色の話**で、影が落ちるかどうかは Receive Realtime Shadow が決める。GUI の見出しも「落ち影の色」に改め、肌が続くマテリアル間で揃えないと境目で影の色に段差が出る旨を Note で明示（実際に顔と首のつなぎ目で起きた）。**互換性は切った** ── 旧名の保存値は既定 0 に戻る（落ち影の色付けを使っていた材質は値を入れ直すこと）。
- **陰の持ち上げ（Procedural Shadow Lift）を廃止し、フィルライトへ置き換え**（T-370）。旧機能（`_FrontLiftStrength` / `_UpLiftStrength` / `_LiftFalloff`）は「顔の自己陰をマスク無しで消す」ための仕組みだったが、その用途は顔 SDF が受け持つようになり、実プロジェクトでの使用も 0/46 件だった。FR-31 は廃止扱い。既存マテリアルの残存値は Unity が無視するだけで壊れない。
- **Cel シェーダーを廃止し、Idol に一本化**（T-356）。用途（3D ライブ・ステージ）が Idol で満たされたための整理。撤去: `Runtime/Shaders/Cel/**`（7 パス一式）・`CelShaderGUI` / `CelBakingPanel` / `CelSetupWindow` / `DollToCelConverterWindow`・`CelCharacter` / `CelCharShadowFeature` / `CelOutlineFeature`・検証スイートの Cel 段（check.py / param_check `--generic` / self_test の複製）・文書の Cel 章（SRP_BATCHER / VARIANTS / README）。**`ToonPBRMigrator` は残る** ── 旧 Cel / Doll 材質から Idol への移行表は現役。Cel を使っていたマテリアルはシェーダー欠落（ピンク）になるため、Migrator で Idol へ移行するか破棄すること。
- **頬の赤み（Blush）を廃止**（T-349）。プロジェクト内 46 マテリアルの `_BlushStrength` が**すべて 0**（＝ 誰も使っていない）である一方、Skin / Face バリアントでは `ToonBlushShape` の帯計算と lerp が**強度 0 でも毎ライト毎ピクセル走っていた**。撤去したのはプロパティ 4 個（`_BlushColor` / `_BlushStrength` / `_BlushCenter` / `_BlushWidth`）・`ToonBlushShape`・GUI セクション。既存マテリアルは残存プロパティを Unity が無視するだけで壊れず、**絵も 1 画素も変わらない**。FR-30 は廃止扱い。**代替**: 頬の色は肌テクスチャに描くか、Skin - Subsurface（皮下散乱）で出す。
- **HairShadow（前髪の専用影）と ContactShadow（画面空間の接地影）を廃止し、影を HQ セルフシャドウへ一本化**（T-344）。前髪の影は投影が**頭上→真下の固定でライト方向と無関係**（動くライトのステージでは原理的に嘘が出る）、コンタクト影は**画面空間デプスマーチの原理的弱点**（画面外の遮蔽物が影を落とさない・ディザが TAA 無しで這う）が理由。撤去: HairShadow パス（8→7 パス）/ `HairShadowCaster` / `HairShadowFeature` / `_CONTACT_SHADOW_ON` キーワード（**ForwardLit の feature 組 40 → 20 = バリアント半減**）/ 関連プロパティ 9 個。**移行**: 前髪→顔の影は HQ Self Shadow（`Window > Origuma > URP Shadow Setup` の「キャラ重視」プリセットでテクセル密度をキャラへ集中）で出す。`_ContactShadowOn` が ON だったマテリアルは効果が消えるだけで壊れない（残った stale キーワードは Unity が無視する）。SetupCheck / param_check / self_test / 文書も同時更新。

- **HairSeeThrough パスを `SRPDefaultUnlit` から独自 LightMode `IdolHairSeeThrough` + `HairSeeThroughFeature` へ移行**（T-341）。旧構成は URP が UniversalForward と同じ描画パスで処理するため [本体][透過] が交互に並び、**SetPass が跳ねて ForwardLit が SRP Batcher でまとまらなかった**（利用者の実測で判明。診断と処方は T-040 の輪郭と同一）。新構成では ForwardLit は素でバッチされ、透過は不透明の後に 1 箇所でまとめて描かれる（「眉・目 → 髪透過」の描画順も構造的に保証される）。**移行**: (1) `Window > Origuma > Idol Setup` で `HairSeeThroughFeature` を Renderer に追加しないと前髪透過が出ない (2) 旧タグで止めたパス停止の記録は不活性化するため `Tools > Idol > 使っていない重ね描きパスを止める` を**再実行**すること。GUI（前髪透過プリセット横の未導入ガード）・SetupCheck・param_check に検知を追加済み。Feature は ToonOutlineFeature と同型だが**フルシェーディングのため per-object データを素通しする**（None にすると穴の縁で本体と明るさが割れる）。

### Changed

- **GUI の設定手順を Doll 基準で是正**（T-359）。Doll の全セクションを洗い出して突き合わせた結果への対処。**陳腐化した案内 2 件を修正**: Surface Type の案内が「質感タブにセクションが増えます」のままで、実際は顔=陰・影タブ / 髪=スペキュラタブ / 布=スペキュラ+質感タブへ移っていた（英語版は存在しない "Material tab" を案内していた ── **最初に読む案内**なので実害が大きい）。`Exclude from Shadow Map` の tooltip が **T-344 で廃止した髪影パス**を「引き続き焼く」と案内していた（今 ON にすると影が単に消える）。**手順の穴を 2 つ塞いだ**: Surface Type = Default のとき「部位を選ぶまで肌・顔・髪・布の機能は 1 つも出ない」と明示（実プロジェクトで 34 件が Default のまま放置されていた）／画面空間輪郭の Feature 未導入ガードを追加（3 Feature 中 2 つにしかガードが無く、Material ID を振っても無警告で何も起きなかった）。**棚割り**: 焼いたマップのうち部位を選ばない 3 種（ベントノーマル / キャビティ / 曲率）を**質感タブの「ベイクしたマップ」1 セクションへ集約**（Doll と同じ棚。従来は 3 タブに散っていた）。セクションの既定の開閉も Doll に合わせた（Doll に同等セクションがあるものだけ開く）。
- **詳細タブを独立に戻した**（T-354。Doll も同時に 8 タブ化＝タブ同一の原則を維持）。T-352 で Doll に倣いレンダーステート等を Baking タブ末尾へ同居させたが、「Baking タブにレンダーステートがいる」のは分類として無理があった。8 タブ: `基本 / 陰・影 / ライト / スペキュラ / 質感 / 演出 / 詳細 / Baking`（4 列 × 2 段）。
- **アウトラインを演出タブ → 基本タブへ移動**（T-353。Doll も同時に移動＝タブ同一の原則を維持）。輪郭線はマテリアルごとの恒久設定＝キャラの基本の見た目であって、時間で変化する演出（ディゾルブ）とは性質が違うため。演出タブにはディゾルブだけが残る。
- **タブ構成を Doll と同一の 7 タブへ**（Idol GUI、T-352）。`基本 / 陰・影 / ライト / スペキュラ / 質感 / 演出 / Baking` ── 数・名前・順序・棚割りとも Doll と同じにし、2 つのシェーダーを行き来しても迷わないようにした。移動: **リム → 質感タブ**（Doll の「肌と縁の質感」と同じ扱い）、**クリアコート・虹色・グリッタ → 質感タブ「コートとグリッター」**（Doll の同名節と同じ組み合わせ）、**旧・詳細タブ（レンダーステート / ステンシル / デバッグ）→ Baking タブ末尾**（Doll も Baking タブが高度な設定を抱える）。質感タブは「肌の質感（部位ゲート）→ 縁 → コートとグリッター」の順で、リム以下は部位を選ばないためどの SurfaceType でも空にならない。foldout の開閉状態と保存済みタブ選択は引き継がれる（タブ番号は 7 タブへ clamp）。
- **タブの棚割りを「光の物理」で再整理**（Idol GUI、T-347）。質感タブが実態は「部位別機能の寄せ集め」で、ラベルと中身がずれていた（SDF シャドウが質感タブにある等）。移動 3 件・タブ数と名前は不変: **顔 SDF → 陰・影タブ**（陰の境界をどう決めるかの機能。Doll の「顔（SDF / 陰補正）」と同じ棚）、**髪 - 異方性 → スペキュラタブ**（Kajiya-Kay / 異方性 GGX は鏡面ローブ。Doll と同じ棚）、**布 - シーン → スペキュラタブ**（Charlie sheen も鏡面ローブ）。これで**質感タブ＝散乱・透過（頬・肌 SSS・透過・シアー生地）＝「光が中に入る系」**に純化され、ツヤ系は Smoothness 再掲とともにスペキュラタブへ集結した。部位ゲート（SurfaceType で該当部位にだけ出す挙動）は維持。Hair マテリアルでは質感タブが空になるため案内 HelpBox を追加。
- **Specular タブの先頭に Smoothness Scale を再掲**（Idol GUI）。ツヤ（ハイライトの締まり）を決めるのはローブ幅 = `_Smoothness` だが、そのダイヤルは標準 PBR の流儀どおり表面属性として Base タブ（Mask Map の A チャンネル倍率）に置かれており、「Specular タブの Intensity を上げてもツヤが出ず明るくなるだけ」という迷いが実際に起きた。実体は同一プロパティの再掲（どちらで動かしても同じ値）。Specular Intensity の tooltip にも「強さだけ・締まりは Smoothness 側」の案内を追記。
- **Idol インスペクタの棚割りと説明表示を Doll 基準で見直し**（T-340）:
  - **MatCap を演出（FX）タブ → Specular タブへ移動**。ビュー空間のハイライト＝質感であって演出ではない（Doll も Specular タブに置いている）。演出タブは Outline → Dissolve の Doll と同じ構成・同じ順になった
  - **効いていない機能の説明 HelpBox を出さないようにした**: Dissolve（Progress 0 のとき）/ MatCap（Intensity 0 のとき。パラメータの条件展開も追加）/ Sheer Fabric（Intensity 0 のとき。同）。説明の中身はスライダーの tooltip へ吸収し、情報は失っていない
  - Ramp Override の「任意です」Note と Outline の「参考絵は輪郭無し」Note を削除し tooltip へ吸収（機能 OFF の人に常時場所を取る形をやめた）
  - 残した HelpBox はチャンネル凡例（Mask/NPR Map）・常時有効なセクションの設計宣言・状態依存の警告のみ
- **FaceDirectionBinder を「Play = マテリアルインスタンス / Edit = 非破壊 MPB」の二層方式に書き換え**（`Runtime/Scripts/Idol/FaceDirectionBinder.cs`。T-340）。
  従来は既定が MaterialPropertyBlock で、**顔の Renderer（目・眉・睫毛・口も抱えて実測 8 マテリアル）が Play 中も SRP Batcher から外れていた**。回避用の `Write To Material` トグルは共有マテリアルへの直書きで、他キャラを巻き込み .mat を汚す取引だった。
  新実装は Doll（`DollLiveDirector`）/ Cel（`CelCharacter`）と同型: Play は `Renderer.materials` で初回インスタンス化して **Face のインスタンスだけ**へ書き（旧直書き経路が非 Face マテリアルにも毎フレーム書いていた無駄も解消）、Edit は既存 MPB を読んでから上書きするマージ式プレビュー。OnDisable は Play=元値復元（元値の XZ が退化している既定 (0,0,0,0) は復元しない ── 戻すと normalize(0) で顔が NaN になるため）/ Edit=自分が当てた Renderer だけ MPB 解除（**従来は解除処理自体が無く、Edit の MPB が永久残留していた**）。`_writeToMaterial` フィールドは廃止（外部参照 0 件・旧シーンの直列化キーは Unity が無害に無視）。`[DisallowMultipleComponent]` を追加。NFR-02 / SRP_BATCHER.md / VERIFICATION.md を同時更新。
- **`ToonPBRCommon.hlsl` の `_HeadForward` / `_HeadRight` から `lint:script-set` 印を除去**。Properties へ宣言済み（T-338）の今、この印は何も免除しない不活性コメントで、残すと「要らない所に印を付ける」前例になるため（`_DebugMode` の教訓と同じ）。

- **Cel のキャラ影（CelCharShadow）の毎タップ sincos を除去**（Core の `VogelDisk` 位相回転版へ移行。8 タップ/px。見た目不変・数値等価）。
- **Idol の `ToonVogelDisk` / `ToonRgbToHsv` / `ToonHsvToRgb` も前方転送化**。当初「Core 版が旧式のため寄せない」と判断した 3 関数は、**Idol の実装を Core 側へ逆輸入**（`VogelDisk(float2)` オーバーロード / `EasyPBR_RgbToHsv`・`EasyPBR_HsvToRgb`）して同値になったため、GGX 群と同じ 1 行転送に揃えた。転送前後で `hlsl_compile --cost --variants`（180 プログラム）の命令数・一時レジスタは完全一致。

### Fixed

- **光源の真反対のローアングルで靴などが真っ黒になるのを修正**（T-379）。Energy Conservation が拡散から引く量を `F × Specular Intensity` の生値で取っていたため、(1) 逆光で鏡面を足していないのに引く (2) V ≈ −L でハーフベクトルが潰れ F → 1 (3) 倍率 4 で 1 超え、の 3 つが重なると `saturate(1 − 4) = 0` で影色ごと拡散が消えていた。引く量を「実際に足した割合」（1 で頭打ち・光の当たる面だけ）に揃えた。Energy Conservation 0 の材質は不変。
- **濃い影の中でグリッタがベース反射ごと消えていたのを修正**（T-378）。Core の `ApplyGlitterLight` はフラッシュとベース反射を同じ光エネルギーに掛けるが、Idol は主光源の `shadowAttenuation` だけを渡していたため、影の中で粒が完全に無くなっていた（Doll は direct + indirect を渡す）。環境光（SH × Ambient Intensity）を足し、影の中でもベースは環境光で残り、フラッシュは環境光レベルの控えめな輝きになる。「影の中で強く光らない」という Idol 側の減衰は維持。
- **スライダーのドラッグ中に警告箱が現れて別のプロパティを掴む事故を修正**（Idol GUI、T-377）。IMGUI のスライダーは制御 ID を矩形位置から作るため、上に HelpBox が出入りして矩形がずれるとドラッグ中のまま掴む対象が変わる。原因 2 箇所: (1) 伝達関数の「全面が影のまま」警告が Threshold と Softness の**間**にあり、Softness / Wrap をドラッグすると自分の上に箱が出ていた → 依存する 3 スライダーの後へ移動 (2) Inspector 先頭の未割り当てテクスチャ警告が強度 0 で出入りしていた → ドラッグ中（`GUIUtility.hotControl != 0`）は前回の表示を保つ。
- **Inspector 表示時の引っかかりの一因だった `_DebugMode` の `[Enum]` ドロワー生成失敗を修正**（T-375）。Unity の Enum ドロワーは名前/値の対を**最大 7 組**しか受け付けず、14 組だったため「Failed to create material drawer Enum」をドロワー適用のたび（Inspector 表示・描画時のカリング）にスタックトレース付きで吐き、Debug View も素の数値入力に退化していた。属性を外し、GUI が自前の Popup で選択肢を出すようにした（値の対応は従来どおり・10 は廃止したコンタクト影の欠番）。
- **顔 SDF の UV 中央に焼き込まれていた縦一直線の段差を修正**（T-372 / T-373・Core 側）。80 度前後の光で額から顎までの硬い割線が出ていた原因。Base Softness でもシャドウマップでも法線でもなく、**ベイカーの頂点値平滑化が UV 継ぎ目の複製頂点を別々に平均していた**（継ぎ目の両側で値が勾配に比例して割れる）。平滑化を位置溶接したグラフ上で行うようにした。**顔 SDF は要再ベイク。**
- **1ch（16bit）SDF で光が正面を横切る瞬間に左右が段差で入れ替わるのを修正**（T-371）。ミラー U の切替が `RdotL < 0` の硬い分岐で、以前は「正面は顔全面が光るので見えない」前提だったが、Cast Shadow をベイクした SDF は正面光でも鼻の影が残るため実際に見えていた。両側をサンプルして正面付近（±8.6 度）でクロスフェードするようにした（4ch 経路の軸フェードと同じ考え方）。顔のテクセルだけ 1 フェッチ増。
- **Face SDF の Cast Shadow が睫毛・眉（統合メッシュ内の別マテリアル）を遮蔽物として拾い、目の周りにブロブ状の恒久影を焼き込んでいたのを修正**（T-355・Core 側）。遮蔽コライダーがメッシュ全体から作られていたのが原因で、遮蔽判定を編集中マテリアルのサブメッシュに限定した（鼻・唇の落ち影は残る）。**Cast Shadow ON で焼いた Face SDF は焼き直しが必要。**
- **16bit SDF（R×256+G）の格納規約が 1ch 経路と逆で、顎から首にかけて影が入らなかったのを修正**（T-354・Core 側）。1ch 経路（lilToon 規約）は白 = 最後まで照らされる側で読むのに、ベイカーは内部規約（白 = すぐ陰る側）のまま格納していた。パック時に反転して修正。**16-bit 1ch で焼いた既存テクスチャは焼き直しが必要**（4ch・従来出力は無関係）。
- **リムライトが追加光源（ステージのスポット等）の色を拾わなかったのを修正**（T-351）。Doll はリムを**ライトごと**に計算しているが、Idol は光源ループの外で**主光源 1 灯ぶんだけ**計算していたため、スポットで色を作る使い方ではリムだけ主光源（多くは白）の色に取り残されていた。リムを「視線依存の形」（`ToonRimShape`。深度シルエットのフェッチを含み frag で 1 回）と「ライト依存の適用」（`ToonRimLight`。逆光・向き・落ち影・光のエネルギー）に分割し、主光源と追加光源それぞれで適用するようにした。**深度フェッチはライト数に依らず 1 回のまま**なので追加コストはライトごとの数命令。追加光源ぶんのリムは Additional Light Blend（Add / Max）にも乗り、リムのエネルギーは白飛び防止の上限も通る（Doll と同じ扱い）。**主光源しか無いシーンでは見た目は変わらない。**
- **画面空間輪郭（ScreenSpaceOutlineFeature）が Idol 以外（EasyPBR の Doll・背景など）にも線を引いていたのを修正**（T-342）。全画面ブリットの Roberts cross が深度・法線エッジを無差別に拾っていた。Idol の DepthNormals が A チャンネルへ書く材質 ID に**下限 0.02 の「Idol が描いた画素」目印**を敷き、ブリット側は目印の無い画素圏（A=0 = 他シェーダー・背景）に線を引かないようにした。ID の推奨刻み（0.1 以上）・既定の Material ID Threshold（0.05）とは干渉せず、Idol 同士の見た目は不変。

### Added

- **顔 SDF を下向きの面で切る `SDF Blend Normal Min / Max` を追加**（陰・影タブ、T-376。Doll と同名プロパティ・同じ既定 -1 / 0）。SDF のスイープは水平面内で回すので**光の仰角を知らず**、顎の裏（法線がほぼ真下）を「ほぼ全方位で照らされる」と焼いてしまう。隣接する首は N·L で上からの光に正しく陰るため、下から覗くと「顎裏＝明・首＝陰」の段差が出ていた。オブジェクト空間の法線 Y で SDF を通常の陰影へフェードさせる（真下 → 0、水平以上 → 1）。Doll から移行した材質は持ち越した値がそのまま効く。
- **フィルライト（照り返し）を追加**（ライトタブ、T-370。Doll と同名プロパティ）。指定方向（Pitch / Yaw・ワールド空間）からのバウンス光を陰側に注ぐ ── 床からの暖色の照り返し・空からの寒色が典型。Half-Lambert（wrap 0.5）で柔らかく回り込み、Shade Side Only（既定 1）で主光の陰側に限定する。実ライトと独立した加算光なのでライト構成に依らず安定。0 で分岐ごとスキップ・キーワード非増。
- **ディテールマップを追加**（基本タブ > ベース、T-368。Doll と同名プロパティ + `_DetailOn` トグル）。独立したタイリングを持つ重ねレイヤー（RGB = 色 / A = 合成率）とディテールノーマル ── タトゥー・チークの印刷・布地の織り目など。色は HSV 補正の**後**に合成（ディテールは「その色で置く」意図のため全体の色調補正に巻き込まない）。ノーマルはベースと**接空間で whiteout 合成してから 1 回だけ回す**（TBN 回転を 2 回重ねると合成にならない）。OFF（既定）でフェッチごとスキップ。
- **2 ローブ目のスペキュラを追加**（スペキュラタブ、T-369。Doll のデュアルローブから輸入・同名プロパティ）。シャープな芯の下に広いマットなにじみを敷く肌・シルクの定番で、ハイライトが「点」でなく「面」で光るようになる。F（フレネル）は f0 が同じ物性なので主ローブと共有し、色は自前（`_SecSpecularColor`）。0（既定）で分岐ごとスキップ・キーワード非増。
- **ピーチファズ（産毛の縁光沢）を追加**（質感タブ > 縁の光沢。T-363）。**リムとは向きが逆**で、面が光源を向いているほど強く出る（細かい毛が順光で散乱する現象）。肌・ベルベット・フェルト向け。実装は Core の `CalculatePeachFuzz`（Doll と共有・プロパティ名も同一）で、**Idol は既に `GetFresnelTerms` から `fuzzFresnel` を受け取りながら捨てていた**ため、配線とプロパティ 3 個の追加だけで入った。リムと同じ 2 段構成（視線依存の形は frag で 1 回、ライトごとに適用）で、主光源・追加光源の両方に乗り、Additional Light Blend と白飛び防止も通る。影の中では出さない。`_FuzzIntensity = 0`（既定）で一様分岐ごとスキップ＝従来と完全に同一・キーワード非増。
- **暗転（Black Out）を追加**（演出タブ > ディゾルブ / 暗転。T-361 / T-364）。キャラ単位の駆動は EasyShaderCore の新設コンポーネント **`BlackOutController`**（Add Component > Origuma/EasyShaderCore/Black Out Controller）で行う ── Play = マテリアルインスタンス / Edit = 非破壊 MPB の二層方式で SRP Batcher を維持し、`amount` は Timeline の Animation Track から直接キーを打てる。`_BlackOut` で最終色を黒へ落とす。**エミッシブの後**に掛かるので発光も一緒に沈み、**輪郭パスにも同じ値が掛かる**（掛けないと暗転しきったキャラの輪郭線だけが明るく残って宙に浮く）。アルファは触らないので「消える」のではなく「黒く沈む」── 消す演出はディゾルブの役目。既定 0 で従来と同一、キーワード非増。Doll と同名プロパティなので値をそのまま持ち込める。
- **半透明（Render Mode）を追加**（T-358）。基本タブ > サーフェスに **不透明 / カットアウト / 半透明**の 3 択を追加（Doll の Render Mode と同型）。ブレンド・ZWrite・レンダーキュー・RenderType タグ・アルファクリップを**まとめて**設定するので、個別に触って食い違う事故が起きない。**既定は不透明で既存マテリアルの絵は変わらない。** 半透明を選ぶと GUI が代償を明示する ── 不透明キューを出るため深度プリパスに載らず、**深度モードのリム（Rim Mode = Screen Silhouette）と SSAO が効かなくなる**（Fresnel リムは効く）。ZWrite が切れるのでキャラ内部の前後関係は描画順任せになり、重なる部位は Render Queue で順序を作る必要がある。基本 > ベースにあった Alpha Clip のトグルは Render Mode が持つようになったため撤去（入口を 2 つ持つと必ず食い違うため）。
- **白飛び防止とライト色の整形を追加**（T-350。Doll から輸入・実装は Core 共有・プロパティ名も Doll と同一）。ステージ照明からキャラの可読性を守る防御層。**白飛び防止**: `_DiffuseLightLimit`（1 灯あたりの拡散光の輝度上限。**拡散と透過にだけ**掛かり、鏡面は強い光ほど鋭く光るまま。Doll は伝達関数を通した後の拡散光を抑えるが Idol は光源側を抑えるので、NdotL の階調が残り上限に当たった面がのっぺり潰れない）／ `_AdditionalLightBlendMode`（Add = 物理・重なると白へ飛ぶ / Max = 最も強い 1 灯だけが効くので彩度が残る）。**ライト色の整形**: `_LightColorInfluence`（0 = 同輝度の白色光として扱い、濃い色のスポットで肌が染まらない）／ `_LightSaturationLimit`（色相は保ったまま彩度に上限）／ `_LightMinBrightness`（暗転寄りの演出でも見える下限）── シェーディング前に `Light` 構造体の色を書き換えるので陰影・リム・グリッタまで一貫。**既定はすべて素通し**（Doll の既定 limit 1.0 / Blend Max とは違えてある ── 既存マテリアルの絵を変えないため）。一様分岐でキーワード非増。GUI はライトタブ。
- **深度オフセット（Offset Factor / Units）を追加**（T-348）。lilToon 相当のポリゴン深度オフセットで、眉・睫毛を顔面のわずかに手前に浮かせる用途（負の Factor でカメラ側）。本体・前髪透過・DepthOnly・DepthNormals の各パスに同じ値を掛け、深度テクスチャ利用者（SSAO 等）や Depth Priming と食い違わないようにした。ShadowCaster には掛けない（影の自己遮蔽はシャドウバイアスの管轄）。GUI は詳細タブ > レンダーステート。
- **グリッタ（ラメ・スパンコール）を追加**（T-348）。実装は Core の `BRDF_Glitter.hlsl` を Doll と共有し、プロパティ名も Doll と同一（`_GlitterMask` / `_GlitterColor` / `_GlitterIntensity` / `_GlitterScale` / `_GlitterSize` / `_GlitterTilt` / `_GlitterSparsity` / `_GlitterIridescence` / `_GlitterIridescenceShift` / `_GlitterBaseReflection`）。ライト非依存の幾何（最近傍セル探索）を frag で 1 回だけ計算し、主光源＋追加光源（Forward+ 対応）ごとにフラッシュを加算。主光源分は影・距離減衰で暗くする。`_GlitterIntensity > 0` の一様分岐で**キーワード非増**・0 のときはマスクのフェッチごとスキップ。GUI はスペキュラタブ（ツヤの仲間・部位ゲート無し）。
- **Face SDF ベイクに距離場ブレンド整形と 16bit 1ch 出力を追加**（Baking パネル、T-346）。頂点スイープの生の出力は影境界の等値線にポリゴン割りと法線ノイズがそのまま出て線がガタつく。Core ベイカー（`EasyPbrFaceSdfBaker`）が手描き SDF ツールの本質工程（白黒マスク → 距離場変換 → ブレンド）を画像空間で内蔵し、**外部ツール無しで滑らかな線**を焼けるようにした（DF Blend、既定 ON。丸め半径は Line Softness）。**16-bit 1ch (R\*256+G)** を ON にすると右光スイープだけを 16bit 精度で焼き、T-345 の `_FaceSDF16Bit` 経路へ直結（8bit の約 0.7 度刻みの閾値階段が消える。ミラー U 規約＝左右対称の顔向け）。パネルは焼いた出力形式に合わせて `_FaceSDF4Ch` / `_FaceSDF16Bit` を**対で切り替える**（片方だけ残ると 16bit パッキングを 4ch として誤読するため）。Cast Shadow（鼻・眉の落ち影レイキャスト）の設定もパネルに追加 ── 距離場ブレンドと合わせると落ち影が整った線として焼ける。
- **顔 SDF の 1ch 経路を lilToon 系「外部 SDF」の一級経路として整備**（T-345）。角度写像（threshold = 1 − (F·L)·0.5+0.5）・ミラー U・smoothstep は元々標準規約と同型だったため、外部生成（手描き・マスク距離場ブレンド産）の SDF テクスチャがそのまま挿せる。`_FaceSDF16Bit` で **R×256+G の 16bit パッキング**をデコード（8bit 単チャンネルは閾値が約 0.7 度刻みの階段になり、ライトを回すと影の線がカクつく ── 非圧縮テクスチャ必須）。自前ベイク（4ch・幾何スイープ）は従来どおり併存。
- **リムライトに Fresnel (PBR) モードを追加し、既定にした**（`_RimMode` = 1。T-343）。深度差方式（Screen Silhouette = 0）は「背後に近い物があると消える・画面端で消える・オフセット先の遮蔽で消える」という画面空間サンプリング原理の弱点があり（利用者実測）、既定を Fresnel 側へ置いた。**`_RimMode` を一度も保存していないマテリアル（＝全既存マテリアル）はリムの見た目が Fresnel へ変わる**。アニメ的な縁取り線が要る材質は 0 に戻せば従来どおり。1 = Fresnel PBR は **EasyPBR(Doll) と同じ EasyShaderCore の式**（`GetFresnelTerms` / `CalculateRimLight`）へ委譲し、リムがライトのエネルギーに比例する ── ステージ照明の色・強度がそのまま縁に乗る。深度テクスチャを読まないぶん従来モードより軽量。`_RimFresnelThickness`（Doll と同じ 0=極細〜1=極太の写像）を追加。uniform 分岐でキーワード非増、GUI はモード別に効くパラメータだけを表示。
- **Documentation~ に `SRP_BATCHER.md` と `VARIANTS.md` を新設**（T-340）。EasyPBR の 2 本立て（実践ガイド＋キーワード台帳）と同構成で、Idol / Cel 両章を持つ。⚡ tooltip（ShaderGuiKit）が案内する `SRP_BATCHER.md` が**存在しない状態を解消**。キーワード表は `param_check.py` の `ALLOWED_KEYWORDS` と 1:1。あわせて ARCHITECTURE.md の LightMode 旧名（`ToonOutline`/`ToonHairShadow` → 実装どおり `IdolOutline`/`IdolHairShadow`）と命名節、CLAUDE.md のディレクトリ図（`Assets/ToonPBR/` 前提 → パッケージ実態）と検証コマンドのパスを実態へ更新。
- **`IdolSetupWindow` を新設**（`Editor/Idol/IdolSetupWindow.cs`、`Window > Origuma > Idol Setup`）。Idol の Renderer Feature 3 種（`ToonOutlineFeature` / `HairShadowFeature` / `ScreenSpaceOutlineFeature`）をワンクリックで追加/削除する。基底の `FeatureSetupWindowBase` はそもそも旧 IdolSetupWindow を汎用化して Core へ移管したもので、移管後に Idol 用の派生だけ再作成されていなかった（Cel/Doll にはあった）。
- **Idol インスペクタにプロパティ検索ボックス**（タブバー上。入力中はタブを無視して表示名/プロパティ名の部分一致をフラット表示。Doll と同実装・Unity 6000.3 の propertyFlags API 分岐込み）。プロパティ 191 個のタブ横断検索。
- **Idol インスペクタのツールバーに SRP Batcher ガイドへの DocLink**（新設した `Documentation~/SRP_BATCHER.md` の GitHub URL）。
- **Outline / Hair Shadow セクションに Renderer Feature 未導入ガード**（`FeatureSetup.DrawFeatureGuard`。未導入なら警告＋ Idol Setup を開くボタン）。Outline は既定 OFF が設計思想なので **ON のマテリアルにだけ**表示。Hair Shadow は従来の文章 Note を検知式ガードへ置換（`HairShadowCaster` 側の注意は Note のまま残る）。
- **GPU Instancing の警告**（Idol / Cel 両インスペクタ）。両シェーダーとも `multi_compile_instancing` を意図的に宣言していないため、Enable GPU Instancing は**そのレンダラーを SRP Batcher から外すだけ**であることを ON 検出時に警告。
- Idol インスペクタの `Section()` が desc 引数を素通しするようになった（従来は捨てていた）。
- **batchmode 一括適用の入口 `IdolBatchApplyCI.RunAllCI` を追加**（`Editor/Idol/IdolBatchApplyCI.cs`。Editor を開けない環境向け・Selection 非依存）。HairSeeThrough 空振り停止 → `_OutlineOn` 0 揃え → ToonOutlineFeature 導入 → サーフェスタイプ設定 → 絵に出ない計算停止を「絵が変わらないものが先」の順で一括実行する。判定はメニュー版（`ToonPBRSurfaceTypeFromName` / `ToonPBRDropDeadWork`）の同じコードを internal 共有で通し（複製は必ずずれる ── T-107）、メニュー版が確認ダイアログで見せる「目覚める値」一覧はログへ出す。
- **HLSL 純関数 4 つの本体を EasyShaderCore と共有**（`ToonPBRCommon.hlsl` が Core の `Common_Math.hlsl` / `BRDF_GGX.hlsl` を include。T-340）。実装が同値だった `ToonIGN`（バイト同値）と `ToonD_GGX` / `ToonV_SmithGGX` / `ToonF_Schlick`（本体同値。後者 2 つは引数順が Core と逆のため転送でスワップ）を **1 行の前方転送**にした。Toon\* の名前を残すのは静的検査（E008〜E012 / W108）が Toon 接頭辞だけを検査対象にするためで、**呼び出し側 5 ファイルは 1 文字も変えていない**。転送前後で `hlsl_compile.py --cost --variants`（180 プログラム）の**命令数・一時レジスタが完全一致**することを確認済み＝生成コード不変。**寄せない判断も明文化**: `ToonVogelDisk`（UNROLL 時に sincos が定数畳み込みされる改良版。Core 版に置換すると実行時 sincos 32 回が復活）と `ToonRgbToHsv` / `ToonHsvToRgb`（E009 準拠の max 下限形・float。Core は `+e` 下駄形・half）は Idol 実装を維持し、理由を各関数のコメントに追記。検査も同時に更新: `param_check` の守り検査（`check_guards`）が **Core への include を辿って本体まで見る**ようになり（Smith 可視項の `max(1-a², 0)` が Core 側で消えても検出する）、`self_test` に Core 側へ欠陥を注入する複製ベースのケースを追加（97 項目 / カバー率 76 検査）。

- **新シェーダー `Idol`（`Origuma/EasyToon_URP/Idol`）。** BRDF を物理ベースのまま維持し、
  **拡散光の伝達関数だけ**を様式化する。背景が PBR でライトが動く環境
  （ライブ演出・ワールド）でキャラだけが浮かないことを狙ったもので、
  **`Cel` の置き換えではなく別用途**（[README](../README.md) の「Cel との使い分け」）。
  - **拡散**: 曲率駆動のソフトシャドウ（境界の広さが面の曲率で変わる）/ HSV による影色変換 /
    ターミネータの暖色 / 正面・上向きの陰の持ち上げ（顔の自己陰をマスク無しで消す）
  - **影**: PCSS（接地硬化）/ コンタクトシャドウ / マイクロシャドウ /
    **前髪の影の専用シャドウマップ**（Renderer Feature が正射影で焼く）/ 顔 SDF
  - **鏡面**: GGX（高さ相関の厳密な Smith 可視項）/ クリアコート / 薄膜干渉 /
    エネルギー保存つき Charlie sheen / 2 ローブの異方性 Kajiya-Kay / スペキュラ AA
  - **環境**: ボックス投影のプローブブレンド / ベントノーマル / 多重バウンス AO /
    鏡面遮蔽 / 多重散乱の補償
  - **URP 17 の受け口**: デカール（DBuffer）/ APV / ライトクッキー / ライトレイヤー /
    **MotionVectors パス**（TAA の前提）/ LOD クロスフェード
  - **演出**: 前髪透過（部位プリセット付き）/ シアー生地 / ディゾルブ /
    MatCap（**加算のみ** ── 乗算は環境の主経路を上書きするため持たない）/
    アルベドの HSV 補正 / 画面空間輪郭
  - **移行ツール**: `Cel` と EasyPBR `Doll` から値を移す（下見つき・Ctrl+Z 一回で戻る）
  - パス 8 本 / プロパティ 190 / 自前キーワード 10。**キーワードは既定 OFF の機能に
    足していない** ── 一様分岐で切るのでバリアントが増えない

- **Idol の Baking タブ: Face SDF に `X Axis Tilt`（左右スイープ光の仰角・度、既定 0）を追加。**
  R/G（左右）チャンネルのスイープ面を顔 Up 方向へ倒し、「やや上から差す光」を前提に
  境界角度を焼く。水平スイープのままだと顎下〜首の境界が実際のライト（通常は上方から）
  とずれ、モデルによっては首まわりの影が不自然になるため。左右とも同じ「上」へ倒すので
  左右対称は保たれ、倒した軸は Forward と直交のまま＝格納値の意味（`cosθ*0.5+0.5`）が
  変わらないので、上下（B/A）チャンネルもランタイム（`ToonPBRLighting.hlsl` の 4ch 合成・
  1ch 経路とも）も**変更なし**。既定 0 で従来と同一の焼き上がり。
  実体は EasyShaderCore の `EasyPbrFaceSdfBaker`（`Settings.xAxisTilt`）。

- **顔 SDF: 光が真後ろ（および真正面）を通るとき左右チャンネルが段差で入れ替わるのを修正**
  （`Runtime/Shaders/Idol/Shading/ToonPBRLighting.hlsl` の 4ch 経路）。
  4ch の重み（`max(0, ±dirX)` / `max(0, ±dirY)`）は前方軸まわりの**方位だけ**で決まるため、
  光が前方軸上を通る瞬間は両成分が同時に 0 へ落ちて方位が定まらず、無限小の符号で
  R↔G が瞬時に入れ替わっていた。真正面は顔全面が光るので見えないが、真後ろは陰の
  遷移帯（`|f - sdf| < soft`）に入るため段差として出る。横成分の長さ
  `lateral = |sin(光と顔前方のなす角)|` を方位の確からしさとし、軸から約 14.5°
  （`lateral < 0.25`）の内側では 4ch の平均へ `smoothstep` でフェードして連続化した。
  AA 項（`sdfAA`）も同じ係数で寄せるので境界の甘さも跳ねない。
  **新規プロパティ・キーワードなし・再ベイク不要。**

- **`Curvature Influence`（`_CurvatureSoftness`）の既定を 0.8 → 0 に変更。**
  曲率は法線の画面微分から作るため、補間法線の微分は**三角形の中で一定・辺で不連続**。
  0.8 では境界幅が三角形ごとに最大 1.8 倍まで飛び、**低ポリのキャラで陰にポリゴンの面が
  並んで見えていた**（陰の遷移帯に入っている面はすべて明度が幅で決まるため、
  ターミネータ付近だけでなく胴体一面に出る）。ShaderGUI には T-339 の時点で
  「トゥーン用途では 0 が既定的に正しい」と判断が書かれていたので、既定をそれに合わせた。
  **変わるのは新規マテリアルだけ**（既存はシリアライズ済みの値を保つ）。曲率で境界幅を
  変えたい場合は下記の Curvature Map 経由で使う。

- **`_CurvatureMapStrength` を「画面空間推定への変調」から「ベイク値への置き換え」に変更**
  （`Runtime/Shaders/Idol/Passes/ForwardPass.hlsl`、表示名も `Curvature Map Strength` →
  `Curvature Map Blend`）。従来は `c.curvature *= lerp(1, baked*2, strength)` と乗算だったため、
  焼いたマップを入れても**画面空間の三角形ごとの飛びが残り**、facet は消えなかった。
  `lerp(画面空間, |baked*2-1|, strength)` に変え、1 で完全にベイク値へ置き換わるようにした。
  ベイク済み曲率は頂点から作られテクセル単位で連続するので、**曲率駆動の柔らかさを
  保ったまま facet だけ消せる**唯一の経路になる。符号（凸/凹）は境界幅に関係ないので
  絶対値を取る。1 では `_CurvatureReferenceRadius` は効かず、大きさの校正はベイカーの
  `Intensity`（既定 4）が持つ。**既定 0 なので既存マテリアルの見た目は不変。**
  PROPERTIES.md が元々「画面空間の曲率推定を上書きします」と書いていた記述とも一致した。

- **Baking タブ: Face SDF を焼いたら `Use 4ch SDF`（`_FaceSDF4Ch`）も立てるようにした。**
  Baker が焼くのは常に 4ch なのに、マテリアル既定は 1ch のレガシー経路
  （**R しか読まず UV をミラーして左右を作る**）。焼いただけで放置すると G/B/A が
  捨てられるうえ、ミラーの切り替えが `RdotL` の符号で二値に飛ぶため真後ろで
  露骨な段差になる（T-081 で同じ踏み方をしている）。`_FaceFlatness` と同じく
  **0 のときだけ立てる**ので、意図して 1ch に戻している materials は尊重する。

### Changed

- **シェーダー名を `Idol` から `Cel` に変更。** 正式リリース前のため、`Idol` の名前は
  **新しく追加するシェーダー**（PBR ライティングに馴染むトゥーン）へ渡す。
  ライブ演出のように**背景が PBR でライトが動く**用途はそちらの土俵で、
  名前が用途を表すようにするための入れ替え。
  - シェーダー名 `Origuma/EasyToon_URP/Idol` → `Origuma/EasyToon_URP/Cel`
  - LightMode `IdolOutline` / `IdolCharShadow` → `CelOutline` / `CelCharShadow`
  - 型・ファイル名 `Idol*` → `Cel*`（9 型 / 36 ファイル）
  - **マテリアルの参照は GUID なので切れない。** `.meta` は移動先へ持っていってある
  - `0.1.0` の記載も現行名に揃えた（正式リリース前のため）

### Added
- **境界ソフト化（Stage Shadow）**: 明暗境界（ターミネータ）を滲ませて、法線の食い違い・低ポリの段差・メッシュ跨ぎの継ぎ目を目立たなくする機能セット。すべて既定 OFF・**新規キーワードなし**（uniform 動的分岐）・単一 CBUFFER 維持。既定値では Phase 1 の見た目が完全不変。
  - **ターミネータ・スキャッタ（境界の赤滲み）**: EasyShaderCore の `ApplyTerminatorScatter`（pre-integrated skin scattering 近似、`band = saturate(4·s·(1-s))^…` で 0.5 跨ぎにバンドが立つ）を流用し、`CalculateSingleLight` 内で角度陰確定後の `diffuseColor` に `litMask` を finalShade として適用。2段影モードで2影が有効なら 2影の境界にも重ねる（Ramp モードは litMask=halfLambert の連続値で 1 境界のみ）。curvature mask は Cel にベイク配線が無いため 1.0 固定。`_SkinScatterIntensity==0` で `UNITY_BRANCH` ごとスキップ（既定 0）。
  - **境界の暗縁（コアシャドウ）**: 明暗境界の遷移帯だけを乗算で暗くする（`edgeBand = saturate(4·litMask·(1-litMask))` を pow で細く絞り、`diffuseColor *= lerp(1, _ShadowEdgeDarken, edgeBand)`）。スキャッタ適用後に乗せ、赤滲みの縁に細い暗線を出す。`_ShadowEdgeDarken==1`（既定）で無効。
  - **境界ディザ**: ランプのしきい値評価に IGN ノイズを ± で加えてから量子化し、境界だけをスクリーン安定なノイズで割る（バンディング・段差を隠す）。`CalculateSingleLight` 内で rampInput 確定後・量子化直前に注入（ForwardPass から `positionCS.xy` を伝播。メイン／追加ライト両経路で一貫）。`_ShadowDitherAmount==0`（既定）でスキップ。
  - スキャッタ・暗縁は「境界の滲み」なので追加ライトにも乗る（`CalculateSingleLight` はメイン／追加両方から呼ばれ、同じ分岐が通る）。
  - **追加プロパティ**（すべて既存の単一 CBUFFER に追加・SRP Batcher 維持）: `_SkinScatterColor`（Color, 既定 (0.9,0.3,0.2), **Doll Skin Scatter と同名**）/ `_SkinScatterIntensity`（Range 0–1, 既定 0, **同名**）/ `_SkinScatterWidth`（Range 0–1, 既定 0.5, **同名**）/ `_ShadowEdgeDarken`（Range 0–1, 既定 1=無効, Cel 固有）/ `_ShadowEdgeWidth`（Range 0–1, 既定 0.3, Cel 固有）/ `_ShadowDitherAmount`（Range 0–0.1, 既定 0, Cel 固有）。Skin Scatter 系は Doll と同名のため Doll→Cel 変換で値が引き継がれる（[MIGRATION](Documentation~/MIGRATION.md)）。
  - **インスペクター**: 「陰・影」タブに `Boundary Softening（境界ソフト化）` セクションを新設（既定折りたたみ）。スキャッタ / 暗縁 / ディザの 3 サブグループ、各 Intensity=0・既定値で「効果なし」が分かる表記。
- **PBR スペキュラモード + 環境反射**: トゥーンキャラをフォトリアル背景の 3D ライブ会場に置いたとき、スペキュラだけを物理寄りにして環境と調和させる機能。
  - **`_SpecularModel`（0=Cel 既定 / 1=PBR）**: 直接光スペキュラを uniform 動的分岐で Cel（Blinn-Phong セル化）と PBR（GGX 1ローブ・EasyShaderCore の `GGXLobe`）に切替。**新しい shader keyword は増やさない**（静的キーワードは `_ALPHATEST_ON` / `_DISSOLVE_ON` / `_IDOL_CHARSHADOW` の 3 つのまま）。1キャラ内で肌・髪＝Cel、金具・革・エナメル＝PBR の混在を SRP バッチを割らずに実現するため、バリアントではなく uniform 分岐を選択（ループのない小 ALU で分岐の損も小さい）。PBR パスは既存のスペキュラマスク・`_SpecularShadeInfluence`（陰側減衰）・落ち影・lightEnergy 経路を Cel と共用し、追加ライトでも同じ分岐が通る。既定 0 で従来のセル描画と完全一致。
  - **環境反射（Reflection Probe）**: `_ReflectionStrength`（既定 0 = OFF）で、EasyShaderCore の `EasyPBR_EnvironmentReflection` によりシーンの Reflection Probe をスペキュラに加算。ぼけは AA 適用後 Smoothness 由来の roughness、縁の重みは Fresnel(F0)、遮蔽はスペキュラマスク × ベイク AO。`_SpecularModel` とは独立のトグルで、直接光ループの外（Black Out より前）に合成。0 のとき `UNITY_BRANCH` でサンプルごとスキップ。
  - **追加プロパティ**: `_SpecularModel`（Float, enum Cel/PBR, 既定 0）/ `_SpecularF0`（Range 0–1, 既定 0.04, **EasyPBR Doll と同名**）/ `_ReflectionStrength`（Range 0–2, 既定 0, **EasyPBR Doll と同名**）。いずれも既存の単一 CBUFFER に追加（SRP Batcher 維持）。
  - **インスペクター**: Specular セクションに `Specular Model` ポップアップを追加。Cel 選択時は Threshold / Softness、PBR 選択時は Fresnel(F0) を表示。環境反射は同セクションのサブグループ（既定 OFF）。

### Fixed
- **キャラ専用セルフシャドウで、遮蔽物がないのに影が落ちる問題を修正**:
  - **根本原因: 受影 UV の上下反転**。キャスター用の GPU 変換済み VP（`GL.GetGPUProjectionMatrix` 適用済み）を受影サンプルにも使い回していたが、D3D 系ではラスタライザのビューポート変換とサンプラの v 方向が対称でないため UV.y が上下鏡像になり、鏡像位置の深度（例: スカートに対する上半身）を拾って偽の落ち影が出ていた。URP の `ShadowUtils.GetShadowTransform` と同じく、受影用行列（`_CelCharShadowWorldToUv`）を GPU 変換**前**の射影から別途構築する方式に修正
  - あわせてキャスターバイアスも再設計した。
  - 法線バイアスの向きが外側（光源側）で、格納深度が実面より浅くなり自己影アクネを逆に作っていた → URP の `ApplyShadowBias` と同じ**内側（inset）**に反転し、NdotL でスケール（光に正対する面はオフセット不要、掠め角ほど強く）。Feature がキャスターへ `_CelCharShadowLightDir` を新たにグローバル供給する
  - バイアス量が固定値（法線: ワールド 2cm / 深度: クリップ z 定数）で、薄い布を貫通したりバウンディングサイズで効きが変わっていた → **「シャドウマップ 1 texel の世界サイズ × 倍率」のワールド長**に変更（URP 標準影と同じスケーリング。キャラ 1 体・2048 なら数 mm 程度になり、深度バイアスもライト方向のワールド後退に統一）
  - **Feature のバイアス項目を改名**（`_depthBias`/`_normalBias` → `_depthBiasTexels`/`_normalBiasTexels`、既定各 2.0）。単位が変わったため旧設定値は引き継がれず、既存の Renderer Data では新しい既定値が適用される
- **Package Manager からの追加直後にも EasyShaderCore の自動インストールが走るように修正**: 本体 Editor asmdef（`Origuma.EasyToon.URP.Editor`）を versionDefines + defineConstraints（シンボル `EASYSHADERCORE_PRESENT`）で Core 不在時にコンパイル対象から除外した。従来は Core 不在時のコンパイルエラーでドメインリロードが完了せず、PM 追加直後に `InitializeOnLoad`（Installer）が走らないため、エディタを再起動するまで Core が自動導入されなかった。除外により PM 追加直後（同一エディタセッション内・再起動不要）に Installer が走り、ゼロクリックで Core が導入される。

## [0.1.0] - 2026-07-08

初期実装。

### Added

- **Toon シェーディングコア**: 2段影（色相シフト/彩度制御付き陰色）/ Ramp テクスチャモード / ベイク AO による影しきい値オフセット / Shade Normal / 落ち影の分離塗り分け / セルスペキュラ（Specular AA 付き）
- **顔**: Face SDF 顔影（4ch・左右非対称対応）/ 前髪透過（ステンシル bit1=Brow, bit2=Eye + HairSeeThrough パス）/ 髪→顔のスクリーンスペース落ち影（深度差の窓判定）
- **髪・質感**: 天使の輪（ヘアフローマップ駆動・カメラ追従率制御）/ ストッキング・シアー生地（視角依存の布レイヤ＋すそ光沢）
- **リムライト**: スクリーンスペース深度リム（ピクセル幅一定）/ フレネルリム / バックライトリム
- **キャラ専用セルフシャドウ**: `CelCharShadowFeature`（Render Graph・専用深度マップ・3x3 PCF・髪→顔の落ち影）
- **アウトライン**: 背面法線拡張（頂点カラー制御・距離/FOV 正規化・スクリーン幅クランプ・Albedo ブレンド）+ `CelOutlineFeature`
- **演出**: `CelCharacter`（仮想ライト方向オーバーライド / BlackOut / BackRim / 前髪透過の一括制御・Timeline 対応）/ Dissolve / Light Conditioning / Anti-Blowout / SH 整形
- **Editor**: タブ式カスタムインスペクター（Chara Part プリセット・⚡バリアント可視化）/ ベイク統合（EasyShaderCore の Baker を利用: AO / Shade Normal / Hair Flow / Face SDF）/ `Cel Setup` ウィンドウ（RendererFeature ワンクリック追加）/ `Doll to Cel Converter`（マテリアル変換）
- **Doll(EasyPBR) 互換のプロパティ命名**: 意味が一致するプロパティは Doll と同名（シェーダー差し替えで値が引き継がれる → [MIGRATION](Documentation~/MIGRATION.md)）
- SRP Batcher 互換（単一 CBUFFER・静的キーワードは `_ALPHATEST_ON` / `_DISSOLVE_ON` / `_IDOL_CHARSHADOW` のみ）

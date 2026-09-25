# プロパティ一覧（自動生成）

**この文書は生成物です。手で書き換えても次の生成で消えます。**
説明を足したいときは `ToonPBRShaderGUI.cs` の tooltip を書いてください ──
インスペクタと文書の両方に同時に効きます。

```bash
python gen_properties.py --write
```

シェーダー: `Idol.shader` / プロパティ 229 個

⚡ はシェーダーバリアントを生むもの（マテリアル間で値が違うとバッチが分断される）。

## 基本（Base）

### サーフェス（Surface） ／ サーフェスタイプ（部位）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SurfaceType` ⚡ | Surface Type | `Float` | `0` | どの質感機能をコンパイルして表示するかを決めます |

### ベース（Base）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_BaseMap` | Base Map | `2D` | `"white" {}` | — |
| `_BaseColor` | Base Color | `Color` | `(1,1,1,1)` | — |

### ベース（Base） ／ ノーマルマップ (凹凸)

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_NormalMapOn` | Normal Map On | `Float` | `0` | — |
| `_BumpMap` | Bump Map | `2D` | `"bump" {}` | — |
| `_BumpScale` | Bump Scale | `Range(0,2)` | `1` | 接空間ノーマルの強さ |

### ベース（Base） ／ ディテールマップ（タトゥーやチーク等）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DetailOn` | Detail On | `Float` | `0` | 独立したタイリングを持つ重ねレイヤー ── タトゥー・チークの印刷・布地の織り目など。RGB = 色 / A = 合成率。NPR Map の B で効かせる場所を絞れます（色・ノーマルとも） |
| `_DetailMap` | Detail Map (RGB=color A=blend) | `2D` | `"black" {}` | — |
| `_DetailColor` | Detail Color | `Color` | `(1,1,1,1)` | RGB はディテールの色。A は混ぜる量 |
| `_DetailMultiply` | Detail Multiply | `Float` | `0` | OFF = ディテールの色で置き換え（タトゥー・プリント）。ON = 乗算（DCC で焼いた生地の陰・AO） |
| `_DetailNormalMap` | Detail Normal Map | `2D` | `"bump" {}` | — |
| `_DetailNormalScale` | Detail Normal Scale | `Range(0,2)` | `1` | ベースのノーマルの上に whiteout 合成されます。効くのは影のグラデーション・sheen・リムで、ハイライトと映り込みはベースの法線のままです（細かい織り目が点にならないように） |
| `_DetailCavity` | Detail Cavity | `Range(0,2)` | `0` | ディテール法線は鏡面に入らないので、ハイライトからは織り目が見えません。代わりにディテール法線の斜面で鏡面・sheen・クリアコート・映り込みを落とします ── ハイライトの中に織り目の陰が出ます（点にはなりません） |
| `_DetailNormalRotation` | Detail Normal Rotation | `Range(-180,180)` | `0` | 織り目を回します（度）。引いた法線も合わせて回すので陰影の向きも付いてきます。材質全体で 1 つの角度です |

### ベース（Base） ／ 色調補正 (HSV)

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_AlbedoHueShift` | Albedo Hue Shift | `Range(-0.5,0.5)` | `0` | 色相を回します |
| `_AlbedoSaturation` | Albedo Saturation | `Range(0,2)` | `1` | 鮮やかさ |
| `_AlbedoValue` | Albedo Value | `Range(0,2)` | `1` | 明るさ |

### マスクマップ（Mask Map）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_MaskMap` | Mask Map | `2D` | `"white" {}` | パック済みの RGBA マスク |
| `_Metallic` | Metallic | `Range(0,1)` | `0` | R チャンネルへの倍率（1 でマップの値そのまま） |
| `_MaskAIsRoughness` | Mask A Is Roughness | `Float` | `0` | ON = A が Roughness（InstaMat / Substance の標準出力）で、ここで反転して読みます。OFF = A は Smoothness。Mask Map が無いときは反転しません（白を反転すると全面マットになるため） |
| `_DirectOcclusion` | Direct Occlusion | `Range(0,1)` | `0.3` | 物理的には AO は間接光だけのもの。絵として要るときだけ上げる |
| `_MicroShadow` | Micro Shadow | `Range(0,1)` | `1` | 斜めから当たる直接光を遮蔽量で削る |

### ジオメトリマップ（Geometry Map）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_GeometryMapOn` ⚡ | Geometry Map On | `Float` | `0` | — |
| `_GeometryMap` | Geometry Map (R=Cavity G=Curvature B=AO) | `2D` | `"white" {}` | — |
| `_OcclusionSource` | Occlusion Source | `Float` | `0` | Mask G = 作り込んだ遮蔽（細かい皺など）/ Geometry B = Unity で焼いた遮蔽（脇の下・襟の内側など）/ Both = 掛け合わせ |
| `_CavityStrength` | Cavity Strength | `Range(0,1)` | `0` | R チャンネルが窪みでアルベドと鏡面をどれだけ落とすか。0 でこのチャンネルは無効 |
| `_OcclusionStrength` | Occlusion Strength | `Range(0,1)` | `1` | 選んだ遮蔽が間接光をどれだけ落とすか。0 で無効 |

### NPR マップ（NPR Map）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_NPRMapOn` | NPR Map On | `Float` | `0` | — |
| `_NPRMap` | NPR Map | `2D` | `"white" {}` | — |
| `_NPRShadowOffsetStrength` | NPR Shadow Offset Strength | `Range(0,1)` | `0.4` | G が影の境界をどれだけずらすか |

### ファブリックマップ（Fabric Map）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FabricMapOn` ⚡ | Fabric Map On | `Float` | `0` | — |
| `_FabricMap` | Fabric Map | `2D` | `"white" {}` | — |
| `_Reflectance` | Reflectance | `Range(0,1)` | `0.5` | 非金属の反射率。f0 = 0.16 × 値²（0.5 で 0.04 = 従来の固定値）。綿 0.35 / 絹・サテン 0.55 / エナメル・ビニール 0.7 あたり。Fabric Map の R が掛かります |

### 発光（Emission）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_EmissionOn` | Emission On | `Float` | `0` | — |
| `_EmissionMap` | Emission Map | `2D` | `"white" {}` | — |
| `_EmissionColor` | Emission Color | `Color` | `(0,0,0,1)` | HDR |

### アウトライン（Outline）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_OutlineOn` ⚡ | Outline On | `Float` | `0` | 背面法線押し出しの輪郭を別 LightMode で描きます。既定は OFF ── 参考にしている絵には輪郭線が無く、逆光リムと明度差でシルエットを抜いています |
| `_UseSmoothNormal` | Use Smooth Normal | `Float` | `0` | SmoothNormalBaker をメッシュに通してあることが前提 |
| `_UseVertexWidth` | Use Vertex Width | `Float` | `0` | 頂点ごとに線を細くできます（睫毛や細いベルトなど） |
| `_OutlineColor` | Outline Color | `Color` | `(0.2,0.15,0.18,1)` | — |
| `_OutlineAlbedoBlend` | Outline Albedo Blend | `Range(0,1)` | `0.5` | 1 で線を単色でなく表面の色で染めます |
| `_OutlineAlbedoDarken` | Outline Albedo Darken | `Range(0,1)` | `0.45` | — |
| `_OutlineWidth` | Outline Width | `Range(0,10)` | `0.8` | — |
| `_OutlineZOffset` | Outline Z Offset | `Range(0,1)` | `0` | 線をカメラから遠ざけて、本体を突き抜けないようにします |
| `_OutlineMaxDistance` | Outline Max Distance | `Range(1,100)` | `25` | 画面上での太りを止める距離。引きの画で効きます |

## 陰・影（Shading）

### 拡散の伝達関数（Diffuse Transfer）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ShadowThreshold` | Shadow Threshold | `Range(0,1)` | `0.2` | 明暗の境界の位置 |
| `_ShadowSoftness` | Shadow Softness | `Range(0.001,0.5)` | `0.2` | 曲率で広げる前の、境界の基本の幅 |
| `_CurvatureSoftness` | Curvature Softness | `Range(0,4)` | `0` | 曲がった面ほど境界を広げる度合い。幅 = Base Softness × (1 + 曲率 × Influence)。曲率は焼いた Curvature Map（質感タブ > ベイクしたマップ）から取ります ── 無ければ何も起きません |

### 拡散の伝達関数（Diffuse Transfer） ／ シェーディング法線

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ShadeNormalMap` | Shade Normal Map | `2D` | `"bump" {}` | 拡散の伝達だけに使う、なめらかな法線 |
| `_ShadeNormalStrength` | Shade Normal Strength | `Range(0,1)` | `0` | — |
| `_DiffuseWrap` | Diffuse Wrap | `Range(0,1)` | `0.5` | 光を明暗境界の先まで回り込ませます。エネルギー保存形なので伝達の上限が 1/(1+wrap) まで下がります（上げるほど天井が下がる） |

### 拡散の伝達関数（Diffuse Transfer） ／ リアルタイム影の受け

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ReceiveShadowStrength` | Receive Shadow Strength | `Range(0,1)` | `0.7` | 最後に一度だけ掛かります。HQ 影とマイクロシャドウがここに畳まれているので、下げるとまとめて薄くなります |
| `_ShadowAttenSoftness` | Shadow Atten Softness | `Range(0.001,1)` | `0.7` | 遷移の幅。中心は「半分遮蔽」に固定なので、影の大きさは変わらず柔らかさだけが変わります |
| `_ShadowEdgeAA` | Shadow Edge AA | `Range(0,2)` | `1` | 境界を 1 画素ぶん広げてジャギを隠します |

### HQ セルフシャドウ（HQ Self Shadow）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HQShadowOn` ⚡ | HQ Shadow On | `Float` | `1` | 主光源のみ。全機能の中でテクスチャフェッチが一番多い |
| `_HQShadowSoftness` | HQ Shadow Softness (texels) | `Range(0,3)` | `1` | フィルタ半径 = 1 + 値 × 6 テクセル（メートルではありません）。16 タップだと 1.5 あたりから粒（ディザのノイズ）が見え始めます ── HQ Shadow Taps を上げるか、粒を許容するか。シャドウマップの解像度を下げれば同じ値でもワールドでの半影は広がります（タダで柔らかくなる） |
| `_HQShadowTaps` ⚡ | HQ Shadow Taps | `Float` | `1` | — |
| `_ReceiverNormalBias` | Receiver Normal Bias | `Range(0,4)` | `1` | — |
| `_BlueNoiseTex` | Blue Noise Tex (dither) | `2D` | `"gray" {}` | — |

### 影の色（Shade Color） ／ 落ち影の色

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_CastShadowColor` | Cast Shadow Color | `Color` | `(0.5, 0.45, 0.5, 1)` | シャドウマップ由来の落ち影（髪・手）だけに掛ける色。NdotL の陰は通常の影色のままです |
| `_CastShadowColorStrength` | Cast Shadow Color Strength | `Range(0,1)` | `0` | 色をどれだけ掛けるか。0 で落ち影も通常の影色。影の中の鏡面と環境光も同時に落とすので、落ち影がNdotL の陰とは別の出来事として見えます |

### 影の色（Shade Color）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_UseRampMap` | Use Ramp Map | `Float` | `0` | — |
| `_RampMap` | Ramp Map | `2D` | `"white" {}` | — |
| `_RampRowCount` | Ramp Row Count | `Float` | `8` | テクスチャに縦へ何本のランプを並べてあるか |
| `_RampIndexOverride` | Ramp Index Override | `Float` | `-1` | この材質が使うランプの行（-1 で先頭行） |
| `_RampStrength` | Ramp Strength | `Range(0,1)` | `1` | 1 でランプだけが影の色を決めます。1 未満では HSV の影色が混ざり、その設定が下に出ます |

### 顔（SDF）（Face (SDF)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FaceSDFMap` | Face SDF Map (16-bit R*256+G) | `2D` | `"white" {}` | 正面から 180 度スイープした境界の角度を R×256+G に詰めたもの。Baking タブで焼きます。**非圧縮・sRGB OFF 必須**（BC 圧縮は RG の連続性を壊します） |
| `_FaceSDFFlipU` | Face SDF Flip U | `Float` | `0` | 焼いたときの左右の取り決めに合わせます |
| `_FaceShadowOffset` | Face Shadow Offset | `Range(-0.5,0.5)` | `0` | 境界ぜんたいをずらします。正で顔がより回り込んでも明るいまま |
| `_FaceFlatness` | Face Flatness | `Range(0,1)` | `1` | 0 は法線による伝達、1 は SDF だけ |
| `_FaceSDFBlendNormalMin` | Face SDF Blend Normal Min | `Range(-1.5,1)` | `-1` | 法線と頭 up 軸の内積がこの値以下で顔の SDF の影響がゼロ。顎下や首など下向きの面で SDF をフェードアウトさせる。頭ボーンに追従するので俯いても顎裏の判定がずれない |
| `_FaceSDFBlendNormalMax` | Face SDF Blend Normal Max | `Range(-1,1.5)` | `0` | 法線と頭 up 軸の内積がこの値以上で顔の SDF の影響が 100%。Min と Max の間は滑らかにフェード |
| `_FaceSDFElevationMin` | Face SDF Elevation Fade Min | `Range(0,1.5)` | `1.0` | 光の仰角（\|頭 up・ライト\|）がこの値を超えると SDF が法線の陰影へ戻り始める。SDF は水平スイープなので仰角を知らず、トップライトで俯くと顔だけ明るいまま首が暗くなる。既定は OFF（1.0 以上）: 法線に戻すと鼻や唇の凹凸が出るので、顔を平らに保ちたいなら Face Shadow Tone を使う |
| `_FaceSDFElevationMax` | Face SDF Elevation Fade Max | `Range(0,1.5)` | `1.5` | 光の仰角がこの値で SDF が完全に法線の陰影へ戻る（首と同じ伝達関数なので継ぎ目が合う） |
| `_FaceShadowToneMode` | Face Shadow Tone | `Float` | `0` | 顔に落ちるシャドウマップの影（鼻下・唇・前髪）を画面固定の網点に置き換える。遮蔽量が点の密度になる。Off = PCF の値そのまま / Dots = Bayer 4×4 の網点 / Grain = ブルーノイズの粒。漫画のトーンのように画面に固定される。TAA では時間方向に均されて滑らかな半調に戻る |
| `_FaceShadowToneScale` | Face Shadow Tone Scale (px) | `Range(1,8)` | `3` | 網点 1 セルの画面ピクセル数。1 で画素単位、3〜4 で印刷のトーンらしく見える |
| `_FaceShadowToneStrength` | Face Shadow Tone Strength | `Range(0,1)` | `0.6` | 影ドットの濃さ。1 で影色まで落ち、0.5 なら影色と明色の中間 |
| `_FaceShadowToneThreshold` | Face Shadow Tone Threshold | `Range(0,0.9)` | `0.2` | この値以下の遮蔽は密度に写す前に切り捨てる。薄い半影には点が出ず、影の芯だけがトーンになる |
| `_FaceUseObjectAxis` | Face Use Object Axis | `Float` | `1` | 頭ボーンの向きを供給するものが無いときの代替 |

## ライト（Lighting）

### 環境光（Environment） ／ アンビエント

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_AmbientIntensity` | Ambient Intensity | `Range(0,2)` | `0.5` | 上げると影も一緒に持ち上がります。影が浅いときはまずここを下げること |
| `_AmbientFlatten` | Ambient Flatten | `Range(0,1)` | `0.4` | 参照する向きを真上へ寄せます。間接光の方向性を潰すほどセル塗りの平面感が保たれます |
| `_AOMultiBounce` | AO Multi Bounce | `Range(0,1)` | `1` | 遮蔽を灰色へ落とさずアルベドの色で染めます。暗部が色を保ちます |
| `_ShadowAmbientTint` | Shadow Ambient Tint | `Color` | `(1,1,1,1)` | — |
| `_ShadowAmbientIntensity` | Shadow Ambient Intensity | `Range(0,2)` | `1` | 影の中に届く環境光。下げると影が濃くなります |

### 環境光（Environment） ／ 環境反射（Reflection Probe）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_EnvSpecIntensity` | Env Spec Intensity | `Range(0,2)` | `0.35` | リフレクションプローブをどれだけ足すか。拡散はここで実際に足した量だけ縮みます（理論値ではなく） |
| `_EnvSpecFlatten` | Env Spec Flatten | `Range(0,1)` | `0.1` | 参照する mip を粗い側へ寄せます。素材の粗さを変えずに映り込みだけ鈍らせます |

### 光源方向の上書き（Light Direction Override）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_LightOverrideOn` | Light Override On | `Float` | `0` | — |
| `_LightOverrideYaw` | Light Override Yaw (deg) | `Range(-180,180)` | `0` | — |
| `_LightOverridePitch` | Light Override Pitch (deg) | `Range(-89,89)` | `30` | — |
| `_LightOverrideSpecular` | Light Override Specular | `Float` | `1` | — |

### フィルライト（照り返し）（Fill Light (Bounce)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FillColor` | Fill Color (HDR) | `Color` | `(0.4, 0.45, 0.6, 1)` | 照り返しの色（床からの暖色、空からの寒色など） |
| `_FillIntensity` | Fill Intensity | `Range(0,2)` | `0` | 陰側に注ぐ方向性のあるバウンス光（床の照り返しが典型）。メインライトの明るさから独立。0 で OFF |
| `_FillPitch` | Fill Pitch | `Range(-90,90)` | `-60` | -90 で床から真上へ |
| `_FillYaw` | Fill Yaw | `Range(-180,180)` | `0` | — |
| `_FillShadeOnly` | Fill Shade Only | `Range(0,1)` | `1` | 1 で主光の陰側に限定します（照っている側まで足すと白飛び方向にしか働きません） |

### ライト色の整形（Light Conditioning）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_LightColorInfluence` | Light Color Influence | `Range(0,1)` | `1` | 1 = ライト色をそのまま使います。0 = **同じ明るさの白色光として扱い**、濃い赤のスポットでも肌が真っ赤に染まりません |
| `_LightSaturationLimit` | Light Saturation Limit | `Range(0,1)` | `1` | 色相は保ったままライトの彩度に上限を掛けます（Influence の穏やかな版） |
| `_LightMinBrightness` | Light Min Brightness | `Range(0,1)` | `0` | ライトの明るさの下限。暗転寄りの演出でもキャラが見える状態を保ちます。0 で OFF |

### 白飛び防止（Anti-Blowout）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DiffuseLightLimit` | Diffuse Light Limit (0 = Off) | `Range(0,5)` | `1.2` | 1 灯あたりの拡散光の輝度上限。**拡散と透過にだけ**掛かります（鏡面は強い光ほど鋭く光るのが正しいので対象外）。NdotL の階調は残るので、上限に当たった面がのっぺり潰れません |
| `_AdditionalLightBlendMode` | Additional Light Blend Mode | `Float` | `0` | Add: 物理的 ── 何灯も重なると白へ飛びます。Max: 最も強い 1 灯だけが効くので**彩度が残ります**（ライトの多いステージ向けのアニメ的な嘘） |
| `_AlbedoBrightnessLimit` | Albedo Brightness Limit (1 = Off) | `Range(0.5,1)` | `1` | アルベドの最大成分の上限。1.0 近い白い衣装は 1 灯で飛びます。0.85〜0.9 で余白ができます。色相・彩度は変わりません |
| `_AdditionalLightTotalLimit` | Additional Light Total Limit (0 = Off) | `Range(0,5)` | `0` | 追加光ぜんぶの合計の輝度上限（Add / Max 合成の後） |
| `_SpecularLightLimit` | Specular Light Limit (0 = Off) | `Range(0,10)` | `4` | 鏡面・sheen・クリアコートに使う 1 灯あたりの光の輝度上限。Diffuse Light Limit より十分高くしないとハイライトが平たくなります |
| `_OutputLuminanceLimit` | Output Luminance Limit (0 = Off) | `Range(0,5)` | `0` | 直接光＋間接光への最後の保険。発光は対象外なので Bloom は従来どおり効きます |

## スペキュラ（Specular）

### スペキュラ（Specular）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_NormalCavity` | Normal Cavity | `Range(0,2)` | `0` | ノーマルマップの斜面で鏡面・sheen・クリアコート・映り込みを落とします。Specular Normal Flatten と組で使います ── 均した凹凸はハイライトを割らなくなり、陰としてだけハイライトの中に残ります |
| `_Smoothness` | Smoothness | `Range(0,1)` | `0.25` | **ツヤのダイヤル。**鏡面ローブの幅で、低いと広くうっすら・高いと締まった光沢になります。Base タブ > Mask Map > Smoothness Scale と同一プロパティです（A チャンネルの倍率） |
| `_SpecularIntensity` | Specular Intensity | `Range(0,4)` | `0` | 強さだけを変えます ── ハイライトの締まり（ツヤ）は上の Smoothness 側です。髪と布はここを通りません（それぞれ自前の強度を持っています） |
| `_SpecEnergyConservation` | Spec Energy Conservation | `Range(0,1)` | `0` | 鏡面が反射した割合（Fresnel × Specular Intensity、光の当たる面だけ）だけ拡散を縮めます。縁で拡散＋鏡面が入射光を超えないようにする保存則。0 で従来どおり鏡面を上乗せするだけ |
| `_SpecularTint` | Specular Tint | `Color` | `(1,1,1,1)` | — |
| `_SpecularTintStrength` | Specular Tint Strength | `Range(0,1)` | `0` | — |
| `_EnergyCompensation` | Energy Compensation | `Range(0,1)` | `1` | 粗い金属で単散乱 GGX が失うエネルギーを戻します。1 のとき完全反射体は入射をちょうど全部返します（白炉試験） |
| `_SpecularNormalFlatten` | Specular Normal Flatten | `Range(0,1)` | `0` | 鏡面・映り込み・MatCap・グリッターが見る法線をメッシュの法線へ寄せます。浅い凹凸から先にハイライトを割らなくなり、深いしわは残ります |

### Metal Override（金属部の上書き）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_MetalSpecularBoost` | Metal Specular Boost | `Range(0,4)` | `1` | 金属部（Mask Map R × Metallic）だけ Specular Intensity に掛かる倍率。1 で従来どおり。肌のハイライトを絞っても金具を殺さない（逆も）ための分離です |
| `_MetalEnvBoost` | Metal Env Boost | `Range(0,4)` | `1` | 金属部だけ Env Specular Intensity に掛かる倍率。金属の見た目はほぼ映り込みで決まるので、暗いステージで金具が死ぬときに上げます。クリアコート層には掛かりません |
| `_MetalDiffuseRetain` | Metal Diffuse Retain | `Range(0,1)` | `0` | 金属部に拡散の陰影を一部残します。物理では金属の拡散は 0 なので、映り込みの弱いステージでは金属が沈みます。トゥーンでは少し残す方が読めます。0 = 物理どおり |

### スペキュラ（Specular） ／ Secondary Lobe（マット）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SecSpecularColor` | Sec Specular Color (HDR) | `Color` | `(1,1,1,1)` | — |
| `_SecSpecularIntensity` | Sec Specular Intensity | `Range(0,2)` | `0` | シャープな芯の下に敷く広いマットなにじみ。肌やシルクが「点」でなく「面」で光るようになります。0 で OFF（分岐ごとスキップ） |
| `_SecSmoothness` | Sec Smoothness | `Range(0.01,1)` | `0.2` | 主ローブの Smoothness よりだいぶ低くしておくのが定石です |

### スペキュラ（Specular） ／ 影の中・アンチエイリアス

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SpecShadowFloor` | Spec Shadow Floor | `Range(0,1)` | `0.1` | 影側にどれだけ鏡面を残すか |
| `_SpecAAVariance` | Spec AA Variance | `Range(0,1)` | `0.15` | 法線の画面上のばらつきぶん、ローブを広げます |
| `_SpecAAThreshold` | Spec AA Threshold | `Range(0,1)` | `0.2` | — |

### Sheen（布の光沢）（Sheen (Cloth)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SheenColor` | Sheen Color | `Color` | `(1,1,1,1)` | ベルベットやサテンの縁の光沢。Charlie 分布で、GGX の 2 本目ではありません |
| `_SheenRoughness` | Sheen Roughness | `Range(0.02,1)` | `0.3` | 下地の粗さとは独立です。下げるほど縁が細くなります |
| `_SheenIntensity` | Sheen Intensity | `Range(0,4)` | `0.6` | — |
| `_SheenEnergyConservation` | Sheen Energy Conservation | `Range(0,1)` | `0` | sheen の指向性アルベドぶん下地を縮めてから足します。0 は足すだけなので、縁で入射より多く返ることがあります |
| `_SheenMetalTint` | Sheen Metal Tint | `Range(0,1)` | `0` | 金属部の sheen をアルベド（金属の反射色）で染めます（ラメ・金糸）。0 で白のまま |
| `_ClothAnisotropy` | Cloth Anisotropy | `Range(0,0.9)` | `0` | 織りの方向へ光沢を伸ばします |
| `_ClothTangentSwap` | Cloth Tangent Swap | `Float` | `0` | 光沢が織りと直交して出るときに切り替えます |
| `_AnisotropyMapOn` ⚡ | Anisotropy Map On | `Float` | `0` | — |
| `_AnisotropyMap` | Anisotropy Map (RG=dir B=strength) | `2D` | `"white" {}` | — |
| `_SheenNormalFlatten` | Sheen Normal Flatten | `Range(0,1)` | `0` | sheen が見る法線をメッシュの法線へ寄せます。浅い凹凸から先に消えます |
| `_SheenDetailNormal` | Sheen Detail Normal | `Range(0,1)` | `1` | sheen が Detail Normal をどれだけ見るか |

### 異方性ハイライト（髪）（Anisotropic (Hair)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairTangentSwap` | Hair Tangent Swap | `Float` | `1` | ハイライトが毛の流れと直交して出るときに切り替えます |
| `_HairAnisoGGXOn` | Hair Aniso GGX On (off = Kajiya-Kay) | `Float` | `0` | — |
| `_HairAnisotropy` | Hair Anisotropy | `Range(-1,1)` | `0.8` | — |

### 異方性ハイライト（髪）（Anisotropic (Hair)） ／ 流れ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairShiftMap` | Hair Shift Map (R) | `2D` | `"gray" {}` | 2 本のバンドを崩します。R を -0.5〜0.5 として読み 0.3 倍するので、シフトを置き換えるのではなく揺らします |
| `_HairFlowMap` | Hair Flow Map (RG=dir B=conf) | `2D` | `"black" {}` | メッシュの接線を上書きします。倍角エンコード（R=cos2θ, G=sin2θ）なのでUV がミラーでも同じ向きになります。B は信頼度 |
| `_HairFlowStrength` | Hair Flow Strength | `Range(0,8)` | `0` | 0 でメッシュの接線そのまま。UV ミラーで天使の輪が割れるときに上げます |

### 異方性ハイライト（髪）（Anisotropic (Hair)） ／ ハイライト

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairSpecColor1` | Hair Spec Color 1 | `Color` | `(1,1,1,1)` | 内側の細いバンド |
| `_HairShift1` | Hair Shift 1 | `Range(-1,1)` | `0.08` | バンドを法線方向へずらします。負で根元側へ動きます |
| `_HairSmoothness1` | Hair Smoothness 1 | `Range(0,1)` | `0.7` | バンドの幅。Kajiya-Kay では指数 2^(10x+1) になります |
| `_HairSpecColor2` | Hair Spec Color 2 | `Color` | `(0.75,0.85,0.8,1)` | 外側の広いバンド。束感が乗るのはこちらだけです |
| `_HairShift2` | Hair Shift 2 | `Range(-1,1)` | `-0.12` | — |
| `_HairSmoothness2` | Hair Smoothness 2 | `Range(0,1)` | `0.35` | — |
| `_HairSpecIntensity` | Hair Spec Intensity | `Range(0,4)` | `1.0` | 2 本まとめて倍率を掛けます。髪は共通の Specular Intensity を通りません |
| `_HairStrandScale` | Hair Strand Scale | `Range(0,200)` | `50` | 束の粒の U 方向の細かさ（3 オクターブのサイン）。画面上で 1 周期が 1 画素を切ると自動で効かなくなります |
| `_HairStrandSparkle` | Hair Strand Sparkle | `Range(0,1)` | `0` | 粒が副バンドをどれだけ削るか。主バンドには掛かりません（細い芯なので割ると消えるため） |

### MatCap（MatCap）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_MatCapIntensity` | MatCap Intensity | `Range(0,5)` | `0` | 0 で分岐ごと飛びます。加算のアクセント専用（乗算は持ちません ── 環境光の主経路であるプローブ + SH を上書きできてしまうため）。テクスチャ未割り当てのまま上げておくと**加算は 0 なのにコストだけ払います** |
| `_MatCapTex` | MatCap Tex (RGB) | `2D` | `"black" {}` | — |
| `_MatCapColor` | MatCap Color (HDR) | `Color` | `(1,1,1,1)` | — |
| `_MatCapLightAlign` | MatCap Light Align | `Range(0,1)` | `0` | 参照の向きを画面内の光の向きへ回します。ハイライトがカメラに貼り付いて見える弱点が減ります |

## 質感（Effects）

### コートとグリッター（Coat and Glitter） ／ クリアコート

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ClearcoatStrength` | Clearcoat Strength | `Range(0,1)` | `0` | 別の粗さを持つ薄い層を 1 枚重ねます。IOR は 1.5 固定（f0 = 0.04）。漆・真珠・濡れた唇。0 で機能ごとコンパイルから外れます（キーワード _CLEARCOAT_ON がこの値に追従） |
| `_ClearcoatSmoothness` | Clearcoat Smoothness | `Range(0,1)` | `0.9` | — |

### コートとグリッター（Coat and Glitter） ／ イリデッセンス

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_IridescenceIntensity` | Iridescence Intensity | `Range(0,1)` | `0` | 見る角度で色が回る薄膜のティント。0 で白（色が付かない） |
| `_IridescenceThickness` | Iridescence Thickness | `Range(0,4)` | `1` | 面が傾くにつれ色相がどれだけ速く回るか |
| `_IridescenceShift` | Iridescence Shift | `Range(0,1)` | `0` | 開始の色相をずらします |

### コートとグリッター（Coat and Glitter） ／ グリッター

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_GlitterMask` | Glitter Mask (R) | `2D` | `"white" {}` | — |
| `_GlitterColor` | Glitter Color (HDR) | `Color` | `(2,2,2,1)` | — |
| `_GlitterAlbedoTint` | Glitter Albedo Tint | `Range(0,1)` | `0` | 粒の色にアルベドを掛けます。1 で生地と同じ色のラメ（Sparkle と共通） |
| `_GlitterRim` | Glitter Rim | `Range(0,4)` | `1` | リムの帯をどれだけスパンコールに分解するか。1 で帯が円盤の集まりに、2 以上で明るく、0 で滑らかな帯 |
| `_GlitterSpecular` | Glitter Specular | `Range(0,4)` | `1` | スペキュラ（GGX・sheen・髪・映り込み）をどれだけスパンコールに分解するか。Glitter Rim と同じ尺度 |
| `_GlitterIntensity` | Glitter Intensity | `Range(0,50)` | `0` | 0 で機能ごとコンパイルから外れます（キーワード _GLITTER_ON がこの値に追従）。粒のきらめきの強さです |
| `_GlitterScale` | Glitter Scale (Scale) | `Range(10,1000)` | `100` | UV あたりのセル数。上げるほど細かく密に |
| `_GlitterSize` | Glitter Size | `Range(0.0005,0.05)` | `0.005` | セル内の粒の半径 |
| `_GlitterTilt` | Glitter Tilt | `Range(0,2)` | `0.8` | 粒ごとの法線の傾け。強いほど色々な角度でフラッシュします |
| `_GlitterSparsity` | Glitter Sparsity | `Range(0,1)` | `0.5` | 粒をランダムに間引きます |
| `_GlitterIridescence` | Glitter Iridescence | `Range(0,1)` | `0.5` | 粒ごとの虹色（ホログラムスパンコール） |
| `_GlitterIridescenceShift` | Glitter Iridescence Shift | `Range(0,1)` | `0.5` | — |
| `_GlitterBaseReflection` | Glitter Base Reflection | `Range(0,0.5)` | `0.05` | 光っていない粒にも残す薄い反射。生地がラメ物だと分かる下地です |

### シアー生地 (ストッキング)（Sheer Fabric）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_StockingIntensity` | Stocking Intensity | `Range(0,1)` | `0` | 0 で分岐ごと飛びます。布を別メッシュで重ねずに、視角依存の不透明度で肌の上へ乗せます |
| `_StockingColor` | Stocking Color | `Color` | `(0.76, 0.65, 0.55, 1)` | 正面では肌に乗算され、シルエットでは布そのものの色になります。同じ色が「透けた布」と「布地」の両方に見えます |
| `_StockingMask` | Stocking Mask (R) | `2D` | `"white" {}` | 布のある場所。太ももの境目はここに描きます |
| `_StockingFrontOpacity` | Stocking Front Opacity | `Range(0,1)` | `0.25` | 正面を向いた面でどれだけ肌が透けるか |
| `_StockingPower` | Stocking Power | `Range(0.5,8)` | `1.5` | — |

### SSS（表面下散乱）（SSS (Subsurface)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SubsurfaceColor` | Subsurface Color | `Color` | `(1.0, 0.55, 0.45, 1)` | 境界の影側へにじむ色 |
| `_SubsurfaceStrength` | Subsurface Strength | `Range(0,2)` | `0` | — |

### 透過（Transmission）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_TransmissionColor` | Transmission Color | `Color` | `(1.0, 0.35, 0.25, 1)` | アルベドに乗算されるので、置き換えではなく色付けになります |
| `_TransmissionPower` | Transmission Power | `Range(1,16)` | `4` | 光が抜けてくる向きにどれだけ絞るか。上げるほどほぼ光源を覗き込む角度でしか見えなくなります |
| `_TransmissionStrength` | Transmission Strength | `Range(0,4)` | `0` | — |
| `_TransmissionDistortion` | Transmission Distortion | `Range(0,1)` | `0.2` | 抜ける光を SSS の向きへ曲げます。ライトベクトルを打ち消したときはNaN にせず生のライト方向へ落とします |
| `_SSSMap` | SSS Map (RGB=dir A=thickness) | `2D` | `"bump" {}` | 焼いた散乱方向。無い場合はシェーディング法線を使います |
| `_SSSMapStrength` | SSS Map Strength | `Range(0,1)` | `0` | — |

### リムライト / Peach Fuzz（Rim / Peach Fuzz）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_RimColor` | Rim Color | `Color` | `(1.0, 0.75, 0.5, 1)` | — |
| `_RimIntensity` | Rim Intensity | `Range(0,8)` | `1.5` | ライトのエネルギーに比例し（ステージ照明の色が縁に乗る）、光が回り込んだ側だけに出ます |
| `_RimFresnelThickness` | Rim Fresnel Thickness | `Range(0,1)` | `0.3` | 0 で極細（指数 12）、1 で極太（0.5）。Doll と同じ写像です |
| `_RimReceiveShadow` | Rim Receive Shadow | `Range(0,1)` | `1` | 落ち影の中でリムを消します。見るのは落ち影だけで NdotL の陰は含みません（リムは「そこに光が届いているか」の話なので） |
| `_RimDetailNormal` | Rim Detail Normal | `Range(0,1)` | `1` | リムが Detail Normal をどれだけ見るか。0 で織り目に縁が立たなくなります |
| `_RimNormalFlatten` | Rim Normal Flatten | `Range(0,1)` | `0` | リムが見る法線をメッシュの法線へ寄せます。浅い凹凸から先に縁が消え、傾きの大きい深いしわの縁は残ります。距離やミップに依りません |

### リムライト / Peach Fuzz（Rim / Peach Fuzz） ／ Peach Fuzz（縁の柔らかい光沢）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FuzzColor` | Fuzz Color (HDR) | `Color` | `(1.0, 0.95, 0.9, 1.0)` | — |
| `_FuzzIntensity` | Fuzz Intensity | `Range(0,5)` | `0` | 0 で機能ごとスキップします（一様分岐・バリアント非増） |
| `_FuzzPower` | Fuzz Power | `Range(0.1,10)` | `4` | 小さいほど帯が広く、光の当たる側まで回り込みます。大きいほどシルエットに張り付いた細い線になります |

### ベイクマップ（Bent / Cavity / 曲率）（Baked Maps） ／ ベント法線マップ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_BentNormalOn` | Bent Normal On | `Float` | `0` | 間接拡散の向きを、遮蔽されていない方向へ寄せます |
| `_BentNormalMap` | Bent Normal Map | `2D` | `"bump" {}` | — |

## 演出（FX）

### ディゾルブ / 暗転（Dissolve / Black Out）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DissolveAmount` | Dissolve Amount | `Range(0,1)` | `0` | 0 で全部出ています（分岐ごと飛び、キーワードレスなのでバリアントも増えません）。1 で全部消えます。両端は保証されています（縁の幅ぶん閾値を広げてあるので消え残りません） |
| `_DissolveInvert` | Dissolve Invert | `Float` | `0` | 判定の符号を反転します。反対の端から消えます |
| `_DissolveType` | Dissolve Type | `Float` | `1` | 0 = 使わない（ノイズだけ）/ 1 = ワールド Y / 2 = ローカル Y。ローカルはキャラが動いても一緒に動きます |
| `_DissolveStartY` | Dissolve Start Y | `Float` | `0` | 勾配が 0 になる高さ。End と同じ値でも安全です（「一気に消す」の意味になります） |
| `_DissolveEndY` | Dissolve End Y | `Float` | `2` | — |

### ディゾルブ / 暗転（Dissolve / Black Out） ／ ノイズ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DissolveTex` | Dissolve Tex (R) | `2D` | `"white" {}` | UV だけで引きます（三平面投影はしません）。キャラの UV が整っている前提です |
| `_DissolveNoiseScale` | Dissolve Noise Scale | `Float` | `1` | — |
| `_DissolveNoiseStrength` | Dissolve Noise Strength | `Range(0,1)` | `0.5` | 高さの境界をノイズがどれだけ崩すか。0 で水平な直線になります |

### ディゾルブ / 暗転（Dissolve / Black Out） ／ 縁

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DissolveEdgeColor` | Dissolve Edge Color (HDR) | `Color` | `(1, 0.6, 0, 1)` | 縁の帯の内側に発光として足されます |
| `_DissolveEdgeColor2` | Dissolve Edge Color 2 (HDR) | `Color` | `(1, 0, 0, 1)` | 縁の帯ぜんたいでアルベドを置き換えます（焦げの表現） |
| `_DissolveEdgeWidth` | Dissolve Edge Width | `Range(0.001,0.5)` | `0.05` | — |
| `_DissolveEdgeStep` | Dissolve Edge Step (toon) | `Float` | `0` | 縁を 2 段に量子化し、発光もグラデーションでなく硬く切ります |

### ディゾルブ / 暗転（Dissolve / Black Out） ／ 暗転エフェクト

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_BlackOut` | Black Out | `Range(0,1)` | `0` | 最終色を黒へ落とします（**発光も含めて**。輪郭線も一緒に沈みます）。アルファは触らないので、消えるのではなく黒く沈みます（消したいときはディゾルブ） |

## 詳細（Advanced）

### デバッグ表示（Debug View）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DebugMode` | Debug Mode | `Float` | `0` | — |

### レンダーステート（Render State）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_Cull` | Cull | `Float` | `2` | 全パスに掛かるので、影のシルエットが本体と一致します |
| `_ZTest` | Z Test | `Float` | `4` | Never にすると Unity は何も言わずに**完全に消えます**。従うのは本体のパスだけで、深度と法線は LEqual のままです |
| `_OffsetFactor` | Offset Factor | `Float` | `0` | ポリゴン深度オフセット（傾き項）。負でカメラ側に寄ります ── 眉・睫毛を顔の上に浮かせる用途。本体・前髪透過・深度・法線の各パスに掛かります（影には掛かりません） |
| `_OffsetUnits` | Offset Units | `Float` | `0` | 深度オフセットの定数項（最小深度刻み単位） |
| `_ShadowCasterOff` | Shadow Caster Off | `Float` | `0` | このマテリアルが**影を落とすのをやめます**（落ちる影が消えるだけで、受ける影は残ります）。顔に自己影を落とす瞳・睫毛には有効ですが、**髪に使うと顔にも体にも髪の影が落ちなくなります** |

### ステンシル（Stencil）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_StencilRef` | Stencil Ref | `Range(0,15)` | `0` | Replace なら書き込む値、Equal なら比べる値 |
| `_StencilComp` | Stencil Comp | `Float` | `8` | Never にすると Unity は何も言わずに 1 画素も描きません |
| `_StencilPass` | Stencil Pass | `Float` | `0` | Replace でも Write Mask が 0 だと**何も書きません**。それを当てにしている材質が黙って成立しなくなります |
| `_StencilReadMask` | Stencil Read Mask | `Range(0,255)` | `15` | 比較で見るビット |
| `_StencilWriteMask` | Stencil Write Mask | `Range(0,255)` | `15` | 書き込んでよいビット |

### ステンシル（Stencil） ／ 前髪透過

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairSeeThroughAlpha` | Hair See Through Alpha | `Range(0,1)` | `0.6` | 眉・目の上にかかる髪の不透明度 |

## ?

### (節なし)

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_Cutoff` | Cutoff | `Range(0,1)` | `0.5` | このアルファ未満の画素を捨てます |
| `_ShadowHueShift` | Shadow Hue Shift | `Range(-0.2,0.2)` | `-0.03` | 影の色相を回します。Saturation が 1 のまま両方とも既定ならHSV 変換ごと飛ぶので、触らなければコストはゼロです |
| `_ShadowSaturation` | Shadow Saturation | `Range(0,3)` | `1.3` | 1 より上げると影が濁らず鮮やかに残ります（アニメ塗りの定番） |
| `_ShadowValue` | Shadow Value | `Range(0,1)` | `0.75` | 下げると影が濃くなります。「ライト」タブの環境光も影を持ち上げます |
| `_AddLightShadowColor` | Add Light Shadow Color | `Range(0,1)` | `0` | 追加光が当たっていない側に足す量（影色 × ライトの色）。0 = 当たった所とリムにだけ寄与する（物理どおり。赤い逆光は縁に留まる）。1 = ライトの色でキャラ全体を染める（色ウォッシュ用） |
| `_ShadowTint` | Shadow Tint (multiply) | `Color` | `(1,1,1,1)` | HSV の後に影へ乗算されます |
| `_ShadowColor` | Shadow Color (mix toward) | `Color` | `(0.50, 0.32, 0.62, 1)` | 影を寄せたい色相。明るさは正規化して落とすので**色相だけ**が効きます（暗い色を選んでも暗くはなりません） |
| `_ShadowColorMix` | Shadow Color Mix | `Range(0,1)` | `0` | 0 でこの処理ごと飛びます |
| `_SrcBlend` | Src Blend | `Float` | `1` | — |
| `_DstBlend` | Dst Blend | `Float` | `0` | — |
| `_ZWrite` | Z Write | `Float` | `1` | 半透明では通常 Off のままにします |

---

説明のあるもの 170 / 229。**残り 59 個は tooltip が書かれていない** ── `ToonPBRShaderGUI.cs` に足すとここにも出ます。

# プロパティ一覧（自動生成）

**この文書は生成物です。手で書き換えても次の生成で消えます。**
説明を足したいときは `ToonPBRShaderGUI.cs` の tooltip を書いてください ──
インスペクタと文書の両方に同時に効きます。

```bash
python gen_properties.py --write
```

シェーダー: `Idol.shader` / プロパティ 209 個

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
| `_NormalMapOn` | Use Normal Map | `Float` | `0` | — |
| `_BumpMap` | Normal Map | `2D` | `"bump" {}` | — |
| `_BumpScale` | Normal Scale | `Range(0,2)` | `1` | 接空間ノーマルの強さ |

### ベース（Base） ／ ディテールマップ（タトゥーやチーク等）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DetailOn` | Use Detail Map | `Float` | `0` | 独立したタイリングを持つ重ねレイヤー ── タトゥー・チークの印刷・布地の織り目など。RGB = 色 / A = 合成率 |
| `_DetailMap` | Detail Map (RGB=color A=blend) | `2D` | `"black" {}` | — |
| `_DetailColor` | Detail Color | `Color` | `(1,1,1,1)` | — |
| `_DetailNormalMap` | Detail Normal Map | `2D` | `"bump" {}` | — |
| `_DetailNormalScale` | Detail Normal Scale | `Range(0,2)` | `1` | ベースのノーマルの上に whiteout 合成されます |

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
| `_Metallic` | Metallic Scale | `Range(0,1)` | `0` | R チャンネルを倍率で調整 |
| `_OcclusionStrength` | Occlusion Strength | `Range(0,1)` | `1` | G が間接光をどれだけ落とすか |
| `_DirectOcclusion` | Apply AO to Direct Light | `Range(0,1)` | `0.3` | 物理的には AO は間接光だけのもの。絵として要るときだけ上げる |
| `_MicroShadow` | Micro Shadow | `Range(0,1)` | `1` | 斜めから当たる直接光を遮蔽量で削る |

### NPR マップ（NPR Map）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_NPRMapOn` | Use NPR Map | `Float` | `0` | — |
| `_NPRMap` | NPR Map | `2D` | `"white" {}` | — |
| `_NPRShadowOffsetStrength` | Shadow Offset Strength | `Range(0,1)` | `0.4` | G が影の境界をどれだけずらすか |

### 発光（Emission）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_EmissionOn` | Enable Emission | `Float` | `0` | — |
| `_EmissionMap` | Emission Map | `2D` | `"white" {}` | — |
| `_EmissionColor` | Emission Color | `Color` | `(0,0,0,1)` | HDR |

### アウトライン（Outline）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_OutlineOn` ⚡ | Enable Outline | `Float` | `0` | 背面法線押し出しの輪郭を別 LightMode で描きます。既定は OFF ── 参考にしている絵には輪郭線が無く、逆光リムと明度差でシルエットを抜いています |
| `_UseSmoothNormal` | Use Baked Smooth Normal | `Float` | `0` | SmoothNormalBaker をメッシュに通してあることが前提 |
| `_UseVertexWidth` | Width Mask from vertex color A | `Float` | `0` | 頂点ごとに線を細くできます（睫毛や細いベルトなど） |
| `_OutlineColor` | Color | `Color` | `(0.2,0.15,0.18,1)` | — |
| `_OutlineAlbedoBlend` | Blend with Albedo | `Range(0,1)` | `0.5` | 1 で線を単色でなく表面の色で染めます |
| `_OutlineAlbedoDarken` | Albedo Darken | `Range(0,1)` | `0.45` | — |
| `_OutlineWidth` | Width | `Range(0,10)` | `0.8` | — |
| `_OutlineZOffset` | Z Offset | `Range(0,1)` | `0` | 線をカメラから遠ざけて、本体を突き抜けないようにします |
| `_OutlineMaxDistance` | Fade Distance | `Range(1,100)` | `25` | 画面上での太りを止める距離。引きの画で効きます |

## 陰・影（Shading）

### 拡散の伝達関数（Diffuse Transfer）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ShadowThreshold` | Shadow Threshold | `Range(0,1)` | `0.5` | 明暗の境界の位置 |
| `_ShadowSoftness` | Base Softness | `Range(0.001,0.5)` | `0.12` | 曲率で広げる前の、境界の基本の幅 |
| `_CurvatureSoftness` | Curvature Influence | `Range(0,4)` | `0` | 曲がった面ほど境界を広げる度合い。幅 = Base Softness × (1 + 曲率 × Influence)。曲率は焼いた Curvature Map（質感タブ > ベイクしたマップ）から取ります ── 無ければ何も起きません |

### 拡散の伝達関数（Diffuse Transfer） ／ シェーディング法線

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ShadeNormalMap` | Shade Normal Map | `2D` | `"bump" {}` | 拡散の伝達だけに使う、なめらかな法線 |
| `_ShadeNormalStrength` | Shade Normal Strength | `Range(0,1)` | `0` | — |
| `_DiffuseWrap` | Diffuse Wrap | `Range(0,1)` | `0.25` | 光を明暗境界の先まで回り込ませます。エネルギー保存形なので伝達の上限が 1/(1+wrap) まで下がります（上げるほど天井が下がる） |

### 拡散の伝達関数（Diffuse Transfer） ／ リアルタイム影の受け

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ReceiveShadowStrength` | Receive Realtime Shadow | `Range(0,1)` | `0.7` | 最後に一度だけ掛かります。HQ 影とマイクロシャドウがここに畳まれているので、下げるとまとめて薄くなります |
| `_ShadowAttenSoftness` | Realtime Shadow Softness | `Range(0.001,1)` | `0.35` | 遷移の幅。中心は「半分遮蔽」に固定なので、影の大きさは変わらず柔らかさだけが変わります |
| `_ShadowEdgeAA` | Edge Anti-Aliasing | `Range(0,2)` | `1` | 境界を 1 画素ぶん広げてジャギを隠します |

### HQ セルフシャドウ（HQ Self Shadow）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HQShadowOn` ⚡ | Enable HQ Self Shadow | `Float` | `0` | 主光源のみ。全機能の中でテクスチャフェッチが一番多い |
| `_HQShadowSoftness` | Penumbra (texels) | `Range(0,1)` | `0.3` | 単位はシャドウマップのテクセル。メートルではありません |
| `_ShadowPenumbraScale` | Penumbra Scale | `Range(0,1000)` | `200` | — |
| `_ReceiverNormalBias` | Receiver Normal Bias | `Range(0,4)` | `1` | — |
| `_ShadowContactHardening` | Contact Hardening (PCSS) | `Float` | `0` | 遮蔽物が近いところで半影を狭めます |

### 影の色 (HSV)（Shadow Color (HSV)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ShadowHueShift` | Hue Shift | `Range(-0.2,0.2)` | `-0.03` | 影の色相を回します。Saturation が 1 のまま両方とも既定ならHSV 変換ごと飛ぶので、触らなければコストはゼロです |
| `_ShadowSaturation` | Saturation Scale | `Range(0,3)` | `1.3` | 参考にしている絵では、影は暗くなるだけでなく彩度が上がります |
| `_ShadowValue` | Value Scale | `Range(0,1)` | `0.75` | 下げると影が濃くなります。「ライト」タブの環境光も影を持ち上げます |
| `_AddLightShadowColor` | Shadow Color from Add. Lights | `Range(0,1)` | `1` | 追加光源の影にどれだけ影色を掛けるか。点光源すべてに全量掛けると濁って見えがちです |
| `_ShadowTint` | Tint (multiply) | `Color` | `(1,1,1,1)` | HSV の後に影へ乗算されます |
| `_ShadowColor` | Shadow Hue (mix toward) | `Color` | `(0.50, 0.32, 0.62, 1)` | 影を寄せたい色相。明るさは正規化して落とすので**色相だけ**が効きます（暗い色を選んでも暗くはなりません） |
| `_ShadowColorMix` | Hue Mix | `Range(0,1)` | `0` | 0 でこの処理ごと飛びます |

### 影の色 (HSV)（Shadow Color (HSV)） ／ 落ち影の色

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_CastShadowColor` | Cast Shadow Color | `Color` | `(0.5, 0.45, 0.5, 1)` | シャドウマップ由来の落ち影（髪・手）だけに掛ける色。NdotL の陰は通常の影色のままです |
| `_CastShadowColorStrength` | Cast Shadow Color Strength | `Range(0,1)` | `0` | 色をどれだけ掛けるか。0 で落ち影も通常の影色。影の中の鏡面と環境光も同時に落とすので、落ち影がNdotL の陰とは別の出来事として見えます |

### Terminator（明暗境界）（Terminator）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_TerminatorColor` | Terminator Color | `Color` | `(1.0, 0.82, 0.72, 1)` | 明暗の境目に出る暖色の帯 |
| `_TerminatorStrength` | Strength | `Range(0,1)` | `0.35` | 肌だけでなく全部の質感に掛かります。境界の色を全身で揃えるのは意図的な様式化です |
| `_TerminatorSharpness` | Sharpness | `Range(0.1,8)` | `2.0` | 帯の芯からどれだけ速く落ちるか |
| `_TerminatorFadeStart` | Fade Start (m) | `Range(0,200)` | `20` | 帯が消え始める距離（メートル） |
| `_TerminatorFadeEnd` | Fade End (m) | `Range(0,200)` | `40` | — |

### ランプで上書き（Ramp Override）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_UseRampMap` | Use Ramp Map | `Float` | `0` | — |
| `_RampMap` | Ramp Map | `2D` | `"white" {}` | — |
| `_RampRowCount` | Ramp Row Count | `Float` | `8` | テクスチャに縦へ何本のランプを並べてあるか |
| `_RampIndexOverride` | Ramp Index Override (-1 = use NPR.a) | `Float` | `-1` | -1 で NPR マップの A から画素ごとに行を選びます |
| `_RampStrength` | Blend | `Range(0,1)` | `1` | 曲率駆動のステップとランプの間を混ぜます。ランプは必須ではありません |

### 顔（SDF）（Face (SDF)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FaceSDFMap` | Face SDF (16-bit R*256+G) | `2D` | `"white" {}` | 正面から 180 度スイープした境界の角度を R×256+G に詰めたもの。Baking タブで焼きます。**非圧縮・sRGB OFF 必須**（BC 圧縮は RG の連続性を壊します） |
| `_FaceSDFFlipU` | Flip SDF U | `Float` | `0` | 焼いたときの左右の取り決めに合わせます |
| `_FaceShadowOffset` | Shadow Offset | `Range(-0.5,0.5)` | `0` | 境界ぜんたいをずらします。正で顔がより回り込んでも明るいまま |
| `_FaceFlatness` | SDF Blend | `Range(0,1)` | `1` | 0 は法線による伝達、1 は SDF だけ |
| `_FaceSDFBlendNormalMin` | SDF Blend Normal Min | `Range(-1.5,1)` | `-1` | 顔の SDF の影響がゼロになるローカル Y 法線のしきい値。顎下や首など下向きの面で SDF をフェードアウトさせる |
| `_FaceSDFBlendNormalMax` | SDF Blend Normal Max | `Range(-1,1.5)` | `0` | 顔の SDF の影響が 100% になるローカル Y 法線のしきい値。Min と Max の間は滑らかにフェード |
| `_FaceUseObjectAxis` | Fallback to Object Axis | `Float` | `1` | 頭ボーンの向きを供給するものが無いときの代替 |

## ライト（Lighting）

### 環境光（Environment） ／ アンビエント

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_AmbientIntensity` | Ambient (SH) Intensity | `Range(0,2)` | `0.5` | 上げると影も一緒に持ち上がります。影が浅いときはまずここを下げること |
| `_AmbientFlatten` | Flatten | `Range(0,1)` | `0.4` | 参照する向きを真上へ寄せます。間接光の方向性を潰すほどセル塗りの平面感が保たれます |
| `_AOMultiBounce` | AO Multi Bounce | `Range(0,1)` | `1` | 遮蔽を灰色へ落とさずアルベドの色で染めます。暗部が色を保ちます |
| `_ShadowAmbientTint` | Tint in Shadow | `Color` | `(1,1,1,1)` | — |
| `_ShadowAmbientIntensity` | Intensity in Shadow | `Range(0,2)` | `1` | 影の中に届く環境光。下げると影が濃くなります |

### 環境光（Environment） ／ 環境反射（Reflection Probe）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_EnvSpecIntensity` | Env Specular Intensity | `Range(0,2)` | `0.35` | リフレクションプローブをどれだけ足すか。拡散はここで実際に足した量だけ縮みます（理論値ではなく） |
| `_EnvSpecFlatten` | Roughness Push | `Range(0,1)` | `0.1` | 参照する mip を粗い側へ寄せます。素材の粗さを変えずに映り込みだけ鈍らせます |

### 光源方向の上書き（Light Direction Override）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_LightOverrideOn` | Override Light Direction | `Float` | `0` | — |
| `_LightOverrideYaw` | Yaw (deg) | `Range(-180,180)` | `0` | — |
| `_LightOverridePitch` | Pitch (deg) | `Range(-89,89)` | `30` | — |
| `_LightOverrideSpecular` | Rotate Specular Too | `Float` | `1` | — |

### フィルライト（照り返し）（Fill Light (Bounce)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FillColor` | Fill Light Color (HDR) | `Color` | `(0.4, 0.45, 0.6, 1)` | 照り返しの色（床からの暖色、空からの寒色など） |
| `_FillIntensity` | Fill Light Intensity | `Range(0,2)` | `0` | 陰側に注ぐ方向性のあるバウンス光（床の照り返しが典型）。メインライトの明るさから独立。0 で OFF |
| `_FillPitch` | Fill Light Pitch | `Range(-90,90)` | `-60` | -90 で床から真上へ |
| `_FillYaw` | Fill Light Yaw | `Range(-180,180)` | `0` | — |
| `_FillShadeOnly` | Fill Shade Side Only | `Range(0,1)` | `1` | 1 で主光の陰側に限定します（照っている側まで足すと白飛び方向にしか働きません） |

### ライト色の整形（Light Conditioning）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_LightColorInfluence` | Light Color Influence | `Range(0,1)` | `1` | 1 = ライト色をそのまま使います。0 = **同じ明るさの白色光として扱い**、濃い赤のスポットでも肌が真っ赤に染まりません |
| `_LightSaturationLimit` | Light Saturation Limit | `Range(0,1)` | `1` | 色相は保ったままライトの彩度に上限を掛けます（Influence の穏やかな版） |
| `_LightMinBrightness` | Light Min Brightness | `Range(0,1)` | `0` | ライトの明るさの下限。暗転寄りの演出でもキャラが見える状態を保ちます。0 で OFF |

### 白飛び防止（Anti-Blowout）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DiffuseLightLimit` | Diffuse Light Limit (0 = Off) | `Range(0,5)` | `0` | 1 灯あたりの拡散光の輝度上限。**拡散と透過にだけ**掛かります（鏡面は強い光ほど鋭く光るのが正しいので対象外）。NdotL の階調は残るので、上限に当たった面がのっぺり潰れません |
| `_AdditionalLightBlendMode` | Additional Light Blend | `Float` | `0` | Add: 物理的 ── 何灯も重なると白へ飛びます。Max: 最も強い 1 灯だけが効くので**彩度が残ります**（ライトの多いステージ向けのアニメ的な嘘） |

## スペキュラ（Specular）

### スペキュラ（Specular）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_Smoothness` | Smoothness Scale | `Range(0,1)` | `0.25` | **ツヤのダイヤル。**鏡面ローブの幅で、低いと広くうっすら・高いと締まった光沢になります。Base タブ > Mask Map > Smoothness Scale と同一プロパティです（A チャンネルの倍率） |
| `_SpecularIntensity` | Specular Intensity | `Range(0,4)` | `0.2` | 強さだけを変えます ── ハイライトの締まり（ツヤ）は上の Smoothness 側です。髪と布はここを通りません（それぞれ自前の強度を持っています） |
| `_SpecEnergyConservation` | Energy Conservation | `Range(0,1)` | `0` | 鏡面が反射した割合（Fresnel × Specular Intensity、光の当たる面だけ）だけ拡散を縮めます。縁で拡散＋鏡面が入射光を超えないようにする保存則。0 で従来どおり鏡面を上乗せするだけ |
| `_SpecularTint` | Specular Tint | `Color` | `(1,1,1,1)` | — |
| `_SpecularTintStrength` | Tint Strength | `Range(0,1)` | `0` | — |
| `_EnergyCompensation` | Energy Compensation | `Range(0,1)` | `1` | 粗い金属で単散乱 GGX が失うエネルギーを戻します。1 のとき完全反射体は入射をちょうど全部返します（白炉試験） |

### スペキュラ（Specular） ／ Secondary Lobe（マット）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SecSpecularColor` | 2nd Lobe Color (HDR) | `Color` | `(1,1,1,1)` | — |
| `_SecSpecularIntensity` | 2nd Lobe Intensity | `Range(0,2)` | `0` | シャープな芯の下に敷く広いマットなにじみ。肌やシルクが「点」でなく「面」で光るようになります。0 で OFF（分岐ごとスキップ） |
| `_SecSmoothness` | 2nd Lobe Smoothness | `Range(0.01,1)` | `0.2` | 主ローブの Smoothness よりだいぶ低くしておくのが定石です |

### スペキュラ（Specular） ／ 影の中・アンチエイリアス

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SpecShadowFloor` | Specular in Shadow | `Range(0,1)` | `0.1` | 影側にどれだけ鏡面を残すか |
| `_SpecAAVariance` | Spec AA Variance | `Range(0,1)` | `0.15` | 法線の画面上のばらつきぶん、ローブを広げます |
| `_SpecAAThreshold` | Spec AA Threshold | `Range(0,1)` | `0.2` | — |

### Sheen（布の光沢）（Sheen (Cloth)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SheenColor` | Sheen Color | `Color` | `(1,1,1,1)` | ベルベットやサテンの縁の光沢。Charlie 分布で、GGX の 2 本目ではありません |
| `_SheenRoughness` | Sheen Roughness | `Range(0.02,1)` | `0.3` | 下地の粗さとは独立です。下げるほど縁が細くなります |
| `_SheenIntensity` | Intensity | `Range(0,4)` | `0.6` | — |
| `_SheenEnergyConservation` | Energy Conservation | `Range(0,1)` | `0` | sheen の指向性アルベドぶん下地を縮めてから足します。0 は足すだけなので、縁で入射より多く返ることがあります |
| `_ClothAnisotropy` | Anisotropy | `Range(0,0.9)` | `0` | 織りの方向へ光沢を伸ばします |
| `_ClothTangentSwap` | Use Bitangent as Weave Dir | `Float` | `0` | 光沢が織りと直交して出るときに切り替えます |

### 異方性ハイライト（髪）（Anisotropic (Hair)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairTangentSwap` | Use Bitangent as Strand Dir | `Float` | `1` | ハイライトが毛の流れと直交して出るときに切り替えます |
| `_HairAnisoGGXOn` | Use Anisotropic GGX (off = Kajiya-Kay) | `Float` | `0` | — |
| `_HairAnisotropy` | Anisotropy | `Range(-1,1)` | `0.8` | — |

### 異方性ハイライト（髪）（Anisotropic (Hair)） ／ 流れ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairShiftMap` | Shift Noise (R) | `2D` | `"gray" {}` | 2 本のバンドを崩します。R を -0.5〜0.5 として読み 0.3 倍するので、シフトを置き換えるのではなく揺らします |
| `_HairFlowMap` | Hair Flow (RG=dir B=conf) | `2D` | `"black" {}` | メッシュの接線を上書きします。倍角エンコード（R=cos2θ, G=sin2θ）なのでUV がミラーでも同じ向きになります。B は信頼度 |
| `_HairFlowStrength` | Hair Flow Strength | `Range(0,8)` | `0` | 0 でメッシュの接線そのまま。UV ミラーで天使の輪が割れるときに上げます |

### 異方性ハイライト（髪）（Anisotropic (Hair)） ／ ハイライト

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairSpecColor1` | Primary Color | `Color` | `(1,1,1,1)` | 内側の細いバンド |
| `_HairShift1` | Primary Shift | `Range(-1,1)` | `0.08` | バンドを法線方向へずらします。負で根元側へ動きます |
| `_HairSmoothness1` | Primary Smoothness | `Range(0,1)` | `0.7` | バンドの幅。Kajiya-Kay では指数 2^(10x+1) になります |
| `_HairSpecColor2` | Secondary Color | `Color` | `(0.75,0.85,0.8,1)` | 外側の広いバンド。束感が乗るのはこちらだけです |
| `_HairShift2` | Secondary Shift | `Range(-1,1)` | `-0.12` | — |
| `_HairSmoothness2` | Secondary Smoothness | `Range(0,1)` | `0.35` | — |
| `_HairSpecIntensity` | Intensity | `Range(0,4)` | `1.0` | 2 本まとめて倍率を掛けます。髪は共通の Specular Intensity を通りません |
| `_HairStrandScale` | Strand Scale | `Range(0,200)` | `50` | 束の粒の U 方向の細かさ（3 オクターブのサイン）。画面上で 1 周期が 1 画素を切ると自動で効かなくなります |
| `_HairStrandSparkle` | Strand Sparkle | `Range(0,1)` | `0` | 粒が副バンドをどれだけ削るか。主バンドには掛かりません（細い芯なので割ると消えるため） |

### MatCap（MatCap）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_MatCapIntensity` | MatCap Intensity | `Range(0,5)` | `0` | 0 で分岐ごと飛びます。加算のアクセント専用（乗算は持ちません ── 環境光の主経路であるプローブ + SH を上書きできてしまうため）。テクスチャ未割り当てのまま上げておくと**加算は 0 なのにコストだけ払います** |
| `_MatCapTex` | MatCap (RGB) | `2D` | `"black" {}` | — |
| `_MatCapColor` | Tint (HDR) | `Color` | `(1,1,1,1)` | — |
| `_MatCapLightAlign` | Align to Light | `Range(0,1)` | `0` | 参照の向きを画面内の光の向きへ回します。ハイライトがカメラに貼り付いて見える弱点が減ります |

## 質感（Effects）

### ベイクマップ（Bent / Cavity / 曲率）（Baked Maps） ／ キャビティ（くぼみの微細遮蔽）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_CavityMap` | Cavity Map (R) | `2D` | `"white" {}` | 細かい窪み |
| `_CavityStrength` | Cavity Strength | `Range(0,1)` | `0` | — |

### ベイクマップ（Bent / Cavity / 曲率）（Baked Maps） ／ 曲率マップ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_CurvatureMap` | Curvature Map (R) | `2D` | `"gray" {}` | 焼いた曲率（0.5 = 平坦）。曲率の唯一の供給源で、Curvature Influence（陰・影タブ）がこれを読んで曲がった面の境界を広げます。三角形をまたいで連続なので面は出ません |

### コートとグリッター（Coat and Glitter） ／ クリアコート

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_ClearcoatStrength` | Clearcoat | `Range(0,1)` | `0` | 別の粗さを持つ薄い層を 1 枚重ねます。IOR は 1.5 固定（f0 = 0.04）。漆・真珠・濡れた唇 |
| `_ClearcoatSmoothness` | Clearcoat Smoothness | `Range(0,1)` | `0.9` | — |

### コートとグリッター（Coat and Glitter） ／ イリデッセンス

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_IridescenceIntensity` | Iridescence | `Range(0,1)` | `0` | 見る角度で色が回る薄膜のティント。0 で白（色が付かない） |
| `_IridescenceThickness` | Iridescence Thickness | `Range(0,4)` | `1` | 面が傾くにつれ色相がどれだけ速く回るか |
| `_IridescenceShift` | Iridescence Shift | `Range(0,1)` | `0` | 開始の色相をずらします |

### コートとグリッター（Coat and Glitter） ／ グリッター

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_GlitterMask` | Glitter Mask (R) | `2D` | `"white" {}` | — |
| `_GlitterColor` | Glitter Color (HDR) | `Color` | `(2,2,2,1)` | — |
| `_GlitterIntensity` | Glitter Intensity | `Range(0,50)` | `0` | 0 で機能ごとスキップします（一様分岐 ── バリアント非増・フェッチも無し）。粒のきらめきの強さです |
| `_GlitterScale` | Glitter Density (Scale) | `Range(10,1000)` | `100` | UV あたりのセル数。上げるほど細かく密に |
| `_GlitterSize` | Dot Size | `Range(0.0005,0.05)` | `0.005` | セル内の粒の半径 |
| `_GlitterTilt` | Normal Tilt Strength | `Range(0,2)` | `0.8` | 粒ごとの法線の傾け。強いほど色々な角度でフラッシュします |
| `_GlitterSparsity` | Sparsity | `Range(0,1)` | `0.5` | 粒をランダムに間引きます |
| `_GlitterIridescence` | Iridescence Amount | `Range(0,1)` | `0.5` | 粒ごとの虹色（ホログラムスパンコール） |
| `_GlitterIridescenceShift` | Iridescence Shift | `Range(0,1)` | `0.5` | — |
| `_GlitterBaseReflection` | Base Reflection | `Range(0,0.5)` | `0.05` | 光っていない粒にも残す薄い反射。生地がラメ物だと分かる下地です |

### シアー生地 (ストッキング)（Sheer Fabric）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_StockingIntensity` | Stocking Intensity | `Range(0,1)` | `0` | 0 で分岐ごと飛びます。布を別メッシュで重ねずに、視角依存の不透明度で肌の上へ乗せます |
| `_StockingColor` | Stocking Color | `Color` | `(0.76, 0.65, 0.55, 1)` | 正面では肌に乗算され、シルエットでは布そのものの色になります。同じ色が「透けた布」と「布地」の両方に見えます |
| `_StockingMask` | Stocking Mask (R) | `2D` | `"white" {}` | 布のある場所。太ももの境目はここに描きます |
| `_StockingFrontOpacity` | Front Opacity | `Range(0,1)` | `0.25` | 正面を向いた面でどれだけ肌が透けるか |
| `_StockingPower` | Graze Power | `Range(0.5,8)` | `1.5` | — |

### SSS（表面下散乱）（SSS (Subsurface)）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_SubsurfaceColor` | Subsurface Color | `Color` | `(1.0, 0.55, 0.45, 1)` | 境界の影側へにじむ色 |
| `_SubsurfaceStrength` | Strength | `Range(0,2)` | `0` | — |

### 透過（Transmission）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_TransmissionColor` | Transmission Color | `Color` | `(1.0, 0.35, 0.25, 1)` | アルベドに乗算されるので、置き換えではなく色付けになります |
| `_TransmissionPower` | Power | `Range(1,16)` | `4` | 光が抜けてくる向きにどれだけ絞るか。上げるほどほぼ光源を覗き込む角度でしか見えなくなります |
| `_TransmissionStrength` | Strength | `Range(0,4)` | `0` | — |
| `_TransmissionDistortion` | Distortion | `Range(0,1)` | `0.2` | 抜ける光を SSS の向きへ曲げます。ライトベクトルを打ち消したときはNaN にせず生のライト方向へ落とします |
| `_SSSMap` | SSS Map (RGB=dir A=thickness) | `2D` | `"bump" {}` | 焼いた散乱方向。無い場合はシェーディング法線を使います |
| `_SSSMapStrength` | SSS Map Strength | `Range(0,1)` | `0` | — |

### リムライト / Peach Fuzz（Rim / Peach Fuzz）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_RimMode` | Rim Mode | `Float` | `1` | Screen Silhouette: 深度差の縁取り（アニメ的な逆光リム）。Fresnel PBR: EasyPBR(Doll) と同じ Core の式。リムがライトのエネルギーに比例し、ステージ照明の色が縁に乗ります。深度読みを飛ばすぶん軽量 |
| `_RimColor` | Rim Color | `Color` | `(1.0, 0.75, 0.5, 1)` | — |
| `_RimIntensity` | Intensity | `Range(0,8)` | `1.5` | NPR マップの B でも絞られるので、部位ごとにマスクできます |
| `_RimWidth` | Width | `Range(0,10)` | `1.5` | 深度を読みに行く距離。1m での画素数で、距離で割るので遠近によらず実寸の太さが保たれます |
| `_RimThreshold` | Depth Threshold | `Range(0,0.5)` | `0.02` | 縁とみなす深度の差 |
| `_RimSoftness` | Depth Softness | `Range(0.001,0.5)` | `0.05` | 下限としてだけ効きます。シルエットでは深度が 1 画素でメートル級に飛ぶので、実際の幅は画面上の変化率から決まります |
| `_RimFresnelPower` | Fresnel Falloff | `Range(0.1,8)` | `2.5` | 上げるほどリムがシルエットへ寄って細くなります |
| `_RimBacklightBias` | Backlight Bias | `Range(0,1)` | `0.7` | ライトがカメラを向いている度合いで重み付けします。画面内では一様な値です（どこでも同じ） |
| `_RimDirectionality` | Directionality | `Range(0,1)` | `1` | これが無いとシルエットの全周に等しく出て、ライトを動かしてもリムの位置が変わりません。光が回り込んだ側だけに切ります |
| `_RimReceiveShadow` | Receive Cast Shadow | `Range(0,1)` | `1` | 落ち影の中でリムを消します。見るのは落ち影だけで NdotL の陰は含みません（リムは「そこに光が届いているか」の話なので） |
| `_RimDepthBlend` | Depth Blend | `Range(0,1)` | `0.6` | 0 でフレネルのみ（深度テクスチャ不要）、1 で深度の縁でも絞ります |
| `_RimFresnelThickness` | Fresnel Thickness (PBR) | `Range(0,1)` | `0.3` | 0 で極細（指数 12）、1 で極太（0.5）。Doll と同じ写像です |

### リムライト / Peach Fuzz（Rim / Peach Fuzz） ／ Peach Fuzz（縁の柔らかい光沢）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_FuzzColor` | Peach Fuzz Color (HDR) | `Color` | `(1.0, 0.95, 0.9, 1.0)` | — |
| `_FuzzIntensity` | Peach Fuzz Intensity | `Range(0,5)` | `0` | 0 で機能ごとスキップします（一様分岐・バリアント非増） |
| `_FuzzPower` | Peach Fuzz Width | `Range(0.1,10)` | `4` | 小さいほど帯が広く、光の当たる側まで回り込みます。大きいほどシルエットに張り付いた細い線になります |

### ベイクマップ（Bent / Cavity / 曲率）（Baked Maps） ／ ベント法線マップ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_BentNormalOn` | Use Bent Normal | `Float` | `0` | 間接拡散の向きを、遮蔽されていない方向へ寄せます |
| `_BentNormalMap` | Bent Normal Map | `2D` | `"bump" {}` | — |

## 演出（FX）

### ディゾルブ / 暗転（Dissolve / Black Out）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DissolveAmount` | Dissolve Progress | `Range(0,1)` | `0` | 0 で全部出ています（分岐ごと飛び、キーワードレスなのでバリアントも増えません）。1 で全部消えます。両端は保証されています（縁の幅ぶん閾値を広げてあるので消え残りません） |
| `_DissolveInvert` | Invert | `Float` | `0` | 判定の符号を反転します。反対の端から消えます |
| `_DissolveType` | Axis | `Float` | `1` | 0 = 使わない（ノイズだけ）/ 1 = ワールド Y / 2 = ローカル Y。ローカルはキャラが動いても一緒に動きます |
| `_DissolveStartY` | Start Y | `Float` | `0` | 勾配が 0 になる高さ。End と同じ値でも安全です（「一気に消す」の意味になります） |
| `_DissolveEndY` | End Y | `Float` | `2` | — |

### ディゾルブ / 暗転（Dissolve / Black Out） ／ ノイズ

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DissolveTex` | Noise (R) | `2D` | `"white" {}` | UV だけで引きます（三平面投影はしません）。キャラの UV が整っている前提です |
| `_DissolveNoiseScale` | Noise Scale | `Float` | `1` | — |
| `_DissolveNoiseStrength` | Noise Strength | `Range(0,1)` | `0.5` | 高さの境界をノイズがどれだけ崩すか。0 で水平な直線になります |

### ディゾルブ / 暗転（Dissolve / Black Out） ／ 縁

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DissolveEdgeColor` | Edge Glow (HDR) | `Color` | `(1, 0.6, 0, 1)` | 縁の帯の内側に発光として足されます |
| `_DissolveEdgeColor2` | Edge Char Color (HDR) | `Color` | `(1, 0, 0, 1)` | 縁の帯ぜんたいでアルベドを置き換えます（焦げの表現） |
| `_DissolveEdgeWidth` | Edge Width | `Range(0.001,0.5)` | `0.05` | — |
| `_DissolveEdgeStep` | Step Edge (toon) | `Float` | `0` | 縁を 2 段に量子化し、発光もグラデーションでなく硬く切ります |

### ディゾルブ / 暗転（Dissolve / Black Out） ／ 暗転エフェクト

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_BlackOut` | Black Out | `Range(0,1)` | `0` | 最終色を黒へ落とします（**発光も含めて**。輪郭線も一緒に沈みます）。アルファは触らないので、消えるのではなく黒く沈みます（消したいときはディゾルブ） |

## 詳細（Advanced）

### デバッグ表示（Debug View）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_DebugMode` | Debug View | `Float` | `0` | — |

### レンダーステート（Render State）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_Cull` | Cull | `Float` | `2` | 全パスに掛かるので、影のシルエットが本体と一致します |
| `_ZTest` | Z Test | `Float` | `4` | Never にすると Unity は何も言わずに**完全に消えます**。従うのは本体のパスだけで、深度と法線は LEqual のままです |
| `_OffsetFactor` | Offset Factor | `Float` | `0` | ポリゴン深度オフセット（傾き項）。負でカメラ側に寄ります ── 眉・睫毛を顔の上に浮かせる用途。本体・前髪透過・深度・法線の各パスに掛かります（影には掛かりません） |
| `_OffsetUnits` | Offset Units | `Float` | `0` | 深度オフセットの定数項（最小深度刻み単位） |
| `_ShadowCasterOff` | Exclude from Shadow Map | `Float` | `0` | このマテリアルが**影を落とすのをやめます**（落ちる影が消えるだけで、受ける影は残ります）。顔に自己影を落とす瞳・睫毛には有効ですが、**髪に使うと顔にも体にも髪の影が落ちなくなります** |

### ステンシル（Stencil）

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_StencilRef` | Ref | `Range(0,15)` | `0` | Replace なら書き込む値、Equal なら比べる値 |
| `_StencilComp` | Comp | `Float` | `8` | Never にすると Unity は何も言わずに 1 画素も描きません |
| `_StencilPass` | Pass Op | `Float` | `0` | Replace でも Write Mask が 0 だと**何も書きません**。それを当てにしている材質が黙って成立しなくなります |
| `_StencilReadMask` | Read Mask | `Range(0,255)` | `15` | 比較で見るビット |
| `_StencilWriteMask` | Write Mask | `Range(0,255)` | `15` | 書き込んでよいビット |

### ステンシル（Stencil） ／ 前髪透過

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_HairSeeThroughAlpha` | See-Through Alpha | `Range(0,1)` | `0.6` | 眉・目の上にかかる髪の不透明度 |

## ?

### (節なし)

| プロパティ | 表示名 | 型 | 既定 | 説明 |
| :--- | :--- | :--- | :--- | :--- |
| `_Cutoff` | Cutoff | `Range(0,1)` | `0.5` | このアルファ未満の画素を捨てます |
| `_SrcBlend` | Source Blend | `Float` | `1` | — |
| `_DstBlend` | Destination Blend | `Float` | `0` | — |
| `_ZWrite` | ZWrite | `Float` | `1` | 半透明では通常 Off のままにします |

---

説明のあるもの 154 / 209。**残り 55 個は tooltip が書かれていない** ── `ToonPBRShaderGUI.cs` に足すとここにも出ます。

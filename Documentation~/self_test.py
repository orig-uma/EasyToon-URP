#!/usr/bin/env python3
"""
self_test.py — 検査そのものが生きているかを確かめる

**このプロジェクトで最も繰り返している失敗は「検査が黙って死ぬ」こと。**
落ちた検査は「エラー 0 件」と報告するので、通ったのと見分けが付かない。

    T-132  verify_variants.py が削除済みのクラスを呼んでおり、
           Unity がエラー終了しても `0 組で指摘あり` と出して exit 0 を返していた
    T-155  プリセット窓のフィルタがシェーダー名に一致せず、
           窓ごと一度も動いていなかった（押しても何も起きないボタンを案内し続けた）
    T-166  E009 の正規表現に**バックスペース文字**が埋め込まれ、
           何にもマッチしないまま「エラー 0 件」と報告していた
    T-171  `run()` の戻り値を tuple に変えたのに呼び出し側が bool のままで、
           **空でないタプルが常に真**になり全部 OK と表示された

どれも「0 件」という表示が「検査が動いていない」と見分けられなかった。

**そこで、欠陥をわざと注入して発火することを確かめる。**
今までこれを毎回手でやっていた。手でやる限り、やらない回が必ず出る。

使い方:
    cd Assets/ToonPBR
    python self_test.py
    python check.py --self-test        # 同じものを check.py から回す

仕組み: ToonPBR ディレクトリ一式をテンポラリへ複製し、そこで文字列を
書き換えて検査を回す。**本物のソースには一切触らない。**
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent

def find_unity_project(start: Path) -> Path | None:
    """Unity プロジェクトのルート（`Assets` と `Packages` を持つ場所）。

    **場所に依存した相対パスを書かないこと。** このツリーは
    `Assets/ToonPBR/` にあるが、パッケージへ移すと
    `Packages/com.origuma.easytoon-urp/Documentation~/` へ移る。
    `HERE.parent` で `Assets` を指すような書き方をしていると、
    **移した瞬間に全部空振りする**（T-250）。
    """
    for p in [start, *start.parents]:
        if (p / "Assets").is_dir() and (p / "Packages").is_dir():
            return p
    return None


_PROJ = find_unity_project(HERE)
MATERIALS = ((_PROJ / "Assets" / "requiem" / "vjT4u4BcId" / "Materials 3")
             if _PROJ else HERE / "mats")

NL = chr(10)   # 注入文字列に改行を混ぜるため（エスケープを書かずに済ませる）

# 移行元のシェーダー。移行の検査（check_migration_rules）がこれを読む。
PACKAGES = (_PROJ / "Packages") if _PROJ else HERE.parent.parent / "Packages"
PACKAGE_SHADERS = [
    "com.origuma.easypbr-urp/Runtime/Shaders/Doll/Doll.shader",
    # T-340: ツリーが include する EasyShaderCore の純関数。check_guards が
    # include を辿って Core 側の守り（Smith の max(1-a²,0)）まで見るので、
    # サンドボックスにも同じ相対位置で複製しておく。
    "com.origuma.easyshader-core/Runtime/Shaders/Common/Common_Math.hlsl",
    "com.origuma.easyshader-core/Runtime/Shaders/Common/Common_Color.hlsl",
    "com.origuma.easyshader-core/Runtime/Shaders/Common/Common_Sampling.hlsl",
    "com.origuma.easyshader-core/Runtime/Shaders/Common/BRDF/BRDF_GGX.hlsl",
    "com.origuma.easyshader-core/Runtime/Shaders/Common/BRDF/BRDF_RimFuzz.hlsl",
]


@dataclass
class Case:
    """欠陥を注入して、狙った指摘が出ることを確かめる。

    `edits` が**複数**なのは、1箇所いじるだけでは成立しない欠陥があるため。
    アルファテストの全画素落ちは「BaseMap 未割り当て」と
    「_BaseColor.a < _Cutoff」の**両方**が揃って初めて起きる。
    片方だけ注入して「発火しない」と報告するのは試験側の誤り
    ── 実際そう書いて一度空振りした。

    **`find` は `re:` で始めると正規表現。** マテリアルの値を書き写すと、
    利用者が値を変えた瞬間に「注入先が見つからない」で落ちる ──
    実際 `_ShadowValue: 0.52` を探す形で 2 件が落ちた（利用者が 1 に変えていた）。
    **書き写した数字は古くなる**をこの試験自身が踏んだ形なので、
    値の部分は正規表現で受けること。
    """
    name: str
    tool: list[str]                       # 走らせるコマンド（temp 内の相対）
    edits: list[tuple[str, str, str]]     # (対象ファイル, 探す文字列, 置換後)
    expect: str                           # 出力に現れるべき文字列
    why: str                              # この検査が死ぬと何を見逃すか
    covers: str                           # どの検査を試したか（コード名 or 関数名）
    # **時刻の注入。** Editor ログの鮮度判定（L004）は
    # 「ログがソースより古いか」しか見ていないので、
    # **文字列の置換では絶対に壊せない。** ここだけ別の注入手段が要る。
    mtime: tuple[str, float] | None = None   # (対象ファイル, unix 時刻)


CASES: list[Case] = [
    Case(
        name="E009 飽和後の下駄",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl",
                "c.NdotV      = max(saturate(dot(c.N, c.V)), 1e-4);",
                "c.NdotV      = saturate(dot(c.N, c.V)) + 1e-4;")],
        expect="E009",
        why="値域が [0,1] を外れ、下流の pow が負の底で NaN になる（T-165）",
        covers="E009",
    ),
    Case(
        # ファイル冒頭に「後で定義される関数を呼ぶ」行を差し込む。
        # 引数名を変えるだけでは定義順は変わらない ── 最初そう書いて空振りし、
        # **試験が誤っているのか検査が死んでいるのか**が分からなくなった。
        name="E008 関数の定義順",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl",
                "#define TOON_PI 3.14159265359",
                "#define TOON_PI 3.14159265359" + NL
                + "float ToonLintProbeE008()"
                  " { return ToonShadowAlbedo(float3(0,0,0)).x; }")],
        expect="E008",
        why="HLSL は宣言順に解析する。実バリアントコンパイル（3分）でしか捕まらない類",
        covers="E008",
    ),
    Case(
        name="守りが外れている（Smith 可視項・Core 側）",
        tool=["param_check.py", "."],
        # サンドボックスは tmp/Assets/ToonPBR が root で、Packages は tmp 直下
        # （build_sandbox 参照）。root からは 2 階層上がる。
        edits=[("../../Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/BRDF/BRDF_GGX.hlsl",
                "max(1.0 - a2, 0.0)",
                "(1.0 - a2)")],
        expect="守りが外れている",
        why="T-340 で本体が Core へ移った。ツリーだけ見る検査だと Core 側で守りが消えても黙る"
            "（編集するのはサンドボックスへの複製で、実物の Core は触らない）",
        covers="check_guards",
    ),
    Case(
        name="CLAUDE.md の検査コード表",
        tool=["param_check.py", "."],
        edits=[("CLAUDE.md", "| E009 |", "| E009_disabled |")],
        expect="検査コード E009",
        why="表に無い検査は無いのと同じ。実装したのに誰も知らない検査ができる（T-168）",
        covers="check_docs",
    ),
    Case(
        # **項目数を試験側に書かない。** 最初 `この文書は 172 項目` を探す形で書き、
        # T-172 を足す前だったので即座に空振りした。項目が増えるたび壊れる試験は、
        # まさに T-167 で戒めた「書き写した数字は古くなる」そのもの。
        # 見出しを1つ増やせば、記録されている数と実際の数が必ずずれる。
        name="BACKLOG のサマリ項目数",
        tool=["param_check.py", "."],
        edits=[("BACKLOG.md", "## 現在の状態",
                "### T-99999 自己診断が注入したダミー" + NL + NL + "## 現在の状態")],
        expect="サマリの項目数",
        why="数字がずれているならサマリの中身もずれている。動かないツールを案内する（T-160）",
        covers="check_docs",
    ),
    Case(
        # **この 2 件はパッケージへ移して以降ずっと死んでいた。**
        # 文書を `root.glob("*.md")` で探しており、`root` は
        # `Runtime/Shaders/Idol` なので必ず空。指摘 0 件が「問題が無い」に
        # 見えていた（T-330）。カバー率は関数単位なので、同じ `check_docs` の
        # 他の枝が生きている限り**死んだ枝は数字に出ない**。だから枝ごとに置く。
        name="手順が存在しない道具を案内している",
        tool=["param_check.py", "."],
        edits=[("BACKLOG.md", "## 現在の状態",
                "```bash" + NL + "python zzz_removed_tool.py ." + NL + "```" + NL + NL
                + "## 現在の状態")],
        expect="コマンドが存在しないスクリプトを指す",
        why="書いてある手順がそのままでは動かない。読む人は自分の環境を疑う（T-107）",
        covers="check_docs",
    ),
    Case(
        # **砂場では T-330 / T-331 そのものを再現できない。** 砂場は移行前の
        # 平らな配置なので `root / "BACKLOG.md"` が在ってしまう ── それこそが
        # あの 2 件を緑のまま通していた理由。だから配置に依らず**永久に偽**の
        # ガードを入れて、仕組み（箇所ごとに当たりを数える）を試す。
        name="一度も当たらないガード",
        tool=["tool_lint.py", "."],
        edits=[("param_check.py", '    cs = find_file(root, "ToonPBRMigrator.cs")',
                '    if (root / "zzz_never_here.hlsl").exists():' + NL
                + "        return out" + NL
                + '    cs = find_file(root, "ToonPBRMigrator.cs")')],
        expect="一度も当たらないガード",
        why="その先の検査が丸ごと動かない。指摘 0 件と区別が付かない（T-330 / T-331）",
        covers="check_reachability",
    ),
    # 「サマリが消したものを案内し続ける」は既にある（表の行へ注入する形）。
    # **同じ枝を 2 度試しても項目数が増えるだけ**なので足さない。
    # ただしその試験は**砂場でしか通っていなかった** ── 砂場は文書を平置きで
    # 複製するので `root / "BACKLOG.md"` が在り、本番の入れ子では空だった。
    # 試験の世界と本番の世界が違うと、緑のまま死ぬ（T-330）。
    Case(
        name="半影半径を既定より広げている",
        tool=["param_check.py", ".", "--materials", "mats"],
        # **前提も注入で作る。** この検査は接地硬化（PCSS）が ON のときだけ
        # 走る。以前は素材の現在値（0.shita が ON）に依存していて、利用者が
        # Unity でその値を 0 に変えただけで試験が黙って死んだ ── シーン由来の
        # 状態を試験に焼き込む T-155 の失敗を、試験自身がやっていた形。
        edits=[("mats/0.shita.mat",
                r"re:- _ShadowContactHardening: [\d.]+",
                "- _ShadowContactHardening: 1"),
               ("mats/0.shita.mat",
                r"re:- _HQShadowSoftness: [\d.]+",
                "- _HQShadowSoftness: 0.8")],
        expect="半影半径の可動域",
        why="既定に警告を出さないよう直した検査。閾値を間違えると 46 件全部に出る（T-167）",
        covers="check_pcss",
    ),
    Case(
        # **2箇所同時でなければ成立しない。** BaseMap を外すだけでは
        # `_BaseColor.a = 1.0 >= _Cutoff 0.5` で発火しない。
        name="アルファテストで1画素も描かれない",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat",
                "    - _BaseMap:" + NL + "        m_Texture: {fileID: 2800000",
                "    - _BaseMap:" + NL + "        m_Texture: {fileID: 0"),
               # アルファ側ではなく **Cutoff を上げる**。_BaseColor の rgb は
               # マテリアルごとに違うので、行全体を書き換える形にすると
               # 材料を差し替えた瞬間に空振りする。
               ("mats/0.shita.mat", r"re:- _Cutoff: [\d.]+", "- _Cutoff: 2")],
        expect="1画素も描かれない",
        why="マテリアルが1画素も描かれない。Unity は何も言わないので絵を見るまで気付けない（T-170）",
        covers="check_alpha_clip",
    ),
    Case(
        name="E001 CBUFFER に無いプロパティを参照",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl", "    float  _ShadowColorMix;" + NL, "")],
        expect="E001",
        why="SRP Batcher が壊れるか、実行時に未定義値を読む",
        covers="E001",
    ),
    Case(
        name="E003 未宣言のテクスチャをサンプル",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl",
                "TEXTURE2D(_CurvatureMap);", "TEXTURE2D(_CurvatureMapRenamed);")],
        expect="E003",
        why="コンパイルが通らない。静的検査なら1秒で分かる",
        covers="E003",
    ),
    Case(
        name="E005 UnityPerMaterial の重複宣言",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl", "CBUFFER_END",
                "CBUFFER_END" + NL + "CBUFFER_START(UnityPerMaterial)" + NL
                + "CBUFFER_END")],
        expect="E005",
        why="SRP Batcher が黙って無効になる。絵は変わらないので描画コールの数でしか気付けない",
        covers="E005",
    ),
    Case(
        # **W107 は「黙って何もしない」を撃つ検査。** Editor 側が
        # `HasProperty` の null に守られるので、名前がずれても例外が出ない。
        name="W107 Editor スクリプトのプロパティ名がずれる",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Editor/ToonPBRShaderGUI.cs",
                '"_SpecAAVariance"', '"_SpecAAVarianceTypo"')],
        expect="W107",
        why="インスペクタに項目が出なくなる。例外も警告も出ないので気付けない（T-155 と同型）",
        covers="W107",
    ),
    Case(
        name="Range を外れた値",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat",
                r"re:- _ShadowColorMix: [\d.]+", "- _ShadowColorMix: 5")],
        expect="Range を外れた値",
        why="Range はスライダを縛るだけで実行時は縛らない。lerp が外挿になる（T-076 / T-098）",
        covers="check_ranges",
    ),
    Case(
        # **前提も注入で作る。** 素材の現在値（トグルとキーワードの現状）に
        # 依存すると、利用者がインスペクタを触っただけで試験が死ぬ
        # （PCSS の件と同じ轍。T-155）。キーワード行を m_ValidKeywords へ
        # 注入し（既に在れば重複行は set() が畳むだけで無害）、トグルを 0 に
        # 落とせば、元の状態がどうであれ「キーワード ON × トグル OFF」の
        # 食い違いが必ず成立する。
        name="トグルとキーワードが食い違う",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat",
                "  m_ValidKeywords:" + NL,
                "  m_ValidKeywords:" + NL + "  - _OUTLINE_ON" + NL),
               ("mats/0.shita.mat",
                r"re:- _OutlineOn: [\d.]+", "- _OutlineOn: 0")],
        expect="トグルとキーワードが食い違う",
        why="インスペクタは ON に見えるのに効かない。一括編集でよく起きる",
        covers="check_toggle_keywords",
    ),
    Case(
        name=".mat の値が空になっている",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat", r"re:- _ShadowValue: [\d.]+", "- _ShadowValue:")],
        expect="値が空になっている",
        why="Unity は黙って既定値に落ちるか、インポートに失敗する",
        covers="check_material_integrity",
    ),
    Case(
        name="影の明度が 1 を超えている",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat", r"re:- _ShadowValue: [\d.]+", "- _ShadowValue: 3")],
        expect="影の明度が 1 を超えている",
        why="影のほうが明るくなり、陰影が反転する",
        covers="check_shadow_band",
    ),
    Case(
        # **2箇所の編集で「後ろへ移す」を作る。** 消して末尾に足す。
        name="E004 依存ヘッダのインクルード順",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl",
                '#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"' + NL,
                ""),
               ("ToonPBRCommon.hlsl",
                "#endif // TOON_PBR_COMMON_INCLUDED",
                '#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"' + NL
                + "#endif // TOON_PBR_COMMON_INCLUDED")],
        expect="E004",
        why="HLSL は上から解析する。使用行より後で include しても手遅れ",
        covers="E004",
    ),
    Case(
        # **T-072 は実機で落ちた。** ps_4_0 のサンプラは 16 本で URP と分け合う。
        name="W105 サンプラの本数",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl", "SAMPLER(sampler_BaseMap);",
                "SAMPLER(sampler_BaseMap);" + NL
                + NL.join(f"SAMPLER(sampler_LintProbe{i});" for i in range(14)))],
        expect="W105",
        why="サンプラが上限を超えると**実機で落ちる**。静的検査でしか事前に分からない（T-072）",
        covers="W105",
    ),
    Case(
        # 光源に依存しない重い計算を ToonShadeLight の中へ戻す。
        # `_ShadowColorMix` はマテリアルの値なのでライトに依らない。
        name="光源ループ内の光源非依存な計算",
        tool=["param_check.py", "."],
        edits=[("Shading/ToonPBRLighting.hlsl",
                "    float3 L = light.direction;   // 鏡面・透過に使う実際の向き",
                "    float lintProbe = pow(_ShadowColorMix, 2.0);" + NL
                + "    float3 L = light.direction;   // 鏡面・透過に使う実際の向き")],
        expect="光源ループ内の光源非依存",
        why="ライトの数だけ無駄に再計算される。**同じ忘れ方を2回している**（T-122 / T-123）",
        covers="check_light_loop",
    ),
    Case(
        # **この検査は書いている最中に2回、黙って空振りした**（T-174）。
        # プレハブのホップ忘れと `Path(".").parent` が `.` のまま。
        # どちらも例外を出さず「該当なし」で通る形なので、必ず試験に入れる。
        name="AA が1つも有効になっていない",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("../Settings/Fixture_RPAsset.asset",
                "m_MSAA: 4", "m_MSAA: 1")],
        expect="AA が1つも有効になっていない",
        why="ちらつきの主因になりうる設定を見逃す。シェーダーをいくら直しても消えない（T-174）",
        covers="check_render_settings",
    ),
    Case(
        name="E002 CBUFFER にあるが Properties に無い",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl", "    float  _ShadowColorMix;",
                "    float  _ShadowColorMix;" + NL + "    float  _LintProbeE002;")],
        expect="E002",
        why="インスペクタから触れない値が CBUFFER に残る。SRP Batcher のレイアウトも狂う",
        covers="E002",
    ),
    Case(
        name="E006 TRANSFORM_TEX に対応する _ST が無い",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl", "    float4 _BaseMap_ST;" + NL, "")],
        expect="E006",
        why="UV のタイリング／オフセットが効かなくなる",
        covers="E006",
    ),
    Case(
        # **DepthOnly は Screen Silhouette モードのリムの前提**（CLAUDE.md）。
        # 消えると絵から静かにリムが落ちる。
        name="E007 深度を読むのに DepthOnly パスが無い",
        tool=["shader_lint.py", ".", "--strict"],
        # **`Name` ではなく LightMode タグを変える。** 最初 `Name` を書き換えて
        # 空振りした ── E007 が見ているのは LightMode で、URP が振り分けに
        # 使うのもそちらなので**検査のほうが正しい**。
        edits=[("Idol.shader", '"LightMode" = "DepthOnly"',
                '"LightMode" = "DepthOnlyRenamed"')],
        expect="E007",
        why="_CameraDepthTexture が埋まらず、Screen Silhouette のリムが黙って効かなくなる",
        covers="E007",
    ),
    Case(
        name="W101 キーワードを宣言する pragma が無い",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl", "#if defined(_HQ_SHADOW_ON)",
                "#if defined(_HQ_SHADOW_NOPRAGMA)")],
        expect="W101",
        why="そのキーワードは**永久に立たない**。囲まれたコードが一度も動かない",
        covers="W101",
    ),
    Case(
        name="W102 キーワードを ON にする Property が無い",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Idol.shader", "[Toggle(_HQ_SHADOW_ON)] _HQShadowOn",
                "[Toggle(_HQ_SHADOW_ORPHAN)] _HQShadowOn")],
        expect="W102",
        why="インスペクタから立てられないキーワードができる",
        covers="W102",
    ),
    Case(
        name="W103 未参照のプロパティ",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Idol.shader", '        _Cutoff                  ("  Cutoff", Range(0,1)) = 0.5',
                '        _Cutoff                  ("  Cutoff", Range(0,1)) = 0.5' + NL
                + '        _LintProbeW103           ("  probe", Range(0,1)) = 0')],
        expect="W103",
        why="誰も読まない値がインスペクタに並ぶ。消し忘れの温床",
        covers="W103",
    ),
    Case(
        # `_ShadowPenumbraScale` は Range(0,1000)。lerp の係数に裸で渡すと
        # **外挿**になり、色が定義域の外へ飛ぶ（T-076 / T-098 と同じ形）。
        name="W106 Range 外のプロパティを lerp の係数に渡す",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Shading/ToonPBREnv.hlsl",
                "return lerp(1.0, comp, _EnergyCompensation);",
                "return lerp(1.0, comp, _ShadowPenumbraScale);")],
        expect="W106",
        why="lerp が外挿になり色が破綻する。Range はスライダを縛るだけで実行時は縛らない",
        covers="W106",
    ),
    Case(
        # **プロパティ追加は3箇所同時**（Properties / CBUFFER / 読み出し）。
        # その3つを足して **ShaderGUI にだけ足さない**のが W104 の状況で、
        # 「インスペクタから消えたまま検査は全部通る」形になる。
        name="W104 ShaderGUI に出ないプロパティ",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Idol.shader",
                '        _Cutoff                  ("  Cutoff", Range(0,1)) = 0.5',
                '        _Cutoff                  ("  Cutoff", Range(0,1)) = 0.5' + NL
                + '        _LintProbeW104           ("  probe", Range(0,1)) = 0'),
               ("ToonPBRCommon.hlsl", "    float  _ShadowColorMix;",
                "    float  _ShadowColorMix;" + NL + "    float  _LintProbeW104;"),
               ("Shading/ToonPBRColor.hlsl", "    result *= _ShadowTint.rgb;",
                "    result *= _ShadowTint.rgb * (1.0 - _LintProbeW104 * 0.0);")],
        expect="W104",
        why="Properties に足して GUI に足し忘れると、インスペクタから消えたまま全検査が通る",
        covers="W104",
    ),
    Case(
        # **検査ツール自身を壊す唯一の項目。**
        # E000 は「lint_shader が例外で落ちた」の受け皿で、
        # **落ちたことに気付けるか**を見ている。
        # 落ちた検査が「エラー 0 件」と報告する形はこのプロジェクトの持病
        # （T-132 / T-155 / T-166 / T-171）なので、受け皿が生きているかは重要。
        name="E000 検査自身が落ちたときに気付けるか",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("shader_lint.py",
                "def lint_shader(shader_path: Path, issues: list[Issue]) -> None:" + NL
                + "    src = load(shader_path)",
                "def lint_shader(shader_path: Path, issues: list[Issue]) -> None:" + NL
                + '    raise RuntimeError("self_test が注入した故障")' + NL
                + "    src = load(shader_path)")],
        expect="E000",
        why="**検査が落ちても「エラー 0 件」と出る。** 通ったのと見分けが付かなくなる",
        covers="E000",
    ),
    Case(
        # **分割でいちばん静かな壊れ方**（T-216）。Unity は素の include の中の
        # pragma を読まない ── キーワードが永久に立たず、絵は出るのに効かない。
        name="pragma を include 先へ置く",
        tool=["param_check.py", "."],
        edits=[("Idol.shader",
                "            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP" + NL, ""),
               ("Passes/ForwardPass.hlsl",
                "// ForwardLit の本体（頂点・フラグメント）。",
                "#pragma multi_compile _ _CLUSTER_LIGHT_LOOP" + NL
                + "// ForwardLit の本体（頂点・フラグメント）。")],
        expect="#pragma がある",
        why="キーワードが永久に立たない。コンパイルは通り絵も出るので実機で気付けない",
        covers="check_pragma_placement",
    ),
    Case(
        # 分割で新しく開いた入口（T-213）。**コンパイルは通り、絵も出る。**
        # そこに書いた関数が呼ばれないだけなので、影が薄い程度にしか見えない。
        name="どこからも include されていない HLSL",
        tool=["param_check.py", "."],
        edits=[("ToonPBRCommon.hlsl",
                '#include "Shading/ToonPBRRim.hlsl"' + NL, "")],
        expect="include されていない",
        why="切り出しの途中で 1 本落としても、絵からは気付けない",
        covers="check_orphan_includes",
    ),
    Case(
        # EasyToon の設計ルール 2（キーワードの安易な追加を禁止）。
        # **バリアントは増える一方で、入れたあとで減らすのは難しい。**
        name="許可していないキーワードを足す",
        tool=["param_check.py", "."],
        edits=[("Idol.shader",
                "#pragma shader_feature_local          _ALPHATEST_ON",
                "#pragma shader_feature_local          _ALPHATEST_ON _PROBE_NEW_KEYWORD")],
        expect="許可していないキーワード",
        why="移植先パッケージの設計ルールを外れる。入れたあとで気付いても減らせない（T-207）",
        covers="check_package_rules",
    ),
    Case(
        # サマリの数字を実装から数え直して突き合わせる（T-200）。
        # **書き写した数字は古くなる**がこのプロジェクトの最大の持病。
        # **数字を試験に書かない。** 最初 `値の検算（14 種）` を探す形で書き、
        # 検査を 1 つ足した瞬間に空振りした ── T-172 で同じ直し方をしたのに
        # **別の数字で繰り返した。** 検査関数を 1 つ増やせば、
        # サマリに何が書いてあっても必ずずれる。
        name="サマリの数字が実装とずれる",
        tool=["param_check.py", "."],
        edits=[("param_check.py", "def check_docs(root: Path) -> list[Finding]:",
                "def check_probe_dummy(root: Path) -> list[Finding]:" + NL
                + "    return []" + NL + NL + NL
                + "def check_docs(root: Path) -> list[Finding]:")],
        expect="サマリの数字が実装と違う",
        why="次のセッションが最初に読む場所。ここがずれると全部が古い前提から始まる",
        covers="check_docs",
    ),
    Case(
        # `_MaskMap` に生の AO を入れている 30 個は、`_Metallic = 0` のあいだだけ無害。
        # 上げた瞬間に **金属度が AO で変調される**（T-196）。
        name="パックしていないテクスチャを _MaskMap に入れている",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/8.atama_.mat", r"re:- _Metallic: [\d.]+", "- _Metallic: 1")],
        expect="パックしていないテクスチャ",
        why="金属度が AO で変調され、窪みだけ非金属になる。絵では気付きにくい",
        covers="check_maskmap_packing",
    ),
    Case(
        # 変換を書かずに値域がはみ出す行を撃つ（T-187）。
        # **同名だから安心、が通じない** ── 移行元 0〜5 / 移行先 0〜4 だった。
        name="移行の対応表で値域がはみ出す",
        tool=["param_check.py", "."],
        edits=[("Editor/ToonPBRMigrator.cs",
                'new Rule(Kind.Number,"_SpecularIntensity",  "_SpecularIntensity",' + NL
                + '                     v => Clamp(v, 0.0f, 4.0f), "0〜5 を 0〜4 へ丸めた"),',
                'new Rule(Kind.Number,"_SpecularIntensity",  "_SpecularIntensity"),')],
        expect="値域がはみ出す",
        why="正しい値のまま移すだけで Range の外に出て、lerp が外挿になる（T-076 / T-098）",
        covers="check_migration_rules",
    ),
    Case(
        # **W107 の除外で無検査になった側を塞ぐ検査**（T-186）。
        # 移行元の名前を typo すると `HasProperty` に守られて
        # **その 1 行だけ黙って移らない。**
        name="移行スクリプトの対応表が実在しない名前を指す",
        tool=["param_check.py", "."],
        edits=[("Editor/ToonPBRMigrator.cs",
                '"_AnisoStrandStrength","_HairStrandSparkle"',
                '"_AnisoStrandStrengthTypo","_HairStrandSparkle"')],
        expect="移行元に無いプロパティ",
        why="移行元の名前は W107 の対象外にしてある。ここが死ぬと typo が完全に無検査になる",
        covers="check_migration_rules",
    ),
    Case(
        # **`lint:foreign` の範囲が広がりすぎていないか。**
        # 移行スクリプトは移行元のプロパティ名を正当に書くので W107 を外してあるが、
        # **移行先（ToonPBR）の名前を書いている部分は見たい。**
        # そこを誤字ると「移行したのに値が入っていない」という、
        # まさに W107 が撃とうとしている形になる。
        name="移行スクリプトの書き込み側の誤字",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Editor/ToonPBRMigrator.cs",
                'mat.HasProperty("_ShadowColorMix")',
                'mat.HasProperty("_ShadowColorMixTypo")')],
        expect="_ShadowColorMixTypo",
        why="除外範囲を広げすぎると W107 がファイルごと黙る。移行の取りこぼしに気付けなくなる",
        covers="W107",
    ),
    Case(
        # T-182 で数値検証した「知覚粗さで渡す」を守る。alpha を渡すと
        # B 項の誤差が 2.5 倍になるが、**両方コンパイルが通り目視で区別できない。**
        name="環境 BRDF に alpha を渡してしまう",
        tool=["param_check.py", "."],
        edits=[("Shading/ToonPBRLighting.hlsl",
                "ToonEnvBRDFMultiScatter(s.f0, s.perceptualRoughness, c.NdotV)",
                "ToonEnvBRDFMultiScatter(s.f0, s.roughness, c.NdotV)")],
        expect="環境 BRDF に知覚粗さ",
        why="誘電体の縁が明るく持ち上がる。金属では出ないので気付きにくい（T-182）",
        covers="check_guards",
    ),
    Case(
        # sheen の指向性アルベドは**人が書き写した 15 個の定数**。
        # ずれても絵が少し変わるだけなので目視では見つからない。
        name="sheen の多項式が半球積分と合わない",
        tool=["param_check.py", "."],
        edits=[("Shading/ToonPBRSpecular.hlsl",
                "const float3 k0 = float3( 0.36875,  -0.49901,   0.10783);",
                "const float3 k0 = float3( 0.66875,  -0.49901,   0.10783);")],
        expect="半球積分と合っていない",
        why="布のエネルギー保存が崩れ、縁が明るくなるか暗くなる",
        covers="check_sheen_fit",
    ),
    Case(
        # **Renderer Feature が探す名前がずれると、何も描かないまま静かに通る。**
        # T-249 で LightMode を振り直したときに追随を忘れると起きる形。
        # 実際の呼び出しは**定数経由**なので、リテラルだけ見ても届かない。
        name="W111 Feature が探す LightMode がずれる",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Runtime/ToonOutlineFeature.cs",
                'ShaderTagId("IdolOutline")', 'ShaderTagId("ToonOutline")')],
        expect="W111",
        why="輪郭が一切描かれない。**例外も警告も出ず、機能を入れる前と同じ絵になる**",
        covers="W111",
    ),
    Case(
        # **キーワードを持たない機能は、書き忘れてもコンパイルが通る。**
        # ディゾルブは一様分岐なので、パスを足したときに落としても誰も言わない。
        # 実際 3 パスが切っていなかった（T-264）。
        name="W110 パスがディゾルブを切っていない",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/MotionVectorsPass.hlsl",
                "if (_DissolveAmount > 0.0)", "if (false)")],
        expect="W110",
        why="消えた画素がそのパスにだけ残る。**影・TAA の尾・輪郭と現れ方が違い原因に辿り着けない**",
        covers="W110",
    ),
    Case(
        # **3 つの値の合成でしか判定できない。** 単体を見ても「影が薄い」は分からない。
        # Unity 側の診断は同じ式を持つが**メニューから手で回すもの**で、
        # check.py の運用では届かなかった。
        name="影が薄い（影／光の比）",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat", r"re:- _ShadowValue: [\d.]+", "- _ShadowValue: 1"),
               ("mats/0.shita.mat", r"re:- _ShadowAmbientIntensity: [\d.]+",
                "- _ShadowAmbientIntensity: 2")],
        expect="影がほとんど出ない",
        why="影として認識されない濃さ。**値を個別に見ても判断できない**（3 つの合成）",
        covers="check_shadow_contrast",
    ),
    Case(
        # **Python は文字列のハッシュを実行ごとに変える。**
        # 集合や辞書を並べ替えずに出力へ流すと、走らせるたびに順や代表例が
        # 入れ替わり、**差分を追えなくなる**（T-314）。
        name="走らせるたびに結果が変わる",
        tool=["smoke_tools.py", ".", "--materials", "mats"],
        edits=[("param_check.py", "    print(f\"\\n検算: エラー {errors} 件 / 警告 {warnings} 件\")",
                "    import random\n"
                "    print(f\"\\n検算: エラー {errors} 件 / 警告 {warnings} 件 {random.random()}\")")],
        expect="走らせるたびに結果が変わる",
        why="**直ったのか揺れているのかが読めなくなる。** "
            "同じ状態で違う出力が出ると、差分が信用できない",
        covers="smoke_tools:determinism",
    ),
    Case(
        # **道具は「どこから回しても同じ」でなければならない。**
        # 相対パスの扱いを間違えると、`Documentation~` から回したときだけ
        # 正しく、別の場所からだと黙って何もしない（T-311 → T-313）。
        name="作業ディレクトリで結果が変わる",
        tool=["smoke_tools.py", ".", "--materials", "mats"],
        edits=[("param_check.py",
                "def run(root: Path, materials_dir: Path | None) -> list[Finding]:",
                "def run(root: Path, materials_dir: Path | None) -> list[Finding]:\n"
                "    import os\n"
                "    if os.getcwd() != str(Path(__file__).resolve().parent):\n"
                "        return []")],
        expect="作業ディレクトリで結果が変わる",
        why="**片方の呼び方でだけ正しい**という状態になる。"
            "どちらが正しいか読む側には分からない",
        covers="smoke_tools:cwd",
    ),
    Case(
        # **呼び出し方で結果が変わる検査は、片方で黙って何もしている。**
        # 相対パスだと `Path("..").parent.name` が空文字になり、
        # 親の名前で判断している箇所がすり抜ける（T-311 → T-312）。
        #
        # **元のバグはサンドボックスでは再現しない**（パッケージ構造が無く、
        # 相対でも絶対でも同じ「見つからない」に落ちる）。
        # ここで確かめるのは**検出器が差に気付くか**なので、
        # 呼び出し方で分かれる振る舞いを直接注入する。
        name="呼び出し方で結果が変わる検査",
        tool=["tool_lint.py", ".", "--deep"],
        edits=[("param_check.py",
                "    out: list[Finding] = []\n    for f in sorted(root.rglob(\"*.hlsl\")):",
                "    out: list[Finding] = []\n"
                "    if not root.is_absolute():\n"
                "        out.append(Finding(\"warning\", \"x\", \"x\", \"x\"))\n"
                "    for f in sorted(root.rglob(\"*.hlsl\")):")],
        expect="呼び出し方で結果が変わる検査",
        why="**相対で呼ぶと誤検出、絶対で呼ぶと正しい**のような状態になる。"
            "どちらが正しいか読む側には分からない",
        covers="check_path_independence",
    ),
    Case(
        # **検査の失敗経路は普段通らない。** そこに書き間違いがあっても
        # 平時は誰も踏まないので、「その検査が必要になった日」に初めて
        # NameError で落ちる ── 報告の代わりに道具ごと死ぬ。
        # 実際 `check_sheen_fit` に 2 か所あった（T-296）。
        name="道具の失敗経路に未定義名がある",
        tool=["tool_lint.py", "."],
        edits=[("param_check.py", '"error", str(root), "sheen の多項式係数が読めない"',
                '"error", hlsl.name, "sheen の多項式係数が読めない"')],
        expect="はこの関数のどこにも無い",
        why="**報告の代わりに検算が丸ごと落ちる。** 落ち方が「道具が死んだ」なので、"
            "元の問題に辿り着けない",
        covers="tool_lint",
    ),
    Case(
        # **押しても何も起きないのではなく、項目自体が無い。**
        # 読んだ側は「自分の見落とし」と解釈して探し回る。
        # 文書が実在しないものを指す型は繰り返し出ている（T-264 / T-281 / T-283）。
        name="案内しているメニューが実在しない",
        tool=["param_check.py", "."],
        edits=[("VERIFICATION.md",
                "`Tools > Idol > サーフェスタイプを名前から設定`",
                "`Tools > Idol > サーフェスタイプを自動設定`")],
        expect="案内しているメニューが実在しない",
        why="**リネームすると案内だけが残る。** 存在しない手順を指示され、"
            "利用者は自分の操作を疑って時間を溶かす",
        covers="check_menu_paths",
    ),
    Case(
        # **シェーダーもマテリアルも正しいのに絵が出ない型**（T-281 と同じ）。
        # 品質レベルごとに URP アセットが違うので、PC で調整した絵が
        # 品質を落とした瞬間に別物になる。読み先が未定義でも例外は出ない。
        name="深度を読むのにパイプラインが作らない",
        tool=["param_check.py", ".", "--materials", "mats"],
        # **深度の利用者も注入で作る。** T-343 でリムの既定が Fresnel になり、
        # 深度を読むのは _RimMode = 0（Screen Silhouette）を明示した材質だけに
        # なった。素材の現在値に頼らず、利用者そのものをここで仕立てる。
        edits=[("../Settings/Fixture_RPAsset.asset",
                "  m_RequireDepthTexture: 1", "  m_RequireDepthTexture: 0"),
               ("mats/0.shita.mat",
                r"re:- _RimIntensity: [\d.]+",
                "- _RimMode: 0" + NL + "    - _RimIntensity: 1")],
        expect="深度テクスチャを作らない品質レベルがある",
        why="**リムが全面に出るか一切出ないかに倒れる。** "
            "シェーダーもマテリアルも正しいので「値が悪い」と読めてしまう",
        covers="check_depth_texture_required",
    ),
    Case(
        # **押せないのではなく項目が無い。** Add Renderer Feature の一覧に
        # 出てこないので、読んだ側は自分の見落としと解釈して探し回る。
        # メニューの案内（`check_menu_paths`）と同じ型（T-311）。
        name="文書が実在しない Feature を挙げている",
        tool=["param_check.py", "."],
        edits=[("SETUP.md", "`Toon Outline Feature`", "`Toon Contour Feature`")],
        expect="文書が実在しない Feature を挙げている",
        why="**名前を変えると手順だけが残る。** 一覧に無いものを探させることになる",
        covers="check_doc_feature_names",
    ),
    Case(
        # **診断を誤らせるのが一番の実害。** 実際 `_SrcBlend: 5 / _DstBlend: 10` を
        # 見て「半透明になっている」と読み違えた ── Idol はその 2 つを
        # どこでも読んでいない（移行元の遺物）（T-308）。
        name="前のシェーダーのプロパティが残っている",
        tool=["param_check.py", ".", "--materials", "mats"],
        # **数種類では言わない検査**（普通の残りかすと区別するため）なので、
        # 閾値（40 種類）を超える数を作る。
        edits=[("mats/pin/Pin0.mat", "    - _Cutoff: 0.5" + NL,
                "    - _Cutoff: 0.5" + NL
                + "".join("    - _Legacy%d: 1" % i + NL for i in range(45)))],
        expect="前のシェーダーのプロパティが残っている",
        why="**ファイルとメモリが膨らみ、`.mat` を覗いた人を誤らせる。** "
            "新しいシェーダーは一度も読まない値が残り続ける",
        covers="check_leftover_properties",
    ),
    Case(
        # **`clip()` を持つと早期 Z が使えない。** それでもアルファの器が無ければ
        # サンプル結果は常に 1.0 で、**1 画素も落ちない** ── 払っているだけ。
        # 実測で 46 件中 40 件がこの状態だった（T-307）。
        name="アルファが無いのにクリップしている",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/pin/Pin0.mat", "  m_SavedProperties:",
                "  m_ValidKeywords:" + NL + "  - _ALPHATEST_ON" + NL + "  m_SavedProperties:")],
        expect="アルファが無いのにクリップしている",
        why="**早期 Z を捨てているのに 1 画素も落としていない。** "
            "絵は変わらないので、測るまで気付けない",
        covers="check_alpha_clip_without_alpha",
    ),
    Case(
        # **中身が同一のバリアントが倍に増えるだけ。** 絵は 1 ピクセルも変わらず、
        # ビルド時間と容量にだけ効くので数えるまで気付けない。
        # W109 はシェーダー全体しか見ないので、**パスごとの過不足は素通り**（T-306）。
        name="使わないキーワードをパスに宣言している",
        tool=["param_check.py", "."],
        edits=[("Idol.shader", "            #pragma shader_feature_local          _ALPHATEST_ON",
                "            #pragma shader_feature_local          _ALPHATEST_ON\n"
                "            #pragma shader_feature_local          _NEVER_TESTED_ON")],
        expect="使わないキーワードをパスに宣言している",
        why="**バリアントが倍に増えるだけ。** 別のパスで使っていれば W109 は通るので、"
            "パス単位で見ないと出ない",
        covers="check_pass_keyword_use",
    ),
    Case(
        # **症状は「TAA の設定が悪い」に見える。** 原因はマテリアルの
        # `disabledShaderPasses` にあり、インスペクタにも出ない（T-304）。
        name="MotionVectors パスを止めている",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[(f"mats/pin/Pin{n}.mat", "  - SRPDEFAULTUNLIT\n",
                "  - SRPDEFAULTUNLIT\n  - MOTIONVECTORS\n") for n in range(4)],
        expect="MotionVectors パスを止めている",
        why="**キャラだけが尾を引く／輪郭が二重に残る。** スキンメッシュは"
            "毎フレーム形が変わるので影響が大きい",
        covers="check_motionvectors_disabled",
    ),
    Case(
        # **絵は変わらず draw だけが倍。** `SRPDefaultUnlit` は URP が既定で描くので、
        # 透過を使わないマテリアルも前方描画で 2 回描かれる。
        # 実測で 1 体は 39/46 止めてあり、別の 2 体は 0/20 だった（T-303）。
        name="使っていない重ね描きパスの代金",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/pin/Pin0.mat", "  disabledShaderPasses:\n  - SRPDEFAULTUNLIT\n", "")],
        expect="使っていない重ね描きパスの代金",
        why="**前方描画の draw が丸ごと 2 倍になる。** 絵は変わらないので"
            "フレームデバッガを開くまで分からない",
        covers="check_unused_pass_cost",
    ),
    Case(
        # **絵は 1 ピクセルも変わらないので、フレームデバッガを開くまで気付けない。**
        # キャラは SkinnedMeshRenderer なのでインスタンシングされず、
        # 宣言しても変種が 2 倍になるだけ。そのうえマテリアルの
        # Enable GPU Instancing に印が入ると SRP Batcher から外れる（T-301）。
        name="SRP Batcher を崩す宣言が戻る",
        tool=["param_check.py", "."],
        edits=[("Idol.shader", "            #pragma multi_compile_fog",
                "            #pragma multi_compile_fog\n            #pragma multi_compile_instancing")],
        expect="multi_compile_instancing",
        why="**マテリアルの数だけ定数バッファを積み直すことになる。** "
            "絵は変わらないので、測るまで気付けない",
        covers="check_srp_batcher",
    ),
    Case(
        # **逆転すると機能が丸ごと死ぬか常時発動する。** どちらも説明が付かない。
        # 組は名前の対（Min/Max・Start/End）から自動で拾うので、
        # プロパティが増減しても一覧が腐らない（T-326）。
        name="対になった値が逆転している",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/pin/Pin0.mat", "    - _Shadow2Step: 0.15",
                "    - _Shadow2Step: 0.15" + NL
                + "    - _PerspectiveRemovalStart: 90" + NL
                + "    - _PerspectiveRemovalEnd: 10")],
        expect="対になった値が逆転している",
        why="**効く範囲が消えるか、常に効きっぱなしになる。** "
            "値を個別に見ても妥当に見えるので気付けない",
        covers="check_range_pairs",
    ),
    Case(
        # **有効な環境では通ってしまう型。** URP が 0/1 で必ず定義するマクロを
        # `defined()` で見ると常に真になり、機能を切った変種でだけ落ちる。
        # 実際 Cel が Forward+ 無効の変種でコンパイルできなかった（T-315）。
        name="E014 値マクロを defined() で見ている",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl", "                UNITY_BRANCH",
                "                #if defined(USE_CLUSTER_LIGHT_LOOP)\n"
                "                #endif\n"
                "                UNITY_BRANCH")],
        expect="E014",
        why="**全キーワード組を回すまで出ない。** 手元の設定では通るので、"
            "別の環境やビルドで初めて落ちる",
        covers="E014",
    ),
    Case(
        # **印は警告を黙らせる。** 裏が無いまま付けると、
        # 「実行時 0 のまま動く」状態を**自分の手で隠す**ことになる。
        # 実際 `_DebugMode` に付いていたが、あれは普通のマテリアル値だった（T-300）。
        name="E013 script-set の印に裏が無い",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("ToonPBRCommon.hlsl", "    float  _DebugMode;",
                "    float  _NobodySetsThis;   // lint:script-set\n    float  _DebugMode;")],
        expect="E013",
        why="**誰も設定しない uniform が 0 のまま動く。** 印が警告を消しているので、"
            "検査を回しても出てこない",
        covers="E013",
    ),
    Case(
        # **`HasFloat` は綴りを間違えても false を返すだけ。**
        # 例外も警告も出ないまま、その項目が黙って何もしなくなる。
        # 実際 4 つ書いた見張りのうち 1 つが実在しない名前で、
        # **一度も鳴らない見張り**になっていた（T-299）。
        name="C# が実在しないプロパティを指す",
        tool=["param_check.py", "."],
        # **注入先は消えることがある。** 元は手書きの警戒表の 1 行を狙って
        # いたが、その表をシェーダーからの導出に置き換えた時点で
        # 注入先ごと消え、試験が「注入先が見つからない」で落ちた（T-338）。
        # 消えにくい所 ── この道具の本体である `_SurfaceType` の書き込み ── へ移す。
        edits=[("Editor/ToonPBRSurfaceTypeFromName.cs",
                'mat.SetFloat("_SurfaceType", want)',
                'mat.SetFloat("_SurfaceTypo", want)')],
        expect="C# が実在しないプロパティを指す",
        why="**綴り違いが黙って無効化される。** 動いているつもりの機能が"
            "一度も動かない",
        covers="check_cs_property_names",
    ),
    Case(
        # **複数選択でスライダーを端まで引くと、詰めた値が一度に消える。**
        # 元の値はどこにも残らないので、気付かなければそのまま。
        # 実際 `_SpecularIntensity` は移行元で 0.2 / 0.5 / 0 に分かれていたのに
        # 46 件すべてが 4（上限）になっていた ── **0 だった 8 件も含めて**（T-298）。
        name="値域の上限に張り付いている",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[(f"mats/pin/Pin{n}.mat", "- _SpecularIntensity: 0.2",
                "- _SpecularIntensity: 4") for n in range(4)],
        expect="値域の上限に張り付いている",
        why="**部位ごとに詰めた値が一度に消える。** 元の値は残らないので、"
            "気付かなければ戻せない",
        covers="check_pinned_to_max",
    ),
    Case(
        # **止まっている機能は「動いていない」ように見えない。**
        # サーフェスタイプが Default だと部位別の中身が 1 命令も走らないが、
        # 例外も警告も出ず「PBR 寄りの絵」になるだけ。実際、利用者が見ていた
        # キャラの 20 件中 17 件がこれで、髪の異方性も顔の SDF も止まったまま
        # 影のちらつきや鏡面の強さを議論していた（T-290）。
        name="サーフェスタイプが Default のまま",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/Fixture_00_HAIR.mat", "- _SurfaceType: 3", "- _SurfaceType: 0")],
        expect="サーフェスタイプが Default のまま",
        why="**髪の異方性・顔の SDF・肌の SSS・布の光沢が全部止まる。** "
            "絵は出るので、機能が動いていないことに気付けない",
        covers="check_surface_type_by_name",
    ),
    Case(
        # **未割り当ての既定色が中立とは限らない。** 同じ「テクスチャが無い」でも、
        # MatCap（黒 → 加算 0）は無駄なだけだが、ランプ（白）は**陰影が丸ごと消える**。
        # 例外も警告も出ないので、絵がおかしい原因が割り当て忘れだと気付けない。
        name="機能を有効にしたのにテクスチャが無い",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat", r"re:- _UseRampMap: [\d.]+", "- _UseRampMap: 1")],
        expect="_UseRampMap が 0 でないのに _RampMap が未割り当て",
        why="**陰影が消えてべた塗りになる。** 原因がテクスチャの割り当て忘れだと"
            "分かる手掛かりがどこにも出ない",
        covers="check_dead_gates",
    ),
    Case(
        # **未定義動作は「動いている」ように見える。** `atan2(0,0)` は
        # 手元の GPU では 0 を返すかもしれないが、規定が無いので NaN も許される。
        # 実機で初めて出る上、出方が**旋毛の一点だけ黒い**なので辿れない。
        name="atan2 が (0,0) から守られていない",
        tool=["param_check.py", "."],
        edits=[("Shading/ToonPBRSpecular.hlsl",
                "float theta = (dot(fv, fv) > 1e-12) ? (0.5 * atan2(fv.y, fv.x)) : 0.0;",
                "float theta = 0.5 * atan2(fv.y, fv.x);")],
        expect="atan2 が (0,0) から守られていない",
        why="**NaN が伝播して一点だけ黒くなる。** 開発機では再現せず、"
            "出ても原因が髪の毛流れだと分からない",
        covers="check_atan2_guard",
    ),
    Case(
        # **原因が 3 つに分かれて置いてある型。** 影マップの粒（URP アセット）と、
        # 増幅する側（マテリアル）と、細さ（メッシュ）。どれ 1 つを見ても
        # 「ちらつく」とは分からないので、人が気付けるのは**動かしたとき**だけ。
        #
        # 増幅の経路は 2 つあり、**片方を塞ぐともう片方が開く** ──
        # HQ セルフシャドウを ON にすると URP 標準の数タップ問題は消えるが、
        # 代わりに接地硬化の 8 タップ推定がばらつく。最初は前者しか見ておらず、
        # 利用者が HQ を ON にした途端に**検査が黙った**（ちらつきは続いていた）。
        name="細い影がちらつく組み合わせ",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/0.shita.mat", r"re:- _ShadowSoftness: [\d.]+",
                "- _ShadowSoftness: 0.033")],
        expect="細い影がちらつく組み合わせ",
        why="**動かさないと出ない欠陥。** 静止画では正しく見えるので、"
            "レビューも自動試験もすり抜けて実機で初めて分かる",
        covers="check_shadow_flicker",
    ),
    Case(
        # **「現在の設定」は書いた瞬間から腐る。** 実際 20 倍ずれたまま
        # 「これが現在の設定です」と言い続けていた（T-285）。
        name="「現在の設定」と書いている",
        tool=["param_check.py", "."],
        edits=[("Editor/ToonPBRPresets.cs",
                "Hue Mix 0.35。灰色に沈んでいた影に色が戻る",
                "現在の設定。灰色に沈んでいた影に色が戻る")],
        expect="「現在の設定」と書いている",
        why="値を変えても文だけ残り、**食い違ったまま断言し続ける**",
        covers="check_gui_claims",
    ),
    Case(
        # **絵が壊れないので気付けない型。** SDF を割り当てて Blend を 1 に
        # していても、Binder が無く Fallback も OFF なら faceBlend が 0 になり
        # 一切使われない。普通の陰影で出るため「効いていない」と分からない。
        name="顔の SDF が黙って無効になる",
        tool=["param_check.py", ".", "--materials", "mats"],
        # **生データに頼らない**（T-155）。もう片方のメッセージ
        # （Binder 無しでは無効）は利用者の実マテリアルが既に持ちうるが、
        # 指摘は名前でグループ化されるため**同名の 2 件目は行数を増やさない**
        # ── 生データが持たない側（Flatness 0）を Face でない 0.shita へ
        # 全条件ごと注入する。元の状態がどうであれ新しい行が必ず立つ。
        edits=[("mats/0.shita.mat",
                "  m_ValidKeywords:" + NL,
                "  m_ValidKeywords:" + NL + "  - _SURFACETYPE_FACE" + NL),
               ("mats/0.shita.mat",
                r"re:- _FaceFlatness: [\d.]+", "- _FaceFlatness: 0")],
        expect="Surface Type が Face なのに SDF を使っていない",
        why="Face を選んだ主目的（SDF の陰）が丸ごと眠る。"
            "**絵は普通に出るので効いていないと分からない**",
        covers="check_face_sdf_reachable",
    ),
    Case(
        # **1 枚のマテリアルだけでは判定できない欠陥。** 前髪透過は
        # 「眉・目がビットを書き、髪がそこを抜く」という 3 者の取り決めで、
        # 片側だけ設定しても**何も起きないまま静かに成立しない**（T-254 で出荷した）。
        # 届かないビットを要求させて、横断の突き合わせが生きているか見る。
        name="ステンシルの相手が居ない",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/14.kami_.mat", "- _StencilReadMask: 6", "- _StencilReadMask: 8"),
               ("mats/14.kami_.mat", "- _StencilRef: 0", "- _StencilRef: 8")],
        expect="相手が居ない",
        why="前髪透過が黙って成立しない。**設定した本人には効いているか判断できない**（T-254）",
        covers="check_stencil_reachability",
    ),
    Case(
        # **2 つのスライダの組み合わせでしか起きない破綻。**
        # 片方だけ見ていると気付けないので、既定値の段階で撃たせる。
        name="光を当てても明るくならない設定",
        tool=["param_check.py", "."],
        edits=[("Idol.shader",
                '_DiffuseWrap             ("  Diffuse Wrap", Range(0,1)) = 0.25',
                '_DiffuseWrap             ("  Diffuse Wrap", Range(0,1)) = 1.0')],
        expect="明るくならない",
        why="ライトを当てても影色のまま動かない。**絵からは光源側を疑うしかなく原因に辿り着けない**",
        covers="check_diffuse_reach",
    ),
    Case(
        # **多重散乱の補償。** GGX 側の物理を見る唯一の項目。
        # Filament の書き方（DFG の B 項で割る）へ「直される」のが一番怖い
        # ── 出回っている式なので善意で戻されうるが、この解析フィットでは
        # B が正面付近で 0.001 台まで落ち、誘電体の補償倍率が発散する。
        name="エネルギー補償の割る相手が変わる",
        tool=["param_check.py", "."],
        edits=[("Shading/ToonPBREnv.hlsl",
                "float  Ess  = max(AB.x + AB.y, 1e-3);",
                "float  Ess  = max(AB.y, 1e-3);")],
        expect="割る相手が Ess でなくなっている",
        why="誘電体の鏡面が白飛びする。**絵は「なんか眩しい」としか見えず原因に辿り着けない**",
        covers="check_energy_compensation",
    ),
    Case(
        name="サマリが消したものを案内し続ける",
        tool=["param_check.py", "."],
        edits=[("BACKLOG.md", "| SSAO |",
                "| 死んだ案内 | `ToonDeletedThing` をキャラのルートへ |" + NL + "| SSAO |")],
        expect="サマリが実在しないものを案内している",
        why="削除した機能の設定手順が残る。読む人は無い物を探し、環境が壊れていると読む（T-222）",
        covers="check_docs",
    ),
    Case(
        # **コンパイラの領分。** Editor が開いていて実コンパイルできない間に
        # 新しい関数を足しているので、ここが唯一の守りになる。
        name="E010 定義の無い関数を呼んでいる",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl",
                "ToonAlbedoHSV(albedo.rgb)", "ToonAlbedoHSVTypo(albedo.rgb)")],
        expect="E010",
        why="打ち間違いと、分割したファイルの include 漏れ。どちらも実コンパイルでしか出なかった",
        covers="E010",
    ),
    Case(
        name="E011 引数の数が定義と合わない",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl",
                "ToonAlbedoHSV(albedo.rgb)", "ToonAlbedoHSV(albedo.rgb, 1.0)")],
        expect="E011",
        why="引数の数の不一致。CLAUDE.md が「静的検査では見つからない」と挙げていた類",
        covers="E011",
    ),
    Case(
        # **絵に出ない無駄。** 移植元では別トグルで切っていた値が残り、
        # 値そのものがゲートのこちらでは機能が勝手に ON になる（T-257）。
        #
        # MatCap ではなく Cavity で試すのは、**MatCap は既に 46 件全部で
        # 発火している**ため ── 件数が増えないと試験にならない。
        name="効果ゼロの機能にコストだけ払う",
        tool=["param_check.py", ".", "--materials", "mats"],
        edits=[("mats/14.kami_.mat",
                "    - _CavityMap:" + NL + "        m_Texture: {fileID: 2800000",
                "    - _CavityMap:" + NL + "        m_Texture: {fileID: 0")],
        expect="_CavityMap が未割り当て",
        why="フェッチを毎画素払うのに絵は変わらない。目視でも実機でも気付けない",
        covers="check_dead_gates",
    ),
    Case(
        # 名前を振り直すと片側だけ書き換わる。実際に起きた（T-249）──
        # **例外は出ず「対象 0 件」と出るだけ**で、原因が読めない。
        name="移行元と移行先が同じシェーダー",
        tool=["param_check.py", "."],
        edits=[("Editor/ToonPBRMigrator.cs",
                '"Origuma/EasyPBR_URP/Doll"', '"Origuma/EasyToon_URP/Idol"')],
        expect="移行元と移行先が同じシェーダー",
        why="移行が成立しないのに「対象 0 件」としか出ない。移行ツールが丸ごと死ぬ",
        covers="check_migration_rules",
    ),
    Case(
        # **移植先の作法を満たしているかの記録。** 満たさなくなったことに
        # 気付けないと、移植の前提が崩れたまま進む（T-244 で 3 箇所古かった）。
        name="設計文書のパス数が古い",
        tool=["param_check.py", "."],
        edits=[("ARCHITECTURE.md", "**7 パス。**", "**8 パス。**")],
        expect="設計文書のパス数が実装と違う",
        why="パスを足しても Pass/LightMode の表に載らない。移植先で作法違反になる",
        covers="check_docs",
    ),
    Case(
        # **キーワードを 1 つ足すだけで倍になる**量なので、書き写した数字の中でも
        # 特に古くなりやすい。足した本人はそのとき数え直さない。
        name="サマリのバリアント数が古い",
        tool=["param_check.py", "."],
        edits=[("BACKLOG.md", "ForwardLit は feature 20 × system 32,768",
                "ForwardLit は feature 20 × system 16,384")],
        expect="サマリのバリアント数が実装と違う",
        why="キーワードを足しても数字が据え置かれ、コスト判断が古い前提で行われる（T-227）",
        covers="check_variants",
    ),
    Case(
        # **数字が合わない**より**主張ごと消える**方が危ない。前者は赤く出るが、
        # 後者は検算が 1 つ減っただけなので全部 OK と報告される。
        # 実際 `実コンパイル（N 組）` は文書側に一文が無く、ずっと空振りしていた。
        # ここでは数字を消さず**書式だけ崩す**。サマリを書き直したときに
        # 起きるのはこの形（文言は残るがパターンに一致しない）だから。
        name="サマリから主張が消えている",
        tool=["param_check.py", "."],
        edits=[("BACKLOG.md", "実コンパイル（56 組）", "実コンパイル（多数）")],
        expect="サマリに主張が無い",
        why="比べる相手を失った検算は黙って通る。検査が減ったことに気づけない（T-329）",
        covers="check_docs",
    ),
    Case(
        # 手作業で 2 回見つけて、そのたびにバリアントが半減した種類の無駄。
        # 見つけ方が「気になったので数えてみた」だったので、次は見逃す。
        name="W109 使われないキーワードの宣言",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Idol.shader",
                "            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP",
                "            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP" + NL
                + "            #pragma multi_compile _ _NOBODY_USES_THIS")],
        expect="W109",
        why="コードが同一のバリアントが倍に増える。絵は変わらないので実機でも気付けない",
        covers="W109",
    ),
    Case(
        name="E012 引数の成分数が足りない",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl",
                "ToonAlbedoHSV(albedo.rgb)", "ToonAlbedoHSV(albedo.rg)")],
        expect="E012",
        why="足りない側は暗黙変換されない＝コンパイルエラー。型の誤りを静的に撃つ唯一の検査",
        covers="E012",
    ),
    Case(
        # **こちらはコンパイルが通る。** 通るぶんだけ気付きにくい。
        name="W108 引数の成分数が多い（切り捨て）",
        tool=["shader_lint.py", ".", "--strict"],
        edits=[("Passes/ForwardPass.hlsl",
                "ToonAlbedoHSV(albedo.rgb)", "ToonAlbedoHSV(albedo.rgba)")],
        expect="W108",
        why="黙って切り捨てられる。コンパイルは通るので実機でも気付けない",
        covers="W108",
    ),
    # ── Editor ログの検査（batchmode が塞がれているときの唯一のコンパイル証拠）
    Case(
        # **種別をまたいで隠されないこと。** シェーダーを取り込み直しても
        # C# のエラーは何も直らない。1 本の区切りで判定していたせいで、
        # 本物の `error CS1010` を握り潰した（T-231）。
        name="L001 C# のエラーがシェーダー取り込みに隠されない",
        tool=["editor_log_check.py", ".", "--log", "logs/Editor.log"],
        edits=[("logs/Editor.log",
                "Start importing Assets/ToonPBR/ToonPBR.shader",
                "Assets/ToonPBR/Editor/X.cs(1,1): error CS1010: Newline in constant" + NL
                + "Start importing Assets/ToonPBR/ToonPBR.shader")],
        expect="[L001]",
        why="C# のコンパイルエラーを「古い」と誤判定して握り潰す（実際に踏んだ）",
        covers="L001",
    ),
    Case(
        name="L001 取り込み後のエラーを現行として拾う",
        tool=["editor_log_check.py", ".", "--log", "logs/Editor.log"],
        edits=[("logs/Editor.log",
                "Refreshing native plugins compatible for Editor",
                "Shader error in 'Toon/URP/CharacterPBR': undeclared identifier '_Foo' at Assets/ToonPBR/ToonPBR.shader(100) (on d3d11)")],
        expect="[L001]",
        why="Editor が出している本物のコンパイルエラーを見落とす（今これが唯一の証拠）",
        covers="L001",
    ),
    Case(
        name="L002 取り込み前のエラーを古いとして除外する",
        tool=["editor_log_check.py", ".", "--log", "logs/Editor.log"],
        edits=[("logs/Editor.log",
                "Start importing Assets/ToonPBR/ToonPBR.shader using Guid(abc) (ShaderImporter) -> (artifact id: 'x') in 0.46 seconds",
                "Shader error in 'Toon/URP/CharacterPBR': undeclared identifier '_Foo' at Assets/ToonPBR/ToonPBR.shader(100) (on d3d11)" + NL + "Start importing Assets/ToonPBR/ToonPBR.shader using Guid(abc) (ShaderImporter) -> (artifact id: 'x') in 0.46 seconds")],
        expect="[L002]",
        why="何時間も前に直したエラーで足を止める。ログは追記され続ける",
        covers="L002",
    ),
    Case(
        name="L003 取り込みの記録が無いことに気付く",
        tool=["editor_log_check.py", ".", "--log", "logs/Editor.log"],
        edits=[("logs/Editor.log",
                "Assets/ToonPBR/ToonPBR.shader",
                "Assets/Other/Other.shader")],
        expect="[L003]",
        why="別プロジェクトのログを読んで「エラー 0 件」と報告する",
        covers="L003",
    ),
    Case(
        name="L004 ログがソースより古いとき未検証と言う",
        tool=["editor_log_check.py", ".", "--log", "logs/Editor.log"],
        edits=[],
        mtime=("logs/Editor.log", 946684800.0),   # 2000-01-01
        expect="[L004]",
        why="**まだコンパイルしていないのを合格と読む。** この道具で一番危ない誤り",
        covers="L004",
    ),
]

def coverage_report() -> list[str]:
    """**どの検査が試験されていないか**をソースから数えて出す。

    「自己診断が通った ＝ 検査は全部生きている」と読まれるのが一番危ない。
    静的検査の合格をコンパイルの合格と読み替えるのと同じ過大解釈で、
    このプロジェクトは**それで実際に2回取りこぼしている**（T-072 / T-085）。

    一覧は**ソースから数える**。ここに検査名を書き写すと、
    検査を足したときに古くなる（T-167 / T-168 で2回踏んだ形）。
    """
    lint = (HERE / "shader_lint.py").read_text(encoding="utf-8", errors="replace")
    param = (HERE / "param_check.py").read_text(encoding="utf-8", errors="replace")

    import re
    all_checks = set(re.findall(r'Issue\(\s*"([EW]\d{3})"', lint))
    all_checks |= set(re.findall(r"^def (check_\w+)", param, re.MULTILINE))
    # Editor ログの検査も同じ扱いにする。**一覧はあちらの CODES から数える**
    # ── ここに書き写すと、検査を足したときに古くなる
    elog = (HERE / "editor_log_check.py").read_text(encoding="utf-8", errors="replace")
    all_checks |= set(re.findall(r'^\s*"(L\d{3})":', elog, re.MULTILINE))

    # **UNITS も数えること。** 注入では試せない検査（実プロジェクトを要るもの）は
    # 関数を直接撃つ形で試験しているが、そこを数えないと
    # **試験済みのものを「未試験」と報告する** ── 逆向きの誤報で、
    # 「カバー率が上がらない」と読まれて余計な作業を呼ぶ（T-281）。
    covered = {c.covers for c in CASES} | {u[3] for u in UNITS if len(u) > 3}
    return sorted(all_checks - covered)


def _source_parts() -> dict[str, Path | None]:
    """本物のツリーの部品の場所。**平坦でも分かれていても見つける。**

    目印は `ToonPBRCommon.hlsl` ── シェーダーの置き場はここ。
    C# は「シェーダーのフォルダ名と同じ名前の部屋」で探す
    （`Shaders/Idol/` に対して `Editor/Idol/` と `Runtime/Scripts/Idol/`）。
    単にパッケージルートを見ると**隣のシェーダーのスクリプトまで拾う。**
    """
    base = HERE.parent if HERE.name == "Documentation~" else HERE
    common = next(iter(sorted(base.rglob("ToonPBRCommon.hlsl"))), None)
    shaders = common.parent if common else HERE
    name = shaders.name

    editor = runtime = None
    for parent in [*shaders.parents]:
        if (parent / "package.json").exists():
            for d in parent.rglob(name):
                if not d.is_dir() or d == shaders:
                    continue
                if "Editor" in d.parts:
                    editor = d
                elif "Runtime" in d.parts:
                    runtime = d
            break
    if editor is None and (shaders / "Editor").is_dir():
        editor = shaders / "Editor"
    if runtime is None and (shaders / "Runtime").is_dir():
        runtime = shaders / "Runtime"
    return {"shaders": shaders, "editor": editor, "runtime": runtime, "docs": HERE}


def build_sandbox(tmp: Path) -> Path:
    """ToonPBR 一式と .mat を複製する。**本物には触らない。**

    `Assets/ToonPBR` の形にするのは `check_render_settings` のため
    ── あの検査は `root.parent` が `Assets` であることを前提にしている
    （前提を外したまま書いて**黙って空振りした**のが T-174）。
    """
    assets = tmp / "Assets"
    root = assets / "ToonPBR"

    # **本物のツリーは分かれている。** パッケージへ移すと
    # シェーダーは `Runtime/Shaders/Idol/`、C# は `Editor/Idol/` と
    # `Runtime/Scripts/Idol/`、道具と文書は `Documentation~/` にある。
    #
    # サンドボックスは**平坦に組み直す** ── 注入の宛先（`Passes/ForwardPass.hlsl`
    # など）を全部書き換えるより、集める側で吸収するほうが壊れにくい。
    # 道具は平坦・分割のどちらでも動くので、これで検査の意味は変わらない。
    #
    # **`HERE` を丸ごと写す作りだった。** 道具が `Documentation~/` へ移った瞬間、
    # シェーダーの無いサンドボックスができて 49 / 53 が落ちた（T-252）。
    # 幸い**黙って通らず大声で落ちた** ── 注入先が無いことを試験が言う設計。
    root.mkdir(parents=True, exist_ok=True)
    # **`Packages` を空でも置く。** プロジェクトのルートは
    # 「`Assets` と `Packages` を両方持つ場所」で探すので、これが無いと
    # 探索がサンドボックスを通り越して**本物のプロジェクト**に当たる。
    # そうなると試験の結果が利用者の設定次第で変わる。
    (tmp / "Packages").mkdir(exist_ok=True)

    # **URP の「0/1 で定義するマクロ」を最小限だけ置く。**
    # `E014` はその一覧を URP 本体から集めるので、`Library/PackageCache` が
    # 無いと**集合が空になり、検査が黙って何もしない。**
    # 実物を写すのではなく、判定に必要な形だけを作る。
    lib = tmp / "Library" / "PackageCache" / "com.unity.render-pipelines.universal@t" / "ShaderLibrary"
    lib.mkdir(parents=True, exist_ok=True)
    (lib / "Core.hlsl").write_text(
        "#if defined(_CLUSTER_LIGHT_LOOP)" + NL
        + "#define USE_CLUSTER_LIGHT_LOOP 1" + NL
        + "#else" + NL
        + "#define USE_CLUSTER_LIGHT_LOOP 0" + NL
        + "#endif" + NL,
        encoding="utf-8", newline="")
    for part, dst in _source_parts().items():
        if dst is None:
            continue
        if part == "shaders":
            shutil.copytree(dst, root, dirs_exist_ok=True,
                            ignore=shutil.ignore_patterns("__pycache__"))
        elif part == "docs":
            for f in list(dst.glob("*.md")) + list(dst.glob("*.py")):
                shutil.copy2(f, root / f.name)
        else:
            (root / part.capitalize()).mkdir(exist_ok=True)
            for f in dst.glob("*.cs"):
                shutil.copy2(f, root / part.capitalize() / f.name)
    if MATERIALS.is_dir():
        shutil.copytree(MATERIALS, root / "mats")
        write_render_fixtures(assets, root / "mats")

    # **移行元のシェーダーは本物を複製する。**
    # 作り物にすると対応表へ 1 行足すたびに古くなり、
    # 「試験のほうが先に壊れる」形になる（T-173 で踏んだ）。
    # ファイルは 2 つだけなので複製が安い。
    for rel in PACKAGE_SHADERS:
        src = PACKAGES / rel
        if not src.exists():
            continue
        dst = tmp / "Packages" / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)

    write_log_fixture(root)
    return root


def write_log_fixture(root: Path) -> None:
    """Editor ログの作り物。**エラーが 1 件も無い状態**を基準にする。

    本物のログ（100KB・履歴数時間ぶん）を複製しないのは、
    そこに何が残っているかが実行するたびに変わるから ──
    件数比較の試験にならない。見たいのは「注入で増えるか」だけなので、
    取り込みが 1 行あるだけの最小のログで足りる。

    **更新時刻を未来に置く。** 他の項目がソースを書き換えると
    その時点で mtime が「今」になり、ログのほうが古くなってしまう。
    そうなると鮮度の判定（L004）が全項目で発火して、
    **どの項目も基準が「未検証」になり比較が壊れる**（実際そう書いて踏んだ）。
    """
    logs = root / "logs"
    logs.mkdir(exist_ok=True)
    log = logs / "Editor.log"
    log.write_text(
        "Start importing Assets/ToonPBR/ToonPBR.shader using Guid(abc)"
        " (ShaderImporter) -> (artifact id: 'x') in 0.46 seconds" + NL
        + "Refreshing native plugins compatible for Editor" + NL,
        encoding="utf-8", newline="")
    future = time.time() + 86400
    os.utime(log, (future, future))


def write_render_fixtures(assets: Path, mats: Path) -> None:
    """描画設定の検査用に、**最小限の作り物**を置く。

    本物のシーンとプレハブを複製しないのは、プロジェクトの構成に依存すると
    試験のほうが先に壊れるから。検査が見ているのは
    「URP アセットの MSAA」「マテリアル → プレハブ → シーンの参照」
    「カメラの Anti-aliasing」の3つだけなので、それだけを作る。

    既定は **MSAA 有効**にしておく ── 注入で無効にしたときに
    指摘が「増える」形にしないと、件数比較の試験にならない。
    """
    import re as _re

    meta = next(iter(sorted(mats.glob("*.mat.meta"))), None)
    if meta is None:
        return
    m = _re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(encoding="utf-8"),
                   _re.MULTILINE)
    if not m:
        return
    mat_guid = m.group(1)
    prefab_guid = "1" * 32

    (assets / "Settings").mkdir(parents=True, exist_ok=True)
    # 影の粒度も置く。`check_shadow_flicker` は**実寸を URP アセットから計算する**
    # ので、これが無いと検査が黙って早期リターンし、試験は「発火しない」を
    # 検査の死と区別できない。
    # 4096 / 影距離 50m / 1 カスケード → 1 テクセル ≒ 17mm（毛束は 1 テクセル未満）。
    (assets / "Settings" / "Fixture_RPAsset.asset").write_text(
        "%YAML 1.1" + NL + "MonoBehaviour:" + NL
        + "  m_MSAA: 4" + NL + "  m_RenderScale: 1" + NL
        + "  m_MainLightShadowmapResolution: 4096" + NL
        + "  m_ShadowDistance: 50" + NL
        + "  m_ShadowCascadeCount: 1" + NL
        # 既定は**作る**側にしておく ── 注入で 0 に落としたときに
        # 指摘が「増える」形にしないと、件数比較の試験にならない。
        + "  m_RequireDepthTexture: 1" + NL,
        encoding="utf-8", newline="")

    # **部位名を持つマテリアルを 1 枚置く。**
    # 移行元の 46 枚は `0.shita` のような名前で、部位を大文字トークンで
    # 持っていない ── そのままでは `check_surface_type_by_name` を試せない。
    # **正しい状態（Hair = 3）で置く**ので、注入で 0 に落としたときだけ発火する。
    #
    # **シェーダーの GUID を持たせること。** 検算はマテリアルを
    # シェーダーの GUID で絞る（隣のシェーダーのものを巻き込まないため）ので、
    # GUID の無い作り物は**黙って対象から外れる** ── 一度これで
    # 「注入しても増えない」になった。
    shader_guid = ""
    for meta in sorted(assets.rglob("*.shader.meta")):
        m = _re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(encoding="utf-8"), _re.M)
        if m:
            shader_guid = m.group(1)
            break
    # **「全件が上限」を試すには、フォルダ 1 つぶんの材料が要る。**
    # `check_pinned_to_max` はキャラ単位（フォルダ単位）で見るので、
    # 1 枚だけ書き換えても条件が揃わない。専用の小さなフォルダを作る。
    # **アルファの器が無い画像を 1 枚置く。**
    # `check_alpha_clip_without_alpha` は guid からファイルを引いて
    # ヘッダを読むので、サンドボックスに画像が 1 枚も無いと
    # **索引が空になり、検査が黙って何もしない。**
    # 24bit・無圧縮の最小 TGA（ヘッダ 18 バイト + 1 画素）。
    tga_guid = "cafe" * 8
    (assets / "Fixture_NoAlpha.tga").write_bytes(
        bytes([0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 24, 0]) + bytes([255, 255, 255]))
    (assets / "Fixture_NoAlpha.tga.meta").write_text(
        "fileFormatVersion: 2" + NL + "guid: " + tga_guid + NL,
        encoding="utf-8", newline="")

    pin = mats / "pin"
    pin.mkdir(exist_ok=True)
    for n in range(4):
        (pin / f"Pin{n}.mat").write_text(
            "%YAML 1.1" + NL + "--- !u!21 &2100000" + NL + "Material:" + NL
            + f"  m_Name: Pin{n}" + NL
            + "  m_Shader: {fileID: 4800000, guid: " + shader_guid + ", type: 3}" + NL
            # **既定は「止めてある」側にしておく。** 注入で外したときに
            # 指摘が増える形にしないと、件数比較の試験にならない。
            + "  disabledShaderPasses:" + NL
            + "  - SRPDEFAULTUNLIT" + NL
            + "  m_SavedProperties:" + NL
            + "    m_TexEnvs:" + NL
            + "    - _BaseMap:" + NL
            + "        m_Texture: {fileID: 2800000, guid: " + tga_guid + ", type: 3}" + NL
            + "    m_Floats:" + NL
            + "    - _Cutoff: 0.5" + NL
            + "    - _SpecularIntensity: 0.2" + NL
            # 2 段影の関係を試すぶん（`check_cel_bands`）。
            # **正しい向き**で置く ── 注入で逆転させたときに増える形にする。
            + "    - _ToonStep: 0.5" + NL
            + "    - _Shadow2Step: 0.15" + NL
            + "    m_Colors:" + NL
            + "    - _Shadow2Color: {r: 0.4, g: 0.3, b: 0.4, a: 1}" + NL,
            encoding="utf-8", newline="")

    (mats / "Fixture_00_HAIR.mat").write_text(
        "%YAML 1.1" + NL + "--- !u!21 &2100000" + NL + "Material:" + NL
        + "  m_Name: Fixture_00_HAIR" + NL
        + "  m_Shader: {fileID: 4800000, guid: " + shader_guid + ", type: 3}" + NL
        + "  m_SavedProperties:" + NL
        + "    m_Floats:" + NL
        + "    - _SurfaceType: 3" + NL,
        encoding="utf-8", newline="")

    (assets / "Fixture.prefab").write_text(
        "Renderer:" + NL
        + "  m_Materials:" + NL
        + "  - {fileID: 2100000, guid: " + mat_guid + ", type: 2}" + NL,
        encoding="utf-8", newline="")
    (assets / "Fixture.prefab.meta").write_text(
        "fileFormatVersion: 2" + NL + "guid: " + prefab_guid + NL,
        encoding="utf-8", newline="")

    (assets / "Fixture.unity").write_text(
        "Camera:" + NL + "  m_Antialiasing: 0" + NL
        + "PrefabInstance:" + NL
        + "  m_SourcePrefab: {fileID: 100100000, guid: " + prefab_guid
        + ", type: 3}" + NL,
        encoding="utf-8", newline="")

    # **`_MaskMap` が指すテクスチャの `.meta` を用意する。**
    # `check_maskmap_packing` は guid からファイル名を引いて
    # 「単一チャンネルの焼き上がりか」を名前で判定する。
    # テクスチャ本体は要らない ── 見ているのは名前だけ。
    # これが無いと索引が空になり、**検査が黙って早期リターンする。**
    for mat in sorted(mats.glob("*.mat")):
        m = _re.search(r"- _MaskMap:" + NL + r"[ \t]*m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})",
                       mat.read_text(encoding="utf-8", errors="replace"))
        if not m:
            continue
        (assets / f"{mat.stem}_AO.png.meta").write_text(
            "fileFormatVersion: 2" + NL + "guid: " + m.group(1) + NL,
            encoding="utf-8", newline="")


def run_tool(root: Path, tool: list[str]) -> str:
    proc = subprocess.run([sys.executable, *tool], cwd=root,
                          capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    return (proc.stdout or "") + (proc.stderr or "")


def _matches(find: str, text: str) -> bool:
    """`re:` 始まりなら正規表現として探す。"""
    if find.startswith("re:"):
        return re.search(find[3:], text) is not None
    return find in text


def _apply(find: str, repl: str, text: str) -> str:
    if find.startswith("re:"):
        return re.sub(find[3:], repl, text, count=1)
    return text.replace(find, repl, 1)


# 注入前の出力を道具ごとに 1 回だけ取る（後片付けは必ず走るので使い回せる）。
_BASELINE: dict[tuple, str] = {}


def run_case(root: Path, case: Case) -> tuple[bool, str]:
    """1件ぶん。戻り値は (通ったか, 通らなかった理由)。"""
    originals: dict[Path, str] = {}

    for rel, find, _ in case.edits:
        path = root / rel
        if not path.exists():
            return False, f"対象が無い: {rel}"

        text = path.read_text(encoding="utf-8", errors="replace")
        originals.setdefault(path, text)

        # **注入する文字列が実在することを先に確かめる。**
        # 見つからないまま「発火しなかった」と報告すると、検査が死んでいるのか
        # 試験が古いのかが混ざる ── この試験が防ごうとしている混同そのもの。
        if not _matches(find, text):
            return False, f"注入先が見つからない: {find[:44]!r}"

    # **件数で比べること。** 最初は「注入前に出ていないこと」を条件にしていたが、
    # `13.mekage` のように**本物の欠陥が既にある**検査では注入前から出ていて
    # 試験にならなかった（T-170）。見たいのは「注入で増えるか」。
    # **注入前の状態は項目ごとに変わらない。**
    # 後片付けは `finally` で必ず走るので、同じ道具・同じ引数なら
    # 出力も同じ ── なのに 88 項目ぶん回し直していた。
    # 道具の組み合わせは 10 種ほどしかないので、そこを共有する。
    key = tuple(case.tool)
    if key not in _BASELINE:
        _BASELINE[key] = run_tool(root, case.tool)
    before = _BASELINE[key].count(case.expect)

    saved_mtime: tuple[Path, float] | None = None
    try:
        for rel, find, repl in case.edits:
            path = root / rel
            path.write_text(
                _apply(find, repl,
                       path.read_text(encoding="utf-8", errors="replace")),
                encoding="utf-8", newline="")
        if case.mtime:
            rel, when = case.mtime
            path = root / rel
            if not path.exists():
                return False, f"時刻を変える対象が無い: {rel}"
            saved_mtime = (path, path.stat().st_mtime)
            os.utime(path, (when, when))
        after = run_tool(root, case.tool).count(case.expect)
    finally:
        for path, text in originals.items():
            path.write_text(text, encoding="utf-8", newline="")
        # **時刻も必ず戻す。** 戻し忘れると後続の項目が
        # 「ログが古い」状態で走り、原因の分からない失敗になる。
        if saved_mtime:
            os.utime(saved_mtime[0], (saved_mtime[1], saved_mtime[1]))

    if after > before:
        return True, ""
    return False, f"注入しても '{case.expect}' が増えない（前 {before} 件 → 後 {after} 件）"


def unit_csharp_message_filter() -> str:
    """`csharp_compile.parse_messages` が指摘を捨てていないか。

    **文字列注入では試せない検査がある。** `csharp_compile.py` は実プロジェクトの
    `Library/Bee` を要るので、サンドボックスでは動かない。だから長いあいだ
    自己診断の対象外で、**3 回続けて偽の合格を出した**（T-257）:

      1. パッケージへ移したら `Assembly-CSharp*.rsp` を名指しで探していて 0 件
      2. 応答ファイルの表記（プロジェクト相対）と絶対パスを突き合わせて 0 件
      3. csc の出力も相対で来るのに絶対パスだけで絞り、**全部捨てて 0 件**

    どれも「11 ファイル / エラー 0 件」の顔をしていた。ここは道具の中の関数を
    直接撃つ ── サンドボックスを介さないぶん、置き場所が変わっても腐らない。

    戻り値は失敗の理由。空文字なら通過。
    """
    sys.path.insert(0, str(HERE))
    try:
        import csharp_compile
    except Exception as e:                      # noqa: BLE001
        return f"csharp_compile を読み込めない: {e}"

    rel = "Packages/com.origuma.easytoon-urp/Editor/Idol/ToonPBRShaderGUI.cs"
    abs_ = "C:/UnityProjects/x/" + rel
    targets_rel, targets_abs = {rel}, {abs_}

    # csc は応答ファイルと同じ表記で出す。相対・絶対の**どちらでも**拾えること。
    for label, line in (
        ("相対", rf"{rel.replace('/', chr(92))}(12,3): error CS1061: nope"),
        ("絶対", rf"{abs_.replace('/', chr(92))}(12,3): error CS1061: nope"),
    ):
        errs, _ = csharp_compile.parse_messages([line], targets_abs, targets_rel, True)
        if len(errs) != 1:
            return f"{label}パスの error を落としている（{len(errs)} 件）"

    # 逆に、隣のシェーダーの指摘は拾わないこと（絞りが効いていること）。
    other = r"Packages\com.origuma.easytoon-urp\Editor\CelShaderGUI.cs(1,1): error CS0000: x"
    errs, _ = csharp_compile.parse_messages([other], targets_abs, targets_rel, True)
    if errs:
        return "対象外のファイルの error まで拾っている（絞りが効いていない）"
    return ""


def unit_hlsl_zero_program() -> str:
    """`hlsl_compile.verdict` が「0 プログラム」を合格にしていないか。

    `csharp_compile.py` と同じ立場の道具（実プロジェクトの `Library/PackageCache` を
    要るのでサンドボックスに載せられない）。実際に同じ穴が開いていた ──
    シェーダーの `#pragma vertex` / `#pragma fragment` を読めなくなると入口が
    None になり、**1 つもコンパイルしないまま `0 プログラム中 0 成功` で exit 0**
    を返していた（T-258）。`check.py` のまとめでは `OK` と並ぶ。

    戻り値は失敗の理由。空文字なら通過。
    """
    sys.path.insert(0, str(HERE))
    try:
        import hlsl_compile
    except Exception as e:                      # noqa: BLE001
        return f"hlsl_compile を読み込めない: {e}"

    code, why = hlsl_compile.verdict(0, 0)
    if code == 0:
        return "0 プログラムを合格にしている（1 つもコンパイルせずに OK を返す）"
    if not why:
        return "0 プログラムで落とすが理由を言わない（読む人が原因に辿り着けない）"
    if hlsl_compile.verdict(16, 1)[0] == 0:
        return "失敗があるのに合格を返している"
    if hlsl_compile.verdict(16, 0)[0] != 0:
        return "全部成功しているのに合格を返さない"
    return ""


def unit_x4000_fold() -> str:
    """既知の X4000 だけを畳み、**それ以外は通す**か。

    **砂場に Cel のツリーが無い**ので実コンパイルでは試せない（同 845 行）。
    畳む判断は `x4000_known` 1 つに閉じているので、そこを直接叩く。

    危ないのは畳みすぎる方向 ── 本当に未初期化のローカルまで消すと、
    絵にゴミが出ているのに検査は静かなままになる。
    """
    sys.path.insert(0, str(HERE))
    try:
        import hlsl_compile
    except Exception as e:                      # noqa: BLE001
        return f"hlsl_compile を読み込めない: {e}"

    art = ("warning: [ForwardLit / 既定 / ps_5_0] ../CelLighting.hlsl(37,5): "
           "warning X4000: use of potentially uninitialized variable (litMask)")
    if hlsl_compile.x4000_known(art) != "litMask":
        return "既知の X4000 を畳めていない（毎回出る警告は要約を読ませなくする）"

    real = ("warning: [ForwardLit / 既定 / ps_5_0] ../CelLighting.hlsl(42,48): "
            "warning X4000: use of potentially uninitialized variable (zzzUninit)")
    if hlsl_compile.x4000_known(real) is not None:
        return "表に無い名前まで畳んでいる（本物の未初期化が消える）"

    if hlsl_compile.x4000_known("warning X3206: implicit truncation") is not None:
        return "X4000 以外まで畳んでいる"

    if not hlsl_compile.X4000_KNOWN_OK:
        return "許容表が空（畳む対象が消えたなら判定ごと外すこと）"
    return ""


def unit_shipping_combos() -> str:
    """毎回の実コンパイルが**出荷している構成**を通しているか。

    長らく「既定」= キーワード無しの 1 組だけを回していた。だが PC 用レンダラは
    Forward+ なので、**誰も使わない構成だけを検証して「成功」と言っていた**
    ── そこでしか出ない欠陥を実際に 1 つ見逃した（T-333）。

    **砂場では試せない。** 砂場にレンダラの `.asset` は無い。
    表そのものと、キーワードが本当にシェーダーに在るかを見る。
    URP は `_FORWARD_PLUS` → `_CLUSTER_LIGHT_LOOP` と改名した実績があり、
    追随し損ねると導出は通ったまま**既定と同じものを回す**形になる。
    """
    sys.path.insert(0, str(HERE))
    try:
        import hlsl_compile
    except Exception as e:                      # noqa: BLE001
        return f"hlsl_compile を読み込めない: {e}"

    table = getattr(hlsl_compile, "RENDERING_MODE_KEYWORDS", None)
    if not table:
        return "描画経路の表が無い（出荷構成を導けない）"
    kws = {k for _, ks in table.values() for k in ks}
    if not kws:
        return "どの経路にもキーワードが無い（既定と見分けが付かない）"

    # 表のキーワードがパッケージのシェーダーに実在するか。
    pkg = next((p for p in HERE.resolve().parents
                if (p / "package.json").exists()), None)
    if pkg is None:
        return ""                               # パッケージ外なら見ない
    src = "".join(f.read_text(encoding="utf-8", errors="replace")
                  for pat in ("*.shader", "*.hlsl") for f in pkg.rglob(pat))
    missing = sorted(k for k in kws if k not in src)
    if missing:
        return ("出荷構成のキーワードがシェーダーに無い: " + ", ".join(missing)
                + "（導出は通るのに既定と同じものを回す）")
    return ""


def unit_renderer_parity() -> str:
    """レンダラごとに Feature の顔ぶれが違う状態を見つけるか。

    **砂場では試せない。** 砂場にレンダラの `.asset` は無い。
    プロジェクトの形（`Assets` と `Packages` を持つ階層）を作り物で組む。

    見たいのは 2 方向:
      - 片方にしか入っていない → 言う
      - **どちらにも入っていない → 言わない**（使っていないだけかもしれない。
        そちらはトグルと突き合わせる `check_feature_installed` の領分）
    """
    sys.path.insert(0, str(HERE))
    try:
        import param_check
    except Exception as e:                      # noqa: BLE001
        return f"param_check を読み込めない: {e}"

    guid_a, guid_b = "a" * 32, "b" * 32
    with tempfile.TemporaryDirectory(prefix="parity_") as td:
        proj = Path(td)
        pkg = proj / "Packages" / "dummy" / "Runtime"
        pkg.mkdir(parents=True)
        (proj / "Assets").mkdir()
        for name, guid in (("AlphaFeature", guid_a), ("BetaFeature", guid_b)):
            (pkg / f"{name}.cs").write_text(
                "class X : ScriptableRendererFeature {}", encoding="utf-8")
            (pkg / f"{name}.cs.meta").write_text(
                f"guid: {guid}" + NL, encoding="utf-8")

        def renderer(name: str, guids: list[str]) -> None:
            body = "m_RenderingMode: 0" + NL + NL.join(
                f"  m_Script: {{fileID: 11500000, guid: {g}}}" for g in guids)
            (proj / "Assets" / f"{name}.asset").write_text(body, encoding="utf-8")

        # 片方にだけ入っている
        renderer("PC_Renderer", [guid_a])
        renderer("Mobile_Renderer", [])
        got = param_check.check_renderer_feature_parity(pkg)
        if not any("顔ぶれ" in f.title for f in got):
            return "片方にしか入っていない Feature を見つけられない"
        if "AlphaFeature" not in " ".join(f.detail for f in got):
            return "どの Feature が足りないかを言っていない"

        # どちらにも入っていない → 言わない
        renderer("PC_Renderer", [])
        if param_check.check_renderer_feature_parity(pkg):
            return "どちらにも無いものまで言っている（使っていないだけかもしれない）"

        # 両方に入っている → 言わない
        renderer("PC_Renderer", [guid_a, guid_b])
        renderer("Mobile_Renderer", [guid_a, guid_b])
        if param_check.check_renderer_feature_parity(pkg):
            return "揃っているのに言っている"
    return ""


def unit_asmdef_dependency() -> str:
    """`_check_asmdef_deps` が「不在時に守られていない参照」を見つけるか。

    **サンドボックスでは試せない。** 自己診断の複製はシェーダーと C# を
    平坦に並べたもので、`package.json` も `asmdef` も持っていない
    （検査は package.json を見つけられず何もせず返る）。
    そこで**偽のパッケージ 2 つを一時ディレクトリに組んで**直接撃つ。

    見つけたい欠陥: 他パッケージのアセンブリを参照しているのに、
    そのパッケージが無いときに除外される作りになっていない状態。
    **コンパイルエラーになるとドメインリロードが完了せず、
    `InitializeOnLoadMethod` の自動インストーラが走らない。**

    **`dependencies` に書いてあれば良い、ではない。** git URL で配る
    パッケージは書けない（UPM がレジストリ解決に失敗する）ので、
    `versionDefines` + `defineConstraints` の形も通すこと ── 最初
    「宣言が無い」だけを見て、正しく組まれた実装に誤検出を出した。

    戻り値は失敗の理由。空文字なら通過。
    """
    import json as _json

    sys.path.insert(0, str(HERE))
    try:
        import param_check
    except Exception as e:                      # noqa: BLE001
        return f"param_check を読み込めない: {e}"

    def build(tmp: Path, how: str) -> Path:
        """how: 'bare'（守り無し） / 'declared'（dependencies） / 'guarded'（定義制約）"""
        pkgs = tmp / "Packages"
        core = pkgs / "com.example.core"
        main = pkgs / "com.example.main"
        for d in (core / "Editor", main / "Editor", main / "Runtime" / "Shaders" / "X"):
            d.mkdir(parents=True, exist_ok=True)
        (core / "package.json").write_text(
            _json.dumps({"name": "com.example.core", "version": "1.0.0"}),
            encoding="utf-8")
        (core / "Editor" / "Core.Editor.asmdef").write_text(
            _json.dumps({"name": "Example.Core.Editor"}), encoding="utf-8")

        (main / "package.json").write_text(
            _json.dumps({"name": "com.example.main", "version": "1.0.0",
                         "dependencies": ({"com.example.core": "1.0.0"}
                                          if how == "declared" else {})}),
            encoding="utf-8")
        asm = {"name": "Example.Main.Editor",
               "references": ["Example.Core.Editor"]}
        if how == "guarded":
            asm["defineConstraints"] = ["CORE_PRESENT"]
            asm["versionDefines"] = [{"name": "com.example.core",
                                      "expression": "", "define": "CORE_PRESENT"}]
        (main / "Editor" / "Main.Editor.asmdef").write_text(
            _json.dumps(asm), encoding="utf-8")
        return main / "Runtime" / "Shaders" / "X"

    def hits(how: str, tmp: Path) -> bool:
        found = param_check._check_asmdef_deps(build(tmp / how, how))
        return any("com.example.core" in f.title for f in found)

    with tempfile.TemporaryDirectory(prefix="asmdefprobe_") as td:
        tmp = Path(td)
        if not hits("bare", tmp):
            return "守り無しの参照を見つけられない（Core 不在でコンパイルが落ちる状態を通す）"
        if hits("declared", tmp):
            return "dependencies に書いてあるのに指摘する（誤検出）"
        if hits("guarded", tmp):
            return ("versionDefines + defineConstraints で守ってあるのに指摘する"
                    "（git URL 配布の正しい形を誤検出する）")
    return ""


def unit_feature_installed() -> str:
    """`check_feature_installed` が「Feature が入っていない」を見つけるか。

    **サンドボックスでは試せない。** この検査は実プロジェクトの
    `Assets/` を走査して Renderer Data を探すので、複製の中では
    `project` が見つからず何もせずに返る。偽のプロジェクトを組んで直接撃つ。

    見つけたい欠陥: マテリアルが `_OutlineOn` を立てているのに、
    それを描く Renderer Feature がどの `.asset` にも入っていない状態。
    **キーワードは立ち、シェーダーもコンパイルされ、絵だけが変わらない。**

    戻り値は失敗の理由。空文字なら通過。
    """
    sys.path.insert(0, str(HERE))
    try:
        import param_check
    except Exception as e:                      # noqa: BLE001
        return f"param_check を読み込めない: {e}"

    GUID = "0123456789abcdef0123456789abcdef"

    def build(tmp: Path, installed: bool) -> tuple[Path, Path]:
        proj = tmp
        assets = proj / "Assets"
        pkg = proj / "Packages" / "com.example.pkg"
        shaders = pkg / "Runtime" / "Shaders" / "X"
        scripts = pkg / "Runtime" / "Scripts" / "X"
        mats = assets / "Mats"
        for d in (assets / "Settings", shaders, scripts, mats):
            d.mkdir(parents=True, exist_ok=True)

        (pkg / "package.json").write_text('{"name":"com.example.pkg"}', encoding="utf-8")
        (shaders / "ToonPBRCommon.hlsl").write_text("// dummy", encoding="utf-8")
        (scripts / "ToonOutlineFeature.cs").write_text("// dummy", encoding="utf-8")
        (scripts / "ToonOutlineFeature.cs.meta").write_text(
            f"fileFormatVersion: 2{NL}guid: {GUID}{NL}", encoding="utf-8")
        (mats / "a.mat").write_text(
            f"Material:{NL}  m_SavedProperties:{NL}    m_Floats:{NL}"
            f"    - _OutlineOn: 1{NL}", encoding="utf-8")

        body = f"MonoBehaviour:{NL}  m_Script: {{fileID: 11500000, guid: {GUID}}}{NL}"
        (assets / "Settings" / "Renderer.asset").write_text(
            body if installed else f"MonoBehaviour:{NL}  m_Name: Empty{NL}",
            encoding="utf-8")
        return shaders, mats

    with tempfile.TemporaryDirectory(prefix="featureprobe_") as td:
        root, mats = build(Path(td) / "missing", installed=False)
        found = param_check.check_feature_installed(root, mats)
        if not any("ToonOutlineFeature" in f.title for f in found):
            return "Feature の未導入を見つけられない（設定しても何も描かない状態を通す）"

        root, mats = build(Path(td) / "installed", installed=True)
        found = param_check.check_feature_installed(root, mats)
        if any("ToonOutlineFeature" in f.title for f in found):
            return "導入されているのに指摘する（誤検出）"
    return ""


# 道具の中の関数を直接撃つ項目。サンドボックスも注入も使わない。
#
# **サンドボックスで動かせない道具はここに置く。** 実プロジェクト（Unity の
# 応答ファイル・URP のシェーダーライブラリ）を要る道具は複製の中では動かず、
# 長く自己診断の外にあった。その 2 つが揃って偽の合格を出していた（T-257 / T-258）。
UNITS: list[tuple[str, object, str]] = [
    ("csharp_compile — 指摘を捨てていないか",
     unit_csharp_message_filter,
     "C# のコンパイルエラーを 1 件も報告しないまま「エラー 0 件」と出す",
     "csharp_compile"),
    ("param_check — Feature の未導入を見つけるか",
     unit_feature_installed,
     "設定は ON でキーワードも立つのに、描く人が居ないまま絵が変わらない状態",
     "check_feature_installed"),
    ("param_check — 不在時に守られていない参照を見つけるか",
     unit_asmdef_dependency,
     "Core 不在でコンパイルが落ち、ドメインリロードが完了せず自動インストーラが走らない状態",
     "_check_asmdef_deps"),
    ("hlsl_compile — 0 プログラムを合格にしないか",
     unit_hlsl_zero_program,
     "シェーダーを 1 つもコンパイルしないまま「16 プログラム成功」の顔で OK を返す",
     "hlsl_compile"),
    ("hlsl_compile — 既知の X4000 だけを畳んでいるか",
     unit_x4000_fold,
     "本当に未初期化のローカルまで畳み、絵にゴミが出ているのに検査が静かなままになる",
     "x4000_known"),
    ("hlsl_compile — 出荷している構成を通しているか",
     unit_shipping_combos,
     "誰も使わない構成だけを実コンパイルして「成功」と言い、出荷構成の欠陥を見逃す",
     "shipping_combos"),
    ("param_check — レンダラ間の Feature の差を見つけるか",
     unit_renderer_parity,
     "片方のレンダラでだけ輪郭やキャラ影が描かれず、品質レベルを変えた時だけ絵が変わる",
     "check_renderer_feature_parity"),
]


def main() -> int:
    if not MATERIALS.is_dir():
        print(f"  注意: {MATERIALS} が無い。マテリアルを使う項目は飛ばす。")

    print(f"{NL}{'=' * 62}{NL}  自己診断 — 検査そのものが生きているか{NL}{'=' * 62}")

    passed, failed = 0, []

    with tempfile.TemporaryDirectory(prefix="toonpbr_selftest_") as td:
        root = build_sandbox(Path(td))

        for case in CASES:
            ok, reason = run_case(root, case)
            if ok:
                passed += 1
                print(f"  OK      {case.name}")
            else:
                failed.append((case.name, reason))
                print(f"  **失敗**  {case.name}")
                print(f"            {reason}")
                print(f"            見逃すもの: {case.why}")

    for name, fn, why, *_ in UNITS:
        reason = fn()
        if not reason:
            passed += 1
            print(f"  OK      {name}")
        else:
            failed.append((name, reason))
            print(f"  **失敗**  {name}")
            print(f"            {reason}")
            print(f"            見逃すもの: {why}")

    print(f"{NL}{'-' * 62}")
    print(f"  {passed} / {passed + len(failed)} 項目で発火を確認")

    # **試験していない検査を必ず並べる。** 合否には影響させない
    # ── 全部を試験するまで赤のままにすると、赤が常態になって読まれなくなる。
    # ただし黙ってもいけない。「自己診断が通った」を「全部生きている」と
    # 読み替えさせないことがこの数行の目的。
    uncovered = coverage_report()
    total = len(uncovered) + len({c.covers for c in CASES})
    covered = total - len(uncovered)
    print(f"  カバー率 {covered} / {total} 検査")
    if uncovered:
        print(f"  **未試験（生死は不明）**: {', '.join(uncovered)}")

    # **この 2 つの数字を文書へ書き写している。** 項目数は `param_check` が
    # 検算しているが、カバー率は**どこも比べていなかった**ので、実測 76 の
    # 隣に 77 と書いても誰も気付かなかった（T-335 で実際にやった）。
    # 両方を知っているのはここだけなので、ここで突き合わせる。
    doc = HERE / "BACKLOG.md"
    if doc.exists():
        m = re.search(r"自己診断（\*\*(\d+) 項目 / カバー率 (\d+) 検査\*\*）",
                      doc.read_text(encoding="utf-8", errors="replace"))
        if m is None:
            print("  **BACKLOG に自己診断の規模が書かれていない**"
                  "（書き写した数字が古くなっても誰も気付けない）")
        else:
            said = (int(m.group(1)), int(m.group(2)))
            real = (passed + len(failed), covered)
            if said != real:
                print(f"  **BACKLOG の数字が古い**: 項目 {said[0]} / カバー率 {said[1]}"
                      f" と書いてあるが、実測は 項目 {real[0]} / カバー率 {real[1]}")
                failed.append(("BACKLOG の自己診断の規模",
                               f"{said} と書いてあるが実測 {real}"))

    if failed:
        print(f"{NL}  **{len(failed)} 件の検査が期待どおり動いていない**")
        print("  **この状態の「エラー 0 件」は信用できない。**")
        return 1

    print("  検査はすべて生きている。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

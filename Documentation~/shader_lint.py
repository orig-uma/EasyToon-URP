#!/usr/bin/env python3
"""
shader_lint.py — URP シェーダーの静的検査

Unity を起動せずに検出できるクラスの誤りだけを拾う。
コンパイルの代わりにはならないが、Claude Code が編集直後に自己検証できる。

検出するもの:
  E001  HLSL で参照されているプロパティが UnityPerMaterial CBUFFER に無い
  E002  CBUFFER にあるがプロパティにも無く、スクリプト設定の印も無い
  E003  サンプルされているテクスチャが TEXTURE2D/SAMPLER で宣言されていない
  E008  自前の Toon* 関数を定義より前で呼んでいる（HLSL は宣言順に解析する）
  E009  saturate/clamp の結果に下駄を足している（値域が [0,1] を外れ pow が NaN になる）
  E010  自前の Toon* 関数を呼んでいるが、その定義がツリーのどこにも無い
  E011  自前の Toon* 関数に渡している引数の数が定義と合わない
  E012  自前の Toon* 関数への引数の成分数が足りない（暗黙変換されない）
  W108  自前の Toon* 関数への引数の成分数が多い（黙って切り捨てられる）
  W109  宣言したキーワードがどの #if にも現れない（バリアントだけが倍になる）
  W105  自前の SAMPLER 宣言が多すぎる（ps_4_0 の 16 本上限に当たる）
  W106  Range が [0,1] を外れるプロパティを lerp の補間係数にそのまま渡している（外挿）
  W107  Editor スクリプトのプロパティ名／シェーダー名がシェーダー側と一致しない（黙って動かなくなる）
  E004  使用シンボルに必要なヘッダが、その行より前でインクルードされていない
  E005  UnityPerMaterial CBUFFER が複数の箇所で宣言されている
  E006  TRANSFORM_TEX に使われている _XXX_ST が CBUFFER に無い
  E007  シーン深度を読むのに DepthOnly パスが無い
  W101  #if defined() で使われているキーワードを宣言する #pragma が無い
  W102  #pragma で宣言されたキーワードを ON にする Property が無い
  W103  Properties にあるが HLSL からもレンダーステートからも参照されない
  W104  Properties にあるがカスタム ShaderGUI が参照していない（インスペクタに出ない）

使い方:
  cd Assets/ToonPBR && python shader_lint.py .
  cd Assets/ToonPBR && python shader_lint.py . --strict   # 警告も失敗扱い

スクリプトから設定するプロパティは宣言行に印を付けて除外する:
  float4 _HeadForward;   // lint:script-set
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

# ---------------------------------------------------------------------------
# 設定
# ---------------------------------------------------------------------------

# シンボル -> それを宣言しているヘッダ
REQUIRED_INCLUDES: dict[str, str] = {
    "SampleSceneDepth": "DeclareDepthTexture.hlsl",
    "LoadSceneDepth": "DeclareDepthTexture.hlsl",
    "SampleSceneColor": "DeclareOpaqueTexture.hlsl",
    "SampleSceneNormals": "DeclareNormalsTexture.hlsl",
    "GetMainLight": "Lighting.hlsl",
    "GetAdditionalLight": "Lighting.hlsl",
    "GetAdditionalLightsCount": "Lighting.hlsl",
    "LIGHT_LOOP_BEGIN": "Lighting.hlsl",
    "SampleSH": "Lighting.hlsl",
    "ApplyShadowBias": "Shadows.hlsl",
    "TransformWorldToShadowCoord": "Shadows.hlsl",
}

# ヘッダ -> それが間接的に引き込む他のヘッダ。
# 例: Lighting.hlsl は内部で Shadows.hlsl を include しているので、
#     Lighting.hlsl があれば ApplyShadowBias も使える。
HEADER_PROVIDES: dict[str, set[str]] = {
    "Lighting.hlsl": {
        "Core.hlsl", "Shadows.hlsl", "RealtimeLights.hlsl",
        "GlobalIllumination.hlsl", "BRDF.hlsl", "SurfaceInput.hlsl",
    },
    "Core.hlsl": {"Common.hlsl", "Input.hlsl", "ShaderVariablesFunctions.hlsl"},
    "LitInput.hlsl": {"Core.hlsl", "SurfaceInput.hlsl"},
}

# Unity / URP が定義済みのキーワード。W101 の対象外。
BUILTIN_KEYWORDS: set[str] = {
    "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN",
    "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHTS_VERTEX", "_ADDITIONAL_LIGHT_SHADOWS",
    "_SHADOWS_SOFT", "_FORWARD_PLUS", "_CLUSTER_LIGHT_LOOP",
    "_REFLECTION_PROBE_BLENDING", "_REFLECTION_PROBE_BOX_PROJECTION",
    "_SCREEN_SPACE_OCCLUSION", "_LIGHT_LAYERS", "_LIGHT_COOKIES",
    "_CASTING_PUNCTUAL_LIGHT_SHADOW", "_DBUFFER", "_SURFACE_TYPE_TRANSPARENT",
    "_DBUFFER_MRT1", "_DBUFFER_MRT2", "_DBUFFER_MRT3",
    "_WRITE_RENDERING_LAYERS", "_STATIC_LIGHTMAP", "_DYNAMIC_LIGHTMAP",
    "USE_FORWARD_PLUS", "USE_CLUSTER_LIGHT_LOOP",
}

# URP / Unity 側で宣言済みのテクスチャとサンプラ。E003 の対象外。
# パッケージ内の宣言までは追わないので、ここに列挙して誤検出を防ぐ。
BUILTIN_TEXTURES: set[str] = {
    "_MainLightShadowmapTexture", "_AdditionalLightsShadowmapTexture",
    "_CameraDepthTexture", "_CameraNormalsTexture", "_CameraOpaqueTexture",
    "_BlitTexture", "unity_SpecCube0", "unity_SpecCube1",
}

BUILTIN_SAMPLERS: set[str] = {
    "sampler_PointClamp", "sampler_PointRepeat",
    "sampler_LinearClamp", "sampler_LinearRepeat",
    "sampler_LinearClampCompare",
    "samplerunity_SpecCube0", "samplerunity_SpecCube1",
}

# テクスチャ型。CBUFFER に入れてはいけない。
TEXTURE_TYPES = {"2d", "3d", "cube", "cubearray", "2darray", "any"}

SHADER_EXT = {".shader", ".hlsl", ".cginc", ".hlslinc"}


# ---------------------------------------------------------------------------
# データ
# ---------------------------------------------------------------------------

@dataclass
class Issue:
    code: str
    path: Path
    line: int
    message: str

    @property
    def severity(self) -> str:
        return "error" if self.code.startswith("E") else "warning"

    def render(self, root: Path) -> str:
        try:
            rel = self.path.relative_to(root)
        except ValueError:
            rel = self.path
        return f"{rel}:{self.line}: {self.severity} {self.code}: {self.message}"


@dataclass
class SourceFile:
    path: Path
    text: str
    lines: list[str] = field(default_factory=list)

    def __post_init__(self) -> None:
        self.lines = self.text.splitlines()


# ---------------------------------------------------------------------------
# 前処理
# ---------------------------------------------------------------------------

def strip_comments(text: str) -> str:
    """コメントを空白に置換する。行番号と桁位置は保つ。"""
    out = []
    i = 0
    n = len(text)
    while i < n:
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            chunk = text[i:j]
            out.append("".join(ch if ch == "\n" else " " for ch in chunk))
            i = j
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def find_block(text: str, opener_re: str) -> tuple[int, int] | None:
    """opener_re にマッチした直後の { から対応する } までの範囲を返す。"""
    m = re.search(opener_re, text, re.IGNORECASE)
    if not m:
        return None
    start = text.find("{", m.end() - 1)
    if start < 0:
        return None
    depth = 0
    for idx in range(start, len(text)):
        if text[idx] == "{":
            depth += 1
        elif text[idx] == "}":
            depth -= 1
            if depth == 0:
                return start + 1, idx
    return None


def line_of(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


# ---------------------------------------------------------------------------
# パーサ
# ---------------------------------------------------------------------------

PROP_RE = re.compile(
    r"^[ \t]*((?:\[[^\]]*\][ \t]*)*)(_\w+)[ \t]*\([ \t]*\"[^\"]*\"[ \t]*,[ \t]*(\w+)",
    re.MULTILINE,
)

# Range(lo, hi) の上下限を取る。W106 で外挿を見るのに使う。
RANGE_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*\"[^\"]*\"[ \t]*,"
    r"[ \t]*Range\([ \t]*([-\d.]+)[ \t]*,[ \t]*([-\d.]+)[ \t]*\)[ \t]*\)",
    re.MULTILINE,
)


def find_lerp_args(text: str):
    """3引数の lerp をすべて拾い、(開始位置, 第3引数) を返す。

    複数行にまたがる lerp も取れるように、括弧の対応を自前で数える。
    正規表現では入れ子の括弧（lerp の中の lerp や関数呼び出し）を扱えない。
    """
    for m in re.finditer(r"\blerp[ \t]*\(", text):
        i = m.end()
        depth = 1
        args = [""]
        while i < len(text) and depth > 0:
            ch = text[i]
            if ch == "(":
                depth += 1
            elif ch == ")":
                depth -= 1
                if depth == 0:
                    break
            if depth == 1 and ch == ",":
                args.append("")
            else:
                args[-1] += ch
            i += 1
        if len(args) == 3:
            yield m.start(), " ".join(args[2].split())


# saturate()/clamp() の呼び出し。E009 で「飽和させた後の下駄」を見るのに使う。
SATURATE_CALL_RE = re.compile(r"(?<![A-Za-z0-9_])(saturate|clamp)\s*\(")

# saturate() の閉じ括弧の直後に続く「+ 小さい正の定数」。同じく E009 で使う。
EPSILON_TAIL_RE = re.compile(r"\s*\+\s*([0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)f?")


# 自前の Toon* 関数の定義。E008 で定義順を見るのに使う。
# 戻り値の型が付いた行頭のものだけを拾う（呼び出しや前方宣言と区別するため）。
TOON_FUNC_DEF_RE = re.compile(
    r"^(?:inline[ 	]+)?(?:void|bool|int|uint|float|half)[234]?(?:x[234])?"
    r"[ 	]+(Toon\w+)[ 	]*\(",
    re.MULTILINE,
)

CBUFFER_DECL_RE = re.compile(
    r"^[ \t]*(?:uniform[ \t]+)?"
    r"(?:half|float|int|uint|bool|fixed)[1-4]?(?:x[1-4])?[ \t]+"
    r"(_\w+)[ \t]*(?:\[[^\]]*\])?[ \t]*;(.*)$",
    re.MULTILINE,
)

TEXTURE_DECL_RE = re.compile(r"\bTEXTURE(?:2D|3D|CUBE)(?:_X|_ARRAY)?\s*\(\s*(_\w+)\s*\)")
SAMPLER_DECL_RE = re.compile(r"\bSAMPLER(?:_CMP)?\s*\(\s*(sampler_?\w+)\s*\)")
# サンプラの共有はエイリアスで書く。ps_4_0 のサンプラレジスタは 16 本しかなく、
# テクスチャごとに宣言すると実際に上限を超えてコンパイルが落ちる（T-072）。
#   #define sampler_BumpMap sampler_BaseMap
# これを宣言として認めないと E003 が誤検出になる。
# 自前で持ってよいサンプラの本数。URP の消費ぶんを引いた実効的な余裕。
SAMPLER_BUDGET = 4

SAMPLER_ALIAS_RE = re.compile(r"^[ \t]*#define[ \t]+(sampler_?\w+)[ \t]+(sampler_?\w+)[ \t]*$", re.M)
SAMPLE_RE = re.compile(r"\bSAMPLE_TEXTURE(?:2D|3D|CUBE)(?:_X)?(?:_LOD|_BIAS|_GRAD)?\s*\(\s*(_\w+)\s*,\s*(\w+)")
TRANSFORM_TEX_RE = re.compile(r"\bTRANSFORM_TEX\s*\([^,]+,\s*(_\w+)\s*\)")
INCLUDE_RE = re.compile(r'^\s*#\s*include\s+"([^"]+)"', re.MULTILINE)
PRAGMA_KW_RE = re.compile(
    r"^\s*#\s*pragma\s+(?:shader_feature|multi_compile)\w*\s+(.*)$", re.MULTILINE
)
IFDEF_RE = re.compile(r"#\s*(?:if|elif)\s+[^\n]*?defined\s*\(\s*(\w+)\s*\)|#\s*ifdef\s+(\w+)")
DEFINE_RE = re.compile(r"^\s*#\s*define\s+(\w+)", re.MULTILINE)
RENDER_STATE_RE = re.compile(r"(?<!\w)\[\s*(_\w+)\s*\]")
LIGHTMODE_RE = re.compile(r'"LightMode"\s*=\s*"(\w+)"')
CUSTOM_EDITOR_RE = re.compile(r'^\s*CustomEditor\s+"([\w.]+)"', re.MULTILINE)

# W107 で見る呼び出し。**ここに無い呼び出しの引数は対象外。**
# 「ソース中の "_Xxx" を全部見る」だと、アセット名の接尾辞のような
# シェーダーと無関係な文字列まで拾って誤検出になる（実際に出た）。
# Unity のマテリアル API と、このプロジェクトの Editor ヘルパを列挙する。
PROPERTY_CALL_RE = re.compile(
    r"\b(Has(?:Float|Int|Color|Vector|Texture|Property)"
    r"|Get(?:Float|Int|Color|Vector|Texture)"
    r"|Set(?:Float|Int|Color|Vector|Texture)?"
    r"|FindProperty|PropertyToID"
    r"|Enable(?:Keyword)|Disable(?:Keyword)|IsKeywordEnabled"
    r"|SetKeyword|SetToggle|Draw|Indented|Prop"
    # ShaderGuiKit の描画ヘルパ。**`Pv` を `P` より先に置くこと** ──
    # 交替は左から試すので、逆だと `Pv(` が `P` に食われて外れる。
    # ここに載せ忘れると、その呼び方をしている GUI のプロパティ参照が
    # **まるごと W107 の視界から消える**（GUI をタブ式に書き換えた際に発生）。
    r"|Pv|P)"
    r"\s*\(([^;]*?)\)",
    re.DOTALL,
)

# URP が自前で持っている LightMode。シェーダー側に無くても正常。
URP_BUILTIN_LIGHTMODES = {
    "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer",
    "SRPDefaultUnlit", "ShadowCaster", "DepthOnly", "DepthNormals",
    "DepthNormalsOnly", "MotionVectors", "Meta", "Universal2D",
}

PROP_LITERAL_RE = re.compile(r'"(_[A-Za-z]\w*)"')

# ShaderGUI のソースに出てくるプロパティ名リテラル
CS_PROP_LITERAL_RE = re.compile(r'"(_\w+)"')


def load(path: Path) -> SourceFile:
    return SourceFile(path=path, text=path.read_text(encoding="utf-8", errors="replace"))


def resolve_include(src: Path, target: str) -> Path | None:
    if target.startswith("Packages/"):
        # **同居しているパッケージは解決できる。**
        # 以前は `Packages/` で始まるものを一律で諦めていたが、それだと
        # 隣に置いた自前パッケージの中身まで見えなくなる ── 実際 Cel は
        # `ToonRamp` を Core（`com.origuma.easyshader-core`）から取っており、
        # **実在する関数を「どこにも定義されていない」と 3 回報告していた。**
        # 定義が見えないと E010 は誤検出しか出せないので、エラー 3 件が
        # 常態化して**検査そのものが無視される**方向に働く。
        #
        # URP 本体は `Library/PackageCache/` にあって位置が保証されないので
        # 対象にしない（見つからなければ従来どおり諦める）。
        for parent in src.resolve().parents:
            if parent.name == "Packages" and parent.is_dir():
                candidate = (parent.parent / target).resolve()
                return candidate if candidate.is_file() else None
        return None
    candidate = (src.parent / target).resolve()
    return candidate if candidate.is_file() else None


def collect_headers_by_line(src: SourceFile, seen: set[Path] | None = None) -> list[tuple[int, str]]:
    """(行番号, ヘッダ名) の一覧。ローカル include は再帰的に展開し、その行の位置に畳む。"""
    seen = seen if seen is not None else set()
    if src.path in seen:
        return []
    seen.add(src.path)

    stripped = strip_comments(src.text)
    result: list[tuple[int, str]] = []

    for m in INCLUDE_RE.finditer(stripped):
        target = m.group(1)
        line = line_of(stripped, m.start())
        name = Path(target).name
        result.append((line, name))

        # そのヘッダが内部で引き込むものも同じ行で使えるとみなす
        for provided in HEADER_PROVIDES.get(name, set()):
            result.append((line, provided))

        local = resolve_include(src.path, target)
        if local is not None:
            nested = collect_headers_by_line(load(local), seen)
            result.extend((line, nested_name) for _, nested_name in nested)

    return result


# ---------------------------------------------------------------------------
# 検査
# ---------------------------------------------------------------------------

def code_roots(root: Path) -> list[Path]:
    """C# を探す場所。**ツリーの外にあることがある。**

    パッケージへ移すと `Editor/` と `Runtime/Scripts/` はシェーダーと
    別の階層へ行く。`root` の下だけを見ると**スクリプトが 1 つも見つからず、
    「参照されていない」「実在しない」と誤って報告する**（T-252）。

    **同じパッケージに別のシェーダーが同居する。** 単にパッケージルートを
    足すと隣のスクリプトまで拾い、そちらのプロパティを「宣言が無い」と
    誤検出する（実際 57 件出た）。**シェーダーのフォルダ名と同じ名前の
    部屋だけ**を見る ── `Shaders/Idol/` に対して `Editor/Idol/` と
    `Runtime/Scripts/Idol/`。
    """
    roots = [root]
    name = root.resolve().name
    for parent in root.resolve().parents:
        if (parent / "package.json").exists():
            for d in parent.rglob(name):
                if d.is_dir() and d != root.resolve():
                    roots.append(d)
            break
    return roots


_VALUE_MACROS: set[str] | None = None


def urp_value_macros(start: Path) -> set[str]:
    """URP / Core が **0 と 1 の両方で `#define` する**マクロ名を集める。

    こういうマクロは**常に定義されている**ので、`defined()` で見ると
    **必ず真**になる。書いた側は「機能が有効なときだけ」のつもりでも、
    無効な変種でも中がコンパイル対象になり、
    **その機能の中でしか定義されない識別子**を参照して落ちる。

    実際に踏んだ形（T-315）: Cel が
    `#if defined(USE_FORWARD_PLUS) || defined(USE_CLUSTER_LIGHT_LOOP)` と
    書いていて、Forward+ を切った変種でコンパイルできなかった。
    **このプロジェクトは Forward+ なので普段は通っており**、
    全キーワード組を回して初めて出た。
    URP 自身は `#if USE_CLUSTER_LIGHT_LOOP` と**値で**判定している。

    **一覧を書き写さないこと。** URP の更新で増減するので、毎回集める
    （130 ファイルで 0.0 秒）。見つからなければ何も言わない。
    """
    global _VALUE_MACROS
    if _VALUE_MACROS is not None:
        return _VALUE_MACROS

    cache = None
    for p in [start.resolve(), *start.resolve().parents]:
        if (p / "Library" / "PackageCache").is_dir():
            cache = p / "Library" / "PackageCache"
            break
    found: dict[str, set[str]] = {}
    if cache is not None:
        for pkg in cache.glob("com.unity.render-pipelines.*"):
            lib = pkg / "ShaderLibrary"
            if not lib.is_dir():
                continue
            for f in lib.rglob("*.hlsl"):
                try:
                    text = f.read_text(encoding="utf-8", errors="replace")
                except OSError:
                    continue
                for m in re.finditer(
                        r"^\s*#\s*define\s+([A-Z_][A-Z0-9_]*)\s+([01])\s*$", text, re.M):
                    found.setdefault(m.group(1), set()).add(m.group(2))
    _VALUE_MACROS = {k for k, v in found.items() if v == {"0", "1"}}
    return _VALUE_MACROS


def lint_shader(shader_path: Path, issues: list[Issue]) -> None:
    src = load(shader_path)
    stripped = strip_comments(src.text)

    # --- Properties -------------------------------------------------------
    prop_block = find_block(stripped, r"\bProperties\b")
    props: dict[str, tuple[str, str, int]] = {}   # name -> (type, attrs, line)
    if prop_block:
        s, e = prop_block
        for m in PROP_RE.finditer(stripped[s:e]):
            attrs, name, ptype = m.group(1), m.group(2), m.group(3)
            props[name] = (ptype.lower(), attrs, line_of(stripped, s + m.start()))

    # --- ローカル include を含めた全 HLSL ソース ---------------------------
    #
    # **再帰で辿ること。** 直下の include だけを見ていたが、
    # パスの本体を `Passes/ForwardPass.hlsl` へ切り出したように**入れ子が増える**と、
    # 深い所のファイルが検査対象から丸ごと外れる（T-210）。
    #
    # 併せて「そのファイルが include された時点で、既に何が include 済みか」
    # （＝継承ヘッダ）も集める。E004 は使用行より前のヘッダを見るが、
    # **切り出したファイル自身は何も include していない。**
    # 呼ばれた場所で `ToonPBRCommon.hlsl` が済んでいるなら、それは使える。
    sources: list[SourceFile] = [src]
    inherited: dict[Path, set[str]] = {shader_path: set()}

    def walk(parent: SourceFile) -> None:
        text = strip_comments(parent.text)
        before: set[str] = set(inherited.get(parent.path, set()))

        for m in INCLUDE_RE.finditer(text):
            target = m.group(1)
            name = Path(target).name
            local = resolve_include(parent.path, target)

            if local is not None:
                # **交わりを取る。** 同じファイルが複数箇所から include されるなら、
                # **どこから来ても使えるもの**だけが「使える」。
                # 和にすると、片方の経路で足りていないのを見逃す。
                have = inherited.get(local)
                inherited[local] = set(before) if have is None else (have & before)

                if all(local != f.path for f in sources):
                    child = load(local)
                    sources.append(child)
                    walk(child)

                # **その子が引き込むヘッダも、ここから先は使える。**
                # `ToonPBRCommon.hlsl` を include した時点で URP の `Lighting.hlsl` も
                # 入っている ── これを伝えないと、後続の include 先で
                # 「Lighting が無い」と誤検出する（最初これで 5 件出した）。
                before |= {n for _, n in collect_headers_by_line(load(local))}

            before.add(name)
            before |= HEADER_PROVIDES.get(name, set())

    walk(src)

    combined: list[tuple[SourceFile, str]] = [(f, strip_comments(f.text)) for f in sources]

    # Properties ブロックを除いた「コード部分」を作る
    code_only = stripped[:prop_block[0]] + " " * (prop_block[1] - prop_block[0]) + stripped[prop_block[1]:] \
        if prop_block else stripped
    code_texts = [code_only] + [t for f, t in combined if f.path != shader_path]
    all_code = "\n".join(code_texts)

    # --- CBUFFER ----------------------------------------------------------
    cbuffer_members: dict[str, tuple[Path, int, str]] = {}
    cbuffer_sites: list[tuple[Path, int]] = []

    for f, text in combined:
        for m in re.finditer(r"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)", text):
            cbuffer_sites.append((f.path, line_of(text, m.start())))
            end = text.find("CBUFFER_END", m.end())
            end = len(text) if end < 0 else end
            body = text[m.end():end]
            # 印を拾うために元テキストからも同範囲を取る
            raw_body = f.text[m.end():end] if end <= len(f.text) else ""
            for d in CBUFFER_DECL_RE.finditer(body):
                name = d.group(1)
                ln = line_of(text, m.end() + d.start())
                raw_line = f.lines[ln - 1] if ln - 1 < len(f.lines) else ""
                cbuffer_members[name] = (f.path, ln, raw_line)
            _ = raw_body

    if len(cbuffer_sites) > 1:
        for path, ln in cbuffer_sites[1:]:
            issues.append(Issue(
                "E005", path, ln,
                "UnityPerMaterial CBUFFER が複数回宣言されている。"
                "全パスで内容が一致していないと SRP Batcher が無効になる",
            ))

    # --- レンダーステートで使われるプロパティ ------------------------------
    render_state_props: set[str] = set()
    body_only = stripped
    if prop_block:
        s, e = prop_block
        body_only = stripped[:s] + " " * (e - s) + stripped[e:]
    for m in RENDER_STATE_RE.finditer(body_only):
        render_state_props.add(m.group(1))

    # Cull [_Cull] のような角括弧参照は HLSL のコード参照ではないので取り除く。
    # 残さないと「CBUFFER に無い」と誤検出する。
    all_code = RENDER_STATE_RE.sub(" ", all_code)

    # --- E001 / W103 ------------------------------------------------------
    for name, (ptype, attrs, ln) in props.items():
        if ptype in TEXTURE_TYPES:
            continue
        drives_keyword = "toggle" in attrs.lower() or "keywordenum" in attrs.lower()
        referenced = re.search(rf"(?<![\w]){re.escape(name)}(?![\w])", all_code) is not None

        if referenced and name not in cbuffer_members:
            issues.append(Issue(
                "E001", shader_path, ln,
                f"'{name}' が HLSL から参照されているが UnityPerMaterial CBUFFER に無い。"
                "未宣言エラーか SRP Batcher の非互換になる",
            ))
        elif not referenced and name not in render_state_props and not drives_keyword:
            issues.append(Issue(
                "W103", shader_path, ln,
                f"'{name}' がどこからも参照されていない。使うか消すか",
            ))

    # --- E002 -------------------------------------------------------------
    for name, (path, ln, raw_line) in cbuffer_members.items():
        if name in props:
            continue
        if name.endswith("_ST") and name[:-3] in props:
            continue
        if "lint:script-set" in raw_line:
            # **印だけで信用しないこと。** この印は警告を黙らせるので、
            # 付けたのに**誰も設定していない**と、実行時 0 のまま動く状態を
            # 自分の手で隠すことになる。名指ししている C# を探して裏を取る。
            setter = False
            for r in code_roots(shader_path.parent):
                for cs in r.rglob("*.cs"):
                    if f'"{name}"' in cs.read_text(encoding="utf-8", errors="replace"):
                        setter = True
                        break
                if setter:
                    break
            if not setter:
                issues.append(Issue(
                    "E013", path, ln,
                    f"'{name}' に 'lint:script-set' と書いてあるが、"
                    f"**その名前を設定している C# が見つからない。** "
                    f"印は警告を黙らせるので、誰も設定しないまま"
                    f"**実行時 0 で動く状態を自分で隠す**ことになる。"
                    f"Properties に足すか、設定する側を書くこと",
                ))
            # **印は E002 を免除しない。** ここは長く `continue` していたが、
            # `SRP Batcher: not compatible` の原因がこれだった（T-338）。
            # SRP Batcher は `UnityPerMaterial` の全メンバーが Properties に
            # あることを**理由を問わず**要求する。「スクリプトが設定するから
            # UI は要らない」は正しいが、それは `[HideInInspector]` で表すもので、
            # **Properties から消してよい理由にはならない。**
            #
            # 1 つ欠けるだけでそのシェーダーは丸ごと非対応になり、
            # **絵は正しいままバッチングだけが消える** ── インスペクタを
            # 見ている限り気付けず、利用者のスクリーンショットで初めて出た。
        issues.append(Issue(
            "E002", path, ln,
            f"'{name}' が CBUFFER にあるが Properties に無い。"
            "実行時に 0 になるうえ、**SRP Batcher が丸ごと効かなくなる**"
            "（`UnityPerMaterial var is not declared in shader property section`）。"
            "スクリプトから設定する値なら "
            "`[HideInInspector] _Name (\"...\", Vector) = (0,0,0,0)` として "
            "Properties にも置くこと ── `lint:script-set` の印だけでは足りない",
        ))

    # --- E014 -------------------------------------------------------------
    # **0/1 で定義されるマクロを `defined()` で見ている。**
    # 常に定義されているので必ず真になり、機能を切った変種でも
    # 中がコンパイル対象になる ── そこで参照している識別子は
    # その機能の中でしか定義されないので落ちる。
    # **有効な環境では通ってしまう**ため、全キーワード組を回すまで出ない。
    value_macros = urp_value_macros(shader_path.parent)
    if value_macros:
        for f, text in combined:
            for m in re.finditer(r"defined\s*\(\s*([A-Z_][A-Z0-9_]*)\s*\)",
                                 strip_comments(text)):
                if m.group(1) not in value_macros:
                    continue
                issues.append(Issue(
                    "E014", f.path, line_of(text, m.start()),
                    f"'{m.group(1)}' は URP が **0 か 1 で必ず定義する**ので、"
                    f"`defined()` は**常に真**になる。"
                    f"機能を切った変種でも中がコンパイル対象になり、"
                    f"**その機能の中でしか定義されない識別子**を参照して落ちる。"
                    f"`#if {m.group(1)}` と**値で**判定すること"
                    f"（URP 自身もそう書いている）",
                ))

    # --- E006 -------------------------------------------------------------
    for m in TRANSFORM_TEX_RE.finditer(all_code):
        tex = m.group(1)
        st = f"{tex}_ST"
        if st not in cbuffer_members:
            issues.append(Issue(
                "E006", shader_path, 1,
                f"TRANSFORM_TEX が '{tex}' に使われているが '{st}' が CBUFFER に無い",
            ))

    # --- W105 -------------------------------------------------------------
    # ps_4_0 のサンプラレジスタは 16 本。URP 側がシャドウマップ・深度・環境・
    # デカール・SSAO・LOD ディザで数本使うので、自前で使えるのは実質わずか。
    # テクスチャごとに SAMPLER() を書くと **Hair + HQ 影のような組み合わせで
    # 実機のコンパイルが落ちる**（T-072 で実際に落ちた）。
    # 共有には core のインライン名（sampler_LinearRepeat 等）を使うこと。
    own_samplers = sorted({
        m.group(1)
        for f, txt in combined if f.path.suffix in SHADER_EXT
        for m in SAMPLER_DECL_RE.finditer(txt)
        if m.group(1) not in BUILTIN_SAMPLERS
    })
    if len(own_samplers) > SAMPLER_BUDGET:
        issues.append(Issue(
            "W105", combined[0][0].path, 1,
            f"自前の SAMPLER 宣言が {len(own_samplers)} 本ある（目安 {SAMPLER_BUDGET} 本まで）。"
            f"ps_4_0 の上限 16 本を URP と分け合うので実機で落ちる。"
            f"core の sampler_LinearRepeat / sampler_LinearClamp に寄せること: "
            + ", ".join(own_samplers)))

    # --- E003 -------------------------------------------------------------
    # --- W106 -------------------------------------------------------------
    # Range が [0,1] を外れるプロパティを lerp の補間係数に**そのまま**渡すと外挿になる。
    # lerp(a, b, 2.0) は b を通り越した先の値で、色なら負に沈み、遮蔽なら 1 を超える。
    # T-076（サブサーフェスの色が 1.55 超で負になった）と T-098（AO が負）は
    # どちらもこれ。**インスペクタのスライダは範囲を縛るが、実行時の値は縛らない**ので、
    # 他シェーダーから移植したマテリアルには範囲外の値がそのまま残る。
    # saturate / clamp / min で包んであれば安全なので、包まれていないものだけ挙げる。
    wide = {
        m.group(1): (float(m.group(2)), float(m.group(3)))
        for m in RANGE_RE.finditer(stripped)
        if float(m.group(2)) < 0.0 or float(m.group(3)) > 1.0
    }
    if wide:
        guarded = re.compile(r"\b(?:saturate|clamp|min|smoothstep|step|frac)\s*\(")
        for f, txt in combined:
            if f.path.suffix not in SHADER_EXT:
                continue
            for pos, third in find_lerp_args(txt):
                if guarded.search(third):
                    continue
                for name, (lo, hi) in wide.items():
                    if re.search(rf"(?<!\w){re.escape(name)}(?!\w)", third):
                        issues.append(Issue(
                            "W106", f.path, line_of(txt, pos),
                            f"lerp の補間係数に Range({lo}, {hi}) の '{name}' を"
                            f"そのまま渡している（'{third[:48]}'）。[0,1] を外れると外挿になり、"
                            f"色が負に沈むか遮蔽が 1 を超える。saturate() で包むこと"))

    # --- E008 -------------------------------------------------------------
    # **HLSL は宣言順に解析される。** 定義より前で呼ぶと `undeclared identifier`。
    # E004 はインクルード順を見ているが、**同一ファイル内の関数の定義順は見ていなかった。**
    # このセッションだけで2回踏んだ:
    #   - スペキュラ AA のヘルパを ToonFilterRoughness の隣に置いたが、
    #     それを呼ぶ ToonStrandSpecularGGX の方が前にあった
    #   - ToonDiskPhase / ToonVogelDisk の並び
    # どちらも**実バリアントコンパイル（約3分）でしか捕まらなかった**。
    # 静的検査なら1秒で分かる類なので、ここで見る。
    #
    # 対象は自前の Toon* だけ。URP や core の関数はインクルードで入るので
    # ファイル内の位置では判断できない。
    # **ファイルをまたいで見ること。** 元は 1 ファイルの中だけを見ていたが、
    # 分割で定義と呼び出しが別ファイルへ散った（T-212）。
    # `ToonPBRCommon.hlsl` が `Shading/*.hlsl` を順に include する形なので、
    # **「展開したときにどちらが先か」で判断しなければ意味がない。**
    #
    # そこで include を実際に展開した並び（セグメント列）を作り、
    # (ファイル, 位置) を通し番号へ写してから比べる。
    segments: list[tuple[Path, int, int]] = []      # (ファイル, 開始, 終了)

    def expand(f: SourceFile, depth: int = 0) -> None:
        if depth > 16:
            return
        txt = strip_comments(f.text)
        cursor = 0
        for m in INCLUDE_RE.finditer(txt):
            local = resolve_include(f.path, m.group(1))
            if local is None:
                continue
            segments.append((f.path, cursor, m.start()))
            expand(load(local), depth + 1)
            cursor = m.end()
        segments.append((f.path, cursor, len(txt)))

    expand(src)

    text_of = {f.path: t for f, t in combined}

    def flat_pos(path: Path, pos: int) -> int | None:
        """展開後の通し位置。同じファイルが 2 回展開されるなら最初の方を採る。"""
        run = 0
        for p, a, b in segments:
            if p == path and a <= pos < b:
                return run + (pos - a)
            run += b - a
        return None

    defs: dict[str, tuple[Path, int, int]] = {}     # name -> (file, pos, flat)
    for f, txt in combined:
        if f.path.suffix not in SHADER_EXT:
            continue
        for m in TOON_FUNC_DEF_RE.finditer(txt):
            fp = flat_pos(f.path, m.start())
            if fp is None:
                continue
            cur = defs.get(m.group(1))
            if cur is None or fp < cur[2]:
                defs[m.group(1)] = (f.path, m.start(), fp)

    for name, (def_file, def_pos, def_flat) in defs.items():
        for f, txt in combined:
            if f.path.suffix not in SHADER_EXT:
                continue
            hit = None
            for m in re.finditer(rf"\b{re.escape(name)}\s*\(", txt):
                fp = flat_pos(f.path, m.start())
                if fp is None or fp >= def_flat:
                    continue
                hit = m
                break
            if hit is None:
                continue
            issues.append(Issue(
                "E008", f.path, line_of(txt, hit.start()),
                f"'{name}' を定義（{def_file.name} の {line_of(text_of[def_file], def_pos)} 行）"
                f"より前で呼んでいる。HLSL は宣言順に解析するので "
                f"'undeclared identifier' になる。定義を使用箇所より前へ移すこと"))
            break


    # --- E010 / E011 -------------------------------------------------------
    # **コンパイラの領分に一歩踏み込む。** 名前の打ち間違いと引数の数の違いは、
    # 静的検査では見つからない代表として CLAUDE.md に挙がっていた。
    # だが自前の `Toon*` に限れば、定義も呼び出しもこのツリーの中にあるので
    # **突き合わせられる。**
    #
    # 効くのは Editor が開いていて実コンパイルができないときで、
    # まさにそのときに新しい関数を足している（T-223 / T-225）。
    #
    # **誤検出を出さないために対象を絞ってある:**
    #   - `Toon` 前置詞のものだけ。URP や core の関数は対象外
    #   - 構造体名は除く（`ToonSurface` / `ToonContext`）
    #   - `return ToonFoo(` を定義と読まないよう、戻り値の位置の予約語を弾く
    #   - 既定引数を持つ定義は「必要な数〜全部」の幅で許す
    #   - 同名の多重定義（オーバーロード）はどれか1つに合えばよい
    #
    # 定義の書式は**インデントを許すこと。** `Passes/*.hlsl` は `.shader` の
    # HLSLPROGRAM の中から切り出したのでインデントが残っており、
    # 行頭固定だと `ToonVert` / `ToonFrag` が定義として見えない。
    RET_KEYWORDS = {"return", "if", "else", "while", "for", "switch",
                    "case", "do", "break", "continue", "defined"}
    FUNC_DEF_ANY_RE = re.compile(
        r"^[ \t]*(?:inline[ \t]+)?(\w+)[234]?(?:x[234])?[ \t]+(Toon\w+)[ \t]*\(",
        re.MULTILINE)
    CALL_RE = re.compile(r"(?<![A-Za-z0-9_])(Toon\w+)\s*\(")

    def balanced_end(s: str, open_pos: int) -> int | None:
        """`(` の位置から対応する `)` の位置。見つからなければ None。"""
        depth = 0
        for i in range(open_pos, len(s)):
            if s[i] == "(":
                depth += 1
            elif s[i] == ")":
                depth -= 1
                if depth == 0:
                    return i
        return None

    def split_args(s: str) -> list[str]:
        """トップレベルのカンマで割る。入れ子の呼び出しは1つと数える。"""
        out_: list[str] = []
        depth = 0
        cur: list[str] = []
        for ch in s:
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            if ch == "," and depth == 0:
                out_.append("".join(cur))
                cur = []
            else:
                cur.append(ch)
        tail = "".join(cur).strip()
        if tail or out_:
            out_.append(tail)
        return [a.strip() for a in out_]

    struct_names = set()
    for _, txt in combined:
        struct_names |= set(re.findall(r"^\s*struct\s+(\w+)", txt, re.MULTILINE))

    # --- 成分数の推論に使う表（E012 / W108）--------------------------------
    #
    # **分からないものは分からないままにする。** 型推論の真似事をして
    # 誤検出を出すと、正しい設定に警告を出す状態になり他の指摘が埋もれる。
    # 現状のツリーでは引数 251 個中 182 個（72%）を判定でき、食い違い 0 件。
    #
    # 途中まで**フィールド名だけで型を引いていて誤検出を出した** ──
    # `mainLight.color` を自前の構造体の `color` と取り違え、
    # URP の `Light`（half3）を 4 成分と読んだ。**変数の型が分かるものだけ**にした。
    all_src = "\n".join(txt for f, txt in combined if f.path.suffix in SHADER_EXT)
    SWIZZLE_CHARS = set("xyzwrgba")

    cbuf_comp: dict[str, int] = {}
    for mm in re.finditer(r"^[ \t]*(?:float|half|int|uint|bool)([234]?)[ \t]+(_\w+)[ \t]*;",
                          all_src, re.MULTILINE):
        cbuf_comp[mm.group(2)] = int(mm.group(1) or 1)

    struct_fields: dict[str, dict[str, int]] = {}
    for sm in re.finditer(r"struct\s+(\w+)\s*\{([^}]*)\}", all_src, re.DOTALL):
        d: dict[str, int] = {}
        for fm in re.finditer(
                r"(?:float|half|int|uint|bool)([234]?)(?!x[234])\s+(\w+)\s*[;:]", sm.group(2)):
            d[fm.group(2)] = int(fm.group(1) or 1)
        struct_fields[sm.group(1)] = d

    # 変数 -> 自前の構造体型。同名で型が割れているものは捨てる（曖昧なら見ない）
    var_struct: dict[str, str | None] = {}
    for sname in struct_fields:
        for mm in re.finditer(rf"(?<![\w.]){re.escape(sname)}[ \t]+(\w+)[ \t]*[;=]", all_src):
            k = mm.group(1)
            var_struct[k] = None if k in var_struct and var_struct[k] != sname else sname
    var_of_struct = {k: v for k, v in var_struct.items() if v}

    local_comp: dict[str, int | None] = {}
    for mm in re.finditer(
            r"(?<![\w.])(?:float|half|int|uint|bool)([234]?)(?!x[234])[ \t]+(\w+)[ \t]*[=;,)]",
            all_src):
        n = int(mm.group(1) or 1)
        k = mm.group(2)
        local_comp[k] = None if k in local_comp and local_comp[k] != n else n
    locals_comp = {k: v for k, v in local_comp.items() if v is not None}

    def expr_components(expr: str) -> int | None:
        """式の成分数。**確実に分かるものだけ返す。**"""
        e = expr.strip()
        mm = re.fullmatch(r"(?:float|half)([234])\s*\(.*\)", e, re.DOTALL)
        if mm:
            return int(mm.group(1))
        if re.fullmatch(r"(?:float|half)\s*\(.*\)", e, re.DOTALL):
            return 1
        mm = re.fullmatch(r"(\w+)\.(\w+)", e)
        if mm:
            base, fld = mm.group(1), mm.group(2)
            if base in var_of_struct:
                return struct_fields[var_of_struct[base]].get(fld)
            # 自前の構造体でない変数のドットは、スウィズルとしてだけ読む。
            # 他人の構造体のフィールドかもしれないので、スウィズル以外は諦める
            if len(fld) <= 4 and set(fld) <= SWIZZLE_CHARS:
                return len(fld)
            return None
        if re.fullmatch(r"-?[0-9.]+f?", e):
            return 1
        if e in cbuf_comp:
            return cbuf_comp[e]
        return locals_comp.get(e)

    def param_components(decl: str) -> int | None:
        """仮引数の宣言から成分数。行列と構造体は対象外。"""
        mm = re.match(
            r"(?:in|out|inout)?\s*(?:const\s+)?"
            r"(?:float|half|int|uint|bool)([234]?)(x[234])?\s+\w+", decl.strip())
        if not mm or mm.group(2):
            return None
        return int(mm.group(1) or 1)

    arity: dict[str, list[tuple[int, int]]] = {}     # name -> [(必要数, 全部)]
    sigs: dict[str, list[list[str]]] = {}           # name -> [仮引数の宣言そのまま]
    def_parens: set[tuple[Path, int]] = set()
    for f, txt in combined:
        if f.path.suffix not in SHADER_EXT:
            continue
        for m in FUNC_DEF_ANY_RE.finditer(txt):
            if m.group(1) in RET_KEYWORDS:
                continue
            open_pos = m.end() - 1
            close = balanced_end(txt, open_pos)
            if close is None:
                continue
            def_parens.add((f.path, open_pos))
            params = [p for p in split_args(txt[open_pos + 1:close])
                      if p and p != "void"]
            required = len([p for p in params if "=" not in p])
            arity.setdefault(m.group(2), []).append((required, len(params)))
            sigs.setdefault(m.group(2), []).append(params)

    # マクロも呼べる。引数付き `#define Toon...(a, b)` を同じ表に入れる。
    for f, txt in combined:
        if f.path.suffix not in SHADER_EXT:
            continue
        for m in re.finditer(r"^[ \t]*#define[ \t]+(Toon\w+)\(([^)]*)\)",
                             txt, re.MULTILINE):
            n = len([p for p in split_args(m.group(2)) if p])
            arity.setdefault(m.group(1), []).append((n, n))
            def_parens.add((f.path, m.start(2) - 1))

    for f, txt in combined:
        if f.path.suffix not in SHADER_EXT:
            continue
        for m in CALL_RE.finditer(txt):
            name = m.group(1)
            open_pos = m.end() - 1
            if (f.path, open_pos) in def_parens or name in struct_names:
                continue
            close = balanced_end(txt, open_pos)
            if close is None:
                continue

            if name not in arity:
                issues.append(Issue(
                    "E010", f.path, line_of(txt, m.start()),
                    f"'{name}' はどこにも定義されていない。"
                    f" 打ち間違いか、定義するファイルがどこからも include されていない"))
                continue

            call_args = split_args(txt[open_pos + 1:close])
            n_args = len(call_args)
            if not any(lo <= n_args <= hi for lo, hi in arity[name]):
                want = " / ".join(f"{lo}" if lo == hi else f"{lo}〜{hi}"
                                  for lo, hi in arity[name])
                issues.append(Issue(
                    "E011", f.path, line_of(txt, m.start()),
                    f"'{name}' に引数を {n_args} 個渡しているが、定義は {want} 個"))
                continue

            # --- E012 / W108: ベクトルの成分数 ---------------------------
            sig = next((s for s in sigs.get(name, []) if len(s) == n_args), None)
            if sig is None:
                continue
            for idx, (a_expr, p_decl) in enumerate(zip(call_args, sig)):
                want_n = param_components(p_decl)
                got_n = expr_components(a_expr)
                if want_n is None or got_n is None or got_n == want_n:
                    continue
                if got_n == 1:
                    continue            # スカラーはベクトルへ昇格する。正しい
                where = f"'{name}' の第 {idx + 1} 引数 `{a_expr.strip()[:32]}`"
                if got_n < want_n:
                    issues.append(Issue(
                        "E012", f.path, line_of(txt, m.start()),
                        f"{where} は {got_n} 成分だが、仮引数 `{p_decl.strip()}` は "
                        f"{want_n} 成分。**足りない側は暗黙変換されない**"))
                else:
                    issues.append(Issue(
                        "W108", f.path, line_of(txt, m.start()),
                        f"{where} は {got_n} 成分だが、仮引数 `{p_decl.strip()}` は "
                        f"{want_n} 成分。**黙って切り捨てられる**（コンパイルは通る）"))

    # --- E009 -------------------------------------------------------------
    # **飽和させた後に下駄を足さないこと。** `saturate(x) + 1e-4` は
    # ゼロ除算を避ける意図で書かれるが、**値域が [0, 1] から [1e-4, 1.0001] にずれる。**
    # 上に飛び出したぶん `1.0 - x` が負になり、**負の底の pow は HLSL で未定義**
    # （fxc は exp2(y * log2(負)) を計算するので NaN を返す）。
    #
    # T-165 で実際に踏んだ:
    #   c.NdotV = saturate(dot(N, V)) + 1e-4;          // 最大 1.0001
    #   float Fc = 0.04 + 0.96 * pow(1.0 - c.NdotV, 5.0);   // 底が -1e-4 → NaN
    # カメラを真正面から向いた面で起きる。球体である眼球には必ずその点があり、
    # クリアコートは目のマテリアルで有効なので、**瞳の中心に NaN が出ていた。**
    #
    # 正しくは `max(saturate(x), eps)` ── 下限で挟めば片側しか動かない。
    #
    # 対象は小さい正の定数（< 0.1）だけにする。それ以上は意図的なバイアスで、
    # 見た目で分かる。ゼロ避けの下駄だけを撃つことで誤検出を出さない。
    for f, txt in combined:
        if f.path.suffix not in SHADER_EXT:
            continue
        for m in SATURATE_CALL_RE.finditer(txt):
            i, depth = m.end(), 1
            while i < len(txt) and depth:
                depth += (txt[i] == "(") - (txt[i] == ")")
                i += 1
            if depth:
                continue
            tail = EPSILON_TAIL_RE.match(txt[i:])
            if not tail or not 0.0 < float(tail.group(1)) < 0.1:
                continue
            issues.append(Issue(
                "E009", f.path, line_of(txt, m.start()),
                f"{m.group(1)}() の結果に下駄 '+{tail.group(1)}' を足している。"
                f"値域が [0,1] から上へはみ出し、'1.0 - x' が負になる。"
                f"負の底の pow は NaN（T-165）。"
                f"max({m.group(1)}(...), {tail.group(1)}) と下限で挟むこと"))

    declared_tex = {m.group(1) for _, t in combined for m in TEXTURE_DECL_RE.finditer(t)}
    declared_smp = {m.group(1) for _, t in combined for m in SAMPLER_DECL_RE.finditer(t)}

    # エイリアス先が宣言済み（または組み込み）なら、エイリアス元も宣言済みとみなす。
    for _, t in combined:
        for m in SAMPLER_ALIAS_RE.finditer(t):
            if m.group(2) in declared_smp or m.group(2) in BUILTIN_SAMPLERS:
                declared_smp.add(m.group(1))

    for f, text in combined:
        for m in SAMPLE_RE.finditer(text):
            tex, smp = m.group(1), m.group(2)
            ln = line_of(text, m.start())
            if tex not in declared_tex and tex not in BUILTIN_TEXTURES:
                issues.append(Issue("E003", f.path, ln, f"'{tex}' が TEXTURE2D(...) で宣言されていない"))
            if smp not in declared_smp and smp not in BUILTIN_SAMPLERS:
                issues.append(Issue("E003", f.path, ln, f"'{smp}' が SAMPLER(...) で宣言されていない"))

    # --- E004  インクルード順 ---------------------------------------------
    for f, text in combined:
        headers = collect_headers_by_line(f)
        for symbol, header in REQUIRED_INCLUDES.items():
            for m in re.finditer(rf"(?<![\w]){re.escape(symbol)}\s*\(", text):
                use_line = line_of(text, m.start())
                available = (header in inherited.get(f.path, set())
                             or any(name == header and ln < use_line
                                    for ln, name in headers))
                if not available:
                    issues.append(Issue(
                        "E004", f.path, use_line,
                        f"'{symbol}' を使う前に {header} がインクルードされていない。"
                        "HLSL は上から順に解析されるので、依存ヘッダは使用行より前に置く",
                    ))
                break   # 同じシンボルは最初の1件だけ報告する

    # --- キーワード -------------------------------------------------------
    declared_kw: set[str] = set()
    # **`multi_compile` は別扱い。** こちらはパイプラインやスクリプトが
    # グローバルに立てるもので、**マテリアルのプロパティを持たないのが正しい。**
    # 実際 Cel の `_IDOL_CHARSHADOW` は Renderer Feature が
    # `Shader.EnableKeyword` で立てており、「ON にする Property が無い」と
    # 報告するのは**正しい設計を欠陥と呼んでいる**ことになる。
    # Property を要求してよいのは `shader_feature` だけ。
    feature_kw: set[str] = set()
    for m in PRAGMA_KW_RE.finditer(stripped):
        is_feature = "shader_feature" in m.group(0)
        for token in m.group(1).split():
            if token.startswith("_") and len(token) > 1:
                declared_kw.add(token)
                if is_feature:
                    feature_kw.add(token)

    # Properties が生成するキーワード
    property_kw: set[str] = set()
    for name, (ptype, attrs, ln) in props.items():
        for a in re.finditer(r"\[\s*Toggle(?:Off)?\s*\(\s*(\w+)\s*\)\s*\]", attrs, re.IGNORECASE):
            property_kw.add(a.group(1))
        if re.search(r"\[\s*Toggle(?:Off)?\s*\]", attrs, re.IGNORECASE):
            property_kw.add(f"{name.upper()}_ON")
        for a in re.finditer(r"\[\s*KeywordEnum\s*\(([^)]*)\)\s*\]", attrs, re.IGNORECASE):
            for opt in a.group(1).split(","):
                opt = opt.strip()
                if opt:
                    property_kw.add(f"{name.upper()}_{opt.upper()}")

    local_defines = {m.group(1) for _, t in combined for m in DEFINE_RE.finditer(t)}

    used_kw: dict[str, tuple[Path, int]] = {}
    for f, text in combined:
        for m in IFDEF_RE.finditer(text):
            kw = m.group(1) or m.group(2)
            if not kw or not kw.startswith("_"):
                continue
            if kw in BUILTIN_KEYWORDS or kw in local_defines:
                continue
            used_kw.setdefault(kw, (f.path, line_of(text, m.start())))

    for kw, (path, ln) in sorted(used_kw.items()):
        if kw not in declared_kw:
            issues.append(Issue(
                "W101", path, ln,
                f"キーワード '{kw}' を宣言する #pragma shader_feature / multi_compile が無い。常に無効になる",
            ))

    for kw in sorted(feature_kw):
        if kw in BUILTIN_KEYWORDS:
            continue
        if kw not in property_kw:
            issues.append(Issue(
                "W102", shader_path, 1,
                f"キーワード '{kw}' を ON にする Property が無い。"
                "[Toggle(...)] か [KeywordEnum(...)] を足すか、カスタム ShaderGUI で設定する",
            ))

    # --- W109 ---------------------------------------------------------------
    # **宣言したのにどの条件にも出てこないキーワードは、ただの死に重み。**
    # コードが同一のバリアントが倍に増えるだけで、絵は 1 ピクセルも変わらない。
    #
    # このプロジェクトは**手作業で 2 回見つけて、そのたびにバリアントが半減した**
    # ── `_REFLECTION_PROBE_BLENDING`（自前の環境サンプルを使うので不要）と
    # `_ADDITIONAL_LIGHTS_VERTEX`（頂点ライトを実装していない）。
    # 見つけ方が「気になったので数えてみた」だったので、次は見逃す。
    #
    # **排他グループの先頭は除く。** `shader_feature A B C` の A は
    # 「B でも C でもない状態」を表すので、`#if defined(A)` が無いのが正しい。
    #
    # 参照の判定は `#if defined(X)` だけでなく**`#if X` の形も見る**こと。
    # 最初 `defined()` だけを見て `_CASTING_PUNCTUAL_LIGHT_SHADOW` を
    # 死に重みと誤判定した（ShadowPass が `#if X` で使っている）。
    cond_syms: set[str] = set()
    for _, text in combined:
        for m in re.finditer(r"^\s*#\s*(?:if|elif|ifdef|ifndef)\b([^\n]*)$",
                             text, re.MULTILINE):
            cond_syms |= set(re.findall(r"[A-Za-z_]\w*", m.group(1)))

    group_leaders: set[str] = set()
    for m in PRAGMA_KW_RE.finditer(stripped):
        opts = [o for o in m.group(1).split()
                if o.startswith("_") and len(o) > 1]
        if len(opts) > 1:
            group_leaders.add(opts[0])

    for kw in sorted(declared_kw):
        if kw in BUILTIN_KEYWORDS or kw in group_leaders or kw in cond_syms:
            continue
        issues.append(Issue(
            "W109", shader_path, 1,
            f"キーワード '{kw}' はどの `#if` にも現れない。"
            f" **コードが同一のバリアントが倍に増えるだけ**で絵は変わらない。"
            f" 使わないなら pragma から外すこと"))

    # --- W111 ---------------------------------------------------------------
    # **Renderer Feature が探す名前がずれると、何も描かないまま静かに通る。**
    #
    # `ShaderTagId("X")` は LightMode を、`FindPass("X")` は Pass 名を探す。
    # 見つからなくても例外は出ず、**描画対象が 0 件になるだけ。**
    # 絵は「その機能を入れる前」と同じなので、設定を疑い続けることになる
    # ── W107（プロパティ名のずれ）と同じ形で、対象が別（T-267）。
    #
    # 実際に名前を振り直しており（T-249 で ToonOutline → IdolOutline）、
    # 機能側は追随していたが**診断メッセージが古いまま**だった。
    # 文字列は一致するまで誰も気付かない。
    #
    # **ツリーの全シェーダーを合算すること。** この関数はシェーダーごとに走るので、
    # 現在のシェーダーだけを見ると、同じ部屋の別シェーダー（Hidden の画面空間輪郭など）
    # の番で「宣言されているのは (無し)」という誤検出になる ── 実際に出した。
    # C# の走査範囲は部屋単位なので、突き合わせる相手も部屋単位で揃える。
    tree_shaders = sorted(shader_path.parent.rglob("*.shader"))
    lightmodes: set[str] = set()
    pass_names: set[str] = set()
    for sh in tree_shaders:
        sh_text = strip_comments(sh.read_text(encoding="utf-8", errors="replace"))
        lightmodes |= set(re.findall(r'"LightMode"\s*=\s*"(\w+)"', sh_text))
        pass_names |= set(re.findall(r'^\s*Name\s+"([^"]+)"', sh_text, re.MULTILINE))

    # 同じ C# を部屋のシェーダーの数だけ見ることになるので、代表 1 枚のときだけ出す。
    if tree_shaders and shader_path.resolve() != tree_shaders[0].resolve():
        lightmodes = pass_names = None   # type: ignore[assignment]

    # **リテラルだけを見ても届かない。** 実際の呼び出しは
    # `material.FindPass(ShadowPassName)` のように**定数経由**で、
    # `"..."` を探す正規表現では 1 件も当たらない ── 最初そう書いて、
    # 検査のカバー率が実質ゼロだった。同じファイル内の
    # `const string` / `static readonly string` を辿る。
    CONST_RE = re.compile(
        r'(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"([^"]*)"')
    unresolved = 0

    for cs_root in (code_roots(shader_path.parent) if lightmodes is not None else []):
        for cs in sorted(cs_root.rglob("*.cs")):
            cs_text = cs.read_text(encoding="utf-8", errors="replace")
            consts = dict(CONST_RE.findall(cs_text))

            def arg(raw: str) -> str | None:
                """呼び出しの引数を文字列に解決する。辿れなければ None。"""
                raw = raw.strip()
                if raw.startswith('"') and raw.endswith('"'):
                    return raw[1:-1]
                return consts.get(raw)

            for pat, kind in ((r'ShaderTagId\(\s*([^),]+?)\s*\)', "tag"),
                              (r'FindPass(?:Index)?\(\s*([^),]+?)\s*\)', "pass")):
                for m in re.finditer(pat, cs_text):
                    got = arg(m.group(1))
                    if got is None:
                        unresolved += 1
                        continue
                    if kind == "tag":
                        if got in lightmodes or got in URP_BUILTIN_LIGHTMODES:
                            continue
                        issues.append(Issue(
                            "W111", cs, line_of(cs_text, m.start()),
                            f"ShaderTagId(\"{got}\") に一致する LightMode が無い。"
                            f" **例外は出ず、描画対象が 0 件になるだけ。**"
                            f" 宣言されているのは: "
                            f"{', '.join(sorted(lightmodes)) or '(無し)'}"))
                    else:
                        if got in pass_names:
                            continue
                        issues.append(Issue(
                            "W111", cs, line_of(cs_text, m.start()),
                            f"FindPass(\"{got}\") に一致する Pass 名が無い。"
                            f" **-1 が返るだけで例外は出ず、その描画が黙って消える。**"
                            f" 宣言されているのは: "
                            f"{', '.join(sorted(pass_names)) or '(無し)'}"))

    # **辿れなかったものを黙らない。** 変数や連結で組み立てていると解決できず、
    # 「見た」と「見ていない」が区別できなくなる。
    if lightmodes is not None and unresolved:
        issues.append(Issue(
            "W111", shader_path, 1,
            f"ShaderTagId / FindPass の引数 {unresolved} 件は定数を辿れず**未検査**。"
            f" 変数や文字列連結で組み立てているものは、名前がずれても見つけられない"))

    # --- W110 ---------------------------------------------------------------
    # **形を持つパスは全部、同じ画素を切らないといけない。**
    #
    # ディゾルブはキーワードを持たない（`_DissolveAmount > 0` の一様分岐）ので、
    # パスを足したときに**書き忘れてもコンパイルは通る。** 通ってしまうから、
    # 実際に 3 パスが切っていなかった ── 髪の落ち影・速度・輪郭（T-264）。
    #
    # 現れ方が経路ごとに違い、どれも原因に辿り着きにくい:
    #   - 影を焼く側が切らない  → **消えた部分の落ち影だけが残る**
    #   - 速度を書く側が切らない → TAA が尾を引く（AA の設定を疑うことになる）
    #   - 輪郭が切らない        → 消えた本体の輪郭だけが宙に残る
    #
    # 判定は「アルファテストで切っているパス」＝ 形を持つパスに限る。
    # `_ALPHATEST_ON` を持つなら、そのパスは幾何をラスタライズしている。
    # `combined` の要素は (SourceFile, text)。**Path ではない** ──
    # `as_posix()` を呼んで E000（検査中の例外）で落ちた。
    for pass_src, pass_text in combined:
        pass_path = pass_src.path
        if "Passes/" not in pass_path.as_posix().replace("\\", "/"):
            continue
        if "_ALPHATEST_ON" not in pass_text:
            continue                    # 形を持たないパス（あれば）は対象外
        # **見るのは「ゲート」であって呼び出し名ではない。**
        # Idol は `_DissolveAmount > 0` の一様分岐、Cel は
        # `#if defined(_DISSOLVE_ON)` でゲートする。名前は違うが、
        # どちらも「切るかどうかを決めている場所」なのでここを見れば足りる。
        #
        # 一度**呼び出し名（`ApplyCelDissolveClip` など）も許す**形にしたが、
        # それだと**分岐を殺す欠陥を見逃す** ── 自己診断が注入するのは
        # `if (_DissolveAmount > 0.0)` → `if (false)` で、呼び出しは残るからだ。
        # 自己診断が「注入しても増えない」と教えてくれた。
        if "_DissolveAmount" in pass_text or "_DISSOLVE_ON" in pass_text:
            continue
        issues.append(Issue(
            "W110", pass_path, 1,
            f"このパスはアルファテストで切っているのにディゾルブを切っていない。"
            f" **消えたはずの画素がこのパスにだけ残る** ──"
            f" 影なら落ち影が、速度なら TAA の尾が、輪郭なら線が残る。"
            f" `_DissolveAmount > 0` の分岐で `ToonDissolveClip` を呼ぶこと"))

    # --- W107 ---------------------------------------------------------------
    # **Editor スクリプトの文字列がシェーダー側と一致しないと、黙って何もしない。**
    # `HasFloat` / `HasProperty` で守ってあるぶん例外も警告も出ず、
    # 「押しても何も起きない」という形でしか現れない。
    #
    # 実際に踏んだ（T-155）: マテリアルの絞り込みを
    # `m.shader.name.Contains("ToonPBR")` と書いていたが、**シェーダー名は
    # `Toon/URP/CharacterPBR`** でファイル名とは別物。プリセットウィンドウ全体と
    # 診断のマテリアル検査が、対象 0 件のまま**一度も動いていなかった。**
    #
    # 見るのは2つ:
    #   - `"_Xxx"` というリテラルが Properties かキーワードに実在するか
    #   - `shader.name.Contains("...")` の引数が実際のシェーダー名の一部か
    shader_name_m = re.search(r'^\s*Shader\s+"([^"]+)"', stripped, re.MULTILINE)
    shader_name = shader_name_m.group(1) if shader_name_m else ""

    # **CustomEditor を持つシェーダーのときだけ走らせる。**
    # シェーダーごとに呼ばれるので、絞らないと ToonScreenOutline（プロパティが少ない）の
    # 文脈で ToonPBR のプロパティ名が全部「存在しない」と出る ── 実際に出た。
    # Editor スクリプトが対象にしているのは CustomEditor を宣言したシェーダーだけ。
    # 同名の部屋（`Editor/Idol/`）そのものが Editor スクリプトの置き場。
    editor_dir = next((r for r in code_roots(shader_path.parent)
                       if any(r.glob("*.cs")) and "Editor" in r.parts),
                      shader_path.parent / "Editor")
    if editor_dir.is_dir() and CUSTOM_EDITOR_RE.search(stripped):
        keyword_set = {
            t
            for m in re.finditer(r"#pragma\s+shader_feature_local\w*\s+(.+)", stripped)
            for t in m.group(1).split()
            if t.startswith("_")
        }

        for cs in sorted(editor_dir.glob("*.cs")):
            text = cs.read_text(encoding="utf-8", errors="replace")

            # **移行スクリプトは他所のシェーダーのプロパティ名を正当に書く。**
            # `// lint:foreign-begin` 〜 `// lint:foreign-end` で挟んだ範囲は
            # W107 の対象から外す。
            #
            # ファイル単位で外さないのは、**同じファイルの書き込み側**
            # （移行先である ToonPBR のプロパティ名）は見たいから。
            # そこを誤字ると「移行したのに値が入っていない」という、
            # まさに W107 が撃とうとしている形になる。
            foreign: list[tuple[int, int]] = []
            for beg in re.finditer(r"//\s*lint:foreign-begin", text):
                end = re.search(r"//\s*lint:foreign-end", text[beg.end():])
                foreign.append((beg.start(),
                                beg.end() + end.end() if end else len(text)))

            def in_foreign(pos: int) -> bool:
                return any(a <= pos < b for a, b in foreign)

            # **プロパティ名として使われている文字列だけを見る。**
            # シェーダーと無関係な "_Xxx" もソースにはある
            # （`private const string Suffix = "_SmoothNormals";` が実際にあった）。
            # 直前の文脈を正規表現で見るのは脆いので、
            # **既知の呼び出しの引数だけ**を対象にする。
            for call in PROPERTY_CALL_RE.finditer(text):
                if in_foreign(call.start()):
                    continue
                for lit in PROP_LITERAL_RE.finditer(call.group(2)):
                    name = lit.group(1)
                    if name in props or name in keyword_set:
                        continue

                    issues.append(Issue(
                        "W107", cs, line_of(text, call.start()),
                        f"'{name}' は Properties にもキーワードにも無い"
                        f"（{call.group(1)} の引数）。"
                        f"Unity 側は HasProperty で守られて**黙って何もしない**ので、"
                        f"名前を間違えても気付けない"))

            for m in re.finditer(r'shader\.name\.Contains\("([^"]+)"\)', text):
                needle = m.group(1)
                if needle and needle in shader_name:
                    continue
                issues.append(Issue(
                    "W107", cs, line_of(text, m.start()),
                    f"シェーダー名の判定 '{needle}' が実際の名前 '{shader_name}' に含まれない。"
                    f"**対象 0 件のまま黙って動かなくなる**（T-155 で実際に起きた）"))

    # --- W107（Runtime 側）--------------------------------------------------
    # Runtime の Renderer Feature / コンポーネントも同じ脆さを持つ。
    # `PropertyToID` の名前が違えば **SetGlobal しても誰も読まない**し、
    # `Shader.Find` の名前が違えば null が返って機能ごと無効になる。
    #
    # **名前の集合はディレクトリ内の全シェーダーから作る。**
    # Runtime は ToonScreenOutline の CBUFFER 変数（Hidden なので Properties に無い）や、
    # スクリプトから設定するグローバルも参照する。片方のシェーダーだけで判定すると
    # 誤検出になる ── Editor 側の W107 を最初に書いたとき実際にそれで 90 件出した。
    runtime_dir = next((r for r in code_roots(shader_path.parent)
                        if any(r.glob("*.cs")) and "Runtime" in r.parts),
                       shader_path.parent / "Runtime")
    if runtime_dir.is_dir() and CUSTOM_EDITOR_RE.search(stripped):
        known: set[str] = set()
        shader_names: set[str] = set()

        for f in sorted(shader_path.parent.glob("*.shader")) +                  sorted(shader_path.parent.glob("*.hlsl")):
            txt = strip_comments(f.read_text(encoding="utf-8", errors="replace"))

            known |= {m.group(2) for m in PROP_RE.finditer(txt)}
            known |= {m.group(1) for m in CBUFFER_DECL_RE.finditer(txt)}
            known |= {m.group(1) for m in TEXTURE_DECL_RE.finditer(txt)}

            nm = re.search(r'^\s*Shader\s+"([^"]+)"', txt, re.MULTILINE)
            if nm:
                shader_names.add(nm.group(1))

        for cs in sorted(runtime_dir.glob("*.cs")):
            text = cs.read_text(encoding="utf-8", errors="replace")

            for m in re.finditer(r'PropertyToID\("(_[A-Za-z]\w*)"\)', text):
                if m.group(1) in known:
                    continue
                issues.append(Issue(
                    "W107", cs, line_of(text, m.start()),
                    f"'{m.group(1)}' はどのシェーダーにも宣言が無い。"
                    f"SetGlobal しても読む側が居ないので**黙って効かない**"))

            for m in re.finditer(r'Shader\.Find\("([^"]+)"\)', text):
                if m.group(1) in shader_names:
                    continue
                issues.append(Issue(
                    "W107", cs, line_of(text, m.start()),
                    f"Shader.Find('{m.group(1)}') に一致するシェーダーがこのディレクトリに無い。"
                    f"null が返って**機能ごと無効になる**"))

    # --- W104  ShaderGUI の網羅 --------------------------------------------
    check_shader_gui_coverage(shader_path, stripped, props, issues)

    # --- E007 -------------------------------------------------------------
    uses_scene_depth = any(
        re.search(r"(?<![\w])(SampleSceneDepth|LoadSceneDepth)\s*\(", t) for _, t in combined
    )
    lightmodes = {m.group(1) for m in LIGHTMODE_RE.finditer(stripped)}
    if uses_scene_depth and "DepthOnly" not in lightmodes:
        issues.append(Issue(
            "E007", shader_path, 1,
            "シーン深度を読んでいるが DepthOnly パスが無い。"
            "自分自身が深度テクスチャに書き込まれずリムが破綻する",
        ))


def check_shader_gui_coverage(
    shader_path: Path,
    stripped: str,
    props: dict[str, tuple[str, str, int]],
    issues: list[Issue],
) -> None:
    """カスタム ShaderGUI が描画しないプロパティを見つける。

    ShaderGUI は描くプロパティを明示列挙するのが普通で、Properties に足して
    GUI 側に足し忘れると「インスペクタから消えたまま検査は全部通る」という
    一番気付きにくい壊れ方をする。

    判定は ShaderGUI のソースに現れる "_Xxx" というリテラルの集合との突き合わせ。
    コンパイラではないので、キーワード同期だけで参照していて実際には描いていない
    プロパティは見逃す。それでも「足し忘れ」は確実に捕まる。
    """
    m = CUSTOM_EDITOR_RE.search(stripped)
    if not m:
        return

    class_name = m.group(1).rsplit(".", 1)[-1]

    # **同じ階層の下だけでは足りない。** パッケージでは Editor スクリプトが
    # `Editor/<名前>/` へ行き、シェーダーの下には無い（T-252）。
    # **ここはパッケージ全体を見る。**
    # `code_roots` は「シェーダーのフォルダ名と同じ名前の部屋」だけを見るが、
    # それは**プロパティの検査**が隣のシェーダーのスクリプトを拾って
    # 57 件の誤検出を出したからで、ここの事情とは違う。
    # W104 が訊いているのは「そのクラスがどこかに在るか」だけで、
    # クラス名は十分に一意なので取り違えようがない。
    #
    # 実際 Cel の GUI は `Editor/CelShaderGUI.cs`（`Editor/Cel/` ではない）に
    # 置かれていて、**実在するのに「見つからない」と報告していた。**
    matches: list[Path] = []
    for r in code_roots(shader_path.parent):
        matches += list(r.rglob(f"{class_name}.cs"))
    if not matches:
        for parent in shader_path.resolve().parents:
            if (parent / "package.json").exists():
                matches += list(parent.rglob(f"{class_name}.cs"))
                break
    if not matches:
        issues.append(Issue(
            "W104", shader_path, 1,
            f"CustomEditor に '{class_name}' を指定しているが {class_name}.cs が見つからない。"
            "インスペクタが既定表示に落ちていないか確認する",
        ))
        return

    literals: set[str] = set()
    for path in matches:
        literals |= set(CS_PROP_LITERAL_RE.findall(path.read_text(encoding="utf-8", errors="replace")))

    for name, (_ptype, attrs, ln) in sorted(props.items(), key=lambda kv: kv[1][2]):
        if "hideininspector" in attrs.lower():
            continue
        if name in literals:
            continue
        issues.append(Issue(
            "W104", shader_path, ln,
            f"'{name}' が {class_name} から参照されていない。"
            "カスタムインスペクタでは表示されない可能性が高い",
        ))


# ---------------------------------------------------------------------------
# エントリポイント
# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description="URP シェーダーの静的検査")
    ap.add_argument("paths", nargs="*", default=["Assets"], help="検査対象のファイルまたはディレクトリ")
    ap.add_argument("--strict", action="store_true", help="警告も失敗として扱う")
    args = ap.parse_args()

    root = Path.cwd()
    targets: list[Path] = []
    for raw in args.paths:
        p = Path(raw)
        if p.is_dir():
            targets.extend(sorted(p.rglob("*.shader")))
        elif p.is_file() and p.suffix in SHADER_EXT:
            targets.append(p)

    if not targets:
        print("検査対象の .shader が見つかりません", file=sys.stderr)
        return 2

    issues: list[Issue] = []
    for shader in targets:
        try:
            lint_shader(shader, issues)
        except Exception as exc:                    # noqa: BLE001
            issues.append(Issue("E000", shader, 1, f"検査中に例外: {exc}"))

    issues.sort(key=lambda i: (str(i.path), i.line, i.code))
    for issue in issues:
        print(issue.render(root))

    errors = sum(1 for i in issues if i.severity == "error")
    warnings = len(issues) - errors
    print(f"\n{len(targets)} 個のシェーダーを検査: エラー {errors} 件 / 警告 {warnings} 件")

    if errors:
        return 1
    if warnings and args.strict:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

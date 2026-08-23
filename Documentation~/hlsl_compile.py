#!/usr/bin/env python3
"""Unity を起動せずに HLSL を**実コンパイル**する。

Windows に必ず入っている `d3dcompiler_47.dll`（fxc の本体）を ctypes で叩く。
URP のシェーダーライブラリは `Library/PackageCache/` にそのまま置いてあるので、
`#include` を解決してやれば **Unity が読むのと同じソースを同じヘッダで**
コンパイルできる。

**なぜ要るか。** Editor が起動していると batchmode はグローバルキャッシュの
ロックで**そもそも起動できない**（CLAUDE.md 参照）。Editor 自身のログを読む
`editor_log_check.py` は、ユーザーが Unity にフォーカスを戻すまで何も出ない。
その間、型エラーや未宣言は**一切検証できなかった。**

これで埋まるもの:

  - HLSL の型エラー（`float3` に `float3x3` を渡す類）
  - 未宣言の識別子、関数の定義順、include 順
  - サンプラ・定数バッファなどのリソース上限（T-072 で実機が落ちた類）
  - キーワードの組み合わせでだけ壊れるもの（T-085 で Hair が全滅した類）

**Unity のコンパイルと同一ではない。** 違いは意識して受け入れている:

| | |
|---|---|
| バックエンド | fxc（D3D11）のみ。Vulkan / Metal / GLES は見ない |
| プラットフォーム定義 | Editor.log に出ていたものを写した。Unity 側が増やしたら食い違う |
| `#include_with_pragmas` | Unity 専用の指令。素の `#include` に読み替える（中の pragma は fxc が無視するだけ）|
| `#define NAME()` | **fxc は引数ゼロの関数形式マクロを解釈できない。** core の `PopMarker` がこれで、どこからも使われていないので落として通す |
| `half` のグローバル | fxc は `ps_5_0` で拒否する。Unity は許すので後方互換フラグを立てて合わせる |

つまり**「fxc が通る」は「Unity が通る」の十分条件ではない。**
だが**落ちたものは本物**で、通ったものは型と宣言に関しては確からしい。

使い方:
    cd Assets/ToonPBR
    python hlsl_compile.py                # 既定バリアントで全パス
    python hlsl_compile.py --variants     # キーワードの組を回す（C# の表を読む）
    python hlsl_compile.py --pass ForwardLit
"""
from __future__ import annotations

import argparse
import ctypes
import re
import sys
from pathlib import Path

HRESULT = ctypes.c_long
OPEN_FN = ctypes.WINFUNCTYPE(HRESULT, ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p,
                             ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
                             ctypes.POINTER(ctypes.c_uint))
CLOSE_FN = ctypes.WINFUNCTYPE(HRESULT, ctypes.c_void_p, ctypes.c_void_p)

D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY = 1 << 12

# **Editor.log のコンパイル記録から写した。** 推測ではなく実際に Unity が
# 出していた行（`Platform defines:`）。Unity 側が増やしたらここも増やすこと。
BASE_DEFINES = [
    ("SHADER_API_D3D11", "1"), ("SHADER_API_DESKTOP", "1"),
    ("SHADER_TARGET", "45"), ("UNITY_VERSION", "600003"),
    ("UNITY_COMPILER_HLSL", "1"),
    ("UNITY_ENABLE_DETAIL_NORMALMAP", "1"), ("UNITY_ENABLE_REFLECTION_BUFFERS", "1"),
    ("UNITY_LIGHTMAP_FULL_HDR", "1"), ("UNITY_LIGHT_PROBE_PROXY_VOLUME", "1"),
    ("UNITY_PBS_USE_BRDF1", "1"), ("UNITY_PLATFORM_SUPPORTS_DEPTH_FETCH", "1"),
    ("UNITY_SPECCUBE_BLENDING", "1"), ("UNITY_SPECCUBE_BOX_PROJECTION", "1"),
    ("UNITY_USE_DITHER_MASK_FOR_ALPHABLENDED_SHADOWS", "1"),
]

# fxc が解釈できない `#define NAME()`。行ごと落とす。
EMPTY_MACRO_RE = re.compile(r"^([ \t]*#define[ \t]+\w+\(\)[ \t]*)$", re.MULTILINE)


class MACRO(ctypes.Structure):
    _fields_ = [("Name", ctypes.c_char_p), ("Definition", ctypes.c_char_p)]


class Includer:
    """`ID3DInclude`。COM ではなく Open/Close の 2 エントリだけの vtable。

    **自前で展開しないのが要点。** `#if` の中の include を無条件に展開すると
    別物になるので、コンパイラ側の前処理に解決させる。
    """

    def __init__(self, root: Path, packages: dict[str, Path], project: Path | None = None):
        self.root = root
        self.packages = packages
        self.project = project or root
        self.alive: dict[int, tuple] = {}
        self.opened: list[Path] = []
        self.missing: list[str] = []
        self._open = OPEN_FN(self._on_open)
        self._close = CLOSE_FN(self._on_close)
        self.vtbl = (ctypes.c_void_p * 2)(
            ctypes.cast(self._open, ctypes.c_void_p),
            ctypes.cast(self._close, ctypes.c_void_p))
        self.obj = ctypes.c_void_p(ctypes.addressof(self.vtbl))
        self.ptr = ctypes.pointer(self.obj)

    def resolve(self, name: str, parent: Path | None) -> Path | None:
        name = name.replace("\\", "/")
        if name.startswith("Packages/"):
            # **ローカルパッケージも見ること。** `Packages/` 直下に実体があるものは
            # `Library/PackageCache/` に写されない。EasyShaderCore がこれで、
            # 見落とすと「include を開けない」で全滅する（T-248）。
            parts = name.split("/", 2)
            pkg = self.packages.get(parts[1])
            if pkg:
                return pkg / parts[2]
            local = self.project / "Packages" / parts[1] / parts[2]
            return local if local.exists() else None
        bases = ([parent.parent] if parent else []) + [self.root]
        for base in bases:
            p = base / name
            if p.exists():
                return p
        return None

    def _on_open(self, this, itype, fname, parent_data, out_data, out_bytes):
        name = fname.decode()
        parent = self.alive.get(parent_data, (None, None))[1] if parent_data else None
        path = self.resolve(name, parent)
        if path is None or not path.exists():
            self.missing.append(name)
            return -1
        # **ここを憶えても速くならない**（実測。T-324）。
        # 180 プログラム × 各 20 数本ぶん読み直しているので効きそうに見えるが、
        # 全組の 143 秒はほぼ **fxc の最適化そのもの**で、
        # 中身を憶えても 144 秒 / 20 → 18 秒と誤差だった。
        # **効果を示せない複雑さは足さない。**
        text = path.read_text(encoding="utf-8", errors="replace")
        data = EMPTY_MACRO_RE.sub("", text).encode("utf-8")
        buf = ctypes.create_string_buffer(data, len(data))
        addr = ctypes.cast(buf, ctypes.c_void_p).value
        self.alive[addr] = (buf, path)
        self.opened.append(path)
        out_data[0] = addr
        out_bytes[0] = len(data)
        return 0

    def _on_close(self, this, data):
        return 0


def blob_text(blob) -> str:
    """ID3DBlob の中身。vtable は QI/AddRef/Release/GetBufferPointer/GetBufferSize。"""
    if not blob:
        return ""
    vt = ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p)).contents.value
    fns = ctypes.cast(vt, ctypes.POINTER(ctypes.c_void_p))
    getp = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(fns[3])
    getsz = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(fns[4])
    return ctypes.string_at(getp(blob), getsz(blob)).decode(errors="replace")


def find_project(root: Path) -> Path:
    for parent in root.parents:
        if (parent / "Library" / "PackageCache").is_dir():
            return parent
    raise FileNotFoundError(
        "Library/PackageCache が見つからない。URP のシェーダーライブラリが"
        " 無いとコンパイルできない（一度 Unity でプロジェクトを開くこと）")


def find_main_shader(root: Path) -> Path | None:
    """このツリーの主シェーダー（`Hidden/` でない `.shader`）。

    **ファイル名を決め打ちしないこと。** 以前は `root / "ToonPBR.shader"` と
    書いていたが、シェーダー名を `Idol` へ振り直した以上ファイル名も変わりうるし、
    パッケージへ移せば場所も変わる。**決め打ちは移した瞬間に空振りする**（T-250）。

    見つからなければ None。呼び出し側は黙って通さずスキップすること。
    """
    best: Path | None = None
    for p in sorted(root.rglob("*.shader")):
        m = re.search(r'^\s*Shader\s+"([^"]+)"',
                      p.read_text(encoding="utf-8", errors="replace"), re.MULTILINE)
        if m and not m.group(1).startswith("Hidden/"):
            if best is None or len(p.parts) < len(best.parts):
                best = p
    return best


def parse_passes(shader: Path) -> list[dict]:
    txt = shader.read_text(encoding="utf-8", errors="replace")

    # **`HLSLINCLUDE` は全パスの先頭に注入される。**
    # SubShader 直下に置くと Unity が各 `HLSLPROGRAM` の頭へ差し込む仕組みで、
    # 共通の宣言（CBUFFER・テクスチャ）をここへ集めるのは普通の書き方。
    #
    # これを読んでいなかったので、**Cel は 14 プログラム中 4 つが
    # 「未宣言の識別子」で落ち続けていた** ── 宣言は `CelInput.hlsl` に
    # ちゃんとあるのに、道具がその include を見ていなかっただけ。
    # **落ちたものは本物、と言っている道具が偽の失敗を出していた**ことになり、
    # 「Cel の fxc は当てにならない」と学習させる方向に働く。
    common = ""
    for m in re.finditer(r"\bHLSLINCLUDE\b", txt):
        e = txt.find("ENDHLSL", m.end())
        if e < 0:
            continue
        common += txt[m.end():e] + "\n"

    out = []
    for m in re.finditer(r'Name\s+"(\w+)"', txt):
        s = txt.find("HLSLPROGRAM", m.end())
        e = txt.find("ENDHLSL", s)
        if s < 0 or e < 0:
            continue
        body = common + txt[s + len("HLSLPROGRAM"):e]
        # Unity 専用の指令。中の pragma は fxc が無視するだけなので読み替えて良い
        body = body.replace("#include_with_pragmas", "#include")
        vert = re.search(r"#pragma\s+vertex\s+(\w+)", body)
        frag = re.search(r"#pragma\s+fragment\s+(\w+)", body)
        out.append({"name": m.group(1), "body": body,
                    "vert": vert.group(1) if vert else None,
                    "frag": frag.group(1) if frag else None})
    return out


def derive_combos(body: str) -> list[tuple[str, list[str]]]:
    """**表が無いパスは、宣言しているキーワードから組を作る。**

    以前はここが `[("既定", [])]` で、表に載っていないパスは
    **既定の 1 組しか通していなかった。** Cel の `ForwardLit` がそれで、
    **Forward+ を切った変種で落ちるバグ（T-315）はここを素通りしていた。**

    そのバグを起こしたのは `_ADDITIONAL_LIGHTS` ── `shader_feature` ではなく
    **`multi_compile`**（パイプラインが立てる側）だったので、
    材質のキーワードだけ見ていても届かない。

    **全組は取らない。** 掛け合わせると簡単に数百になるので、

      素        … 何も立てない
      1 つずつ  … 群ごとに先頭の値だけを立てる
      全部      … 各群の先頭を同時に立てる

    の形にする。「この 1 つを立てると落ちる」と
    「全部立てると落ちる」は、これで両方拾える。
    """
    groups: list[list[str]] = []
    for m in re.finditer(r"^\s*#\s*pragma\s+(?:shader_feature|multi_compile)\w*\s+(.*)$",
                         body, re.M):
        # **行末のコメントを落とすこと。** 落とさずに拾ったせいで
        # コメント中の `_HairSeeThroughAlpha`（**マテリアルのプロパティ**）を
        # キーワードとして立て、`half _HairSeeThroughAlpha;` の宣言が
        # マクロで置き換わって**構文エラーになった** ── 検査側の誤り。
        text = re.sub(r"//.*", "", m.group(1))
        # **キーワードは全部大文字**（Unity の慣習）。混ざり書きは
        # プロパティ名なので、コメントを落とし損ねてもここで止まる。
        toks = [t for t in text.split()
                if t.startswith("_") and len(t) > 1 and t.upper() == t]
        # 4 つを超える列挙（プラットフォーム系）は組合せの意味が薄いので外す
        if toks and len(toks) <= 4:
            groups.append(toks)
    if not groups:
        return [("既定", [])]

    combos: list[tuple[str, list[str]]] = [("素", [])]
    for g in groups:
        combos.append((g[0].strip("_").lower()[:14], [g[0]]))
    combos.append(("全部", [g[0] for g in groups]))
    return combos


def merge_combos(table: list[tuple[str, list[str]]],
                 derived: list[tuple[str, list[str]]]) -> list[tuple[str, list[str]]]:
    """表と宣言から作った組を混ぜる（同じキーワード集合は 1 つに）。"""
    out: list[tuple[str, list[str]]] = []
    seen: set[frozenset[str]] = set()
    for label, kws in list(table) + list(derived):
        key = frozenset(kws)
        if key in seen:
            continue
        seen.add(key)
        out.append((label, kws))
    return out or [("既定", [])]


def parse_variant_table(cs: Path) -> tuple[list, list, dict]:
    """`ToonPBRVariantCheck.cs` のキーワード表を読む。

    **表を書き写さない。** Python 側に持つと C# 側を直したときに古くなる
    ── このプロジェクトの持病そのもの（T-167 / T-200）。
    """
    if not cs.exists():
        return [], [], {}
    src = cs.read_text(encoding="utf-8", errors="replace")

    def block(anchor: str) -> str:
        i = src.find(anchor)
        return src[i:src.find("};", i)] if i >= 0 else ""

    surfaces = re.findall(r'"(_SURFACETYPE_\w+)"', block("SurfaceTypes ="))

    def sets(text: str) -> list[tuple[str, list[str]]]:
        got = []
        for m in re.finditer(r'\("([^"]+)",\s*new\s*(?:string\[0\]|\[\]\s*\{([^}]*)\})', text):
            kws = re.findall(r'"(\w+)"', m.group(2) or "")
            got.append((m.group(1), kws))
        return got

    features = sets(block("FeatureSets ="))

    passes: dict[str, list[tuple[str, list[str]]]] = {}
    pblock = block("PassSets =")
    for m in re.finditer(r'\["(\w+)"\]\s*=\s*new\[\]\s*\{', pblock):
        end = pblock.find("},\n", m.end())
        seg = pblock[m.end():end if end > 0 else len(pblock)]
        passes[m.group(1)] = sets(seg)
    return surfaces, features, passes


class Compiler:
    def __init__(self, root: Path):
        self.root = root
        self.project = find_project(root)
        cache = self.project / "Library" / "PackageCache"
        self.packages = {d.name.split("@")[0]: d
                         for d in cache.iterdir() if d.is_dir() and "@" in d.name}
        try:
            self.d3d = ctypes.WinDLL("d3dcompiler_47.dll")
        except OSError as e:                       # pragma: no cover
            raise FileNotFoundError(f"d3dcompiler_47.dll をロードできない: {e}")
        self.last_cost = (-1, -1)
        self.last_res = (-1, -1, -1)
        self.last_code = b""
        self.d3d.D3DCompile.argtypes = [
            ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p,
            ctypes.POINTER(MACRO), ctypes.c_void_p, ctypes.c_char_p,
            ctypes.c_char_p, ctypes.c_uint, ctypes.c_uint,
            ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
        self.d3d.D3DDisassemble.argtypes = [
            ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint,
            ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]

    def bytecode(self, blob) -> bytes:
        """コンパイル結果のバイト列。**死に重みの判定に使う。**"""
        vt = ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p)).contents.value
        fns = ctypes.cast(vt, ctypes.POINTER(ctypes.c_void_p))
        getp = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(fns[3])
        getsz = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(fns[4])
        return ctypes.string_at(getp(blob), getsz(blob))

    def measure(self, blob) -> tuple[int, int]:
        """コンパイル済みバイトコードから (命令スロット数, 一時レジスタ数)。

        fxc は逆アセンブルの末尾に `Approximately N instruction slots used` を
        出す。**推定ではなく、実際に生成された命令の数。**
        一時レジスタ（`dcl_temps`）は占有率に直結する。
        """
        vt = ctypes.cast(blob, ctypes.POINTER(ctypes.c_void_p)).contents.value
        fns = ctypes.cast(vt, ctypes.POINTER(ctypes.c_void_p))
        getp = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(fns[3])
        getsz = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(fns[4])

        dis = ctypes.c_void_p()
        hr = self.d3d.D3DDisassemble(getp(blob), getsz(blob), 0, None,
                                     ctypes.byref(dis))
        if (hr & 0xffffffff) != 0:
            return -1, -1
        # **資源の実測。** `ps_4_0` のサンプラは 16 本しかなく、超えると
        # 実機で落ちる（T-072 で実際に落ちた）。W105 は自前の `SAMPLER()` 宣言を
        # 数えるだけで、**URP が使うぶんが見えない。** ここで数えるのは
        # コンパイラが実際に割り当てた本数。
        text = blob_text(dis)
        self.last_res = (
            len(set(re.findall(r"^\s*dcl_sampler\s+(s\d+)", text, re.M))),
            len(set(re.findall(r"^\s*dcl_resource_texture\w*\s*\([^)]*\)\s+(t\d+)",
                               text, re.M))),
            len(set(re.findall(r"^\s*dcl_constantbuffer\s+CB(\d+)", text, re.M))),
        )
        slots = temps = -1
        for line in text.splitlines():
            m = re.search(r"Approximately (\d+) instruction slots used", line)
            if m:
                slots = int(m.group(1))
            m = re.match(r"\s*dcl_temps (\d+)", line)
            if m:
                temps = int(m.group(1))
        return slots, temps

    def compile(self, body: str, name: str, entry: str, profile: str,
                keywords: list[str], stage: str,
                measure: bool = False) -> tuple[bool, list[str]]:
        inc = Includer(self.root, self.packages, self.project)
        macros = BASE_DEFINES + [(stage, "1")] + [(k, "1") for k in keywords]
        arr = (MACRO * (len(macros) + 1))()
        for i, (k, v) in enumerate(macros):
            arr[i].Name = k.encode()
            arr[i].Definition = v.encode()

        blob = ctypes.c_void_p()
        err = ctypes.c_void_p()
        data = body.encode("utf-8")
        hr = self.d3d.D3DCompile(
            data, len(data), name.encode(), arr,
            ctypes.cast(inc.ptr, ctypes.c_void_p),
            entry.encode(), profile.encode(),
            D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY, 0,
            ctypes.byref(blob), ctypes.byref(err))

        self.last_cost = self.measure(blob) if (measure and blob) else (-1, -1)
        self.last_code = self.bytecode(blob) if blob else b""

        msgs = [l.strip() for l in blob_text(err).splitlines()
                if ": error " in l or ": warning X" in l]
        # `#pragma vertex` などは fxc が知らない。指摘としては無意味なので落とす
        msgs = [l for l in msgs if "unknown pragma ignored" not in l]
        if inc.missing:
            msgs.append(f"include を解決できない: {', '.join(sorted(set(inc.missing)))}")
        return (hr & 0xffffffff) == 0, msgs


# **閉じ括弧まで要求する。** 数字だけを緩く取ると `1e-4` を `1` と読んで
# 黙って別の質問に答える（`> 1` を満たすマテリアルを数えてしまい、
# 実際は全件 ON のゲートを「全件 OFF」と報告した）。
# 最後まで食えない形（複合条件など）は**数えないほうがいい** ── None を返す。
GATE_RE = re.compile(
    r"if\s*\(\s*(_[A-Za-z]\w*)\s*([<>])\s*([-+]?[\d.]+(?:[eE][-+]?\d+)?)\s*\)")


def gate_usage(cond: str, mats: list[Path]) -> tuple[int, int] | None:
    """この条件を満たすマテリアルの数と、見たマテリアルの総数。

    **命令数だけでは判断できない。** 「その機能が何命令かかるか」と
    「出荷物で実際に使われているか」は別の問いで、後者が分からないと
    キーワード化が得かどうかが決まらない ── `shader_feature` は
    **使っているマテリアルが 1 つも無ければビルドに含まれない**ので、
    全マテリアルで OFF のゲートだけがバリアントを増やさずに消せる。

    条件が `_X > 0.5` の形でないとき（複合条件など）は None。
    """
    m = GATE_RE.search(cond)
    if not m or not mats:
        return None
    name, op, raw = m.group(1), m.group(2), float(m.group(3))
    pat = re.compile(rf"- {re.escape(name)}: ([-\d.eE+]+)")
    on = 0
    for f in mats:
        hit = pat.search(f.read_text(encoding="utf-8", errors="replace"))
        if hit is None:
            continue
        v = float(hit.group(1))
        if (v > raw) if op == ">" else (v < raw):
            on += 1
    return on, len(mats)


def branch_cost(root: Path, comp: "Compiler", pass_name: str = "ForwardLit",
                materials: Path | None = None) -> None:
    """**一様分岐で切っている機能が、OFF のときいくら占めているか**を測る。

    ToonPBR は既定 OFF の機能をキーワードではなく `UNITY_BRANCH if (_X > 0)` で
    持っている。**バリアントは増えないが、コードは常にコンパイルされる。**
    その代償がいくらかを知らずに「キーワードにしない」と決めるのは勘でしかない。

    測り方は素朴で確実 ── ツリーを複製し、分岐の条件を `false` に潰して
    測り直し、差を取る。潰せば分岐の中身ごと消えるので、差がそのまま
    「その機能を持っているせいで増えている命令数」になる。

    **キーワードにするかの判断材料。** 命令が大きければキーワードで切る価値があり、
    小さければバリアントを倍にする方が高くつく。
    """
    import shutil
    import tempfile

    # `UNITY_BRANCH` の直後の `if (...)` だけを対象にする。
    # 比較演算を含むものに絞るのは、ゲート（`_X > 0`）と
    # 制御フロー（`if (flip)`）を分けるため。
    BRANCH_RE = re.compile(
        r"UNITY_BRANCH\s*\n\s*(if\s*\([^)\n]*[><][^)\n]*\))")

    with tempfile.TemporaryDirectory(prefix="toonpbr_branch_") as td:
        sandbox = Path(td) / "tree"
        shutil.copytree(root, sandbox, ignore=shutil.ignore_patterns("__pycache__"))
        sub = Compiler.__new__(Compiler)
        sub.__dict__.update(comp.__dict__)
        sub.root = sandbox

        # **ファイル名を決め打ちしない。** ここは `sandbox / "ToonPBR.shader"` と
        # 書いてあり、T-249 で `Idol.shader` へ改名して以来 FileNotFoundError で
        # **落ちたまま誰も回していなかった**（T-259）。T-250 で同じ決め打ちを
        # 主経路からは消したのに、この関数だけ見に行っていない
        # ── T-253 / T-257 と同じ「片方だけ直す」形。
        shader = find_main_shader(sandbox)
        if shader is None:
            print("error: 主シェーダーが見つからない（.shader が 1 枚も無い）")
            return

        def measure() -> tuple[int, int]:
            ps = [x for x in parse_passes(shader) if x["name"] == pass_name]
            if not ps:
                return (-1, -1)
            p = ps[0]
            ok, _ = sub.compile(p["body"], "f.hlsl", p["frag"], "ps_5_0", [],
                                "SHADER_STAGE_FRAGMENT", measure=True)
            return sub.last_cost if ok else (-1, -1)

        base = measure()
        if base[0] < 0:
            print("error: 基準のコンパイルに失敗した")
            return
        print(f"\n=== 一様分岐の代償（{pass_name} / キーワード無し）===")
        print(f"基準: {base[0]:,} 命令 / 一時レジスタ {base[1]}")

        rows = []
        for rel in sorted(sandbox.rglob("*.hlsl")):
            text = rel.read_text(encoding="utf-8", errors="replace")
            for m in BRANCH_RE.finditer(text):
                cond = m.group(1)
                rel.write_text(text.replace(cond, "if (false)", 1),
                               encoding="utf-8", newline="")
                got = measure()
                rel.write_text(text, encoding="utf-8", newline="")
                if got[0] < 0 or got[0] == base[0]:
                    continue      # 潰しても変わらない＝この経路は測れない
                rows.append((base[0] - got[0], base[1] - got[1],
                             rel.relative_to(sandbox).as_posix(), cond))

        # **「消せるもの」と「消せないもの」を混ぜないこと。**
        # 以前は全部を足して「既定 OFF の機能が 67%」と出していたが、
        # その中には `probePosition.w > 0.0` のような**計算結果で切る制御フロー**が
        # 混ざっていた。プローブの重み判定はキーワードにできないので、
        # 合計を見た人は消せない分まで消せると思い込む（T-259）。
        #
        # 判定はマテリアルの uniform を参照しているか ── `_Xxx` 形の識別子。
        UNIFORM_RE = re.compile(r"\b_[A-Z]\w*\b")
        gated = [r for r in rows if UNIFORM_RE.search(r[3])]
        flow = [r for r in rows if not UNIFORM_RE.search(r[3])]

        mats = sorted(materials.glob("*.mat")) if materials and materials.is_dir() else []
        always_off = 0

        def dump(title: str, items: list, limit: int, cross: bool) -> None:
            nonlocal always_off
            if not items:
                return
            print(f"\n  {title}")
            head = f"  {'命令':>7}{'レジスタ':>9}  条件"
            print(head + ("                       マテリアル" if cross and mats else ""))
            for d, dt, where, cond in items[:limit]:
                note = ""
                if cross and mats:
                    use = gate_usage(cond, mats)
                    if use is None:
                        note = "  （形が違って数えられない）"
                    elif use[0] == 0:
                        note = f"  **{use[1]} 件すべて OFF**"
                        always_off += d
                    elif use[0] == use[1]:
                        note = f"  {use[1]} 件すべて ON"
                    else:
                        note = f"  混在 {use[0]}/{use[1]}"
                print(f"  {d:>7,}{dt:>9}  {cond[:42]:<44}[{where.split('/')[-1]}]{note}")
            # **切り捨てを黙らない。** 見えている行だけが全部だと読まれる。
            if len(items) > limit:
                rest = sum(r[0] for r in items[limit:])
                print(f"  （ほか {len(items) - limit} 件・計 {rest:,} 命令は非表示）")

        rows.sort(reverse=True)
        gated.sort(reverse=True)
        flow.sort(reverse=True)

        dump("マテリアルの値で切っている（キーワードにできる）", gated, 20, True)
        dump("計算結果で切っている（キーワードにできない）", flow, 6, False)

        total = sum(r[0] for r in gated if r[0] > 0)
        print(f"\n  キーワードにすれば消せる合計: {total:,} 命令 "
              f"（基準の {total * 100 // max(base[0], 1)}%）")
        if mats:
            print(f"  うち**マテリアル {len(mats)} 件すべてで OFF**: {always_off:,} 命令 "
                  f"（基準の {always_off * 100 // max(base[0], 1)}%）")
            print("  ここは `shader_feature` にすれば**バリアントを増やさずに**消える"
                  "（使うマテリアルが無ければビルドに含まれないため）。")
            print("  **混在しているものは別の話。** 両方のバリアントが出荷され、"
                  "さらに SRP Batcher のバッチも分断される。")
        else:
            print("  **どれが実際に使われていないかはここでは分からない。**"
                  " `--materials <dir>` を渡すと突き合わせる ── "
                  "出荷物で常に OFF のものだけがバリアントを増やさずに消せる。")


def verdict(total: int, failed: int) -> tuple[int, str]:
    """終了コードと、合格にできない理由。

    **0 プログラムを合格にしない。** シェーダーの構造が変わって
    `#pragma vertex` / `#pragma fragment` を読めなくなると、入口が None になり
    **1 つもコンパイルしないまま `0 プログラム中 0 成功 / 0 失敗` と出て exit 0**
    を返していた（T-258）。`check.py` のまとめには `OK  実コンパイル (fxc)` と並ぶので、
    通ったのと見分けが付かない ── `csharp_compile.py` の T-257 と同じ形。

    関数として切り出してあるのは `self_test.py` がここだけを直接撃てるようにするため。
    この道具は `Library/PackageCache` の URP ライブラリを要るので、
    サンドボックスに複製して注入する形の自己診断には載せられない。
    """
    if total == 0:
        return 2, ("**1 プログラムもコンパイルしていない。** これは合格ではない ── "
                   "パスの入口（#pragma vertex / fragment）を 1 つも読めていない")
    if failed:
        return 1, ""
    return 0, ""


# fxc が X4000 で名指しする識別子のうち、**値が欠けていないと確認済み**のもの。
#
# 6 件とも形は同じ ── `UNITY_BRANCH`（= `[branch]`）を付けた `if` の中で
# `return` する関数。fxc は戻り値の一時領域を「変数」として扱い、
# **全経路が返していても**未初期化の可能性ありと言う。
# 報告される「変数名」が**関数名そのもの**なのはそのため。
# `litMask` だけは `out` 引数だが、これも両経路で代入している。
#
# **単一出口に書き換えれば消せるが、有料。** 実測した（T-332）:
#
#   ResolveDiffuseShade を if/else + 1 か所 return へ
#     命令 859 → 863（+4）／一時レジスタ 21 → 20（-1）／警告 2 件減
#
# 6 件全部だと 1% 強を絵の変わらない書き換えに払うことになる。払わない。
#
# **消すのでなく畳む。** 毎回出る警告は要約を読ませなくする側に働く
# （13 種の要約を作ったのはそのため）。ここに無い X4000 ──
# たとえば**ローカル変数が本当に未初期化**の場合 ── は今まで通り出る。
X4000_KNOWN_OK = {
    "ResolveDiffuseShade", "litMask",
    "CalculateCelSpecular", "CalculatePbrSpecular", "CalculateSpecular",
    "CalculateAngelRing",
}

X4000_ARTIFACT_RE = re.compile(r"warning X4000:[^(]*\((\w+)\)")


def x4000_known(msg: str) -> str | None:
    """既知の X4000 なら名指しされた名前を返す。**畳む判断はここだけ。**

    砂場に Cel のツリーが無いので、実コンパイルでは試験できない。
    判定を 1 つの関数に閉じ込めて、単体で叩けるようにしてある。
    """
    hit = X4000_ARTIFACT_RE.search(msg)
    return hit.group(1) if hit and hit.group(1) in X4000_KNOWN_OK else None


# 描画経路 → その経路でシェーダーに立つキーワード。
# `UniversalRendererData.m_RenderingMode` は 0=Forward / 1=Deferred / 2=Forward+。
RENDERING_MODE_KEYWORDS = {
    0: ("Forward", []),
    1: ("Deferred", []),
    2: ("Forward+", ["_CLUSTER_LIGHT_LOOP"]),
}


def shipping_combos(root: Path) -> tuple[list[tuple[str, list[str]]], str]:
    """**このプロジェクトが実際に出荷している**描画経路の組を返す。

    毎回の実コンパイルは長らく「既定」= キーワード無しの 1 組だけだった。
    ところがこのプロジェクトの PC 用レンダラは **Forward+**（`m_RenderingMode: 2`）
    なので、**誰も使わない構成だけを検証していた**。実際、そこでしか出ない
    欠陥を 1 つ見逃していた ── ライトループ内の微分が未定義（T-333）。

    全変種は 141 秒かかって毎回は回せない（既定 1 組なら 2.8 秒）。
    **出荷する経路だけ**なら 2 組で足りる。

    レンダラの場所は決め打ちしない。`Assets` と `Packages` を持つ階層を
    プロジェクトルートとし、`Assets` 以下の `.asset` を中身で判定する
    （`param_check` の品質レベル走査と同じ流儀）。
    """
    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return [], "プロジェクトルートが見つからない"

    found: dict[str, list[str]] = {}
    seen_modes: list[str] = []
    for a in sorted((project / "Assets").rglob("*.asset")):
        try:
            t = a.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        m = re.search(r"^\s*m_RenderingMode: (\d+)", t, re.MULTILINE)
        if not m:
            continue
        label, kws = RENDERING_MODE_KEYWORDS.get(
            int(m.group(1)), (f"不明({m.group(1)})", []))
        found[label] = kws
        seen_modes.append(f"{a.name}={label}")

    if not found:
        return [], "レンダラの .asset が 1 つも見つからない"
    return sorted(found.items()), " / ".join(seen_modes)


def main() -> int:
    ap = argparse.ArgumentParser(description="Unity 無しで HLSL を実コンパイルする")
    ap.add_argument("root", nargs="?", default=".",
                    help="主シェーダー（.shader）のあるディレクトリ")
    ap.add_argument("--shader", default=None,
                    help="既定は root から Hidden でない .shader を探す")
    ap.add_argument("--pass", dest="only", help="このパスだけ")
    ap.add_argument("--branch-cost", action="store_true",
                    help="一様分岐で切っている機能が OFF のとき占めている命令数を測る")
    ap.add_argument("--materials", default=None,
                    help="--branch-cost と併用。マテリアルの .mat を読み、"
                         "各ゲートが出荷物で実際に使われているかを突き合わせる")
    ap.add_argument("--cost", action="store_true",
                    help="命令スロット数と一時レジスタ数を実測して出す")
    ap.add_argument("--variants", action="store_true",
                    help="キーワードの組を回す（ToonPBRVariantCheck.cs の表を読む）")
    ap.add_argument("--shipping", action="store_true",
                    help="出荷している描画経路の組だけ回す（レンダラの m_RenderingMode から導く）")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    # **ファイル名を決め打ちしない。** 移動と改名で場所も名前も変わる（T-250）。
    shader = (root / args.shader) if args.shader else find_main_shader(root)
    if shader is None or not shader.exists():
        print(f"error: 主シェーダーが見つからない（root={root}）")
        return 2

    try:
        comp = Compiler(root)
    except FileNotFoundError as e:
        print(f"error: {e}")
        return 2

    if args.branch_cost:
        mats = Path(args.materials) if args.materials else None
        # **渡されたのに見つからないときは黙らない。** 突き合わせの列が
        # 消えるだけなので、指定し損ねたことに気付けない。
        if mats is not None and not mats.is_dir():
            print(f"error: --materials のディレクトリが無い: {mats}")
            return 2
        branch_cost(root, comp, materials=mats)
        return 0

    passes = parse_passes(shader)
    if args.only:
        passes = [p for p in passes if p["name"] == args.only]
    if not passes:
        print("error: 対象のパスが見つからない")
        return 2

    # **表の場所を決め打ちしない。** パッケージへ移すと `Editor/<名前>/` へ行き、
    # 旧パスでは見つからない。そのとき `--variants` は**黙って既定バリアントだけ**
    # 回して「成功」と出る ── 57 組を通したつもりで 8 組しか通していない状態（T-254）。
    table = next(iter(sorted(root.rglob("ToonPBRVariantCheck.cs"))), None)
    if table is None:
        for parent in root.resolve().parents:
            if (parent / "package.json").exists():
                table = next(iter(sorted(parent.rglob("ToonPBRVariantCheck.cs"))), None)
                break
    if args.variants and table is None:
        print("error: キーワード表（ToonPBRVariantCheck.cs）が見つからない。"
              "**既定バリアントしか回せない**ので --variants は成立しない")
        return 2
    surfaces, features, pass_sets = parse_variant_table(table or root / "_none_")

    # 出荷している描画経路を先に決める。**導けなければ黙って既定に落ちない。**
    ship_combos = None
    if args.shipping:
        ship_combos, why = shipping_combos(root)
        if not ship_combos:
            print(f"error: 出荷構成を導けない（{why}）。"
                  " **既定だけ回して成功と言う形にはしない** ──"
                  " 誰も使わない構成を検証して通ったことになる")
            return 2
        print(f"  出荷構成: {', '.join(l for l, _ in ship_combos)}（{why}）")

    total = failed = 0
    reported: set[str] = set()
    seen_known: set[str] = set()
    ship_used: set[str] = set()
    costs: list[tuple[int, int, str, str, str]] = []
    res_max = (0, 0, 0)          # サンプラ / テクスチャ / 定数バッファの最大
    for p in passes:
        if args.variants:
            # **パス名で決め打ちしないこと。**
            # 以前は `ForwardLit` だけに全組を当てていたが、
            # `HairSeeThrough` は**同じキーワードを宣言して同じコードを読む**
            # のに 3 組しか通していなかった ── 同じ変種の山が片方だけ
            # 未検証のまま残る。**宣言している側**を見て決める。
            declares_surface = any(s in p["body"] for s in surfaces) if surfaces else False
            if declares_surface and features:
                combos = [(f"{lbl}/{s.split('_')[-1]}", kws + [s])
                          for lbl, kws in features for s in surfaces]
            else:
                # **表と宣言の両方を使う。** 表は手で選んだ組（`multi_compile` の
                # 状態など、宣言からは作れないものを含む）なので捨てない。
                # 一方で**表の方が少ないパスがあった** ── Idol の `Outline` は
                # 3 組（宣言からは 4）、`HairShadow` は 2 組（同 3）。
                # 表は手で書くので、キーワードを足したときに更新し忘れる。
                combos = merge_combos(pass_sets.get(p["name"]) or [],
                                      derive_combos(p["body"]))
        elif ship_combos is not None:
            # **そのパスが宣言していないキーワードは足さない。**
            # 足しても素の組と同じものを 2 回コンパイルするだけで、
            # 時間だけ倍になる（毎回回すものが重くなると回されなくなる）。
            seen_kw: set[tuple[str, ...]] = set()
            combos = []
            for label, kws in ship_combos:
                use = [k for k in kws if k in p["body"]]
                ship_used.update(use)
                key = tuple(sorted(use))
                if key in seen_kw:
                    continue
                seen_kw.add(key)
                combos.append((label, use))
        else:
            combos = [("既定", [])]

        bad = 0
        made = 0          # このパスで実際にコンパイルしたプログラム数
        for label, kws in combos:
            for entry, profile, stage in (
                    (p["vert"], "vs_5_0", "SHADER_STAGE_VERTEX"),
                    (p["frag"], "ps_5_0", "SHADER_STAGE_FRAGMENT")):
                if not entry:
                    continue
                total += 1
                made += 1
                ok, msgs = comp.compile(p["body"], f"{p['name']}.hlsl",
                                        entry, profile, list(kws), stage,
                                        measure=args.cost)
                if args.cost and ok:
                    slots, temps = comp.last_cost
                    costs.append((slots, temps, p["name"], label, profile))
                    res_max = tuple(max(x, y) for x, y in zip(res_max, comp.last_res))
                    smp = comp.last_res[0]
                    # **16 本を超えると実機で落ちる。** 14 で警告するのは、
                    # 機能を 1 つ足すと 1〜2 本増えるため（余裕を見る）。
                    if smp >= 14:
                        key = f"sampler:{p['name']}:{smp}"
                        if key not in reported:
                            reported.add(key)
                            sev = "error" if smp >= 16 else "warning"
                            print(f"{sev}: [{p['name']} / {label}] "
                                  f"サンプラを {smp} 本使っている（上限 16）。"
                                  f"**超えると実機で落ちる**（T-072）")
                        if smp >= 16:
                            failed += 1
                if ok and not msgs:
                    continue
                # （X4000 の既知分は下で畳む）
                if not ok:
                    bad += 1
                    failed += 1
                for m in msgs:
                    # 同じ指摘が組の数だけ並ぶと読めない。中身で1回に畳む
                    key = m.split(": ", 1)[-1]
                    if key in reported:
                        continue
                    reported.add(key)
                    known = x4000_known(m)
                    if known:
                        seen_known.add(known)
                        continue
                    tag = "error" if ": error " in m else "warning"
                    print(f"{tag}: [{p['name']} / {label} / {profile}] "
                          f"{m.split(chr(92))[-1]}")
        # **入口が取れなかったパスを「OK」と言わない。**
        # `#pragma vertex` / `#pragma fragment` が読めないと `vert`/`frag` が
        # None になり、両方 continue して**1 つもコンパイルしないまま**
        # `OK ForwardLit 1 組` と出る（T-258）。組の数はパスの構造から出るので、
        # 何も通していなくても減らない。
        if made == 0:
            failed += 1
            print(f"  失敗 {p['name']:<16} **入口が取れず 0 プログラム** "
                  f"（#pragma vertex / fragment を読めていない）")
            continue
        mark = "OK  " if bad == 0 else "失敗"
        print(f"  {mark} {p['name']:<16} {len(combos):>3} 組 / {made} プログラム")

    # **出荷構成が既定と見分けが付かないなら言う。**
    # URP は `_FORWARD_PLUS` → `_CLUSTER_LIGHT_LOOP` と改名した実績があり、
    # 追随し損ねると導出は成功したまま**キーワードが 1 つも立たない**
    # ── 出荷構成を回したつもりで既定を 2 回回すだけになる。
    if ship_combos is not None and not ship_used:
        total += 1
        expect = sorted({k for _, kws in ship_combos for k in kws})
        print(f"error: 出荷構成のキーワードがどのパスにも無い: "
              f"{', '.join(expect) or '(経路が全て素)'}")
        print("    導出は通っているのに**既定と同じものを回している**。"
              " URP 側の改名に追随できていない可能性がある。")

    if seen_known:
        print(f"  既知 X4000（[branch] + 途中 return の癖）{len(seen_known)} 件は畳んだ"
              f": {', '.join(sorted(seen_known))}")
    # **表に載っているのに出なくなったものを黙って残さない。**
    # 消した関数の名前が残っていると、その名前で**本物の X4000 が出ても畳まれる**。
    # 検査コード表と同じ向きの確認（T-168 で 2 回踏んだ形）。
    stale = X4000_KNOWN_OK - seen_known
    if seen_known and stale:
        total += 1
        print(f"error: X4000 の許容表に**今は出ていない名前**が残っている: "
              f"{', '.join(sorted(stale))}")
        print("    その名前で本物の X4000 が出ても畳まれる。消すこと。")

    if costs:
        # **推定ではなく実測。** `param_check --cost` はソースのゲートを評価した
        # テクスチャフェッチ数で、命令数は分からない。
        # 一時レジスタは占有率（同時に走らせられるスレッド数）に直結する。
        print("\n=== 実測コスト（fxc / D3D11）===")
        print(f"{'パス':<16}{'組':<22}{'段':<12}{'命令':>8}{'一時レジスタ':>12}")
        for slots, temps, name, label, profile in sorted(costs, reverse=True)[:12]:
            stage = "頂点" if profile.startswith("vs") else "フラグメント"
            print(f"{name:<16}{label[:20]:<22}{stage:<12}{slots:>8,}{temps:>12}")
        # **最後の組ではなく全組の最大を出す。** 最後に測るのがどの段かで
        # 変わる数字には意味が無い（頂点シェーダーはサンプラを使わない）。
        s, tx, cb = res_max
        print(f"\n  資源の最大（全組）: サンプラ {s} 本 / 上限 16"
              f"（余裕 {16 - s} 本）、テクスチャ {tx} 本、定数バッファ {cb} 本")
        worst = max(costs)
        print(f"  最も重い: {worst[2]} / {worst[3]} で {worst[0]:,} 命令 "
              f"/ 一時レジスタ {worst[1]}")
        print("  **一時レジスタが多いほど同時に走るスレッドが減る。**"
              " 命令数より効くことがある。")

    print(f"\n実コンパイル(fxc): {total} プログラム中 {total - failed} 成功 / {failed} 失敗")
    code, why = verdict(total, failed)
    if why:
        print(f"error: {why}")
    if code:
        return code
    print("  **fxc が通ることは Unity が通ることの十分条件ではない**"
          "（D3D11 のみ・プラットフォーム定義は写し）。")
    print("  ただし**落ちたものは本物**。型・宣言・リソース上限はここで出る。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

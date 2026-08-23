#!/usr/bin/env python3
"""
param_check.py — シェーダーの式が「実際の値」で成立しているかを検算する

`shader_lint.py` はコードの構造（宣言漏れ・include 順）を見る。
こちらは**値**を見る。両方通って初めて、書いた式が意図どおり動く。

なぜ要るか:
  このプロジェクトで実際に出た退行は、どれも**構造ではなく値の関係**が壊れていた。
  コンパイルは通り、静的検査も通り、実機で初めて分かった。

    T-113  コンタクトシャドウの立ち上がり幅がレイの歩幅を下回り、
           滑らかにしたはずのランプが二値に戻ってちらついた
    T-119  同じ関係を見る診断を書いたが、しきい値が 2 倍厳しく、
           **正しい設定に警告を出していた**
    T-108  厚み判定の窓がレイ長より広く、判定が一度も発火していなかった

  いずれも「A が B より大きいか」を一度計算すれば分かる。
  毎回その場で電卓を叩くのではなく、ここに固定する。

使い方:
  cd Assets/ToonPBR
  python param_check.py .
  python param_check.py . --materials "../requiem/vjT4u4BcId/Materials 3"

マテリアルを渡さなければシェーダーの既定値だけを見る。
渡せば、既定値を上書きしている実際のマテリアルも1つずつ見る。
"""

from __future__ import annotations

import argparse
import glob
import os
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

DEFINE_RE = re.compile(r"^#define[ 	]+(TOON_\w+)[ 	]+(\d+)[ 	]*$", re.MULTILINE)


def read_defines(hlsl_path: Path) -> dict[str, int]:
    """タップ数を**ソースから読む。**

    最初はここに `TOON_CONTACT_STEPS = 8` と書き写し、
    「シェーダーと一致させること」とコメントしていた。
    **それは必ずずれる。** 実際このプロジェクトでは、同じやり方で書き写した
    `ToonPBRVariantCheck.PassSets` が長期間ずれたまま放置され、
    Outline と ShadowCaster が一度も検証されていなかった（T-107）。
    人間の注意力に頼る同期は、検査そのものを黙って無効にする。

    見つからなければ例外を投げる。**黙って既定値で続けない** ──
    間違った歩幅で計算した結果は、警告が出ないぶん誤った安心を与える。
    """
    if not hlsl_path.exists():
        raise FileNotFoundError(f"{hlsl_path} が無い。root の指定を確認すること。")

    # **1 ファイルに決め打ちしないこと。** 元は `ToonPBRCommon.hlsl` だけを見ていたが、
    # 影の定数は分割で `Shading/ToonPBRShadows.hlsl` へ移った（T-212）。
    # どこに置かれても拾えるよう、ディレクトリ全体の .hlsl / .shader を舐める。
    found: dict[str, int] = {}
    for f in sorted(hlsl_path.parent.rglob("*.hlsl")) + sorted(hlsl_path.parent.rglob("*.shader")):
        found.update({m.group(1): int(m.group(2))
                      for m in DEFINE_RE.finditer(f.read_text(encoding="utf-8", errors="replace"))})

    required = ["TOON_SHADOW_TAPS", "TOON_BLOCKER_TAPS",
                ]
    missing = [k for k in required if k not in found]
    if missing:
        raise ValueError(
            f"{hlsl_path.parent} 以下に {', '.join(missing)} が見つからない。"
            f" 名前を変えたなら param_check.py の required も直すこと。")

    return found


def read_core_includes(root: Path, tree_text: str) -> str:
    """ツリーが `Packages/com.origuma.*` から include している HLSL を読んで返す。

    T-340 で GGX 純関数の本体（Smith 可視項の `max(1-a², 0)` の守りを含む）が
    EasyShaderCore 側へ移った。ツリーだけを見ると「守りが外れている」と誤報し、
    逆に **Core 側で守りが消えたときは黙る**。include を辿って本体まで見る。

    Packages ディレクトリは root の親を遡って探す。自己診断のサンドボックスも
    root の隣に `Packages/` を複製するので、同じ規則で解決できる。
    見つからなければ空文字（呼び出し側はツリーだけで検査を続ける）。
    """
    rels = re.findall(r'#include\s+"(Packages/com\.origuma\.[^"]+)"', tree_text)
    if not rels:
        return ""
    base = None
    # root は相対パスで渡ってくることがある（check.py 経由は絶対、手動は "." など）。
    # 相対のまま parents を辿ると "." で止まって Packages に届かない。
    root = root.resolve()
    for parent in [root, *root.parents]:
        if (parent / "Packages").is_dir():
            base = parent
            break
    if base is None:
        return ""
    parts = []
    for rel in dict.fromkeys(rels):     # 重複除去・順序維持
        f = base / rel
        if f.exists():
            parts.append(f.read_text(encoding="utf-8", errors="replace"))
    return (chr(10) * 2).join(parts)


def read_all_hlsl(root: Path) -> str:
    """ディレクトリ以下の HLSL / シェーダーを 1 本につなげて返す。

    **1 ファイルに決め打ちしないこと。** 元は `ToonPBRCommon.hlsl` を直接開いていたが、
    分割で中身が `Shading/` と `Passes/` へ散った（T-211 / T-212）。
    決め打ちのままだと**探しているものが「無い」ことになり、検査が丸ごと空振りする。**
    """
    parts = []
    for f in sorted(root.rglob("*.hlsl")) + sorted(root.rglob("*.shader")):
        parts.append(f.read_text(encoding="utf-8", errors="replace"))
    return (chr(10) * 2).join(parts)


@dataclass
class Finding:
    level: str          # "error" | "warning"
    where: str          # マテリアル名、または "既定値"
    title: str
    detail: str


# ---------------------------------------------------------------------------
# パーサ
# ---------------------------------------------------------------------------

PROP_DEFAULT_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*\"[^\"]*\"[ \t]*,"
    r"[ \t]*(?:Range\([^)]*\)|Float)[ \t]*\)[ \t]*=[ \t]*([-\d.]+)",
    re.MULTILINE,
)

MAT_FLOAT_RE = re.compile(r"^[ \t]*- (_\w+): ([-\d.eE+]+)[ \t]*$", re.MULTILINE)


def read_defaults(shader_path: Path) -> dict[str, float]:
    text = shader_path.read_text(encoding="utf-8", errors="replace")
    return {m.group(1): float(m.group(2)) for m in PROP_DEFAULT_RE.finditer(text)}


def read_material(path: Path) -> dict[str, float]:
    text = path.read_text(encoding="utf-8", errors="replace")
    return {m.group(1): float(m.group(2)) for m in MAT_FLOAT_RE.finditer(text)}


def keywords_of(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8", errors="replace")
    m = re.search(r"m_ValidKeywords:\n((?:[ \t]*- \w+\n)*)", text)
    return set(re.findall(r"- (\w+)", m.group(1))) if m else set()


# ---------------------------------------------------------------------------
# 検算
# ---------------------------------------------------------------------------

def check_guards(root: Path) -> list[Finding]:
    """**式そのものに入れた守りが外れていないか**をソースで見る。

    値の検算だけでは、守りが効いているおかげで成立している状態と、
    守りが不要な状態を区別できない。守りを消す変更を検出するにはソースを見るしかない。
    """
    out: list[Finding] = []
    text = read_all_hlsl(root)
    if not text:
        return out
    # 守りの本体が Core 側にある場合（T-340: GGX の純関数）も検査対象に足す。
    text = text + (chr(10) * 2) + read_core_includes(root, text)

    guards = [
        # (探す式, 名前, 外れたときに何が起きるか)
        # 厳密形の Smith 可視項（T-246）。alpha が 1 を超えると (1-a²) が負になり、
        # **負の底の sqrt は NaN**。NaN は乗算も lerp も素通りして最終色まで届く。
        (r"max\s*\(\s*1\.0\s*-\s*a2\s*,\s*0\.0\s*\)",
         "Smith 可視項の sqrt の中身",
         "alpha > 1 で sqrt の中が負になり NaN。鏡面が丸ごと壊れる（T-165 と同型）"),
        # **UE4 の EnvBRDFApprox は知覚粗さで当てられている。** alpha を渡すと
        # 常により滑らかな面として評価され、環境鏡面が明るく出る。
        # T-182 で数値検証した結果（分割和の真値と突き合わせ）:
        #
        #   知覚粗さ … 最大 A=0.149 / B=0.127 / RMS=0.054
        #   alpha    … 最大 A=0.135 / B=0.314 / RMS=0.086
        #
        # **B 項（f0 に依らない下駄）が 2.5 倍悪化する。**
        # 誘電体の縁が持ち上がる形で出るので、金属では気付けない。
        # 両方コンパイルが通り、両方それらしく見えるので**目視では区別できない。**
        # `s.perceptualRoughness` のようにドットで参照されるので `[\w.]` にする
        # ── 最初 `\w` だけで書いて**正しいソースに誤検出**した。
        (r"ToonEnvBRDFMultiScatter\s*\(\s*[^,]+,\s*[\w.]*[Pp]erceptual\w*\s*,",
         "環境 BRDF に知覚粗さを渡している",
         "alpha を渡すと B 項の誤差が 2.5 倍になり、誘電体の縁が明るく持ち上がる（T-182）"),
    ]

    for pattern, name, harm in guards:
        if not re.search(pattern, text):
            out.append(Finding(
                "error", "ToonPBRCommon.hlsl", f"守りが外れている: {name}",
                f"{harm}。式を書き換えるなら、この検査も一緒に更新すること。"))

    return out


# ToonShadeLight の引数と、そこから直接作られる光源依存の量。
# ここを起点に「その変数から作られたものも光源依存」と伝播させる。
LIGHT_SEED = {
    "L", "H", "NdotL", "NdotH", "VdotH", "LdotH", "NdotLs", "light", "Ld",
    "lightColor", "radiance", "atten", "shadowAtten", "castAtten",
    "lit", "rawT", "softness", "castShadow", "Hcloth", "NdotHc", "attenAA", "edgeAA",
}

# 重いとみなすもの。**自前のヘルパ呼び出し（Toon*）も含める。**
# 最初は組み込み関数だけを見ていて、`ToonApplyRoughnessKernel` を
# ライトループへ戻す注入テストが**素通りした**。
# 中で sqrt を呼ぶ関数を「軽い」と見なしていては検査の意味が無い。
HEAVY_OPS = re.compile(
    r"\b(sqrt|rsqrt|exp2|exp|log2|pow|sincos|sin|cos|atan2|normalize"
    r"|SAMPLE_\w+|rcp|smoothstep|Toon\w+)\s*\(")

DECL_RE = re.compile(r"^(?:float|half)[234]?\s+(\w+)\s*=\s*(.+);$")


def check_light_loop(root: Path, known_ok: set[str] | None = None) -> list[Finding]:
    """`ToonShadeLight` に**光源に依存しない重い計算**が残っていないか見る。

    この関数はライトの数だけ呼ばれる。光源に依存しない値を中に置くと
    灯数ぶん無駄に再計算される。シェーダーには「光源に依存しない前計算」の
    ブロックが用意してあるのに、**新機能を足すときに2回そこを忘れた**
    （T-122 のシーン、T-123 の髪。どちらも T-138 で移した）。

    **光源依存は伝播させること。** 単純に識別子を見るだけだと
    `ltLight = L + ...` から作られた `back = pow(dot(V, -ltLight), ...)` を
    「光源非依存」と誤判定する（実際に出た）。**誤検出の出る検査は無いより悪い。**

    既知で許容しているものは `known_ok` で除外する。
    """
    out: list[Finding] = []
    text = read_all_hlsl(root)
    if not text:
        return out
    if "float3 ToonShadeLight(" not in text:
        return out

    i = text.index("float3 ToonShadeLight(")
    depth = 0
    j = text.index("{", i)
    for k in range(j, len(text)):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                j = k
                break

    lines = [l.strip() for l in text[i:j].splitlines()]
    base_line = text[:i].count("\n") + 1

    dep = set(LIGHT_SEED)
    for _ in range(4):                      # 前方参照は無いが収束させる
        for s in lines:
            m = DECL_RE.match(s)
            if m and any(re.search(rf"\b{re.escape(x)}\b", m.group(2)) for x in dep):
                dep.add(m.group(1))

    ok = known_ok or set()

    for n, s in enumerate(lines, base_line):
        m = DECL_RE.match(s)
        if not m or m.group(1) in dep or m.group(1) in ok:
            continue
        if HEAVY_OPS.search(m.group(2)):
            out.append(Finding(
                "warning", f"ToonPBRCommon.hlsl:{n}",
                f"光源ループ内の光源非依存な重い計算: {m.group(1)}",
                f"`{s[:70]}` はライトに依存しないのに ToonShadeLight の中にある。"
                f" 灯数ぶん再計算される。フラグメントの「光源に依存しない前計算」へ移すこと。"))

    return out


RANGE_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*\"[^\"]*\"[ \t]*,"
    r"[ \t]*Range\([ \t]*([-\d.]+)[ \t]*,[ \t]*([-\d.]+)[ \t]*\)[ \t]*\)",
    re.MULTILINE,
)


def read_ranges(shader_path: Path) -> dict[str, tuple[float, float]]:
    text = shader_path.read_text(encoding="utf-8", errors="replace")
    return {m.group(1): (float(m.group(2)), float(m.group(3)))
            for m in RANGE_RE.finditer(text)}


def check_ranges(values: dict[str, float], ranges: dict[str, tuple[float, float]],
                 where: str) -> list[Finding]:
    """マテリアルの値が Range を外れていないか。

    **`Range` 属性はインスペクタのスライダを縛るだけで、実行時の値は縛らない。**
    他シェーダーから移植した .mat には範囲外の値がそのまま残り、
    lerp が外挿になって色や遮蔽が負に振れる（T-076 / T-098 で実害）。

    Unity の診断（ToonPBRSetupCheck）も同じことを見ているが、
    **あちらは Editor を開かないと回せない。** 移植直後やスクリプトで
    一括編集した直後に、Unity を起動せず確かめられる価値がある。
    """
    out: list[Finding] = []

    for name, val in values.items():
        rng = ranges.get(name)
        if rng is None:
            continue
        lo, hi = rng
        if val < lo - 1e-6 or val > hi + 1e-6:
            out.append(Finding(
                "error", where, f"Range を外れた値: {name}",
                f"{name} = {val} だが Range は [{lo}, {hi}]。"
                f" スライダは縛るが実行時は縛らないので、この値のまま計算される。"
                f" lerp の係数なら外挿になり、色が負に沈むか遮蔽が 1 を超える。"))

    return out




def fetch_count(v: dict[str, float], kw: set[str],
                defines: dict[str, int]) -> list[tuple[str, int]]:
    """1画素あたりのテクスチャフェッチ数を、ゲートを評価して数える。

    **ゲートを見ないと意味が無い。** ソース上の呼び出し箇所を数えるだけでは、
    マテリアルごとに切られている経路まで数えてしまう。
    実際に引かれる回数は設定で 2 倍近く変わる。
    """
    st = int(v.get("_SurfaceType", 0))
    hq = "_HQ_SHADOW_ON" in kw
    on = lambda k, t=0.5: v.get(k, 0.0) > t

    rows = [
        ("_BaseMap", 1),
        ("_MaskMap", 1),
        ("_NPRMap", 1 if on("_NPRMapOn") else 0),
        ("_CavityMap", 1 if on("_CavityStrength", 0.0) else 0),
        ("_EmissionMap", 1 if on("_EmissionOn") else 0),
        ("_BumpMap", 1 if on("_NormalMapOn") else 0),
        ("_ShadeNormalMap", 1 if on("_ShadeNormalStrength", 0.0) else 0),
        # 散乱が両方 0 なら消費側が無いので引かない（T-117）
        ("_SSSMap", 1 if (on("_SSSMapStrength", 0.0)
                          and (v.get("_SubsurfaceStrength", 0.0)
                               + v.get("_TransmissionStrength", 0.0)) > 0.0) else 0),
        ("_BentNormalMap", 1 if on("_BentNormalOn") else 0),
        ("_CurvatureMap", 1 if on("_CurvatureSoftness", 0.0) else 0),
        ("_RampMap", 1 if on("_UseRampMap") else 0),
        ("_FaceSDFMap", 1 if st == 2 else 0),
        ("_HairShiftMap", 1 if st == 3 else 0),
        ("_HairFlowMap", 1 if (st == 3 and on("_HairFlowStrength", 0.0)) else 0),
        ("環境反射（プローブ）", 2),
        ("影フィルタ", defines["TOON_SHADOW_TAPS"] if hq else 1),
        ("ブロッカー探索",
         defines["TOON_BLOCKER_TAPS"] if (hq and on("_ShadowContactHardening")) else 0),
    ]
    return rows


def report_cost(root: Path, materials_dir: Path | None) -> None:
    """フェッチ数の内訳を出す。合否には影響しない情報表示。"""
    defaults = read_defaults((find_main_shader(root) or root / '_missing_.shader'))
    defines = read_defines(find_file(root, "ToonPBRCommon.hlsl"))

    targets: list[tuple[str, dict[str, float], set[str]]] = []
    if materials_dir is None:
        targets.append(("既定値", defaults, {"_HQ_SHADOW_ON"}))
    else:
        for path in find_materials(materials_dir):
            values = dict(defaults)
            values.update(read_material(path))
            targets.append((path.stem, values, keywords_of(path)))

    print("=== 1画素あたりのテクスチャフェッチ ===\n")

    worst = None
    for name, values, kw in targets:
        rows = fetch_count(values, kw, defines)
        total = sum(n for _, n in rows)
        shadow = sum(n for k, n in rows
                     if k in ("影フィルタ", "ブロッカー探索"))
        if worst is None or total > worst[1]:
            worst = (name, total, rows, shadow)

    name, total, rows, shadow = worst
    for k, n in rows:
        if n:
            print(f"    {k:22} {n:>3}")
    print(f"    {'-' * 26}")
    print(f"    {'合計':22} {total:>3}  （最も重い {name}）")
    print(f"    {'うち影関連':22} {shadow:>3}  = {shadow / total * 100:.0f}%\n")

    if shadow / total > 0.6:
        print("    **フェッチの大半が影。** ちらつきの調査で切り分ける対象と同じなので、")
        print("    接地硬化（-8）を切れば、揺れとコストが同時に下がる。\n")


TOGGLE_RE = re.compile(r"\[Toggle\((\w+)\)\][ \t]*(_\w+)")


def read_toggles(shader_path: Path) -> dict[str, str]:
    """`[Toggle(KEYWORD)] _Prop` の対応を取る。"""
    text = shader_path.read_text(encoding="utf-8", errors="replace")
    return {m.group(2): m.group(1) for m in TOGGLE_RE.finditer(text)}


def check_toggle_keywords(values: dict[str, float], keywords: set[str],
                          toggles: dict[str, str], where: str) -> list[Finding]:
    """トグルのプロパティ値とキーワードの状態が一致しているか。

    **片方だけずれると「トグルは ON なのに効かない」形で出る。**
    インスペクタは ON に見えるので、原因に辿り着くのが非常に難しい。

    スクリプトで .mat を一括編集すると起きる ── プロパティだけ書き換えて
    `m_ValidKeywords` を更新し忘れるのが典型。このセッションだけで
    46 マテリアルを 15 回以上一括編集しており、そのたびに危険があった。
    """
    out: list[Finding] = []

    for prop, keyword in toggles.items():
        if prop not in values:
            continue

        on = values[prop] > 0.5
        has = keyword in keywords
        if on == has:
            continue

        out.append(Finding(
            "error", where, f"トグルとキーワードが食い違う: {prop}",
            f"{prop} = {values[prop]}（{'ON' if on else 'OFF'}）だが "
            f"{keyword} は {'ON' if has else 'OFF'}。"
            f" インスペクタの見た目と実際の分岐が一致しないので、"
            f" 「設定したのに効かない」という形で出る。"))

    return out


NUMERIC_RE = re.compile(r"^[ \t]*- (_\w+): ([-\d.eE+]+)[ \t]*$", re.MULTILINE)
COLOR_RE = re.compile(r"^[ \t]*- (_\w+): \{([^}]*)\}[ \t]*$", re.MULTILINE)


def check_material_integrity(path: Path) -> list[Finding]:
    """.mat が壊れていないか。

    **スクリプトで一括編集すると壊れる。** このセッションだけで 46 マテリアルを
    18 回以上正規表現で書き換えており、そのたびに:

      - 数値のはずが空や文字列になる（置換の取りこぼし）
      - 色の成分が 4 つでなくなる（r/g/b だけ書いて a を落とす）
      - 構造キーごと消える

    どれも Unity は黙って既定値に落とすか、最悪インポートに失敗する。
    **編集した直後に構造として見るのが一番安い。**
    """
    out: list[Finding] = []
    text = path.read_text(encoding="utf-8", errors="replace")
    name = path.stem

    for key in ("m_Shader:", "m_SavedProperties:", "m_Floats:", "m_Colors:"):
        if key not in text:
            out.append(Finding("error", name, f"構造キーが無い: {key}",
                               "一括編集で壊した可能性がある。Unity がインポートに失敗する。"))

    for m in NUMERIC_RE.finditer(text):
        try:
            v = float(m.group(2))
        except ValueError:
            out.append(Finding("error", name, f"数値でない値: {m.group(1)}",
                               f"'{m.group(2)}' は float として読めない。"))
            continue
        if v != v or abs(v) == float("inf"):
            out.append(Finding("error", name, f"NaN / Inf: {m.group(1)}",
                               "計算に混ざると画面全体が黒や白に飛ぶ。"))

    for m in COLOR_RE.finditer(text):
        if "r:" not in m.group(2):
            continue
        if len(re.findall(r"[rgba]:", m.group(2))) != 4:
            out.append(Finding("error", name, f"色の成分が 4 つでない: {m.group(1)}",
                               "r/g/b/a のどれかが落ちている。"))

    # **値が空になった行を拾う。** 置換で数値を消してしまう形の壊し方は
    # 上の NUMERIC_RE では見つからない ── 「数値がある行」しか見ていないので、
    # 値ごと消えた行は**そもそもマッチしない**（注入テストで素通りした）。
    #
    # ただし `- _BaseMap:` のようにテクスチャ項目は値が無いのが正常。
    # **`m_Floats:` ブロックの中だけ**を見る必要がある。
    floats = re.search(r"^[ \t]*m_Floats:[ \t]*$", text, re.MULTILINE)
    if floats:
        rest = text[floats.end():]
        # 次の同じ深さのキー（m_Colors など）までがブロック。
        end = re.search(r"^[ \t]{0,4}m_\w+:", rest, re.MULTILINE)
        block = rest[:end.start()] if end else rest

        for m in re.finditer(r"^[ \t]*- (_\w+):[ \t]*$", block, re.MULTILINE):
            out.append(Finding("error", name, f"値が空になっている: {m.group(1)}",
                               "m_Floats の項目に値が無い。一括編集で数値ごと消した可能性がある。"))

    return out


# Unity が略記の pragma を展開したときのバリアント数。
# `multi_compile_fog` は `__ / FOG_LINEAR / FOG_EXP / FOG_EXP2` の 4 通り。
PRAGMA_SHORTHAND = {
    "multi_compile_fog": 4,
    "multi_compile_fragment_fog": 4,
    "multi_compile_instancing": 2,
}


def count_variants(shader: Path) -> dict[str, dict]:
    """`.shader` のパスごとにバリアント数を数える。

    **数え方は Unity の規則に合わせてある**（記録の 40 と一致することで裏を取った）:

      `#pragma shader_feature A`        → 2 通り（無指定 + A）
      `#pragma shader_feature _ A`      → 2 通り
      `#pragma shader_feature A B C`    → 3 通り（**無指定は作られない**）

    複数列挙のときに「無指定」を足すか足さないかで、サーフェスタイプの
    5 通りが 6 通りになる。実際そう数えて 48 と出し、記録の 40 と食い違った。

    `#include_with_pragmas` が引き込むぶん（APV など）は**数えていない。**
    数えられないものを 1 と見なすと、合計が本当より小さく見える ──
    黙って小さく出るくらいなら、数えていないと言うほうがよい。
    """
    txt = re.sub(r"//[^\n]*", "", shader.read_text(encoding="utf-8", errors="replace"))

    result: dict[str, dict] = {}
    for m in re.finditer(r'Name\s+"(\w+)"', txt):
        end = txt.find("ENDHLSL", m.end())
        if end < 0:
            continue
        body = txt[m.end():end]

        system = feature = 1
        for pm in re.finditer(r"^[ \t]*#pragma[ \t]+(\S+)([^\n]*)$", body, re.MULTILINE):
            directive, rest = pm.group(1), pm.group(2).strip()
            if directive in PRAGMA_SHORTHAND:
                system *= PRAGMA_SHORTHAND[directive]
                continue
            opts = rest.split()
            if not opts:
                continue
            n = len(opts) if len(opts) > 1 else 2
            if directive.startswith("multi_compile"):
                system *= n
            elif directive.startswith("shader_feature"):
                feature *= n

        result[m.group(1)] = {
            "system": system,
            "feature": feature,
            "external": len(re.findall(r"#include_with_pragmas", body)),
        }
    return result


def check_variants(root: Path) -> list[Finding]:
    """**サマリに書いたバリアント数を実装から数え直す。**

    書き写した数字は古くなる ── このプロジェクトの最大の持病。
    バリアント数は特に古くなりやすい。**キーワードを 1 つ足すだけで倍**になり、
    足した本人はそのとき数え直さないため。実際、記録の
    「feature 40 × system 16,384」は `_HAIRSEETHROUGH_ON` を足した後も
    そのままだった。

    **budget を決めて警告にはしない。** 妥当な上限が分からないまま閾値を置くと、
    正常な状態に警告が出続けて他の指摘を埋める（T-167 で踏んだ形）。
    ここは「書いてある数字と合っているか」だけを見る。
    """
    out: list[Finding] = []
    shader = (find_main_shader(root) or root / '_missing_.shader')
    log = find_file(root, "BACKLOG.md")
    if not shader.exists() or not log.exists():
        return out

    counts = count_variants(shader)
    if "ForwardLit" not in counts:
        out.append(Finding(
            "error", shader.name, "ForwardLit パスが見つからない",
            "バリアント数を数えられない。パス名を変えたなら検査側も直すこと。"))
        return out

    head = re.split(r"^### T-\d+", log.read_text(encoding="utf-8", errors="replace"),
                    maxsplit=1, flags=re.MULTILINE)[0]

    m = re.search(r"ForwardLit は feature ([\d,]+) × system ([\d,]+)", head)
    if not m:
        return out                       # 書いていないなら見ない

    want_f = int(m.group(1).replace(",", ""))
    want_s = int(m.group(2).replace(",", ""))
    got = counts["ForwardLit"]
    if (want_f, want_s) != (got["feature"], got["system"]):
        out.append(Finding(
            "error", log.name, "サマリのバリアント数が実装と違う",
            f"「feature {want_f:,} × system {want_s:,}」と書いてあるが、"
            f"実装から数えると feature {got['feature']:,} × system {got['system']:,}。"
            f" **キーワードを足したら数え直すこと。** `--variants` で全パスを出せる。"))
    return out


def report_variants(root: Path) -> None:
    """パスごとのバリアント数。合否には影響しない。"""
    shader = (find_main_shader(root) or root / '_missing_.shader')
    if not shader.exists():
        return
    counts = count_variants(shader)

    print("=== バリアント数（パスごと）===")
    print(f"{'パス':<18}{'system':>10}{'feature':>10}{'積':>14}")
    total = 0
    for name, c in counts.items():
        prod = c["system"] * c["feature"]
        total += prod
        ext = "  +外部" if c["external"] else ""
        print(f"{name:<18}{c['system']:>10,}{c['feature']:>10,}{prod:>14,}{ext}")
    print(f"{'合計':<18}{'':>10}{'':>10}{total:>14,}")
    print("  system = multi_compile（パイプラインが立てる）")
    print("  feature = shader_feature_local（マテリアルが立てる）")
    print("  **+外部** は #include_with_pragmas が引き込むぶん。数えていない")
    print()


def find_file(root: Path, name: str) -> Path:
    """名前でファイルを探す。**平坦でも分かれていても見つける。**

    このツリーは今 `Assets/ToonPBR/` に平坦に置いてあるが、パッケージへ移すと
    4 箇所に分かれる（`Runtime/Shaders/Idol/` / `Runtime/Scripts/` /
    `Editor/` / `Documentation~/`）。**`root / "Editor" / "X.cs"` のような
    書き方は移した瞬間に空振りする**（T-250）。

    まず root の下を探し、無ければパッケージのルート（`package.json` のある所）
    まで上がってその下を探す。同名が複数あるときは**浅い方**を採る。

    見つからないときは `root / name` を返す ── 呼び出し側は `exists()` で
    判定しているので、**存在しないパスを返せば従来どおりスキップされる。**
    `None` を返すと `exists()` の呼び出し側が全部落ちる。
    """
    hits = sorted(root.rglob(name))
    if hits:
        return min(hits, key=lambda p: len(p.parts))
    for parent in root.resolve().parents:
        if (parent / "package.json").exists():
            hits = sorted(parent.rglob(name))
            if hits:
                return min(hits, key=lambda p: len(p.parts))
            break
    return root / name


def find_main_shader(root: Path) -> Path | None:
    """このツリーの主シェーダー（`Hidden/` でない `.shader`）。

    **ファイル名を決め打ちしないこと。** 以前は `(find_main_shader(root) or root / '_missing_.shader')` と
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


def check_docs(root: Path) -> list[Finding]:
    """ドキュメントが実装とずれていないか。

    **このプロジェクトで 4 回続けて見つかった系統。**

      T-107  CLAUDE.md の検査コマンドが存在しないパスを指していた（実行すると落ちる）
      T-158  設定依存の数値を書き写しており、設定が変わって古くなっていた
      T-160  BACKLOG の現状サマリが 50 項目ぶん古く、動かないツールを案内していた
      T-161  REQUIREMENTS の「未実装」に実装済みの項目が残っていた

    **古い情報は無い情報より悪い。** 確認せずに使ってしまう。
    CLAUDE.md は「作業を始める前に REQUIREMENTS と BACKLOG を読むこと」としているので、
    そこがずれていると次に読む人が誤った前提から始まる。

    見るのは「機械的に判定できるもの」だけ。文章の鮮度は判定できない。
    """
    out: list[Finding] = []

    # **再帰で集めること。** パスの本体を `Passes/` へ切り出したように入れ子が増えると、
    # 深い所のファイルが丸ごと対象から外れ、そこへ移した識別子が
    # 「実在しない」と誤検出される（T-210 で FR-48 / FR-53 の 2 件が出た）。
    # **C# はツリーの外にあることがある。** パッケージへ移すと
    # `Editor/<名前>/` と `Runtime/Scripts/<名前>/` へ行き、シェーダーの下から消える。
    # 見落とすと REQUIREMENTS の実装欄が丸ごと「実在しない」になる（T-252）。
    #
    # **同名の部屋だけを見ること** ── 単にパッケージルートを足すと、
    # 隣のシェーダーのスクリプトまで拾って別物を検査する。
    scan = [root]
    _name = root.resolve().name
    for _parent in root.resolve().parents:
        if (_parent / "package.json").exists():
            scan += [d for d in _parent.rglob(_name)
                     if d.is_dir() and d != root.resolve()]
            break

    src = ""
    seen: set[Path] = set()
    for base in scan:
        for f in list(base.rglob("*.shader")) + list(base.rglob("*.hlsl")) \
                + list(base.rglob("*.cs")):
            if f in seen:
                continue
            seen.add(f)
            src += f.read_text(encoding="utf-8", errors="replace")

    names = {p.name for base in scan for p in base.rglob("*")}

    # **文書はツリーの外にある。** `root` は `Runtime/Shaders/Idol` なので、
    # `root.glob("*.md")` も `root / "BACKLOG.md"` も**必ず空**になる。
    # パッケージへ移して以降、下の (1) と (2b) は**一度も走っていなかった**
    # ── 指摘 0 件は「問題が無い」ではなく「見ていない」だった（T-330）。
    # 同じ関数の (2)(3)(4) は `find_file` を使っており、そちらは動いていた。
    #
    # **照合する側も広げないと誤検出になる。** `names` は同名の部屋だけなので、
    # 道具は `Documentation~/` にあって入っていない。(1) をそのまま生かすと
    # 文書中の `python check.py` が全部「存在しないスクリプト」になる
    # （実測 28 件すべて誤検出）。判定はパッケージ全体で行う。
    pkg_root = next((p for p in root.resolve().parents
                     if (p / "package.json").exists()), None)
    doc_scan = pkg_root or root
    pkg_names = {p.name for p in doc_scan.rglob("*")}
    _pkg_src: list[str] = []          # (2b) で外れたときだけ読む

    def pkg_src() -> str:
        if not _pkg_src:
            _pkg_src.append("".join(
                f.read_text(encoding="utf-8", errors="replace")
                for pat in ("*.shader", "*.hlsl", "*.cs")
                for f in doc_scan.rglob(pat)))
        return _pkg_src[0]

    # (1) **コマンドがそのまま動くか。**
    #     T-107 では CLAUDE.md が `tools/shader_lint.py Assets/ToonNPR/Shaders` という
    #     **どちらも存在しないパス**を指しており、書いてある手順が実行できなかった。
    #
    #     見るのは ```bash ブロックの中の `python <path>` だけ。
    #     地の文の `Core.hlsl` のような URP のファイル名まで見ると誤検出になる
    #     ── BACKLOG は経緯として URP のファイル名を挙げるので、最初にそれで
    #     8 件の誤検出を出した。**判定できるのは「実行できるか」だけ。**
    doc_files = sorted(doc_scan.rglob("*.md"))

    # **入力が空なら「問題が無い」ではなく「見ていない」と言う。**
    # 今回の 2 件は文書が 1 件も見つからないまま指摘 0 件を返していた。
    # 探し方を間違えても、置き場所が変わっても、同じ静かな 0 になる。
    if not doc_files:
        out.append(Finding(
            "warn", doc_scan.name, "文書が 1 件も見つからない",
            f"`{doc_scan}` の下に .md が無いので、"
            f"**手順の検査と案内の検査は動いていない**。"
            f" 指摘 0 件を「問題が無い」と読まないこと。"))

    for doc in doc_files:
        text = doc.read_text(encoding="utf-8", errors="replace")

        for block in re.findall("```bash" + chr(10) + "(.*?)```", text, re.DOTALL):
            for m in re.finditer(r"python[3]?\s+([\w./-]+\.py)", block):
                leaf = m.group(1).rsplit("/", 1)[-1]
                if leaf in pkg_names:
                    continue
                out.append(Finding(
                    "error", doc.name, f"コマンドが存在しないスクリプトを指す: {m.group(1)}",
                    "書いてある手順がそのままでは動かない（T-107 と同じ）。"))

    # (2) REQUIREMENTS の実装欄が指す識別子が実在するか。
    req = find_file(root, "REQUIREMENTS.md")
    if req.exists():
        text = req.read_text(encoding="utf-8", errors="replace")

        for m in re.finditer(r"^\| (FR-\d+) \| [^|]+ \| ([^|]+) \|$", text, re.MULTILINE):
            for tok in set(re.findall(r"`([A-Za-z_][\w.]*)`", m.group(2))):
                if tok.endswith((".cs", ".py", ".hlsl", ".shader")):
                    continue                     # (1) が見ている

                # `ToonContext.bentN` のようなドット記法は末尾だけ照合する。
                # 構造体のフィールドはソースに「型 名前;」の形でしか現れない。
                leaf = tok.rsplit(".", 1)[-1]
                if leaf in src:
                    continue

                out.append(Finding(
                    "error", req.name, f"{m.group(1)} が実在しない実体を指す: {tok}",
                    "名前を変えたか削除したのに要件表が追従していない。"))

    # (2b) **サマリが実在しないものを案内していないか。**
    #
    # 機能を削除したとき、コードからは消しても**「セットアップにこれを付けろ」と
    # 書いた表の行が残る。** 読む人は存在しないコンポーネントを探すことになり、
    # しかも探しても見つからないので「自分の環境が壊れている」と読む。
    # 実際カプセル遮蔽を消した後、`ToonCapsuleOccluders` を案内する行が
    # サマリに残っていた（T-222）。
    #
    # **判定できる形に絞る。** 自前の識別子は `Toon` 前置詞を付ける規約
    # （CLAUDE.md）なので、`Toon` で始まるものだけ見る。シーン名や URP の
    # ファイル名まで見ると誤検出になる ── (1) で 8 件出した轍。
    #
    # BACKLOG は**サマリだけ**。以下の T-番号は「そのとき消した」という記録なので、
    # 消したものの名前が出てくるのが正しい。
    for doc_name in ("BACKLOG.md", "CLAUDE.md"):
        doc = find_file(root, doc_name)
        if not doc.exists():
            continue
        head = re.split(r"^### T-\d+",
                        doc.read_text(encoding="utf-8", errors="replace"),
                        maxsplit=1, flags=re.MULTILINE)[0]
        for tok in sorted(set(re.findall(r"`(Toon[A-Za-z0-9_]*)`", head))):
            if tok in src or f"{tok}.cs" in names:
                continue
            # **文書は Idol と Cel で共用している。** Idol を対象に走らせたとき
            # Cel 側の識別子を「実在しない」と言わないよう、外れたら全体で見る。
            if tok in pkg_src() or f"{tok}.cs" in pkg_names:
                continue
            out.append(Finding(
                "error", doc_name, f"サマリが実在しないものを案内している: {tok}",
                "削除したのに案内の行が残っている。読む人は無い物を探すことになる。"))

    # (3) BACKLOG のサマリが項目数を正しく言っているか。
    #     **数字がずれていたら中身もずれている。** T-160 では
    #     「99 項目」のまま 160 まで伸びており、案内していたツールが
    #     その時点で動いていなかった。
    log = find_file(root, "BACKLOG.md")
    if log.exists():
        text = log.read_text(encoding="utf-8", errors="replace")
        actual = len(re.findall(r"^### T-\d+", text, re.MULTILINE))
        m = re.search(r"この文書は\s*(\d+)\s*項目", text)

        if m and int(m.group(1)) != actual:
            out.append(Finding(
                "error", log.name, "サマリの項目数が実際と違う",
                f"「{m.group(1)} 項目」と書いてあるが実際は {actual} 件。"
                f" 数字がずれているならサマリの中身もずれている ── "
                f"現在の状態・判断待ち・推奨ツールを見直すこと。"))

    # (5) **サマリに書いた数字が、実装から計算した値と合っているか。**
    #
    # T-199 で「サマリは機械が守れない」と書いたが、**それは半分だけ本当だった。**
    # 文章の鮮度は判定できないが、**書いてある数字はどれもソースから計算できる。**
    # 「書き写した数字は古くなる」はこのプロジェクトの最大の持病
    # （T-155 / T-167 / T-168 / T-192）なので、計算できるものは計算して突き合わせる。
    log = find_file(root, "BACKLOG.md")
    if log.exists():
        # **サマリだけを見る。** 以下の T-番号の項目は「そのとき何項目だったか」の
        # 記録で、書き換えるのは履歴の改竄になる。ところがこの検査は文書全体を
        # 探していたので、T-200 の履歴の表にあった `**39 項目 / カバー率 34 検査**`
        # を掴んでいた ── **サマリを正しく直しても落ち続ける**という、
        # 直し方の分からない失敗の形になる（T-221 で踏んだ）。
        text = re.split(r"^### T-\d+", log.read_text(encoding="utf-8", errors="replace"),
                        maxsplit=1, flags=re.MULTILINE)[0]

        def count_re(path: Path, pattern: str) -> int:
            if not path.exists():
                return -1
            return len(set(re.findall(pattern, path.read_text(
                encoding="utf-8", errors="replace"), re.MULTILINE)))

        claims: list[tuple[str, str, int]] = []

        n_checks = count_re(find_file(root, "param_check.py"), r"^def (check_\w+)")
        claims.append(("値の検算", r"値の検算（(\d+) 種）", n_checks))

        n_codes = count_re(find_file(root, "shader_lint.py"), r'Issue\(\s*"([EW]\d{3})"')
        # **範囲を式に埋め込まないこと。** `W101-W107` と決め打ちしていたので、
        # コードが W111 まで増えた後も**その表記でないと一致しない**状態だった
        # ── 文書に本当のこと（W101-W111）を書くと検査が黙り、
        # 検査を通すために**嘘を書く**羽目になる（実際 1 度そうした。T-329）。
        claims.append(("検査コード", r"W101-W\d+ の (\d+) コード", n_codes))

        st = find_file(root, "self_test.py")
        if st.exists():
            body = st.read_text(encoding="utf-8", errors="replace")
            # **注入ケースだけでは足りない。** サンドボックスで動かせない道具
            # （`csharp_compile.py` など）は道具の中の関数を直接撃つ形にしてあり、
            # それも自己診断の 1 項目として数える（T-257）。
            claims.append(("自己診断の項目",
                           r"\*\*(\d+) 項目 / カバー率",
                           len(re.findall(r"^    Case\(", body, re.MULTILINE))
                           + len(re.findall(r"^    \(\"", body, re.MULTILINE))))

        # **設計文書（ARCHITECTURE.md）の数字も実装から数え直す。**
        # ここは移植先の作法を満たしているかの記録で、**満たさなくなったことに
        # 気付けないと移植の前提が崩れる。** 実際 3 箇所が古くなっていた（T-244）。
        arch = find_file(root, "ARCHITECTURE.md")
        if arch.exists():
            atext = arch.read_text(encoding="utf-8", errors="replace")
            sh = ((find_main_shader(root) or root / '_missing_.shader')).read_text(encoding="utf-8", errors="replace")
            body = sh[sh.index("Properties"):sh.index("SubShader")]

            n_tex = len(re.findall(
                r"^[ 	]*(?:\[[^\]]*\][ 	]*)*_\w+[ 	]*\([ 	]*\"[^\"]*\"[ 	]*,"
                r"[ 	]*2D[ 	]*\)", body, re.MULTILINE))
            m2 = re.search(r"2D (\d+) 個すべて既定値あり", atext)
            if m2 and int(m2.group(1)) != n_tex:
                out.append(Finding(
                    "error", arch.name, "設計文書のテクスチャ数が実装と違う",
                    f"「2D {m2.group(1)} 個」と書いてあるが実際は {n_tex} 個。"
                    f" **既定値の指定漏れは、未ベイクのマテリアルが黒くなる形で出る。**"))

            n_pass = len(re.findall(r'Name\s+"\w+"', sh))
            m3 = re.search(r"\*\*(\d+) パス。\*\*", atext)
            if m3 and int(m3.group(1)) != n_pass:
                out.append(Finding(
                    "error", arch.name, "設計文書のパス数が実装と違う",
                    f"「{m3.group(1)} パス」と書いてあるが実際は {n_pass}。"
                    f" **パスを足したら Pass/LightMode の表にも足すこと。**"))

        # 実コンパイルの組数。ForwardLit だけがサーフェスタイプ軸を回す。
        vc = find_file(root, "ToonPBRVariantCheck.cs")
        if vc.exists():
            src = vc.read_text(encoding="utf-8", errors="replace")

            def block(name: str) -> str:
                i = src.find(name)
                return src[i:src.find("};", i)] if i >= 0 else ""

            surfaces = len(re.findall(r'"_SURFACETYPE_\w+"', block("SurfaceTypes =")))
            forward = len(re.findall(r'\("[^"]+",\s*new', block("FeatureSets =")))
            others = len(re.findall(r'\("[^"]+",\s*new', block("PassSets =")))
            if surfaces and forward:
                claims.append(("実コンパイルの組数",
                               r"実コンパイル（(\d+) 組）", forward * surfaces + others))

        for label, pattern, actual in claims:
            if actual < 0:
                continue
            m = re.search(pattern, text)
            if not m:
                # **「書いていないなら見ない」で 1 つ死んでいた。**
                # `実コンパイル（N 組）` は文書側にその一文が無く、
                # ここを黙って通っていたので**何とも比べていなかった**
                # ── 検算が 1 つ減ったことに誰も気づけない（T-329）。
                #
                # 文言を書き換えて一致しなくなる形でも同じことが起きる。
                # 実際、サマリを書き直したときに 3 つの検算が同時に黙り、
                # しかも**検査は全部 OK と報告した**。
                #
                # 逆に、検査を通すために文書へ**嘘を書く**道もある。
                # `W101-W107` と決め打ちしたパターンに合わせて、
                # 本当は W111 まであるのに古い範囲を書いた（同 T-329）。
                # パターンは**表記に依らない形**にしておくこと。
                out.append(Finding(
                    "warn", log.name, f"サマリに主張が無い: {label}",
                    f"実装から {actual} と数えられるのに、"
                    f"サマリにその一文（`{pattern}`）が無い。"
                    f" **比べる相手が無い検算は黙って通る。**"
                    f" 書き足すか、検算が要らないなら claims から外すこと。"))
                continue
            if int(m.group(1)) != actual:
                out.append(Finding(
                    "error", log.name, f"サマリの数字が実装と違う: {label}",
                    f"「{m.group(1)}」と書いてあるが、実装から数えると {actual}。"
                    f" **書き写した数字は古くなる。**"
                    f" サマリは次のセッションが最初に読む場所なので、"
                    f"ここがずれていると全部が古い前提から始まる。"))

    # (4) CLAUDE.md の検査コード表が shader_lint.py の実装と一致しているか。
    #     **同じずれを2回やった。** E008 を足したときも E009 を足したときも
    #     表の更新を忘れており、E009 のときは BACKLOG のサマリも
    #     「E001-E008」のまま残っていた（T-168）。
    #
    #     CLAUDE.md は毎セッション読み込まれる。**そこに無い検査は無いのと同じ**
    #     ── 実装したのに誰も知らない検査ができる。
    #
    #     逆向きも見る。表にあって実装に無いコードは、消した検査の行が
    #     残っている状態で、こちらは「あるはずの検査が無い」誤解を生む。
    lint = find_file(root, "shader_lint.py")
    claude = find_file(root, "CLAUDE.md")
    if lint.exists() and claude.exists():
        impl = set(re.findall(r'Issue\(\s*"([EW]\d{3})"',
                              lint.read_text(encoding="utf-8", errors="replace")))
        doc = set(re.findall(r"^\| ([EW]\d{3}) \|",
                             claude.read_text(encoding="utf-8", errors="replace"),
                             re.MULTILINE))
        for code in sorted(impl - doc):
            out.append(Finding(
                "error", claude.name, f"検査コード {code} が CLAUDE.md の表に無い",
                f"shader_lint.py は {code} を出すが表に載っていない。"
                f" CLAUDE.md は毎セッション読み込まれるので、"
                f"**そこに無い検査は無いのと同じ**になる。"))
        for code in sorted(doc - impl):
            out.append(Finding(
                "error", claude.name, f"検査コード {code} は実装に無い",
                f"表には載っているが shader_lint.py は {code} を出さない。"
                f" 消した検査の行が残っていると「あるはずの検査が無い」誤解を生む。"))

    return out


MAT_ALPHA_RE = re.compile(
    r"^[ \t]*- _BaseColor:[ \t]*\{r: [-\d.eE+]+, g: [-\d.eE+]+, b: [-\d.eE+]+,"
    r" a: ([-\d.eE+]+)\}", re.MULTILINE)

MAT_BASEMAP_RE = re.compile(
    r"^[ \t]*- _BaseMap:" + chr(10) + r"[ \t]*m_Texture: \{fileID: (\d+)", re.MULTILINE)


def check_alpha_clip(path: Path, kw: set[str], v: dict[str, float],
                     where: str) -> list[Finding]:
    """アルファテストで**全画素が落ちる**マテリアルを見つける。

    シェーダーの式（ForwardLit / 各深度パス共通）:
        albedo = SAMPLE(_BaseMap) * _BaseColor
        clip(albedo.a - _Cutoff)

    `_BaseMap` が未割り当てなら Unity は既定テクスチャを挿す。
    このシェーダーの宣言は `"white"` なので `baseTex.a = 1`、
    つまり実効アルファは `_BaseColor.a` そのもの。
    それが `_Cutoff` を下回ると**1画素も残らない。**

    **エラーも警告も出ずに、ただ何も描かれない。**
    Unity のインスペクタはマテリアルを正常に表示するし、
    シェーダーもコンパイルは通る。メッシュが消えていることに
    気付くには実際に絵を見るしかない ── このプロジェクトが
    繰り返し踏んでいる「実装したのに効いていない」の最も静かな形。

    **テクスチャが割り当ててあるときは見ない。** そのアルファは読めないので
    判定できない。誤検出を出すくらいなら見逃すほうがよい。
    """
    out: list[Finding] = []
    if "_ALPHATEST_ON" not in kw:
        return out

    text = path.read_text(encoding="utf-8", errors="replace")

    m = MAT_BASEMAP_RE.search(text)
    if m is None or m.group(1) != "0":
        return out                      # 割り当て済み、または読めない形

    a = MAT_ALPHA_RE.search(text)
    alpha = float(a.group(1)) if a else 1.0
    cutoff = v.get("_Cutoff", 0.5)

    if alpha < cutoff:
        # **エラーではなく警告。** 「1画素も描かれない」は事実だが、
        # **それが事故か意図かは機械では決められない。**
        # 実際 `13.mekage`（目影）を調べたら、`Materials` / `Materials 1` /
        # `Materials 2` / `Materials 3` の**4世代すべて**で同じ状態だった
        # ── シェーダーを3回移行しても直っていない、つまり**最初の取り込みから**。
        # VRoid 系でサブメッシュを隠す常套手段がまさにこれ（アルファを 0 にする）で、
        # 作者が意図的に消していると読むのが自然だった。
        #
        # エラーにしていたあいだ `check.py` は**何度回しても赤**で、
        # 「正しい設定に警告を出さない」（CLAUDE.md）に反していた。
        # 赤が常態になったゲートは読まれなくなる ── T-167 で直したのと同じ失敗。
        out.append(Finding(
            "warning", where, "1画素も描かれない（意図的に隠している可能性）",
            f"_BaseMap が未割り当て（既定の white → alpha 1）で"
            f" _BaseColor.a = {alpha}、_Cutoff = {cutoff}。"
            f" clip({alpha} - {cutoff}) が全画素で負になり、このマテリアルは"
            f"**1画素も描かれない。**"
            f" 意図的に隠しているならこのままでよい ── アルファを 0 にするのは"
            f"サブメッシュを消す常套手段。"
            f" 出したいのなら BaseMap を割り当てるか、_BaseColor.a を"
            f" {cutoff} 以上にするか、Alpha Clip を切ること。"))

    return out


def check_shadow_band(v: dict[str, float], where: str) -> list[Finding]:
    """影色の変換が破綻していないか。"""
    out: list[Finding] = []

    sat = v.get("_ShadowSaturation")
    val = v.get("_ShadowValue")
    mix = v.get("_ShadowColorMix")

    # 影が光より明るくなる設定は、まず間違いなく事故。
    if val is not None and val > 1.0:
        out.append(Finding(
            "error", where, "影の明度が 1 を超えている",
            f"_ShadowValue = {val}。影の方が光より明るくなる。"))

    # 彩度を上げすぎると、彩度の高い部位で色が飽和して階調が消える。
    if sat is not None and sat > 2.5:
        out.append(Finding(
            "warning", where, "影の彩度スケールが高い",
            f"_ShadowSaturation = {sat}。元の彩度が 0.4 を超える部位で"
            f" HSV の S が 1 に張り付き、影の中の階調が消える。"))

    if mix is not None and not (0.0 <= mix <= 1.0):
        out.append(Finding(
            "error", where, "影色の混合率が範囲外",
            f"_ShadowColorMix = {mix}。lerp の外挿になり色が破綻する。"))

    return out


def check_diffuse_reach(v: dict[str, float], where: str,
                        defaults: dict[str, float]) -> list[Finding]:
    """**光を当てても明るくならない設定**になっていないか。

    拡散の伝達関数はエネルギー保存形の wrap を通してから閾値を掛ける:

        rawT = (NdotL + wrap) / (1 + wrap)²        [ToonWrapDiffuse]
        lit  = smoothstep(閾値 - 幅, 閾値 + 幅, rawT)

    **wrap を上げると rawT の上限が下がる。** NdotL = 1（光が真正面）でも
    `1 / (1 + wrap)` までしか届かない ── wrap 0.25 で 0.80、wrap 1.0 で 0.50。

    `_ShadowThreshold` と `_DiffuseWrap` は**どちらも Range(0,1) で独立に動く。**
    閾値が上限を超えると、**どんなライトを当てても全面が影**になる。
    絵は「真っ暗」ではなく「影色で塗られたまま動かない」ので、
    ライトの設定を疑い続けることになる ── 原因に辿り着けない類。

    エネルギー保存の正規化そのものは正しい（Call of Duty / Frostbite の形）。
    問題は**2 つのスライダの組み合わせに何の警告も無い**こと。
    """
    out: list[Finding] = []

    def val(name: str) -> float | None:
        got = v.get(name, defaults.get(name))
        return got

    th = val("_ShadowThreshold")
    wrap = val("_DiffuseWrap")
    soft = val("_ShadowSoftness")
    if th is None or wrap is None or soft is None:
        return out

    cap = 1.0 / (1.0 + wrap)      # NdotL = 1 のときの rawT

    if cap <= th - soft:
        out.append(Finding(
            "error", where, "光を当てても一切明るくならない",
            f"_DiffuseWrap = {wrap} なので rawT は最大 {cap:.3f} までしか届かないが、"
            f" _ShadowThreshold = {th} / 幅 {soft} なので明側に入るには {th - soft:.3f} が要る。"
            f" **どんなライトを当てても全面が影のまま。**"
            f" 閾値を {cap - soft:.2f} 以下にするか、wrap を下げること。"))
    elif cap < th + soft:
        out.append(Finding(
            "warning", where, "最も明るい面でも完全には明るくならない",
            f"_DiffuseWrap = {wrap} で rawT の上限が {cap:.3f}、"
            f" 完全な明側には {th + soft:.3f} が要る。"
            f" 光が真正面から当たっている面でも "
            f"{min(1.0, max(0.0, (cap - (th - soft)) / max(2 * soft, 1e-6))):.0%} "
            f"までしか明るくならず、**全体が薄く曇る。**"))

    return out


SHEEN_COEF_RE = re.compile(
    r"const float3 k([0-4])\s*=\s*float3\(\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)\s*\)")


def check_sheen_fit(root: Path) -> list[Finding]:
    """`ToonSheenAlbedo` の多項式が**実際に半球積分と一致するか**を検算する。

    ここまでの検査は「宣言があるか」「値の大小関係が成立するか」を見ていた。
    これは初めて**物理そのもの**を確かめる ── Charlie 分布と Ashikhmin 可視項の
    半球積分を Python 側で解き、シェーダーの多項式と突き合わせる。

    なぜ要るか: この多項式は**数値で解いた結果を人が書き写した 15 個の定数**で、
    ソースには「最大誤差 0.044 / RMS 0.005」と書いてある。
    **その主張を誰も確かめていなかった。**「書き写した数字は古くなる」は
    このプロジェクトが繰り返し踏んでいる形（T-155 / T-167 / T-168）で、
    しかもここは**間違っていても絵が少し変わるだけ**なので目視では見つからない。

    sheen は下地を `(1 - sheenColor * E)` で縮めてから足すのに使う。
    E がずれると布のエネルギー保存が崩れ、**縁が明るくなるか暗くなる。**

    **係数はソースから読む。** ここに書き写したら同じ罠にはまる。
    """
    out: list[Finding] = []
    text = read_all_hlsl(root)
    if not text:
        return out
    coef = {int(m.group(1)): tuple(float(m.group(i)) for i in (2, 3, 4))
            for m in SHEEN_COEF_RE.finditer(text)}
    if len(coef) != 5:
        # **係数が 1 つも無いのは「別のシェーダーを見ている」ということ。**
        # 布の sheen を持たない木に当てても意味が無いので黙って抜ける。
        # 中途半端に見つかったときだけ「書き方が変わった」と報告する。
        if not coef:
            return out
        out.append(Finding(
            # **ここは `hlsl.name` と書いてあって未定義だった。**
            # エラー経路そのものが壊れており、係数が読めない状況になると
            # 報告の代わりに `NameError` で**検算が丸ごと落ちる**。
            # 一度も通っていない経路は、書いてあっても動くとは限らない。
            "error", str(root), "sheen の多項式係数が読めない",
            f"k0〜k4 のうち {len(coef)} 個しか見つからない。"
            f" 書き方を変えたなら param_check の SHEEN_COEF_RE も直すこと。"))
        return out

    import math

    def d_charlie(ndoth: float, a: float) -> float:
        inv = 1.0 / max(a, 0.002)
        sin2 = max(1.0 - ndoth * ndoth, 0.0078125)
        return (2.0 + inv) * (sin2 ** (inv * 0.5)) / (2.0 * math.pi)

    def e_numeric(ndotv: float, a: float, nt: int = 48, npz: int = 96) -> float:
        ct = min(max(ndotv, 1e-4), 1.0)
        st = math.sqrt(max(1.0 - ct * ct, 0.0))
        total = 0.0
        for i in range(nt):
            th = (i + 0.5) * (math.pi / 2) / nt
            sl, cl = math.sin(th), math.cos(th)
            dw = sl * ((math.pi / 2) / nt) * ((2 * math.pi) / npz)
            for j in range(npz):
                ph = (j + 0.5) * (2 * math.pi) / npz
                lx, ly, lz = sl * math.cos(ph), sl * math.sin(ph), cl
                hx, hy, hz = lx + st, ly, lz + ct
                hn = math.sqrt(hx * hx + hy * hy + hz * hz) or 1e-9
                vis = 1.0 / (4.0 * (lz + ct - lz * ct) + 1e-5)
                total += d_charlie(hz / hn, a) * vis * lz * dw
        return total

    def e_poly(ndotv: float, a: float) -> float:
        x = 1.0 - min(max(ndotv, 0.0), 1.0)
        q = 1.0 / (1.0 + a)
        qv = (1.0, q, q * q)
        dot = lambda k: sum(k[i] * qv[i] for i in range(3))
        e = dot(coef[4])
        for idx in (3, 2, 1, 0):
            e = dot(coef[idx]) + x * e
        return min(max(e, 0.0), 1.0)

    worst = 0.0
    where = ""
    for a in (0.1, 0.3, 0.7, 1.0):
        for nv in (0.05, 0.5, 1.0):
            d = abs(e_poly(nv, a) - e_numeric(nv, a))
            if d > worst:
                worst, where = d, f"粗さ {a} / NdotV {nv}"

    # ソースが名乗っている上限（0.044）に、格子の粗さぶんの余裕を足した値。
    # 細かい格子で 0.031 だったので、0.08 を超えたら係数が別物になっている。
    if worst > 0.08:
        out.append(Finding(
            # **ここも `hlsl.name` だった（2 か所目）。**
            # 1 か所目を直したときに同じ関数の中を見ておらず、
            # `tool_lint.py` が拾って初めて分かった。
            "error", str(root), "sheen の多項式が半球積分と合っていない",
            f"最大誤差 {worst:.3f}（{where}）。ソースは「最大誤差 0.044」と"
            f"名乗っているが、実際に Charlie 分布と Ashikhmin 可視項を"
            f"積分すると合わない。**係数が書き換わっている。**"
            f" sheen は下地を (1 - sheenColor * E) で縮めるのに使うので、"
            f"E がずれると布のエネルギー保存が崩れ、縁が明るくなるか暗くなる。"))

    return out


ENVBRDF_COEF_RE = re.compile(
    r"const float4 (c[01])\s*=\s*float4\(([^)]*)\)")


def check_energy_compensation(root: Path) -> list[Finding]:
    """多重散乱の補償が**本当にエネルギーを保存しているか**を白炉試験で確かめる。

    `check_sheen_fit` が sheen の指向性アルベドを検算しているのに対し、
    こちらは GGX 側。**同じ種類の未検証の主張が隣にあった。**

    **白炉試験（f0 = 1 で 1.0 を返すか）は検査に使えない。** 最初そう書いたが、
    Fdez-Agüera の式は f0 = 1 のとき Favg = 1 になり、**係数に関係なく
    恒等的に 1.0** になる。落ちようのない検査を置くと
    「エネルギー保存を検算した」という誤った安心になるので外した。
    （手で確認した結果は「ずれ 0.000000」── 式の性質であって、実装の証拠ではない。）

    代わりに、係数でしか決まらない性質を見る:
      - Ess = A + B が 1 を超えない（超えたら補償前からエネルギーが増えている）
      - Ess が粗さに対して単調に下がる（粗い面ほど取りこぼす）
      - 誘電体の正面反射率が f0 に一致する
    そして**式の形**を見る（下記）。

    **ここは Filament と違う割り方をしている。** Filament の
    `energyCompensation = 1 + f0 * (1/dfg.y - 1)` は DFG の B 項で割るが、
    この実装は `Ess = A + B` で割る。Karis の解析フィットでは B が正面付近で
    0.001 台まで落ちるため、**B で割ると誘電体の補償倍率が発散する**
    （実測: 無限大）。逸脱には理由があり、白炉試験で 1.0 ちょうどになる。

    **係数はソースから読む。** 写したら「書き写した数字は古くなる」に嵌る。

    **限界: 式の構造は Python 側に写してある。** 係数を変えれば追随するが、
    式の形を書き換えるとこの検査は古い形を検算し続ける
    （`check_sheen_fit` と同じ制約）。
    """
    out: list[Finding] = []
    hlsl_files = sorted(root.rglob("ToonPBREnv.hlsl"))
    if not hlsl_files:
        return out
    src = hlsl_files[0]
    text = src.read_text(encoding="utf-8", errors="replace")

    coef = {m.group(1): tuple(float(x) for x in m.group(2).split(","))
            for m in ENVBRDF_COEF_RE.finditer(text)}
    if len(coef) != 2 or any(len(v) != 4 for v in coef.values()):
        out.append(Finding(
            "error", src.name, "EnvBRDF の係数が読めない",
            f"c0 / c1 のうち {len(coef)} 個しか見つからない。"
            f" 書き方を変えたなら param_check の ENVBRDF_COEF_RE も直すこと。"
            f" **読めないまま黙って通すと、検算していないのに通ったように見える。**"))
        return out
    c0, c1 = coef["c0"], coef["c1"]

    def env_ab(pr: float, ndotv: float) -> tuple[float, float]:
        rx, ry = pr * c0[0] + c1[0], pr * c0[1] + c1[1]
        rz, rw = pr * c0[2] + c1[2], pr * c0[3] + c1[3]
        a004 = min(rx * rx, 2.0 ** (-9.28 * ndotv)) * rx + ry
        return -1.04 * a004 + rz, 1.04 * a004 + rw

    def multi_scatter(f0: float, pr: float, ndotv: float) -> float:
        a, b = env_ab(pr, ndotv)
        fss = max(f0 * a + b, 0.0)
        ess = a + b
        favg = f0 + (1.0 - f0) / 21.0
        return fss + fss * favg / (1.0 - (1.0 - ess) * favg) * (1.0 - ess)

    rough = [i / 20 for i in range(21)]
    ndotv = [i / 20 for i in range(1, 21)]

    # (1) 補償の前からエネルギーが増えていないか
    ess_max = max(sum(env_ab(r, v)) for r in rough for v in ndotv)
    if ess_max > 1.001:
        out.append(Finding(
            "error", src.name, "単散乱のアルベドが 1 を超えている",
            f"Ess = A + B の最大が {ess_max:.4f}。**補償を掛ける前から"
            f"入射より多く返している。** 補償はこれを更に持ち上げるので、"
            f"粗い金属が白く飛ぶ。"))

    # (2) Ess は粗いほど単調に下がるはず（粗い面ほどエネルギーを取りこぼす）。
    #     このフィットでは Ess = rz + rw で NdotV に依らず、粗さの一次式になる。
    for v in (0.05, 0.5, 1.0):
        seq = [sum(env_ab(r, v)) for r in rough]
        bad = [(rough[i], seq[i], seq[i + 1]) for i in range(len(seq) - 1)
               if seq[i + 1] > seq[i] + 1e-6]
        if bad:
            r0, a0, a1 = bad[0]
            out.append(Finding(
                "error", src.name, "単散乱のアルベドが粗さに対して単調でない",
                f"粗さ {r0:.2f} 付近で Ess が {a0:.4f} → {a1:.4f} と上がっている"
                f"（NdotV {v}）。**粗い面ほど取りこぼすはず**なので係数がおかしい。"))
            break

    # (3) **式の形そのものを見る。**
    #
    # **白炉試験（f0 = 1 で 1.0 になるか）は検査にならない。** 実際に書いてみて
    # 気付いたが、Fdez-Agüera の式は f0 = 1 のとき Favg = 1 になり、
    # `Ess + (Ess/Ess) * (1 - Ess)` が**係数に関係なく恒等的に 1** になる。
    # 直接光側も `Ess × (1/Ess)` で同じ。**どんな係数でも通る＝落ちない検査**で、
    # 置いておくと「エネルギー保存を検算している」という誤った安心になる。
    # 係数は上の (1)(2)(4) で見て、式の形はここで見る。
    #
    # 一番起きやすい書き換えは「Filament に合わせて B 項で割る」こと
    # ── 出回っている式なので善意で戻されうるが、この解析フィットでは
    # B が正面付近で 0.001 台まで落ち、誘電体の補償倍率が発散する（実測: 無限大）。
    #
    # **識別子の有無で見ないこと。** 最初 `"Favg" not in body` と書いたが、
    # `Favg` を `FavgX` に変えるだけで**部分文字列として素通りした**。
    # 見るのは戻り値の構造 ── 単散乱 `FssEss` に、取りこぼしを戻す第 2 項が
    # 足されたままか。消せば `return FssEss;` になるので必ず引っ掛かる。
    ms_body = re.search(r"float3 ToonEnvBRDFMultiScatter\([^)]*\)\s*\{(.*?)\n\}",
                        text, re.DOTALL)
    ms_ret = re.search(r"return\s+FssEss\s*\+[^;]*_EnergyCompensation[^;]*;",
                       ms_body.group(1)) if ms_body else None
    if ms_ret is None:
        out.append(Finding(
            "error", src.name, "多重散乱の補償が戻り値から消えている",
            "`ToonEnvBRDFMultiScatter` が「単散乱 + 取りこぼしを戻す項」の形で"
            " 返していない（`return FssEss + ... _EnergyCompensation ...;`）。"
            " **粗い金属が暗く濁って背景と質感が揃わない。**"))
    body = re.search(r"float3 ToonEnergyCompensation\([^)]*\)\s*\{(.*?)\n\}",
                     text, re.DOTALL)
    if body is None:
        out.append(Finding(
            "error", src.name, "ToonEnergyCompensation の本体が読めない",
            "関数の書き方が変わったなら param_check の正規表現も直すこと。"
            " **読めないまま黙って通すと検算していないのに通ったように見える。**"))
    elif "AB.x + AB.y" not in body.group(1):
        out.append(Finding(
            "error", src.name, "補償の割る相手が Ess でなくなっている",
            "`ToonEnergyCompensation` が `AB.x + AB.y`（＝ Ess）で割っていない。"
            " Filament の書き方（B 項で割る）へ戻すと、この解析フィットでは"
            " **誘電体の補償倍率が発散して鏡面が白飛びする。**"
            " LUT を持つ実装なら B で正しいが、ここは Karis の解析フィット。"))

    # (4) 誘電体の正面。ここは f0 そのものに近くなければならない
    #     （斜めは Fresnel で 1 に近づくのが正しいので見ない）
    front = multi_scatter(0.04, 0.0, 1.0)
    if not (0.03 <= front <= 0.07):
        out.append(Finding(
            "error", src.name, "誘電体の正面反射率が f0 から外れている",
            f"f0 = 0.04・粗さ 0・正面で {front:.4f}。0.04 付近になるはず。"
            f" **肌や布の鏡面が全体的に明るすぎるか暗すぎる。**"))

    return out


MIGRATION_RULE_RE = re.compile(
    r'new Rule\(Kind\.(\w+),\s*"(_\w+)",\s*"(_\w+)"')

# 移行元パッケージ。無ければこの検査は黙って飛ばす（他所のプロジェクトで動かすため）。
MIGRATION_SOURCES = {
    # 旧 Cel（旧 Idol の改名先。T-249）は T-356 で廃止したため Doll のみ。
    "Doll": "com.origuma.easypbr-urp/Runtime/Shaders/Doll/Doll.shader",
}


def check_dead_gates(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**効果ゼロなのにコストだけ払っている機能**を見つける。

    このシェーダーは既定 OFF の機能を「値そのもの」で切っている
    （`_MatCapIntensity > 0` など）。移植元では**別のトグル**で切っていて
    値の既定が 1 のものがあり、シェーダーを差し替えると
    **値だけが残って機能が勝手に ON になる。**

    実際に踏んだ（T-255）── 46 マテリアル全部で `_MatCapIntensity` が 1 だった。
    `_MatCapTex` は未割り当てで既定が黒なので**絵は変わらない**が、
    分岐は通るのでフェッチ 1 回と 26 命令を毎画素払っていた。

    **絵に出ないので、目視でも実機でも気付けない。**
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    # **未割り当ての既定色が中立とは限らない。**
    #
    # Unity は未割り当てのテクスチャに単色（white / black / gray / bump）を返す。
    # 中立値がチャンネルごとに違うマップでは、白は「無変化」ではなく
    # **特定の壊れ方**になる ── しかも例外も警告も出ないので、
    # 絵がおかしいのがテクスチャの割り当て忘れだとは気付けない。
    #
    # (ゲート, テクスチャ, 絵が壊れるか, 未割り当てのとき何が起きるか)
    GATES = [
        ("_MatCapIntensity", "_MatCapTex", False,
         "既定が黒なので**加算値が 0**。絵は変わらないがフェッチと約 26 命令を払う"),
        ("_CavityStrength", "_CavityMap", False,
         "既定が白なので**窪みが 1（無変化）**。絵は変わらないがフェッチを払う"),
        ("_NPRMapOn", "_NPRMap", True,
         "既定が白だと **G が 1**（基準は 0.5）になり、**影が最大まで遅れて出なくなる**"),
        ("_UseRampMap", "_RampMap", True,
         "既定が白だと拡散が全面明るくなり、**陰影が消えてべた塗りになる**"),
        ("_StockingIntensity", "_StockingMask", True,
         "既定が白だと **材質の全面**にストッキングがかかる"),
        ("_DissolveAmount", "_DissolveTex", True,
         "既定が一様な白なので模様が出ず、**全体が一斉に消える**"),
    ]

    for gate, tex, breaks, why in GATES:
        bad: list[str] = []
        for mat in find_materials(materials_dir):
            text = mat.read_text(encoding="utf-8", errors="replace")
            m = re.search(rf"- {gate}: ([-\d.eE+]+)", text)
            if not m or float(m.group(1)) == 0.0:
                continue
            assigned = re.search(
                rf"- {tex}:\s*\n\s*m_Texture: \{{fileID: (\d+)", text)
            if not (assigned and assigned.group(1) != "0"):
                bad.append(mat.stem)
        if bad:
            out.append(Finding(
                "error" if breaks else "warning", f"{len(bad)} 件（{bad[0]} ほか）",
                f"{gate} が 0 でないのに {tex} が未割り当て",
                f"{why}。使わないなら {gate} を 0 にすること。"
                f" **移植元では別のトグルで切っていた値が残っている**可能性が高い。"
                # **指摘だけして直し方を書かないと動かない。**
                # 「0 にすること」は 66 件を手で回せという意味になっていた。
                + ("" if breaks else
                   " 絵が変わらないものは"
                   " `Tools > Idol > 絵に出ない計算を止める` で一括で 0 にできる"
                   "（テクスチャが割り当ててあるものには触らない）。")))

    # 顔 SDF はトグルではなく**サーフェスタイプ**で決まる。
    # KeywordEnum(Default, Skin, Face, Hair, Cloth) の 3 番目。
    bad = []
    for mat in find_materials(materials_dir):
        text = mat.read_text(encoding="utf-8", errors="replace")
        m = re.search(r"- _SurfaceType: ([-\d.eE+]+)", text)
        if not m or round(float(m.group(1))) != 2:
            continue
        assigned = re.search(r"- _FaceSDFMap:\s*\n\s*m_Texture: \{fileID: (\d+)", text)
        if not (assigned and assigned.group(1) != "0"):
            bad.append(mat.stem)
    if bad:
        out.append(Finding(
            "error", f"{len(bad)} 件（{bad[0]} ほか）",
            "顔なのに _FaceSDFMap が未割り当て",
            "既定が白だと SDF が常に 1 を返し、**顔が明るいままで SDF の陰が出ない**。"
            " 顔の陰の形を SDF で作らないなら Surface Type を Skin にすること。"))
    return out


def check_migration_rules(root: Path) -> list[Finding]:
    """移行スクリプトの対応表が**両側とも実在の名前を指しているか**を見る。

    **W107 の除外を入れたぶん、ここが無検査になった。**
    `ToonPBRMigrator.cs` は移行元（EasyToon / EasyPBR）のプロパティ名を書くので
    `// lint:foreign-begin` で W107 から外してある。その結果:

      - 移行元の名前を typo → `HasProperty` に守られて**そのプロパティだけ黙って移らない**
      - 移行先の名前を typo → W107 が撃つ（除外の外なので）

    前者が丸ごと抜けていた。**穴を開けたなら別の口で塞ぐこと。**

    移行元は `Packages/com.origuma.*` を読む。パッケージが無い環境では飛ばす
    ── 移行スクリプトが動かない環境で警告を出しても仕方がない。
    """
    out: list[Finding] = []
    cs = find_file(root, "ToonPBRMigrator.cs")
    if not cs.exists():
        return out

    # **移行元と移行先が同じシェーダーを指していないか。**
    #
    # シェーダーの名前を振り直すと、片側だけ書き換わって両者が一致することがある。
    # 実際に起きた（T-249）── 新シェーダーが旧シェーダーの名前を引き継いだとき、
    # 移行元の定数が古い名前のまま残り、**移行元＝移行先**になった。
    #
    # このとき例外は出ない。「対象 0 件」と表示されるだけで、
    # **移行できないのか対象が無いのかが区別できない。**
    src_text = cs.read_text(encoding="utf-8", errors="replace")
    names = dict(re.findall(r'private const string (\w+)\s*=\s*"([^"]+)"', src_text))
    target = names.get("TargetShader")
    if target:
        for key, value in names.items():
            if key == "TargetShader" or "Shader" not in key:
                continue
            if value == target:
                out.append(Finding(
                    "error", cs.name, "移行元と移行先が同じシェーダー",
                    f"`{key}` と `TargetShader` がどちらも '{value}'。"
                    f" **移行が成立しない。**「対象 0 件」と出るだけで"
                    f"原因が読めない形になる（T-249）。"))

    # **階層を数えないこと。** 元は `root.parent.parent / "Packages"` で、
    # コメントも `Assets/ToonPBR → Assets → プロジェクトルート` と
    # **移行前の配置**を数えていた。パッケージへ移って root が
    # `Packages/<パッケージ>/Runtime/Shaders/Idol` になった時点で
    # `Runtime/Packages` を探すようになり、**この検査は丸ごと死んだ**（T-331）。
    #
    # W107 の除外で開いた穴を塞ぐために書いた検査なので、
    # 死んでいる間は移行元の名前の打ち間違いが**どこにも出ない**。
    # 砂場は移行前の配置を写しているので試験は通り続けていた。
    #
    # 上へ登って見つける。`Packages/` の中に居るならその `Packages`、
    # `Assets/` の中に居るなら兄弟の `Packages`。
    # **候補を並べて 1 か所で判定する。** 分岐を 2 つ書くと、この配置では
    # 片方が必ず先に当たるので**もう片方は永久に偽**になり、
    # 「一度も当たらないガード」を数える検査（tool_lint）が撃つ。
    # 層違いのための分岐は、まとめれば偽陽性にならない。
    _parents = root.resolve().parents
    packages = next((c for c in
                     [p for p in _parents if p.name == "Packages"]
                     + [p / "Packages" for p in _parents]
                     if c.is_dir()), None)
    if packages is None:
        return out

    def props_of(path: Path) -> set[str]:
        text = path.read_text(encoding="utf-8", errors="replace")
        try:
            blk = text[text.index("Properties"):text.index("SubShader")]
        except ValueError:
            return set()
        return {m.group(1) for m in re.finditer(
            r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*\"", blk, re.MULTILINE)}

    src_props: set[str] = set()
    found_any = False
    for label, rel in MIGRATION_SOURCES.items():
        p = packages / rel
        if p.exists():
            src_props |= props_of(p)
            found_any = True
    if not found_any:
        return out

    dst_props = props_of((find_main_shader(root) or root / '_missing_.shader'))
    text = cs.read_text(encoding="utf-8", errors="replace")

    def ranges_of(path: Path) -> dict[str, tuple[float, float]]:
        t = path.read_text(encoding="utf-8", errors="replace")
        try:
            blk = t[t.index("Properties"):t.index("SubShader")]
        except ValueError:
            return {}
        return {mm.group(1): (float(mm.group(2)), float(mm.group(3)))
                for mm in re.finditer(
                    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*\"[^\"]*\"[ \t]*,"
                    r"[ \t]*Range\(\s*([-\d.]+)\s*,\s*([-\d.]+)\s*\)", blk, re.MULTILINE)}

    # **移行元ごとに別々に持つこと。** 最初は1つの辞書へマージしていて、
    # `_OutlineWidth` が Idol 0〜20 / Doll 0〜10 と食い違っているのを
    # **後から読んだ Doll が上書きして隠していた。**
    # 実際に移行スクリプトを走らせて初めて出た（T-189）── 静的検査が
    # 「合っている」と言っていた裏で、Idol からの移行だけが範囲外になっていた。
    per_source: dict[str, dict[str, tuple[float, float]]] = {}
    for label, rel in MIGRATION_SOURCES.items():
        p = packages / rel
        if p.exists():
            per_source[label] = ranges_of(p)
    dst_ranges = ranges_of((find_main_shader(root) or root / '_missing_.shader'))

    # **変換を書いていない行は、値域も一致していなければならない。**
    # 移行元のほうが広いと、正しい値のまま移しただけで Range の外に出る。
    # `Range` はスライダを縛るだけで実行時は縛らないので、
    # 外へ出た値は .mat に残って lerp の外挿になる（T-076 / T-098）。
    #
    # 実際 `_SpecularIntensity` が 0〜5 → 0〜4 で、実データに 5.0 が 1 個あった。
    # **同名だから安心、が通じない**（移行先が無いのを見逃したのと同じ思い込み）。
    for m in MIGRATION_RULE_RE.finditer(text):
        kind, src, dst = m.group(1), m.group(2), m.group(3)
        line = text[:m.start()].count("\n") + 1

        if kind == "Number" and dst in dst_ranges:
            # 同じ `new Rule(...)` の中にラムダがあるかで「変換あり」を判定する。
            tail = text[m.end():text.find("),", m.end()) + 2]
            if "=>" not in tail:
                dlo, dhi = dst_ranges[dst]
                for label, rng in per_source.items():
                    if src not in rng:
                        continue
                    slo, shi = rng[src]
                    if slo < dlo - 1e-9 or shi > dhi + 1e-9:
                        out.append(Finding(
                            "error", f"{cs.name}:{line}",
                            f"値域がはみ出す: {src} → {dst}（{label}）",
                            f"{label} の [{slo}, {shi}] が移行先 [{dlo}, {dhi}] に収まらない。"
                            f" 変換を書いていないので、**正しい値のまま移すだけで"
                            f"Range の外に出る。** Clamp を入れること。"))

        if src not in src_props:
            out.append(Finding(
                "error", f"{cs.name}:{line}", f"移行元に無いプロパティ: {src}",
                f"EasyToon / EasyPBR のどちらにも '{src}' は無い。"
                f" `HasProperty` に守られるので例外は出ず、"
                f"**その 1 行だけ黙って移らない。**"))

        if dst not in dst_props:
            out.append(Finding(
                "error", f"{cs.name}:{line}", f"移行先に無いプロパティ: {dst}",
                f"ToonPBR に '{dst}' は無い。読んだ値の行き先が無く、"
                f"**移行したつもりで消える。**"))

    return out


MASKMAP_GUID_RE = re.compile(
    r"^[ \t]*- _MaskMap:" + chr(10) + r"[ \t]*m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})",
    re.MULTILINE)

# 単一チャンネルの焼き上がりに付く名前。`EasyPbrAoBaker` が `*_AO.png` を出す。
SINGLE_CHANNEL_HINT = re.compile(r"_(AO|Occlusion|Cavity|Curvature)\.(png|tga|exr)$", re.I)


def check_maskmap_packing(materials_dir: Path | None,
                          values: dict[str, float], where: str,
                          guid_to_path: dict[str, str]) -> list[Finding]:
    """**パックされていないテクスチャを `_MaskMap` に入れていないか。**

    ToonPBR の `_MaskMap` は **R=Metallic / G=Occlusion / B=Thickness / A=Smoothness**。
    ところがこのプロジェクトの 30 マテリアルは、**生の AO テクスチャ**
    （`*_AO.png`）をそこに入れている。グレースケールなので

        R = AO → metallic = mask.r * _Metallic
        G = AO → occlusion                       ← ここだけ正しい
        B = AO → thickness（透過の厚み）
        A = 1  → smoothness

    **今は無害。** `_Metallic` が 30 個すべて 0 で、透過も切ってあるから。
    G が偶然そのまま AO なので、遮蔽だけは正しく効いている。

    **`_Metallic` を上げるか透過を入れた瞬間に壊れる。**
    金属度が AO で変調され、窪みだけ非金属になる。

    **実際に効く条件が揃ったときだけ指摘する。**
    今の状態に警告を出すと 30 件出て、他の指摘を埋もれさせる（T-119 / T-167）。
    """
    out: list[Finding] = []
    if materials_dir is None:
        return out

    metallic = values.get("_Metallic", 0.0)
    transmission = values.get("_TransmissionStrength", 0.0)
    if metallic <= 0.0 and transmission <= 0.0:
        return out                      # R も B も読まれない。今は問題にならない

    path = guid_to_path.get(where, "")
    if not path or not SINGLE_CHANNEL_HINT.search(path):
        return out

    used = []
    if metallic > 0.0:
        used.append(f"_Metallic = {metallic}（R を金属度として読む）")
    if transmission > 0.0:
        used.append(f"_TransmissionStrength = {transmission}（B を厚みとして読む）")

    out.append(Finding(
        "error", where, "パックしていないテクスチャを _MaskMap に入れている",
        f"_MaskMap が '{Path(path).name}' を指している。名前からして単一チャンネルの"
        f"焼き上がりで、**R=Metallic / G=Occlusion / B=Thickness / A=Smoothness の"
        f"パック済みマップではない。** グレースケールなので遮蔽（G）だけは"
        f"偶然正しく効くが、{' / '.join(used)} なので**別のチャンネルまで読まれる。**"
        f" 金属度が AO で変調され、窪みだけ非金属になる。"
        f" AO を G に詰めたマップを焼くか、その値を 0 に戻すこと。"))

    return out


# 走査するシェーダーの GUID。`run()` が入れる。**空なら絞らない**
# （単体で呼ばれたときに 0 件になって黙るのを避けるため）。
_SHADER_GUID: str = ""
_MATERIAL_CACHE: dict[str, list[Path]] = {}
_TEXTURE_INDEX: dict[str, Path] | None = None

# 顔の軸（`faceFwd` / `faceRight` / `faceUp` / `fwd` / `right`）は
# 光源に依存しないのは事実だが、外へ出すには顔の SDF ブロックを丸ごと
# 組み替える必要があり、**灯数 1 の構成では利得がゼロ**（T-139）。
# **1 か所で持つこと** ── `run()` と `run_generic()` で別々に書いていたら
# 呼び方によって 4 件出たり出なかったりした。
LIGHT_LOOP_KNOWN_OK = {"faceFwd", "faceRight", "faceUp", "fwd", "right"}


def find_materials(d: Path) -> list[Path]:
    """マテリアルを集める。**拡張子だけで決めない。**

    Unity のマテリアルは普通 `.mat` だが、スクリプトで作ったものは
    `.asset` で保存されていることがある（`AssetDatabase.CreateAsset` の
    引数がそのまま拡張子になるため）。

    実際そうなっていた ── 利用者に「ここを見て」と渡された
    `Assets/AvatarM/ToonPBR_Idol/` の 20 件が全部 `.asset` で、
    検査は**「マテリアルが見つからない」と言って丸ごと素通り**していた。
    パスの打ち間違いにしか見えないので、原因に辿り着かない。

    `.asset` は何にでも使われる拡張子なので、**中身で確かめる**
    （`!u!21` が Unity の Material のクラス ID）。

    **先頭だけを見ないこと。** Unity 6 はキーワード状態を
    `MonoBehaviour`（`!u!114`）のサブアセットとして**先に**書くことがあり、
    `Material:` はその後ろに来る。先頭 400 字で判定していたときは、
    同じフォルダの 46 件中 25 件がその形で、**半分以上を落としていた**
    （たまたま今の `.asset` は Material が先頭にあって表に出なかった）。

    **フォルダ 1 つに決め打ちしないこと。** 以前は `glob`（直下のみ）で、
    `check.py` の既定も 1 キャラのフォルダを決め打ちしていた。
    実際にはこのシェーダーを使うマテリアルは **3 フォルダに 86 件**あり、
    **診断は 46 件しか見ていなかった** ── 利用者が今まさに見ているキャラの
    17 件（サーフェスタイプが Default）は、標準の診断に一度も出ていない。

    代わりにシェーダーの GUID で絞る。再帰的に舐めても、隣のシェーダーの
    マテリアルを巻き込まない。
    """
    key = str(d.resolve())
    cached = _MATERIAL_CACHE.get(key)
    if cached is not None:
        return cached

    out: list[Path] = []
    for f in list(d.rglob("*.mat")) + list(d.rglob("*.asset")):
        try:
            text = f.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if f.suffix == ".asset" and "!u!21 " not in text:
            continue
        if _SHADER_GUID and _SHADER_GUID not in text:
            continue
        out.append(f)
    out = sorted(out, key=lambda f: f.name)
    _MATERIAL_CACHE[key] = out
    return out


def build_maskmap_index(materials_dir: Path | None, assets: Path) -> dict[str, str]:
    """マテリアル名 → `_MaskMap` が指すファイルのパス。

    guid からファイルを引くのに `.meta` を全部走査するので、**1 回だけ作る。**
    マテリアルごとに走査すると 46 × 数千ファイルになる。
    """
    index: dict[str, str] = {}
    if materials_dir is None or not assets.is_dir():
        return index

    want: dict[str, str] = {}
    for mat in find_materials(materials_dir):
        m = MASKMAP_GUID_RE.search(mat.read_text(encoding="utf-8", errors="replace"))
        if m:
            want[m.group(1)] = mat.stem
    if not want:
        return index

    for meta in assets.rglob("*.meta"):
        mm = re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(
            encoding="utf-8", errors="replace"), re.MULTILINE)
        if mm and mm.group(1) in want:
            index[want[mm.group(1)]] = str(meta.with_suffix(""))
    return index


# EasyToon パッケージの設計ルール（Documentation~/ARCHITECTURE.md 末尾）のうち、
# 静的に判定できるもの。**移植先の作法に合わせながら進めるための足場。**
#
# ここを機械で見ておかないと、分割やリネームを進めるうちに
# 「パッケージに入れられない形」へ静かに漂着する。
# 逆に、守れていることが毎回確かめられるなら、restructure を安心して進められる。

# ルール 2 で許すキーワード。**増やすときはここに書く**のが手続き。
# 「安易な追加は禁止」を人の注意力に任せず、追加そのものを明示的な作業にする。
ALLOWED_KEYWORDS = {
    "_ALPHATEST_ON",
    "_HQ_SHADOW_ON",
    "_OUTLINE_ON",
    "_SURFACETYPE_DEFAULT", "_SURFACETYPE_SKIN", "_SURFACETYPE_FACE",
    "_SURFACETYPE_HAIR", "_SURFACETYPE_CLOTH",
}

TEX_DEFAULT_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*\"[^\"]*\"[ \t]*,"
    r"[ \t]*2D[ \t]*\)[ \t]*=[ \t]*\"?(\w*)\"?", re.MULTILINE)

# Unity が用意している既定テクスチャ。これ以外だと未ベイクで何が来るか読めない。
SAFE_TEX_DEFAULTS = {"white", "black", "bump", "gray", "grey", "red", "linearGrey"}


def check_pragma_placement(root: Path) -> list[Finding]:
    """**素の `#include` の中に `#pragma` を置いていないか**（T-216）。

    分割で生まれた、いちばん静かな壊れ方。

    Unity は素の `#include` で取り込んだファイルの `#pragma` を**読まない。**
    つまりキーワードの宣言をそこへ移すと、**そのキーワードは永久に立たない。**
    バリアントが消えるだけなので:

      - コンパイルは通る
      - 絵も出る（キーワード OFF の枝が走る）
      - 「なぜか効かない」としか見えない

    このプロジェクトが最も嫌う形そのもの。CLAUDE.md に「置かないこと」と
    書いてあるが、**文章は守ってくれない。**

    `#include_with_pragmas` で取り込む場合は読まれるので、そちらは対象外。
    どちらで取り込まれているかを実際に見て判定する。
    """
    out: list[Finding] = []

    plain: set[Path] = set()
    with_pragmas: set[Path] = set()
    for f in sorted(root.rglob("*.hlsl")) + sorted(root.rglob("*.shader")):
        text = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r'#include(_with_pragmas)?\s+"([^"]+)"', text):
            target = m.group(2)
            if target.startswith("Packages/"):
                continue
            try:
                p = (f.parent / target).resolve()
            except OSError:
                continue
            (with_pragmas if m.group(1) else plain).add(p)

    for f in sorted(root.rglob("*.hlsl")):
        rp = f.resolve()
        if rp not in plain or rp in with_pragmas:
            continue
        text = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r"^[ \t]*#pragma[ \t]+(\S+)", text, re.MULTILINE):
            line = text[:m.start()].count("\n") + 1
            out.append(Finding(
                "error", f"{f.relative_to(root)}:{line}".replace("\\", "/"),
                f"素の include の中に #pragma がある: {m.group(1)}",
                "Unity は素の `#include` の中の pragma を**読まない。**"
                " キーワードの宣言なら**永久に立たなくなる** ── "
                "コンパイルは通り絵も出るので、実機で「なぜか効かない」としか見えない。"
                " pragma は `.shader` 側へ戻すこと"
                "（どうしても include 側に置くなら `#include_with_pragmas`）。"))
            break     # 1 ファイル 1 件で十分

    return out


def check_orphan_includes(root: Path) -> list[Finding]:
    """**どこからも include されていない HLSL** を見つける（T-213）。

    分割で生まれた危険。`Shading/` や `Passes/` に置いたファイルは、
    `#include` を書き忘れれば**ただ存在するだけ**になる。

    HLSL はそれを教えてくれない。**コンパイルは通り、絵も出る**
    ── そこに書いた関数が「呼ばれていない」だけなので。
    切り出しの途中で 1 本落としても、影が薄くなったとしか見えない。

    このプロジェクトが何度も踏んだ「実装したのに効いていない」の、
    分割によって新しく開いた入口。
    """
    out: list[Finding] = []

    included: set[Path] = set()
    for f in sorted(root.rglob("*.hlsl")) + sorted(root.rglob("*.shader")):
        text = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r'#include\s+"([^"]+)"', text):
            target = m.group(1)
            if target.startswith("Packages/"):
                continue
            p = (f.parent / target)
            try:
                included.add(p.resolve())
            except OSError:
                pass

    for f in sorted(root.rglob("*.hlsl")):
        if f.resolve() in included:
            continue
        out.append(Finding(
            "error", str(f.relative_to(root)).replace("\\", "/"),
            "どこからも include されていない",
            "分割で置いたファイルが繋がっていない。**コンパイルは通り、絵も出る** ── "
            "そこに書いた関数が呼ばれないだけなので、影が薄い程度にしか見えない。"
            " include を書き忘れていないか確認すること。"))

    return out


def _check_asmdef_deps(root: Path) -> list[Finding]:
    """他パッケージのアセンブリを参照する asmdef が、**不在時に守られているか。**

    **`dependencies` に書けばいい、ではない。** 最初そう書いて誤検出を出した ──
    `EasyShaderCoreInstaller.cs` の冒頭に理由が書いてある:

        package.json の dependencies に Core を書かない。書くと UPM が
        レジストリ解決に失敗し、本パッケージの git URL インストール自体が拒否される。

    Core は git URL で配っているので、名前とバージョンでは解決できない。
    **宣言しないのが正しい。** 代わりに次の 3 つで成立させている:

      1. Editor asmdef に `versionDefines`（Core があれば EASYSHADERCORE_PRESENT）
      2. 同じ asmdef に `defineConstraints`（そのシンボルが無ければ**丸ごと除外**）
      3. 参照ゼロの独立した Installer asmdef が `InitializeOnLoadMethod` で自動導入

    **2 が肝で、抜けると 3 が動かない。** Core 不在でコンパイルエラーになると
    Unity はドメインリロードを完了できず、`InitializeOnLoadMethod` が走らない
    ── つまり**自動インストーラが永久に起動しない。** 絵ではなく
    「入れた直後だけ動かない」という形で出るので、原因に辿り着きにくい。

    そこでこの検査が見るのは「宣言の有無」ではなく**守りの有無**にした。
    """
    out: list[Finding] = []

    pkg_root = next((p for p in root.resolve().parents
                     if (p / "package.json").exists()), None)
    if pkg_root is None:
        return out
    packages_dir = pkg_root.parent
    if packages_dir.name != "Packages":
        return out

    try:
        me = json.loads((pkg_root / "package.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return out
    my_name = me.get("name", pkg_root.name)
    declared = set((me.get("dependencies") or {}).keys())

    # アセンブリ名 -> それを定義しているパッケージ
    owner: dict[str, str] = {}
    for other in sorted(packages_dir.glob("*/package.json")):
        try:
            name = json.loads(other.read_text(encoding="utf-8")).get("name")
        except (OSError, ValueError):
            continue
        if not name:
            continue
        for asm in other.parent.rglob("*.asmdef"):
            try:
                owner[json.loads(asm.read_text(encoding="utf-8"))["name"]] = name
            except (OSError, ValueError, KeyError):
                continue

    guid_refs = 0
    missing: dict[str, set[str]] = {}
    for asm in sorted(pkg_root.rglob("*.asmdef")):
        try:
            data = json.loads(asm.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        # この asmdef が「不在で除外される」形になっているか。
        # versionDefines がパッケージ P を見て定義するシンボルが、
        # そのまま defineConstraints に入っていれば守られている。
        constraints = set(data.get("defineConstraints") or [])
        guarded = {vd.get("name") for vd in (data.get("versionDefines") or [])
                   if vd.get("define") in constraints}

        for ref in data.get("references") or []:
            if ref.startswith("GUID:"):
                guid_refs += 1
                continue
            pkg = owner.get(ref)
            if pkg is None or pkg == my_name or pkg in declared or pkg in guarded:
                continue
            missing.setdefault(pkg, set()).add(f"{data.get('name')} → {ref}")

    for pkg, refs in sorted(missing.items()):
        out.append(Finding(
            "error", "設計ルール 4", f"不在時に守られていない参照: {pkg}",
            f"{', '.join(sorted(refs))}。'{pkg}' を参照しているのに"
            f" dependencies にも無く、versionDefines + defineConstraints でも"
            f" 守られていない。**{pkg} が無いプロジェクトではコンパイルエラーになる。**"
            f" エラーがあると Unity はドメインリロードを完了できず、"
            f"`InitializeOnLoadMethod` の自動インストーラが**走らない** ── "
            f"入れた直後だけ動かない、という原因の見えない形で出る。"
            f" git URL 配布のパッケージは dependencies に書けない"
            f"（UPM がレジストリ解決に失敗してインストール自体が拒否される）ので、"
            f" versionDefines で定義したシンボルを defineConstraints に入れること。"))

    if guid_refs:
        # **黙って飛ばさない。** GUID 参照は名前で解決できないので未検査。
        out.append(Finding(
            "warning", "設計ルール 4", "GUID で書かれた asmdef 参照は未検査",
            f"{guid_refs} 件。名前で解決できないのでこの検査の対象外。"
            f" 依存の宣言漏れがあっても見つけられない。"))

    return out


def check_package_rules(root: Path) -> list[Finding]:
    """EasyToon パッケージへ入れるための設計ルールを見る（T-206）。

    6 つのうち静的に判定できる 4 つ:

      2. キーワードは決めたものだけ
      4. EasyPBR の `Doll/` を include しない
      5. 未ベイク・既定値で全機能が安全にスキップされる（2D の既定値が明示されている）
      6. RendererFeature は Render Graph API で書く

    1（CBUFFER 単一）は E005 が、3（Core の純粋性）は Core を使い始めてから。
    """
    out: list[Finding] = []

    shaders = sorted(root.rglob("*.shader"))
    hlsls = sorted(root.rglob("*.hlsl"))
    if not shaders:
        return out

    # --- ルール 2 -------------------------------------------------------
    used: set[str] = set()
    for f in shaders + hlsls:
        text = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r"#pragma\s+shader_feature\w*\s+(.+)", text):
            used |= {t for t in m.group(1).split() if t.startswith("_")}

    for kw in sorted(used - ALLOWED_KEYWORDS):
        out.append(Finding(
            "error", "設計ルール 2", f"許可していないキーワード: {kw}",
            f"EasyToon は「キーワードは表のものだけ。shader_feature の安易な追加は禁止」"
            f"（ARCHITECTURE.md）。**バリアントは増える一方**で、"
            f"入れたあとで減らすのは難しい。"
            f" 本当に要るなら param_check の ALLOWED_KEYWORDS に足してから使うこと"
            f" ── 追加を明示的な作業にするための手続き。"))

    # --- ルール 4 -------------------------------------------------------
    for f in shaders + hlsls:
        text = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r'#include\s+"([^"]*Doll/[^"]*)"', text):
            out.append(Finding(
                "error", f.name, "EasyPBR の Doll/ を include している",
                f"'{m.group(1)}'。パッケージ間の依存を作ると、"
                f"EasyToon 単体で配れなくなる（ARCHITECTURE.md の設計ルール 4）。"))

    # --- ルール 4（asmdef 側）-------------------------------------------
    # **`#include` だけがパッケージ間の依存ではない。** asmdef の参照も同じで、
    # しかもこちらは**宣言し忘れても同じプロジェクト内では動いてしまう。**
    # `Packages/` に並べて置いてあれば Unity が見つけるので、
    # **単体で配った瞬間に Editor アセンブリがコンパイルできなくなる**まで気付けない。
    #
    # 判定は「兄弟パッケージが定義しているアセンブリを参照しているのに、
    # package.json の dependencies にそのパッケージが無い」。
    # Unity 自身のアセンブリは URP の依存経由で入るので対象外。
    out += _check_asmdef_deps(root)

    # --- ルール 5 -------------------------------------------------------
    for f in shaders:
        text = f.read_text(encoding="utf-8", errors="replace")
        try:
            blk = text[text.index("Properties"):text.index("SubShader")]
        except ValueError:
            continue
        for m in TEX_DEFAULT_RE.finditer(blk):
            if m.group(2) in SAFE_TEX_DEFAULTS:
                continue
            out.append(Finding(
                "error", f.name, f"テクスチャの既定値が不明: {m.group(1)}",
                f"= {m.group(2)!r}。**未ベイクで何が来るか読めない。**"
                f" EasyToon は「未ベイク・既定値で全機能が安全にスキップされること必須」"
                f"（設計ルール 5）。white / bump / black などを明示すること。"))

    # --- ルール 6 -------------------------------------------------------
    for cs in sorted(root.glob("Runtime/**/*.cs")):
        text = cs.read_text(encoding="utf-8", errors="replace")
        if "ScriptableRendererFeature" not in text:
            continue
        if "RecordRenderGraph" in text:
            continue
        out.append(Finding(
            "error", cs.name, "RendererFeature が Render Graph API で書かれていない",
            "URP 17 では Render Graph が既定で、旧 `Execute` だけの実装は"
            "**Compatibility Mode を切ると黙って何も描かなくなる。**"
            "（設計ルール 6）"))

    return out


AA_NAMES = {0: "なし", 1: "FXAA", 2: "SMAA", 3: "TAA"}


_COMP_NAME = {1: "Never", 2: "Less", 3: "Equal", 4: "LEqual",
              5: "Greater", 6: "NotEqual", 7: "GEqual", 8: "Always"}
_OP_REPLACE = 2


def check_stencil_reachability(materials_dir: Path | None) -> list[Finding]:
    """**1 画素も描かれない描画設定**と、**相手が居ないステンシル**を見つける。

    `_ZTest` と `_StencilComp` は**どの検査も見ていなかった。**
    どちらも `Never`（値 1）にすると、その材質は完全に消える。
    Unity は何も言わないので、絵を見て「なぜか出ない」としか分からない。

    **ステンシルは 1 枚のマテリアルだけでは判定できない。**
    前髪透過は「眉・目がビットを書き、髪がそこを抜く」という 3 者の取り決めで、
    片側だけ設定しても**何も起きないまま静かに成立しない。**
    実際そのまま出荷され、指摘されるまで気付かなかった（T-254）。
    GUI は「3 つすべてに設定して初めて機能します」と書いているが、
    **文章はチェックリストにならない。** ここで実際に突き合わせる。

    見るもの:
      1. `_ZTest` / `_StencilComp` が Never → 1 画素も描かれない
      2. `Equal` で `ref & readMask != 0` なのに、その値を書く材質が居ない
      3. 抜く側（`Equal` / `ref & readMask == 0` / readMask != 0）が居るのに
         書く側が居ない ── **穴を空けて誰も埋めない状態**
      4. 書く側の Queue が抜く側より後 ── 先に書かれていないと抜けない
      5. `Replace` なのに `_StencilWriteMask == 0` → 何も書かない
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    mats = find_materials(materials_dir)
    if not mats:
        return out

    def num(text: str, key: str, default: float) -> float:
        m = re.search(rf"^[ \t]*- {re.escape(key)}: ([-\d.eE+]+)[ \t]*$",
                      text, re.MULTILINE)
        return float(m.group(1)) if m else default

    info = []
    for path in mats:
        t = path.read_text(encoding="utf-8", errors="replace")
        q = re.search(r"m_CustomRenderQueue: (-?\d+)", t)
        info.append({
            "name": path.stem,
            "ztest": int(num(t, "_ZTest", 4)),
            "comp": int(num(t, "_StencilComp", 8)),
            "op": int(num(t, "_StencilPass", 0)),
            "ref": int(num(t, "_StencilRef", 0)),
            "read": int(num(t, "_StencilReadMask", 15)),
            "write": int(num(t, "_StencilWriteMask", 15)),
            "queue": int(q.group(1)) if q else 2000,
        })

    # (1) 1 画素も描かれない
    for m in info:
        for key, label in (("ztest", "_ZTest"), ("comp", "_StencilComp")):
            if m[key] == 1:
                out.append(Finding(
                    "error", m["name"], "1画素も描かれない描画設定",
                    f"{label} = Never。この材質は**完全に消える。**"
                    f" Unity は何も言わないので絵を見るまで分からない。"))

    # (5) 書くつもりで何も書いていない
    for m in info:
        if m["op"] == _OP_REPLACE and m["write"] == 0:
            out.append(Finding(
                "error", m["name"], "Replace なのに書き込みマスクが 0",
                "_StencilPass = Replace だが _StencilWriteMask = 0 なので"
                " **ステンシルに何も書かない。** これを当てにしている材質は"
                " 黙って成立しなくなる。"))

    writers = [m for m in info if m["op"] == _OP_REPLACE and m["write"] != 0]

    for m in info:
        if m["comp"] != 3:                       # Equal 以外は相手を要らない
            continue
        want = m["ref"] & m["read"]
        if m["read"] == 0:
            continue                             # 常に真。別の意味で使っている

        if want != 0:
            # (2) その値を書ける材質が居るか
            ok = [w for w in writers
                  if (w["ref"] & w["write"] & m["read"]) == want]
            if not ok:
                out.append(Finding(
                    "error", m["name"], "ステンシルの相手が居ない",
                    f"Comp = Equal / Ref {m['ref']} / ReadMask {m['read']} なので"
                    f" ビット {want} が書かれた画素にしか描かれないが、"
                    f"**それを書く材質が 1 つも無い。** この材質は出ない。"))
                continue
            src = ok
        else:
            # (3) 抜く側。書く側が居ないと穴を空けて誰も埋めない
            src = [w for w in writers if (w["write"] & m["read"]) != 0]
            if not src:
                out.append(Finding(
                    "error", m["name"], "抜く相手が居ない（前髪透過が成立しない）",
                    f"Comp = Equal / Ref 0 / ReadMask {m['read']} で"
                    f"「そのビットが立っていない画素だけ描く」設定だが、"
                    f"**ビットを書く材質（眉・目）が 1 つも無い。**"
                    f" 全面で条件が成立するので**何も起きない** ── "
                    f"設定した本人には効いているのか判断できない（T-254）。"))
                continue

        # (4) 順序。先に書かれていないと読めない
        latest = max(w["queue"] for w in src)
        if m["queue"] <= latest:
            out.append(Finding(
                "error", m["name"], "ステンシルを読む側が先に描かれる",
                f"この材質の Queue {m['queue']} に対し、ビットを書く材質は"
                f" 最大 {latest}。**まだ書かれていないものを読む**ので、"
                f"設定は正しく見えても効かない。Queue を {latest + 1} 以上にすること。"))

    return out


# マテリアル側のトグル -> それを描くために要る Renderer Feature（C# のファイル名）
def check_renderer_feature_parity(root: Path) -> list[Finding]:
    """レンダラごとに **Renderer Feature の顔ぶれが違う**状態を見つける。

    `check_feature_installed` はマテリアルのトグルを起点にするので、
    **トグルを持たない機能は見られない。** Cel の `CelOutline` /
    `CelCharShadow` がそれで、パスは在るがプロパティの門が無い。

    出荷しているレンダラが 2 つある以上（Forward の Mobile と Forward+ の PC）、
    片方にしか入っていない Feature は**そちらで品質レベルを切り替えた瞬間に
    だけ絵が変わる。** マテリアルを見ても原因に辿り着けない類。

    実際そうなっている ── PC に 5 つ、Mobile に 0 個。

    **差だけを言う。** どちらにも入っていない Feature は「使っていない」
    だけかもしれないので触らない（それは `check_feature_installed` が
    トグルと突き合わせて判断する）。
    """
    out: list[Finding] = []
    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return out

    # Feature の GUID → クラス名。`.meta` は `.cs` の隣にある。
    guid_to_class: dict[str, str] = {}
    for cs in (project / "Packages").rglob("*.cs"):
        try:
            if "ScriptableRendererFeature" not in cs.read_text(
                    encoding="utf-8", errors="replace"):
                continue
        except OSError:
            continue
        meta = cs.with_suffix(cs.suffix + ".meta")
        if not meta.exists():
            continue
        m = re.search(r"^guid: ([0-9a-f]{32})",
                      meta.read_text(encoding="utf-8", errors="replace"), re.MULTILINE)
        if m:
            guid_to_class[m.group(1)] = cs.stem
    if not guid_to_class:
        return out

    installed: dict[str, set[str]] = {}
    for a in sorted((project / "Assets").rglob("*.asset")):
        try:
            t = a.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if not re.search(r"^\s*m_RenderingMode: \d+", t, re.MULTILINE):
            continue
        installed[a.name] = {c for g, c in guid_to_class.items() if g in t}
    if len(installed) < 2:
        return out                      # 比べる相手が無い

    everywhere = set.intersection(*installed.values())
    anywhere = set.union(*installed.values())
    partial = sorted(anywhere - everywhere)
    if not partial:
        return out

    where = "／".join(
        f"{n}: {len(s)} 個" for n, s in sorted(installed.items()))
    lacking = {n: sorted(f for f in partial if f not in s)
               for n, s in sorted(installed.items())}
    detail = "／".join(f"{n} に無い: {', '.join(v)}"
                       for n, v in lacking.items() if v)
    out.append(Finding(
        "warn", "Renderer Feature", "レンダラで Feature の顔ぶれが違う",
        f"{where}。{detail}。"
        f" **そのレンダラでは該当のパスが一度も描かれない。**"
        f" 品質レベルを切り替えたときにだけ絵が変わるので、"
        f"マテリアルを見ても原因に辿り着けない。"
        f" トグルを持たない機能（Cel の輪郭・キャラ影）は"
        f"マテリアル側からは判定できないため、ここで見る。"
        f" そのレンダラを出荷しないなら放置してよい。"))
    return out


FEATURE_REQUIRED = {
    "_OutlineOn":    ("ToonOutlineFeature",
                      "輪郭は独自 LightMode \"IdolOutline\" にあり、URP は既定で描かない"),
}


def check_feature_installed(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**Feature が要る機能を ON にしたのに、Feature が入っていない**状態を見つける。

    独自 LightMode のパスは URP が既定で描かない。トグルを ON にすると
    キーワードは立ち、シェーダーもコンパイルされるが、**描く人が居ない。**
    絵は「機能を入れる前」とまったく同じになるので、
    マテリアル側の設定をいくら見直しても原因に辿り着けない。

    実際この状態になっていた ── `_OutlineOn` が 46 マテリアルすべてで 1 なのに、
    `ToonOutlineFeature` はどの Renderer Data にも入っていなかった（T-281）。

    **Unity 側の診断（ToonPBRSetupCheck）は同じことを見ている**が、
    あちらはメニューから手で回すもの。`check.py` を回す運用では届かない。

    判定は Feature の C# の GUID を `.asset` から探す形。
    クラス名の文字列ではなく GUID を見るのは、**名前を変えても追随する**ため。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return out

    # 使われているトグルを集める
    used: dict[str, int] = {}
    req = dict(FEATURE_REQUIRED)
    # 前髪透過（T-341）はトグルでなく**ステンシル設定 + パス有効**で判定する。
    # C# 側の UsesSeeThrough と同じ条件: _StencilComp = Equal(3) / _StencilReadMask = 6。
    # パス停止は m_DisabledShaderPasses に LightMode タグ名で記録される。
    SEETHROUGH_KEY = "(前髪透過のステンシル設定)"
    req[SEETHROUGH_KEY] = ("HairSeeThroughFeature",
                           "前髪透過は独自 LightMode \"IdolHairSeeThrough\" にあり、URP は既定で描かない")
    mats = find_materials(materials_dir)
    for f in mats:
        t = f.read_text(encoding="utf-8", errors="replace")
        for prop in FEATURE_REQUIRED:
            m = re.search(rf"^[ \t]*- {prop}: ([-\d.eE+]+)[ \t]*$", t, re.MULTILINE)
            if m and float(m.group(1)) > 0.5:
                used[prop] = used.get(prop, 0) + 1
        comp = re.search(r"^[ \t]*- _StencilComp: ([-\d.eE+]+)[ \t]*$", t, re.MULTILINE)
        mask = re.search(r"^[ \t]*- _StencilReadMask: ([-\d.eE+]+)[ \t]*$", t, re.MULTILINE)
        if (comp and abs(float(comp.group(1)) - 3.0) < 0.5
                and mask and abs(float(mask.group(1)) - 6.0) < 0.5
                and not re.search(r"^[ \t]*- IdolHairSeeThrough[ \t]*$", t, re.MULTILINE)):
            used[SEETHROUGH_KEY] = used.get(SEETHROUGH_KEY, 0) + 1
    if not used:
        return out

    # Feature の C# を探して GUID を引く
    scripts = {p.stem: p for p in (root.resolve().parents[0]).rglob("*Feature.cs")}
    for parent in here.parents:
        if (parent / "package.json").exists():
            scripts.update({p.stem: p for p in parent.rglob("*Feature.cs")})
            break

    asset_text: str | None = None
    renderers: dict[str, str] = {}      # レンダラの .asset 名 -> 中身

    for prop, count in sorted(used.items()):
        feature, why = req[prop]
        src = scripts.get(feature)
        if src is None:
            out.append(Finding(
                "warning", "Renderer Feature", f"{feature} のソースが見つからない",
                f"{prop} が {count} 件で有効だが、{feature}.cs を探せなかったので"
                f" 導入されているか判定できない。**未検査。**"))
            continue

        meta = src.with_suffix(src.suffix + ".meta")
        m = re.search(r"^guid: ([0-9a-f]{32})",
                      meta.read_text(encoding="utf-8", errors="replace"), re.MULTILINE) \
            if meta.exists() else None
        if m is None:
            out.append(Finding(
                "warning", "Renderer Feature", f"{feature} の .meta を読めない",
                f"GUID が取れないので導入されているか判定できない。**未検査。**"))
            continue

        guid = m.group(1)
        if asset_text is None:
            # `.asset` を 1 度だけ全部読んで連結する（数百ファイルなので許容範囲）
            chunks = []
            for a in (project / "Assets").rglob("*.asset"):
                try:
                    text = a.read_text(encoding="utf-8", errors="replace")
                except OSError:
                    continue
                chunks.append(text)
                # **レンダラ単位でも持つ。** 連結して「どこかに在る」で通すと、
                # **片方のレンダラにだけ入っている**状態が無言で抜ける。
                # 実際そうなっていた ── PC_Renderer に Feature が 5 つ、
                # Mobile_Renderer は 0 個。Feature はレンダラの子アセットとして
                # 同じファイルに書かれるので、ファイル単位で判定できる。
                if re.search(r"^\s*m_RenderingMode: \d+", text, re.MULTILINE):
                    renderers[a.name] = text
            asset_text = "\n".join(chunks)

        # **どのレンダラに入っているか**まで見る。導入済みでも、出荷している
        # もう一方に無ければ、そちらでは**一度も描かれない**。
        if renderers:
            has = [n for n, t in renderers.items() if guid in t]
            lacks = [n for n in renderers if n not in has]
            if has and lacks:
                out.append(Finding(
                    "warn", "Renderer Feature",
                    f"{feature} が一部のレンダラにしか入っていない",
                    f"{', '.join(sorted(has))} には在るが"
                    f" **{', '.join(sorted(lacks))} には無い。**"
                    f" {prop} を有効にしたマテリアルが {count} 件あるので、"
                    f"そのレンダラで描いたときだけ**この機能は何も描かない**"
                    f" ── 品質レベルを切り替えたときにだけ絵が変わる形になり、"
                    f"マテリアルを見ても原因に辿り着けない。"
                    f" そのレンダラを出荷しないなら放置してよい。"))
                continue

        if guid in asset_text:
            continue

        out.append(Finding(
            "error", "Renderer Feature", f"{feature} が導入されていない",
            f"{prop} を有効にしたマテリアルが {count} / {len(mats)} 件あるのに、"
            f" **{feature} がどの Renderer Data にも入っていない。** {why}ので、"
            f"この機能は**何も描かない** ── 絵は入れる前とまったく同じになる。"
            f" マテリアル側をいくら見直しても原因に辿り着けない類。"
            f" URP の Renderer Data の Add Renderer Feature から追加すること。"
            # **どちらに倒すかの判断材料まで書く。**
            # 「入れろ」しか書いていないと、使わないと決めた人には
            # 消せない赤が残り続け、やがて診断ごと読まれなくなる。
            f" **使わないと決めたなら {prop} を 0 にしてよい。**"
            f" Feature が無い間このパスは**一度も描かれない**ので、"
            f"1 のままでも実行時のコストは無い ── どちらを選んでも"
            f"**絵も速度も変わらない。**変わるのは「入れたときに描かれるか」だけ。"))

    return out


def _texture_index(project: Path) -> dict[str, Path]:
    """guid → テクスチャのファイル。**1 回だけ作って使い回す。**"""
    global _TEXTURE_INDEX
    if _TEXTURE_INDEX is not None:
        return _TEXTURE_INDEX
    index: dict[str, Path] = {}
    for meta in (project / "Assets").rglob("*.meta"):
        src = meta.with_suffix("")
        if src.suffix.lower() not in (".png", ".tga", ".jpg", ".jpeg", ".psd", ".tif", ".tiff"):
            continue
        try:
            m = re.search(r"^guid: ([0-9a-f]{32})",
                          meta.read_text(encoding="utf-8", errors="replace"), re.M)
        except OSError:
            continue
        if m:
            index[m.group(1)] = src
    _TEXTURE_INDEX = index
    return index


def _has_alpha_channel(p: Path) -> bool | None:
    """画像にアルファの器があるか。判定できなければ None。"""
    s = p.suffix.lower()
    try:
        head = p.read_bytes()[:64]
    except OSError:
        return None
    if s == ".png":
        if head[:8] != b"\x89PNG\r\n\x1a\n":
            return None
        return head[25] in (4, 6)          # 4=Gray+A / 6=RGBA
    if s == ".tga":
        # 17 バイト目が画素深度、18 バイト目の下位 4bit がアルファのビット数
        return (head[17] & 0x0F) > 0 or head[16] == 32
    if s in (".jpg", ".jpeg"):
        return False                       # JPEG にアルファは無い
    return None


def check_alpha_clip_without_alpha(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**アルファが無いのにクリップしている**材質を見つける（T-307）。

    `clip()` を持つシェーダーは**早期 Z を使えない。** 画素が生きるか
    死ぬかがフラグメントを走らせるまで決まらないので、深度を先に書けない。
    深度プリパスと影のパスも、単純な頂点処理では済まなくなる。

    ところが `_BaseMap` にアルファの器が無ければ、Unity はサンプル結果の
    アルファを **1.0** で返す ── `clip(1.0 - _Cutoff)` は**絶対に発火しない。**
    払っているだけで、1 画素も落ちない。

    実測: 3 体のうち 1 体（46 件）で **40 件**がこの状態だった
    （BaseMap がすべて 24bit の TGA）。他の 2 体は全件アルファ付きで正しい。

    **`_BaseColor.a` が `_Cutoff` を下回るときは何も言わない** ──
    そちらは「全画素が消える」別の欠陥で、専用の検査が持っている。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out
    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return out
    index = _texture_index(project)
    if not index:
        return out

    groups: dict[Path, list[Path]] = {}
    for m in find_materials(materials_dir):
        groups.setdefault(m.parent, []).append(m)

    for folder, mats in sorted(groups.items()):
        bad: list[str] = []
        for f in mats:
            t = f.read_text(encoding="utf-8", errors="replace")
            if not re.search(r"^[ \t]*- _ALPHATEST_ON[ \t]*$", t, re.M):
                continue
            g = re.search(r"- _BaseMap:\s*\n\s*m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})", t)
            if not g:
                continue                    # 未割り当ては別の検査が持っている
            p = index.get(g.group(1))
            if p is None or not p.exists() or _has_alpha_channel(p) is not False:
                continue
            # インポータが生成しているなら器の有無は関係ない
            meta = p.with_suffix(p.suffix + ".meta")
            if meta.exists() and re.search(r"alphaSource: [12]",
                                           meta.read_text(encoding="utf-8", errors="replace")):
                continue
            # 全画素が消える側は別の欠陥。ここでは扱わない
            ca = re.search(r"- _BaseColor: \{r: [-\d.eE+]+, g: [-\d.eE+]+, "
                           r"b: [-\d.eE+]+, a: ([-\d.eE+]+)\}", t)
            cut = re.search(r"^[ \t]*- _Cutoff: ([-\d.eE+]+)[ \t]*$", t, re.M)
            if ca and cut and float(ca.group(1)) < float(cut.group(1)):
                continue
            bad.append(f.stem)

        if bad:
            out.append(Finding(
                "warning", f"{folder.name}（{len(bad)} / {len(mats)} 件）",
                "アルファが無いのにクリップしている",
                f"`{folder.name}` の {len(bad)} 件。`_BaseMap` にアルファの器が無いので、"
                f"サンプル結果は常に 1.0 ── **`clip()` は 1 画素も落とさない。**"
                f" それでも `clip()` を持つシェーダーは**早期 Z を使えない**"
                f"（生死がフラグメントを走らせるまで決まらないため）。"
                f" 深度プリパスと影のパスも単純な処理では済まなくなる。"
                f" **払っているだけ**なので、Alpha Clip を切ってよい。"
                f" 例: {bad[0]}"))
    return out


def check_leftover_properties(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**前のシェーダーの遺物**がマテリアルに残っていないか（T-308）。

    シェーダーを差し替えても、Unity は**古いプロパティを消さない。**
    `.mat` に値が残り続け、新しいシェーダーは一度も読まない。

    実害は 2 つ:

      1. ファイルとメモリが膨らむ ── 1 材質あたり 150 種類を超えると効いてくる
      2. **診断を誤らせる。** 実際この検査を書く直前、`_SrcBlend: 5 /
         _DstBlend: 10` を見て「5 件が半透明になっている」と読み違えた。
         Idol は**その 2 つをどこでも読んでいない**（移行元の遺物）。
         人が `.mat` を覗いたときも同じ誤読をする。

    **消さなくても絵は変わらない。** 古いシェーダーへ戻す可能性があるなら
    残しておく判断も正しいので、警告に留める。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out
    shader = find_main_shader(root)
    if shader is None:
        return out
    text = shader.read_text(encoding="utf-8", errors="replace")
    try:
        i = text.index("Properties")
        j = text.index("{", i)
    except ValueError:
        return out
    depth, k = 0, j
    while k < len(text):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                break
        k += 1
    props = set(re.findall(r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\(",
                           text[j + 1:k], re.M))
    if not props:
        return out

    groups: dict[Path, list[Path]] = {}
    for m in find_materials(materials_dir):
        groups.setdefault(m.parent, []).append(m)

    for folder, mats in sorted(groups.items()):
        kinds: set[str] = set()
        total = 0
        for f in mats:
            t = f.read_text(encoding="utf-8", errors="replace")
            have = set(re.findall(r"^[ \t]*- (_\w+):", t, re.M))
            # `_ST` / `_TexelSize` などは Unity がテクスチャに付ける従属物
            left = {x for x in have - props
                    if not x.endswith(("_ST", "_TexelSize", "_HDR", "_MipInfo"))}
            kinds |= left
            total += len(left)
        # 数種類なら普通の残りかす。桁が違うときだけ言う
        if len(kinds) < 40:
            continue
        out.append(Finding(
            "warning", f"{folder.name}（{len(mats)} 件）",
            "前のシェーダーのプロパティが残っている",
            f"`{folder.name}` に **{len(kinds)} 種類**（延べ {total} 件）。"
            f" シェーダーを差し替えても Unity は古いプロパティを消さないので、"
            f"Idol が**一度も読まない値**が `.mat` に残り続けている。"
            f" **絵は変わらない**が、ファイルとメモリが膨らむうえ、"
            f"**診断を誤らせる** ── 例えば `_SrcBlend` / `_DstBlend` が残っていると"
            f"「半透明になっている」と読めてしまうが、Idol はその 2 つを読んでいない。"
            f" 古いシェーダーへ戻す可能性があるなら、残す判断も正しい。"))
    return out


def check_pass_keyword_use(root: Path) -> list[Finding]:
    """**そのパスで使わないキーワードを宣言していないか**（T-306）。

    `shader_feature` は宣言したパスの変種を 2 倍（列挙なら値の数だけ）にする。
    **使っていないパスに書くと、中身が同一のバリアントが倍に増えるだけ。**
    ビルド時間とシェーダーの容量に効き、絵は 1 ピクセルも変わらないので、
    数えるまで気付けない。

    W109 は「シェーダー全体でどこにも現れないキーワード」を見るが、
    **パスごとの過不足は見ていない** ── 別のパスで使っていれば通ってしまう。

    include を辿って、そのパスから実際に読めるコードだけを見る。
    `HairSeeThrough` は `_HQ_SHADOW_ON` を宣言するが、これは
    `ForwardPass.hlsl` を読んでいるので正しい（そこで使っている）。
    """
    out: list[Finding] = []
    shader = find_main_shader(root)
    if shader is None:
        return out
    text = shader.read_text(encoding="utf-8", errors="replace")

    def resolve(target: str, base: Path) -> Path | None:
        p = (base.parent / target).resolve()
        return p if p.is_file() else None

    def reachable(src: str, base: Path, seen: set[Path]) -> str:
        body = src
        for m in re.finditer(r'#include(?:_with_pragmas)?\s+"([^"]+)"', src):
            f = resolve(m.group(1), base)
            if f is None or f in seen:
                continue
            seen.add(f)
            t = f.read_text(encoding="utf-8", errors="replace")
            body += "\n" + reachable(t, f, seen)
        return body

    for m in re.finditer(r'Name\s+"(\w+)"', text):
        s = text.find("HLSLPROGRAM", m.end())
        e = text.find("ENDHLSL", s)
        if s < 0 or e < 0:
            continue
        prog = text[s:e]
        # **1 行に複数書いてあるものは「列挙」で、1 本の軸。**
        # `_SURFACETYPE_DEFAULT _SURFACETYPE_SKIN …` の DEFAULT は
        # 「どれでもない」状態を表すだけで、**テストされないのが正しい。**
        # 個別に見ると誤検出になる（最初これで 2 件出た）。
        groups: list[list[str]] = []
        for d in re.finditer(r"^\s*#\s*pragma\s+shader_feature\w*\s+(.*)$", prog, re.M):
            toks = [t for t in d.group(1).split() if t.startswith("_") and len(t) > 1]
            if toks:
                groups.append(toks)
        if not groups:
            continue

        code = reachable(prog, shader, set())

        def used(k: str) -> bool:
            return bool(re.search(rf"defined\s*\(\s*{k}\s*\)|#\s*ifdef\s+{k}\b", code))

        unused = sorted(g[0] if len(g) > 1 else g[0]
                        for g in groups if not any(used(k) for k in g))
        if unused:
            out.append(Finding(
                "warning", f"{shader.name}: {m.group(1)}",
                "使わないキーワードをパスに宣言している",
                f"{' / '.join(unused)} は、このパスから読めるコードのどこにも出てこない。"
                f" **中身が同一のバリアントが倍に増えるだけ**で、"
                f"絵は 1 ピクセルも変わらない ── ビルド時間と容量にだけ効くので、"
                f"数えるまで気付けない。"))
    return out


def shader_guid(root: Path) -> str:
    """このツリーの主シェーダーの GUID。マテリアルを絞るのに使う。

    **プロパティの有無で判別しないこと。** シェーダーを差し替えても
    Unity は古いプロパティを消さないので、1 材質あたり 150〜240 種類の
    遺物が残っている（T-308）。名前で判別すると**使っていない材質**を
    読んでしまう ── 実際それで誤検出を 1 件出した（T-325）。
    """
    shader = find_main_shader(root)
    if shader is None:
        return ""
    meta = shader.with_suffix(shader.suffix + ".meta")
    if not meta.exists():
        return ""
    m = re.search(r"^guid: ([0-9a-f]{32})",
                  meta.read_text(encoding="utf-8", errors="replace"), re.M)
    return m.group(1) if m else ""


def check_range_pairs(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**対になった値が逆転していないか**（T-326）。

    `_XxxMin` / `_XxxMax`、`_XxxStart` / `_XxxEnd` は
    「この範囲で効く」という意味なので、**逆転すると機能が丸ごと死ぬか
    常時発動する**。どちらも絵の説明が付かない形なので迷わず出せる。

    **一覧は書き写さない。** シェーダーの Properties から名前の対で拾う
    ── プロパティが増減しても勝手に追従する。

    `Min == Max` は見ない。帯の幅がゼロになるだけで、
    「ここで硬く切る」という意図があり得る。**逆転だけ**が説明の付かない形。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out
    guid = shader_guid(root)
    if not guid:
        return out

    shader = find_main_shader(root)
    text = shader.read_text(encoding="utf-8", errors="replace")
    try:
        i = text.index("Properties")
        j = text.index("{", i)
    except ValueError:
        return out
    depth, k = 0, j
    while k < len(text):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                break
        k += 1
    names = set(re.findall(r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\(",
                           text[j + 1:k], re.M))
    pairs: list[tuple[str, str]] = []
    for name in sorted(names):
        for lo, hi in (("Min", "Max"), ("Start", "End")):
            if name.endswith(lo) and (name[:-len(lo)] + hi) in names:
                pairs.append((name, name[:-len(lo)] + hi))
    if not pairs:
        return out

    bad: dict[str, list[str]] = {}
    for f in sorted(materials_dir.rglob("*.mat")) + sorted(materials_dir.rglob("*.asset")):
        try:
            t = f.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if guid not in t:
            continue
        for lo, hi in pairs:
            def g(key: str) -> float | None:
                m = re.search(rf"^[ \t]*- {key}: ([-\d.eE+]+)[ \t]*$", t, re.M)
                return float(m.group(1)) if m else None
            a, b = g(lo), g(hi)
            if a is not None and b is not None and a > b:
                bad.setdefault(f"{lo} > {hi}", []).append(f.stem)

    for what, mats in sorted(bad.items()):
        out.append(Finding(
            "error", f"{len(mats)} 件（{mats[0]} ほか）",
            f"対になった値が逆転している: {what}",
            "`Min` / `Max`（`Start` / `End`）は「この範囲で効く」という意味なので、"
            "**逆転すると機能が丸ごと死ぬか常時発動する。**"
            " どちらも絵の説明が付かない。値を入れ替えること。"))
    return out


def check_srp_batcher(root: Path) -> list[Finding]:
    """**SRP Batcher が効く形を保っているか**（T-301）。

    SRP Batcher は「マテリアルごとの値を GPU 側に置きっぱなしにして、
    描画のたびに送り直さない」仕組み。効かなくなると、マテリアルの数だけ
    定数バッファを積み直すことになる。**絵は 1 ピクセルも変わらない**ので、
    フレームデバッガを開くまで気付けない。

    条件は静的に確かめられるものが多い:

      1. `UnityPerMaterial` が**ちょうど 1 つ**で、全パスで同じ内容
      2. その中に `#if` が無い（変種ごとに配置が変わると成立しない）
      3. その中にテクスチャ／サンプラの宣言が無い
      4. その中に `unity_*` の組み込み変数が無い
      5. **`multi_compile_instancing` を宣言していない**

    5 は説明が要る。キャラは SkinnedMeshRenderer で描くので
    **そもそもインスタンシングされない**。`UNITY_INSTANCING_BUFFER` も
    持たないので、宣言しても変種が 2 倍になるだけ。
    そのうえ**マテリアルの Enable GPU Instancing に印が入った瞬間、
    そのレンダラーは SRP Batcher から外れる**（Unity は
    インスタンシングを優先するため）── 得の無い側に倒れる罠になる。
    実際 8 パス全部に付いていて、外したら**システム変種が半分**になった。
    """
    out: list[Finding] = []
    shader = find_main_shader(root)
    if shader is None:
        return out
    text = shader.read_text(encoding="utf-8", errors="replace")

    if re.search(r"^\s*#\s*pragma\s+multi_compile_instancing", text, re.M):
        n = len(re.findall(r"^\s*#\s*pragma\s+multi_compile_instancing", text, re.M))
        out.append(Finding(
            "warning", f"{shader.name}（{n} パス）",
            "multi_compile_instancing が SRP Batcher の邪魔になる",
            "キャラは SkinnedMeshRenderer で描くので**インスタンシングされない**。"
            "`UNITY_INSTANCING_BUFFER` も無いので、宣言しても**変種が 2 倍になるだけ**。"
            " そのうえ**マテリアルの Enable GPU Instancing に印が入った瞬間、"
            "そのレンダラーが SRP Batcher から外れる** ── 得の無い側に倒れる。"))

    # CBUFFER の形
    # **隣のシェーダーを混ぜないこと。**
    # 同じフォルダに `IdolScreenOutline.shader`（画面空間の輪郭）が居て、
    # そちらは**自分の `UnityPerMaterial` を持つのが正しい**。
    # 一緒に数えて「複数ある」と報告していた。
    # 見るのは共有ヘッダ（`.hlsl`）と**主シェーダー本体**だけ。
    blocks: list[tuple[Path, str, str]] = []
    for f in sorted(root.rglob("*.hlsl")) + [shader]:
        t = f.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r"CBUFFER_START\((\w+)\)(.*?)CBUFFER_END", t, re.S):
            blocks.append((f, m.group(1), m.group(2)))

    per_mat = [b for b in blocks if b[1] == "UnityPerMaterial"]
    if len(per_mat) > 1:
        out.append(Finding(
            "error", " / ".join(sorted({b[0].name for b in per_mat})),
            "UnityPerMaterial が複数ある",
            "**同じパスに 2 つ入ると配置が定まらず、SRP Batcher が成立しない。**"
            " 宣言は 1 か所にまとめること。"))

    for f, name, body in per_mat:
        if re.search(r"^\s*#\s*(if|ifdef|ifndef|elif|else)\b", body, re.M):
            out.append(Finding(
                "error", f.name, "UnityPerMaterial の中に条件分岐がある",
                "**変種ごとに配置が変わると SRP Batcher が成立しない。**"
                " 条件で増減する値は、常に宣言して中で使い分けること。"))
        if re.search(r"\b(TEXTURE2D\w*|TEXTURECUBE\w*|SAMPLER)\s*\(", body):
            out.append(Finding(
                "error", f.name, "UnityPerMaterial の中にテクスチャ宣言がある",
                "**テクスチャとサンプラは定数バッファに入らない。** 外へ出すこと。"))
        builtin = sorted(set(re.findall(r"\b(unity_\w+)\b", body)))
        if builtin:
            out.append(Finding(
                "error", f.name, "UnityPerMaterial の中に組み込み変数がある",
                f"{' / '.join(builtin)} はエンジンが `UnityPerDraw` で渡すもの。"
                f"**マテリアル側に置くと値が来ないうえ、SRP Batcher も崩れる。**"))

    # 全パスが共通ヘッダを読んでいるか（＝どのパスでも CBUFFER が同じ）
    header = per_mat[0][0].name if per_mat else None
    if header:
        missing = []
        for m in re.finditer(r'Name\s+"(\w+)"', text):
            s = text.find("HLSLPROGRAM", m.end())
            e = text.find("ENDHLSL", s)
            if s < 0 or e < 0:
                continue
            body = text[s:e]
            # SubShader 直下の HLSLINCLUDE は全パスに入る
            if header in body or re.search(rf'HLSLINCLUDE.*?{re.escape(header)}',
                                           text, re.S):
                continue
            missing.append(m.group(1))
        if missing:
            out.append(Finding(
                "error", " / ".join(missing),
                f"{header} を読まないパスがある",
                "**パスごとに UnityPerMaterial が違うと SRP Batcher が成立しない。**"
                " 共通ヘッダを全パスで読むこと。"))
    return out


def check_cs_property_names(root: Path) -> list[Finding]:
    """**C# が名指しするプロパティが実在するか**（T-299）。

    エディタ拡張は `HasFloat("_Foo")` で存在を確かめてから読む、という
    書き方をする。**綴りを間違えると `false` が返るだけ**で、例外も警告も
    出ないまま**その項目が黙って何もしなくなる。**

    実際に踏んだ形: サーフェスタイプの道具に「タイプを直した瞬間に
    目を覚ます値」の見張りを 4 つ書いたが、うち 1 つは `_AnisoStrength` で
    **実在しない**（正しくは `_HairSpecIntensity`）。
    **一度も鳴らない見張り**になっていた。

    移行スクリプトの対応表には既に同じ検査がある（`check_migration_rules`）。
    そちらは対応表という決まった形だけを見ているので、ここは
    **文字列リテラルとして書かれたプロパティ名**を広く拾う。

    移行元シェーダーの名前は対象外 ── `lint:foreign` で囲ってあるので
    そこは読み飛ばす。
    """
    out: list[Finding] = []
    shader = find_main_shader(root)
    if shader is None:
        return out
    text = shader.read_text(encoding="utf-8", errors="replace")
    props = set(re.findall(r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\(", text, re.M))
    if not props:
        return out

    pkg = root
    p = root
    while p.parent != p:
        if (p / "package.json").exists():
            pkg = p
            break
        p = p.parent

    # **呼び出しの形だけを見てはいけない。**
    # 最初は `HasFloat("_Foo")` のような**直接の引数**だけを拾っていたが、
    # 見張りの表を `("_AnisoStrength", 0f, 1f, ...)` というタプルで持って
    # あとから `HasFloat(prop)` と回す書き方が拾えず、
    # **まさに守りたかった形を素通り**した（注入しても撃たなかった）。
    # プロパティ名らしい文字列リテラルを広く見る。
    call = re.compile(r"\"(_[A-Za-z]\w*)\"")
    # **Idol のマテリアルを触るファイルだけを見る。**
    # パッケージ全体を見たら 17 件すべて誤検出だった:
    #   `_HeadForward` / `_IdolHairShadowMap` … `Shader.SetGlobalX` で渡す
    #                                            **グローバル uniform**。
    #                                            マテリアルのプロパティではない
    #   `_OutlineThickness` ほか            … 輪郭の後処理という**別シェーダー**の持ち物
    #   `_SmoothNormals`                    … メッシュ側のバッファ名
    # これらは「実在しない」のではなく**別の場所に在る**。
    # 判定を賢くするより、見る場所を正しくするほうが確か。
    # **見つからなければ 1 つ上へ落とす。** 自己診断のサンドボックスは
    # 平坦に組み直してあって `Editor/Idol/` が無く、早期リターンだと
    # **検査が黙って何もしない**（`check_menu_paths` で踏んだのと同じ形）。
    # 実プロジェクトでは `Editor/Idol/` が在るので、隣の Cel は拾わない。
    scope = pkg / "Editor" / "Idol"
    if not scope.is_dir():
        scope = pkg / "Editor"
    if not scope.is_dir():
        return out

    bad: dict[str, str] = {}
    for cs in sorted(scope.rglob("*.cs")):
        raw = cs.read_text(encoding="utf-8", errors="replace")
        # **`Material` を扱わないファイルは対象外。**
        # `SmoothNormalBaker` はメッシュを焼く道具で、`"_SmoothNormals"` は
        # **出力ファイル名の接尾辞**。プロパティ名の綴りとは何の関係も無い。
        if not re.search(r"\bMaterial\b", raw):
            continue
        # **移行元の名前は別物。** `lint:foreign` で囲われた範囲は飛ばす。
        # 消してしまうと行番号がずれるので、**範囲として持って判定する。**
        foreign = [(m.start(), m.end()) for m in
                   re.finditer(r"lint:foreign-begin.*?lint:foreign-end", raw, re.S)]
        stripped = _strip_line_comments(raw)
        for m in call.finditer(stripped):
            name = m.group(1)
            if name in props or name in bad:
                continue
            if name.isupper() or name.endswith("_ON"):
                continue                      # キーワードはプロパティではない
            at = raw.find(f'"{name}"')
            if at < 0 or any(s <= at < e for s, e in foreign):
                continue
            bad[name] = f"{cs.name}:{raw[:at].count(chr(10)) + 1}"

    for name, where in sorted(bad.items()):
        out.append(Finding(
            "warning", where, f"C# が実在しないプロパティを指す: {name}",
            f"シェーダーに `{name}` は無い。`HasFloat` は **false を返すだけ**なので"
            f"例外も警告も出ず、**その処理が黙って何もしなくなる。**"
            f" 名前を変えたか、書き間違えたか。"))
    return out


def check_doc_feature_names(root: Path) -> list[Finding]:
    """**文書が挙げる Renderer Feature が実在するか**（T-311）。

    Feature の名前を変えると、文書の手順だけが残る。
    「Add Renderer Feature から `Toon Outline Feature` を選ぶ」と書いてあるのに
    その名前が一覧に無い ── **押せないのではなく項目が無い**ので、
    読んだ側は自分の見落としと解釈して探し回る。
    メニューの案内（`check_menu_paths`）と同じ型。

    Unity は `ToonOutlineFeature` を **`Toon Outline Feature`** と表示するので、
    空白を落として突き合わせる。
    """
    out: list[Finding] = []
    # **必ず解決してから親を辿ること。** 相対パスのままだと
    # `Path("..").parent.name` は **空文字**で、`"Packages"` と一致しない
    # ── 呼び出し方（相対か絶対か）で結果が変わる検査になっていた。
    # 実際、相対で呼ぶと隣のパッケージを見つけられず誤検出が 1 件出た。
    root = root.resolve()
    pkg = root
    p = root
    while p.parent != p:
        if (p / "package.json").exists():
            pkg = p
            break
        p = p.parent

    # **隣のパッケージも見る。** 文書は EasyPBR の `DollOutlineFeature` の
    # ように**別パッケージの Feature を引き合いに出す**ことがあり、
    # 自分の中だけを見ると「実在しない」と誤って報告する（実際そうなった）。
    scan_root = pkg.parent if pkg.parent.name == "Packages" else pkg
    real: set[str] = set()
    for cs in sorted(scan_root.rglob("*.cs")):
        text = cs.read_text(encoding="utf-8", errors="replace")
        if "ScriptableRendererFeature" not in text:
            continue
        for m in re.finditer(r"class\s+(\w*Feature)\b", text):
            real.add(m.group(1))
    if not real:
        return out

    cited: dict[str, str] = {}
    for doc in sorted(pkg.rglob("*.md")):
        text = doc.read_text(encoding="utf-8", errors="replace")
        # 「`Toon Outline Feature`」「`ToonOutlineFeature`」のどちらも拾う
        for m in re.finditer(r"`([A-Z][\w ]*?Feature)`", text):
            name = m.group(1).replace(" ", "")
            if name in real or name in cited:
                continue
            cited[name] = f"{doc.name}:{text[:m.start()].count(chr(10)) + 1}"

    for name, where in sorted(cited.items()):
        out.append(Finding(
            "warning", where, f"文書が実在しない Feature を挙げている: {name}",
            f"パッケージに `{name}` というクラスが無い。"
            f" **Add Renderer Feature の一覧に出てこない**ので、"
            f"読んだ側は自分の見落としと解釈して探し回る。"
            f" 実在するのは: {', '.join(sorted(real))}"))
    return out


def check_menu_paths(root: Path) -> list[Finding]:
    """**案内しているメニューが実在するか**を見る（T-293）。

    文書やインスペクタが「`Tools > Idol > ...` を押してください」と書いても、
    それが実在するかは誰も確かめていない。**リネームすると案内だけが残る。**

    この型は繰り返し出ている:
      - `check_gui_claims` ── インスペクタの説明が実装とずれる（3 回）
      - `check_feature_installed` ── 機能を ON にしたが Feature が無い
      - 移行スクリプトの対応表が実在しないプロパティを指す

    メニューの案内は特にずれやすい。**押しても何も起きないのではなく、
    そもそも項目が無い**ので、利用者は「自分の見落とし」と解釈して探し回る。

    `[MenuItem("Tools/Idol/...")]` を集めて、案内側と突き合わせる。
    """
    out: list[Finding] = []
    # パッケージのルートを探す。**見つからなければ `root` 自身を使う。**
    # ここで早期リターンしていたせいで、平坦に組み直したサンドボックスでは
    # **検査が黙って何もしていなかった**（自己診断が「増えない」と教えてくれた）。
    pkg = root
    p = root
    while p.parent != p:
        if (p / "package.json").exists():
            pkg = p
            break
        p = p.parent

    declared: set[str] = set()
    for cs in sorted(pkg.rglob("*.cs")):
        for m in re.finditer(r'\[MenuItem\s*\(\s*"([^"]+)"', cs.read_text(
                encoding="utf-8", errors="replace")):
            declared.add(m.group(1))
    if not declared:
        return out

    # **自分たちが持っているメニューだけを見る。**
    # ここを絞らないと誤検出しか出ない ── 実際、最初に書いたときは
    # 4 件すべてが誤検出で、`GetWindow<T>(false, "Cel Setup")` という
    # **C# のコード**と、`Window > Package Manager`（**Unity 自身のメニュー**）
    # を「実在しない」と言っていた。
    # 宣言済みメニューの上位 2 階層を「持ち物」とみなす。
    owned = {"/".join(d.split("/")[:2]) for d in declared if d.count("/") >= 1}
    if not owned:
        return out

    CITE = re.compile(r"\b(Tools|Window)\s*>\s*([^`\n*」）)]+)")
    seen: dict[str, str] = {}
    # **検査の文面自身も見る。** 指摘のたびに
    # 「`Tools > Idol > ...` で直せる」と案内しているが、その文字列は
    # 誰も検算していなかった ── **自分で作った穴。**
    # メニューをリネームすると、診断が存在しない手順を指し続ける。
    for doc in (sorted(pkg.rglob("*.md")) + sorted(pkg.rglob("*.cs"))
                + sorted(pkg.rglob("*.py"))):
        # **自己診断は壊れた名前を「わざと」持っている。**
        # 注入用のテストデータなので、ここで実在を求めると必ず鳴る。
        if doc.name == "self_test.py":
            continue
        text = doc.read_text(encoding="utf-8", errors="replace")
        if doc.suffix == ".cs":
            text = _strip_line_comments(text)
        for m in CITE.finditer(text):
            path = re.sub(r"\s*/\s*", "/",
                          (m.group(1) + "/" + m.group(2)).replace(" > ", "/")).strip()
            path = path.rstrip(" 。、,.")
            if not any(path.startswith(o) for o in owned):
                continue                      # 他人のメニューには口を出さない
            if path in seen:
                continue
            # 末尾に説明が続いている場合を考慮して前方一致でも許す
            if any(d.startswith(path) or path.startswith(d) for d in declared):
                continue
            seen[path] = doc.name

    for path, where in sorted(seen.items()):
        out.append(Finding(
            "warning", where, "案内しているメニューが実在しない",
            f"`{path}` を案内しているが、`[MenuItem]` にその名前が無い。"
            f" **押せないのではなく項目自体が無い**ので、"
            f"読んだ側は「自分の見落とし」と解釈して探し回ることになる。"
            f" 実在するのは: {', '.join(sorted(d for d in declared if d.startswith(path.split('/')[0]))[:4])}"))
    return out


def check_depth_texture_required(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**深度テクスチャを読む機能があるのに、パイプラインが作らない**（T-291）。

    このシェーダーは 1 か所で `SampleSceneDepth` を読む:

      リムのシルエット検出   `ToonPBRRim.hlsl` ── 法線方向にずらした点の
                             深度差でシルエットを見つける。**Screen Silhouette
                             モードのときだけ**通る（T-343 で既定は Fresnel）

    URP が深度テクスチャを作らない設定だと、**読み先が未定義**になる。
    例外は出ず、深度差が常に一定になるので**リムが全面に出る／一切出ない**の
    どちらかに倒れる。「リムの値が悪い」と読めてしまい、原因に辿り着けない。

    **品質レベルごとに URP アセットが違う**のが厄介なところ。
    実際このプロジェクトは PC 側が 1 で Mobile 側が 0 だった ──
    PC で調整した絵が、品質を落とした瞬間に別物になる。
    `ToonOutlineFeature` の未導入（T-281）と同じ型で、
    **シェーダーもマテリアルも正しいのに絵が出ない**。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    # 深度を読む機能が実際に使われているか
    users: dict[str, int] = {}
    for mat in find_materials(materials_dir):
        t = mat.read_text(encoding="utf-8", errors="replace")
        # 深度を読むのは Screen Silhouette モードのリムだけ。
        # _RimMode 未保存のマテリアルは既定 1 (Fresnel) なので深度を読まない
        # （T-343 で既定を反転済み）。
        mm = re.search(r"^[ \t]*- _RimMode: ([-\d.eE+]+)[ \t]*$", t, re.M)
        ri = re.search(r"^[ \t]*- _RimIntensity: ([-\d.eE+]+)[ \t]*$", t, re.M)
        if (mm and float(mm.group(1)) < 0.5
                and ri and float(ri.group(1)) > 0.0):
            users["リム(Screen Silhouette)"] = users.get("リム(Screen Silhouette)", 0) + 1
    if not users:
        return out

    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return out

    off: list[str] = []
    for rp in sorted((project / "Assets").rglob("*.asset")):
        try:
            t = rp.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if "m_RequireDepthTexture" not in t:
            continue
        m = re.search(r"m_RequireDepthTexture: ([-\d]+)", t)
        if m and int(m.group(1)) == 0:
            off.append(rp.name)
    if not off:
        return out

    used = " / ".join(f"{k} {v} 件" for k, v in sorted(users.items()))
    out.append(Finding(
        "warning", " / ".join(off), "深度テクスチャを作らない品質レベルがある",
        f"{used} が `SampleSceneDepth` を読むのに、"
        f"この URP アセットは Depth Texture を作らない。"
        f" **読み先が未定義**になり、例外も出ないまま深度差が一定に潰れて"
        f"**リムが全面に出る／一切出ない**のどちらかに倒れる。"
        f" シェーダーもマテリアルも正しいので「リムの値が悪い」と読めてしまう。"
        f" その品質レベルを使わないなら問題ないが、"
        f"**PC で調整した絵が品質を落とした瞬間に別物になる**ことは知っておくこと。"))
    return out


def check_pinned_to_max(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**全マテリアルが値域の上限に張り付いている**ものを見つける（T-298）。

    複数選択でスライダーを端まで引くと、部位ごとに詰めた値が**一度に消える。**
    元の値はどこにも残らないので、気付かなければそのまま。

    実際に踏んだ形 ── `_SpecularIntensity` を移行の世代で辿ると:

        移行元         1.5 が 43 件 / 0 が 3 件
        1 世代前       0.2 が 22 件 / 0.5 が 16 件 / **0 が 8 件**
        現在（Idol）   **4 が 46 件**（値域の上限）

    移行の変換は `Clamp(v, 0, 4)` なので、0.2 が 4 になることはない。
    **意図的に 0 だった 8 件も 4 になっている** ── 全選択で引いた形。

    **既定が上限のものは対象外。** `_MicroShadow` や `_RampStrength` は
    既定が 1 で上限も 1 なので、全件 1 なのは普通の状態
    ── ここを見ないと 11 件のうち 8 件が誤検出になる。

    警告に留める。上限が欲しい場面はあるし、**全件が同じ値であること自体は
    欠陥ではない**（既定のまま触っていなければそうなる）。

    **下限側は見ない（調べたうえでの判断）。** 「全件が下限」は
    `_EnvSpecIntensity = 0`（環境反射を切る）のように**正当な状態**で、
    上限側と対称ではない。実際に 3 体を調べたら 9 件出たが、
    どれも「その機能を使っていない」だけだった。

    「テクスチャは割り当ててあるのに強度が 0」も見ない。
    `Materials 3` は 46 件すべてに `_SSSMap` があって強度 0 が 32 件あるが、
    **移行元も `_SkinScatterIntensity` / `_SSSIntensity` が 0** で、
    移行が忠実に写しただけ ── 焼いた作業が捨てられているのではない。
    ここを警告にすると、正しい状態に対して鳴り続ける。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out
    shader = find_main_shader(root)
    if shader is None:
        return out

    text = shader.read_text(encoding="utf-8", errors="replace")
    try:
        i = text.index("Properties")
        j = text.index("{", i)
    except ValueError:
        return out
    depth, k = 0, j
    while k < len(text):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                break
        k += 1

    spec = re.compile(
        r'^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\([ \t]*"[^"]*"[ \t]*,[ \t]*'
        r'Range\(([-\d.]+)[ \t]*,[ \t]*([-\d.]+)\)\)[ \t]*=[ \t]*([-\d.]+)', re.M)
    ranges = {m.group(1): (float(m.group(2)), float(m.group(3)), float(m.group(4)))
              for m in spec.finditer(text[j + 1:k])}
    if not ranges:
        return out

    # **キャラごとに見る。** 「全選択でスライダーを引く」は 1 体分の
    # マテリアルを選んでやる操作なので、プロジェクト全体で混ぜると
    # **キャラごとに違う値が打ち消し合って、何も出なくなる**
    # ── 実際 3 体を一度に渡したら 0 件になった。
    groups: dict[Path, list[Path]] = {}
    for m in find_materials(materials_dir):
        groups.setdefault(m.parent, []).append(m)

    for folder, mats in sorted(groups.items()):
        if len(mats) < 4:        # 数が少ないと「全件同じ」に意味が無い
            continue
        texts = [m.read_text(encoding="utf-8", errors="replace") for m in mats]

        pinned: list[str] = []
        for name, (lo, hi, dflt) in sorted(ranges.items()):
            if hi <= lo or abs(hi - dflt) < 1e-6:
                continue                              # 既定が上限なら普通
            vals = []
            for t in texts:
                m2 = re.search(rf"^[ \t]*- {name}: ([-\d.eE+]+)[ \t]*$", t, re.M)
                if m2:
                    vals.append(float(m2.group(1)))
            if len(vals) != len(mats) or not vals:
                continue
            if len(set(vals)) == 1 and abs(vals[0] - hi) < 1e-6:
                pinned.append(f"{name} = {hi:g}（既定 {dflt:g}）")

        if pinned:
            out.append(Finding(
                "warning", f"{folder.name}（{len(mats)} 件すべて）",
                "値域の上限に張り付いている",
                f"{' / '.join(pinned)}。"
                f" **複数選択でスライダーを端まで引くと、部位ごとに詰めた値が"
                f"一度に消える。** 元の値はどこにも残らない。"
                f" 意図した設定ならそのままでよいが、"
                f"**サーフェスタイプが Default で眠っている機能が混じっていると、"
                f"タイプを直した瞬間に最大強度で目を覚ます**点に注意すること。"))
    return out


def check_unused_pass_cost(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**使っていない重ね描きパスの代金**を見つける（T-303）。

    `HairSeeThrough` は `LightMode = SRPDefaultUnlit` にあり、
    **URP が既定で描く**（Renderer Feature が要らないのが利点）。
    裏返すと、前髪透過を使わないマテリアルまで
    **前方描画で毎フレーム 2 回描かれる。**
    ステンシル（眉 2 / 目 4 のビット）で画素は落ちるが、
    **描画コールと頂点処理は走る。**

    止め方はマテリアル側にある（`SetShaderPassEnabled`）。
    Cel の GUI は部位プリセットで自動的に止めていたが、
    **Idol の GUI は呼んでいなかった。**

    実測で差が出た形:
      46 件のキャラ … 39 件で止まっていた
      20 件のキャラ … **1 件も止まっていない**（× 2 体）

    **絵は変わらず draw だけが倍**なので、フレームデバッガを開くまで
    気付けない。キャラごとに見る ── 1 体だけ止め忘れる形で出るため。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out
    shader = find_main_shader(root)
    if shader is None:
        return out
    text = shader.read_text(encoding="utf-8", errors="replace")
    if "SRPDefaultUnlit" not in text:
        return out

    groups: dict[Path, list[Path]] = {}
    for m in find_materials(materials_dir):
        groups.setdefault(m.parent, []).append(m)

    for folder, mats in sorted(groups.items()):
        if len(mats) < 4:
            continue
        paying = 0
        for f in mats:
            t = f.read_text(encoding="utf-8", errors="replace")
            m = re.search(r"disabledShaderPasses:\s*\n((?:[ \t]*- \w+\n)*)", t)
            names = {n.upper() for n in re.findall(r"-[ \t]*(\w+)", m.group(1))} if m else set()
            if "SRPDEFAULTUNLIT" in names:
                continue
            # 透過を実際に使っている髪は払って当然
            comp = re.search(r"^[ \t]*- _StencilComp: ([-\d.eE+]+)[ \t]*$", t, re.M)
            mask = re.search(r"^[ \t]*- _StencilReadMask: ([-\d.eE+]+)[ \t]*$", t, re.M)
            if comp and mask and round(float(comp.group(1))) == 3 \
                    and round(float(mask.group(1))) == 6:
                continue
            paying += 1

        if paying:
            out.append(Finding(
                "warning", f"{folder.name}（{paying} / {len(mats)} 件）",
                "使っていない重ね描きパスの代金を払っている",
                # **文面にフォルダ名を入れる。** 同じ文面の指摘は 1 行に
                # まとめられるので、キャラごとに出したいものが**混ざって消える**
                # ── 自己診断が「注入しても増えない」と教えてくれた。
                f"`{folder.name}` の {paying} 件。"
                f"`HairSeeThrough` は `SRPDefaultUnlit` にあり **URP が既定で描く**ので、"
                f"前髪透過を使わないマテリアルも**前方描画で 2 回**描かれる。"
                f" ステンシルで画素は落ちるが、**描画コールと頂点処理は走る。**"
                f" **絵は変わらず draw だけが倍**なので、"
                f"フレームデバッガを開くまで気付けない。"
                f" `Tools > Idol > 使っていない重ね描きパスを止める` でまとめて止まる。"))
    return out


def check_motionvectors_disabled(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**MotionVectors パスを止めたキャラ**を見つける（T-304）。

    止めれば描画は減るが、**TAA とモーションブラーが動く物体を追えなくなる。**
    スキンメッシュは毎フレーム形が変わるので、影響が大きい ──
    キャラだけが尾を引く、輪郭が二重に残る、という形で出る。

    **原因が見えないのが厄介。** 症状は「TAA の設定が悪い」に見えるが、
    実際にはマテリアルの `disabledShaderPasses` にある。
    インスペクタにも出ないので、`.mat` を開くまで分からない。

    実測: 3 体のうち 1 体（46 件）が**全件で止めていた。**
    パッケージ側に止めるコードは無いので、手で入れたもの。
    そのキャラは今のところ TAA のシーンに居ないため実害は出ていない。

    **TAA を使っていなければ正しい最適化**なので、警告に留める。
    プロジェクト内に TAA のカメラが何台あるかを添えて、判断材料にする。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    groups: dict[Path, list[Path]] = {}
    for m in find_materials(materials_dir):
        groups.setdefault(m.parent, []).append(m)

    # プロジェクトに TAA / モーションブラーが在るか（URP は 3 が TAA）
    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    taa: list[str] = []
    if project is not None:
        for sc in sorted((project / "Assets").rglob("*.unity")):
            try:
                t = sc.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            if re.search(r"^\s*m_Antialiasing: 3\s*$", t, re.M):
                taa.append(sc.name)

    for folder, mats in sorted(groups.items()):
        if len(mats) < 4:
            continue
        off = 0
        for f in mats:
            t = f.read_text(encoding="utf-8", errors="replace")
            m = re.search(r"disabledShaderPasses:\s*\n((?:[ \t]*- \w+\n)*)", t)
            names = {n.upper() for n in re.findall(r"-[ \t]*(\w+)", m.group(1))} if m else set()
            if "MOTIONVECTORS" in names:
                off += 1
        if off != len(mats):
            continue                     # 一部だけなら意図的な使い分けとみなす

        where = (f"このプロジェクトには TAA のカメラが {len(taa)} 台ある"
                 f"（{' / '.join(taa[:3])}）" if taa
                 else "このプロジェクトに TAA のカメラは無い")
        out.append(Finding(
            "warning", f"{folder.name}（{off} 件すべて）",
            "MotionVectors パスを止めている",
            f"`{folder.name}` の {off} 件。止めれば描画は減るが、"
            f"**TAA とモーションブラーがこのキャラを追えなくなる。**"
            f" スキンメッシュは毎フレーム形が変わるので、"
            f"**キャラだけが尾を引く／輪郭が二重に残る**形で出る。"
            f" 症状は「TAA の設定が悪い」に見えるが、原因は"
            f"マテリアルの `disabledShaderPasses` で、"
            f"**インスペクタにも出ない。** {where}。"
            f" TAA を使わないなら正しい最適化なので、そのままでよい。"))
    return out


def check_surface_type_by_name(materials_dir: Path | None) -> list[Finding]:
    """**サーフェスタイプが Default のまま**の材質を見つける（T-290）。

    このシェーダーの部位別の中身 ── 髪の異方性 2 バンド、顔の SDF、
    肌の SSS、布の Charlie sheen ── は**すべて `_SurfaceType` の裏**にある。
    Default のままだと、それらは 1 命令も走らない。

    **例外も警告も出ない。** 「PBR 寄りの絵」に見えるだけなので、
    シェーダーの出来を評価している最中でも、機能が止まっていることに
    気付ける形になっていない。

    実際に踏んだ形（T-290）: 利用者が見ていたキャラの **20 件中 17 件**が
    Default だった。髪の異方性も顔の SDF も止まったまま、
    影のちらつきや鏡面の強さを議論していた。
    移行スクリプトを通した別のキャラは 46 件すべて正しかった ──
    **移行を経ていない材質だけが落ちる。**

    手掛かりは名前。VRoid の書き出しは `..._00_HAIR` のように
    **部位を大文字のトークン**で持つ。小文字混じり（`Face_00_SKIN` の
    `Face`）と混同しないよう、大文字のトークンだけを見る
    ── そこを緩めると顔の肌が Face に化ける。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    KIND = {"SKIN": (1, "Skin"), "FACE": (2, "Face"),
            "HAIR": (3, "Hair"), "CLOTH": (4, "Cloth")}
    bad: list[str] = []
    kinds: set[str] = set()
    for mat in find_materials(materials_dir):
        # `_` 区切りの大文字トークンだけを見る（末尾の連番は落とす）
        tokens = [t for t in re.split(r"[_\s]+", mat.stem) if t in KIND]
        if not tokens:
            continue
        want, label = KIND[tokens[-1]]
        text = mat.read_text(encoding="utf-8", errors="replace")
        m = re.search(r"- _SurfaceType: ([-\d.eE+]+)", text)
        got = round(float(m.group(1))) if m else 0
        if got == want:
            continue
        # **Default 以外に設定してあるなら口を出さない。** 意図して
        # 別のタイプにしている（髪の房を Cloth で扱うなど）ことがある。
        if got != 0:
            continue
        bad.append(mat.stem)
        kinds.add(label)

    if bad:
        out.append(Finding(
            "error", f"{len(bad)} 件（{bad[0]} ほか）",
            "サーフェスタイプが Default のまま",
            f"名前は {' / '.join(sorted(kinds))} を示しているのに Default。"
            f" **部位別の中身が 1 命令も走らない** ── 髪の異方性 2 バンド、"
            f"顔の SDF、肌の SSS、布の Charlie sheen はすべて `_SurfaceType` の裏にある。"
            f" 例外も警告も出ず「PBR 寄りの絵」に見えるだけなので、"
            f"**シェーダーを評価している最中でも気付けない**。"
            f" `Tools > Idol > サーフェスタイプを名前から設定` でまとめて直せる。"))
    return out


def check_atan2_guard(root: Path) -> list[Finding]:
    """**`atan2(0, 0)` は未定義。** 守られていない呼び出しを見つける（T-288）。

    D3D も Vulkan も (0,0) の結果を規定していない。0 が返る実装もあれば
    NaN が返る実装もあり、**開発機では出ないのに実機で出る**類になる。
    NaN が出れば sincos → 接線フレームと伝播して、**ハイライトに黒い穴**が開く。

    実際に踏みかけた形（毛流れ）:
      フローマップの「ここは向きが決まらない」は **RG = (0.5, 0.5)**。
      旋毛の中心や毛流れの交差点に必ず現れる。信頼度×強度が 1 に飽和すると
      lerp が完全にそちらへ寄り、`fv` の長さが 0 になる。
      **一点だけ黒くなる**ので、原因を辿るのがきわめて難しい。

    守り方は「長さが 0 なら別の値へ逃がす」。同じ行か直前の数行に
    `dot(...)` / `length(...)` の判定があれば守られていると見なす。
    """
    out: list[Finding] = []
    for f in sorted(root.rglob("*.hlsl")):
        lines = _strip_line_comments(f.read_text(encoding="utf-8", errors="replace")).split("\n")
        for i, line in enumerate(lines):
            if "atan2(" not in line:
                continue
            # 同じ行か直前 3 行に長さの判定があるか
            near = "\n".join(lines[max(0, i - 3):i + 1])
            if re.search(r"\b(dot|length|any|all)\s*\(", near):
                continue
            out.append(Finding(
                "warning", f"{f.name}:{i + 1}", "atan2 が (0,0) から守られていない",
                f"`{line.strip()[:70]}` ── 両方の引数が 0 になる入力があるなら"
                f"**結果は未定義**（0 が返る実装も NaN が返る実装もある）。"
                f" NaN は sincos や正規化を通って伝播し、**一点だけ黒くなる**形で出るので"
                f"原因に辿り着けない。長さが 0 のときの逃がし先を書くこと。"))
    return out


def check_shadow_flicker(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**細い影がちらつく設定**を見つける（T-287）。

    実際に踏んだ形: 前髪が顔に落とす細い影がカメラの微動でちらついた。
    原因は 3 つの重なりで、**どれか 1 つを見ても分からない**:

      1. 影マップの粒に対して毛束が細い
         4096 / 影距離 40m / 3 カスケード → 1 テクセル ≒ 2.7mm。
         4mm の毛束は **1.5 テクセル**しか無く、動くたびに有無が入れ替わる
      2. `_HQShadowOn` が OFF
         自前の 16 タップ（画素ごとに回す Vogel）を使わず URP 標準に任せている
      3. `_ShadowSoftness` が硬い
         遮蔽量のわずかな揺れが**そのまま on/off に増幅**される

    **テクセル寸法は URP アセットから計算する。** 書き写すと必ず古くなる
    ── 実際、以前「約 4.9mm」と書いてあったものがカスケードの変更で
    2.93mm になっていた（T-155）。

    セルの硬さ自体は絵の様式なので**それ単体では指摘しない。**
    3 つが揃ったときだけ出す。
    """
    out: list[Finding] = []
    if materials_dir is None or not materials_dir.is_dir():
        return out

    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return out

    # --- URP アセットから影の粒度を出す -----------------------------------
    best: tuple[float, str] | None = None
    for rp in sorted((project / "Assets").rglob("*.asset")):
        try:
            t = rp.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if "m_MainLightShadowmapResolution" not in t:
            continue

        def num(key: str, default: float) -> float:
            m = re.search(rf"{key}: ([-\d.]+)", t)
            return float(m.group(1)) if m else default

        res = num("m_MainLightShadowmapResolution", 2048)
        dist = num("m_ShadowDistance", 50)
        casc = num("m_ShadowCascadeCount", 1)
        split = num("m_Cascade2Split", 0.25) if casc == 2 else 0.1
        # カスケードが 2 以上なら 2x2 アトラスに分かれる
        tile = res / 2 if casc >= 2 else res
        near = dist * (split if casc >= 2 else 1.0)
        # 境界球はおよそ far の 1.4 倍を覆う
        texel = (near * 1.4) / max(tile, 1)
        if best is None or texel < best[0]:
            best = (texel, rp.name)
    if best is None:
        return out
    texel_mm, asset_name = best[0] * 1000.0, best[1]

    # --- マテリアル側の条件 -----------------------------------------------
    #
    # **増幅の経路は 1 つではない。** 最初は「HQ が OFF」だけを見ていたが、
    # 利用者が HQ を ON にした途端に検査が黙った ── ちらつきは続いていたのに。
    # ON にすると接地硬化（PCSS）が付いてきて、**別の経路で同じ症状**が出る。
    # どちらの経路も拾う。
    hq_off = hard = pcss = 0
    mats = find_materials(materials_dir)
    for f in mats:
        t = f.read_text(encoding="utf-8", errors="replace")

        def g(key: str) -> float | None:
            m = re.search(rf"^[ \t]*- {key}: ([-\d.eE+]+)[ \t]*$", t, re.M)
            return float(m.group(1)) if m else None

        recv = g("_ReceiveShadowStrength")
        if recv is not None and recv <= 0.01:
            continue                       # 影を受けない材質は関係ない

        hq = (g("_HQShadowOn") or 0.0) > 0.5
        soft = g("_ShadowSoftness")
        if soft is not None and soft < 0.05:
            hard += 1
        if not hq:
            hq_off += 1
        # 経路2: 8 タップのブロッカー推定 → 半径が画素ごとにばらつく
        elif (g("_ShadowContactHardening") or 0.0) > 0.5 \
                and (g("_ShadowPenumbraScale") or 0.0) >= 100.0:
            pcss += 1

    if not mats or hard == 0 or (hq_off == 0 and pcss == 0):
        return out

    if pcss:
        cause = (f"その上、接地硬化が入ったものが {pcss} 件"
                 f"（Penumbra Scale 100 以上）。**8 タップのブロッカー推定は"
                 f"ブロッカーがテクセル数個ぶんしか無いと当たらず、"
                 f"半径が画素ごとに 1.0〜8.4 テクセルの間で振れて「まだら」になる。**"
                 f" `Tools > Idol > プリセットを適用` の「ちらつき対策 ①」で"
                 f"接地硬化だけを切れる ── このスケールでは真の半影が"
                 f"1 テクセルに届かないので、**物理的に失うものは無い**。")
    else:
        cause = (f"その上 HQ セルフシャドウが OFF のものが {hq_off} 件で、"
                 f"URP 標準の数タップに任せている。"
                 f"**遮蔽量の揺れが硬い境界でそのまま on/off に増幅される。**"
                 f" `Tools > Idol > プリセットを適用` の「ちらつき対策」で"
                 f"リアルタイム影の側だけを緩められる（セルの硬さは変えない）。")

    # **細い造形が影マップで何テクセルになるか。**
    #
    # しきい値 2.5 で始めたが**甘すぎた** ── 実際に困っている環境が
    # 2.9 テクセルで、検査は黙っていた（T-287）。
    # 4 テクセルを切ると、動いたときにテクセル境界を跨いで有無が入れ替わる。
    # PCF が数タップしか無ければ、その揺れがそのまま遮蔽量に出る。
    strand_mm = 4.0
    texels = strand_mm / max(texel_mm, 1e-6)
    if texels >= 4.0:
        return out

    out.append(Finding(
        "warning", f"{len(mats)} 件", "細い影がちらつく組み合わせ",
        f"影マップの 1 テクセルが **{texel_mm:.2f}mm**（{asset_name} から計算）で、"
        f"{strand_mm:.0f}mm の毛束は **{texels:.1f} テクセル**しか無い。"
        f"Base Softness が 0.05 未満（硬いセル）のものが {hard} 件。 {cause}"
        f" **解像度を上げるだけでは足りない** ── "
        f"4096 → 8192 にしても毛束は 2.9 テクセルにしかならず、揺れは残った。"
        f" 足りなければ髪だけを焼く専用の影へ移すこと（テクセルが更に約 2 倍細かくなる）。"))
    return out


def check_shadow_contrast(v: dict[str, float], where: str) -> list[Finding]:
    """**影が光の何倍の明るさになるか**を実際の値から出す。

    「影が薄い」の原因は 3 つに割れる ── 影色そのもの・影の中の環境光・
    環境光と主光源の強度比。どれが効いているかは値を眺めても分からないので、
    **最終的な比を出してしまう方が早い。**

    式は Unity 側の診断（`ToonPBRSetupCheck.ReportShadowContrast`）と同じ:

        amb    = _AmbientIntensity * sh
        shadow = key * _ShadowValue + amb * _ShadowAmbientIntensity
        lit    = key + amb
        ratio  = shadow / lit

    **主光源 1.0・SH 0.5 を仮定する。** Unity 側はシーンから実際の
    ライト強度と環境光を読むが、こちらはシーンを開けないので置く。
    **絶対値ではなく設定同士の関係を見るための数字**として使うこと。

    Unity 側の診断は**メニューから手で回すもの**で、`check.py` の運用では
    届かない ── 実際、影が薄い状態が長く続いていた（T-284）。
    """
    out: list[Finding] = []
    sv = v.get("_ShadowValue")
    ai = v.get("_AmbientIntensity")
    asi = v.get("_ShadowAmbientIntensity")
    if sv is None or ai is None or asi is None:
        return out

    KEY, SH = 1.0, 0.5
    amb = ai * SH
    lit = KEY + amb
    if lit <= 1e-4:
        return out
    ratio = (KEY * sv + amb * asi) / lit

    # 判定の帯は Unity 側の診断と揃える。**ここを別々に決めると
    # 「Unity では警告、check.py では無言」という食い違いが起きる。**
    if ratio > 0.80:
        out.append(Finding(
            "warning", where, "影がほとんど出ない",
            f"影／光 = {ratio:.2f}。**影として認識されない濃さ。**"
            f" _ShadowValue {sv} / 環境光 {amb:.2f}（影の中 {amb * asi:.2f}）。"
            f" **Intensity in Shadow を下げるのが一番副作用が少ない**"
            f"（全体の明るさを保ったまま影だけ沈む）。"))
    elif ratio > 0.70:
        out.append(Finding(
            "warning", where, "影が薄い",
            f"影／光 = {ratio:.2f}。トゥーン影としては弱い。"
            f" _ShadowValue {sv} / 環境光 {amb:.2f}（影の中 {amb * asi:.2f}）。"
            f" プリセット「標準」（Value 0.62 / Intensity in Shadow 0.45）なら"
            f" 同じ環境光で 0.56 になる。"
            f" **主光源 1.0・SH 0.5 を仮定した値**なので、絶対値ではなく"
            f"設定同士の関係として読むこと。"))
    return out


def _strip_line_comments(src: str) -> str:
    """`//` から行末までを落とす。**文字列の中の `//` は残す**（URL など）。"""
    out = []
    for line in src.splitlines():
        in_str = False
        esc = False
        cut = len(line)
        for i, ch in enumerate(line):
            if esc:
                esc = False
                continue
            if ch == "\\":
                esc = True
            elif ch == '"':
                in_str = not in_str
            elif ch == "/" and not in_str and i + 1 < len(line) and line[i + 1] == "/":
                cut = i
                break
        out.append(line[:cut])
    return "\n".join(out)


def check_gui_claims(root: Path) -> list[Finding]:
    """**インスペクタが実装と違うことを言っていないか。**

    GUI の主張が事実と食い違った例が**3 回続いた**:

      T-264  「影・深度・法線のパスでも切るので消えた部分の影は残らない」
             → 髪の落ち影は残っていた
      T-282  「Binder がシーンに無いと顔だけ破綻します」
             → 破綻しない。段階的に劣化する設計
      T-283  「Thickness を Ray Length の半分以上にすると死にます」
             → 正しくは 2 倍。**4 倍きつく、正常な既定を「死んでいる」と告げていた**

    **インスペクタは一番目に付く場所**で、読む人はそれを信じる。
    コメントや文書より優先して正しさを保つべきなのに、そこだけ検査が無かった
    （W107 は名前は見るが、主張の中身は見ない）。

    文章の正しさは機械では判定できないが、**式に現れる係数**なら照合できる。
    ここで見るのはその一部だけ ── **全部を守れるわけではない**ので、
    GUI に数値を書くときは対応する検査もここへ足すこと。
    """
    out: list[Finding] = []
    gui = next(iter(sorted(root.parent.rglob("ToonPBRShaderGUI.cs"))), None)
    if gui is None:
        for parent in root.resolve().parents:
            if (parent / "package.json").exists():
                gui = next(iter(sorted(parent.rglob("ToonPBRShaderGUI.cs"))), None)
                break
    shadows = next(iter(sorted(root.rglob("ToonPBRShadows.hlsl"))), None)
    if gui is None or shadows is None:
        return out

    raw = gui.read_text(encoding="utf-8", errors="replace")
    hlsl = shadows.read_text(encoding="utf-8", errors="replace")

    # **画面に出る文字列だけを見ること。** ファイル全体を見ると
    # **コメントに書いた数字で合格してしまう** ── 実際そうなっていて、
    # 表示文が誤ったままでも通った。落ちない検査は無いのと同じ。
    gui_text = "\n".join(re.findall(r'"((?:[^"\\]|\\.)*)"', _strip_line_comments(raw)))

    # (0) **「現在の設定」と書かないこと。** 書いた瞬間から腐る。
    #     実際、鏡面プリセットが「現在の設定。布 0.10 / 肌 0.25 / その他 0.20」と
    #     名乗り続けていたが、46 マテリアルすべて `_SpecularIntensity = 4`
    #     ── **その 20 倍**だった（T-285）。値を書くか、書かないかのどちらか。
    for label, path in (("プリセット", "ToonPBRPresets.cs"), ("インスペクタ", gui.name)):
        src = gui if path == gui.name else next(
            iter(sorted(gui.parent.rglob(path))), None)
        if src is None:
            continue
        shown = "\n".join(re.findall(
            r'"((?:[^"\\]|\\.)*)"',
            _strip_line_comments(src.read_text(encoding="utf-8", errors="replace"))))
        if "現在の設定" in shown:
            out.append(Finding(
                "error", src.name, f"{label}が「現在の設定」と書いている",
                "**書いた瞬間から腐る表現。** 利用者が値を変えても文だけ残り、"
                "実際と食い違ったまま「これが現在の設定です」と言い続ける"
                "（実際 20 倍ずれていた）。**具体的な値を書くか、何も書かないこと。**"))

    return out


def check_face_sdf_reachable(v: dict[str, float], where: str,
                             kw: set[str]) -> list[Finding]:
    """顔の SDF が**黙って無効になっている**組み合わせを見つける。

    シェーダーは段階的に劣化する設計:

        faceBlend = _FaceFlatness * max(bound, _FaceUseObjectAxis)

    `bound` は `FaceDirectionBinder` がシーンから供給する（実行時にしか分からない）。
    **Binder が無く、Fallback to Object Axis も OFF なら `faceBlend` が 0** になり、
    SDF マップを割り当てて SDF Blend を 1 にしていても**一切使われない。**
    絵は普通の法線陰影で出るので壊れて見えず、**設定が効いていないことに気付けない。**

    ここで見られるのはマテリアル側だけ（Binder の有無は静的には分からない）ので、
    「Binder が無ければこうなる」という条件付きで出す。
    """
    out: list[Finding] = []
    if "_SURFACETYPE_FACE" not in kw:
        return out

    flat = v.get("_FaceFlatness")
    fallback = v.get("_FaceUseObjectAxis")
    if flat is None or fallback is None:
        return out

    if flat > 0.0 and fallback <= 0.5:
        out.append(Finding(
            "warning", where, "顔の SDF が Binder 無しでは無効になる設定",
            f"_FaceFlatness = {flat} なのに _FaceUseObjectAxis = 0。"
            f" シェーダーは `_FaceFlatness * max(bound, _FaceUseObjectAxis)` で混ぜるので、"
            f"**シーンに FaceDirectionBinder が無いと SDF が一切使われない。**"
            f" 絵は普通の陰影で出るため壊れて見えず、"
            f"**設定が効いていないことに気付けない。**"
            f" Binder を置くか、Fallback to Object Axis を ON にすること。"))

    if flat <= 0.0:
        out.append(Finding(
            "warning", where, "Surface Type が Face なのに SDF を使っていない",
            f"_FaceFlatness = {flat}。SDF 側の設定（マップ・オフセット）は"
            f" すべて無視され、法線による陰影だけになる。"
            f" Face を選ぶ主目的が効いていない状態。"))

    return out


def check_render_settings(root: Path, materials_dir: Path | None) -> list[Finding]:
    """**キャラを映しているシーンに AA が1つも無い**状態を見つける。

    ここまでの検査はシェーダーと .mat しか見ていなかった。
    ところがユーザーの不満は一貫して「細かい部分のちらつき」で、
    **アンチエイリアスが無ければ、シェーダーを何度直しても消えない。**
    髪の毛・睫毛・金具のようなサブピクセルの造形と鏡面ハイライトは、
    AA が無いとカメラの微動でそのまま明滅する。

    シェーダー側の修正でちらつきを追って**2回退行させている**（T-120 / T-124）。
    描画設定を見ずにシェーダーだけ疑っていたのが遠因なので、ここに入れる。

    判定は**両方が無いときだけ**。MSAA が切ってあってもカメラが TAA/SMAA を
    使っていれば正常なので、片方だけでは指摘しない ── 実際このプロジェクトには
    TAA を使っているシーンがあり、そこに警告を出すのは誤検出になる。

    対象は `--materials` のマテリアルを参照しているシーンだけに絞る。
    プロジェクトには 25 シーンあり、全部に出すと読めなくなる。
    """
    out: list[Finding] = []
    if materials_dir is None:
        return out

    # **`Assets` はプロジェクトルートから引く。** 以前は `root.parent` が
    # `Assets` である前提で書いていたが、それはツリーが `Assets/ToonPBR/` に
    # あるときだけ成り立つ。**パッケージへ移した瞬間に空振りする**（T-250）。
    #
    # **resolve() を忘れないこと。** root は `.` で渡されるので、
    # `Path(".").parent` は `.` のまま ── 親へ上がったつもりで同じ場所を見て、
    # 該当シーン 0 件で黙って空振りしていた。
    here = root.resolve()
    project = next((p for p in [here, *here.parents]
                    if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if project is None:
        return out
    assets = project / "Assets"

    def guid_of(meta: Path) -> str | None:
        m = re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(
            encoding="utf-8", errors="replace"), re.MULTILINE)
        return m.group(1) if m else None

    # (1) 対象マテリアルの guid を .meta から集める
    guids: set[str] = {g for g in (guid_of(m) for m in materials_dir.glob("*.mat.meta"))
                       if g}
    if not guids:
        return out

    # **プレハブを1ホップ挟むこと。** シーンはマテリアルを直接参照しない ──
    # キャラはプレハブとして置かれ、マテリアルはプレハブが持っている。
    # 最初これを忘れて「該当シーン 0 件」になり、**検査が黙って空振りした。**
    # 「0 件」が「問題なし」と見分けられないのは、このプロジェクトの持病そのもの。
    for prefab in assets.rglob("*.prefab"):
        text = prefab.read_text(encoding="utf-8", errors="replace")
        if not any(g in text for g in guids):
            continue
        meta = prefab.with_suffix(".prefab.meta")
        if meta.exists():
            g = guid_of(meta)
            if g:
                guids.add(g)

    # (2) URP アセットの MSAA。1 は「無効」（2/4/8 がサンプル数）
    msaa_off = []
    for rp in sorted(assets.glob("Settings/*.asset")):
        text = rp.read_text(encoding="utf-8", errors="replace")
        if "m_MSAA:" not in text or "m_RenderScale:" not in text:
            continue                       # URP アセットではない
        m = re.search(r"^  m_MSAA: (\d+)", text, re.MULTILINE)
        if m and int(m.group(1)) <= 1:
            msaa_off.append(rp.stem)
    if not msaa_off:
        return out                         # どこかで MSAA が有効なら黙る

    # (3) そのマテリアルを使っているシーンで、カメラの AA を見る
    for scene in sorted(assets.rglob("*.unity")):
        text = scene.read_text(encoding="utf-8", errors="replace")
        if not any(g in text for g in guids):
            continue

        modes = [int(x) for x in re.findall(r"m_Antialiasing: (\d+)", text)]
        if not modes or any(x > 0 for x in modes):
            continue                       # カメラが無い、または AA が有効

        out.append(Finding(
            "warning", scene.name, "AA が1つも有効になっていない",
            f"URP アセット（{', '.join(msaa_off)}）の MSAA が無効で、"
            f" このシーンのカメラも AA {AA_NAMES[0]}。"
            f" **髪・睫毛・金具のようなサブピクセルの造形と鏡面ハイライトは、"
            f"AA が無いとカメラの微動でそのまま明滅する。**"
            f" シェーダー側をいくら直しても消えない種類のちらつき。"
            f" カメラの Anti-aliasing を SMAA か TAA にするか、"
            f" URP アセットの MSAA を 4x にして比べること。"))

    return out


def check_pcss(v: dict[str, float], where: str, enabled: bool,
               defaults: dict[str, float]) -> list[Finding]:
    """PCSS の半影半径がどれだけ振れるかを見る。"""
    out: list[Finding] = []
    if not enabled:
        return out
    if v.get("_ShadowContactHardening", 0.0) <= 0.5:
        return out

    soft = v.get("_HQShadowSoftness")
    if soft is None:
        return out

    # **出荷時の既定値には警告を出さない。**
    # 以前はしきい値を絶対値（8 テクセル）で書いていて、既定 0.3 が 8.4 になり
    # **46 マテリアル全部に出ていた。** 100% に出る警告は何も切り分けない
    # ── CLAUDE.md が名指しで禁じている形（「誤検出の出る検査は無いより悪い」）。
    #
    # 見るべきは「既定より広げたか」。広げたのはユーザーの判断なので、
    # そのとき何を引き換えにしているかを示すのがこの検査の仕事。
    # 既定値はシェーダーから読む。**ここに数字を書かない**（書くと古くなる）。
    base = defaults.get("_HQShadowSoftness")
    if base is None or soft <= base + 1e-6:
        return out

    r0 = 1.0 + soft * 6.0
    lo, hi = 1.0, r0 * 3.0
    hi_base = (1.0 + base * 6.0) * 3.0

    out.append(Finding(
        "warning", where, "半影半径の可動域を既定より広げている",
        f"_HQShadowSoftness = {soft}（既定 {base}）。"
        f" radius が {lo:.1f}〜{hi:.1f} テクセルまで振れる"
        f"（既定なら {hi_base:.1f} まで）。"
        f" 半影の推定は 8 タップのブロッカー探索なので、"
        f" **その分散がそのままフィルタ幅の揺れになる。**"
        # **判断材料として物理値を出す。** 「揺れる」だけでは
        # 切るべきか演出として残すべきかを決められない。
        f" なお平行光源（太陽・視直径 0.53 度）の真の半影は"
        f" 遮蔽物までの距離 × 0.00925 で、キャラの自己遮蔽では"
        f" 顎→首 10cm でも **0.93mm** にしかならない。"
        f" シャドウマップ 1 テクセルがそれより太いなら、"
        f" 接地硬化が作っている変化は物理ではなく演出。"
        # **テクセル寸法をここに書かない。** URP アセットの設定で変わるので、
        # 書き写すと古くなる ── 実際「約 4.9mm / 顔 31 テクセル」と書いていたが、
        # ユーザーがカスケードの分割を 0.125 → 0.075 に変えており、
        # 実際は 2.93mm / 51 テクセルになっていた（T-155）。
        # **同じことをこの関数自身がやっていた**（「顔は約 30 テクセル」を
        # 焼き込んでいた。上のコメントで戒めている当の間違い）── T-167。
        # カスケード球の半径は URP 内部の式で決まるので Python では再現しない。
        # 正確な値は Unity 側の診断（テクセル密度）が設定から計算して出す。
        f" 顔が何テクセルになるかは URP アセットの設定で変わるので、"
        f" Unity 側の診断（テクセル密度）で実測を見ること。"
        f" ちらつくなら _ShadowPenumbraScale を下げるか接地硬化を切ること"
        f" ── 物理的に失うものは無い。"))

    return out


def run(root: Path, materials_dir: Path | None) -> list[Finding]:
    shader = (find_main_shader(root) or root / '_missing_.shader')

    # **マテリアルはシェーダーの GUID で絞る。**
    # これが無いと、再帰的に舐めたときに隣のシェーダーのマテリアルまで
    # 拾って、持っていないプロパティを「未設定」と誤って報告する。
    global _SHADER_GUID
    meta = shader.with_suffix(shader.suffix + ".meta")
    if meta.exists():
        m = re.search(r"^guid: ([0-9a-f]{32})",
                      meta.read_text(encoding="utf-8", errors="replace"), re.M)
        _SHADER_GUID = m.group(1) if m else ""
    _MATERIAL_CACHE.clear()
    if not shader.exists():
        return [Finding("error", str(root), "ToonPBR.shader が無い",
                        f"{shader} を開けない。root の指定を確認すること。")]

    defaults = read_defaults(shader)
    ranges = read_ranges(shader)
    toggles = read_toggles(shader)
    defines = read_defines(find_file(root, "ToonPBRCommon.hlsl"))
    findings: list[Finding] = []

    # 式に入れた守りが外れていないか。値の検算より先に見る
    # （守りが無い状態では、値が正しくても破綻しうる）。
    findings += check_guards(root)

    # ドキュメントの陳腐化。**このプロジェクトで 4 回続けて見つかった系統。**
    findings += check_docs(root)

    # バリアント数。**キーワードを 1 つ足すだけで倍**になるので、
    # 書き写した数字の中でも特に古くなりやすい。
    findings += check_variants(root)

    # 効果ゼロなのにコストだけ払っている機能（値がゲートの設計の副作用）
    findings += check_dead_gates(root, materials_dir)

    # 顔の軸（faceFwd / faceRight / faceUp / fwd / right）は既知で許容。
    # 光源に依存しないのは事実だが、外へ出すには顔の SDF ブロックを
    # 丸ごと組み替える必要があり、**灯数 1 の構成では利得がゼロ**。
    # 顔の経路はこのシェーダーで最も込み入っているので、
    # 多灯構成で実測して効くと分かるまで手を付けない（T-139）。
    findings += check_light_loop(root, known_ok=LIGHT_LOOP_KNOWN_OK)

    # 既定値そのものを検算する。**マテリアルが無い環境でも意味を持つ。**
    findings += check_shadow_band(defaults, "既定値")
    findings += check_diffuse_reach(defaults, "既定値", defaults)
    findings += check_pcss(defaults, "既定値", enabled=True, defaults=defaults)
    findings += check_ranges(defaults, ranges, "既定値")

    findings += check_sheen_fit(root)
    findings += check_energy_compensation(root)
    findings += check_package_rules(root)
    findings += check_orphan_includes(root)
    findings += check_pragma_placement(root)
    findings += check_migration_rules(root)
    # 対の値（Min/Max・Start/End）の逆転。旧 run_generic（Cel 用）に
    # しか居なかったが、検査自体はシェーダー非依存なので本線に置く（T-356）。
    findings += check_range_pairs(root, materials_dir)
    findings += check_render_settings(root, materials_dir)
    findings += check_stencil_reachability(materials_dir)
    findings += check_feature_installed(root, materials_dir)
    findings += check_renderer_feature_parity(root)
    findings += check_gui_claims(root)
    findings += check_alpha_clip_without_alpha(root, materials_dir)
    findings += check_leftover_properties(root, materials_dir)
    findings += check_pass_keyword_use(root)
    findings += check_srp_batcher(root)
    findings += check_cs_property_names(root)
    findings += check_doc_feature_names(root)
    findings += check_menu_paths(root)
    findings += check_depth_texture_required(root, materials_dir)
    findings += check_pinned_to_max(root, materials_dir)
    findings += check_unused_pass_cost(root, materials_dir)
    findings += check_motionvectors_disabled(root, materials_dir)
    findings += check_surface_type_by_name(materials_dir)
    findings += check_atan2_guard(root)
    findings += check_shadow_flicker(root, materials_dir)

    if materials_dir is None:
        return findings

    maskmap_index = build_maskmap_index(materials_dir, root.resolve().parent)

    mats = find_materials(materials_dir)
    if not mats:
        findings.append(Finding("warning", str(materials_dir), "マテリアルが見つからない",
                                "パスを確認すること。"))
        return findings

    for path in mats:
        values = dict(defaults)
        values.update(read_material(path))       # マテリアルが既定を上書きする
        kw = keywords_of(path)
        name = path.stem

        findings += check_shadow_band(values, name)
        findings += check_face_sdf_reachable(values, name, kw)
        findings += check_shadow_contrast(values, name)
        findings += check_diffuse_reach(values, name, defaults)
        findings += check_pcss(values, name, "_HQ_SHADOW_ON" in kw, defaults)
        findings += check_alpha_clip(path, kw, values, name)
        findings += check_maskmap_packing(materials_dir, values, name, maskmap_index)

        # Range は**マテリアルに書かれている値だけ**見る。
        # 既定値とマージした values を渡すと、シェーダー側の既定が
        # 範囲外だった場合に 46 件へ増幅されて読めなくなる。
        findings += check_ranges(read_material(path), ranges, name)

        # **キーワードは .mat に書かれている値だけで判定する。**
        # 既定値とマージすると、シェーダー側の既定が ON のプロパティで
        # 「キーワードが無い」と全件に出てしまう。
        findings += check_toggle_keywords(read_material(path), kw, toggles, name)

        # ファイルとしての健全性。値の検算より前に壊れていたら意味が無い。
        findings += check_material_integrity(path)

    return findings


def main() -> int:
    ap = argparse.ArgumentParser(description="シェーダーの式を実際の値で検算する")
    ap.add_argument("root", nargs="?", default=".", help="ToonPBR.shader のあるディレクトリ")
    ap.add_argument("--materials", help=".mat を並べたディレクトリ")
    ap.add_argument("--strict", action="store_true", help="警告も失敗扱いにする")
    ap.add_argument("--cost", action="store_true",
                    help="1画素あたりのテクスチャフェッチ数を出す（合否には影響しない）")
    ap.add_argument("--variants", action="store_true",
                    help="パスごとのバリアント数を出す（合否には影響しない）")
    args = ap.parse_args()

    mats = Path(args.materials) if args.materials else None

    # 定数がソースから読めないのは**検査そのものが成立しない**状態。
    # スタックトレースではなく、何を直せばいいかを出して落とす。
    try:
        if args.cost:
            report_cost(Path(args.root), mats)
        if args.variants:
            report_variants(Path(args.root))

        findings = run(Path(args.root), mats)
    except (FileNotFoundError, ValueError) as e:
        print("error: 検査を実行できない")
        print(f"    {e}")
        return 1

    # 同じ指摘が 46 マテリアルぶん並ぶと読めないので、内容でまとめる。
    grouped: dict[tuple[str, str, str], list[str]] = {}
    for f in findings:
        grouped.setdefault((f.level, f.title, f.detail), []).append(f.where)

    errors = warnings = 0
    for (level, title, detail), wheres in grouped.items():
        if level == "error":
            errors += len(wheres)
        else:
            warnings += len(wheres)

        head = wheres[0] if len(wheres) == 1 else f"{len(wheres)} 件（{wheres[0]} ほか）"
        print(f"{level}: {title}  [{head}]")
        print(f"    {detail}")

    total = errors + warnings
    print(f"\n検算: エラー {errors} 件 / 警告 {warnings} 件")

    if errors:
        return 1
    if warnings and args.strict:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""シェーダーと ShaderGUI から**プロパティ一覧を生成する**。

`README_ToonPBR.md` は「セットアップと数値レシピ」で、表示名で 84 / 189 を
説明している。残りには説明が無い ── だが**手で書き足すと意味を捏造する。**
値域と既定はシェーダーに、説明は GUI の tooltip に既にあるので、
そこから機械的に組み立てる。

**生成物は書き換えないこと。** 手を入れても次の生成で消える。
説明を足したいときは `ToonPBRShaderGUI.cs` の tooltip を書く ──
そうすればインスペクタと文書の両方に同時に効く。

使い方:
    python gen_properties.py            # 標準出力へ
    python gen_properties.py --write    # PROPERTIES.md を書く
    python gen_properties.py --check    # 最新かどうかだけ見る（差分があれば 1）
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "PROPERTIES.md"

PROP_RE = re.compile(
    r'^[ \t]*((?:\[[^\]]*\][ \t]*)*)(_\w+)[ \t]*\([ \t]*"([^"]*)"[ \t]*,[ \t]*'
    r'([^)]*(?:\([^)]*\))?[^)]*)\)[ \t]*=[ \t]*(.+?)[ \t]*$', re.M)

# GUI 側
METHOD_RE = re.compile(r"^\s*private void (Draw\w+)\(MaterialEditor e\)", re.M)
CASE_RE = re.compile(r"case (\d+): (Draw\w+)\(materialEditor\); break;")
SECTION_RE = re.compile(r'Section\("(\w+)",\s*\w+,\s*"([^"]*)",\s*"([^"]*)"\)')
SUBHEAD_RE = re.compile(r'SubHeader\("([^"]*)",\s*"([^"]*)"\)')
# **文字列の連結を扱えること。** tooltip は長くなると
# `"..." + "..."` と折り返して書かれる。単一のリテラルしか受け付けないと、
# **説明を足したプロパティほど一覧から消える**という逆立ちした挙動になる
# （実際 9 個が「GUI に出ていない」側へ落ちた）。
_STR = r'(?:"(?:[^"\\]|\\.)*"\s*\+\s*)*"(?:[^"\\]|\\.)*"'
DRAWPROP_RE = re.compile(
    rf'\bPv?\(e,\s*"(_\w+)",\s*({_STR}),\s*({_STR}|null),\s*({_STR}|null)\s*\)')


def unquote(expr: str) -> str:
    """C# の文字列式（連結を含む）を 1 つの文字列にする。"""
    if expr is None or expr == "null":
        return ""
    parts = re.findall(r'"((?:[^"\\]|\\.)*)"', expr)
    return "".join(parts).replace('\\"', '"').replace("\\\\", "\\")
TEXPROP_RE = re.compile(r'Prop\("(_\w+)"\)|DrawToggleWithTexture\(e, "(_\w+)", "(_\w+)"\)')


def find_shader() -> Path:
    hit = next(iter(sorted((HERE.parent).rglob("ToonPBRCommon.hlsl"))), None)
    base = hit.parent if hit else HERE
    for p in sorted(base.rglob("*.shader")):
        if re.search(r'^\s*Shader\s+"(?!Hidden/)',
                     p.read_text(encoding="utf-8", errors="replace"), re.M):
            return p
    raise SystemExit("主シェーダーが見つからない")


def find_gui() -> Path:
    for parent in HERE.parent.rglob("ToonPBRShaderGUI.cs"):
        return parent
    raise SystemExit("ToonPBRShaderGUI.cs が見つからない")


def read_props(shader: Path) -> list[dict]:
    text = shader.read_text(encoding="utf-8", errors="replace")
    i = text.index("Properties")
    j = text.index("{", i)
    depth, k = 0, j
    while k < len(text):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                break
        k += 1
    out = []
    hidden = 0
    for m in PROP_RE.finditer(text[j + 1:k]):
        attrs, name, disp, typ, default = m.groups()
        # **`[HideInInspector]` は一覧に載せない。** この文書は「利用者が
        # 設定するもの」の一覧で、並び順も GUI が描く順から作る。
        # GUI に出ないものは置き場が無く、取りこぼし扱いで生成が止まる。
        #
        # ただし**黙って消さない。** 取りこぼしの検査は「正規表現が壊れて
        # プロパティが静かに消える」ことを見るためにあるので、
        # 除外した数は必ず出す（0 と「見ていない」を混ぜないため）。
        #
        # SRP Batcher のために Properties へ出す必要はあるが、
        # UI には出さない値がこれ（`_HeadForward` など。T-338）。
        if "HideInInspector" in attrs:
            hidden += 1
            continue
        out.append({"name": name, "display": disp.strip(), "type": typ.strip(),
                    "default": default.strip(),
                    "variant": "Toggle(" in attrs or "KeywordEnum" in attrs})
    if hidden:
        print(f"  （`[HideInInspector]` の {hidden} 個は一覧から外した"
              f" ── スクリプトが設定する値）")
    return out


def read_gui(gui: Path) -> tuple[dict[str, tuple], list[str]]:
    """プロパティ名 -> (タブ, セクション, 英 tooltip, 日 tooltip)。"""
    text = gui.read_text(encoding="utf-8", errors="replace")

    tabs_en = re.search(r"s_TabsEn\s*=\s*\{([^}]*)\}", text)
    tabs_jp = re.search(r"s_TabsJp\s*=\s*\{([^}]*)\}", text)
    en = re.findall(r'"([^"]*)"', tabs_en.group(1)) if tabs_en else []
    jp = re.findall(r'"([^"]*)"', tabs_jp.group(1)) if tabs_jp else []
    tab_names = [f"{j}（{e}）" for e, j in zip(en, jp)] or en

    # タブ番号 -> 直接呼ぶメソッド
    tab_of: dict[str, str] = {}
    for num, method in CASE_RE.findall(text):
        idx = int(num)
        tab_of[method] = tab_names[idx] if idx < len(tab_names) else f"タブ{idx}"

    # DrawTabX が呼ぶ DrawY も同じタブに属する
    for method, tab in list(tab_of.items()):
        body = re.search(rf"private void {method}\(MaterialEditor e\)\s*\{{(.*?)\n        \}}",
                         text, re.DOTALL)
        if body:
            for callee in re.findall(r"\b(Draw\w+)\(e\);", body.group(1)):
                tab_of.setdefault(callee, tab)

    # **行単位で走査しないこと。** `P(e, "_X", "Label",` は 3〜4 行に折り返して
    # 書かれているものが多く、1 行ずつ正規表現を当てると**ほとんど取りこぼす**
    # （最初そう書いて `_AlphaClipOn` などが丸ごと欠けた）。
    # 出現位置（オフセット）を集めて順に畳む。
    events: list[tuple[int, str, tuple]] = []
    for m in METHOD_RE.finditer(text):
        events.append((m.start(), "method", (m.group(1),)))
    for m in SECTION_RE.finditer(text):
        events.append((m.start(), "section", (f"{m.group(3)}（{m.group(2)}）",)))
    for m in SUBHEAD_RE.finditer(text):
        events.append((m.start(), "sub", (m.group(2),)))
    for m in DRAWPROP_RE.finditer(text):
        name, _label, tip_en, tip_jp = m.groups()
        events.append((m.start(), "prop",
                       (name, unquote(tip_en), unquote(tip_jp))))
    for m in TEXPROP_RE.finditer(text):
        for name in (m.group(1), m.group(2), m.group(3)):
            if name:
                events.append((m.start(), "tex", (name,)))
    events.sort(key=lambda e: e[0])

    result: dict[str, tuple] = {}
    cur_method = cur_section = base_section = ""
    for _pos, kind, payload in events:
        if kind == "method":
            cur_method, cur_section, base_section = payload[0], "", ""
        elif kind == "section":
            cur_section = base_section = payload[0]
        elif kind == "sub":
            cur_section = f"{base_section} ／ {payload[0]}".strip(" ／")
        elif kind == "prop":
            name, tip_en, tip_jp = payload
            result[name] = (tab_of.get(cur_method, "?"), cur_section, tip_en, tip_jp)
        elif kind == "tex":
            name = payload[0]
            if name not in result:
                result[name] = (tab_of.get(cur_method, "?"), cur_section, "", "")
    return result, tab_names


def build() -> tuple[str, int]:
    shader = find_shader()
    gui = find_gui()
    props = read_props(shader)
    placed, tab_names = read_gui(gui)

    lines = [
        "# プロパティ一覧（自動生成）",
        "",
        "**この文書は生成物です。手で書き換えても次の生成で消えます。**",
        "説明を足したいときは `ToonPBRShaderGUI.cs` の tooltip を書いてください ──",
        "インスペクタと文書の両方に同時に効きます。",
        "",
        "```bash",
        "python gen_properties.py --write",
        "```",
        "",
        f"シェーダー: `{shader.name}` / プロパティ {len(props)} 個",
        "",
        "⚡ はシェーダーバリアントを生むもの（マテリアル間で値が違うとバッチが分断される）。",
        "",
    ]

    by_tab: dict[str, dict[str, list[dict]]] = {}
    orphans: list[dict] = []
    for p in props:
        got = placed.get(p["name"])
        if got is None:
            orphans.append(p)
            continue
        tab, section, tip_en, tip_jp = got
        p = {**p, "tip_en": tip_en, "tip_jp": tip_jp}
        by_tab.setdefault(tab, {}).setdefault(section or "(節なし)", []).append(p)

    order = {name: i for i, name in enumerate(tab_names)}
    for tab in sorted(by_tab, key=lambda t: order.get(t, 99)):
        lines += [f"## {tab}", ""]
        for section, items in by_tab[tab].items():
            lines += [f"### {section}", "",
                      "| プロパティ | 表示名 | 型 | 既定 | 説明 |",
                      "| :--- | :--- | :--- | :--- | :--- |"]
            for p in items:
                mark = " ⚡" if p["variant"] else ""
                tip = p["tip_jp"].replace("|", "\\|") or "—"
                lines.append(
                    f"| `{p['name']}`{mark} | {p['display'] or '—'} | "
                    f"`{p['type']}` | `{p['default']}` | {tip} |")
            lines.append("")

    if orphans:
        lines += ["## GUI に出ていないもの", "",
                  "**インスペクタから触れません。** W104 が拾うはずのもの。", ""]
        for p in orphans:
            lines.append(f"- `{p['name']}` — {p['display']}")
        lines.append("")

    no_tip = sum(1 for t in by_tab.values() for s in t.values()
                 for p in s if not p["tip_jp"])
    total = sum(len(s) for t in by_tab.values() for s in t.values())
    lines += [
        "---",
        "",
        f"説明のあるもの {total - no_tip} / {total}。"
        f"**残り {no_tip} 個は tooltip が書かれていない** ── "
        "`ToonPBRShaderGUI.cs` に足すとここにも出ます。",
        "",
    ]
    return "\n".join(lines), len(orphans)


def main() -> int:
    ap = argparse.ArgumentParser(description="プロパティ一覧を生成する")
    # 他の道具と同じ渡し方（第 1 引数にツリー）を受け付ける。
    # ここが無いと `smoke_tools.py` からの呼び出しが argparse エラーで落ちる
    # ── しかもトレースバックが出ないので**疎通試験は OK と報告していた**（T-268）。
    # 対象は ToonPBRCommon.hlsl から自力で見つけるので、値は使わない。
    ap.add_argument("root", nargs="?", default=None, help="（互換のため受け取るだけ）")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    text, orphans = build()

    # **取りこぼしを黙って書かない。** W104 が「GUI が触っていないプロパティ」を
    # 見ているので、このツリーで取りこぼしが出たら**生成側の正規表現が壊れている。**
    # 実際、tooltip を文字列連結で書いた 9 個が拾えなくなった ──
    # **説明を足したプロパティほど一覧から消える**という逆立ちした挙動だった。
    if orphans:
        print(f"error: {orphans} 個を配置できなかった。"
              f" W104 が通っているなら**生成側の正規表現が壊れている** ── "
              f"tooltip の書き方（文字列連結など）を追えていない可能性が高い")
        return 1

    if args.check:
        cur = OUT.read_text(encoding="utf-8", errors="replace") if OUT.exists() else ""
        if cur.replace("\r\n", "\n") == text:
            print(f"PROPERTIES.md は最新（{OUT.name}）")
            return 0
        print("error: PROPERTIES.md が古い。`python gen_properties.py --write` で更新すること")
        return 1
    if args.write:
        OUT.write_text(text, encoding="utf-8", newline="")
        print(f"{OUT} を書いた（{len(text.splitlines())} 行）")
        return 0
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())

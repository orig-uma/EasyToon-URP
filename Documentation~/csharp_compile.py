#!/usr/bin/env python3
"""Unity を起動せずに C# を**実コンパイル**する。

Unity は Roslyn を同梱している（`Editor/Data/DotNetSdkRoslyn/csc.dll`）。
参照アセンブリも `Editor/Data/Managed/` と `Library/ScriptAssemblies/` に
そのまま置いてあるので、**Unity が使うのと同じ型で**コンパイルできる。

**なぜ要るか。** `hlsl_compile.py` が HLSL 側を埋めた後も、C# だけは
Editor が再コンパイルするまで一切検証できなかった。実際そこで
**文字列リテラルが行内で閉じていない**という初歩的な壊し方を通してしまい、
Editor がリフレッシュするまで 20 分気付けなかった（T-231）。

使い方:
    cd Assets/ToonPBR
    python csharp_compile.py              # エラーのみ
    python csharp_compile.py --warnings   # 警告も出す

**Unity 自身の応答ファイルを使う。**

`Library/Bee/artifacts/*.dag/<アセンブリ名>.rsp` に、Unity が実際に csc へ
渡した引数がそのまま残っている。参照・定義・言語バージョンを推測せずに
**同じ条件で**回せる。

**どの応答ファイルかは名前でなく中身で決める。** 対象の .cs を実際に
列挙しているものを採る ── アセンブリ名の表はツリーを動かすたびに腐り、
実際 `Assembly-CSharp*` を名指ししていたせいでパッケージへ移した後
**1 ファイルも検査しないまま「エラー 0 件」**を 3 回返した（T-257）。

**推測した参照集合は Unity より寛容だった**（T-241）──
`FindObjectsByType<T>(FindObjectsInactive)` という存在しないオーバーロードを
通してしまい、Editor だけが CS1503 で落ちた。応答ファイルを使う形にして
**同じ壊し方を正しく捕まえることを確認した**（T-242）。

応答ファイルが無いとき（Unity で一度も開いていない）は自前の参照集合に落ちる。
そのときは**「エラー 0 件」が保証にならない**ので、出力にそう書く。

**応答ファイルが古いと、新しいファイルが 1 つも検査されない。**
載っていないものは名指しで警告する ── 黙って通るのが一番危ない。

**Unity のコンパイルと完全に同じではない。** 違い:

  - `asmdef` を読んでいない。応答ファイルが使えるときは Unity の分け方に
    そのまま従うが、使えないときは**このツリーの .cs をまとめて 1 つ**にする
  - `Assembly-CSharp` / `Assembly-CSharp-Editor` は参照から外す
    （自分自身が入っているので、参照すると型が二重定義になる）
  - 定義シンボル（`UNITY_EDITOR` など）はここで明示する
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import tempfile
from pathlib import Path

# Unity が Editor スクリプトに与える定義。**足りないと `#if` の中が消える。**
DEFINES = ["UNITY_EDITOR", "UNITY_EDITOR_WIN", "UNITY_64",
           "UNITY_STANDALONE_WIN", "UNITY_STANDALONE",
           "UNITY_6000_3", "UNITY_6000", "UNITY_2023_1_OR_NEWER",
           "UNITY_2022_1_OR_NEWER", "UNITY_2021_1_OR_NEWER",
           "UNITY_2020_1_OR_NEWER", "UNITY_2019_1_OR_NEWER"]

# 自分自身を含むので参照しない（型の二重定義になる）
SKIP_ASSEMBLIES = {"Assembly-CSharp.dll", "Assembly-CSharp-Editor.dll"}

MSG_RE = re.compile(r"^(?P<path>[^(]+)\((?P<line>\d+),(?P<col>\d+)\): "
                    r"(?P<sev>error|warning) (?P<code>CS\d+): (?P<msg>.*)$")


def find_unity_data() -> Path:
    """Unity のインストール先。ProjectVersion.txt のバージョンを優先して探す。"""
    roots = [Path("C:/Program Files/Unity/Hub/Editor"),
             Path("C:/Program Files/Unity/Editor")]
    cands: list[Path] = []
    for r in roots:
        if not r.is_dir():
            continue
        for d in sorted(r.iterdir(), reverse=True):
            data = d / "Editor" / "Data"
            if data.is_dir():
                cands.append(data)
            elif (d / "Data").is_dir():
                cands.append(d / "Data")
    if not cands:
        raise FileNotFoundError("Unity のインストールが見つからない")
    return cands[0]


def find_project(root: Path) -> Path:
    for parent in root.parents:
        if (parent / "Library" / "ScriptAssemblies").is_dir():
            return parent
    raise FileNotFoundError(
        "Library/ScriptAssemblies が見つからない"
        "（一度 Unity でプロジェクトを開くこと）")


def scan_roots(root: Path) -> list[Path]:
    """C# を探す場所。**シェーダーのツリーの外にあることがある。**

    パッケージへ移すと C# は `Editor/<名前>/` と `Runtime/Scripts/<名前>/` へ行き、
    シェーダーの下から消える。root の下だけを見ると **.cs が 0 個**になり、
    この道具は「0 ファイル / エラー 0 件」で**通ったふりをする**（T-257）。
    `editor_log_check.py` が同じ穴を踏んでいて（T-253）、そちらだけ塞いでいた。

    **同名の部屋だけ**を見ること。単にパッケージルートを足すと、
    隣のシェーダーの C# まで自分のものとして判定する。
    """
    roots = [root.resolve()]
    name = root.resolve().name
    for parent in root.resolve().parents:
        if (parent / "package.json").exists():
            roots += [d for d in parent.rglob(name)
                      if d.is_dir() and d != root.resolve()]
            break
    return roots


def rsp_sources(rsp: Path) -> set[str]:
    """応答ファイルが列挙している .cs。

    **表記はプロジェクト相対**（`Packages/com.origuma.easytoon-urp/Editor/...`）。
    絶対パスと突き合わせると 1 件も当たらないまま「エラー 0 件」になるので、
    呼ぶ側も相対で揃えること。
    """
    return {l.strip().strip('"').replace("\\", "/")
            for l in rsp.read_text(encoding="utf-8", errors="replace").splitlines()
            if l.strip().startswith('"') and l.strip().endswith('.cs"')}


def find_unity_rsp(project: Path, targets: set[str]) -> list[Path]:
    """**Unity 自身がコンパイルに使った応答ファイル。**

    Bee が `Library/Bee/artifacts/*.dag/<アセンブリ名>.rsp` に残している。
    参照・定義・言語バージョンが**そのまま**入っているので、これを使えば
    「Unity と同じ条件」を推測せずに再現できる。

    自前で組んだ参照集合は**Unity より寛容になることがあった**（T-241）。
    存在しないオーバーロードを通してしまい、Editor だけが CS1503 で落ちた。
    応答ファイルを使えばその食い違いは原理的に起きない。

    **アセンブリ名で探さないこと。** 以前は `Assembly-CSharp*.rsp` を名指しで
    見ていたが、パッケージへ移すと asmdef 側（`Origuma.EasyToon.URP.Editor`）に
    変わり、**1 ファイルも当たらないまま「エラー 0 件」を返した**（T-257）。
    名前の表はツリーを動かすたびに腐るので、**中身で選ぶ** ──
    対象の .cs を実際に列挙している応答ファイルだけを採る。
    """
    dag = project / "Library" / "Bee" / "artifacts"
    if not dag.is_dir() or not targets:
        return []

    # 同じアセンブリが複数の dag に残る。新しいほうだけを採る。
    best: dict[str, Path] = {}
    for d in sorted(dag.glob("*.dag")):
        for f in sorted(d.glob("*.rsp")):
            # `*.dll.mvfrm.rsp` は別物（参照アセンブリの検証用）
            if f.name.endswith(".dll.mvfrm.rsp"):
                continue
            if not (rsp_sources(f) & targets):
                continue
            prev = best.get(f.name)
            if prev is None or f.stat().st_mtime > prev.stat().st_mtime:
                best[f.name] = f
    return [best[k] for k in sorted(best)]


def run_unity_rsp(rsp: Path, project: Path, out: Path,
                  host: Path, csc: Path,
                  extra_sources: list[str] | None = None) -> subprocess.CompletedProcess:
    """応答ファイルを回す。**出力先と、追加するソース以外は 1 行も変えない。**

    `extra_sources` は**まだ Unity が見ていない新規ファイル**を足すためのもの。
    ファイルを作った直後は応答ファイルに載っておらず、そのままでは
    **新規ファイルだけが検査から漏れる** ── しかも「11 ファイル / エラー 0 件」と
    出るので、通ったように見える（T-278）。載っていないものを足して回せば、
    Unity のリフレッシュを待たずに確かめられる。
    """
    lines = []
    for line in rsp.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith("-out:"):
            lines.append(f'-out:"{out}"')
        elif line.startswith("-refout:"):
            continue                      # 参照アセンブリは要らない
        else:
            lines.append(line)
    for src in extra_sources or []:
        lines.append(f'"{src}"')
    tmp = out.with_suffix(".rsp")
    tmp.write_text("\n".join(lines), encoding="utf-8", newline="")
    return subprocess.run([str(host), str(csc), f"@{tmp}"],
                          capture_output=True, text=True,
                          encoding="utf-8", errors="replace", cwd=str(project))


def build_rsp(sources: list[Path], project: Path, data: Path, out: Path) -> tuple[Path, int]:
    if not sources:
        raise FileNotFoundError("対象の .cs が無い")

    lines = ["/target:library", "/nologo", "/nostdlib+", "/langversion:9.0",
             f"/out:{out}"]
    lines += [f"/define:{d}" for d in DEFINES]

    netstd = data / "NetStandard" / "ref" / "2.1.0" / "netstandard.dll"
    if not netstd.exists():
        found = list((data / "NetStandard").rglob("netstandard.dll"))
        if not found:
            raise FileNotFoundError("netstandard.dll が見つからない")
        netstd = found[0]
    lines.append(f'/reference:"{netstd}"')

    # **モジュール版だけを参照する。** 一枚岩の UnityEditor.dll と併せると
    # `EditorWindow` などが二重定義になり、CS0433 で全滅する。
    for d in (data / "Managed" / "UnityEngine", data / "Managed" / "UnityEditor"):
        for f in sorted(d.glob("*.dll")) if d.is_dir() else []:
            lines.append(f'/reference:"{f}"')

    for f in sorted((project / "Library" / "ScriptAssemblies").glob("*.dll")):
        if f.name in SKIP_ASSEMBLIES:
            continue
        lines.append(f'/reference:"{f}"')

    for s in sources:
        lines.append(f'"{s}"')

    rsp = out.with_suffix(".rsp")
    rsp.write_text("\n".join(lines), encoding="utf-8", newline="")
    return rsp, len(sources)


def parse_messages(outputs: list[str], targets_abs: set[str], targets_rel: set[str],
                   exact: bool) -> tuple[list[str], list[str]]:
    """csc の出力から、**対象ファイルの**エラーと警告だけを取り出す。

    応答ファイルはアセンブリ全体を通すので、隣のシェーダーの C# の指摘も混ざる。
    そこを絞る必要があるが、**絞りすぎると全部消えて「エラー 0 件」になる。**

    **csc は応答ファイルに書かれたとおりの表記で出す。** Unity の応答ファイルは
    プロジェクト相対なので出力も相対で来る。絶対パスの集合とだけ突き合わせて
    いた時期があり、注入した誤りが素通りした（T-257）。どちらの表記でも拾う。

    関数として切り出してあるのは、`self_test.py` がここだけを直接撃てるように
    するため ── この道具は文字列注入では試せない（実プロジェクトが要る）。
    """
    errors: list[str] = []
    warns: list[str] = []
    for text in outputs:
        for line in text.splitlines():
            m = MSG_RE.match(line.strip())
            if not m:
                continue
            path = m.group("path").replace("\\", "/")
            if exact and path not in targets_abs and path not in targets_rel:
                continue
            (errors if m.group("sev") == "error" else warns).append(
                f"{m.group('path')}({m.group('line')}) {m.group('code')}: {m.group('msg')}")
    return errors, warns


def main() -> int:
    ap = argparse.ArgumentParser(description="Unity 無しで C# を実コンパイルする")
    ap.add_argument("root", nargs="?", default=".", help="対象ツリー")
    ap.add_argument("--warnings", action="store_true", help="警告も出す")
    ap.add_argument("--strict", action="store_true", help="警告も失敗扱いにする")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    try:
        data = find_unity_data()
        project = find_project(root)
    except FileNotFoundError as e:
        print(f"error: {e}")
        return 2

    csc = data / "DotNetSdkRoslyn" / "csc.dll"
    host = data / "netcorerun" / "netcorerun.exe"
    if not csc.exists() or not host.exists():
        print(f"error: Roslyn が見つからない（{csc}）")
        return 2

    # **対象の .cs を先に確定させる。** root の下に無いこともある（パッケージ配置）。
    sources = sorted({p.resolve() for base in scan_roots(root)
                      for p in base.rglob("*.cs")})
    if not sources:
        # **0 件で通さない。** これを許すと、置き場所が変わっただけで
        # 検査が黙って空回りし、「エラー 0 件」と報告する（T-257）。
        print(f"error: **対象の .cs が 1 つも無い** ── {root} と同名の部屋を探したが見つからない")
        return 2

    # 応答ファイルはプロジェクト相対、コンパイラの出力は絶対。**両方持つ。**
    targets_rel = {p.relative_to(project).as_posix() for p in sources}
    targets_abs = {p.as_posix() for p in sources}

    # **Unity 自身の応答ファイルがあればそれを使う。** 推測した参照集合は
    # Unity より寛容になることがあり、実際に存在しないオーバーロードを
    # 通してしまった（T-241）。応答ファイルなら食い違いが原理的に起きない。
    unity_rsps = find_unity_rsp(project, targets_rel)
    outputs: list[str] = []
    exact = bool(unity_rsps)
    n = 0
    stale_files: list[str] = []

    if exact:
        # **どの応答ファイルにも載っていない新規ファイルを先に洗い出す。**
        listed_all: set[str] = set()
        for r in unity_rsps:
            listed_all |= rsp_sources(r)
        stale_files = [rel for rel in sorted(targets_rel) if rel not in listed_all]

        # 新規ファイルは**一番多く対象を含む応答ファイル**へ足して回す。
        # 作った直後は Unity が見ていないので、放っておくと
        # **新規ファイルだけが検査から漏れたまま「エラー 0 件」**になる（T-278）。
        host_idx = 0
        if len(unity_rsps) > 1:
            counts = [len(rsp_sources(r) & targets_rel) for r in unity_rsps]
            host_idx = counts.index(max(counts))

        with tempfile.TemporaryDirectory(prefix="toonpbr_csc_") as td:
            for i, r in enumerate(unity_rsps):
                extra = [str(project / s) for s in stale_files] if i == host_idx else None
                pr = run_unity_rsp(r, project, Path(td) / f"a{i}.dll", host, csc, extra)
                # **stderr も読む。** csc は普段 stdout に出すが、
                # 応答ファイル自体を読めないような失敗は stderr にしか出ない。
                outputs.append((pr.stdout or "") + "\n" + (pr.stderr or ""))
        n = len(sources)
    else:
        with tempfile.TemporaryDirectory(prefix="toonpbr_csc_") as td:
            out = Path(td) / "check.dll"
            try:
                rsp, n = build_rsp(sources, project, data, out)
            except FileNotFoundError as e:
                print(f"error: {e}")
                return 2
            pr = subprocess.run([str(host), str(csc), f"@{rsp}"],
                                capture_output=True, text=True,
                                encoding="utf-8", errors="replace")
            outputs.append(pr.stdout or "")

    errors, warns = parse_messages(outputs, targets_abs, targets_rel, exact)

    mode = "Unity の応答ファイル" if exact else "自前の参照集合"
    print(f"C# 実コンパイル（{mode}）: {n} ファイル / "
          f"エラー {len(errors)} 件 / 警告 {len(warns)} 件")
    if stale_files:
        # 検査はしている（応答ファイルへ足して回した）が、
        # **Unity 自身はまだ見ていない**ことは伝える。
        print(f"注記: 応答ファイルに未掲載の新規ファイル {len(stale_files)} 件を"
              f"足して検査した（Unity は未コンパイル）: {', '.join(stale_files[:3])}")
    if n == 0:
        # **0 ファイルで合格を返さない。** 「エラー 0 件」に見えるが、
        # 通ったのではなく**1 行も見ていない**。ツリーを動かすたびにこの状態に
        # なり、実際に 3 回続けて偽の合格を出した（T-257）。
        print("error: **1 ファイルも検査していない。** これは合格ではない ── "
              "応答ファイルが対象を 1 つも列挙していない（置き場所を変えた直後なら、"
              "Unity にフォーカスを戻してコンパイルさせること）")
        return 1
    if not errors and not exact:
        # **黙って合格にしない。** 自前の集合は Unity より寛容になることがある。
        print("  ただし**エラー 0 件は「Unity も通る」の保証にならない**"
              "（T-241 で実際に見逃した）。最終的な権威は Editor のログ。")
    for e in errors:
        print(f"error: {e}")
    if args.warnings:
        for w in warns:
            print(f"warning: {w}")
    elif warns:
        codes = sorted({w.split(":")[0].split()[-1] for w in warns})
        print(f"  （警告の内訳: {', '.join(codes)} ── --warnings で全部出る）")

    if errors:
        return 1
    if warns and args.strict:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

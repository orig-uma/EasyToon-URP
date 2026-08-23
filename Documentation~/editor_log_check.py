#!/usr/bin/env python3
"""Editor.log から実コンパイルの結果を読む。

batchmode が使えないとき（Editor が起動中だとグローバルキャッシュの
ロックで Unity 自体が落ちる・CLAUDE.md 参照）、**実コンパイルの結果を
得られる唯一の経路が Editor 自身のログ**になる。

ただし手で grep すると両方向に間違える:

  - ログは追記される。**何時間も前に直したエラーがそのまま残っている**
  - 逆に、編集直後でリフレッシュがまだなら「エラー 0」に見えるが、
    それは通ったのではなく**まだコンパイルしていない**だけ

後者のほうが危ない。静的検査の合格をコンパイルの合格と読み替えるのと
同じ誤りで、このプロジェクトが繰り返し踏んできた形（T-132 / T-166 / T-171）。

判定に要るのは 2 点だけ:

  1. 最後にソースを取り込んだリフレッシュはどこか → それ以降のエラーだけが現行
  2. それはソースの更新より後か           → 前なら「未検証」であって「合格」ではない

行番号による裏取りも入れてある。`ToonPBR.shader(792)` を指すエラーは、
現在のファイルが 575 行なら**その時点で古い**と確定できる（実際にこれで
分割前のエラーを切り分けた・T-221）。
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import time
from pathlib import Path

# 例: Shader error in 'Toon/URP/CharacterPBR': undeclared identifier '_X' at Assets/A/B.shader(792) (on d3d11)
SHADER_MSG_RE = re.compile(
    r"^Shader (error|warning) in '(?P<shader>[^']*)': (?P<msg>.*?)"
    r"(?: at (?P<path>[^()]+?)\((?P<line>\d+)\))?"
    r"(?: \(on (?P<api>\w+)\))?\s*$"
)
# 例: Assets/ToonPBR/Editor/Foo.cs(12,5): error CS0103: ...
CS_MSG_RE = re.compile(r"^(?P<path>[^(]+\.cs)\((?P<line>\d+),\d+\): (?P<sev>error|warning) (?P<msg>CS\d+: .*)$")
IMPORT_RE = re.compile(r"Start importing (?P<path>\S+) using Guid")
# C# のコンパイルが 1 回終わるたびに出る。**エラーはこの直前の区間に属する。**
COMPILE_RE = re.compile(r"^\s*CompileScripts: [\d.]+ms")

# **シェーダーと C# を分けて見る。** コンパイルする仕組みが別なので、
# 片方を編集しても**もう片方の結果は有効なまま**。まとめて「未検証」に
# 丸めると、証拠があるのに無いことにしてしまう ── 実際、C# を触っただけで
# シェーダーの合格が消える形になっていた（T-224）。
SOURCE_KINDS = {
    "シェーダー": (".hlsl", ".shader"),
    "C#": (".cs",),
}
SOURCE_SUFFIXES = tuple(s for v in SOURCE_KINDS.values() for s in v)

# 検査コード。**self_test の網羅率がこの表を数える。**
# 検査名を試験側に書き写すと、検査を足したときに古くなる（T-167 / T-168）。
CODES = {
    "L001": "最後の取り込み以降に出ている、現行のコンパイルエラー",
    "L002": "古い指摘（直したのにログに残っている分）の除外",
    "L003": "このツリーを取り込んだ記録がログに無い",
    "L004": "ログがソースより古い ── 合格ではなく未検証",
}


def default_log_path() -> Path | None:
    """OS ごとの既定の Editor.log。見つからなければ None。"""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        p = Path(local) / "Unity" / "Editor" / "Editor.log"
        if p.exists():
            return p
    mac = Path.home() / "Library" / "Logs" / "Unity" / "Editor.log"
    if mac.exists():
        return mac
    lin = Path.home() / ".config" / "unity3d" / "Editor.log"
    if lin.exists():
        return lin
    return None


def scan_roots(root: Path) -> list[Path]:
    """走査する場所。**C# はツリーの外にあることがある。**

    パッケージへ移すと `Editor/<名前>/` と `Runtime/Scripts/<名前>/` へ行き、
    シェーダーの下から消える。root の下だけを見ると **C# の判定が
    出力から黙って消える** ── 「エラー 0 件」ですらなく、行ごと無くなるので
    見落としに気付けない（T-253）。

    **同名の部屋だけ**を見ること。単にパッケージルートを足すと、
    隣のシェーダーの C# まで自分のものとして判定する。
    """
    roots = [root]
    name = root.resolve().name
    for parent in root.resolve().parents:
        if (parent / "package.json").exists():
            roots += [d for d in parent.rglob(name)
                      if d.is_dir() and d != root.resolve()]
            # **鮮度はパッケージ全体で見る。**
            # Editor のログが記録しているのは**プロジェクト全体の取り込み**で、
            # シェーダー単位ではない。同名の部屋だけを見ていると、
            # **隣の Cel を触ったのに「ログの方が新しい＝検証済み」と読める。**
            # 判定を誤る方向（未検証を検証済みに見せる方向）なので広く取る。
            roots.append(parent)
            break
    return roots


def newest_source(root: Path, suffixes: tuple[str, ...] = SOURCE_SUFFIXES) -> tuple[Path | None, float]:
    """指定した拡張子のうち最後に更新されたものとその時刻。ログの鮮度判定に使う。"""
    best: Path | None = None
    best_mt = 0.0
    for base in scan_roots(root):
        for p in base.rglob("*"):
            if p.suffix in suffixes and p.is_file():
                mt = p.stat().st_mtime
                if mt > best_mt:
                    best, best_mt = p, mt
    return best, best_mt


def line_count(path: Path) -> int | None:
    try:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            return sum(1 for _ in f)
    except OSError:
        return None


def analyze(log: Path, root: Path, project_root: Path):
    """ログを読み、(現行の指摘, 古い指摘, 最後の取り込み位置) を返す。"""
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()

    # このツリーのシェーダー名を集める。パスを持たないエラー行の帰属に使う
    our_shaders = set()
    for sh in root.rglob("*.shader"):
        m = re.search(r'^\s*Shader\s+"([^"]+)"', sh.read_text(encoding="utf-8", errors="replace"), re.M)
        if m:
            our_shaders.add(m.group(1))

    rel_root = os.path.relpath(root, project_root).replace("\\", "/")

    def ours(path_str: str | None, shader: str | None) -> bool:
        if path_str and rel_root in path_str.replace("\\", "/"):
            return True
        return bool(shader and shader in our_shaders)

    # 1. 最後にこのツリーのファイルを取り込んだ行。
    #
    # **種別ごとに持つ。** 1 本にまとめていたせいで、
    # **C# のエラーの後にシェーダーを取り込んだだけで「古い」と判定**していた。
    # 実際それで本物の `error CS1010` を握り潰した（T-231）。
    # シェーダーの再取り込みは C# のエラーを何も直さない。
    last_import = -1                       # どれか（L003 の判定に使う）
    last_by_kind = {"シェーダー": -1, "C#": -1}
    compiles = [i for i, ln in enumerate(lines) if COMPILE_RE.match(ln)]
    for i, ln in enumerate(lines):
        m = IMPORT_RE.search(ln)
        if not m:
            continue
        path = m.group("path").replace("\\", "/")
        if rel_root not in path:
            continue
        last_import = i
        for kind, suffixes in SOURCE_KINDS.items():
            if path.endswith(suffixes):
                last_by_kind[kind] = i

    current, stale = [], []
    for i, ln in enumerate(lines):
        hit = None
        m = SHADER_MSG_RE.match(ln)
        if m and ours(m.group("path"), m.group("shader")):
            hit = {
                "sev": m.group(1),
                "text": ln.strip(),
                "path": m.group("path"),
                "line": int(m.group("line")) if m.group("line") else None,
            }
        else:
            m = CS_MSG_RE.match(ln)
            if m and ours(m.group("path"), None):
                hit = {
                    "sev": m.group("sev"),
                    "text": ln.strip(),
                    "path": m.group("path"),
                    "line": int(m.group("line")),
                }
        if not hit:
            continue

        # 2. 行番号による裏取り。今のファイルに無い行を指すなら古いと確定できる
        reason = None
        if hit["line"] and hit["path"]:
            f = project_root / hit["path"]
            n = line_count(f) if f.exists() else None
            if n is not None and hit["line"] > n:
                reason = f"{Path(hit['path']).name} は現在 {n} 行しかない（指摘は {hit['line']} 行目）"
        # **その種別の最後の取り込み**と比べる。シェーダーを取り込み直しても
        # C# のエラーは直らないし、その逆も同じ。
        kind = "C#" if (hit["path"] or "").endswith(".cs") else "シェーダー"
        hit["kind"] = kind

        if kind == "C#" and len(compiles) >= 1:
            # **C# はコンパイル事象で区切る。**
            #
            # 取り込み（`Start importing *.cs`）では駄目だった ── スクリプトは
            # 資産としての取り込みとアセンブリのビルドが別で、取り込みの記録は
            # 起動時のものしか残らない。**ログの更新時刻も当てにならない**
            # （シェーダーの取り込みで追記されるだけで C# は再ビルドされない）。
            #
            # **エラーはその直後のコンパイルに属する。** よって現行なのは
            # 「最後から2番目のコンパイル」以降に出ているものだけ。
            # 最後のコンパイルより前で切ると、**まさに失敗した回のエラーを
            # 消してしまう**（そのコンパイルの完了行はエラーより後に出るため）。
            prev = compiles[-2] if len(compiles) >= 2 else -1
            if reason is None and i <= prev:
                reason = "C# はその後に再コンパイルされている"
        else:
            cutoff = last_by_kind.get(kind, -1)
            if reason is None and cutoff >= 0 and i < cutoff:
                reason = f"{kind} の最後の取り込みより前に出ている"

        if reason:
            hit["why_stale"] = reason
            stale.append(hit)
        else:
            current.append(hit)

    return current, stale, last_import


def main() -> int:
    ap = argparse.ArgumentParser(description="Editor.log から実コンパイル結果を読む")
    ap.add_argument("root", nargs="?", default=".", help="検査対象ツリー（既定: カレント）")
    ap.add_argument("--log", help="Editor.log のパス（既定: OS ごとの標準位置）")
    ap.add_argument("--require-fresh", action="store_true",
                    help="ログがソースより古いとき失敗にする（CI 向け）")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    if not root.is_dir():
        print(f"error: ツリーが見つからない: {root}")
        return 2

    # プロジェクトルート（Assets の親）。ログ中の相対パスはここが基準
    # **`ProjectSettings` の有無まで条件にしない。** 自己診断のサンドボックスには
    # 無いので、そこで判定が外れて `rel_root` が "." になり、
    # **ツリー外のエラーまで自分のものとして拾ってしまう。**
    project_root = root
    for parent in root.parents:
        if (parent / "Assets").is_dir():
            project_root = parent
            break

    log = Path(args.log) if args.log else default_log_path()
    if log is None or not log.exists():
        print("error: Editor.log が見つからない。--log で指定するか、Unity を一度起動すること")
        return 2

    print(f"ログ: {log}")
    log_mt = log.stat().st_mtime
    print(f"  最終更新: {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(log_mt))}")

    current, stale_msgs, last_import = analyze(log, root, project_root)

    if last_import < 0:
        print("  [L003] このツリーのファイルを取り込んだ記録が無い")
    if stale_msgs:
        print(f"  [L002] 古い指摘を {len(stale_msgs)} 件除外した（例: {stale_msgs[-1]['why_stale']}）")

    print()
    for h in current:
        code = "L001" if h["sev"] == "error" else "L001w"
        print(f"{'error' if h['sev'] == 'error' else 'warning'}: [{code}] {h['text']}")

    errors = [h for h in current if h["sev"] == "error"]
    warns = [h for h in current if h["sev"] != "error"]

    # **種別ごとに判定する。** 「C# を触ったのでシェーダーも未検証」は誤り。
    # 逆も同じで、シェーダーだけ直したときに C# の合格を消す理由が無い。
    stale_kinds: list[str] = []
    for kind, suffixes in SOURCE_KINDS.items():
        src, src_mt = newest_source(root, suffixes)
        if src is None:
            # **黙って飛ばさない。** 種別ごと対象が無いのは、
            # 置き場所を取り違えている可能性が高い（T-253）。
            print(f"  {kind}: **対象が 1 つも無い** ── 置き場所が違うかもしれない")
            continue
        if src_mt > log_mt:
            stale_kinds.append(kind)
            rel = os.path.relpath(src, root).replace("\\", "/")
            print(f"  {kind}: **未検証** ── {rel} がログより新しい "
                  f"({time.strftime('%H:%M:%S', time.localtime(src_mt))} > "
                  f"{time.strftime('%H:%M:%S', time.localtime(log_mt))})")
        else:
            n = len([h for h in errors if h["path"] and h["path"].endswith(suffixes)])
            print(f"  {kind}: エラー {n} 件（ログのほうが新しい ── この結果は現行のもの）")

    if stale_kinds:
        print(f"未検証: [L004] Editor はまだ{'・'.join(stale_kinds)} の変更を見ていない")
        print("  → Unity にフォーカスを戻すとリフレッシュが走る。その後もう一度この検査を回すこと")
        print("  **エラー 0 件はコンパイルが通った証拠にならない。まだ走っていないだけ**")
        if errors:
            # **黙って通さない。** ログにエラーが出ていて、その後ソースが
            # 変わっている状態は「直したかもしれない」であって「直った」ではない。
            # ただし次の行動が読み取れないと、赤が常態になって読まれなくなる。
            print(f"  **ログには現行のエラーが {len(errors)} 件ある**（上を参照）")
            print("  ソースはその後で変わっているので、直っている可能性はある。")
            print("  → Unity にフォーカスを戻してリフレッシュさせ、もう一度この検査を回すこと。")
            print("     それまでは**直ったと見なさない。**")
            return 1
        if args.require_fresh:
            return 1
        return 0

    if errors:
        print(f"\n実コンパイル: エラー {len(errors)} 件 / 警告 {len(warns)} 件")
        return 1

    print(f"実コンパイル: エラー 0 件 / 警告 {len(warns)} 件"
          f"（最後の取り込み以降・ログはソースより新しい）")
    # **カバー範囲を毎回書く。** ここを黙ると「57 組通った」と読まれる。
    # ログに残るのは失敗だけで、成功したコンパイルは 1 行も出ない ──
    # つまり**何組通ったかは原理的に分からない**。
    # キーワードの組み合わせで初めて壊れる類は、ここには絶対に出ない
    # （T-085 で Hair パスが全滅したときも「0 errors」と報告された）。
    print("  ただし**これは Unity が実際に必要とした分だけ**。57 組のバリアント全部ではない。")
    print("  何組コンパイルされたかはログからは分からない（成功は記録されない）。")
    print("  キーワードの組み合わせでだけ壊れる不具合は、この経路では出ない。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""道具の入口が全部**動く**ことだけを確かめる（結果の正しさは見ない）。

**なぜ要るか。** このプロジェクトは同じ壊れ方を 3 回している:

    T-253  移動で C# の判定が出力から消えた（`root` の下だけを見ていた）
    T-257  応答ファイルをアセンブリ名で探していて 0 ファイル検査
    T-259  `--branch-cost` が `ToonPBR.shader` を決め打ちしており、
           T-249 の改名以来ずっと FileNotFoundError で落ちていた

**T-259 は「誰も回していなかった」ことが本体。** `--branch-cost` は
`check.py` からも自己診断からも呼ばれない枝で、壊れても誰も気付かない。
何セッションも落ちたまま放置され、判断材料になるはずの数字が
ずっと取れていなかった。

存在しないファイル名を静的に探す形も試したが、**3 件のうち 1 件しか捕まらない**
（T-257 の `Assembly-CSharp-Editor.rsp` は実在するファイルで、
このレイアウトに合わないだけだった）。3 件に共通するのは
「その経路を誰も走らせていない」ことなので、**全部走らせる**のが直接の対策。

見るのは1つだけ ── **Python のトレースバックが出ていないこと。**
指摘が出て exit 1 になるのは正常（このスクリプトの関心事ではない）。

使い方:
    python smoke_tools.py <root> [--materials <dir>]
"""
from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent

# (道具, 追加の引数)。`{mats}` はマテリアルのディレクトリに置き換わる。
ENTRIES: list[tuple[str, list[str]]] = [
    ("shader_lint.py",      []),
    ("shader_lint.py",      ["--strict"]),
    ("param_check.py",      []),
    ("param_check.py",      ["--materials", "{mats}"]),
    ("param_check.py",      ["--variants"]),
    ("param_check.py",      ["--cost"]),
    ("hlsl_compile.py",     []),
    ("hlsl_compile.py",     ["--cost"]),
    ("hlsl_compile.py",     ["--branch-cost"]),
    ("hlsl_compile.py",     ["--branch-cost", "--materials", "{mats}"]),
    ("csharp_compile.py",   []),
    ("csharp_compile.py",   ["--warnings"]),
    ("editor_log_check.py", []),
    ("gen_properties.py",   ["--check"]),
]

# **走らせていないものを黙らない。** ここに書いていない入口は無検査。
NOT_COVERED = [
    ("verify_variants.py", "Unity の batchmode が要る（Editor 起動中は起動できない）"),
    ("hlsl_compile.py --variants", "176 プログラムで 140 秒。--full のときだけ回る"),
    ("rename_shader.py", "ツリーを書き換えるので疎通では回せない"),
    ("self_test.py", "このスクリプトを含む検査側。二重に回さない"),
]

TRACEBACK = "Traceback (most recent call last)"


def run(tool: str, args: list[str], root: Path,
        mats: Path | None) -> tuple[bool, str, float]:
    """(トレースバックが無いか, 要約, 秒)。"""
    argv = [sys.executable, str(HERE / tool), str(root)]
    for a in args:
        argv.append(str(mats) if a == "{mats}" else a)

    t0 = time.monotonic()
    try:
        pr = subprocess.run(argv, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=300)
    except subprocess.TimeoutExpired:
        return False, "300 秒で終わらない（無限ループの疑い）", time.monotonic() - t0
    dt = time.monotonic() - t0

    out = (pr.stdout or "") + (pr.stderr or "")
    # **argparse のエラーはトレースバックを出さない。** 引数の渡し方が
    # 合っていない呼び出しを「OK」と報告していた（T-268）── 呼べていないのに
    # 疎通したことになるので、この試験の目的そのものが崩れる。
    if out.lstrip().startswith("usage:"):
        first = next((l.strip() for l in out.splitlines()
                      if l.strip().startswith(("error:", tool))), "引数が合わない")
        return False, f"**呼べていない** {first[:100]}", dt
    if TRACEBACK in out:
        # 最後の例外行だけを見せる。全文は読ませない
        last = [l.strip() for l in out.splitlines() if l.strip()][-1]
        return False, f"**落ちた** {last[:100]}", dt
    if not out.strip():
        # **何も言わない道具は疑う。** 入口が空振りしていても exit 0 になる
        return False, "**何も出力しない**（入口が空振りしている疑い）", dt
    return True, f"exit={pr.returncode}", dt


def main() -> int:
    ap = argparse.ArgumentParser(description="道具の入口が動くことだけを確かめる")
    ap.add_argument("root", nargs="?", default=".")
    ap.add_argument("--materials", default=None)
    args = ap.parse_args()

    root = Path(args.root).resolve()
    mats = Path(args.materials).resolve() if args.materials else None
    if mats is not None and not mats.is_dir():
        print(f"error: --materials のディレクトリが無い: {mats}")
        return 2

    entries = ENTRIES
    skipped_mats = 0
    if mats is None:
        skipped_mats = sum(1 for _, a in entries if "{mats}" in a)
        entries = [(t, a) for t, a in entries if "{mats}" not in a]

    bad: list[str] = []
    total = 0.0
    for tool, extra in entries:
        ok, note, dt = run(tool, extra, root, mats)
        total += dt
        # `{mats}` のまま出すと、何を渡したのか読めない
        shown = ["<materials>" if a == "{mats}" else a for a in extra]
        label = f"{tool} {' '.join(shown)}".strip()
        mark = "OK  " if ok else "失敗"
        print(f"  {mark} {label:<52}{dt:>6.1f}s  {note}")
        if not ok:
            bad.append(f"{label} — {note}")

    # --- 作業ディレクトリで結果が変わらないか -----------------------------
    #
    # **道具は「どこから回しても同じ」でなければならない。**
    # 相対パスの扱いを間違えると、`Documentation~` から回したときだけ
    # 正しく、別の場所からだと**黙って何もしない**、という状態になる
    # ── 実際にパスを解決し忘れて 1 件そうなっていた（T-311）。
    #
    # 全部を回すと遅いので、いちばん経路の多い `param_check` だけを
    # 2 か所から回して出力を突き合わせる（1 回 3.5 秒）。
    if mats is not None:
        import tempfile

        def once(cwd: Path) -> str:
            proc = subprocess.run(
                [sys.executable, str(HERE / "param_check.py"), str(root),
                 "--materials", str(mats)],
                cwd=str(cwd), capture_output=True, text=True,
                encoding="utf-8", errors="replace")
            return (proc.stdout or "") + (proc.stderr or "")

        # **同じ場所から 2 回。** Python は文字列のハッシュを実行ごとに
        # 変えるので、集合や辞書を**並べ替えずに**出力へ流していると
        # 走らせるたびに順や代表例が入れ替わる。差分を追えなくなるうえ、
        # 「直ったのか揺れているのか」が読めなくなる。
        first, again = once(HERE), once(HERE)
        if first != again:
            bad.append("param_check — **走らせるたびに結果が変わる**"
                       "（集合や辞書を並べ替えずに出力している）")
            print("  失敗 同じ状態でも 2 回で結果が違う")
        else:
            print("  OK   同じ状態なら何度回しても同じ結果")

        outs = [first, once(Path(tempfile.gettempdir()))]
        # 場所の欄には絶対パスが混じるので、行の頭（種類）だけで比べる
        def kinds(s: str) -> list[str]:
            return sorted(l.split("[")[0].strip() for l in s.splitlines()
                          if l.startswith(("error:", "warning:")))
        if kinds(outs[0]) != kinds(outs[1]):
            bad.append("param_check — **作業ディレクトリで結果が変わる**"
                       "（相対パスの解決漏れ。先に resolve() すること）")
            print("  失敗 作業ディレクトリを変えると結果が変わる")
        else:
            print(f"  OK   作業ディレクトリを変えても同じ結果"
                  f"（{len(kinds(outs[0]))} 件で一致）")

    print(f"\n入口の疎通: {len(entries)} 経路 / {len(entries) - len(bad)} 通過 "
          f"（{total:.0f} 秒）")
    if skipped_mats:
        print(f"  **--materials を渡していないので {skipped_mats} 経路は未検査。**")
    print("  ここで見ているのは**落ちないこと**だけ。結果の正しさは self_test.py の担当。")
    print("  **回していない入口:**")
    for name, why in NOT_COVERED:
        print(f"    - {name} … {why}")

    for b in bad:
        print(f"error: {b}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

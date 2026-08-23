#!/usr/bin/env python3
"""check.py — ToonPBR の検証をまとめて回す

検査が3つに分かれていて、どれを飛ばしても「通った」と誤解しうる:

  shader_lint.py    コードの**構造**（宣言漏れ・include 順・定義順・サンプラ本数）
  param_check.py    式が**実際の値**で成立しているか（帯の潰れ・守りの有無・Range）
  verify_variants   Unity での**実コンパイル**（56 組 × D3D/Vulkan × 頂点/フラグメント）

**この3つは互いの穴を埋め合っている。** 実際、静的検査が通ったまま
実コンパイルだけが落ちた例が2回ある（`c` の未宣言、関数の定義順）。
逆に、コンパイルが通ったまま値の関係が壊れていた例も複数ある（T-108 / T-113）。

使い方:
  cd Assets/ToonPBR
  python check.py                       # 静的検査 + 値の検算（数秒）
  python check.py --unity "<Unity.exe>" # 実コンパイルまで（3分ほど）

マテリアルの場所は --materials で変えられる。**既定は `Assets` 全体**で、
そこからシェーダーの GUID で絞る（1 キャラのフォルダを決め打ちしていた頃は、
利用者が見ているキャラが診断に入っていなかった ── T-297）。
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
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
# **1 キャラのフォルダを決め打ちしていた。** このシェーダーを使う
# マテリアルは 3 フォルダに 86 件あり、**診断は 46 件しか見ていなかった**
# ── 利用者が今まさに見ているキャラは一度も入っていない（T-297）。
# `Assets` 全体を渡す。絞り込みは param_check がシェーダーの GUID で行う。
DEFAULT_MATERIALS = (_PROJ / "Assets") if _PROJ else None

LABEL = {"ok": "OK  ", "fail": "失敗", "skip": "未検証"}


def run(title: str, argv: list[str], allow_skip: bool = False) -> tuple[str, list[str]]:
    """道具を 1 つ回して、状態と `error:` 行を返す。

    **飛ばしたことを黙らない。** 「実行できなかった」を「通った」と
    同じ顔で並べると、まとめだけ見た人が誤解する。
    子が `未検証` と名乗って正常終了したときだけ `skip` にする。
    """
    print(f"\n{'=' * 62}\n  {title}\n{'=' * 62}")
    proc = subprocess.run([sys.executable, str(HERE / argv[0]), *argv[1:]],
                          capture_output=True, text=True, encoding="utf-8",
                          errors="replace")
    out = (proc.stdout or "") + (proc.stderr or "")
    print(out.rstrip())

    if proc.returncode != 0:
        status = "fail"
    elif allow_skip and any(l.lstrip().startswith(("未検証", "skip"))
                            for l in out.splitlines()):
        status = "skip"
    else:
        status = "ok"

    lines = [l.strip() for l in out.splitlines()
             if l.lstrip().startswith("error:")]

    # **警告も持ち帰る。** これまで要約に出ていたのは `error:` だけで、
    # 警告 101 件は子の出力の中に埋もれていた ── 17 種類しかないのに、
    # **読むには 101 行を目で追うしかなかった。**
    # 種類ごとに畳んでまとめに出す（件数は場所の欄から拾う）。
    _WARNINGS.extend(l.strip() for l in out.splitlines()
                     if l.lstrip().startswith("warning:"))

    print(f"  → {LABEL[status].strip()}（終了コード {proc.returncode}）")
    return status, lines


# 各道具が出した警告をここに溜める（種類ごとに畳んでまとめに出す）
_WARNINGS: list[str] = []


def digest_warnings() -> list[tuple[str, int, str]]:
    """警告を種類ごとに畳む。→ (見出し, 出た箇所の数, 代表の場所)"""
    import collections
    seen: dict[str, list[str]] = collections.OrderedDict()
    for line in _WARNINGS:
        body = line[len("warning:"):].strip()

        # **コンパイラの警告は種類で畳む。**
        # fxc は変数ごとに 1 行出すので、同じ 1 種類が 6 行に散る
        # ── 畳むための一覧が畳まれていない、では意味が無い。
        # 例: `... CelLighting.hlsl(37,5): warning X4000: use of ... (litMask)`
        fx = re.search(r"warning (X\d+): ([^(]+?)\s*(?:\(|$)", body)
        if fx:
            src = re.search(r"([\w.\-]+\.hlsl)\(", body)
            seen.setdefault(f"{fx.group(1)} {fx.group(2)}", []).append(
                src.group(1) if src else "")
            continue

        m = re.match(r"(.*?)\s*\[(.*)\]\s*$", body)
        title, where = (m.group(1), m.group(2)) if m else (body, "")
        seen.setdefault(title, []).append(where)
    return [(t, len(w), w[0]) for t, w in seen.items()]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root", nargs="?",
                    help="検査対象のツリー。既定は道具の置き場所から推定する")
    ap.add_argument("--materials", default=str(DEFAULT_MATERIALS),
                    help=".mat を並べたディレクトリ")
    ap.add_argument("--unity", help="Unity 実行ファイル。渡すと実コンパイルまで回す")
    ap.add_argument("--no-strict", action="store_true",
                    help="警告を失敗扱いにしない")
    ap.add_argument("--full", action="store_true",
                    help="実コンパイルをキーワードの全組で回す（140 秒ほど）")
    ap.add_argument("--self-test", action="store_true",
                    help="検査そのものが生きているかを、欠陥を注入して確かめる")
    args = ap.parse_args()

    # **道具の置き場所＝検査対象、とは限らない。** パッケージへ移すと
    # 道具は `Documentation~/` に入り、シェーダーは `Runtime/Shaders/` にある。
    # `.` を渡すと**シェーダーが 1 枚も無い場所を検査して「エラー 0 件」**になる
    # ── 一番危ない黙り方（T-252）。
    # **同じパッケージに別のシェーダーが同居する。** 単に「パッケージルート」を
    # 渡すと、隣の Cel を検査してしまう（両方 `Runtime/Shaders/` の下にある）。
    # 自分のツリーは `ToonPBRCommon.hlsl` を持つ場所として特定する。
    if args.root:
        root = Path(args.root).resolve()
    else:
        base = HERE.parent if HERE.name == "Documentation~" else HERE
        hit = next(iter(sorted(base.rglob("ToonPBRCommon.hlsl"))), None)
        root = (hit.parent if hit else base).resolve()

    results: list[tuple[str, str, list[str]]] = []

    # **自己診断は最初に回す。** 検査が死んでいる状態の「エラー 0 件」は
    # 通ったのと見分けが付かない。順番を逆にすると、信用できない合格を
    # 先に読ませることになる。
    if args.self_test:
        st, bad = run("自己診断 — 検査が生きているか", ["self_test.py"])
        results.append(("自己診断 (self_test)", st, bad))

        # **自己診断が見ていない枝がある。** `self_test.py` は指摘の中身を
        # 試すが、**呼ばれない入口そのもの**は対象外。`--branch-cost` は
        # ここからも自己診断からも呼ばれず、改名以来ずっと落ちていた（T-259）。
        # 落ちないことだけを確かめる（43 秒）。
        st, bad = run("入口の疎通 — 全部の道具が動くか",
                      ["smoke_tools.py", str(root), "--materials", args.materials])
        results.append(("入口の疎通 (smoke_tools)", st, bad))

    lint = ["shader_lint.py", str(root)]
    if not args.no_strict:
        lint.append("--strict")
    # **道具そのものを先に検査する。** 検査の失敗経路は普段通らないので、
    # そこに書き間違いがあると「その検査が必要になった日」に初めて
    # NameError で落ちる ── 報告の代わりに道具ごと死ぬので、
    # 元の問題に辿り着けない。実際 2 か所あった（T-296）。
    # `--deep`（呼び出し方の一致・22 秒）は自己診断のときだけ。
    tl = ["tool_lint.py"] + (["--deep"] if args.self_test else [])
    st, bad = run("道具の検査 — 未定義名", tl)
    results.append(("道具の検査 (tool_lint)", st, bad))

    st, bad = run("静的検査 — 構造", lint)
    results.append(("静的検査 (shader_lint)", st, bad))

    # **生成物が古いままだと、読む人は古い既定値を信じる。**
    # プロパティ一覧はシェーダーと GUI から作るので、どちらかを触ったら
    # 作り直しが要る。ここで気付かせる（1 秒未満）。
    st, bad = run("生成物の鮮度 — プロパティ一覧が最新か",
                  ["gen_properties.py", "--check"])
    results.append(("生成物の鮮度 (gen_properties)", st, bad))

    param = ["param_check.py", str(root), "--materials", args.materials]
    st, bad = run("値の検算 — 式が実際の値で成立するか", param)
    results.append(("値の検算 (param_check)", st, bad))

    # **実コンパイル（fxc）。** Unity を起動せずに d3dcompiler_47.dll を叩く。
    # Editor が開いていて batchmode が使えないときでも、
    # 型・未宣言・引数の数・リソース上限はここで出る（T-230）。
    # 既定 16 プログラムで 2.5 秒、`--full` で 176 プログラム 140 秒。
    # **既定（キーワード無し）は誰も出荷していない構成だった。**
    # このプロジェクトの PC 用レンダラは Forward+ なので、毎回の実コンパイルは
    # **使われない構成だけを通して「成功」と言っていた**。そこでしか出ない
    # 欠陥を実際に 1 つ見逃していた（T-333 のループ内微分）。
    # 全組は 141 秒で毎回は回せないが、出荷経路だけなら 2 組で足りる。
    hc = ["hlsl_compile.py", str(root), "--shipping"]
    if args.full:
        hc = ["hlsl_compile.py", str(root), "--variants"]
    st, bad = run("実コンパイル — fxc で直接（Unity 不要）", hc, allow_skip=True)
    results.append(("実コンパイル (fxc)"
                    + ("・全組" if args.full else "・出荷構成"), st, bad))

    # **C# も Unity 無しでコンパイルする。** Unity 同梱の Roslyn を叩く。
    # ここが無かったせいで、文字列リテラルが行内で閉じていない状態を
    # Editor がリフレッシュするまで 20 分通してしまった（T-231）。
    # **C# は「パッケージ全体」で見る。**
    # シェーダーのツリーを渡すと `Editor/Idol/` と `Runtime/Scripts/Idol/` しか
    # 見ず、**Cel の 7 本（`Editor/` 直下）が一度も検査されていなかった。**
    # 静的検査や fxc と違って、C# は**1 つのアセンブリとして一緒に**
    # コンパイルされるので、シェーダー単位で分ける方が不自然。
    # 13 ファイル → **21 ファイル**（T-318）。
    cs_root = root
    for p in [root, *root.parents]:
        if (p / "package.json").exists():
            cs_root = p
            break
    st, bad = run("実コンパイル — Roslyn で C#（Unity 不要）",
                  ["csharp_compile.py", str(cs_root)], allow_skip=True)
    results.append(("実コンパイル (C#)", st, bad))

    if args.unity:
        st, bad = run("実コンパイル — 56 組のバリアント",
                      ["verify_variants.py", "--unity", args.unity])
        results.append(("実コンパイル (verify_variants)", st, bad))
    else:
        # **飛ばしたことを黙らない。** 「静的検査が通った」を
        # 「コンパイルが通った」と読み替えるのが一番危ない誤解。
        #
        # ただし Editor が起動中なら batchmode は**そもそも起動できない**
        # （グローバルキャッシュのロックで Unity が落ちる・CLAUDE.md 参照）。
        # そのとき唯一残るコンパイル証拠が Editor 自身のログなので、拾いに行く。
        st, bad = run("実コンパイル — Editor.log から読む（batchmode の代わり）",
                      ["editor_log_check.py", str(root)], allow_skip=True)
        # まとめでは**「部分」と名乗らせる。** ここを `実コンパイル` と書くと
        # 56 組が通ったのと同じ顔で並んでしまう。
        results.append(("実コンパイル・部分 (editor_log)", st, bad))
        if st != "ok":
            print(f"\n{'=' * 62}")
            print("  Editor 側の証拠は取れていないが、**fxc は上で通している。**")
            print("  fxc が見ないもの: D3D11 以外のバックエンド、")
            print("  Unity 独自の前処理、ベクトルの次元不一致（黙って変換される）。")
            print(f"{'=' * 62}")

    print(f"\n{'=' * 62}\n  まとめ\n{'=' * 62}")
    for name, st, _ in results:
        print(f"  {LABEL[st]}  {name}")

    # **警告を種類ごとに畳んで出す。**
    # 出していなかったので、17 種類しかないものを読むのに
    # **101 行を目で追う**しかなかった。まとめに出ないものは読まれない。
    digest = digest_warnings()
    if digest:
        total = sum(n for _, n, _ in digest)
        print(f"\n  警告 {total} 件（{len(digest)} 種類）")
        for title, n, where in sorted(digest, key=lambda x: -x[1]):
            head = f"{title}" + (f"（{n} か所）" if n > 1 else "")
            print(f"    - {head}")
            print(f"        {where}")

    failed = [(n, bad) for n, st, bad in results if st == "fail"]
    if failed:
        print(f"\n  **{len(failed)} 件失敗**")

        # **落ちた中身をここに再掲する。** 子の出力は 46 マテリアルぶんの
        # 上に流れていて `tail` では読めない。まとめだけ見て次の行動が
        # 決まらないゲートは、やがて読まれなくなる。
        for name, bad in failed:
            if bad:
                for line in bad:
                    print(f"    [{name}] {line}")
            else:
                print(f"    [{name}] error: 行が取れなかった。上の出力を参照すること")
        return 1

    # **「すべて通過」を、通っていない項目があるのに言わない。**
    # 未検証／スキップを合格に丸めるのが、このプロジェクトで一番繰り返した誤り。
    unverified = [n for n, st, _ in results if st in ("stale", "skip")]
    if unverified:
        print(f"\n  落ちた検査は無い。ただし **{len(unverified)} 件は未検証**: "
              + " / ".join(unverified))
        return 0

    print("\n  すべて通過")
    return 0


if __name__ == "__main__":
    sys.exit(main())

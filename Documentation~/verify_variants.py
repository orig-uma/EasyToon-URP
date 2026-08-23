#!/usr/bin/env python3
"""検証用のスクラッチプロジェクトを立て、そこで実バリアント検証を回す。

なぜ要るか:
  本番プロジェクトを Unity Editor で開いていると batchmode が使えない
  （ロックはプロジェクト単位）。**別ディレクトリに複製すれば Editor を
  閉じずに回せる。** CLAUDE.md に手順が書いてあるが、ビルトインモジュールの
  引き写しを忘れると、コードと無関係なエラーが出る。その手順をここに固定する。

**この道具は「偽の合格」を出していた（T-132 で修正）。**
  以前は削除済みのクラス `ShaderCompileCheck.RunCI` を呼んでいた。
  Unity がエラー終了しても ERR/WARN 行が1つも出ないため、
  `0 組で指摘あり` と表示して **exit 0（成功）** を返していた。
  終了コードも見ていなかった。
  **検証したという証拠が取れないなら、成功を返してはいけない。**

  同じ形の失敗を既に2回している（T-072 のサンプラ上限超過、
  T-085 の変数二重宣言）。どちらも「0 errors」と報告された後に
  実機や別手段で発覚した。

使い方:
  python verify_variants.py --unity "<Unity.exe のパス>"

  実際にコンパイルするのは Unity 側の ToonPBRVariantCheck で、
  56 組（パス × キーワード × D3D/Vulkan × 頂点/フラグメント）を回す。
  **#define を注入する旧方式はやめた** ── あちらはキーワードを
  CompileVariant へ直接渡すので、注入は二重になるうえ検出力も落ちる。
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent          # Assets/ToonPBR
PROJECT = HERE.parent.parent                    # プロジェクトルート


def build_scratch(root: Path) -> None:
    """検証専用プロジェクトを組む。"""
    (root / "Assets").mkdir(parents=True, exist_ok=True)
    (root / "ProjectSettings").mkdir(exist_ok=True)
    (root / "Packages").mkdir(exist_ok=True)

    shutil.copy(PROJECT / "ProjectSettings" / "ProjectVersion.txt",
                root / "ProjectSettings" / "ProjectVersion.txt")

    dst = root / "Assets" / "ToonPBR"
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(HERE, dst)

    # **ビルトインモジュールを引き写すこと。** URP だけの manifest だと
    # Animator や HumanBodyBones が見つからず、コードと無関係なエラーが出る。
    src = json.loads((PROJECT / "Packages" / "manifest.json").read_text(encoding="utf-8"))
    deps = {k: v for k, v in src["dependencies"].items()
            if k.startswith("com.unity.modules.")}
    for key in ("com.unity.render-pipelines.universal",
                "com.unity.render-pipelines.core",
                "com.unity.shadergraph", "com.unity.burst",
                "com.unity.mathematics", "com.unity.collections"):
        if key in src["dependencies"]:
            deps[key] = src["dependencies"][key]

    (root / "Packages" / "manifest.json").write_text(
        json.dumps({"dependencies": deps}, indent=2), encoding="utf-8")


def run_unity(unity: str, root: Path) -> tuple[int, str]:
    """スクラッチで ToonPBRVariantCheck.RunCI を回す。

    戻り値は (終了コード, ログ全文)。**判定は呼び出し側でする。**
    ここで握りつぶすと、以前と同じ「証拠が無いのに成功」に戻る。
    """
    # **`-logFile -`（標準出力）は使わない。** Windows では捕捉できず、
    # 集計行が1行も取れないまま終了コード 0 が返る。
    # 実際この道具を直した直後に踏み、新しい「証拠が無ければ失敗」の判定が拾った。
    # ファイルへ吐かせて読む形なら確実（このプロジェクトで実績のある手順）。
    log_path = root / "verify.log"

    proc = subprocess.run(
        [unity, "-batchmode", "-quit", "-nographics",
         "-projectPath", str(root),
         "-executeMethod", "ToonNPR.EditorTools.ToonPBRVariantCheck.RunCI",
         "-logFile", str(log_path)],
        capture_output=True, text=True, errors="replace", timeout=1800)

    log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
    return proc.returncode, log + proc.stdout + proc.stderr


def main() -> int:
    ap = argparse.ArgumentParser(
        description="スクラッチプロジェクトでバリアントを実コンパイル検証する")
    ap.add_argument("--unity", required=True, help="Unity 実行ファイルのパス")
    ap.add_argument("--keep", action="store_true",
                    help="失敗時にスクラッチを残す（ログを追うため）")
    args = ap.parse_args()

    if not Path(args.unity).exists():
        print(f"error: Unity が見つからない: {args.unity}", file=sys.stderr)
        return 2

    root = Path(tempfile.mkdtemp(prefix="toonpbr_verify_"))
    print(f"検証プロジェクト: {root}")
    build_scratch(root)

    code, log = run_unity(args.unity, root)

    for line in [ln.strip() for ln in log.splitlines()
                 if re.search(r"\bERR\b|error CS|Shader error", ln)][:20]:
        print(f"  {line}")

    # **証拠が無ければ成功を返さない。**
    # 以前はこの判定が無く、Unity が起動すらできなくても
    # 「0 組で指摘あり」と出して exit 0 を返していた。
    marker = next((ln.strip() for ln in log.splitlines()
                   if "[VariantCheck]" in ln and "組" in ln), None)

    if marker is None:
        print()
        print("error: 検証の結果を確認できなかった。**成功として扱わない。**")
        print(f"    Unity の終了コード: {code}")
        print("    ログに [VariantCheck] の集計行が無い。")
        print("    ToonPBRVariantCheck.RunCI が見つからないか、")
        print("    プロジェクトのコンパイルが通っていない可能性がある。")
        if args.keep:
            print(f"    スクラッチを残した: {root}")
        else:
            shutil.rmtree(root, ignore_errors=True)
        return 1

    print()
    print(marker)

    ok = (code == 0) and ("すべて成功" in marker)

    if ok or not args.keep:
        shutil.rmtree(root, ignore_errors=True)
    else:
        print(f"    スクラッチを残した: {root}")

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

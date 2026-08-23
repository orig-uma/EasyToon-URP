"""道具そのものを検査する ── **一度も通っていない経路の未定義名**を見つける。

なぜ要るか:
  `check_sheen_fit` のエラー経路に `hlsl.name` と書いてあった。`hlsl` は
  どこにも無い。つまり「係数が読めない」状況になると、報告の代わりに
  `NameError` で**検算が丸ごと落ちる**。

  **書いてあるだけの経路は、動くとは限らない。** 検査の失敗経路は普段
  通らないので、こういう欠陥は「その検査が必要になった日」に初めて出る
  ── しかも出方が「検算が落ちた」なので、元の問題に辿り着けない。

  Python は実行するまで名前を解決しないので、構文検査では出ない。
  外部の道具（pyflakes 等）を足すのは依存が増えるので、必要な分だけ書く。

見つけられないもの:
  属性（`obj.foo`）の綴り、型の不一致、実行時にしか決まらない名前。
  ここが見るのは**その場に存在しない名前を読んでいる**ことだけ。
"""
from __future__ import annotations

import ast
import builtins
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
BUILTINS = set(dir(builtins)) | {"__file__", "__name__", "__doc__", "__spec__"}


class Scope:
    def __init__(self, parent: "Scope | None" = None):
        self.parent = parent
        self.names: set[str] = set()

    def has(self, name: str) -> bool:
        s: Scope | None = self
        while s is not None:
            if name in s.names:
                return True
            s = s.parent
        return False


def bind(target: ast.AST, scope: Scope) -> None:
    """代入先・for の変数・with の as など、名前を作る側を登録する。"""
    for node in ast.walk(target):
        if isinstance(node, ast.Name):
            scope.names.add(node.id)


NESTED = (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda)


def own_nodes(fn: ast.AST, include_lambda_body: bool = False):
    """`fn` 自身に属するノードだけを返す（入れ子の関数には降りない）。"""
    stack = list(ast.iter_child_nodes(fn))
    while stack:
        node = stack.pop()
        yield node
        if isinstance(node, NESTED):
            if not (include_lambda_body and isinstance(node, ast.Lambda)):
                continue
        stack.extend(ast.iter_child_nodes(node))


class Checker(ast.NodeVisitor):
    def __init__(self, path: Path, module: Scope):
        self.path = path
        self.module = module
        self.issues: list[tuple[int, str]] = []

    def check_function(self, fn: ast.AST, parent: Scope) -> None:
        scope = Scope(parent)
        args = fn.args
        for a in (list(args.posonlyargs) + list(args.args) + list(args.kwonlyargs)
                  + ([args.vararg] if args.vararg else [])
                  + ([args.kwarg] if args.kwarg else [])):
            scope.names.add(a.arg)

        # **先に全部の束縛を集めてから読みを見る。**
        # Python の関数スコープは「その関数のどこかで代入されていれば
        # ローカル」なので、行の前後は関係ない。
        for node in ast.walk(fn):
            if node is fn:
                continue
            if isinstance(node, (ast.Assign, ast.AugAssign, ast.AnnAssign)):
                bind(node.targets[0] if isinstance(node, ast.Assign) else node.target, scope)
            elif isinstance(node, (ast.For, ast.AsyncFor)):
                bind(node.target, scope)
            elif isinstance(node, ast.withitem):
                if node.optional_vars is not None:
                    bind(node.optional_vars, scope)
            elif isinstance(node, ast.ExceptHandler):
                if node.name:
                    scope.names.add(node.name)
            elif isinstance(node, ast.NamedExpr):
                bind(node.target, scope)
            elif isinstance(node, comprehension_types):
                bind(node.target, scope)
            elif isinstance(node, (ast.Import, ast.ImportFrom)):
                for alias in node.names:
                    scope.names.add((alias.asname or alias.name).split(".")[0])
            elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                scope.names.add(node.name)
            elif isinstance(node, (ast.Global, ast.Nonlocal)):
                scope.names.update(node.names)

        # **入れ子の関数の中まで見ないこと。**
        # `ast.walk` は内側の `def` や `lambda` にも降りるので、
        # そちらの**引数**が「どこにも無い」と誤って報告される
        # ── 最初に書いたときは 38 件中ほとんどがこれだった。
        # 内側は自分のスコープで別に検査する。
        for node in own_nodes(fn):
            if isinstance(node, ast.Name) and isinstance(node.ctx, ast.Load):
                if node.id in BUILTINS or scope.has(node.id):
                    continue
                self.issues.append((node.lineno, node.id))

        # lambda は本体だけを、引数を足したスコープで見る
        for node in own_nodes(fn, include_lambda_body=True):
            if isinstance(node, ast.Lambda):
                self.check_function(node, scope)

        # 入れ子の関数も同じ規則で
        for node in ast.iter_child_nodes(fn):
            self.walk_defs(node, scope)

    def walk_defs(self, node: ast.AST, scope: Scope) -> None:
        """`node` の下にある def を、**正しい親スコープ**で検査する。

        `ast.walk` で全部拾ってはいけない ── 入れ子の関数まで
        **モジュールスコープを親として**もう一度検査してしまい、
        外側の関数の引数を閉包している内側の関数が
        「引数がどこにも無い」と誤って報告される。
        入れ子は `check_function` が自分の中から回す。
        """
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            self.check_function(node, scope)
            return
        for child in ast.iter_child_nodes(node):
            self.walk_defs(child, scope)


comprehension_types = (ast.comprehension,)


def module_level(tree: ast.Module):
    """モジュール直下（class の中は含む・def の中は含まない）のノード。"""
    stack = list(ast.iter_child_nodes(tree))
    while stack:
        node = stack.pop()
        yield node
        if isinstance(node, NESTED):
            continue                    # 関数の中はローカル
        stack.extend(ast.iter_child_nodes(node))


def module_scope(tree: ast.Module) -> Scope:
    """モジュール直下の名前だけ。**関数の中まで降りないこと。**

    `ast.walk` で集めていたときは、**あらゆる関数のローカル変数が
    モジュールスコープに入って**いた。結果、どの関数から見ても
    「名前は在る」ことになり、**検出器がほぼ空振りしていた** ──
    実際、直したはずの `hlsl.name` の**2 か所目**を見逃していた。
    """
    scope = Scope()
    for node in module_level(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            scope.names.add(node.name)
        elif isinstance(node, (ast.Import, ast.ImportFrom)):
            for alias in node.names:
                scope.names.add((alias.asname or alias.name).split(".")[0])
        elif isinstance(node, (ast.Assign, ast.AugAssign, ast.AnnAssign)):
            bind(node.targets[0] if isinstance(node, ast.Assign) else node.target, scope)
        elif isinstance(node, (ast.For, ast.AsyncFor)):
            bind(node.target, scope)
        elif isinstance(node, ast.withitem):
            if node.optional_vars is not None:
                bind(node.optional_vars, scope)
        elif isinstance(node, ast.NamedExpr):
            bind(node.target, scope)
        elif isinstance(node, comprehension_types):
            bind(node.target, scope)
    return scope


def check(path: Path) -> list[tuple[int, str]]:
    try:
        tree = ast.parse(path.read_text(encoding="utf-8", errors="replace"), str(path))
    except SyntaxError as e:
        return [(e.lineno or 0, f"構文エラー: {e.msg}")]
    mod = module_scope(tree)
    c = Checker(path, mod)
    for node in tree.body:
        c.walk_defs(node, mod)
    # 同じ (行, 名前) を重複して出さない
    return sorted(set(c.issues))


def check_path_independence() -> tuple[list[str], int, int]:
    """**呼び出し方で結果が変わる検査**を見つける。

    `param_check` の各検査を、**相対パスと絶対パスの両方**で走らせて
    件数を比べる。違えば、どちらかの呼び方で**黙って何もしていない。**

    実際に踏んだ形（T-311）: パッケージの親が `Packages` かどうかで
    隣のパッケージを見るか決めていたが、相対パスだと
    `Path("..").parent.name` が**空文字**になり一致しない。
    絶対パスで呼べば正しく、相対パスで呼ぶと誤検出が出る、という状態だった。

    **「差 0」は、試せた数と一緒に見ること。** 引数の形が違って
    呼べなかったものは何も確かめていない。
    """
    import inspect

    here = Path(__file__).resolve().parent
    proj = next((p for p in [here, *here.parents]
                 if (p / "Assets").is_dir() and (p / "Packages").is_dir()), None)
    if proj is None:
        return ["Unity プロジェクトが見つからないので試せない"], 0, 0

    # **`Packages/` の下だけを探さないこと。** 自己診断のサンドボックスは
    # `Assets/ToonPBR/` に平坦へ組み直すので、そこだと**何も見つからず
    # 「試せない」で素通り**する ── 試験が注入しても増えなかった。
    shader = next(iter(sorted(proj.rglob("ToonPBRCommon.hlsl"))), None)
    if shader is None:
        return ["シェーダーのツリーが見つからないので試せない"], 0, 0

    sys.path.insert(0, str(here))
    try:
        import param_check as pc
    except Exception as e:                     # noqa: BLE001
        return [f"param_check を読み込めない: {e}"], 0, 0

    abs_root, abs_mat = shader.parent, proj / "Assets"
    # 相対にするには、道具の置き場所からの相対で作り直す
    import os
    rel_root = Path(os.path.relpath(abs_root, here))
    rel_mat = Path(os.path.relpath(abs_mat, here))

    bad: list[str] = []
    tested = skipped = 0
    cwd = os.getcwd()
    os.chdir(here)
    try:
        for name, fn in sorted(vars(pc).items()):
            if not (name.startswith("check_") and inspect.isfunction(fn)):
                continue
            params = list(inspect.signature(fn).parameters.values())

            def build(r: Path, m: Path):
                args = []
                for p in params:
                    if p.name in ("root",):
                        args.append(r)
                    elif "materials" in p.name:
                        args.append(m)
                    elif p.default is not inspect.Parameter.empty:
                        break                  # 既定があるものはそこで打ち切る
                    else:
                        return None            # 埋められない引数がある
                return args

            a, b = build(rel_root, rel_mat), build(abs_root, abs_mat)
            if a is None or b is None:
                skipped += 1
                continue
            try:
                if hasattr(pc, "_MATERIAL_CACHE"):
                    pc._MATERIAL_CACHE.clear()
                x = fn(*a)
                if hasattr(pc, "_MATERIAL_CACHE"):
                    pc._MATERIAL_CACHE.clear()
                y = fn(*b)
            except Exception:                  # noqa: BLE001
                skipped += 1
                continue
            tested += 1
            if len(x) != len(y):
                bad.append(f"{name}: 相対で {len(x)} 件 / 絶対で {len(y)} 件")
    finally:
        os.chdir(cwd)
    return bad, tested, skipped


def check_reachability() -> tuple[list[str], int, int]:
    """**本番の配置で入力に届いていない検査**を見つける。

    2 回続けて同じ形で死んでいた。

      T-330  `root / "BACKLOG.md"` ── root は `Runtime/Shaders/Idol` なので必ず不在
      T-331  `root.parent.parent / "Packages"` ── 移行前の階層を数えていた

    どちらも**指摘 0 件**を返し続け、しかも**自己診断は緑**だった。
    砂場は移行前の平らな配置を写しているので、そこでだけ入力に届く。
    カバー率も助けにならない ── 関数単位なので、同じ関数の他の枝が
    生きていれば死んだ枝は数字に出ない。

    **署名は「呼び出し箇所ごとに一度も当たらない」。**

    最初は「探して見つからず、1 つも読まずに終わった検査」で書いたが、
    **T-331 を撃たなかった** ── その関数は死ぬ前に移行スクリプトを
    1 つ読むので「読み取り 0」に当たらない。関数単位では粗すぎる。

    「外れたら疑う」も駄目だった。実測 18 本の外れは全部
    `package.json` を上へ探す正常な走査で、**正常な走査は最後に必ず当たる。**
    箇所ごとに真偽を数えれば、当たりが 1 度も無いものだけが残る。

    砂場を建てる必要が無く、本番を 1 回走らせるだけで判る（1.6 秒）。

    **層違いの分岐は 1 つの式にまとめること。** 分岐を 2 つ書くと
    片方が必ず先に当たり、もう片方は永久に偽になって撃たれる。
    除外表を作るより、候補を並べて 1 か所で判定するほうが短い。
    """
    import io
    import traceback

    here = Path(__file__).resolve().parent
    # **`package.json` を必須にしないこと。** 最初 `if pkg else []` と書いたので、
    # パッケージ化していない配置（砂場はこちら）では探索を打ち切り、
    # **測れないまま「一度も当たらないガード」と report していた**。
    # 直す先の道具が、直す対象と同じ「黙って 0」をやっていた。
    pkg = next((p for p in here.parents if (p / "package.json").exists()), None)
    # **階層を数えない。** 検査の入口は `ToonPBRCommon.hlsl` が在る部屋。
    hits = sorted((pkg or here.parent).rglob("ToonPBRCommon.hlsl"))
    if not hits:
        return [], -1, -1                          # 測れない ── 呼び出し側が言う
    root = min(hits, key=lambda p: len(p.parts)).parent

    sys.path.insert(0, str(here))
    # 呼び出し箇所 "ファイル:行" -> [当たり, 外れ, 外れの例]
    site: dict[str, list] = {}
    orig = (Path.exists, Path.is_dir)

    def make_probe(i):
        def spy(self, *a, **kw):
            r = orig[i](self, *a, **kw)
            # **道具の中の呼び出し箇所へ付ける。** 呼んだのが pathlib 内部でも、
            # 外側へ辿れば必ず道具の行に着く。
            for fr in reversed(traceback.extract_stack()[:-1]):
                f = Path(fr.filename)
                if f.parent == here and f.suffix == ".py" and f.name != Path(__file__).name:
                    v = site.setdefault(f"{f.name}:{fr.lineno}", [0, 0, None])
                    v[0 if r else 1] += 1
                    if not r and v[2] is None:
                        v[2] = str(self)
                    break
            return r
        return spy

    Path.exists, Path.is_dir = make_probe(0), make_probe(1)
    argv, out_buf = sys.argv, sys.stdout
    try:
        import param_check as pc
        sys.argv = ["param_check.py", str(root)]
        sys.stdout = io.StringIO()
        try:
            pc.main()
        except SystemExit:
            pass
    except Exception as exc:                       # 落ちても黙らせない
        sys.stdout = out_buf
        return [], -1, f"{type(exc).__name__}: {exc}"
    finally:
        Path.exists, Path.is_dir = orig
        sys.argv, sys.stdout = argv, out_buf

    if not site:
        return [], -1, "観測 0"

    bad = [f"{k}  外れ {v[1]} 回・当たり 0（例: {v[2]}）"
           for k, v in sorted(site.items()) if v[0] == 0]
    return bad, len(site), sum(1 for v in site.values() if v[0])


def main() -> int:
    argv = [a for a in sys.argv[1:] if a != "--deep"]
    deep = "--deep" in sys.argv[1:]
    target = Path(argv[0]).resolve() if argv else HERE
    files = sorted(target.glob("*.py")) if target.is_dir() else [target]
    files = [f for f in files if f.name != Path(__file__).name]

    total = 0
    for f in files:
        for line, name in check(f):
            total += 1
            print(f"{f.name}:{line}: error: '{name}' はこの関数のどこにも無い。"
                  f" **その経路に入った瞬間 NameError で落ちる** ──"
                  f" 報告する代わりに道具ごと死ぬので、元の問題に辿り着けない。")

    print(f"\n道具の検査: {len(files)} ファイル / 未定義名 {total} 件")
    if total == 0:
        print("  **これは「動く」の証明ではない。** 名前が在ることしか見ていない。")

    # **これは毎回回す。** 1.6 秒で、2 回続けて踏んだ形をそのまま捕まえる。
    reach, n_site, n_live = check_reachability()
    for line in reach:
        total += 1
        print(f"error: 一度も当たらないガード: {line}")
        print("    **その先の検査は動いていない。** 指摘 0 件は"
              "「問題が無い」ではなく「見ていない」── 砂場は移行前の配置なので"
              " 自己診断は緑のまま通る（T-330 / T-331）。"
              " 層違いのための分岐なら、候補を並べて 1 か所で判定すること。")
    if n_site < 0:
        # **測れないことを「問題なし」と同じ顔で出さない。**
        # 最初この 2 つを同じ見出しで印字したので、砂場では測定不能が
        # 常時 1 件出ており、注入しても件数が増えず**試験が成立しなかった**。
        total += 1
        print(f"error: 入力への到達を測れなかった: {n_live}")
        print("    **測れないことは「届いている」ではない。**"
              " 対象のツリーが見つからないか、検査そのものが落ちている。")
    else:
        print(f"  入力への到達: ガード {n_site} 箇所中 {n_live} 箇所が当たった")

    # **重い検査は毎回回さない。**
    # 呼び出し方の一致は 29 件を 2 回ずつ回すので **22 秒**かかり、
    # `check.py` 全体 36 秒の 6 割を 1 つで食っていた（未定義名の走査は 0.2 秒）。
    # **毎回回すものが重くなると回されなくなる** ── そうなれば
    # 「見ていない検査は無いのと同じ」に逆戻りする。
    # 同じ性質の試験（作業ディレクトリ・実行回数）と揃えて
    # `--self-test` のときだけにした。
    if deep:
        bad, tested, skipped = check_path_independence()
        for line in bad:
            total += 1
            print(f"error: 呼び出し方で結果が変わる検査: {line}")
            print("    **どちらかの呼び方で黙って何もしている。**"
                  " パスを先に `resolve()` すること。")
        print(f"  呼び出し方の一致: {tested} 件を相対・絶対の両方で確認"
              f"（引数の形が違って試せないもの {skipped} 件）")
    else:
        print("  呼び出し方の一致: **未検査**（22 秒かかるので `--deep` のときだけ。"
              "`check.py --self-test` が回す）")

    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())

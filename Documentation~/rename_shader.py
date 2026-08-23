#!/usr/bin/env python3
"""シェーダーの名前を EasyToon の作法に合わせて一括で振り直す。

**移植の最後に残る作業がこれ。** 名前は人が決めるものなので実装を止めていたが、
決まってから手で直すと必ずどこかが漏れる。**漏れても動いてしまう**のが厄介で、

  - `Shader.Find` が null を返す → **例外も警告も出ずに何もしない**（W107 の領域）
  - `LightMode` タグが片側だけ変わる → **輪郭が黙って描かれなくなる**

移植先（EasyToon）の作法は Idol から読み取れる:

    シェーダー名   Origuma/EasyToon_URP/Idol
    LightMode      IdolOutline / IdolCharShadow   ← **シェーダー名を前置する**

つまり**短い名前 1 つ**が決まれば残りは機械的に決まる。

使い方:
    python rename_shader.py Prima            # 下見（何も書き換えない）
    python rename_shader.py Prima --apply    # 適用

適用したら必ず検証を通すこと:
    python check.py --self-test
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# 移植先の作法。**Idol から読み取った実物**であって、決め打ちではない。
VENDOR = "Origuma/EasyToon_URP"

TARGET_SUFFIXES = (".shader", ".hlsl", ".cs", ".md")

# 現在の名前 -> 新しい名前を作る関数
def build_map(name: str) -> list[tuple[str, str]]:
    """置換の対応表。**長いものから先に**（部分一致で壊さないため）。"""
    return [
        # シェーダー名（.shader の宣言と C# の Shader.Find の両方）
        ('"Toon/URP/CharacterPBR"', f'"{VENDOR}/{name}"'),
        ("Toon/URP/CharacterPBR", f"{VENDOR}/{name}"),
        # LightMode の独自タグ。**.shader と RendererFeature の両方に出る。**
        # 片側だけ変えると輪郭が黙って消える
        ('"ToonOutline"', f'"{name}Outline"'),
        ('"ToonHairShadow"', f'"{name}HairShadow"'),
        # **グローバルのシェーダー変数。** C#（`Shader.PropertyToID`）と HLSL の
        # 両方に同じ名前で出る。片側だけ変えると**前髪の影が黙って消える**
        # ── null で守られるのではなく、単に別の変数を読むだけ。
        # 両側ともこのツリーにあるので、まとめて変えれば整合は保たれる。
        ("_ToonHairShadow", f"_{name}HairShadow"),
        # **部分一致でシェーダーを見分けている箇所。**
        # `m.shader.name.Contains("CharacterPBR")` の形で 3 か所ある。
        # フルパスではないので上の表に当たらず、**振り直すと対象 0 件のまま
        # 黙って動かなくなる** ── T-155 で実際に起きた壊れ方そのもの。
        # サンドボックスで振り直して W107 に撃たせて見つけた。
        ('"CharacterPBR"', f'"{name}"'),
        # メニューパス
        ("Tools/Toon NPR/", f"Tools/{name}/"),
        ("Tools > Toon NPR >", f"Tools > {name} >"),
    ]


# 振り直した後に**残っていてはいけない**もの。名前を持つ位置だけを見る。
# 表に無い形の名前が残ると「動くが繋がっていない」状態になり、
# W107 が撃つのは C# 側だけなので気付けない。
LEFTOVER_RE = re.compile(
    r'"(?:Hidden/)?Toon/URP/\w+"|"Toon(?:Outline|HairShadow)"|_ToonHairShadow')


def collect(root: Path) -> list[Path]:
    return sorted(p for p in root.rglob("*")
                  if p.suffix in TARGET_SUFFIXES and p.is_file())


def main() -> int:
    ap = argparse.ArgumentParser(
        description="シェーダー名を EasyToon の作法へ一括で振り直す")
    ap.add_argument("name", help="新しい短い名前（例: Prima）。Idol と並ぶ位置")
    ap.add_argument("root", nargs="?", default=".", help="対象ツリー")
    ap.add_argument("--apply", action="store_true",
                    help="実際に書き換える（既定は下見）")
    args = ap.parse_args()

    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", args.name):
        print("error: 名前は英字で始まる識別子にすること"
              "（LightMode タグと C# の文字列に入るため）")
        return 2

    root = Path(args.root).resolve()
    pairs = build_map(args.name)

    total = 0
    touched: dict[Path, int] = {}
    print(f"新しい名前: {args.name}")
    print(f"  シェーダー   {VENDOR}/{args.name}")
    print(f"  LightMode    {args.name}Outline / {args.name}HairShadow")
    print(f"  メニュー     Tools/{args.name}/")
    print()

    for path in collect(root):
        text = path.read_text(encoding="utf-8", errors="replace")
        new = text
        hits = 0
        for old, rep in pairs:
            n = new.count(old)
            if n:
                new = new.replace(old, rep)
                hits += n
        if not hits:
            continue
        total += hits
        touched[path] = hits
        if args.apply:
            path.write_text(new, encoding="utf-8", newline="")

    for path, hits in sorted(touched.items(), key=lambda kv: -kv[1]):
        rel = path.relative_to(root)
        print(f"  {hits:>3} 箇所  {rel}")

    # **取りこぼしを自分で数える。** 下見のときは「置換後の姿」で数える。
    leftover: list[tuple[Path, list[str]]] = []
    for path in collect(root):
        text = path.read_text(encoding="utf-8", errors="replace")
        if not args.apply:
            for old, rep in pairs:
                text = text.replace(old, rep)
        hits = LEFTOVER_RE.findall(text)
        if hits:
            leftover.append((path.relative_to(root), sorted(set(hits))))

    print(f"\n{'書き換えた' if args.apply else '書き換わる'}: "
          f"{total} 箇所 / {len(touched)} ファイル")

    if leftover:
        print("\n**振り直しきれない名前が残る**（表に足すこと）:")
        for rel, sample in leftover:
            print(f"  {rel}  例: {', '.join(sample[:3])}")
    else:
        print("  取りこぼし無し ── 名前を持つ位置に Toon* は残らない")

    if not args.apply:
        print("\n**下見なので何も変えていない。** 適用するには --apply を付けること。")
        return 0

    print("""
**マテリアルは GUID でシェーダーを指しているので、参照は切れない。**
名前で引いているのは C# の `Shader.Find` と `LightMode` タグだけで、
どちらもこの表に入っている。

次にやること:
  1. python check.py --self-test        ── W107 が片側漏れを撃つ
  2. Unity でマテリアルのインスペクタを開き、シェーダー名が変わっていること
  3. 輪郭を有効にしたマテリアルで、輪郭が出ていること
     （LightMode タグの片側漏れはここでしか見えない）""")
    return 0


if __name__ == "__main__":
    sys.exit(main())

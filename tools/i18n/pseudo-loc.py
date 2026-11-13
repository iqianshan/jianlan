"""生成伪语言资源（qps-ploc），用于 AC-11 的伪语言走查。

伪语言要解决的是"静态扫描扫不出来的漏网文案"：`check-keys.py` 只能发现
源码里显而易见的中英文常量，发现不了 C# 里拼接出来的句子、XAML 里
`<Run>` 的零散文字、以及第三方控件自带的内建文案。把资源整体换成
带重音、拉长 40% 的假文字后，界面上只要还剩一段正常英文或中文，
就说明那段没走资源键。

三个处理规则：
  1. 字符映射：每个 ASCII 字母换成带重音符的近形字（a→á、s→š），
     正常英文里绝不会出现这些字符，肉眼一眼可分。
  2. 长度膨胀：每个元音后重复一次，整体长度约 ×1.35–1.4。
     德/俄语相对英文的膨胀率就在这个区间，正好压测版式（截断/换行）。
  3. 边界标记：每行首尾加 `[` `]`。走查时若看到某段文字缺一侧括号，
     说明那行被裁掉了半截，而不是文案本身有问题。

保护规则：`{0}` 占位符、换行、首尾空白不动——占位符被破坏会让
`string.Format` 抛异常，走查就变成看崩溃而不是看版式。

用法：
    python tools/i18n/pseudo-loc.py            # 生成 Strings.qps-ploc.resx
    python tools/i18n/pseudo-loc.py --check    # 只校验，不写盘

生成物不进版本库（见 .gitignore）：它是构建期产物，改 resx 后重跑即可。
"""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOCALIZATION = ROOT / "src" / "Jianlan.Core" / "Localization"
SOURCE = LOCALIZATION / "Strings.resx"
TARGET = LOCALIZATION / "Strings.qps-ploc.resx"

VOWELS = set("aeiouyAEIOUY")

ACCENTS = {
    "a": "á", "b": "ƀ", "c": "ç", "d": "ð", "e": "é", "f": "ƒ",
    "g": "ğ", "h": "ĥ", "i": "î", "j": "ĵ", "k": "ķ", "l": "ļ",
    "m": "ɱ", "n": "ñ", "o": "ö", "p": "þ", "q": "9", "r": "ř",
    "s": "š", "t": "ţ", "u": "û", "v": "ṽ", "w": "ŵ", "x": "ẋ",
    "y": "ý", "z": "ž",
}

# 大写字母单独映射：直接 upper() 会把 ƀ/þ 这类无大写形字符弄坏
ACCENTS_UPPER = {
    "A": "Á", "B": "Ɓ", "C": "Ç", "D": "Ð", "E": "É", "F": "Ƒ",
    "G": "Ğ", "H": "Ĥ", "I": "Î", "J": "Ĵ", "K": "Ķ", "L": "Ļ",
    "M": "Ṁ", "N": "Ñ", "O": "Ö", "P": "Þ", "Q": "9", "R": "Ř",
    "S": "Š", "T": "Ţ", "U": "Û", "V": "Ṽ", "W": "Ŵ", "X": "Ẋ",
    "Y": "Ý", "Z": "Ž",
}

# {0}、{0:N0}、{0:0} 都算占位符；不保护会让 string.Format 直接抛异常。
# 必须带捕获组：re.split 只把捕获组的内容留在结果里，没有捕获组时
# 分隔符自己会被丢掉，{0} 就从文案里消失了（踩过一次）。
PLACEHOLDER = re.compile(r"(\{\d+(?::[^{}]*)?\})")


def _expand(text: str) -> str:
    """字符映射 + 元音重复。只处理字母，其余字符原样保留。"""
    out: list[str] = []
    for ch in text:
        mapped = ACCENTS.get(ch) or ACCENTS_UPPER.get(ch)
        if mapped is None:
            out.append(ch)
            continue

        out.append(mapped)
        # 元音重复一次 ≈ 整体膨胀 1.35–1.4 倍，与德/俄语实测膨胀率相当
        if ch in VOWELS:
            out.append(mapped)
    return "".join(out)


def pseudo(text: str) -> str:
    """把一条资源值转成伪语言。占位符与换行结构保持不变。"""
    if not text:
        return text

    # 按行处理：关于页这类多行文案，每行都要有自己的括号，
    # 否则只有首行有标记，下面几行漏了也看不出来
    lines = text.split("\n")
    converted: list[str] = []

    for line in lines:
        # 占位符切成独立片段，只转普通文本，占位符原样拼回
        pieces = PLACEHOLDER.split(line)
        rebuilt: list[str] = []
        for index, piece in enumerate(pieces):
            rebuilt.append(piece if index % 2 else _expand(piece))

        body = "".join(rebuilt)
        leading = body[: len(body) - len(body.lstrip())]
        trailing = body[len(body.rstrip()):]
        core = body.strip()

        converted.append(f"{leading}[{core}]{trailing}" if core else body)

    return "\n".join(converted)


def escape(value: str) -> str:
    """写回 resx 需要 XML 转义；换行用 &#10;，与手写资源保持一致。"""
    value = value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    return value.replace("\n", "&#10;")


DATA_BLOCK = re.compile(
    r'(<data name="(?P<key>[^"]+)"[^>]*>\s*<value>)(?P<value>.*?)(</value>)',
    re.DOTALL,
)


def build() -> tuple[str, int]:
    source = SOURCE.read_text(encoding="utf-8")

    count = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal count
        count += 1
        return match.group(1) + escape(pseudo(match.group("value"))) + match.group(4)

    result = DATA_BLOCK.sub(replace, source)

    # 头部补一行说明，避免有人把它当正式语言包提交
    result = result.replace(
        "  中性资源 = 英文（源语言）。中文见 Strings.zh-Hans.resx。",
        "  【生成物，勿手改】伪语言 qps-ploc，由 tools/i18n/pseudo-loc.py 生成。\n"
        "  仅用于 AC-11 走查：带重音字母 + 长度膨胀 40%，用来暴露未走资源键的文案。\n"
        "  中性资源 = 英文（源语言）。中文见 Strings.zh-Hans.resx。",
        1,
    )
    return result, count


def main() -> int:
    check_only = "--check" in sys.argv

    if not SOURCE.exists():
        print(f"找不到源资源：{SOURCE}")
        return 1

    content, count = build()

    if check_only:
        if not TARGET.exists():
            print("FAIL：伪语言资源不存在，先运行不带 --check 的版本")
            return 1
        stale = TARGET.read_text(encoding="utf-8") != content
        print("FAIL：伪语言资源与 Strings.resx 不一致，请重新生成" if stale else "OK：伪语言资源已是最新")
        return 1 if stale else 0

    TARGET.write_text(content, encoding="utf-8", newline="\n")

    # 键数一致是硬要求：漏一条就会在界面上露出英文原文
    source_values = {
        d.get("name"): (d.find("value").text or "")
        for d in ET.parse(SOURCE).getroot().findall("data")
    }
    target_values = {
        d.get("name"): (d.find("value").text or "")
        for d in ET.parse(TARGET).getroot().findall("data")
    }

    missing = set(source_values) - set(target_values)
    extra = set(target_values) - set(source_values)

    print(f"OK：{TARGET.relative_to(ROOT)}  共 {count} 条")
    if missing or extra:
        print(f"FAIL：键不一致 缺 {len(missing)} 多 {len(extra)}")
        return 1

    # 占位符必须逐条对齐：少一个 {0} 不会报错，只会在界面上少显示一个值，
    # 是最难靠肉眼发现的一类走查失效。
    broken: list[str] = []
    for key in sorted(source_values):
        before = re.findall(r"\{\d+", source_values[key])
        after = re.findall(r"\{\d+", target_values[key])
        if before != after:
            broken.append(f"{key}: {before} -> {after}")

    if broken:
        print(f"FAIL：{len(broken)} 条占位符对不上")
        for line in broken[:10]:
            print("  " + line)
        return 1
    print("OK：占位符逐条对齐")

    sample = target_values.get("Menu.NewFolder")
    if sample:
        print(f"样例 Menu.NewFolder = {sample}")
    return 0

    # 抽一条出来对照，确认映射真的生效而不是原样复制
    sample = next(
        (d.find("value").text for d in ET.parse(TARGET).getroot().findall("data")
         if d.get("name") == "Menu.NewFolder"),
        None,
    )
    if sample:
        print(f"样例 Menu.NewFolder = {sample}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

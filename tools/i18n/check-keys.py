"""多语言一致性校验（AC-11 / docs/i18n.md §七）。

三项检查，任一失败即返回 1，可直接挂到 CI：
  1. 键集合一致：Strings.resx（英文中性）与 Strings.zh-Hans.resx 的键完全相同
  2. 生成物同步：Strings.g.cs 必须与 resx 一致（防止改了文案忘了重新生成）
  3. 无残留硬编码：src 下 XAML 的 Text/Content/Header/ToolTip 与 C# 字符串字面量里不得有中文

关于第 3 项的白名单：错误匹配用的英文/中文关键词（如 MapFsErrorCode 里识别 .NET 异常文本）
不是给人看的文案，出现中文是必然的，用行内注释 `// i18n-ignore` 豁免。

用法：
    python tools/i18n/check-keys.py
"""

from __future__ import annotations

import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RESX_EN = ROOT / "src/Jianlan.Core/Localization/Strings.resx"
RESX_ZH = ROOT / "src/Jianlan.Core/Localization/Strings.zh-Hans.resx"
SRC = ROOT / "src"

CJK = re.compile(r"[\u4e00-\u9fff]")
CS_STRING = re.compile(r'"[^"\n]*[\u4e00-\u9fff][^"\n]*"')
# XAML 里会显示给用户的属性；注释与 Style 的 x:Key 不在其中
XAML_USER_ATTR = re.compile(
    r'\b(Text|Content|Header|ToolTip|Title)="([^"{}]*[\u4e00-\u9fff][^"{}]*)"'
)

IGNORE = "i18n-ignore"


def load_keys(path: Path) -> dict[str, str]:
    root = ET.parse(path).getroot()
    out = {}
    for data in root.findall("data"):
        name = data.get("name")
        if not name:
            continue
        value_el = data.find("value")
        out[name] = (value_el.text or "") if value_el is not None else ""
    return out


def check_keys() -> list[str]:
    en, zh = load_keys(RESX_EN), load_keys(RESX_ZH)
    problems = []
    for k in sorted(set(en) - set(zh)):
        problems.append(f"zh-Hans 缺键：{k}")
    for k in sorted(set(zh) - set(en)):
        problems.append(f"zh-Hans 多键：{k}")
    for k in sorted(set(en) & set(zh)):
        if not en[k].strip():
            problems.append(f"英文文案为空：{k}")
        if not zh[k].strip():
            problems.append(f"中文文案为空：{k}")
        # 占位符数量必须一致，否则 string.Format 会错位或抛异常
        if len(re.findall(r"\{\d+\}", en[k])) != len(re.findall(r"\{\d+\}", zh[k])):
            problems.append(f"占位符数量不一致：{k}")
    return problems


def check_generated() -> list[str]:
    result = subprocess.run(
        [sys.executable, str(ROOT / "tools/i18n/gen-strings.py"), "--check"],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        return [result.stdout.strip() or "Strings.g.cs 与 resx 不一致"]
    return []


def iter_sources():
    for path in list(SRC.rglob("*.cs")) + list(SRC.rglob("*.xaml")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        if path.name == "Strings.g.cs":
            continue
        head = path.read_text(encoding="utf-8").splitlines()[:6]
        if any("i18n-ignore-file" in line for line in head):
            continue
        yield path


def check_hardcoded() -> list[str]:
    problems = []
    for path in iter_sources():
        rel = path.relative_to(ROOT)
        for i, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("///"):
                continue
            if IGNORE in line:
                continue

            if path.suffix == ".xaml":
                for m in XAML_USER_ATTR.finditer(line):
                    problems.append(f'{rel}:{i}  XAML 硬编码：{m.group(1)}="{m.group(2)}"')
            else:
                for m in CS_STRING.finditer(line):
                    problems.append(f"{rel}:{i}  C# 硬编码：{m.group(0)}")
    return problems


def main() -> int:
    groups = [
        ("键一致性", check_keys()),
        ("生成物同步", check_generated()),
        ("残留硬编码", check_hardcoded()),
    ]

    failed = False
    for title, problems in groups:
        if not problems:
            print(f"[OK]   {title}")
            continue
        failed = True
        print(f"[FAIL] {title}：{len(problems)} 处")
        for p in problems[:40]:
            print("       " + p)
        if len(problems) > 40:
            print(f"       …另有 {len(problems) - 40} 处")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

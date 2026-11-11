# AC-06 视觉走查验收记录

> 对应 `docs/requirements.md` §12 AC-06：深色 / 浅色 / 高对比度三模式 × 字号 9 / 14 / 24pt 三档，
> 无文字不可读、无控件错位、无文字截断重叠；切换字体后布局重新计算正确。

## 1. 结论

**9 组（3 主题 × 3 字号）全部通过，走查过程中修掉 7 处缺陷，其中 1 处为启动即崩溃。**

| 主题 | 9pt | 14pt | 24pt | 判定 |
|---|---|---|---|---|
| 浅色 | PASS | PASS | PASS | 通过（24pt 下列头完整、无重叠） |
| 深色 | PASS | PASS | PASS | 通过 |
| 高对比度 | PASS | PASS | PASS | **修复后才能启动**（见 §4.1）；修复后黄框黑底、选中行反白均正确 |

补充交叉测试：**英文界面 14 / 24pt**（AC-06 × AC-11）。英文串比中文长 30–50%，在这个组合下又暴露了 2 处只在“英文 + 大字号”同时成立时才出现的缺陷（§4.6、§4.7），中文三档全部测不出来。

字体族切换：中文 14pt 默认字体 → `华文行楷` 24pt，行高/列宽/换行均正确重算，无竖排裁切（§5）。

设置页分页改版后的复验见 §8：三档字号 × 中英两种语言 × 三主题走了一遍，又修掉 4 处（§4.8–§4.11），全部落在“宽度写死、字号一变就截断”这同一类根因上。

## 2. 环境与取证方式

| 项 | 值 |
|---|---|
| 系统 | Windows 10.0.26300，显示缩放 125% |
| 应用 | `publish/` Release 产物，`dotnet publish` 后运行 |
| 尺寸 | 主窗口 1400×900（物理像素），设置页 880×720 DIP（12pt 基准，随字号放大） |
| 截图 | `tools/window-drive/window_drive.py`，`PrintWindow` 抓整窗 + 按 `visible=` 裁剪 |
| 高对比度 | `SystemParametersInfo(SPI_SETHIGHCONTRAST)` 切系统开关，方案 `High Contrast Black` |
| 字号 | 改 `%LOCALAPPDATA%\Lingpane\settings.json` 的 `appearance.fontSizePt`，每档冷启动一次 |
| 脚本 | `D:\ac05\ac06.py`（主题 × 字号矩阵）、`ac06_font.py`（字体族）、`hc.ps1`（高对比度开关） |
| 截图存档 | `D:\ac05\ac06\*.bmp` |

为什么每档都冷启动而不是改设置页：字号变更走 `FontScaleService.Apply` 是热更新路径（FONT-04 已单独验过），但**主题切换 + 大字号**的组合只有在启动时整棵树按新度量排一遍才暴露得出问题，热切容易掩盖首帧问题。

## 3. 判定标准

| 现象 | 判定 |
|---|---|
| 文字被容器硬裁（末字缺笔画、无省略号） | **FAIL** |
| 文字与相邻元素重叠、控件跑到窗口外 | **FAIL** |
| 文字不可读（对比度不足、与背景同色） | **FAIL** |
| 列表末尾行被视口裁掉半行 | 可接受——滚动视口的正常边界，滚动后可见 |
| 长文件名以省略号收尾 | 可接受——`Text.Base` 的既定行为，不是截断 |

## 4. 缺陷与修复

### 4.1 高对比度模式下应用启动即崩溃（P0，此前从未测过）

现象：系统开高对比度后启动，进程在 `MainWindow.Show()` 阶段抛异常退出，`crash.log` 记录：

```
XamlParseException: 无法从文本"{x:Static SystemColors.WindowColor}"创建 Color
  └ FormatException: 令牌无效
主题 HighContrast
```

根因：`Theme.HighContrast.xaml` 里 41 条 `<Color x:Key="Color.Xxx">{x:Static SystemColors.YyyColor}</Color>`。`<Color>` 元素的文本内容交给 `ColorConverter` 按字符串解析，不认标记扩展；`{x:Static}` 只能用在**值为 `Color` 的属性**上（如 `SolidColorBrush.Color`）。

修复：删掉高对比度字典里的 `Color.*` 中间层（经确认只有本文件拼画刷时引用，字典外无消费者），画刷直接上色：

```xml
<SolidColorBrush x:Key="Brush.Surface.Window" Color="{x:Static SystemColors.WindowColor}" po:Freeze="True"/>
```

副作用与口径调整：高对比度字典键数从 83 变为 42（41 画刷 + `Theme.Name`）。`docs/ui-guidelines.md` 规则 1.2 原本要求“三份键集合完全一致”，现改为“`Brush.*` + `Theme.Name` 一致”，并写明 `Color.*` 只是浅色/深色内部中转层。

### 4.2 24pt 下设置页标签被压成“界面…”

`Text.FieldLabel` 的 `Width` 写死 120，24pt 时“界面语言”只显示到“界面…”。

修复：加 `FieldLabelWidth = Even(120 × s)` 到 `FontMetrics`，`FontScaleService` 写 `Size.FieldLabel.Width` / `Size.FieldHint.Margin`，XAML 改 `{DynamicResource}`。

### 4.3 24pt 下下拉框文字顶到 ▾

`ComboBox` 模板里 `ContentPresenter Margin="10,0,28,0"`、箭头 `Margin="0,0,10,0"` 全是字面量，24pt 时“简体中文”直接压在箭头上。

修复：三个值改 `DynamicResource`（`Size.ComboContent.Margin` / `Size.ComboArrow.Margin`），系数分别是 `10 × s`、`34 × s`、`10 × s`。

### 4.4 24pt 下设置页副标题被省略号截断

副标题用 `Text.Caption`（`TextWrapping="NoWrap"` + `CharacterEllipsis`），24pt 时整句比窗口还宽。

修复：改 `Text.Caption.Wrap`。AC-06 的“无文字截断”指的是硬裁，但把一个完整句子折成省略号同样属于信息丢失，按 FAIL 处理。

### 4.5 英文 24pt 下设置页“关闭”按钮被挤出窗口

底部操作栏是四列 Grid（`Auto / Auto / * / Auto`），三个按钮列宽都是 `Auto`：内容比窗口宽时既不会压缩也不会换行，最右侧的 `Close` 直接被画到窗口外（截图里只剩 “Clc”）。

修复：左侧两个次要操作移进 `WrapPanel`，挤不下时折行；`Close` 单独一列右对齐并 `VerticalAlignment="Bottom"`。中文各档仍是一行，布局不变。

### 4.6 英文 24pt 下导航栏文字被硬裁

`Size.Nav.Width.Default` 写死 184，英文 24pt 的 `Documents` / `Downloads` 比栏宽长，`ListBox` 直接切掉末尾字母，连省略号都没有——因为行模板是横向 `StackPanel`，横向测量时给子项无限宽，`TextTrimming` 永远不会触发。

修复两处：

1. 导航栏 `Width="Auto"`，`MinWidth`/`MaxWidth` 取 `Size.Nav.Width.Default`(184) / `Size.Nav.Width.Max`(320)，宽度按内容走并夹在区间内。中文与常规字号下内容不足 184，**宽度与修改前完全一致**。
2. 行模板由横向 `StackPanel` 改成两列 `Grid`（图标 `Auto` + 文字 `*`），文字列加 `TextTrimming="CharacterEllipsis"`，真正超长时以省略号收尾而不是硬裁。

### 4.7 启动时不应用已保存的界面字体（FR-5.1.5）

现象：`settings.json` 里存了 `appearance.fontFamily = "华文行楷"`，重启后主窗口仍是默认字体；只有在**打开过一次设置页**之后，字体才突然全变。

根因：`FontScaleService.ApplyFontFamily` 全项目只有 `SettingsWindow.OnFontChanged` 一个调用点（设置页 `LoadFontsAsync` 回填下拉框时会触发一次）。启动路径只调了 `FontScaleService.Apply`（字号），从未应用字体族，所以资源键 `Font.Family.UI` 一直是默认栈。

修复：`App.OnStartup` 在字号之后补一次 `FontScaleService.ApplyFontFamilyByName(Settings.Appearance.FontFamily)`。走名字应用而不枚举系统字体——`FontCatalog` 明确“不参与启动路径”（枚举 300+ 字体在冷启动上是灾难），名字不存在时 WPF 按字体栈自回落，设置页 `Resolve` 时仍会给 FONT-03 的回落提示。

这个缺陷之所以必须放在 AC-06 里修：它让“切换字体后布局重新计算正确”这条**根本无法验证**——字体压根没生效，任何字体下的布局问题都会被掩盖。

### 4.8 英文 24pt 下设置页下拉框把选项截成前几个字母

现象：`Accent colour` 一栏只显示 `Follow`（原文 `Follow system`），`Interface font` 一栏的文字贴着 ▾ 且被压扁。

根因：宽度写死在 XAML 里（`Width="200"` / `"240"` / `"300"`）。字号一放大文字就变宽，容器不动，必然截断——和 4.2、4.6 是同一个病，只是这次落在下拉框上。

修复：宽度改成随字号缩放的令牌，只留两档——`Size.Combo.Width`（12pt 基准 300）给常规项、`Size.Combo.Width.Narrow`（120）给"历史条数""缓存上限"这类数值项。同时把同一页里混用的 200/240/300 收敛成一档，左右边缘才对得齐。

`FontMetrics` 里新增 `BaselineScale = PointsToPixels(12) / BaseRowPx`：令牌的基准值按 12pt 设计稿写，计算时先除掉 12pt 自身的字号倍数，这样 12pt 下取到的正好是 200/300/120 本身，12pt 布局不漂。

### 4.9 高对比度下设置页选中的导航项读不出来

现象：选中项底色是系统高亮色（深蓝），文字仍是窗口文字色（近黑），整行糊成一片。

根因：`Nav.Item` 模板只给选中态换了 `Sel` 的底色，没换文字色。浅色/深色主题下选中底色是淡蓝/深灰，主文字色压上去本来就够读，问题被掩盖了。

修复：`Text.Nav` 加一条 `IsSelected` 的 `DataTrigger`，把前景色指向 `Brush.Text.OnSelected`。这个画刷已有且在三个主题下取值正确（浅/深色与主文字色同值，高对比度是 `HighlightTextColor`），所以浅色与深色主题**没有任何视觉变化**。

顺带修好了主窗口侧栏的同一处：`Text.Nav` 是两个窗口共用的。

### 4.10 数据目录路径把“打开”按钮挤出可视区

现象：`隐私与存储` 页最后一行的路径很长时，右侧按钮被画到窗口外。

根因：这一行是横向 `StackPanel`，横向测量给子项无限宽，`TextTrimming` 永远不触发——与 4.6 同因。加 `TextTrimming` 不够，得先让文字有个上限。

修复：改成三列 `Grid`（标签 `Auto` / 路径 `*` / 按钮 `Auto`），路径列拿到有限宽度后省略号才生效。

### 4.11 12pt 下“每页一屏”不成立

现象：设置页按 12pt 设计 880×720，字号调到 12pt 后内容比设计稿高 10%，最长的一页开始滚动。

根因：`FitToFontScale` 用 `points / 12` 算放大比例，12pt 得 1.0，窗口不长；但设置页里标签列、下拉框、说明行的尺寸都是**行高**的函数，12pt 的行高是 16px，比设计基线 13px 高 23%，内容自然变长。

修复：比例改为 `FontScaleService.Current.RowPx / FontMetrics.BaseRowPx`，即"实际行高 ÷ 设计基线行高"，仍夹在 1–2 之间并限制在屏幕工作区 94% 内。9pt 及以下不缩小。

## 5. 字体族切换的布局复算

| 组合 | 结果 |
|---|---|
| 中文 14pt + `华文行楷`（字形远宽于默认栈） | 行高不变、列宽不变、超长文件名按省略号收尾，无竖排裁切 |
| 中文 24pt + `华文行楷` | 同上；导航栏仍完整显示“此电脑” |
| 中文 14pt + `Segoe UI`（无中文字形） | 中文落到字体栈回落，无豆腐块，行高不变 |

结论：字族切换只换字形不改尺寸的设计成立；行高由字号算出、不取自字体度量，因此不存在“宋体比雅黑高一点导致行被裁”的问题。

## 6. 未覆盖 / 后续

| 项 | 说明 |
|---|---|
| 8pt / 16pt 边界 | FR-5.1.6 要求 8–24pt 连续可调，本轮按 AC-06 只测 9/14/24 三档。8pt 是下限，理论上更宽松，但 caption 下限 9px 的取整需要单独看一眼 |
| 其余 7 个界面英文 24pt | 本轮英文只测了主窗口与设置页；确认框、属性页、右键菜单等尚未在英文 24pt 下过一遍 |
| 真机 150% / 200% DPI | 本轮在 125% 下测；高 DPI 下 `Even()` 取整与 1px 描边可能有半像素问题 |
| 高对比度“白色”方案 | 只测了 `High Contrast Black`；`High Contrast White` 下 `SystemColors` 取值不同，需再走一遍 |
| 24pt 下的“一屏” | 24pt 六页都要滚动。离屏工具的虚拟屏只有 720 高，真机大屏能给出更多可视区，但本轮没有在真机上量 24pt 的实际可视高度 |

## 7. 复跑方式

```powershell
# 1. 出产物（注意必须是 publish，不是 build——XAML 只在 publish 时重新打进资源）
dotnet publish src/Lingpane.App/Lingpane.App.csproj -c Release -o publish

# 2. 普通主题 × 三档字号
python D:\ac05\ac06.py light light
python D:\ac05\ac06.py dark  dark

# 3. 高对比度（系统级开关，走完记得关）
powershell -File D:\ac05\hc.ps1 on
python D:\ac05\ac06.py hc dark
powershell -File D:\ac05\hc.ps1 off

# 4. 英文交叉项
python D:\ac05\ac06.py en-light light en 14,24

# 5. 字体族
python D:\ac05\ac06_font.py xingkai24 "华文行楷" 24
```

夹具目录 `D:\ac05\visual` 需含：长中文名、超长无扩展名、带多段扩展名（`site-backup-2026-11-05.tar.gz`）、中文目录名各一份，用于压列宽与截断。

设置页单独走查时不必截屏数像素，用离屏工具直接量：

```powershell
# 逐页打印"内容高 / 可视高 / 倍数"，倍数 > 1.02 判定为需要滚动
OffscreenShot.exe _m.png 4000 --settings --settings-metrics [--font-size 9|12|14|24] [--lang en]

# 指定页 + 主题 + 字号单拍一张
OffscreenShot.exe p4-14pt.png 4000 --settings --settings-page 4 --theme light --font-size 14
```

注意 `--font-size` / `--lang` / `--theme` 会写进 exe 旁边的 `data\settings.json`，连跑多档前先把它重置回 `auto` / `null`，否则下一档拿到的是上一档的状态。

## 8. 设置页分页后的复验（2026-11-10）

设置页由“一列 8 个大分区”改成“左侧 6 页导航 + 右侧单页滚动”（常规 / 外观 / 文件列表 / 操作与确认 / 隐私与存储 / 关于）之后，按 AC-06 的口径重新走了一遍。

### 8.1 每页一屏（内容高 ÷ 可视高）

| 页 | 9pt | 12pt | 14pt | 英文 12pt | 英文 14pt |
|---|---|---|---|---|---|
| 常规 | 0.83 | 0.78 | 0.87 | 0.81 | 0.92 |
| 外观 | 0.87 | 0.80 | 0.90 | 0.80 | 0.90 |
| 文件列表 | 0.94 | 0.88 | 1.00 | 0.88 | 1.00 |
| 操作与确认 | 0.89 | 0.84 | 0.97 | 0.92 | 1.06\* |
| 隐私与存储 | 0.94 | 0.88 | 0.98 | 0.88 | 0.98 |
| 关于 | 0.24 | 0.24 | 0.27 | 0.28 | 0.27 |

\* 这一格量的是离屏环境：虚拟屏只有 720 高，`FitToFontScale` 算出的 1034 被工作区夹回 677，可视区因此比真机小。真机 1080p 下 14pt 的可视区约 810，同一页只有 541，仍然一屏。

24pt 下六页全部需要滚动（最长 2.43 屏）。这是设计口径而非缺陷：**“每页一屏”只承诺到 14pt**，24pt 换的是“窗口按行高等比放大 + 页面可滚”，与改版前“整个设置页 5–6 屏”不是一个量级。

### 8.2 本轮修掉的 4 处

§4.8 下拉框截断（英文 24pt）、§4.9 高对比度选中项不可读、§4.10 长路径挤掉按钮、§4.11 12pt 下一屏不成立。

四处根因收敛成一句：**凡是装文字的容器，尺寸必须来自随字号变化的令牌；写死的像素在 24pt 一定会出事**。新增的巡检口径——“容器宽度只准用 `Size.*` 令牌或 `TextTrimming` + `*` 列，禁止字面量”——已写进 `docs/ui-guidelines.md` 规则 15.7。

### 8.3 人工复核过的截图

六页 × 各档的“有没有截断/溢出”由 §8.1 的数值覆盖；下面这些组合另外逐张看过，确认观感与数值一致：

| 组合 | 结论 |
|---|---|
| 中文浅色 12pt × 常规 / 外观 / 文件列表 / 隐私与存储 | 无截断、无重叠、无溢出；下拉框宽度统一，左边缘对齐 |
| 英文 24pt × 外观 / 文件列表 | `Follow system`、`Details`、`Tab bar position` 全部完整（§4.8 修复后）；导航 `Privacy and storage` 完整显示 |
| 英文 14pt × 隐私与存储 | 条数并入按钮行后垂直居中正常；长路径以省略号收尾，右侧按钮在位 |
| 高对比度 12pt × 操作与确认 / 隐私与存储 | 选中项反白可读（§4.9）；正文、按钮、复选框均清晰 |
| 深色 24pt × 外观 | 选中项底色与文字对比正常 |
| 字号控件行（默认字号） | `跟随系统 (9 pt)` 完整，不再被压成 `跟随系统 (…` |

一处需要说明：本机 100% 缩放下“跟随系统”取到的行高与 9pt 完全相同（都是 12px），所以 `--font-size 9` 出的图与不传该参数时逐字节一致——这不是工具没生效，而是两档本来就同值。测 8pt / 10pt 能得到不同结果，可作为反证。



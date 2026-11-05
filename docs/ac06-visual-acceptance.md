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

## 2. 环境与取证方式

| 项 | 值 |
|---|---|
| 系统 | Windows 10.0.26300，显示缩放 125% |
| 应用 | `publish/` Release 产物，`dotnet publish` 后运行 |
| 尺寸 | 主窗口 1400×900（物理像素），设置页 640×660 DIP |
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

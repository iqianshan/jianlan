# Jianlan 界面实现规则 v1.0

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-11-03 |
| 适用范围 | `src/Jianlan.App` 全部 XAML 与自定义控件 |
| 视觉基线 | 强调色 `#245EEB`（深色主题 `#82A7FF`，与 Logo 两套主色一致）；默认密度 32px（舒适）；四级表面 + 1px 描边；无模糊、无投影 |
| 字号基线 | 8–24pt 连续可调（步进 1，默认跟随系统），设计基线 10pt ≈ 13px；行高/列宽随动，图标尺寸固定（§15 / FR-5.1.6 / FONT-06） |
| 依据 | 《Jianlan 需求规格说明书 v1.0》P1–P7、NF-01~NF-13、FR-5.1、FR-5.2.4 |
| 效力 | 本文件为 XAML 层强制规范。与本文件冲突的写法一律按本文件修改，不接受"先实现后补规范" |

---

## 1. 文件结构与加载顺序

```
src/Jianlan.App/
  Typography/
    FontMetrics.cs             ← 字号 → 派生尺寸的唯一计算入口（规则 4.6 / 15.2）
    FontScaleService.cs        ← 运行期写入字号相关资源键、滚动锚定（规则 15.3）
  Design/
    DesignTokens.xaml          ← 唯一入口：尺寸/字体/动效/圆角 + 合并当前主题
    Themes/
      Theme.Light.xaml         ← 只含 Color.* 与 Brush.*
      Theme.Dark.xaml
      Theme.HighContrast.xaml
    Styles/
      Typography.xaml          ← 全部文字样式，字号/字体族/行高统一走 DynamicResource
      Controls.Button.xaml     ← 控件模板，只引用 token，不定义颜色
      Controls.List.xaml
      ...
```

`App.xaml` 中依次合并 `DesignTokens.xaml` 与 `Styles/Typography.xaml`：

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Design/DesignTokens.xaml"/>
            <ResourceDictionary Source="Design/Styles/Typography.xaml"/>
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

**规则 1.1**　主题字典必须位于 `MergedDictionaries` 中带 `Theme.Name` 键的位置，切换时按标记查找，不按索引：

```csharp
public static void ApplyTheme(string name)
{
    var dict = Application.Current.Resources.MergedDictionaries
        .FirstOrDefault(d => d.Contains("Theme.Name"));
    if (dict is null) return;
    var src = new Uri($"Design/Themes/Theme.{name}.xaml", UriKind.Relative);
    if (dict.Source == src) return;
    dict.Source = src;
}
```

**规则 1.2**　对外契约是三份主题字典的 **`Brush.*` + `Theme.Name` 键集合完全一致**（当前 42 个键），新增画刷时三份同步新增，否则切换主题会出现资源缺失而回退为默认黑字。

> `Color.*`（41 个）只是浅色/深色两份字典内部的中间层，画刷以 `Color="{StaticResource Color.X}"` 引用，字典外无任何消费者。高对比度**不设这一层**：`<Color x:Key="...">{x:Static SystemColors.WindowColor}</Color>` 在 XAML 里非法（元素文本走 `ColorConverter` 按字符串解析，取不到 `x:Static`），`{x:Static}` 只能用在其值为 `Color` 的**属性**上。因此高对比度的画刷直接写 `Color="{x:Static SystemColors.XxxColor}"`，随系统配色方案（黑/白）变化。详见 `docs/ac06-visual-acceptance.md` §3.1。

**规则 1.3**　所有 `SolidColorBrush` 必须带 `po:Freeze="True"`。不可冻结的画刷在 WPF 中会产生额外的变更通知开销，列表万行滚动时可见。

---

## 2. Token 引用规则

| 资源类型 | 引用方式 | 原因 |
|---|---|---|
| 圆角、动效时长、层级 | `StaticResource` | 运行期恒定，避免每次属性访问走字典查找 |
| 间距 `Space.*`、内边距 `Padding.*` | `StaticResource` | 不随字号缩放，保持 4px 栅格 |
| **字号 `Font.Size.*`、行高 `Font.LineHeight.*`** | **`DynamicResource`** | 字号可在设置中实时调整（§15） |
| **行/控件高度 `Size.*Height`、`Size.Row.Active`** | **`DynamicResource`** | 随字号重算，否则大字被裁切 |
| **列宽 `Column.*`** | **`DynamicResource`** | 随字号重算，否则日期/大小列截断；图标不随字号（FONT-06） |
| 颜色 `Color.*`、画刷 `Brush.*` | `DynamicResource` | 主题切换需实时生效（FR-5.1.1 <100ms 无闪烁） |
| 字体族 `Font.Family.*` | `DynamicResource` | 与字号同批更新；跨字典引用，Static 解析不可靠 |

**规则 2.1（强制）**　界面 XAML 中**禁止出现颜色字面量**。不得出现 `#FF245EEB`、`Red`、`White`、`Transparent` 以外的颜色值；`Transparent` 仅允许用于背景。

**规则 2.2（强制）**　禁止魔法数字。所有 `Height`、`Width`、`Margin`、`Padding`、`FontSize`、`CornerRadius` 必须来自 token 或用 `Space.*` 组合表达。

**规则 2.3**　间距只允许 4 的整数倍，且优先用 `Space.1/2/3/4/6` 五档。出现 5、7、9 这类值一律视为错误。

**规则 2.4**　禁止在 `Styles/*.xaml` 中定义颜色，颜色只能来自主题字典。控件模板只负责结构与尺寸。

**规则 2.5（强制）**　字号相关 token 必须用 `DynamicResource`，不得用 `StaticResource`——`StaticResource` 在加载时求值一次，改字号不会更新。列表项虽有上万条，但虚拟化下只有可视项（约 30 个）会解析资源，开销可忽略；据此不接受"DynamicResource 太慢"作为退回 `StaticResource` 的理由。

**规则 2.6（强制）**　`Typography.xaml` 中的样式已覆盖 `FontSize` / `FontFamily` / `LineHeight`，界面代码**禁止**再单独设置这三个属性（含 `FontSize="13"` 这类覆盖）。局部需要不同层级时，换样式而不是改属性。

---

## 3. 栅格与区域尺寸

窗口根元素必须设置：

```xml
<Window UseLayoutRounding="True"
        SnapsToDevicePixels="True"
        TextOptions.TextFormattingMode="Display"
        TextOptions.TextRenderingMode="ClearType"
        TextOptions.TextHintingMode="Fixed">
```

| 区域 | 尺寸（10pt 基准） | token | 字号变化时 |
|---|---|---|---|
| 标题栏高度 | 40 | `Size.TitleBar.Height` | 随动 |
| 标签页高度 | 30（行高 + 10） | `Size.Tab.Height` | 随动 |
| 标签页宽度 | 最小 100 / 最大 200，标题可用宽 = 最大宽 − 72（图标 + 关闭按钮 + 内边距） | `Size.Tab.MinWidth` / `Size.Tab.MaxWidth` / `Size.Tab.Title.MaxWidth` | 随动 |
| 工具栏高度 | 48 | `Size.Toolbar.Height` | 随动 |
| 导航栏宽度 | 下限 184（`Size.Nav.Width.Default`），上限 320（`Size.Nav.Width.Max`） | `Size.Nav.Width.*` | 不缩放，但**按内容取宽**：内容不足 184 时等于 184 |
| 导航项高度 | 28 → 与行高一致 | `Size.Nav.Item.Height` | 随动 |
| 列表表头高度 | 30 | `Size.Row.Header` | 随动 |
| 列表行高（当前生效） | 32 | `Size.Row.Active` | 随动 |
| 列表行高（舒适 / 紧凑） | 32 / 26 | `Size.Row.Comfortable` / `Size.Row.Compact` | 随动 |
| 状态栏高度 | 28 | `Size.StatusBar.Height` | 随动 |
| 列表图标 | 16 | `Size.Icon.List` | **不动**（FONT-06） |
| 滚动条宽度 | 10 | `Size.ScrollBar.Thickness` | 不动。实现见 `Design/Styles/Controls.ScrollBar.xaml`：无箭头按钮、滑块圆角胶囊（`CornerRadius.ScrollThumb`），横向同样 10 |
| 分隔条宽度 | 6 | `Size.Splitter.Thickness` | 不动 |
| 间距 / 内边距 | 4 的倍数 | `Space.*` / `Padding.*` | 不动 |

**规则 3.1**　密度切换（FR-5.1.4）**只允许改变 `Size.Row.*`**。字号、图标尺寸、内边距不随密度变化。密度是行距，不是缩放。

**规则 3.2**　列表左边界对齐：标签点槽位（12px，固定宽）→ 图标（16px）→ 文件名，三段位置在所有行、两个窗格间必须像素级一致。**槽位为空时也必须占位**，否则有标签的行会把文件名整体右移。

**规则 3.3**　列宽使用 `Column.*` token，实际显示四列：名称（星号列，随窗口伸缩）+ 修改日期 / 类型 / 大小（固定宽度、可拖拽、记忆进 `settings.json`）。名称列不参与拖拽。

**规则 3.4**　禁止在列表中使用 `Grid.IsSharedSizeScope`。它会让虚拟化失效并强制全量测量，10 万项目录直接卡死。

**规则 3.5（强制）**　列表行、导航项、表头、标签页、工具栏、状态栏的高度必须绑定对应 `Size.*` token（`DynamicResource`），**禁止写死**。写死意味着字号调大后文字被裁切。

**规则 3.6**　高度与列宽取偶数（2px 网格），保证 DPI 缩放下的像素对齐；间距仍严格执行 4px 栅格。字号变化后出现的 34、36 这类偶数属于正常值。

**规则 3.7（强制）**　`GridLength` 类型的属性（`RowDefinition.Height`、`ColumnDefinition.Width`）**不能**直接绑定 `sys:Double` 类型的 token——`DynamicResource` 不做类型转换，运行时抛异常或静默失效。正确写法：

```xml
<!-- ✗ 错误：Size.Toolbar.Height 是 double，赋给 GridLength -->
<RowDefinition Height="{DynamicResource Size.Toolbar.Height}"/>

<!-- ✓ 正确：行高交给容器自身的 Height（double） -->
<Grid.RowDefinitions><RowDefinition Height="Auto"/></Grid.RowDefinitions>
<Border Height="{DynamicResource Size.Toolbar.Height}"/>

<!-- ✓ 正确：列宽用 Auto + 元素固定宽度 -->
<ColumnDefinition Width="Auto"/>
<TextBlock Width="{DynamicResource Column.Date.Width}"/>
```

**规则 3.8（双窗格）**　窗格变窄时的列折叠分两段，顺序固定：

1. **先缩**：把拖宽过的列逐个缩回默认宽度（`Column.*` 的默认值），列还在，文件名保住
2. **再折**：仍放不下才按 大小 → 类型 → 修改日期 折叠

只折叠不先缩，等于用户自己拖宽过的那一列直接消失；只缩不折，名称列会被固定列挤成 0 宽
（S4 实测：三个固定列 310px 吃掉了半个窗格，文件名整列不可见）。
折叠只是把 `Column.*.Width` 令牌置 0，用户设置的宽度保留，窗口一拉宽就回来。

判据门槛：名称列最小 150 + 行内边距 44。宽度取不到时（首次布局 `ActualWidth` 是 `NaN`）
直接返回、保持现状——早期版本漏了这条，`NaN` 让三列被一次性折光。

**规则 3.9（命令归属，强制）**　一个命令只有一处可见入口，位置由**它的作用域**决定，不由"用户此刻在哪个窗格"决定：

| 作用域 | 位置 | 例子 |
|---|---|---|
| 窗格级（作用于当前目录/选中项） | 该窗格的工具栏 | 导航、地址栏、详细/列表 |
| 窗口级·与浏览区相关 | **主窗格**工具栏最右，第二窗格不显示 | 分屏开关（FR-3.3） |
| 窗口级·与应用相关 | 侧栏底部 | 设置（FR-5.1） |

侧栏底部那一行的高度直接取 `Size.StatusBar.Height`，让它的上边框与窗格状态栏的上边框
连成一条横线。不放在导航列表正下方：列表下面那块会随窗口长高而变大，按钮漂在中间、
底下空一片。这一行也不跟「显示状态栏」开关走——状态栏是纯展示设置，不能顺带把唯一的
设置入口藏掉。

反例（S5 的教训）：设置按钮曾放在窗格工具栏最右。单窗格看着没问题，一分屏就出现两颗，
用户以为"两个窗格各有一份设置"。分屏按钮同理——它是窗口形态，两颗按钮会让人以为能各分各的。

分屏按钮的图标是自绘的方框 + 中线，不用 `Segoe MDL2 Assets`：字体里没有语义明确的"左右分栏"字形，
借用 `Tiles`（四宫格）之类反而更像"切换视图"。自绘图标描边取 `Foreground`，
才能跟着按钮的选中态一起变色。

**规则 3.10**　**图标按钮里的字形用 `IconGlyphs` 的 MDL2 码位，不要拿 Unicode 字符当 `Content`。**
工具栏那四颗导航按钮（←/→/↑/⟳）原先直接用字符 U+2190 一族、字号取 `Font.Size.Body`（12）。
箭头在字符框里横向铺满、看着还行，而 `⟳`（U+27F3）的圆圈只占字身约 70%，
12px 下视觉高度只剩 8px——用户的反馈是"刷新图标比别的都小"。字符的字面大小由字体决定，
调字号只能整体放大，调不出想要的视觉尺寸；MDL2 字形占满字身，**字号就是视觉高度**。
现在四颗统一用 `E72B / E72A / E74A / E72C` + 字号 14，`Foreground` 绑到 Button
（禁用时模板换的是 Button.Foreground，文本不主动跟随就还是亮色）。

自绘图标同理要按"行内的字"定尺寸：分屏图标原来 `Viewbox` 15、框 12×8，
在 32px 按钮里只剩约 10px 高，比同一行的"详细""列表"轻一档。改为 18 / 14×10，
描边 1.3→1.4，放大后线才不至于变细。

两个同族图标要靠**结构**区分，不能只靠坐标：分屏与预览都是"方框 + 一条竖线"，
竖线一在正中（x=9）、一在偏右（x=11），差 2px——实测两个图标并排时几乎认不出。
现在分屏是"对半分"（无填充），预览是"右边一窄栏"（右栏 32% 前景色淡填充）。
0.22 的不透明度在深色主题下看不出右栏存在，0.32 才够。

---

## 4. 排版

| 用途 | 字号 | 字重 | 颜色 |
|---|---|---|---|
| 文件名（列表行） | 13 | Normal | `Brush.Text.Primary` |
| 次要列（日期/大小/类型） | 12 | Normal | `Brush.Text.Secondary` |
| 表头 | 11 | Normal | `Brush.Text.Tertiary` |
| 导航项 | 12 | Normal | `Brush.Text.Primary` |
| 活动导航项 | 12 | **Emphasis** | `Brush.Accent.Default` |
| 面包屑当前段 | 12 | Emphasis | `Brush.Text.Primary` |
| 面包屑父级段 | 12 | Normal | `Brush.Text.Secondary` |
| 标签页标题 | 12 | Normal | `Brush.Text.Primary` / 非活动 `Brush.Text.Secondary` |
| 状态栏 | 11 | Normal | `Brush.Text.Secondary` |
| 分组标题 / 侧栏小标题 | 11 | Normal | `Brush.Text.Tertiary` |
| 对话框标题 | 15 | Emphasis | `Brush.Text.Primary` |

> 上表为 **10pt（13px）基准值**。实际渲染按当前字号等比缩放，比例见规则 4.6；用户可调 8–24pt（§15）。

**规则 4.1（强制）**　只允许 `Normal` 与 `Emphasis(Bold)` 两级字重。禁止 `Medium`、`SemiBold`、`Light`——微软雅黑无 Medium 字重，WPF 会走合成加粗，11–13px 下边缘发糊。强调优先靠字号与颜色，不靠加粗。

**规则 4.2（强制）**　数值列必须开启等宽数字，**对齐方式与文本列一致，统一左对齐**：

```xml
<TextBlock Text="{Binding SizeText}"
           Typography.NumeralAlignment="Tabular"
           Style="{StaticResource Text.Number}" />
```

早期 v5 规定数值列右对齐（"让 `42.6 KB` 与 `1.2 MB` 的右缘对齐"）。2026-11-24 走查后改回左对齐：
详情视图四个列头与四个数据列共用一套左缘，扫读时不必在"左起"与"贴右边"两套基准之间切换，
也与资源管理器的习惯一致。等宽数字保留——左对齐下小数点与单位仍然竖着对齐，位宽不跳。

日期列同样使用 Tabular，且**省略年份**（`10-28 14:32`）。跨年条目再显示年份（`2025-09-12`）。

**规则 4.3**　所有单行文本必须 `TextWrapping="NoWrap"` + `TextTrimming="CharacterEllipsis"`，并设置 `ToolTip` 显示完整值（FR-2.7 同理）。禁止让文件名换行撑高行。

**规则 4.4**　禁止负 `LetterSpacing`（WPF 通过 `Typography` 无此属性时不要用 `TextEffect` 模拟）；中文标签不需要加字距。

**规则 4.5（强制）**　**正文用 `TextFormattingMode="Ideal"，字形（字体图标）用 `Display`。**

这一条在 0.2.1 之前是反的：全应用写 `Display` + `Fixed` + `ClearType`，理由是"≤15px 时最清晰"。
那个结论只在 **96 DPI** 下成立。Display 把字形对齐到**逻辑像素**网格，而 125% 缩放下
一个逻辑像素 = 1.25 个物理像素，对齐后的笔画边缘必然落在整数物理像素之外，每条边多一层灰阶过渡；
`Fixed` hinting 又把字形强行拟合回网格，同一个字里横画 1px、竖画 2px。字号越大笔画越密，
落网外的边越多，实测 13pt（17.33px→21.67 物理像素）起观感明显变"脆"，浅色主题下最刺眼。

Ideal 按字体轮廓出字形、允许亚像素定位，125% 下反而更干净。**实测 11pt 小字号也没有变糊**，
所以不再按字号分档，统一 Ideal。

字形是唯一的例外：MDL2 的字形是纯几何线条，Ideal 会把它们放到亚像素上，
ClearType 随即把红绿蓝子像素暴露出来——实测窗口右上角三颗按钮的 × 与方框边缘出现一圈彩边。
凡是 `FontFamily` 指向 `Font.Family.Icon` 的样式都必须显式设回 `Display`：
`Text.Glyph`、`Nav.Glyph`、`Button.WindowControl`、菜单勾与子菜单箭头、搜索清除 ×、
标签关闭 ×、新标签加号。新增图标样式时照此办理。

**规则 4.6（强制）**　字号不是逐项手调，而是由**唯一基准 `Font.Size.Row`** 按固定比例派生。比例与取整规则集中在 `FontMetrics` 一处，界面不得自行乘算：

| 层级 | 派生比例（基准 13px / 10pt） | 下限 | 基准值时 |
|---|---|---|---|
| Title | ×1.538 | 18 | 20 |
| Subtitle | ×1.154 | 14 | 15 |
| Row（基准） | 1.0 | 10.7（8pt） | 13 |
| Body | ×0.923 | 10 | 12 |
| Caption | ×0.846 | 9 | 11 |
| 行高（Row） | ×1.538 | — | 20 |
| 行控件高度 | 行高 + 12（紧凑 +6）后取偶数 | 32 / 26 | 32 |
| 列宽（日期/类型/大小） | ×1.077 后取偶数 | — | 110 / 110 / 90 |

**规则 4.7**　字号调大后层级差必须同比放大，不得固定差值（例如"副标题永远比正文大 3px"）。否则大字号下层级塌陷为同一视觉重量。

---

## 5. 表面与颜色

四级表面，相邻层明度差 4–6%，全部靠明度差与 1px 描边表达层级：

| 层 | 用途 | 浅色 | 深色 |
|---|---|---|---|
| L0 窗口底 | 标题栏、标签带、状态栏 | `#F7F7F9` | `#1A1A1C` |
| L1 内容区 | 列表、导航栏 | `#FFFFFF` / `#FAFAFB` | `#202023` |
| L2 悬浮层 | 右键菜单、对话框、预览窗格 | `#FFFFFF` | `#26262A` |
| L3 交互态 | 悬停、焦点、输入框底 | `#F4F4F7` / `#F2F2F5` | `#2E2E33` |

**规则 5.1（强制）**　禁止使用 `#000000` 或 `#FFFFFF` 作为大面积底色。纯黑纯白在长时浏览下对比过硬，且与任何强调色都会"打架"。

**规则 5.2（强制）**　`Color.Surface.*` 与 `Color.Text.*` 必须成对使用，不得跨层混搭（例如 L0 背景配 L2 的文字色）。

**规则 5.3**　次级文字（`Text.Secondary`）对比度 ≥4.5:1；`Text.Tertiary` 只允许用于表头、分组标题等**非唯一信息**，不得用它承载文件名、路径、错误原因。

**规则 5.4（强制）**　彩色标签只允许以 6px 圆点出现（`Size.Tag.Dot`），最多同时显示 3 个点，超出显示 `+N` 文本。**禁止**用标签色做整行底色、图标着色、边框。

---

## 6. 强调色使用白名单

强调色三档取值（浅色 / 深色）：`#245EEB` / `#82A7FF`。两档都是 Logo 主色，深色那份不是
"提亮版"而是设计稿的另一支主色——`#245EEB` 落在 `#1A1A1C` 上只有 2.2:1，2px 竖条会消失。

| 派生 | 浅色 | 深色 | 对比（相对主题底色） |
|---|---|---|---|
| Default | `#245EEB` | `#82A7FF` | 5.06 / 6.90 : 1 |
| Hover | `#2054D2` | `#91B2FF` | 6.44 / 7.75 : 1 |
| Pressed | `#1C4AB9` | `#7191DE` | 7.6 / 5.27 : 1 |
| Subtle / 选中行填充 | `#E9EFFD` | `#202C44` | — |
| OnFill（实心块上的字） | `#FFFFFF` | `#0F1729` | 5.42 / 7.59 : 1 |

`#245EEB` / `#82A7FF` 只允许出现在以下位置，其余一律禁止：

| 允许 | 说明 |
|---|---|
| 键盘焦点框 | 2px `Brush.Border.Focus`。列表项内缩 1px（行与行紧贴），按钮外扩 2px（周围有空隙）。实现见 `Design/Styles/FocusVisual.xaml` |
| 选中行标记 | 左侧 2px 竖条（`Brush.Accent.Default`） |
| 选中行填充 | `Brush.Surface.Selected`（强调色 10% 混合，不是原色） |
| 活动标签指示 | 标签下方 2px 线 |
| 拖放插入指示 | 2px 横线 + 端点圆点 |
| 进度条 / 加载指示 | — |
| 活动导航项 | 文字与图标 |
| 链接文字 | `Brush.Text.Link` |
| 活动窗格标识 | 窗格边框 1px `Brush.Border.Strong` 加深 |

| 禁止 | 原因 |
|---|---|
| 按钮大面积填充 | 与 8 色标签争夺注意力 |
| 标题、表头、正文着色 | 破坏层级 |
| 选中行反白（蓝底白字） | 万行滚动刺眼，且与标签色冲突 |
| 装饰性线条、图标点缀 | 强调色一旦"装饰化"就失去强调语义 |

**规则 6.1**　高对比度模式下唯一允许的反白位置是选中行，通过 `Brush.Text.OnSelected` 实现——该 token 在浅/深色下等于正文色，在 HC 下等于 `HighlightTextBrush`。行模板始终绑定 `Text.OnSelected`，不写条件分支。

**规则 6.2（强制）**　落在强调色**实心块**上的文字与字形一律用 `Brush.Accent.OnFill`，
不得用 `Text.OnSelected` 或写死白色。理由：两套主题的强调色明暗相反，浅色主题要白字
（5.42:1），深色主题要深墨（7.59:1）；用同一个"看起来对"的颜色必然有一头掉到 3:1 以下。
`OnSelected` 服务的是选中**行**（浅底/深底），语义不同，不能借用。

---

## 7. 描边、圆角、阴影

**规则 7.1（强制）**　描边统一 1px（`BorderThickness.Hairline`）。**禁止 0.5px**——在 125%/150% DPI 下会被渲染为半透明灰线，比 1px 更脏。

**规则 7.2**　层级靠 `Brush.Border.Hairline`（层间）与 `Brush.Border.Strong`（容器）区分；窗口外框用 `Brush.Border.Window`。同一容器不得同时用两种描边色。

**规则 7.3（强制）**　阴影只允许出现在两个位置：右键菜单、模态对话框。且必须使用预渲染九宫格 PNG（3×3 `Border` 嵌套或 `NinePatch` 附件属性）实现，**禁止 `DropShadowEffect`**——它会把该子树降级为软件渲染，NF-04（≥55fps）直接失守。

**规则 7.4（强制）**　列表、导航栏、标签栏、状态栏的视觉树中**禁止出现** `DropShadowEffect`、`BlurEffect`、`BitmapEffect`、`OpacityMask` 配合动画。

**规则 7.5**　圆角：控件 4px，标签页 7px，菜单 8px，对话框 10px。同一控件不得出现不同半径（例如外框 8 内框 6）。

**规则 7.6**　窗口圆角按系统区分：

- Win11（Build ≥ 22000）：`DwmSetWindowAttribute(hwnd, 33 /*CORNERS_PREFERENCE*/, 2 /*ROUND*/, 4)`，系统级圆角，零成本。
- Win10 1809~21H2：保持直角。**禁止 `SetWindowRgn` 硬裁**——边缘锯齿、缩放失效、与 DPI 变化冲突。

**规则 7.7**　标题栏必须自绘（`WindowChrome`），用于两平台统一的深色标题栏与 40px 高度。自绘后拖拽区用 `WindowChrome.IsHitTestVisibleInChrome` 在按钮上放行。

**规则 7.8（强制）**　自绘标题栏的最大化尺寸必须钉在当前显示器的工作区（`rcWork`），实现在 `WindowChromeHelper` 的 `WM_WINDOWPOSCHANGING` 处理里。**不要在 `WM_GETMINMAXINFO` 里改**——`WindowChromeWorker` 会在前一个消息里把最大化窗口设成"整个显示器 + 8px 隐形边框"，覆盖掉那里算好的值；有系统标题栏时那 8px 藏在屏幕外看不见，自绘后客户区等于窗口矩形，任务栏会被整条盖住（实测副屏 125% 下溢出 68px）。

**规则 7.9**　标签卡片内部的列宽：标题列取星号（`*`），图标列与关闭键列取 `Auto`。标签有 `MinWidth`，标题短时卡片被撑开，三列全 `Auto` 的话多出来的宽度会留在卡片右侧、关闭键停在文字后面——看起来像"× 跑到左边去了"（实测 `D:\` 标签右侧空了 57px）。星号列吸收余量，× 恒定贴右。横向 `StackPanel` 测量时约束是无限宽，`Star` 在此退化为 `Auto`，不会反过来把卡片撑宽。

**规则 7.10（强制）**　自绘标题栏的对话框必须带一圈 1px `Brush.Border.Strong` 外描边（`ImmersiveChrome` 在包装时套 `Border`）。系统标题栏没了之后，对话框与主窗口背景同为 `Surface.Window`，只剩 DWM 投影在分界——深色主题下那点投影几乎看不见，弹窗会像主窗口里长出来的一块。**主窗口不加**：它最大化时贴着屏幕边，画线反而多余。

**规则 7.11**　分组标题用**一条 hairline 切段**，不要用底色。设置页曾试过给分组标题加背景色：浅色主题里 `Surface.Hover`(#F4F4F7) 与 `Surface.Window`(#F7F7F9) 只差 3/255，色块根本看不见；深色也只差一档。实现见 `Heading.Group` 样式（Border **下边线** + 上 14 / 线下 8 / 下 10 的间距），间距比分隔线本身更决定"分段感"。

**规则 7.12**　分隔线必须在**标题下方**，不能在上方。线在上方时读下来的顺序是"一条无归属的线 → 组名 → 内容"，标题看着像上一组的结尾；放到下方才是"组名 → 线 → 本组内容"，标题命名、线切段，职责不混。同理，页内边距与分组间距是"窗口变小的缓冲垫"——缩窗口时先收这两处（设置窗 800×640 → 760×600 即按此办），不要靠裁掉最后一个字段来省高度。

**规则 7.13（强制）**　**侧栏这类窄容器里的菜单，一律"右边缘贴侧栏右边界、向下展开"，用 `PlacementMode.Custom` 自己算坐标。** 三个坑，都踩过：

1. **`PlacementMode.Bottom` 是"下方 + 左边缘对齐锚点"**，不是水平居中（和直觉相反）。侧栏 227 DIP、菜单宽 210 DIP，左边缘贴上去就是向右铺，整块压到文件列表上（实测工作区菜单右缘越过侧栏 172 物理像素）。默认的 `MousePoint` 更差：位置还跟着鼠标在行内漂。
2. **行的右边缘不是侧栏右边界**。ListBox 的内边距与滚动条预留那一段不在行里，实测差 82 DIP。只贴行右边缘的话菜单会整块推出窗口左边，只剩右半截。要向上找 ListBox，用它的宽度做基准。
3. **`PlacementMode.Custom` 下 WPF 不做越界修正**，坐标给多少就放多少。左右上下都要自己 clamp，否则菜单在窗口边缘会被裁掉。下方放不下时要手动翻到锚点上方。

实现见 `MainWindow.Nav.cs` 的 `AnchorMenuRight`，6 处入口共用（工作区行、收藏项行、收藏分组行、位置标题、收藏/工作区标题、位置配置按钮）。

> 附注：`MenuDropAlignment`（`SPI_GETMENUDROPALIGNMENT`，惯用手选左手或平板模式时为 1）会改变 `MousePoint` 的铺开方向。2026-11-12 复核本机该值为 **0**，v5 写的"本机就是右对齐"属误判；上述做法与这条设置无关，任何取值下都成立。

**规则 7.14**　**右键必须吃掉事件，不能让它变成"选中这一行"。** `ListBox` 把右键当成选中操作，而侧栏的"选中"等于执行（工作区行选中＝打开工作区、收藏项选中＝打开收藏）。实测右键工作区行会先弹出"打开工作区，要替换当前标签吗"的确认框——用户只是想改名。做法是在行的 `PreviewMouseRightButtonDown` 里 `e.Handled = true` 再自己开菜单；`ContextMenuOpening` 只留给键盘（Shift+F10 / 菜单键）。

**规则 7.15**　**行的第二入口用行尾常显的「⋯」，常态次级色、悬停提亮，18px 见方，不参与 Tab 遍历。** 完全隐藏到悬停才现会让用户以为这一行没有菜单，用主色又会跟行标题抢注意力。尺寸与标签上的关闭键一致（行高 28，再大会把行顶高）；`IsTabStop=False` 是因为侧栏一屏可能二十行，各行占一个焦点停靠点会把"从侧栏走到内容区"变成几十下 Tab——键盘入口是菜单键与 Shift+F10。挂在行根 Grid 上的那份菜单由按钮与右键共用，不要写两份。

**规则 7.16（验收方法）**　**右键菜单这类 Popup 的自动化验证，只走"截图量坐标 + 鼠标点"，不要用方向键，也不要把主窗 `GetWindowRect` 和菜单坐标混算。**

- `keybd_event` / `PostMessage` 都进不了菜单：菜单开着时前台窗口仍是被遮住的主窗，必须先用 `AttachThreadInput` + `BringWindowToTop` + `SetForegroundWindow` 抢前台，否则按键落到别的窗口上。
- 抢到前台后按方向键，焦点会跳出菜单落到侧栏列表上（侧栏"选中即导航"，会静默打开一个视图），比点不中更糟。
- 主窗在 DPI 虚拟化下可能返回 DIP、Popup 返回物理像素，混算必偏。`capture` 与 `lclick` 走同一套客户区坐标，直接量截图即可（`MenuDropAlignment=0` 时菜单左缘不与右键点重合，量一次比猜十次快）。

判定改没改对，优先看落盘副作用（如 `data\tags.jsonl` 新增 `del`），再看界面。

**规则 7.17（验收方法）**　**点侧栏、列表里的"某一行"，用 UI 自动化按行序号，不要用屏幕坐标。**

坐标点行在本机实测会系统性偏移几十像素，且同一坐标两次运行会落到不同行：一次点在侧栏「蓝」上，
应用日志里收到的却是 `NavEmptyRow`（点到了分区空态）。偏移量还随窗口位置与滚动位置变化，
"用截图量、每次加个固定校正值"只能让某一次恰好点对。

可靠做法是 `AutomationId=QuickAccessList` → `ControlType=ListItem` 子项 → 第 N 项 →
`SelectionItemPattern.Select()`。行序号在数据不变时是稳定的，与 DPI、滚动、窗口位置全都无关；
顺带也验证了这条列表确实把每一行暴露成了 `ListItem`（可访问性的一部分）。
.NET 自带的 UIAutomation 客户端程序集就够，无需第三方库；脚本里避免写中文，
用 `AutomationId` 定位而不是 `Name`，可绕开 5.1 版脚本宿主的编码坑。

**规则 7.18**　**设置页里"每行一个对象 + 行内动作"的列表，行由代码生成，动作按钮用 `Button.Link`（次级色文字按钮）。** 项目数随用户增删变化，写死在 XAML 里表达不了；行内三个元素只给"色块 / 输入框 / 动作"三列 `Auto`，不加星号列——星号列会把动作推到面板最右侧，与输入框之间空出一大片，读起来不像"这一行的动作"。

删除类行内动作的确认框必须写清**实际发生的事**，不是"确定删除吗"：标签删除是"只停止显示、
记录仍在库中"，文案就写成"删除后这些标记不再显示，但记录仍保留在标签库中——重新添加同一颜色的标签即可恢复"。
用户对"删除"的默认预期是"没了"，与实现不一致时必须由文案拉平，否则就是欺骗。

---

## 8. 动效

只有三条动画被允许，其余全部瞬时：

| 场景 | 属性 | 时长 | 缓动 |
|---|---|---|---|
| 行/导航项悬停 | 仅 `SolidColorBrush.Color`（Background） | 90ms | `Easing.Standard` |
| 菜单 / 弹窗出现 | `Opacity` 0→1 + `TranslateTransform.Y` 4→0 | 130ms | `Easing.Enter` |
| 提示条（Toast）进入 | `TranslateTransform.Y` + `Opacity` | 180ms | `Easing.Standard` |

**规则 8.1（强制）**　禁止对 `Width`、`Height`、`Margin`、`Padding`、`FontSize` 做动画——触发布局重算，单次即可能超过 NF-09 的 16ms 预算。

**规则 8.2（强制）**　禁止以下场景做过渡：标签切换、目录切换、选中变化、滚动、视图模式切换。文件管理器一旦"有过渡感"就等同于"卡"。

**规则 8.3**　动画必须 `FillBehavior="Stop"` 或在 `Completed` 中移除，避免持续持有动画时钟。

**规则 8.4**　`SystemParameters.ClientAreaAnimation` 为 `false` 或系统启用"减少动画"时，所有时长降为 0（直接赋值，不播动画）。

---

## 9. 图标

**规则 9.1（强制）**　界面 chrome（导航、工具栏、命令、状态栏）全部使用字体字形 `Font.Family.Icon`（Win10：`Segoe MDL2 Assets`；Win11：`Font.Family.Icon.Fluent`）。禁止为 chrome 引入 PNG/SVG 图标集——字体字形零启动成本、单色、任意 DPI 清晰。

**规则 9.2（强制）**　码位集中定义在 `IconGlyphs.cs` 常量类中，XAML 只引用常量。禁止在 XAML 里内联写 `&#xE72B;` 之类的魔法码位。

**规则 9.3（强制）**　系统 Shell 图标只允许出现在文件行内，尺寸固定单档 16px（大图标视图为 24px）。**禁止**把 Shell 图标与字体字形放在同一视觉权重的位置——这是精致感的第一杀手。

**规则 9.4**　字形颜色只用 `Brush.Icon.Primary` / `Brush.Icon.Secondary`；活动态用 `Brush.Accent.Default`。禁止为单个图标单独定义颜色。

**规则 9.5**　Shell 图标异步提取（FR-6.5），提取期间显示占位空白而不是"正在加载"图标，避免闪烁。

---

## 10. 交互状态矩阵

列表行 / 导航项必须严格按下表实现，不允许自行发挥：

| 状态 | 背景 | 文字 | 描边 / 标记 |
|---|---|---|---|
| 常态 | 透明 | `Brush.Text.Primary` | 无 |
| 悬停 | `Brush.Surface.Hover` | `Brush.Text.Primary` | 无 |
| 按下 | `Brush.Surface.Pressed` | `Brush.Text.Primary` | 无 |
| 选中（焦点窗格） | `Brush.Surface.Selected` | `Brush.Text.OnSelected` | 左 2px `Brush.Accent.Default` |
| 选中（失焦窗格） | `Brush.Surface.SelectedInactive` | `Brush.Text.OnSelected` | 左 2px `Brush.Border.Strong` |
| 键盘焦点 | — | — | 2px `Brush.Border.Focus`，列表项内缩 1px / 按钮外扩 2px |
| 禁用 | 透明 | `Brush.Text.Disabled` | 无 |

**规则 10.1**　选中与悬停不同时叠加。选中优先，悬停只在未选中项上生效。

**规则 10.2**　双窗格焦点标识（FR-3.5）：焦点窗格整个列表区 1px `Brush.Border.Strong` 包边，失焦窗格无描边。**禁止**用发光、降透明度、灰化整个非焦点窗格。

> 包边**只在分屏时画**（`PaneView.SetActive(active, outline)` 两个参数分开传）。单窗格时全窗口只有
> 一个浏览区，包个框除了多一条线没有任何信息量。深色主题下 1px `#FF3A3A42` 的对比很弱——
> 这是有意的：焦点标识要让用户"找得到"，不能变成第二条分割线。觉得不够醒目时先看
> 左下角状态栏是否跟着换，而不是加粗包边。

**规则 10.3（强制）**　必须覆盖系统默认选中画刷，否则 `SystemColors.HighlightBrush` 会在深色模式下变成刺眼蓝块：

```xml
<Style TargetType="ListBoxItem" x:Key="Row.Item">
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ListBoxItem">
                <Grid x:Name="Root" Background="Transparent" Height="{DynamicResource Size.Row.Active}">
                    <Border x:Name="Sel" Background="Transparent" BorderThickness="0">
                        <ContentPresenter Margin="{StaticResource Padding.Row}"/>
                    </Border>
                    <Border x:Name="Bar" Width="2" HorizontalAlignment="Left"
                            Background="{DynamicResource Brush.Accent.Default}" Visibility="Collapsed"/>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Sel" Property="Background" Value="{DynamicResource Brush.Surface.Hover}"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="Sel" Property="Background" Value="{DynamicResource Brush.Surface.Selected}"/>
                        <Setter TargetName="Bar" Property="Visibility" Value="Visible"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

**规则 10.4**　右键菜单的项按**作用域**决定出现与否，不用"全列出来 + 置灰"兜住所有场景。

| 作用域 | 处理 | 当前成员 |
|---|---|---|
| 条目级（依赖选中） | 无选中时 `Collapsed`，不是 `IsEnabled=false` | 打开、标签、移除标签、重命名、复制、剪切、删除、永久删除、计算大小、属性 |
| 目录级 | 恒显示 | 收藏、新建文件夹、新建文本文档、粘贴、更多选项 |
| 两栖 | 恒显示，行为随选中态切换 | 复制完整路径（有选中复制各项，无选中复制当前目录） |

收起后必须重算分隔线：开头一条、结尾一条、连续两条都没有意义。
实现见 `PaneView.ApplyMenuScope`，条目级项在 XAML 里标 `Tag="ItemScope"`。

> 为什么不用置灰：一整排灰项既不解释"为什么不能用"，又把真正可做的操作挤到很远。
> 例外是**同一位置固定出现的命令**（如「计算大小」在只选中文件时）——那种情况用户
> 已经在条目上下文里，置灰表达的是"这次不适用"，而不是"这里没东西可选"。

---

## 11. 列表与性能红线

逐条对应验收指标，任一违反即阻塞合并。

| # | 红线 | 对应指标 |
|---|---|---|
| R-01 | `VirtualizingStackPanel.IsVirtualizing=True`、`VirtualizationMode=Recycling`、`ScrollUnit=Pixel` 三者必须同时开启 | NF-03 / NF-04 |
| R-02 | 行模板内可视元素 ≤5 个（图标 + 3 列 + 标签点槽位），嵌套 Panel ≤2 层 | T-06 |
| R-03 | 行模板内禁止 `RelativeSource FindAncestor`（每次生成走视觉树查找） | NF-04 |
| R-04 | `Recycling` 模式下禁止依赖 `Loaded` 事件做一次性初始化 | NF-03 |
| R-05 | 缩略图必须设 `DecodePixelWidth` 限制解码尺寸 + `BitmapScalingMode="LowQuality"` | NF-05 |
| R-06 | 禁止 `DropShadowEffect`/`BlurEffect` 出现在列表、导航、标签栏子树 | NF-04 |
| R-07 | 圆角与裁剪用 `Border`，禁止对高频重绘区域使用 `Path` 几何或 `Clip`+`OpacityMask` 组合 | NF-04 |
| R-08 | 滚动过程中不得发起图标/缩略图/标签查询请求（防抖后统一批量查询） | FR-6.6 / FR-4.2.4 |
| R-09 | 主题切换只替换字典 `Source`，不重建控件树；所有画刷已 Freeze | FR-5.1.1 |
| R-10 | 任何 `Loaded`/`SizeChanged` 回调内禁止磁盘 IO、Shell COM 调用、数据库访问 | P1 / P2 |
| R-11 | 列表刷新采用差异更新，禁止 `ItemsSource=null` 后重设（会丢失滚动位置与选中态） | T-04 |
| R-12 | 单元格内不得使用 `TextBlock.Inlines` 拼接多段富文本 | NF-04 |

---

## 12. 高对比度与可访问性

**规则 12.1**　`SystemParameters.HighContrast == true` 时强制使用 `Theme.HighContrast.xaml`，用户手动选择的浅/深色失效（系统优先）。

**规则 12.2**　HC 模式下：彩色标签降级为单色圆点、状态色降级为正文色、半透明填充禁用、阴影禁用。这些由主题字典统一完成，界面代码不得写 `if (HighContrast)` 分支。

**规则 12.3**　仅在 HC 下允许选中行反白，通过 `Text.OnSelected` 实现（见规则 6.1）。

**规则 12.4**　所有可交互元素必须可 Tab 到达、焦点可见（规则 10）。自定义 `FocusVisualStyle` 必须同时适配三种主题，禁止使用系统默认虚线框。

> 落地状态（2026-11-05）：`Design/Styles/FocusVisual.xaml` 定义 `FocusVisual.Ring`（按钮）与 `FocusVisual.Inset`（列表项），颜色统一取 `Brush.Border.Focus`——浅色/深色是品牌蓝，高对比度自动切到 `SystemColors.HighlightColor`。
> 此前所有自绘模板都写 `FocusVisualStyle="{x:Null}"` 且没有替代，导致纯键盘操作时焦点完全不可见（FR-10.7 / AC-08 的直接阻塞项）。
> **注意**：文字按钮（`Button.Link`）同样适用；不要因为它没有边框就省掉焦点环。

**规则 12.5**　图标按钮必须设置 `AutomationProperties.Name`；纯图标无文字区域必须提供 `ToolTip`。

**规则 12.6**　行最小高度 26px，不得为"视觉精致"降低到 24px 以下（触屏与运动障碍用户）。

---

## 13. 禁止清单（汇总）

| # | 禁止 | 替代做法 |
|---|---|---|
| F-01 | Acrylic / Mica 实时模糊 / `SetWindowCompositionAttribute` | 四级表面 + 1px 描边 |
| F-02 | `DropShadowEffect` 用于列表、卡片、导航 | 1px 描边或九宫格 PNG（仅菜单/对话框） |
| F-03 | 0.5px 描边 | 1px + `UseLayoutRounding` |
| F-04 | 选中行蓝底白字 | `Surface.Selected` 填充 + 左竖条 |
| F-05 | 0.5px 以下的"精致"间距、非 4 倍数间距 | `Space.*` 五档 |
| F-06 | `Medium`/`SemiBold` 字重 | `Normal` / `Emphasis` |
| F-07 | 布局属性动画（宽高、边距、字号） | 只动 `Opacity` / `Transform` / `Color` |
| F-08 | 页面/标签切换滑动转场 | 瞬时切换 |
| F-09 | 彩色标签做整行底色或图标着色 | 6px 圆点，最多 3 个 |
| F-10 | 强调色用于装饰、标题、大面积填充 | 仅用于焦点/选中/活动/进度 |
| F-11 | chrome 混用 PNG 图标与字体字形 | 全部字体字形（MDL2 / Fluent） |
| F-12 | 列表内 `Grid.IsSharedSizeScope`、`FindAncestor`、`Inlines` 富文本 | 固定列宽 + `x:Reference` + 单 `TextBlock` |
| F-13 | 用 `SetWindowRgn` 硬裁圆角 | Win11 用 DWM，Win10 保持直角 |
| F-14 | 界面 XAML 内联颜色与魔法数字 | 全部走 token |
| F-15 | 用 `Opacity` 灰化非焦点窗格 | 1px 描边区分焦点 |
| F-16 | 为功能新增视觉效果而新增颜色键 | 复用现有 41 个画刷键，新增须评审 |

---

## 14. PR 自检清单

提交前逐条勾选，未勾选不予合并：

- [ ] XAML 中无颜色字面量、无魔法数字（全局搜索 `#`、`Red`、`White` 与裸数字）
- [ ] 新增画刷键三份主题字典已同步，`Brush.*` + `Theme.Name` 键数一致（当前 42）
- [ ] 所有新增 `SolidColorBrush` 带 `po:Freeze="True"`
- [ ] 新增控件只使用表格中定义的字号/字重，未出现 `Medium`/`SemiBold`
- [ ] 数值列已开启 `Typography.NumeralAlignment="Tabular"`，且与文本列同为左对齐（规则 4.2）
- [ ] 列表改动已确认虚拟化三件套仍开启，行模板元素数 ≤3
- [ ] 未引入任何 `Effect`（`DropShadowEffect` / `BlurEffect`）
- [ ] 动效只用了三条白名单，时长为 token
- [ ] 字号相关属性（`FontSize` / `FontFamily` / `LineHeight` / 行高 / 列宽）全部 `DynamicResource`，未出现 `StaticResource`
- [ ] 界面未单独覆盖 `FontSize`，全部套用 `Typography.xaml` 中的样式
- [ ] 字号 9 / 14 / 24pt 三档各目视一遍：无裁切、无重叠、列不截断、滚动位置不跳
- [x] 三种主题目视检查：浅色、深色、高对比度下无文字不可读、无控件错位（AC-06，记录见 `docs/ac06-visual-acceptance.md`）
- [ ] 键盘走一遍：Tab 顺序、焦点可见、Space/Enter 可操作
- [ ] 冷启动复测（NF-08 门禁通过）

---

## 15. 字号缩放（FR-5.1.5 ~ FR-5.1.7）

### 15.1 变量

| 项 | 值 |
|---|---|
| 变量 | 界面字号，单位 **pt**，存 `settings.json` → `appearance.fontSizePt` |
| 范围 | **8–24pt 连续可调，步进 1**（`null` = 跟随系统） |
| 默认 | 跟随系统（`SystemFonts.MessageFontSize`，100% DPI 下 12px ≈ 9pt） |
| 内部单位 | 计算一律用 px：`px = pt × 4 / 3`。pt 只用于设置页展示与持久化 |
| 派生系数 | `Font.Scale = 行字号px / 13`（只读，供非 token 场景使用） |
| 字体族 | 同批下发：`Font.Family.UI` 由用户选择覆盖（FR-5.1.5） |

**规则 15.1（强制）**　字号只有这一个变量。禁止出现"导航字号""状态栏字号"这类独立设置项——多变量必然导致布局总有一处错位。

**规则 15.2**　pt 与 px 的换算只发生在 `FontScaleService` 一处。XAML、ViewModel、设置页均不得自行乘除，避免 4/3 系数散落各处。

### 15.2 派生关系

改字号时由 `FontScaleService.Apply(fontSizePt, compact)` 一次性写回下列资源键，界面只读：

| 资源键 | 来源 |
|---|---|
| `Font.Size.Caption` / `Body` / `Row` / `Subtitle` / `Title` | 基准 × 比例（规则 4.6） |
| `Font.LineHeight.Caption` / `Body` / `Row` | 各自字号 × 1.45 / 1.667 / 1.538 |
| `Size.Row.Comfortable` / `Compact` / `Active` | 行高 + 行内余量，取偶数，有下限 |
| `Size.Row.Header` / `Nav.Item.Height` / `Tab.Height` | 同上 |
| `Size.Tab.MinWidth` / `MaxWidth` / `Title.MaxWidth` | 基准宽 × 比例取偶数；标题可用宽由最大宽减去图标、关闭按钮与内边距（72）得出，下限 64 |
| `Size.TitleBar.Height` / `Toolbar.Height` / `StatusBar.Height` | 同上 |
| `Column.Name.MinWidth` / `Date` / `Type` / `Size` | 基准列宽 × 比例，取偶数 |
| `Font.Scale` | 只读系数 |
| `Font.Family.UI` | 用户选择的字体族（无则保持默认栈） |

**不随字号变化**：`Size.Icon.*`（FONT-06 明确要求图标尺寸不跟字号）、`Space.*`、`Padding.*`、`Size.ScrollBar.Thickness`、`Size.Splitter.Thickness`。

**例外**：`Size.Nav.Width.*` 不是缩放，而是**上下限**——导航栏 `Width="Auto"` 取内容宽度并夹在 184–320 之间。写死 184 时英文 24pt 的 "Documents / Downloads" 会被硬裁掉最后几个字母（AC-06 实测），中文与常规字号下内容不足 184，宽度与写死时完全一致。

### 15.3 实现约束

| # | 规则 |
|---|---|
| A-01 | 只有 `FontScaleService.Apply` / `ApplyFontFamily` 允许写这些资源键。禁止在 ViewModel、转换器、XAML 里直接改 `Application.Resources` |
| A-02 | 变更时**不得**重建控件树、不得重设 `ItemsSource`、不得重新枚举目录（FONT-04） |
| A-03 | 变更前后必须 `CaptureAnchor` / `RestoreAnchor`，按项目索引恢复滚动位置（FONT-05） |
| A-04 | 单次切换 <100ms，UI 线程无 >16ms 阻塞（FONT-08） |
| A-05 | 图标尺寸固定，不参与缩放；字号变大时先扩列宽，再折叠列，最后才允许省略 |
| A-06 | 用户手工拖过的列宽按比例缩放**一次**，之后不再自动改写；未拖过的列每次按新基准重算 |
| A-07 | 字号变更后若文字被裁切，加宽列或折叠列，**禁止**回退缩小字号来凑 |
| A-08 | 字体加载超时 >500ms 时保持原字体并提示，不得阻塞 UI（FONT-08） |

### 15.4 布局自适应

| 场景 | 行为 |
|---|---|
| 字号 >16pt | 名称列最小宽按比例提升，日期/类型列随后收缩，避免文件名只剩省略号（FONT-06） |
| 字号 >16pt | 状态栏隐藏总大小，仅保留"已选中 N 项" |
| 窗口宽度不足 | 按 类型 → 大小 → 日期 顺序折叠列，名称列始终保留 |
| 紧凑密度 + 大字号 | 行高取两者较大者，**不允许**文字被压进 26px |
| 高对比度 + 大字号 | 正常渲染，仅圆点/阴影按 HC 规则降级 |
| 字体族切换 | 只换字形，不改任何尺寸；行高由新字体的实际度量决定，不做硬编码换算 |

### 15.5 可读性红线

- 行字号下限 8pt（≈10.7px），caption 下限 9px，不得更低。
- 行高 ≥ 字号的 1.5 倍，否则行间无呼吸、易读性下降。
- 任意档位下正文对比度 ≥4.5:1（`Text.Tertiary` 仍只用于非唯一信息）。
- 字体族切换后若某字形缺字，落到 `Font.Family.UI` 的备用栈，不得显示豆腐块。

### 15.6 验收

- 按 AC-06：**9 / 14 / 24pt 三档** × 浅色 / 深色 / 高对比度共 9 组，逐组检查：无裁切、无重叠、无控件错位、滚动位置不跳。已走查完毕（9/9 PASS），记录见 `docs/ac06-visual-acceptance.md`；英文界面另测 14/24pt，发现两处只在英文大字号下暴露的缺陷（设置页底部按钮被挤出窗口、导航栏文字被硬裁）。
- 自动化断言三条：行高 token 与行字号成正比；列宽之和与列表可视宽一致或正确触发横向滚动；切换耗时 <100ms。

### 15.7 容器宽度红线

- 凡是装文字的容器（`ComboBox` / `TextBox` / 带 `TextTrimming` 的 `TextBlock`），宽度只准三选一：
  1. 绑 `Size.*` 令牌——基准值按 **12pt 设计稿**写，计算时用 `FontMetrics.BaselineScale` 折算回 12pt，否则 12pt 布局会跟着漂；
  2. `Grid` 的 `*` 列 + `TextTrimming`——横向 `StackPanel` 给子项无限宽，`TextTrimming` 永远不触发；
  3. `Auto`（内容短且长度可控，如"12 项"）。
- **禁止字面量像素**。24pt 下文字宽约 ×2，容器不动就必然截断。此条来自四次同一根因的缺陷（AC-06 §4.2 / §4.6 / §4.8 / §4.10）。
- 同一页里同类控件用同一档宽度。可选档位只有两档：`Size.Combo.Width`（常规）、`Size.Combo.Width.Narrow`（数值），不为单个控件新开档。
- 换了底色的状态必须同时给文字色：选中/高亮/警告三态在任何主题下都要能读（AC-06 §4.9）。已有画刷：`Brush.Text.OnSelected`（选中底色上的文字）、`Brush.Text.OnAccent`（强调色实底上的文字）。

### 15.8 设置页分页

- 按**用户想干什么**分页，不按"设置项属于哪个对象"分页。当前六页：常规 / 外观 / 文件列表 / 操作与确认 / 隐私与存储 / 关于。一个 `Settings` 子对象被拆到几页是正常的（`BehaviorSettings` 就被四页瓜分），分页边界服务于用户，不服务于序列化结构。
- 左侧导航只放页名；页标题与副标题固定在内容区顶部不滚动；`恢复本页默认` 在标题栏右上，`恢复全部设置` 在底栏右侧。
- 新增设置项先回答"它属于哪一页"，答不上来说明分页该重划；不许为单项新设置加第七页。
- 每页内容高 ≤ 一屏（14pt 以内）。用 `OffscreenShot --settings-metrics` 量，倍数 > 1.02 视为超标，必须先合并行、再考虑删减说明，最后才允许滚动。
- 组内说明合并成一条放在组尾，不许每条设置后跟一句解释；低频且体积大的块（如八行颜色名）默认折叠，只报摘要。

---


## 附录 A：Token 索引

| 分组 | 键前缀 | 数量 | 引用方式 |
|---|---|---|---|
| 字体与缩放 | `Font.*` | 17 | Static（`Font.Scale` 只读） |
| 间距 | `Space.*` | 8 | Static |
| 尺寸与内边距 | `Size.*`、`Padding.*` | 31 | `Size.*Height` / `Size.Icon.List` 为 Dynamic |
| 圆角 | `Radius.*`、`CornerRadius.*` | 8 | Static |
| 描边 | `BorderThickness.*` | 3 | Static |
| 动效 | `Duration.*`、`Easing.*`、`Motion.*` | 6 | Static |
| 列宽 | `Column.*` | 4 | Dynamic |
| 层级 | `ZIndex.*` | 3 | Static |
| 颜色 | `Color.*` | 41 | Dynamic，仅浅色/深色内部中转；高对比度直接绑 `SystemColors` |
| 画刷 | `Brush.*` | 41 | Dynamic |
| **合计** | `DesignTokens.xaml` | **80** | — |
| **合计** | 浅色 / 深色字典 | **83**（41 色 + 41 刷 + `Theme.Name`） | — |
| **合计** | 高对比度字典 | **42**（41 刷 + `Theme.Name`，无 `Color.*` 层） | — |

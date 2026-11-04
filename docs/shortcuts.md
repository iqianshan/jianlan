# 快捷键

> 对应 FR-10.6。键位一经发布不再变更；新增键位必须避开下表已占用组合。
>
> **实现状态（M1）**：`Alt+←/→/↑`、`Backspace`、`Ctrl+L`、`F5`、`Ctrl+R`、`Ctrl+Shift+1/2`、
> `Ctrl+Shift+D`、`Ctrl+,`、`Enter`、`F2`、`Delete`、`Shift+Delete`、`Ctrl+C/X/V`、`Ctrl+A`、
> `Ctrl+Shift+N`、`Ctrl+Shift+C`、`Alt+Enter`、菜单键（`VK_APPS`）已可用。
> `Ctrl+Shift+3`（大图标）**不绑定**：该视图决定不做，见 `docs/adr/0005-large-icon-view.md`。
>
> **实现方式**：键位表在 `Lingpane.App/Input/KeyBindings.cs`，采用**双入口**：
>
> 1. 主入口是窗口消息 `WM_KEYDOWN` / `WM_SYSKEYDOWN`（`HwndSource` 钩子），按**物理键码（VK）**
>    匹配。输入法（TSF/IME）会在 WPF 之前改写 `Key`，`Ctrl+,` 这类标点组合在中文输入法下
>    经常收不到 `PreviewKeyDown`——这也是"`Ctrl+C/V` 正常、`Ctrl+,` 无反应"的真实原因。
> 2. 兜底是 `PreviewKeyDown`，把 `Key.System` / `Key.ImeProcessed` / `Key.DeadCharProcessed`
>    归一化后再查同一张表，覆盖没有物理键的合成输入。
>
> **不使用** `Window.InputBindings`：`KeyGesture` 对 `,` 这类 OEM 键的匹配不可靠，
> 且不认 `Key.System`，匹配规则藏在框架里无法断言。
> 右键菜单的键位提示由同一张表填入，不存在菜单与实现不一致的可能。
>
> 排查工具：`Lingpane.exe --keydebug` 会把每条按键消息写进 `%LOCALAPPDATA%\Lingpane\logs\keydebug.log`
> （物理键码、修饰键、焦点元素、命中的命令），默认关闭，不写盘。

## 1. 标签页

| 快捷键 | 动作 |
|---|---|
| `Ctrl + T` | 新建标签页 |
| `Ctrl + W` | 关闭当前标签页 |
| `Ctrl + Shift + T` | 恢复最近关闭的标签页 |
| `Ctrl + Tab` | 下一个标签页 |
| `Ctrl + Shift + Tab` | 上一个标签页 |
| `Ctrl + 1…8` | 跳到第 N 个标签页 |
| `Ctrl + 9` | 跳到最后一个标签页 |
| `Ctrl + Shift + Tab`（在标签栏上） | 拖动调整顺序（M2） |

## 2. 导航

| 快捷键 | 动作 |
|---|---|
| `Alt + ←` | 后退 |
| `Alt + →` | 前进 |
| `Alt + ↑` | 上一级 |
| `Backspace` | 上一级 |
| `Ctrl + L` | 聚焦地址栏并全选 |
| `Enter`（地址栏内） | 跳转到输入的路径 |
| `F5` | 刷新当前目录 |
| `Ctrl + R` | 刷新当前目录 |
| `Ctrl + D` | 收藏当前目录 |
| `Ctrl + H` | 显示/隐藏历史记录 |

## 3. 视图

| 快捷键 | 动作 |
|---|---|
| `Ctrl + Shift + 1` | 详细信息视图 |
| `Ctrl + Shift + 2` | 列表视图 |
| `Ctrl + Shift + 3` | 大图标视图（**不实现**，见 ADR-0005，键位保留不绑定） |
| `Ctrl + 滚轮` | 图标大小（仅大图标视图生效，随该视图一并搁置） |
| `Ctrl + Shift + D` | 切换主题（跟随系统 → 浅色 → 深色） |
| `Ctrl + Shift + H` | 显示/隐藏隐藏文件 |
| `F11` | 预览窗格开关 |
| `Ctrl + B` | 侧边导航栏开关 |

## 4. 文件操作

| 快捷键 | 动作 |
|---|---|
| `Enter` | 打开选中项 |
| `Ctrl + A` | 全选 |
| `Ctrl + C` / `Ctrl + X` / `Ctrl + V` | 复制 / 剪切 / 粘贴 |
| `Delete` | 删除到回收站 |
| `Shift + Delete` | 永久删除（需二次确认） |
| `F2` | 重命名 |
| `Ctrl + Shift + N` | 新建文件夹 |
| `Alt + Enter` | 打开属性对话框 |
| `Ctrl + Shift + C` | 复制完整路径 |
| 菜单键（`VK_APPS`） | 打开上下文菜单（与右键等效） |

## 5. 双窗格（M2）

| 快捷键 | 动作 |
|---|---|
| `Tab` | 切换焦点窗格 |
| `F6` | 切换焦点窗格（与 `Tab` 等价，兼容 Total Commander 习惯） |
| `Ctrl + \` | 开/关右侧窗格 |
| `Ctrl + Shift + ←/→` | 把选中项移动到另一窗格 |

## 6. 标签色（M2）

| 快捷键 | 动作 |
|---|---|
| `Alt + 1…8` | 给选中项打上第 N 号颜色标签 |
| `Alt + 0` | 清除选中项的标签 |

## 7. 应用

| 快捷键 | 动作 |
|---|---|
| `Ctrl + ,` | 打开设置 |
| `F1` | 快捷键帮助 |
| `Alt + F4` | 退出 |
| `Ctrl + Shift + W` | 关闭窗口（含全部标签页） |

## 8. 冲突规避

以下组合**已占用，不得分配给新功能**：

`Ctrl+T` `Ctrl+W` `Ctrl+Shift+T` `Ctrl+Tab` `Ctrl+1…9` `Alt+←→↑` `Backspace` `Ctrl+L`
`F5` `Ctrl+R` `Ctrl+D` `Ctrl+H` `Ctrl+Shift+1…3` `Ctrl+Shift+D` `Ctrl+,` `Ctrl+滚轮` `F11` `Ctrl+B` `Enter`
`Ctrl+A/C/X/V` `Delete` `Shift+Delete` `F2` `Ctrl+Shift+N` `Alt+Enter` `Ctrl+Shift+C`
`Tab` `F6` `Ctrl+\` `Alt+1…8,0` `Ctrl+,` `F1` `Alt+F4` `Ctrl+Shift+W`

实现要求：

- 键位集中定义在 `Lingpane.App/Input/KeyBindings.cs`，禁止在 XAML 的 `InputBindings` 里散写
- 快捷键表与 `docs/shortcuts.md` 必须同源，改一处即改两处
- 所有快捷键动作必须同时存在于右键菜单或工具栏，不做"只有快捷键才知道"的功能

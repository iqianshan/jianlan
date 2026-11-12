# 弹出层定位盘点（2026-11-12）

起因：侧栏「⋯」点开后菜单整体偏右、压住文件列表，用户反映"点击后都是右对齐"。
本文把应用内所有弹出层列一遍，标出**当前定位方式**与**是否需要改**。

## 1. 实测环境

- 便携版 `publish/Lingpane.exe`，主屏 1920×1080，125% 缩放。
- 窗口 `MoveWindow(260,120,1200,820)`，实际物理 `(325,150) 1500×1025`。
- 鼠标事件用 `tools/window-drive/window_drive.py` 的 `lclick`/`rclick`（`SetInput` 真实注入），
  截图用 `capture`（抓窗口可见矩形）与 `fullshot.py`（抓整屏，含独立 Popup HWND）。
- 读数为物理像素，**相对窗口客户区左上角**。

## 2. 盘点

WPF 的 `PlacementMode.Bottom` 是"下方 + **左边缘**对齐锚点"，不是水平居中；`MousePoint`
是"左上角贴鼠标点"。两者都向**右**铺开——这是 Windows 菜单的默认方向，不是本机设置造成的。

| # | 入口 | 定位 | 实测 | 判定 |
|---|---|---|---|---|
| 1 | 侧栏「工作区」行「⋯」/ 右键 | `Bottom` | 菜单 `x 247–494`，左边缘贴按钮，右缘越过侧栏（322）压住文件列表 172px | **要改** |
| 2 | 侧栏「收藏」行「⋯」/ 右键 | `Bottom` | 同 1 的模板与代码路径；本机收藏为空，未实测 | **要改** |
| 3 | 侧栏分区标题右键（收藏 / 工作区） | `MousePoint` | 未实测，锚点在 258px 侧栏内，与 1 同类 | **要改** |
| 4 | 侧栏「位置」标题右键 | `MousePoint` | 同上 | **要改** |
| 5 | 侧栏齿轮「配置位置」 | `MousePoint` | 菜单 `x 425–631`，左缘贴在齿轮右侧 50px，压住文件列表 | **要改** |
| 6 | 收藏分组行右键（改名 / 移除分组） | `MousePoint` | 同上，锚点在侧栏内 | **要改** |
| 7 | 标签页右键 | `MousePoint` | 菜单 `x 261–508`，向下展开，落在内容区上方 | 不改 |
| 8 | 文件项右键 | `MousePoint` | 左缘 = 鼠标点 +13，标准右下方展开 | 不改 |
| 9 | 文件列表空白右键 | `MousePoint` | 右缘贴住窗口右边界，WPF 自动避让 | 不改 |
| 10 | 纵向分隔条右键 | `MousePoint` | 锚点即鼠标位置，正常 | 不改 |
| 11 | 横向分隔条右键 | `MousePoint` | 同上 | 不改 |
| 12 | 主页工作区卡片右键 | `MousePoint` | 卡片在内容区中部，空间充足 | 不改 |
| 13 | 菜单子项（标签 / 打开方式…） | `Popup Placement=Right` | 在主菜单右侧展开 | 不改 |
| 14 | 设置页 ComboBox 下拉（15 个） | `Popup Placement=Bottom` | 宽度与控件一致，不会横向溢出 | 不改 |
| 15 | 地址栏路径补全 | `Popup Placement=Bottom` | 宽度跟随 `AddressBox` | 不改 |
| 16 | 五个对话框（设置 / 确认 / 重命名 / 许可 / 文本输入） | `CenterOwner` | 与菜单定位无关 | 不改 |

结论：**要改的是第 1–6 项，全部在左侧栏内**。共同点是锚点落在 258px 宽的窄容器里，
菜单宽度（210–262 DIP）超过容器，向左或向右铺都会越界；差别只在越到哪一边。

## 3. 改法

把侧栏这 6 处改成"右边缘贴锚点、向左铺"，菜单就落在侧栏内（工作区菜单 262 > 258，
右缘贴行尾时左缘仍差 12px，WPF 会把整块往右推回工作区，可接受）。

`PlacementMode` 没有现成的"下方 + 右对齐"，要用 `Custom` + 回调：

```csharp
static void AnchorRight(ContextMenu menu, FrameworkElement target)
{
    menu.PlacementTarget = target;
    menu.Placement = PlacementMode.Custom;
    menu.CustomPopupPlacementCallback = (popup, anchor, _) =>
        [new CustomPopupPlacement(
            new Point(anchor.Width - popup.Width, anchor.Height),
            PopupPrimaryAxis.Horizontal)];
}
```

`popup.Width` 在回调里已经是测量后的实际宽度，不需要事先知道菜单多宽。
越出工作区时 WPF 仍会自动挪回，不需要自己 clamp。

## 4. 未确认

`PlacementMode.Center`（`PaneView.xaml.cs` 的 `ShowContextMenu`，键盘菜单键唤起文件菜单）
把菜单放在文件列表正中。键盘操作没有鼠标位置，但"正中央"未必好过"左上角"。
这一项没有实测，等确认要不要一起动。

## 5. 落地（2026-11-12 下午）

第 1–6 项已按第 3 节改完，统一走 `MainWindow.Nav.cs` 的 `AnchorMenuRight`。

改的过程中发现第 3 节的方案不够：只把坐标算成 `anchor.Width - popup.Width` 会把菜单整块
推出窗口左边（只剩右半截）。补了三处：

1. **基准换成侧栏的 ListBox**。行的右边缘不等于侧栏右边界，ListBox 的内边距与滚动条
   预留差了 82 DIP。
2. **左右上下自己 clamp**。`PlacementMode.Custom` 下 WPF 不做越界修正。
3. **下方放不下翻到上方**。工作区行在窗口底部，菜单只能向上展开。

实测：工作区菜单完整可见、落在侧栏内；`x` 方向仅在菜单比侧栏宽的那几像素处压到内容区。

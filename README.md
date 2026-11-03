# Lingpane · 棂窗

轻量、原生、启动即用的 Windows 文件管理器。双窗格、多标签、彩色标签，基于 .NET 9 + WPF。

> 棂（窗棂，中式窗格）＋ pane（英文窗格）——两个语言里指的是同一个东西。

**当前状态：M0 性能基线阶段，尚未发布。** 代码可编译可运行，功能仅覆盖目录浏览。

---

## 实测启动耗时

测量环境：AMD Ryzen 7 PRO 5850U / Windows 11 / Release + ReadyToRun / 20 次取值。

| 方案 | 冷启动 P50 | 备注 |
|---|---|---|
| **Lingpane（当前）** | **~605 ms** | 关闭启动期 IME 初始化后 |
| WPF 最小空窗口 | ~1100 ms | 同环境对照，说明 1.1s 是框架基线而非业务代码 |
| WinForms 最小空窗口 | ~250 ms | 同环境对照 |
| Files（社区版） | 1.5–3 s | 官方 issue 与实测反馈 |

启动耗时的最大单项来自**中文输入法上下文的创建**（约 500 ms）。文件浏览不需要输入，
因此启动阶段关闭 IME，首次进入文本输入场景时再启用。

> 早期目标为 P50 ≤ 400 ms。M0 实测表明该指标在 WPF 上不可达（空窗口即 1.1 s），
> 现按实测调整为 **P50 ≤ 700 ms**，并保留 CI 门禁防止回退。

## 已完成（M0）

- 目录枚举（`FileSystemEnumerable`，零多余分配）
- 系统图标异步提取（按扩展名，不访问磁盘）
- 列表虚拟化（Recycling + Pixel 滚动）
- 启动耗时埋点与测量脚本（`tools/startup-bench`）
- 设计 Token 体系（四级表面 / 三主题 / 8–24pt 字号缩放）

## 规划中

多标签页 · 双窗格 · 彩色标签 · 收藏书签 · 工作区 · 预览窗格 · 设置中心

完整需求见 [`docs/requirements.md`](docs/requirements.md)，界面规范见 [`docs/ui-guidelines.md`](docs/ui-guidelines.md)。

## 构建

```powershell
# 需要 .NET 9 SDK
dotnet build

# 发布（启用 ReadyToRun）
dotnet publish src/Lingpane.App -c Release -r win-x64 --self-contained false -o publish

# 测启动
pwsh tools/startup-bench/Measure-Startup.ps1 -Exe publish/Lingpane.exe -Runs 20
```

## 设计原则

1. **首帧优先**：窗口先出现，内容后填充
2. **一切延迟**：启动路径上只做必须做的事
3. **失败不崩溃**：任何非关键路径异常都不得终止进程
4. **零常驻**：不装服务、不开机自启、不后台驻留

详见 [`docs/ui-guidelines.md`](docs/ui-guidelines.md) 与 [`docs/adr/`](docs/adr/)。

## 许可证

[Apache-2.0](LICENSE)。Copyright 2026 谦山。

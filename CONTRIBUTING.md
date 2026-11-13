# 参与开发

## 先读这三份

| 文档 | 什么时候必须读 |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | 任何改动之前。分层依赖不可逆 |
| [`docs/ui-guidelines.md`](docs/ui-guidelines.md) | 碰任何 XAML。本文件是强制规范，不是建议 |
| [`docs/performance-gate.md`](docs/performance-gate.md) | 改启动路径、列表、图标、枚举时 |

## 硬性要求

1. **不引入启动期 IO**。首帧之前不得读磁盘、开数据库、调 Shell。新代码要先回答"能不能挪到 `Loaded` 之后"
2. **XAML 无颜色字面量、无魔法数字**。全部走 token，见界面规则 §2
3. **`Jianlan.Core` 不引用 WPF**。它必须能脱离 UI 单测
4. **三份主题字典键集保持一致**（当前 83 键）。加颜色必须三份同时加
5. **提交前跑启动测量**并在 PR 描述里贴 P50/P95

## 提交信息

```
<类型>: <一句话说明>

类型：feat | fix | perf | docs | refactor | test | chore
```

例：

```
perf: 图标缓存改为按扩展名索引

滚动 10000 项时不再触发 SHGetFileInfo，实测帧率从 48 提升到 61。
```

## PR 自检

复制 [`docs/ui-guidelines.md`](docs/ui-guidelines.md) §14 的清单，逐条勾选。

## 不接受

- 为"看起来更现代"引入模糊、阴影、过渡动画
- 未经性能预算评审就添加常驻后台任务
- 修改 `docs/` 里的指标数字而不附测量数据
- 一次 PR 混入多个不相关改动

## 开发环境

需要 .NET 9 SDK。不需要 Visual Studio（VS Code + C# DevKit 足够），
也不需要单独安装 Windows SDK——相关引用包由 NuGet 还原。

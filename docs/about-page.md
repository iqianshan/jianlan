# 关于页实现方案

> 对应 `docs/requirements.md` FR-10.8、FR-10.9、AC-10。阶段：**0.2.0**。
> 依赖：`docs/i18n.md` 先落地——关于页文案全部走资源键，否则又要抽一遍。

> **0.2.1 变更（v3 走查）**：形态从「独立窗口」改为「内嵌设置页第八页」。
> 下文 §二 的方案对比与 §五、§六 里的窗口尺寸，是 0.2.0 的决策记录，保留作依据；
> 现状以实现为准——
>
> - 内容是 `src/Jianlan.App/Views/AboutView.xaml(.cs)`（`UserControl`），
>   由 `SettingsWindow` 的 `PageAbout` 直接承载；`AboutWindow` 已删除。
> - 理由：入口本来就只有设置页那一颗按钮，点开再弹一扇 480×680 的窗，
>   等于把同一份内容放到更远的地方，还多一次关闭。§二 里"会把设置页拉得很长"的顾虑，
>   在设置页改成分页导航后不成立——关于页本来就是空的。
> - 三个许可全文仍然开独立的 `LicenseWindow`：11KB 法律文本内嵌进来，
>   这一页会变成几千行，且它不该参与本地化。
> - 版本号、运行环境、目录、诊断四段与 0.2.0 完全一致，没有删减。
> - 品牌区由两行变三行：名称（中英并列）/ 版本号与构建号 / **Slogan**（`About.Tagline`，
>   中文「更简洁、轻量、可控的 Windows 文件资源管理器」/ 英文 *A simpler, lighter, more
>   controllable file manager for Windows*）。Slogan 排在名称下方而不是上方——
>   它是"这个程序是什么"的一句话，不该跟产品名抢第一落点。

## 一、一个必须先分清的前提

Apache-2.0 的合规要求是「**随分发物提供** LICENSE 与 NOTICE，并保留变更声明」，**不要求有关于页**。
关于页是"让用户能找到这些文件"的入口。

所以：

| 阶段 | 合规动作 |
|---|---|
| **0.1.0 发布前（必须）** | 安装包与便携 zip 内附 `LICENSE`、`NOTICE`、`THIRD-PARTY-NOTICES.md`；README 里给出链接 |
| **0.2.0** | 关于页补齐"程序内可见入口"，满足 FR-10.8 的验收表述 |

这也是为什么关于页可以放后期——它补的是"入口"，不是"义务"。

## 二、形态选择

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| A. 设置窗口内加「关于」分节 | 零新窗口 | 许可证全文 + 依赖清单是长文本，会把设置页拉得很长；设置页当前是单 `ScrollViewer` 顺序分区，加长后首屏要滚很久才能看到隐私开关 | ⚠️ 0.2.1 重新采纳（设置页已分页，"拉长"不再成立） |
| B. **独立 `AboutWindow`（非模态，480×680）** | 长文本独立滚动；可单独截图传播；不干扰设置页布局 | 多一个窗口、多一份 XAML | 0.2.0 采用，0.2.1 移除 |
| C. 独立进程 | 隔离 | 与 P3 单进程单窗口冲突 | ❌ |

入口：设置页底部（「隐私」分节之后）一个 `关于 Jianlan` 按钮。不加新快捷键（`F1` 已是快捷键帮助，
`Ctrl+,` 是设置，再占一个不值）。

> 0.2.1：入口即「关于」页本身，上述按钮已删除。

## 三、页面内容

| 区块 | 内容 | 数据来源 | 说明 |
|---|---|---|---|
| 头部 | 应用图标、`Jianlan` / `简览`、一句话定位 | 资源 | 名称本身不翻译 |
| 版本 | `0.1.0` + 构建标识（短 commit） | `AssemblyInformationalVersionAttribute`（`0.1.0+2d31cdc`，取 `+` 后 7 位另排小字） | 用户报 bug 时能定位到具体提交 |
| 运行环境 | .NET 版本、OS 版本与内部版本号、架构、进程位数 | `RuntimeInformation.FrameworkDescription` / `OSDescription` / `ProcessArchitecture`、`Environment.OSVersion.Version` | 贴 issue 用 |
| 许可 | `Apache-2.0`；「查看许可证全文」「查看 NOTICE」 | 内嵌资源 | 见 §四 |
| 第三方依赖 | 名称 / 版本 / 许可证 / 项目地址 | `THIRD-PARTY-NOTICES.md`（内嵌） | 见 §五 |
| 目录入口 | 「打开配置目录」「打开日志目录」「打开数据目录」 | `AppPaths`（已有 `LogsDir` 等） | FR-10.9 |
| 诊断 | 「复制诊断信息」→ 剪贴板（版本 + OS + 架构 + 内存 + 三个目录路径） | 运行时拼装 | 减少来回问"你什么环境" |
| 隐私 | 「不收集遥测、不联网（除手动检查更新跳转外）」 | 资源键，与设置页同一段 | FR-5.3.7 |
| 更新 | 「检查更新」→ 打开 `https://github.com/jianlan/jianlan/releases` | 静态 URL | 不做网络请求（符合 §8.2 B11 与"不联网"承诺） |

布局：单列 `ScrollViewer`，区块之间用 `Text.SectionTitle` 分隔，与设置页视觉一致（复用现有 Token 与样式，
不新增设计资产）。

## 四、许可证全文怎么放

**不要把 11KB 文本写进 XAML**：会进 BAML、增加程序集体积与加载成本，且许可证内容不该参与本地化。

做法：

```xml
<!-- Jianlan.App.csproj（EnableDefaultNoneItems=false，需显式声明） -->
<ItemGroup>
  <EmbeddedResource Include="..\..\LICENSE" LogicalName="Legal.LICENSE" />
  <EmbeddedResource Include="..\..\NOTICE"  LogicalName="Legal.NOTICE" />
  <EmbeddedResource Include="..\..\THIRD-PARTY-NOTICES.md" LogicalName="Legal.ThirdParty" />
</ItemGroup>
```

- 点击「查看许可证全文」→ 读流 → 显示在一个 640×520 的只读窗口中（`TextBox` + `IsReadOnly` +
  `FontFamily=Consolas`，等宽才好读）；不引入 Markdown 渲染，`.md` 用纯文本展示即可。
- 三个文件的读取都在**打开瞬间**发生（<1ms），不参与启动路径。
- 文件缺失（构建配置错误）→ 显示「许可证文件缺失」+ 日志，不崩。

## 五、第三方依赖清单怎么来

### 现状

框架依赖发布（`--self-contained false`），业务 NuGet 依赖接近于零，实际随包分发的是
**.NET 运行时与 WPF 框架组件**（MIT / Apache-2.0 混合，以 .NET 运行时许可为准）。

### 生成方式

> **落地时改成"校验"（2026-11-09）。** 原计划是脚本生成整份清单，实际做下来发现：
> 清单里除了包名与版本，还有"这个包为什么能再分发"这类需要人判断的段落
> （例如 Windows SDK 投影走的是 SDK 条款而不是 MIT，按 MIT 写会让清单更"干净"但那是错的）。
> 让脚本整篇重写等于把判断覆盖成机器猜出来的许可证名，比漏登记更糟。
> 现在的分工是：**脚本保证"漏登记一定失败"，人负责"登记得对不对"**。
> 两个判据都在 `tools/licenses/check-third-party.ps1` 里，CI 步骤名 `Third-party notices`。

原计划的生成流程（保留作参考）：

1. 读 `src/Jianlan.App/obj/project.assets.json` 的 `targets` 节点，取 `runtime` 依赖的包名 + 版本
   （含传递依赖）；
2. 对每个包到 `%USERPROFILE%\.nuget\packages\<id>\<ver>\` 找 `LICENSE` / `LICENSE.txt` / `LICENSE.md`，
   读首行判定 SPDX 标识；找不到则标「见包内 LICENSE」，不猜；
3. 追加一节固定的「运行时组件」说明：.NET 运行时与 WPF 按 MIT 许可分发，指向
   `https://github.com/dotnet/runtime/blob/main/LICENSE.TXT`；
4. 输出 `THIRD-PARTY-NOTICES.md`（名称、版本、许可证、仓库地址四列）。

**CI 校验（实现版）**：`dotnet build` 之后跑 `tools/licenses/check-third-party.ps1`，两条判据：

1. `src/Jianlan.App/obj/project.assets.json` 里 `type=package` 且有 `runtime` 段的每个包，
   包名必须出现在 `THIRD-PARTY-NOTICES.md` 里（版本号对不上只告警，不失败——升级后先改版本很正常）；
2. `publish/` 里每个非 `Jianlan.*` 的 `*.dll`，文件名必须出现在清单里。

第二条是必须的：本项目真正的第三方程序集（`Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll`）
来自目标框架的隐式引用，在 `project.assets.json` 里**没有** package 条目，
只靠第一条会得出"零个第三方包"的假结论。

### 为什么不做成运行时动态生成

运行时读 `deps.json` 再拼清单，意味着把 `System.Text.Json` 的解析与文件 IO 塞进关于页的打开路径，
且 `deps.json` 不一定随包分发（框架依赖发布下它在，但自包含时内容不同）。构建时生成一次、内嵌展示，
数据一致性问题在 CI 就暴露，成本最低。

## 六、实现要点

| 项 | 做法 |
|---|---|
| 形态 | `src/Jianlan.App/Views/AboutView.xaml(.cs)` 用户控件，挂在 `SettingsWindow` 的 `PageAbout`（第八页）里。尺寸随设置窗（760×600），不单独设宽高，长文案靠 `TextWrapping` 换行。0.2.0 时是 480×680 的独立窗口，0.2.1 删除 |
| 品牌区 | 图标 40×40 + 右侧两行：第一行 `Jianlan`（资源键 `About.AppName`，中英同值，品牌名不翻译），第二行版本号与实际构建号（`·` 分隔）。无构建号时版本行只剩版本号，分隔点一并隐藏 |
| 版本号 | `Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion`；`+` 后为 commit，无 `+` 时只显示版本号 |
| 打开目录 | `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })`；路径不存在先 `Directory.CreateDirectory`（日志目录可能尚未创建）；异常 → 状态行提示，不弹原始异常 |
| 复制诊断 | `Clipboard.SetText` 包在 try/catch（剪贴板被占用是常态），失败提示"剪贴板被占用，请重试" |
| 打开链接 | 同样 `UseShellExecute=true` 打开 URL；失败提示"无法打开浏览器" |
| 深色/高对比度 | 全部走现有 `DynamicResource` Token，不写死颜色 |
| 本地化 | 所有文案走 `{loc:Tr Key=About.*}`；许可证与依赖清单**不翻译**（法律文本原文为准） |

## 七、性能

| 指标 | 目标 | 依据 |
|---|---|---|
| 启动成本 | **0ms** | 关于页不在启动路径；`AboutView` 随设置窗一起构建 |
| 首次打开 | <100ms | 读 3 个内嵌资源（<1KB + 11KB + 2KB）+ 拼 20 行文本 |
| 内存 | <2MB（含视觉树） | 随设置窗关闭释放；不做常驻缓存 |

不预加载任何清单，不缓存 `Strings` 之外的数据。

## 八、验收

- [x] 版本号取 `AppInfo.DisplayVersion`，`+` 之后的短 commit 另排小字（构建时 `-p:InformationalVersion=0.1.0+2d31cdc` 实测显示"构建 2d31cdc"）
- [x] 「查看许可证全文」能读到完整 Apache-2.0 文本（含附录）；NOTICE 与依赖清单同样是内嵌资源，内容即仓库原文
- [x] 依赖清单条目与产物一致（`tools/licenses/check-third-party.ps1`，正反两条路径都验过）
- [ ] 三个目录按钮都能打开资源管理器并定位到正确路径（含日志目录尚不存在的首次运行）
      —— 代码路径与设置页的"打开数据目录"同源，但离屏进程开不了资源管理器，**未实测**
- [x] 断网状态下全部功能可用（「检查更新」只 `Process.Start` 一个 URL）
- [x] 深色、浅色两模式文字可读、无错位（截图见走查）；高对比度未实测
- [x] 英文界面下无截断、无中文残留（480×680 下逐屏看过）
- [x] 复制诊断信息成功（`DiagnosticInfo` 7 条单测；剪贴板被占时给提示不崩）

## 九、工作量

| 项 | 工作量 |
|---|---|
| 关于页 XAML + 代码（含三个目录按钮、复制诊断；0.2.1 起为 `AboutView`） | 0.5 天 |
| 许可证/NOTICE 内嵌 + 只读查看窗 | 0.25 天 |
| `gen-third-party.ps1` + CI 校验 | 0.25 天 |
| 设置页入口 + i18n 抽键 | 0.25 天 |
| 三主题走查 + 英文走查 | 0.25 天 |

合计约 **1.5 人日**，排在 0.2.0 内，与 FR-10.4 之后的其他设置项一起收。

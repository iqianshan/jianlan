# 第三方组件许可清单

本文件对应 `docs/requirements.md` 的 **AC-10 开源合规**：发布产物须包含 LICENSE、NOTICE 与第三方依赖许可清单。

Jianlan 本体以 **Apache License 2.0** 发布，全文见 [`LICENSE`](LICENSE)，署名与变更声明见 [`NOTICE`](NOTICE)。
本文件只登记"不是我们自己写的"那部分代码，以及它们的许可条件。

更新规则：新增/升级任何 `PackageReference`、把项目改成 self-contained 发布、或引入第三方图标/字体/示例代码时，
必须同步改这里。**"忘了写"在合规上等同于"没有许可"**。

---

## 一、随安装包一起分发的组件

下列文件在 `publish/` 产物里，会随安装包与便携包复制到用户机器上。

| 组件 | 文件 | 版本 | 许可 | 版权 |
|---|---|---|---|---|
| Windows SDK .NET 投影 | `Microsoft.Windows.SDK.NET.dll` | 10.0.17763.55 | Microsoft Windows SDK 许可条款（Distributable Code） | © Microsoft Corporation |
| CsWinRT 运行时 | `WinRT.Runtime.dll` | 2.2.0.48161 | Microsoft Windows SDK 许可条款（Distributable Code） | © Microsoft Corporation |

两者来自 NuGet 包 `Microsoft.Windows.SDK.NET.Ref` 10.0.17763.57（由 `net9.0-windows10.0.17763.0` 目标框架隐式引入），
用于调用 Windows Shell / 已知文件夹等 WinRT API。它们是 Windows SDK 明确定义的 **Distributable Code**，
允许作为应用程序的一部分再分发，条件是程序本身提供了实质性的主要功能（本项目满足）。

条款原文：
- Windows SDK 许可：<https://aka.ms/WinSDKLicenseURL>
- SDK 内含的第三方代码清单：<https://aka.ms/thirdpartynotices>

> 注：这两个程序集的官方许可**不是** MIT。CsWinRT 项目本身（源码）是 MIT，但随 .NET SDK 下发的
> Windows SDK 投影程序集走的是 SDK 条款。按 MIT 写会让清单看起来更"干净"，但那是不准确的。

---

## 二、运行环境依赖（不随包分发）

当前发布形态为**框架依赖**（`publish/Jianlan.runtimeconfig.json` 声明 `Microsoft.NETCore.App` 9.0 与
`Microsoft.WindowsDesktop.App` 9.0），运行时由用户机器上的 .NET 9 Desktop Runtime 提供，不复制进安装包。

| 组件 | 版本要求 | 许可 | 版权 |
|---|---|---|---|
| .NET Runtime（含 WPF / WindowsBase / PresentationCore） | 9.0 | MIT | © .NET Foundation and Contributors |

MIT 全文见本文件末尾附录。

---

## 三、开发与测试期依赖（不随包分发）

仅参与构建与测试，**不进入发布产物**。列在这里是为了让"从源码构建需要什么"可追溯。

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| xunit | 2.9.2 | Apache-2.0 | 单元测试框架 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 | 测试运行器 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT | `dotnet test` 宿主 |
| coverlet.collector | 6.0.2 | MIT | 覆盖率采集 |

`xunit` 的 Apache-2.0 全文与 `LICENSE` 相同（本项目同为 Apache-2.0）。

---

## 四、字体与图标

界面图标取自 **Segoe MDL2 Assets**，是 Windows 10/11 自带的系统字体。程序只按字符码引用已安装的系统字体，
**不打包、不嵌入、不再分发**字体文件，因此不构成字体许可的再分发。

项目内没有第三方图标库、没有图标字体文件、没有从其他项目复制的图形资源。
`src/Jianlan.App/Assets/` 目前为空目录，留作后续自有资源位置。

---

## 五、代码层面的"特意不用"

以下都是"本来会引入第三个依赖"的地方，当前选择自己实现，记在这里避免以后被无意中加回来：

| 能力 | 选择 | 原因 |
|---|---|---|
| JSON 序列化 | `System.Text.Json`（.NET 内置） | 设置文件小、结构固定，不值得引入 Newtonsoft.Json |
| Shell 图标 | 自己封 `SHGetFileInfo` | 第三方图标库普遍带几百 KB 资源与额外线程模型 |
| 文件夹选择对话框 | 自己封 `IFileOpenDialog` | 避免 WinForms 互操作（会让产物多一套 UI 框架） |
| 已知文件夹 | 自己封 `SHGetKnownFolderPath` | 同上，且"下载"目录被重定向时行为可控 |

---

## 附录：MIT License 全文

适用于上文标注为 MIT 的组件（.NET Runtime、Microsoft.NET.Test.Sdk、coverlet.collector）。

```
MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

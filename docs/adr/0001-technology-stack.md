# ADR 0001：技术栈选型

- 状态：已接受
- 日期：2025-02-14
- 决策者：StreamPilot 开发
- 相关文档：`0002-architecture-layering.md`、`0004-raw-recording.md`

## 背景

StreamPilot 需要把三类能力（多平台低延迟播放、多平台解析、原始流录制）统一到一个 Windows 桌面程序中，并以**自包含单文件 exe** 发布，用户解压双击即可运行，无需安装任何运行环境。

同时存在两个必须尊重的既有事实：

1. 参考项目 `MultiLive-Windows-v10.4.16-diag` 是 **.NET 10 WPF + WebView2** 的自包含程序，其核心播放逻辑是 `Web\player.html` + `mpegts.js 1.8.2` + `hls.js 1.6.16`（Apache-2.0），这部分是**可复用的前端资产**；而它的 C# 解析层是闭源编译产物（`MultiLiveLowLatency.dll`），**不可复用**。
2. 参考项目 `lsar-0.3.19` 是 Tauri（Rust + SolidJS）实现，架构与目标交付物不同，只能**参考思路并用 C# 重写**；`BililiveRecorder-WPF-Portable` 是 WPF 发行包，只能参考录制行为。

因此技术栈的选择空间实际上被"自包含单文件发布 + 可复用 Web 播放前端 + 平台解析用 C# 重写"这三条约束收窄。

## 决策

### 1. 运行时与目标框架：`.NET 10`，`net10.0-windows`

- 采用 **.NET 10（LTS）**，目标框架 `net10.0-windows`，`RuntimeIdentifier=win-x64`。
- 理由：
  - .NET 10 是当前 LTS，生命周期覆盖到 2028 年，避免中途被迫升级。
  - 参考项目 MultiLive 本身就是 .NET 10 自包含发布（`runtimeconfig.json` 中 `tfm` 为 `net10.0`），说明该组合在 Windows 10/11 x64 上已被验证可行。
  - 单文件发布在 .NET 10 上对 WPF 的支持成熟（`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` + `EnableCompressionInSingleFile`）。
  - 开发机已安装 `Microsoft.WindowsDesktop.App 10.0.10` 运行时，便于在无 SDK 环境下做运行时一致性核对。
- 备选与否决理由：
  - `.NET Framework 4.8`：无需 SDK 即可用系统 `csc.exe` 编译，但**无法产出真正的自包含单文件 exe**（依赖系统 Framework、无法内嵌 WebView2 运行时加载器与 .NET 运行时），与"解压即用"硬性要求冲突，否决。
  - `.NET 8`：同样是 LTS，但参考项目基线是 .NET 10，且 .NET 10 的单文件压缩比与裁剪告警处理更完善，否决。
  - `JDK / Go / Rust`：无法复用 WPF + WebView2 生态，且需要额外运行时或交叉编译链，否决。

### 2. UI 框架：WPF + WebView2

- 界面使用 **WPF**（`UseWPF`），播放画面通过 **WebView2** 承载 Web 播放页。
- 理由：
  - 播放链路必须复用 `mpegts.js`/`hls.js`（浏览器 MSE 是低延迟追帧能力的前提），WPF 原生控件无法替代。
  - WebView2 Runtime 在 Windows 10/11 上由 Edge 预装或随系统更新分发，是**最低成本的"无需安装"浏览器内核**；WebView2 SDK 通过 NuGet 引入（`Microsoft.Web.WebView2`）。
  - MultiLive 已验证 `SetVirtualHostNameToFolderMapping` 把 `Web\` 目录映射为 `https://appassets.local/` 的做法可用，且该虚拟主机属于安全上下文，`mode:'cors'` 探测与 Private Network Access 才能正常工作（`file://` 不行）。
- 备选与否决理由：
  - **WinUI 3**：依赖 Windows App SDK（体积大、自包含部署复杂、需要额外运行时引导），且与"单文件 exe + 解压即用"目标冲突，否决。
  - **Avalonia**：跨平台无需 Windows，但其 WebView 控件依赖额外 NuGet 包，单文件体积更大；本项目只面向 Windows 10/11 x64，跨平台收益为零，否决。
  - **Electron/Tauri**：与"用 C# 重写解析层"的要求冲突，且 Electron 单文件产物远超体积预期，否决。

### 3. NuGet 依赖：保持极简（当前仅 1 个运行时依赖）

自包含单文件发布要求依赖越少越好；同时本项目立项时的开发环境**无外网**，无法执行大规模 NuGet restore。因此：

| 依赖 | 版本 | 用途 | 说明 |
|------|------|------|------|
| `Microsoft.Web.WebView2` | `1.0.2903.40` | WPF 内嵌 Edge 内核 | 唯一运行时第三方依赖，含 `WebView2Loader.dll` 原生库 |
| `Microsoft.NET.Test.Sdk` / `xunit` | — | — | **不引入**，见下 |

- **不引入** `Microsoft.Extensions.*`（DI/Logging/Configuration）：用 20~60 行自研 `ServiceRegistry`、`IStructuredLogger`、`JsonConfigStore` 覆盖，避免 4~6 个包及其传递依赖。
- **不引入** `Newtonsoft.Json`：统一使用 BCL 的 `System.Text.Json`。
- **不引入** `xunit`/`NUnit`/`MSTest`：见 ADR 决策 5。
- **不引入** `ffmpeg`/`mpv` 的 NuGet 封装：外部工具以 `tools/` 目录随包分发（见 ADR 0005）。
- 所有第三方依赖与二进制必须在 `THIRD-PARTY-NOTICES.md` 登记版本、许可证、来源与 SHA256。

### 4. 不引入数据库：配置与历史记录用 JSON 文件

- `config.json`（用户配置）、`rooms.json`（房间/预设列表）、`history.json`（解析与播放历史）。
- 理由：数据量级为数十到数百条，SQLite（参考项目 lsar 的选择）会引入原生依赖与文件锁复杂度，收益为零。所有写入走"临时文件 + 原子替换 + 校验"，损坏时回退默认值并记录 `Warn`。
- 约束：内存中缓存的解析结果有上限（`StreamCache` 上限 64 条，超出淘汰最旧），见 `0003-parser-contract.md`。

### 5. 测试：自研极简测试运行器（`StreamPilot.Tests` 为 Exe）

- 目标环境无法下载 `xunit` 等测试包，因此实现一个**源码级别的极简测试框架**：
  - `[TestClass]`/`[TestMethod]` 特性 + 反射发现；
  - `Assert`（Equal/True/False/Throws/NotNull/Contains）；
  - `TestRunner` 输出 `PASS/FAIL` 与失败堆栈，并以退出码 `0/1` 表示结果；
  - 纯函数式设计，覆盖正常、异常、边界三类用例。
- 它仍以 `dotnet run` / `dotnet test`-兼容的普通 Exe 形式存在，不会因为缺包而阻塞构建。
- 覆盖率要求（核心模块 ≥ 60%）通过 `docs/testing/coverage-matrix.md` 建立"模块 ↔ 用例"映射来人工核对；若后续具备网络条件，可平滑替换为 `xunit` + `coverlet`（不需要修改被测代码）。

### 6. 版本与语言级别

- `LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=enable`、`TreatWarningsAsErrors=true`（`src/` 全部工程）。
- `InvariantGlobalization=false`（需要中文/多语言与本地化时间格式化）。
- `EnableWindowsTargeting=true`：允许在非 Windows 主机上还原（便于 CI），本机为 Windows。

## 影响

### 正面

- 复用 Apache-2.0 的 `mpegts.js`/`hls.js`，低延迟追帧不必从零实现；解析层与录制层 100% 自研源码，满足"自研源码"要求。
- 单文件发布只依赖一个 NuGet 包，体积与还原风险都可控。
- 前端（HTML/JS）与后端（C#）通过 WebView2 消息桥解耦，UI 与业务互不阻塞。

### 负面 / 风险

- **WPF 单文件发布无法真正打成 1 个文件**：WebView2 需要 `WebView2Loader.dll`，`Web/` 播放页与 `tools/` 外部工具也必须是磁盘文件。因此最终产物形态为 `StreamPilot-windows-v<版本>.exe` + `Web/` + `tools/` + 文档（与需求文档第 3.4 节"用户解压后只看到 exe + Web/ 资源 + tools/ 依赖 + 文档"一致）。发布脚本使用 `PublishSingleFile` 把 .NET 运行时与托管程序集合并进 exe，把 `WebView2Loader.dll` 与外部工具排除在单文件之外，并按 `xxx-windows-vXXX` 规则命名发行版目录与 exe。
- 自研测试运行器功能弱于 xunit（无并行、无参数化 Fixture 生态）；已通过"每个平台解析器至少 3 组用例 + 纯函数抽取"降低影响。
- 无外网环境下 `.NET 10 SDK` 需由使用者预装；构建脚本在检测不到 SDK 时会明确报错并给出安装指引，而不是静默失败。

## 实施结果（补充记录）

本节记录本 ADR 落地后的实测结果，便于后续维护者核对：

| 项 | 结果 |
|----|------|
| 使用的 SDK | **.NET SDK 10.0.401**（本机安装后完成全部验证） |
| `dotnet build StreamPilot.slnx -c Debug/Release` | **0 警告 / 0 错误**（`TreatWarningsAsErrors=true` 生效） |
| C# 单元测试（自研运行器） | **97 个用例全部通过** |
| 播放策略前端测试（`node --test`） | **18 个用例全部通过** |
| 静态红线自检（`build/verify-tree.ps1`） | 通过（无 TODO/Console/依赖方向/通配监听/二进制混入） |
| 离线 C# 结构分析（`build/analyze-csharp.mjs`） | 94 文件 / 18890 行 / 145 类型 / 618 方法，无结构性问题 |
| 发布产物 | 发行版目录 `StreamPilot-windows-v0.1.0`，内含同名单文件 exe **67.1 MiB**，总包 **69.2 MiB**（含 `Web/`、`docs/`、`tools/README.md`） |
| 运行时冒烟测试 | 启动正常；桥接 `/health` 返回 `{"status":"ok","version":"0.1.0","port":5566}`；日志中 `播放器内核已就绪（HEVC: 支持，H.264: 支持）`、`播放页加载完成` |

首次编译共暴露并修复 **22 处**问题（含 2 处 XML 注释格式错误、3 处 BCL 成员签名误用、4 处命名空间/using 缺失、1 处 `.csproj` 注释里的 `--` 导致 MSB4025、以及逻辑缺陷若干），
证明"只做静态审查不足以替代真实编译"——本 ADR 的决策 5（自研测试运行器）也因此保留，用于在无 SDK 环境下仍能执行回归测试。

## 后续

- 若未来需要在 Linux/macOS 构建，需要重新评估 `EnableWindowsTargeting` 与 WPF（大概率不可行）。
- 若引入超过 50MB 的依赖，必须先修订本 ADR。

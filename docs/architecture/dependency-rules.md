# 依赖规则（架构边界）

本文件把 `CLAUDE.md` 的架构边界落实为可执行的规则。任何违反都必须先改代码，而不是改本文件。

## 1. 工程与依赖方向

```
StreamPilot.App (WPF + WebView2, AssemblyName=StreamPilot)   组合根 / 表现层
  ├──▶ StreamPilot.Core
  ├──▶ StreamPilot.Parsers
  ├──▶ StreamPilot.Recording
  └──▶ StreamPilot.Bridge

StreamPilot.Parsers   ──▶ StreamPilot.Core
StreamPilot.Recording ──▶ StreamPilot.Core
StreamPilot.Bridge    ──▶ StreamPilot.Core
StreamPilot.Tests     ──▶ Core / Parsers / Recording / Bridge
```

> 附加说明：`StreamPilot.Tests` 还用 `<Compile Include>` **按源码链接**编译了 App 层三个"无 WPF 依赖"的
> 纯逻辑文件（`ViewModels/PlatformOption.cs`、`Services/PlatformDetector.cs`、`Services/PresetStore.cs`），
> 用于测试平台识别与预设持久化。这是源文件链接而不是工程引用：测试进程不会因此引入 WPF，
> 也不构成上图中的反向依赖（App 不依赖 Tests）。App 层其余代码（视图、视图模型、宿主）不参与测试编译。

硬性规则：

1. `Core` **不得**引用任何其他 `StreamPilot.*` 工程。
2. `Core` **不得**引用 WPF / WinForms / WinUI（`UseWPF`、`System.Windows.*`、`Microsoft.Web.WebView2`）。
   它只允许 BCL（`System.*`）与自己的类型。
3. `Parsers` / `Recording` / `Bridge` **不得**引用 `App`（禁止反向依赖）。
4. `Bridge` **不得**引用 `App`；`Recording` **不得**引用 `Bridge`（录制不经过中继）。
5. `App` 是唯一组合根，可以引用全部工程。
6. 禁止循环依赖：`dotnet build` 会因项目引用环直接失败，因此规则 1-5 也由"编译期必然失败"兜底。

## 2. 分层职责禁令

| 层 | 禁止事项 |
|----|----------|
| `App`（UI） | 直接 `new` 解析器（`BilibiliParser` 等）；直接读平台 API / 拼平台 URL；在 UI 线程做网络或文件 IO；在 View/Code-behind 里写业务判断 |
| `Parsers` | 启动进程；弹窗；写 UI；调用录制；注册中继 |
| `Recording` | 转码；调用 ffmpeg；解析平台接口；依赖 `Bridge` |
| `Bridge` | 解析房间；弹 UI；监听非 `127.0.0.1` 地址；持久化签名 URL |
| `Core` | 任何具体平台知识（B站/抖音字段名不得出现在 `Core`）；任何 UI 类型；`Process.Start`；`HttpListener` |

## 3. UI 如何合法地使用解析能力

UI 只依赖 `Core.Services` 中的接口：

```csharp
public interface IRoomResolver        // 解析房间 → ResolveOutcome
public interface IPlaybackCoordinator // 准备候选 → PlaybackPlan
public interface IRecordingCoordinator// 开始/停止录制
public interface IPlaybackBridge      // mpv 外挂 + 本地中继
public interface IPlatformParserFactory // 由 Parsers 实现，仅 App 的组合根使用
```

`ShellViewModel` 的构造函数只接收上述接口 + `IStructuredLogger` + 配置，因此可以在测试中替换为测试替身。
`IPlatformParserFactory` 的实现（`PlatformParserFactory`）只在 `App.xaml.cs` 的 `BuildServices` 中出现一次。

## 4. 解析器实现的可见性

- 六个解析器类为 `internal sealed`，只通过 `Core` 的 `IPlatformParser` 暴露。
- `StreamPilot.Parsers.csproj` 通过 `InternalsVisibleTo` 仅对 `StreamPilot`（App 程序集名）与 `StreamPilot.Tests` 开放。
- 因此 UI 层**无法**直接依赖具体解析器类型（编译期即被阻止）。

## 5. 如何自检

```powershell
# 扫描禁止的引用方向与红线（TODO、Console.WriteLine、跨层引用等）
pwsh -File build\verify-tree.ps1
```

`verify-tree.ps1` 会检查：

1. `Core` 下出现 `System.Windows`、`PresentationFramework`、`WebView2`、`Process.Start`、`HttpListener` → 失败；
2. `Parsers`/`Recording`/`Bridge` 下出现 `StreamPilot.App` → 失败；
3. `Recording` 下出现 `ffmpeg`、`Process.Start`、`StreamPilot.Bridge` → 失败；
4. `Bridge` 下出现 `0.0.0.0`、`http://+`、`http://*` → 失败；
5. 全仓库出现 `TODO`/`FIXME`/`HACK`、`Console.WriteLine`/`Console.Write` → 失败；
6. `src/` 下出现 `*.exe`/`*.dll`/`*.pdb` → 失败；
7. 任何 C# 文件行数超过 300 行的单个方法（粗检）→ 提示。

## 6. 文件名规范（仓库内一律 ASCII）

**仓库中所有文件名（含文档）一律使用 ASCII 英文名**，不使用中文或其他非 ASCII 字符作为文件名。文件名承载"这是什么"，中文标题写在文件内部（H1）与索引链接文字里即可。

| 类别 | 命名规则 | 示例 |
|------|----------|------|
| C# 源文件 | PascalCase，与类型名一致 | `BilibiliParser.cs`、`HttpTextClient.cs` |
| C# 工程文件 | 与工程同名 | `StreamPilot.App.csproj` |
| 前端源文件 | kebab-case | `player-core.js` |
| 脚本 | kebab-case | `verify-tree.ps1`、`analyze-csharp.mjs` |
| 文档 | kebab-case；ADR 带四位序号前缀 | `0003-parser-contract.md`、`player-message-contract.md` |
| 配置/清单 | UPPER 或 kebab-case | `THIRD-PARTY-NOTICES.md`、`Directory.Build.props` |

**文件内容语言不受此约束**：代码注释、文档正文、提交信息仍然使用中文。

**引入原因**：中文文件名在 7z 解压、旧编码工具链、CI 归档、跨平台 clone 与 URL 引用等场景下存在乱码或引用失效风险；改为 ASCII 命名后，所有工具链与链接都稳定可预期。

**重命名已有文件**必须用 `git mv`（保留文件历史），并同步更新所有引用：

- 代码与脚本里的路径引用（C# 的 XML 注释、`.csproj` 注释、构建脚本）；
- 文档之间的相对链接；
- `build/verify-tree.ps1` 的"必需文件"清单；
- `README.md` 的文档索引与目录结构树。

**新增文档的检查点**：文件名是否 ASCII、是否被 `README.md` 文档索引收录、是否被其他文档正确链接。

## 7. 变更流程

- 需要新增跨层调用（例如让 `Recording` 直接使用 `Bridge` 中继）必须**先写 ADR** 说明理由，再改代码。
- 需要更换技术栈（UI 框架、HTTP 实现、日志实现）必须修订 `docs/adr/0001-technology-stack.md`。

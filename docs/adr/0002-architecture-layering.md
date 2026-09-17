# ADR 0002：架构分层与模块划分

- 状态：已接受
- 日期：2025-02-14
- 相关文档：`CLAUDE.md`（架构边界）、`0001-technology-stack.md`

## 背景

`CLAUDE.md` 规定了硬性架构边界：

- 禁止跨层调用：UI 层不得直接调用解析层，必须经过 Core 层的服务接口；
- 禁止循环依赖：`Core` 不依赖 `App`，`Bridge` 不依赖 `App`；
- 禁止在 UI 层写业务逻辑；
- 禁止在 Core 层引入 UI 框架依赖（WPF/WinForms/WinUI）；
- 所有平台解析器必须实现统一的 `IPlatformParser`；
- 所有解析结果必须统一为 `StreamCandidate`。

因此分层不是"风格选择"，而是必须满足的约束。同时要让 WPF 应用在 UI 线程之外完成解析、探测、录制与桥接。

## 决策

### 1. 六个工程，单一依赖方向

```
                       ┌───────────────────────────────────────┐
                       │ StreamPilot.App (WPF + WebView2)       │
                       │  UI / ViewModel / 组合根 / 服务编排     │
                       └───────┬───────────────┬───────────────┘
                               │               │
              ┌────────────────┘               └──────────────┐
              ▼                                               ▼
   ┌────────────────────┐                        ┌────────────────────┐
   │ StreamPilot.Bridge │                        │ StreamPilot.Parsers│
   │ 127.0.0.1 内嵌服务  │                        │ 各平台解析器实现    │
   │ mpv 启动 / 流中继   │                        │                    │
   └─────────┬──────────┘                        └─────────┬──────────┘
             │                                             │
             │        ┌──────────────────────────┐         │
             └───────▶│  StreamPilot.Recording   │◀────────┘
                      │  原始流录制 / 分片 / 元数据│
                      └────────────┬─────────────┘
                                   │
                                   ▼
                      ┌──────────────────────────┐
                      │  StreamPilot.Core        │
                      │  领域模型 / 枚举 / 接口    │
                      │  错误 / 日志 / HTTP / 配置 │
                      │  （无 UI 依赖）            │
                      └──────────────────────────┘

                      StreamPilot.Tests ──▶ 引用 Core / Parsers / Recording / Bridge
```

依赖规则（由 `Directory.Build.props` 之外的代码评审与 `docs/architecture/dependency-rules.md` 保证）：

- `Core` 不引用任何其他 `StreamPilot.*` 工程。
- `Parsers`、`Recording`、`Bridge` 只引用 `Core`。
- `App` 引用全部（它是**唯一的组合根**）。
- `Tests` 引用被测工程，但不被任何工程引用。
- 禁止 `Core → App`、`Bridge → App`、`Parsers → App`、`Recording → App` 的反向引用。

### 2. 分层职责

| 层 | 工程 | 允许做 | 禁止做 |
|---|---|---|---|
| 领域/契约层 | `Core` | 领域模型、枚举、`IPlatformParser` 等接口、错误分类、结构化日志抽象、HTTP 客户端、配置存取、缓存、URL 有效期校验 | 引用 WPF/WinForms/WinUI；`Process.Start`；`HttpListener`；任何具体平台知识 |
| 适配层 | `Parsers` | 实现 `IPlatformParser`，把平台响应翻译成 `StreamCandidate` | 弹窗、写日志到 UI、直接启动播放器 |
| 适配层 | `Recording` | 拉取原始流、写容器、分片、断流重连、写元数据 | 转码、调用 ffmpeg、解析平台 API |
| 适配层 | `Bridge` | 仅监听 `127.0.0.1` 的 HTTP 服务：mpv 启动、候选流中继、健康检查 | 解析房间、弹 UI、监听 `0.0.0.0` |
| 表现层 | `App` | View/ViewModel、WebView2 宿主与消息桥、组合根、把 Core 服务编排成用例 | 直接 `new BilibiliParser()`、直接读平台 API、在 UI 线程做网络/IO |

### 3. UI 层如何"经过 Core 层服务接口"

UI 只依赖 Core 中的接口：

```csharp
namespace StreamPilot.Core.Services;

/// <summary>解析房间信息并产出候选流的统一入口。</summary>
public interface IRoomResolver
{
    /// <summary>解析指定平台的房间。</summary>
    Task<ResolveOutcome> ResolveAsync(PlatformId platform, RoomQuery query, CancellationToken cancellationToken);
}

/// <summary>启动/停止一次播放会话。</summary>
public interface IPlaybackCoordinator
{
    /// <summary>为指定会话准备播放候选（探测、排序、写缓存）。</summary>
    Task<PlaybackPlan> PrepareAsync(PlaybackRequest request, CancellationToken cancellationToken);
}

/// <summary>录制会话的启动、停止与状态查询。</summary>
public interface IRecordingCoordinator
{
    /// <summary>开始录制。</summary>
    Task<RecordingHandle> StartAsync(RecordingRequest request, CancellationToken cancellationToken);
}
```

- `StreamPilot.Services` 命名空间下的 `PlaybackCoordinator`/`RecordingCoordinator`（实现类）**位于 App 工程**（组合根所在层），它们通过 `IPlatformParser` 的工厂 `IPlatformParserFactory`（Core 中声明的接口，由 `Parsers` 提供实现）间接使用解析层。这样"UI → Core 接口 → 具体实现"的链路满足"必须经过 Core 层的服务接口"。
- ViewModel 构造函数只接受 `IRoomResolver`、`IPlaybackCoordinator`、`IRecordingCoordinator`、`IStructuredLogger`，因此可被替换为测试替身。

### 4. 线程与异步模型

- 所有解析、探测、录制、桥接请求都是 `async`，入口统一接收 `CancellationToken`。
- **禁止** `.Result`/`.Wait()`/`GetAwaiter().GetResult()`（唯一例外：`App.xaml.cs` 启动阶段的同步初始化，且必须注释说明）。
- WPF 侧：`async void` 仅用于事件处理器，且内部 `try/catch` 兜底并记录。
- 录制线程使用**专用后台 `Task` + `CancellationTokenSource`**，不占用线程池长任务；每个录制会话只处理一个流，重连在会话内串行。
- WebView2 消息回调不阻塞 UI：收到 `play` 后立即返回，实际准备在 `Task.Run` 中完成，通过 `PostWebMessageAsJson` 回推结果。

### 5. 依赖注入：自研极简容器

不引入 `Microsoft.Extensions.DependencyInjection`，实现 `Core.Runtime.ServiceRegistry`：

- 注册形式：`RegisterSingleton<TInterface>(Func<ServiceRegistry, TInterface> factory)`；
- 解析形式：`GetRequired<TInterface>()`，未注册时抛出带服务名的 `InvalidOperationException`；
- 组合根在 `App.Services.AppBootstrap.CreateRegistry()` 中一次性完成注册，`Tests` 中可构造只含被测服务的注册表。

### 6. 数据与缓存边界

- 解析结果缓存 `StreamCache`（Core）：键为 `(PlatformId, RoomId)`，容量上限 64，过期时间 60 秒（进房间前的候选探测会重新校验有效期）。
- 缓存的值是 `ResolvedRoom`（含 `StreamCandidate` 列表），**不含** Cookie 或签名后的最终 URL 日志副本。
- 配置与历史持久化在 `%LOCALAPPDATA%\StreamPilot\`（用户数据与程序目录分离，见 `CLAUDE.md` 安全规则）。

## 影响

### 正面

- 依赖方向单向，`Core` 可在无 Windows 桌面环境下被单元测试覆盖（除 `App` 外的所有工程只依赖 BCL）。
- 平台解析器可独立新增：实现 `IPlatformParser` 并在 `PlatformParserFactory` 中登记即可，UI 无需改动。
- 录制与桥接互不感知，桥接只做"启动 mpv / 转发字节"。

### 负面 / 风险

- 接口数量增加，简单功能也需要穿过 `IRoomResolver` → `IPlatformParserFactory` → `IPlatformParser` 三层。已通过"接口只声明用例级方法"控制膨胀。
- `App` 作为组合根同时承担编排职责，需要防止它逐渐演变成"什么都做"的上帝类。约束：`App/Services` 下每个类只负责一个用例，且不得包含平台 URL/参数硬编码（那些属于 `Parsers`）。

## 后续

- 若引入插件式解析器（用户自定义平台），需要在 `Core` 增加 `IPlatformParserDescriptor` 并修订本 ADR。
- 若桥接服务需要支持多实例（多个浏览器页同时播放），需要引入基于 `sessionId` 的端口/路径隔离，同样需要修订本 ADR。

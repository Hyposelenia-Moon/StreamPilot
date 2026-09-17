# 测试与覆盖率矩阵

## 1. 测试分层

| 层 | 位置 | 运行方式 | 依赖 |
|----|------|----------|------|
| C# 单元测试 | `tests/StreamPilot.Tests` | `dotnet run --project tests/StreamPilot.Tests` | 无第三方包（自研极简运行器，见 [ADR 0001](../adr/0001-technology-stack.md) 决策 5） |
| 播放策略回归测试 | `tests/web/player-core.test.js` | `node --test tests/web/player-core.test.js` | Node.js 内置 `node:test` |
| 静态红线检查 | `build/verify-tree.ps1` | `powershell -File build/verify-tree.ps1` | PowerShell |
| 离线结构分析 | `build/analyze-csharp.mjs` | `node build/analyze-csharp.mjs` | Node.js（无需 SDK） |

一键运行：`powershell -NoProfile -ExecutionPolicy Bypass -File build/test.ps1`（四个阶段依次执行，任一失败即返回非零退出码）。

### 实测结果（.NET SDK 10.0.401）

| 阶段 | 结果 |
|------|------|
| `dotnet build StreamPilot.slnx`（Debug 与 Release） | 0 警告 / 0 错误（`TreatWarningsAsErrors=true`） |
| C# 单元测试 | **97 / 97 通过** |
| 播放策略前端测试 | **15 / 15 通过** |
| 静态红线自检 | 通过 |
| 离线结构分析 | 94 文件 / 18816 行 / 145 类型 / 616 方法，无结构性问题 |

> 离线结构分析的定位：在没有 SDK 的环境里提供可复现的前置检查（括号配对、命名空间规则、重复类型、空 catch、CS1998、未引用私有字段、方法行数、XML 注释 `--`）。
> **它不替代编译器**：本项目的首次真实编译仍发现了 22 处问题，因此有 SDK 时必须以 `dotnet build` 为准。

## 2. 为什么自研测试运行器

目标构建环境**无外网**，无法还原 `xunit` / `NUnit` / `Microsoft.NET.Test.Sdk`。
因此 `StreamPilot.Tests` 是一个普通 Exe：

- `[TestClass]` / `[TestMethod]` 特性 + 反射发现（`tests/StreamPilot.Tests/Framework/`）；
- `Assert` 提供 `Equal`/`EqualDouble`/`True`/`False`/`NotNull`/`Null`/`Contains`/`DoesNotContain`/`SequenceEqual`/`Throws`/`ThrowsAsync`；
- `TestRunner` 输出 `PASS`/`FAIL` 与失败原因，退出码 `0`（全通过）/ `1`（有失败）。

若后续具备网络条件，可平滑替换为 `xunit` + `coverlet`：被测代码不需要任何改动。

## 3. 覆盖率矩阵（核心模块 ↔ 用例）

> 目标：核心模块覆盖率 ≥ 60%（`CLAUDE.md` 红线 10）。由于当前无覆盖率采集工具，
> 本矩阵以"每个核心模块的公开行为是否都被用例覆盖"进行人工核对，并在具备工具后由 `coverlet` 复核。

| 核心模块 | 覆盖的行为 | 对应用例 |
|----------|-----------|----------|
| `Core.Utilities.QueryStringParser` | 重复键、加号语义、百分号解码、非法转义、时间戳秒/毫秒、构建转义 | `QueryStringParserTests`（6 个） |
| `Core.Http.RetryPolicy` | 退避序列与抖动上下界、越界、可重试状态码/异常、尝试上限、状态码→失败分类 | `RetryPolicyTests`（6 个） |
| `Core.Logging.SensitiveData` | URL 签名参数脱敏、Cookie 只保留键名、指纹长度与不可逆性 | `SensitiveDataTests`（4 个） |
| `Core.Models.StreamCandidate` / `StreamCandidateBuilder` | 源索引递增、指纹生成、非法 URL 丢弃、CDN host 覆盖与回退、能力矩阵、首播候选、画质映射 | `StreamCandidateTests`（6 个） |
| `Core.Caching.StreamCache` | 命中与过期、键空间隔离、容量上限淘汰最旧、清空 | `CachingAndValidityTests`（4 个） |
| `Core.Services.CandidateValidity` | 未声明有效期、已过期（含边界）、非法协议 | `CachingAndValidityTests`（3 个） |
| `Core.Parsers.PlatformParserBase` | 房间号格式校验、域名校验（含子域/非法 scheme）、平台不匹配、空候选→未开播、未预期异常包装、取消透传、从链接提取房间号 | `ParserBaseTests`（7 个） |
| `Parsers.Bilibili.IsRiskControlCode` / `Parsers.Yy.ParseRoomPage` / `Parsers.Bigo.CollectCandidates` | 风控码与"房间不存在"区分、YY 房间页字段抽取（含短号→`sid` 与 404 页）、Bigo"有地址即开播"与需要登录时的 `Rejected` 归类 | `PlatformParserTests`（7 个） |
| 各平台画质档位（`QualityOption`） | 档位列表构造（官方档位名 / 码率 / HDR 标记）、按 `PreferredQualityKey` 取档、未知键回退最高档、单档平台只给一项 | `PlatformParserTests`（画质用例，见文件内 `[TestMethod]`） |
| `Recording.SegmentPolicy` | 按字节/时长切分、未开始与时间戳回退、非法配置归一化、最小字节判定 | `SegmentPolicyTests`（5 个） |
| `Recording.RecordingFileNaming` | 命名格式、非法字符与路径穿越、超长截断、扩展名映射、冲突不覆盖、元数据路径 | `RecordingFileNamingTests`（6 个） |
| `Recording.Flv.FlvTagReader` / `FlvTimestamp` | 正常序列、关键帧判定、签名非法、未知标签类型、载荷截断、时间戳边界与截断、缓冲区过短 | `FlvTagReaderTests`（7 个） |
| `Recording.Flv.FlvSegmentWriter` | 文件头字节、载荷逐字节一致、时间戳重定基、序列头重放、分片元数据、释放后拒绝写入 | `FlvSegmentWriterTests`（6 个） |
| `Recording.Hls.HlsPlaylistParser` / `TsStreamRecorder.AlignToPacketBoundary` | media/master 播放列表、ENDLIST、空内容、TS 整包对齐、前导垃圾、不足一包 | `HlsPlaylistTests`（7 个） |
| `Bridge.LoopbackOnlyGuard` / `RelayRegistry` / `BridgeHost` | 回环前缀校验、通配拒绝、端口越界、注册/解析/释放、未知令牌、容量上限、按地址释放、未启动时注册失败 | `BridgeTests`（7 个） |
| `Web.player-core`（播放策略） | 见 `docs/architecture/playback-strategy.md` 第 7 节 | `player-core.test.js`（15 个，含画质下拉规范化） |

**未覆盖（需联网或人工验证，属集成测试范畴）**：

| 模块 | 原因 | 计划 |
|------|------|------|
| 六个平台解析器的实际 HTTP 解析 | 需要真实平台响应；网络与房间是否开播不可控 | 已用一次性探针在真实房间上验证（B站 / 虎牙 / 斗鱼 / YY 拿到候选，Bigo 归类为需要登录）；日常回归用"响应片段 → 判定结果"的离线用例覆盖（见 `PlatformParserTests`） |
| `RecordingSession` / `FlvStreamRecorder` 端到端 | 依赖真实流；但字节级写入、分片策略、命名、元数据均已单测覆盖 | 用本地伪造 HTTP 服务（`HttpListener` 回环）做集成测试 |
| `PlaybackCoordinator` 中继注册 | 依赖桥接服务运行 | 已在 `BridgeTests` 覆盖注册表逻辑；协调器留待集成测试 |
| `MpvLauncher` | 需要 mpv 可执行文件 | 人工验证清单见 `docs/runbooks/troubleshooting.md` |

## 4. 用例编写要求

- 每个核心模块必须有 **正常 / 异常 / 边界** 三类用例；
- 禁止 `Assert.True(true)` 之类的空测试；
- 平台解析器：至少覆盖"正常 / 未开播 / 房间不存在 / 轮播 / 网络错误 / 结构变化"六类；
- 用例名使用中文描述行为（便于阅读失败输出）；
- 涉及文件系统的用例必须使用 `TempDirectory` 并在 `finally` 中清理；
- 涉及网络的用例必须能离线运行（打桩或改为纯函数测试）。

## 5. 本地运行

```powershell
# C# 测试
dotnet run --project tests/StreamPilot.Tests
dotnet run --project tests/StreamPilot.Tests -- QueryStringParser   # 关键字过滤

# 前端测试
node --test tests/web/player-core.test.js

# 全部（含静态检查）
pwsh -File build/test.ps1
```

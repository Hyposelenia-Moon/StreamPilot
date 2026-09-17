# StreamPilot

Windows 桌面直播工具：**多平台低延迟播放** + **多平台解析** + **直播原始流录制**。

Web 端负责低延迟观看，mpv 端负责超分 / HDR / 高画质，两者互不关联。全部为自研源码，最终以自包含产物发布，解压双击即可运行。

---

## 摘要

**StreamPilot 是一个 Windows 桌面直播客户端，把"低延迟观看、多平台解析、原始流录制"三件事做在同一个自研程序里**：WebView2 承载自研播放页负责低延迟播放（极限追帧最低 150 ms），C# 解析层负责 B站 / 抖音 / 虎牙 / 斗鱼 / YY / Bigo 六个平台的房间与直播流解析，录制层以字节级搬运方式保存平台原始 FLV / TS（**不转码**），内嵌回环桥接服务把当前流一键交给 mpv 以获得超分 / HDR 画质。三个参考项目（录播姬、Lsar、MultiLive）只作只读参考，源码 100% 自研，最终以自包含单文件产物发布，用户解压双击即用、无需安装任何环境。

### 核心亮点

- **五要素统一的解析契约**：所有平台都产出 `StreamCandidate`（流地址 / 格式 / CDN host / 编码 / 源索引），并区分八类失败（未开播、房间不存在、轮播中、解析错误、网络错误、平台拒绝、非法输入、不受支持），UI 提示与录制层对平台无感知。
- **可量化的低延迟策略**：三档追帧目标 150 / 200 / 250 ms，CDN 候选**并行**探测（1.2 s 上限）后按延迟排序，首帧超时 6 / 8 s，卡顿判定 4 / 6.5 s（追帧档）与 6 / 9 s（稳定档），每候选重连 2 次（250 ms → 1000 ms）后自动切换线路。全部阈值集中定义于 `Web/player-core.js`，由 `node --test` 回归覆盖。
- **真·原始流录制**：不转码、不重新编码；FLV 按视频关键帧分片并重写文件头 + `onMetaData` + 序列头、时间戳按分片首帧重定基，使每个分片可独立播放；HLS 按 TS 整包对齐并按 m3u8 边界切分；断流按 2/4/8/15/30 s 退避自动重连，编码参数变化时强制切分；每场录制写一份 JSON 侧车元数据（主播名 / 房间号 / 标题 / 分区 / 开播时间 / 时长 / 分片清单）。
- **安全边界写进代码而非文档**：桥接服务只监听 `127.0.0.1`，由 `LoopbackOnlyGuard` 在启动前强制校验（出现 `+` / `*` / `0.0.0.0` 直接拒绝启动）；`Access-Control-Allow-Private-Network` 只出现在预检响应；日志写入前统一脱敏 Cookie 与 `wsSecret` / `txSecret` / `sign` / `token` 等签名参数，只记录不可逆指纹；不实现任何风控绕过（如抖音 `a_bogus`）。
- **依赖极简 + 自研测试**：只引入 1 个 NuGet 运行时依赖（`Microsoft.Web.WebView2`），其余全部使用 BCL；测试用自研极简运行器，因此在**没有外网、无法还原 xunit** 的环境里也能跑回归测试。

### 当前状态

| 项 | 数值 / 结果 |
|----|-------------|
| 交付规模 | 135 个文件 / 1.58 MiB；C# 88 个文件 / 约 1.5 万行（其中 `src` 70 个文件、`tests` 18 个文件） |
| 编译 | `dotnet build StreamPilot.slnx`（Debug 与 Release）**0 警告 / 0 错误** |
| 测试 | C# 单元测试 **80 / 80 通过**；播放策略前端测试 **13 / 13 通过**；静态红线自检与离线结构分析均通过 |
| 产物 | 发行版目录 `StreamPilot-windows-v0.1.0`（单文件 exe 59.3 MiB，整包 61.4 MiB）与同名校验过的压缩包 `StreamPilot-windows-v0.1.0.zip`（54.4 MiB） |
| 运行时 | 已做冒烟验证：桥接 `/health` 返回 `{"status":"ok","version":"0.1.0","port":5566}`，播放页握手成功并报告 `HEVC: 支持，H.264: 支持` |
| 尚未验证 | **真实平台直播链路的端到端**（解析真实房间 → 拉流播放 → 录制落盘）：需要联网与真实房间号，请在目标机器上实测 |

技术栈：.NET 10（`net10.0-windows`）+ WPF + WebView2，产物为 win-x64 自包含单文件。架构决策见 [`docs/adr/`](docs/adr/)，平台实现细节见 [`docs/parsers/`](docs/parsers/README.md)。

---

## 项目定位

| 能力 | 说明 |
|------|------|
| 低延迟播放 | B站 / 抖音 HTTP-FLV 优先、HLS 兜底；极限追帧档位 150 / 200 / 250 ms（默认 250 ms）；首帧超时、CDN 并行探测、候选切换、冻结恢复；HEVC 不可解码时明确提示并推荐 mpv |
| 多平台解析 | B站、抖音、虎牙（P0）、斗鱼、YY（P1）、Bigo（P2）；统一 `StreamCandidate`（流地址 / 格式 / CDN host / 编码 / 源索引）；失败区分未开播、房间不存在、轮播中、解析错误、网络错误、平台拒绝 |
| 原始流录制 | 不转码、不重新编码，直接写平台原始 FLV / TS；按大小或时长自动分片；断流自动重连；写元数据（主播名、房间号、标题、开播时间、录制时长） |
| mpv 外挂 | 内嵌 127.0.0.1 桥接服务（不再依赖外部 PowerShell 脚本），一键把当前流转给 mpv，享受超分 / HDR |

## 环境要求

- **运行（用户）**：Windows 10 / 11 x64 + Microsoft Edge WebView2 运行时（Win10 1803+ 通常随系统或 Edge 自带；缺失时请安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)）
- **构建（开发者）**：.NET 10 SDK（`dotnet --list-sdks` 应能看到 `10.x`）+ Node.js（仅用于运行播放策略前端测试）
- **可选**：`mpv.exe`（外挂播放），放在 `tools\mpv\mpv.exe` 或在“设置”中指定路径

## 安装与使用

1. 解压发行版压缩包 `StreamPilot-windows-v0.1.0.zip` 到任意目录（得到同名目录）。目录内容：
   `StreamPilot-windows-v0.1.0.exe`、`Web\`（播放页与前端库）、`tools\`（自行放置 mpv）、`docs\`、`README.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`VERSION.txt`（含构建信息与 exe 的 SHA256）。
2. 双击其中的 `StreamPilot-windows-v0.1.0.exe`。
3. 在左侧“直播”面板选择平台，填入房间号或直播间链接，点击 **解析房间**。
4. 点击 **开始播放**：播放页会自动完成“候选探测 → 排序 → 首帧 → 追帧”，状态与遥测显示在左侧。
5. 需要高画质 / HDR / 超分时点击 **mpv 播放**（需 mpv 可用）。
6. 需要留存时切到“录制”面板点击 **开始录制**，输出目录见“录制”面板。

## 平台支持

| 平台 | 状态 | 房间号 | 链接 | Web 播放 | 原始流录制 |
|------|------|--------|------|----------|------------|
| 哔哩哔哩 | P0 稳定 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS |
| 抖音 | P0 稳定 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS |
| 虎牙 | P0 稳定 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS |
| 斗鱼 | P1 | ✅ | ✅ | ⚠️ 多数房间仅 RTMP，需用 mpv | ⚠️ 同上 |
| YY | P1 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS |
| Bigo Live | P2 | ✅ | ✅ | ✅ HLS | ✅ TS |

## 已知限制

- **斗鱼**：`getH5PlayV1` 常只返回 RTMP 地址。Web 端无法播放 RTMP，程序会明确提示并使用 mpv 外挂；此时也不支持原始流录制（BCL 无 RTMP 客户端，引入第三方库违反依赖约束，见 [ADR 0004](docs/adr/0004-录制实现.md)）。
- **HLS fMP4 录制**：只支持播放，不支持录制（需要 ISO-BMFF 分片重写能力）。
- **HEVC**：WebView2 能否播放 HEVC 取决于系统是否安装 HEVC 视频扩展。不支持的候选会被过滤并提示使用 mpv；本程序**不内置** ffmpeg 软解转码（与“不转码录制”的定位冲突，见 [ADR 0005](docs/adr/0005-桥接服务与打包发布.md)）。
- **抖音签名**：不实现 `a_bogus` / `ms_token` 等风控签名（属绕过平台风控，见 [ADR 0003](docs/adr/0003-解析器实现.md)）。页面无 `roomStore` 时回退 reflow 接口；极端情况下可能解析失败。
- **虎牙 URL 有效期**：候选未声明过期时间，依赖“探测失败即切换候选”兜底。
- **绝对单文件**：WebView2 需要 `WebView2Loader.dll`，播放页与 `tools\` 必须是磁盘文件，因此产物形态为 `exe + Web\ + tools\ + 文档`。
- **Bigo**：对部分地区（如中国大陆 IP）限制访问。

## 构建

```powershell
# 开发运行（需要 .NET 10 SDK）
powershell -NoProfile -ExecutionPolicy Bypass -File build\run-dev.ps1

# 运行全部测试：C# 单测 + 前端回归 + 静态红线自检 + 离线结构分析
powershell -NoProfile -ExecutionPolicy Bypass -File build\test.ps1

# 打包发布：产物写入 D:\文件\实用软件\b站插件\StreamPilot_publish\
#   StreamPilot-windows-v<版本>\          发行版目录
#   StreamPilot-windows-v<版本>.zip       发行版压缩包（与目录同名）
#   StreamPilot-windows-v<版本>.zip.sha256  压缩包校验值
powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1

# 仅静态红线检查（可附带第三方资产 SHA256）
powershell -NoProfile -ExecutionPolicy Bypass -File build\verify-tree.ps1 -VerifyHashes

# 仅离线 C# 结构分析（无 SDK 环境也能运行）
node build\analyze-csharp.mjs
```

### 当前验证状态（.NET SDK 10.0.401 实测）

| 检查项 | 结果 |
|--------|------|
| `dotnet build StreamPilot.slnx`（Debug / Release） | 0 警告 / 0 错误 |
| C# 单元测试 | **80 / 80 通过** |
| 播放策略前端测试（`node --test`） | **13 / 13 通过** |
| 静态红线自检 | 通过（无 TODO / Console / 依赖方向 / 通配监听 / 二进制混入） |
| 离线 C# 结构分析 | 88 文件 / 15119 行 / 133 类型 / 504 方法，无结构性问题 |
| 发布产物 | 发行版压缩包 `StreamPilot-windows-v0.1.0.zip`（54.4 MiB，含 61.4 MiB 的单文件自包含发行版目录） |
| 运行时冒烟测试 | 启动正常；桥接 `/health` 返回 `{"status":"ok","version":"0.1.0","port":5566}`；日志出现 `播放器内核已就绪（HEVC: 支持，H.264: 支持）` 与 `播放页加载完成` |

## 目录结构

```
StreamPilot/
├── CLAUDE.md                    项目规范（质量红线）
├── docs/
│   ├── adr/                     架构决策记录（0001-0005）
│   ├── architecture/            播放消息契约、依赖规则、播放策略
│   ├── parsers/                 各平台解析器说明
│   ├── testing/                 测试与覆盖率矩阵
│   └── runbooks/                故障排查
├── src/
│   ├── StreamPilot.Core         领域模型 / 契约接口 / 错误 / 日志 / HTTP / 配置（无 UI 依赖）
│   ├── StreamPilot.Parsers      六个平台解析器（只依赖 Core）
│   ├── StreamPilot.Recording    原始流录制：FLV/TS 字节级、分片、重连、元数据（只依赖 Core）
│   ├── StreamPilot.Bridge       仅监听 127.0.0.1 的内嵌 HTTP 服务（只依赖 Core）
│   └── StreamPilot.App          WPF + WebView2 界面与组合根
├── tests/
│   ├── StreamPilot.Tests        C# 单元测试（自研极简运行器，运行方式见下）
│   └── web/                     播放策略纯函数测试（node --test）
├── Web/                         播放页（player.html + player-core.js）+ mpegts.js 1.8.2 / hls.js 1.6.16（Apache-2.0）
├── build/                       构建与校验脚本（run-dev / test / publish / verify-tree）
└── tools/                       外部工具目录（用户自备 mpv，不提交仓库）
```

### 运行 C# 单元测试

测试工程是普通控制台程序（不依赖任何测试框架包，见 [ADR 0001](docs/adr/0001-技术栈选型.md)）：

```powershell
dotnet run --project tests/StreamPilot.Tests
dotnet run --project tests/StreamPilot.Tests -- QueryStringParser   # 按类型名过滤
```

退出码 `0` 表示全部通过，`1` 表示存在失败。

## 用户数据

- 配置：`%LOCALAPPDATA%\StreamPilot\config.json`
- 日志：`%LOCALAPPDATA%\StreamPilot\logs\`
- WebView2 缓存：`%LOCALAPPDATA%\StreamPilot\WebView2\`
- 录制输出：默认 `%USERPROFILE%\Videos\StreamPilot\{平台}\{主播}\`

用户数据与程序目录分离，卸载时删除 `%LOCALAPPDATA%\StreamPilot` 即可清理（`config.json` 中可能含 B站 Cookie，请勿分享）。

## 文档索引

- [ADR 0001 技术栈选型](docs/adr/0001-技术栈选型.md)
- [ADR 0002 架构分层与模块划分](docs/adr/0002-架构分层.md)
- [ADR 0003 平台解析器与统一结果结构](docs/adr/0003-解析器实现.md)
- [ADR 0004 原始流录制、自动分片与断流重连](docs/adr/0004-录制实现.md)
- [ADR 0005 桥接服务、Web 播放宿主与打包发布](docs/adr/0005-桥接服务与打包发布.md)
- [播放消息契约](docs/architecture/播放消息契约.md)
- [依赖规则](docs/architecture/依赖规则.md)
- [播放策略（追帧 / 探测 / 恢复）](docs/architecture/播放策略.md)
- [测试与覆盖率矩阵](docs/testing/覆盖率矩阵.md)
- [第三方依赖清单](THIRD-PARTY-NOTICES.md)

## 参考项目

StreamPilot 的三个能力方向分别参考了以下开源 / 公开项目。**均为只读参考：没有复用其源码，也没有修改其任何文件**；具体复用与重写边界见各 ADR。

| 项目 | 地址 | 参考内容 | 本项目做法 |
|------|------|----------|------------|
| **录播姬**（BililiveRecorder） | <https://github.com/BililiveRecorder/BililiveRecorder> | 直播**原始流录制**的行为：FLV 标签级写入、分片触发条件、"断流后新分片总是重发文件头 + onMetaData + 序列头"、时间戳错位/跳变修复思路、侧车元数据 | 参考行为、**独立实现**（`src/StreamPilot.Recording`），见 [ADR 0004](docs/adr/0004-录制实现.md) |
| **Lsar** | <https://github.com/alley-rs/lsar> | 多平台**解析思路**：统一结果结构、房间状态分类、各平台接口与签名算法（B站 / 抖音 / 虎牙 / 斗鱼 / YY / Bigo） | 参考思路、**用 C# 全部重写**（`src/StreamPilot.Parsers`），并修正其无超时、`unreachable!` panic、错误分类不一致等问题，见 [ADR 0003](docs/adr/0003-解析器实现.md) |
| **MultiLive** | <https://www.bilibili.com/video/BV1y1tu66ERj/> | **低延迟播放**：WebView2 宿主与页面的消息契约、三档极限追帧参数、CDN 候选并行探测与切换、冻结恢复阈值、WebView2 虚拟主机映射 | 播放逻辑自研重写（`Web/player.html` + `player-core.js`），并复用其随包的 Apache-2.0 前端库 `mpegts.js 1.8.2` / `hls.js 1.6.16`；修正其 PNA 响应头位置错误，见 [ADR 0005](docs/adr/0005-桥接服务与打包发布.md) 与 [播放策略](docs/architecture/播放策略.md) |

> 上述项目各自适用其自身的开源许可；StreamPilot 的分发物中只包含 `mpegts.js` 与 `hls.js` 两个 Apache-2.0 库（版本与 SHA256 登记于 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)）。

## 质量红线

见 `CLAUDE.md`。摘要：不提交隐私文件与官方二进制、不绕过平台风控、不在 UI 线程阻塞、不吞异常、不无限重试、桥接只监听回环地址、核心模块覆盖率不低于 60%。

## 许可

本项目源码采用 MIT 许可（见 `LICENSE`）。随包分发的 `mpegts.js` 与 `hls.js` 为 Apache-2.0，其许可文本见 `Web\*-LICENSE.txt`，版权与版本见 `THIRD-PARTY-NOTICES.md`。

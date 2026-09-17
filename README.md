# StreamPilot

Windows 桌面直播工具：**多平台低延迟播放** + **多平台解析** + **直播原始流录制**。

Web 端负责低延迟观看，mpv 端负责超分 / HDR / 高画质，两者互不关联。全部为自研源码，最终以自包含产物发布，解压双击即可运行。

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

1. 解压发布包到任意目录（例如 `D:\StreamPilot`）。
2. 双击 `StreamPilot.exe`。
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
pwsh -File build\run-dev.ps1

# 运行全部测试（C# + 前端纯函数）
pwsh -File build\test.ps1

# 打包发布到 D:\文件\实用软件\b站插件\StreamPilot_publish
pwsh -File build\publish.ps1

# 静态自检（红线检查、依赖方向、TODO 扫描）
pwsh -File build\verify-tree.ps1
```

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

## 质量红线

见 `CLAUDE.md`。摘要：不提交隐私文件与官方二进制、不绕过平台风控、不在 UI 线程阻塞、不吞异常、不无限重试、桥接只监听回环地址、核心模块覆盖率不低于 60%。

## 许可

本项目源码采用 MIT 许可（见 `LICENSE`）。随包分发的 `mpegts.js` 与 `hls.js` 为 Apache-2.0，其许可文本见 `Web\*-LICENSE.txt`，版权与版本见 `THIRD-PARTY-NOTICES.md`。

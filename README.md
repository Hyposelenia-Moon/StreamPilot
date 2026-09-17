# StreamPilot

Windows 桌面直播工具：**多平台低延迟播放 + 直播回放录制 + mpv 外挂**。B站 / 抖音 / 虎牙 / 斗鱼 / YY / Bigo 六平台在同一界面处理，解压即用，无需安装运行环境。

本文分两栏：第一栏面向普通用户，第二栏面向开发者（构建、测试、架构、验证状态）。

---

# 一、普通用户

## 1. 项目定位

| 需求 | StreamPilot 的做法 |
|------|--------------------|
| 在电脑上观看直播，延迟低于网页播放 | 内置播放器，提供 150 / 200 / 250 ms 三档追帧，默认 250 ms |
| 获取平台允许的最高画质 | 画面下方「画质」下拉，档位名与官方直播间一致 |
| 边看边保存直播 | 「直播回放录制」：原始流保存、不转码、自动分段、断流自动重连 |
| 超分 / HDR / 更高画质 | 一键将当前直播交给本机 mpv 播放 |

## 2. 安装

1. 解压 `StreamPilot-windows-v0.1.0.zip`，得到同名文件夹；
2. 双击文件夹内的 `StreamPilot-windows-v0.1.0.exe`；
3. 首次启动若提示缺少 **WebView2 运行时**，安装一次 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10 1803+ 与 Win11 通常自带）。

程序为绿色软件：不写注册表、不注册服务，卸载只需删除程序文件夹，用户数据位于独立目录（见第 8 节）。

## 3. 使用

界面左侧为控制面板，右侧为画面。左栏自上而下共四块：**预设 → 直播源 → 当前直播 → 直播回放录制**。

1. 在 **直播源** 中粘贴房间号（例如 `660000`）或直播间网址，点 **解析房间**。
   无需选择平台：程序按链接域名自动识别；仅填房间号时使用设置中的「默认平台」。
2. 解析成功后：
   - **当前直播** 卡片显示 **开播状态 / 主播 / 标题 / 分区**（开播状态位于最上方并加粗）；
   - 在右下方点 **开始播放**（设置中开启「解析成功后自动开始播放」时自动开始）。
3. 若需以后一键打开，点 **＋新增预设**：在对话框中填 **主播名称** 与 **直播链接**，
   确定时先解析该链接，成功才保存并在列表中选中；失败时在对话框内说明原因且不保存。
   预设项带平台徽标、主播名与开播状态；程序启动后自动检测一遍开播状态，可随时点 **刷新状态** 重查。
4. 画面下方是唯一的控制区：**开始播放 / 追帧 / 追帧档位 / 暂停播放⇄继续播放 / 停止播放 / 画质 / 全屏 / 音量**。
   - **暂停播放**：不结束会话，按钮变为 **继续播放**，从当前位置继续；
   - **停止播放**：结束会话并关闭画面（播放器销毁、地址清空），再次观看需重新点 **开始播放**；
   - **追帧**：立即跳到直播最新位置；**追帧档位** 为 150 / 200 / 250 ms，数值越小延迟越低，对网络抖动越敏感。
     程序不自动改档位，显示档位即生效档位，换档后下次播放沿用。
5. 录制：点 **开始录制**，文件按 `{平台}\{主播}\` 存放，命名为「主播名-房间号-开始时间-分片序号」。
6. 更高画质或超分：点左栏 **mpv 播放**（需自备 mpv，见第 6 节）。

## 4. 画质与 Cookie

- 程序按平台官方接口请求当前可用的档位列表，档位名与官方直播间保持一致。
- 默认取可获取的最高档；高档需要登录态时按第 5 节填入 Cookie，否则平台只下发匿名档位。
- **画质下拉始终显示**：房间只有一档时同样显示当前档位，控件置灰，不隐藏。
- 换档会重新向平台请求一次地址（平台地址与档位绑定），画面重新连接属正常现象。
- 档位名照抄平台返回的口径：程序不发明档位，也不显示未获取到的档位。

## 5. （可选）填 Cookie 换取更高画质

**用途**：部分平台将最高档（虎牙「蓝光20M」、B站 4K/HDR、抖音原画）限定给登录用户，填入本账号 Cookie 后方可请求。

**位置**：右上角 **⚙ 设置** → **常用** → **账号 Cookie** → 对应平台输入框 → **保存**。

**获取方式（以 B站为例，其它平台同理）**：

1. 在浏览器打开该平台官网并登录，进入任意正在直播的直播间；
2. 按 `F12`，切到 **Network（网络）**，按 `F5` 刷新；
3. 点任意一个发往该平台域名的请求（B站为 `api.live.bilibili.com`）；
4. 在 **Headers** → **Request Headers** 中找到 `Cookie:` 一行，复制冒号后的内容；只复制关键字段亦可，例如 B站的 `SESSDATA=xxxx`；
5. 回到 StreamPilot 粘贴并 **保存**，再重新点 **解析房间**。

**仅用于解析**：Cookie 只参与解析请求，用于换取播放地址；播放与本地中继不带登录态，CDN 收不到它，主播的观众列表中也看不到。Cookie 仅写入本机 `%LOCALAPPDATA%\StreamPilot\config.json`，不写入日志。

> **虎牙 20M 档**：`https://www.huya.com/152792` 这类房间的「蓝光20M」档，平台要求通过手机虎牙 App 扫码登录后才授予 7 天观看权限；未完成扫码登录时平台不下发该档。本程序只如实列出平台返回的档位，不代做登录、不绕过权限；扫码需在虎牙官方 App 或网页自行完成，再将该会话的虎牙 Cookie 填入。

## 6. mpv 外挂播放（可选）

1. 下载 mpv（<https://mpv.io/>），将 `mpv.exe` 放到程序目录的 `tools\mpv\` 或 `tools\` 下，也可在设置中手动指定路径；
2. 点左栏 **mpv 播放**，即可用 mpv 打开当前直播；
3. 调整画质、缓存、倍速：直接修改本机 mpv 配置。StreamPilot 只传窗口标题与防盗链 `Referer`，不覆盖 mpv 设置。

## 7. 常见问题

| 现象 | 原因与处理 |
|------|-----------|
| 状态行出现「解析失败：主播未开播（状态：未开播）」 | 主播未开播，等开播后再点解析 |
| 状态行出现「找不到这个直播间」 | 房间号或链接有误；部分平台需填**直播间链接**而非主页链接 |
| 状态行出现「平台拒绝了本次请求（可能触发风控）」 | 平台限流，稍后重试；B站可填 Cookie 提高成功率 |
| 状态行出现「Bigo 要求登录后才能读取直播信息」 | Bigo 对匿名或部分地区不返回播放信息，填入该平台 Cookie 后重试 |
| 画面中间提示「所有线路都连不上」 | 该房间分发线路受限或主播刚下播；换档位或改用 **mpv 播放**，详情见日志（第 8 节） |
| 画面卡顿或反复重连 | 先将 **追帧档位** 调到 250 ms（150 ms 对网络最敏感）。程序只在候选多于一条时探测，且不按探测耗时改变线路顺序（见[播放策略](docs/architecture/playback-strategy.md)） |
| 斗鱼房间持续重连 | 该房间只提供 1 条带时效签名的地址，过期即无法连接。程序最多自动重新解析 2 次，之后停止并提示「已停止自动重试」，需点 **开始播放** 手动重试或改用 **mpv 播放** |
| 某房间无「原画」档 | 平台按登录态下发最高档，填入本账号 Cookie 后重新解析（见第 5 节） |
| 音量不是 30 | 30 仅对首次运行生效，程序不会覆盖已保存的音量；在设置中将「默认音量」改为 30 并保存，画面下方滑块与滚轮可随时调整 |
| 状态行或画面提示需要 HEVC 解码 | 系统缺少 HEVC 视频扩展：改用 **mpv 播放**，或在画质下拉中换成 H.264 档位 |
| 画质下拉中选不了其它档 | 该房间只有一个档位时下拉置灰，属正常现象（当前档位即最高档） |
| 斗鱼房间无画面 | 斗鱼多数房间只提供 RTMP，网页播放器无法播放：使用 **mpv 播放**（此时不能录制） |
| 录制文件打不开 | 直播中断导致末尾分片不完整属正常，播放器通常能播到断点；分片大小与时长可在设置中调整 |
| 需要反馈问题 | 设置 → **高级** → 勾选「记录详细诊断日志」，将 `%LOCALAPPDATA%\StreamPilot\logs` 中的日志发出（已自动脱敏签名与 Cookie） |

### 7.1 卡顿与画面停住的排查

日志（`%LOCALAPPDATA%\StreamPilot\logs`）中有两类关键记录：

- **`播放遥测`**：约每 10 秒一条，含 `bufferedAheadMs`（缓冲领先直播边缘的时长）、`secondsSinceProgress`（画面无进展的秒数）、`droppedVideoFrames` / `totalVideoFrames`、`extremeTargetMs`（当前档位）、`reconnects`（当前线路重连次数）；
- **`缓冲失控`**：画面停住而缓冲持续增长时的处置记录（先恢复播放，再追帧，仍无效则按断流重连）。

**判稳口径**：连续取 ≥6 条 `播放遥测`，若 `secondsSinceProgress=0`、`reconnects=0` 且 `bufferedAheadMs` 为数百毫秒量级，则处于稳定低延迟状态。

**缓冲失控判据**：`bufferedAheadMs` 单调上涨（可达数十秒）且 `secondsSinceProgress` 同步增长，即画面停住、缓冲失控（判据与处置顺序见[故障排查](docs/runbooks/troubleshooting.md)第 1.4 节）。

## 8. 数据位置

| 内容 | 位置 |
|------|------|
| 配置（含 Cookie） | `%LOCALAPPDATA%\StreamPilot\config.json` |
| 预设 | `%LOCALAPPDATA%\StreamPilot\presets.json` |
| 日志 | `%LOCALAPPDATA%\StreamPilot\logs\` |
| 网页内核缓存 | `%LOCALAPPDATA%\StreamPilot\WebView2\` |
| 录制文件 | 默认 `%USERPROFILE%\Videos\StreamPilot\{平台}\{主播}\` |

> **状态查看**：解析结果、播放 / 暂停 / 停止、录制状态、失败原因均显示在画面下方的状态行（蓝色为主，警告与错误变色），同时写入日志（`界面事件` 与 `播放遥测` 行）。播放器自身的日志面板已取消，页面诊断文本统一进入宿主日志。

**卸载**：删除程序文件夹与上述 `%LOCALAPPDATA%\StreamPilot` 目录。

---

# 二、开发者

## 1. 环境要求

- Windows 10 / 11 x64；
- .NET 10 SDK（`dotnet --list-sdks` 可见 `10.x`）；
- Node.js（前端回归测试、离线结构分析）；
- 可选：`mpv.exe`（外挂播放）。

## 2. 构建与测试

```powershell
# 开发运行
powershell -NoProfile -ExecutionPolicy Bypass -File build\run-dev.ps1

# 全部测试：C# 单测 + 前端回归 + 静态红线自检 + 离线结构分析
powershell -NoProfile -ExecutionPolicy Bypass -File build\test.ps1

# 打包发布（产物写入 D:\文件\实用软件\b站插件\StreamPilot_publish\）
#   StreamPilot-windows-v<版本>\            发行版目录（含 exe / Web\ / docs\ / VERSION.txt）
#   StreamPilot-windows-v<版本>.zip         发行版压缩包
#   StreamPilot-windows-v<版本>.zip.sha256  压缩包校验值（置于 zip 之外，避免自引用）
powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1

# 仅静态红线检查（可附带第三方资产 SHA256）
powershell -NoProfile -ExecutionPolicy Bypass -File build\verify-tree.ps1 -VerifyHashes

# 仅离线 C# 结构分析（无 SDK 环境亦可运行）
node build\analyze-csharp.mjs
```

单独运行测试：

```powershell
dotnet run --project tests/StreamPilot.Tests                            # C# 单测
dotnet run --project tests/StreamPilot.Tests -- QueryStringParser       # 按关键字过滤
node --test tests/web/player-core.test.js                               # 播放策略纯函数
# 受限环境（不允许 node --test 派生测试运行器子进程）可用：
node --test --test-isolation=none tests/web/player-core.test.js
```

发布产物只取 `build\publish.ps1` 生成的文件：`StreamPilot-windows-v<版本>.zip` 及其校验值文件 `StreamPilot-windows-v<版本>.zip.sha256`。

## 3. 目录结构

```
StreamPilot/
├── CLAUDE.md                    项目规范（质量红线）
├── StreamPilot.slnx             解决方案
├── assets/                      自绘程序图标（ICO，随 exe 嵌入）
├── docs/                        文件名一律 ASCII 英文；正文与注释使用中文
│   ├── adr/                     架构决策记录（0001-technology-stack ~ 0005-bridge-and-packaging）
│   ├── architecture/            依赖规则、播放策略、播放消息契约
│   ├── parsers/                 各平台解析器说明（含画质档位来源）
│   ├── testing/                 测试与覆盖率矩阵
│   └── runbooks/                故障排查
├── src/
│   ├── StreamPilot.Core         领域模型 / 契约接口 / 错误 / 日志 / HTTP / 配置（无 UI 依赖）
│   ├── StreamPilot.Parsers      六个平台解析器（只依赖 Core）
│   ├── StreamPilot.Recording    原始流录制：FLV/TS 字节级、分片、重连、元数据（只依赖 Core）
│   ├── StreamPilot.Bridge       仅监听 127.0.0.1 的内嵌 HTTP 服务与本地中继（只依赖 Core）
│   └── StreamPilot.App          WPF + WebView2 界面与组合根
├── tests/
│   ├── StreamPilot.Tests        C# 单元测试（自研极简运行器）
│   └── web/                     播放策略纯函数测试（node --test）
├── Web/                         播放页（player.html + player-core.js）+ mpegts.js 1.8.2 / hls.js 1.6.16（Apache-2.0）
├── build/                       构建与校验脚本（run-dev / test / publish / verify-tree / analyze-csharp）
└── tools/                       外部工具目录（用户自备 mpv，不提交仓库）
```

## 4. 平台支持

| 平台 | 优先级 | 房间号 | 链接 | Web 播放 | 回放录制 | 画质档位 |
|------|--------|--------|------|----------|----------|----------|
| 哔哩哔哩 | P0 | ✅ | ✅ | ✅ FLV / HLS-TS / HLS-fMP4 | ✅ FLV / TS | 杜比原画 / 4K 原画 / 2K 原画 / 1080P 高码率（有 4K 的房间）/ 1080P 原画 / 1080P 蓝光 / 720P 超清 / 高清 / 流畅（HDR、高帧率后缀取自接口声明） |
| 抖音 | P0 | ✅ | ✅ | ✅ FLV / HLS-TS | ✅ FLV / TS | 原画 / 蓝光 / 超清 / 高清 / 标清 |
| 虎牙 | P0 | ✅ | ✅ | ✅ FLV / HLS-TS | ✅ FLV / TS | 真原画 / 蓝光20M / 2K HDR / 蓝光10M / 蓝光8M / 蓝光4M / 原画（20M 档需手机 App 扫码登录，见第 5 节） |
| 斗鱼 | P1 | ✅ | ✅ | ⚠️ 多数房间仅 RTMP，需 mpv | ⚠️ 仅 FLV / TS 房间可录 | 原画 / 蓝光8M / 蓝光4M / 超清 / 高清 |
| YY | P1 | ✅ | ✅ | ✅ FLV / HLS-TS | ✅ FLV / TS | 默认（平台给定） |
| Bigo Live | P2 | ✅ | ✅ | ✅ HLS-TS | ✅ TS | 默认（平台自带 HLS） |

> 「解析成功」要求直播间正在开播；未开播 / 房间不存在 / 轮播中 / 风控被拒 / 解析错误 / 网络错误会分别给出不同提示。
> 只有候选列表中包含 `FlvHttp` / `HlsTs` 的候选线路才能录制；`HlsFmp4` 与 `Rtmp` 不参与录制。

## 5. 验证状态

| 检查项 | 最新结果 |
|--------|----------|
| `dotnet build StreamPilot.slnx -c Debug` | **0 警告 / 0 错误**（`TreatWarningsAsErrors=true`） |
| `build\test.ps1` | **四个阶段全部通过** |
| C# 单元测试 | **131 / 131 通过**（`tests/StreamPilot.Tests/Cases/*.cs`） |
| 前端回归测试 | **50 / 50 通过**（`tests/web/player-core.test.js`，`node --test`） |
| 静态红线自检 | 通过 |
| `node build/analyze-csharp.mjs` | **101 文件 / 23282 行 / 156 类型 / 749 方法**，未发现结构性问题 |
| 发布产物 | `StreamPilot-windows-v0.1.0.zip`（附校验值文件，见 `build\publish.ps1`） |
| 真实房间实测（B站 814） | 解析 12 条候选；**经真实中继 12/12 返回媒体数据**（FLV 文件头、HLS 播放列表改写后的切片、TS 同步字节） |
| 播放页控制条几何实测 | 无头 Edge 渲染真实页面（`node tests/web/layout-probe/run-probe.js`），900→480 px 逐档切换「追帧 / 停止追帧」与长状态行：**0 处重叠、控件行数不变** |

> 真实房间实测的做法：将解析出的候选线路注册进真实中继（`BridgeHost`）后，像播放页一样只请求本机中继地址完成，覆盖「解析 → 中继 → CDN → 媒体字节」整条链路；它不等于 WebView2 中出画，出画仍需在真机上肉眼确认。

## 6. 已知限制

- **斗鱼**：多数房间只返回 RTMP（Web 端无法播放，需 mpv），此时也不能录制。
- **HLS fMP4**：只支持播放，不支持录制（需要 ISO-BMFF 分片重写能力）。
- **录制时长**：TS 录制暂无最长时长限制（`MaxDurationMinutes` 只作用于 FLV 录制）；FLV 与 TS 均按分片大小与时长切分。
- **HEVC**：能否播放取决于系统是否安装 HEVC 视频扩展；不支持的候选会被过滤并提示改用 mpv，本程序不内置软解转码。
- **抖音签名**：不实现 `a_bogus` / `ms_token` / `__ac_signature` 等风控签名（属绕过风控），因此走房间页内嵌状态、网页端进房接口等只读路径；备用接口被拒时如实提示，不绕过。
- **抖音链接形式**：支持 `live.douyin.com/<房间号>`，也支持分享出的「首页 + 参数」形式（自动读取 `live_web_rid` / `web_rid` / `room_id`）。
- **B站风控**：`getInfoByRoom` 在部分网络返回风控码时自动降级到 `getRoomBaseInfo` 获取主播名与标题，再用播放接口判定开播状态。
- **Bigo**：匿名请求可能要求登录（`needLogin`），此时提示「要求登录后才能读取直播信息」，而不是「未开播」。
- **虎牙 URL 有效期**：候选未声明过期时间，依赖「探测失败即切换候选」兜底。
- **单文件自包含**：WebView2 需要 `WebView2Loader.dll`，播放页与 `tools\` 必须是磁盘文件，因此产物形态为 `exe + Web\ + tools\ + 文档`。

## 7. 架构与红线

- 分层：`App`（UI / 组合根）→ `Core`（模型 / 契约）← `Parsers` / `Recording` / `Bridge`；`Core` 不依赖任何 UI 框架，`Bridge` 只监听 `127.0.0.1`。
- 宿主与播放页只通过消息契约通信（见[播放消息契约](docs/architecture/player-message-contract.md)）：页面不暴露任何可被宿主调用的 `window` 方法。
- 核心约束见 [`CLAUDE.md`](CLAUDE.md)：不提交隐私文件与官方二进制、不绕过平台风控、不在 UI 线程阻塞、不吞异常、不无限重试、核心模块覆盖率不低于 60%。

## 8. 文档索引

- [ADR 0001 技术栈选型](docs/adr/0001-technology-stack.md)
- [ADR 0002 架构分层与模块划分](docs/adr/0002-architecture-layering.md)
- [ADR 0003 平台解析器与统一结果结构](docs/adr/0003-parser-contract.md)
- [ADR 0004 原始流录制、自动分片与断流重连](docs/adr/0004-raw-recording.md)
- [ADR 0005 桥接服务、Web 播放宿主与打包发布](docs/adr/0005-bridge-and-packaging.md)
- [播放消息契约](docs/architecture/player-message-contract.md)
- [依赖规则（含文件名规范）](docs/architecture/dependency-rules.md)
- [播放策略（追帧 / 探测 / 恢复）](docs/architecture/playback-strategy.md)
- [测试与覆盖率矩阵](docs/testing/coverage-matrix.md)
- [故障排查](docs/runbooks/troubleshooting.md)
- [平台解析器说明](docs/parsers/README.md)
- [第三方依赖清单](THIRD-PARTY-NOTICES.md)

## 9. 第三方参考来源

StreamPilot 的三个能力方向分别参考以下开源 / 公开项目。**均为只读参考：未复用其源码，也未修改其任何文件**；具体复用与重写边界见各 ADR。

| 项目 | 地址 | 参考内容 | 本项目做法 |
|------|------|----------|------------|
| **录播姬**（BililiveRecorder） | <https://github.com/BililiveRecorder/BililiveRecorder> | 直播原始流录制的行为：FLV 标签级写入、分片触发条件、「断流后新分片总是重发文件头 + onMetaData + 序列头」、时间戳错位与跳变修复思路、侧车元数据 | 参考行为、独立实现（`src/StreamPilot.Recording`），见 [ADR 0004](docs/adr/0004-raw-recording.md) |
| **Lsar** | <https://github.com/alley-rs/lsar> | 多平台解析思路：统一结果结构、房间状态分类、各平台接口与签名算法（B站 / 抖音 / 虎牙 / 斗鱼 / YY / Bigo） | 参考思路、用 C# 全部重写（`src/StreamPilot.Parsers`），并修正其无超时、`unreachable!` panic、错误分类不一致等问题，见 [ADR 0003](docs/adr/0003-parser-contract.md) |
| **MultiLive** | <https://www.bilibili.com/video/BV1y1tu66ERj/> | 低延迟播放：WebView2 宿主与页面的消息契约、三档极限追帧参数、CDN 候选并行探测与切换、冻结恢复阈值、WebView2 虚拟主机映射 | 播放逻辑自研重写（`Web/player.html` + `player-core.js`），并复用其随包的 Apache-2.0 前端库 `mpegts.js 1.8.2` / `hls.js 1.6.16`；修正其 PNA 响应头位置错误，见 [ADR 0005](docs/adr/0005-bridge-and-packaging.md) 与 [播放策略](docs/architecture/playback-strategy.md) |
| **biliLive-tools** | <https://github.com/renmu123/biliLive-tools> | 平台画质与码率档位的整理：虎牙 `iBitRate` 档位表与 `ratio` 参数、斗鱼 `rate` 档位表、B站 `qn` 表与 HDR 表达方式 | 只参考档位命名与参数含义，自行实现（`src/StreamPilot.Parsers`），未复用其代码 |

> 上述项目各自适用其自身的开源许可；StreamPilot 的分发物中只包含 `mpegts.js` 与 `hls.js` 两个 Apache-2.0 库，其版本与校验值登记于 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

## 10. 许可

本项目源码采用 MIT 许可（见 `LICENSE`）。随包分发的 `mpegts.js` 与 `hls.js` 为 Apache-2.0，其许可文本见 `Web\MPEGTS-LICENSE.txt` 与 `Web\HLS-LICENSE.txt`，版权与版本见 `THIRD-PARTY-NOTICES.md`。

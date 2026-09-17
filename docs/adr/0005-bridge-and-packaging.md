# ADR 0005：桥接服务、Web 播放宿主与打包发布

- 状态：已接受
- 日期：2025-02-14
- 相关文档：`0001-technology-stack.md`、`0004-raw-recording.md`

## 背景

需求与规范要求：

- 桥接服务**只监听 `127.0.0.1`**，响应头必须包含 `Access-Control-Allow-Private-Network: true`；
- 参考项目 MultiLive 的 mpv 桥接依赖外部 `mpv-bridge.ps1`（隐藏 PowerShell 进程），建议**改为内嵌**；
- 最终产物为**自包含单文件 exe**，用户解压后只看到 `exe + Web/ + tools/ + 文档`；
- 播放网页必须能探测 CDN 候选（`fetch(mode:'cors')`）并交给 `mpegts.js`/`hls.js` 播放，因此页面必须运行在安全上下文（`https://`）而非 `file://`。

对参考项目的分析确认了以下事实：

1. MultiLive 用 `SetVirtualHostNameToFolderMapping("appassets.local", <exe>\Web, DenyCors)` + `https://appassets.local/player.html`，通过 `chrome.webview.postMessage` 双向通信；页面**不暴露任何可被宿主直接调用的 `window` 方法**，控制只能靠消息。
2. MultiLive 的 `mpv-bridge.ps1` 监听 `http://127.0.0.1:5566/`，`GET /play?url=` 用 `Start-Process` 拉起 mpv，mpv 参数为 `--cache=no --cache-pause=no --demuxer-readahead-secs=0 --demuxer-max-bytes=512K --demuxer-max-back-bytes=0 --speed=1.08 --audio-pitch-correction=yes`。
3. 该脚本有两个缺陷：`Access-Control-Allow-Private-Network` 被**无条件**加到所有响应（规范要求只在预检响应上），以及 `BeginGetContext` 超时窗口边界会漏掉一个请求。
4. 抖音流与"软解兜底"场景需要一个**本地中继**（`RELAY-` token、Range 支持、`Referer` 透传）才能让浏览器/WebView2 正常拉流。

## 决策

### 1. 内嵌桥接服务：`HttpListener` on `127.0.0.1`，端口自动选择

- 实现于 `StreamPilot.Bridge`（`BridgeHost` + `LoopbackServer`），随 App 启动，随 App 退出。
- **绑定地址硬编码为 `http://127.0.0.1:{port}/`**，端口优先使用 `config.bridge.port`（默认 `5566`），被占用时在 `5566..5575` 范围内依次尝试，全部占用则回落到系统分配端口（`TcpListener` 绑定 `0` 取端口后关闭再交给 `HttpListener`）。**任何情况下不得绑定 `0.0.0.0`/`+`/`*`**，代码中以 `LoopbackOnlyGuard` 断言前缀字符串包含 `127.0.0.1`，违反即抛异常。
- 不使用 ASP.NET Core / Kestrel：那需要引入 `Microsoft.AspNetCore.App` 框架引用，与"依赖极简 + 单文件"冲突；`HttpListener` 是 BCL 内置。
- 不需要管理员权限：`HttpListener` 的 `http://127.0.0.1:port/` 前缀在 Windows 上允许普通用户注册（只有 `+`/`*` 通配前缀才需要 URL ACL）。

### 2. 端点设计

| 方法 | 路径 | 作用 | 说明 |
|---|---|---|---|
| `GET` | `/health` | 存活探测 | 返回 `{"status":"ok","version":"x.y.z","port":N}` |
| `GET` | `/play?url=<enc>&title=<enc>` | 用 mpv 播放指定 URL | 校验 `url` scheme 为 `http/https/rtmp`；启动失败返回 500；成功 200 `ok` |
| `GET` | `/relay/register`（POST body） | 注册一个待中继的上游 URL，返回本地 URL | 用于浏览器直接播放需要 `Referer`/Cookie 的流（B站 FLV、抖音 FLV）；返回 `http://127.0.0.1:{port}/relay/{token}` |
| `GET` | `/relay/{token}` | 中继上游流 | 透传 `Range`/`Content-Type`/`Content-Length`，注入上游 `Referer`/`UA`；支持 `HEAD`；客户端断开即取消上游请求 |
| `OPTIONS` | 任意 | CORS 预检 | 返回 204 + CORS 头；**仅此处**携带 `Access-Control-Allow-Private-Network: true` |
| `GET` | `/web/*` | 播放页与静态资源 | 仅当未使用 WebView2 虚拟主机映射时的回退路径（用于外部浏览器调试） |

- 响应头（所有响应）：`Access-Control-Allow-Origin: *`（仅回环地址，风险可接受）、`Access-Control-Allow-Methods: GET, POST, HEAD, OPTIONS`、`Access-Control-Allow-Headers: *`、`Access-Control-Expose-Headers: Content-Length, Content-Range, Accept-Ranges`、`Cache-Control: no-store`。
- **修复参考项目缺陷**：`Access-Control-Allow-Private-Network` 只在 `OPTIONS` 且请求头包含 `Access-Control-Request-Private-Network: true` 时返回。
- 中继 token：32 字节 `RandomNumberGenerator` → Base64Url；内存字典 `token → RelayTarget`，上限 64 条，10 分钟未访问淘汰；**不落盘**（避免把签名 URL 持久化）。
- 所有处理器都必须 try/catch 并返回结构化错误（`{"error":"..."}`），**禁止吞异常**：捕获后记录 `Error` 日志并把摘要返回给调用方。
- 关闭流程：`HttpListener.Stop()` + 取消所有进行中的中继 `CancellationTokenSource`，`Dispose` 幂等。

### 3. Web 播放宿主：WebView2 虚拟主机 + 消息桥

- `WebViewHost`（App 层）：
  - `CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", <exe dir>\Web, CoreWebView2HostResourceAccessKind.DenyCors)`，导航到 `https://appassets.local/player.html`。
  - 页面与宿主通过 `chrome.webview.postMessage` / `postMessageAsJson` 通信，字段契约见 `docs/architecture/player-message-contract.md`。
  - 页面**不暴露** `window` 方法（沿用参考项目的做法，避免宿主与页面耦合）。
- 宿主 → 页面消息：`play`（`sessionId`、`mode`、`extremeTargetMs`、`candidates[]`）、`chase`（`keepSeconds`）、`stop`。
- 页面 → 宿主消息：`ready`、`status`、`telemetry`、`error`、`refresh-needed` 等；宿主对 `sessionId` 做**过期校验**（忽略旧会话消息，避免竞态）。
- 播放页为**自研重写**（`Web/player.html`），复用 Apache-2.0 的 `mpegts.js 1.8.2` 与 `hls.js 1.6.16`（随包分发并登记 SHA256），移植并修正参考项目的追帧参数、探测与恢复策略（见 `docs/adr/0006` 与 `docs/parsers` 无关，见 `docs/architecture/playback-strategy.md`）。
- 首版**不内置** ffmpeg 软解中继（见下）；HEVC 不支持时按"候选过滤 + 明确提示 + mpv 外挂"处理。

### 4. 外部工具（`tools/`）：不内嵌、不捆绑官方二进制

- `tools/` 目录用于放置用户自备的 `mpv.exe` / `ffmpeg.exe`（首版仅 mpv 必需）。
- **仓库中不提交 `*.exe`/`*.dll`**（红线 2）：`tools/` 通过 `.gitignore` 排除，只提交 `tools/README.md` 说明放置方式。
- mpv 路径解析顺序（内嵌桥接实现，替代 `mpv-bridge.ps1`）：
  1. `config.json` 的 `mpv.path`；
  2. `<exe dir>\tools\mpv\mpv.exe`、`<exe dir>\tools\mpv.exe`；
  3. `PATH` 中的 `mpv.exe`；
  4. 常见安装位置（`%ProgramFiles%\mpv`、`%LOCALAPPDATA%\Programs\mpv`、scoop shims）；
  5. 查找失败 → UI 提示用户手动选择（`OpenFileDialog`）。
- mpv 启动参数只传播放必需的 `--title=StreamPilot - {title}` 与 `--http-header-fields=Referer: {referer}`（需要 Referer 的 B站/抖音/YY/Bigo 流）：缓存、画质、倍速等属用户偏好，交给用户自己的 mpv 配置，程序不覆盖（早期版本曾沿用参考项目的低延迟参数，因会覆盖用户的 mpv 调优而移除）。
- 进程调用必须处理：`Process.Start` 失败、非零退出码、启动超时（10 秒内未创建主窗口则认为失败）；不 `WaitForExit` 阻塞 UI。

### 5. 打包发布

- 发布命令（`build/publish.ps1`）：

```powershell
dotnet publish src/StreamPilot.App/StreamPilot.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none `
  -o "D:\文件\实用软件\b站插件\StreamPilot_publish\StreamPilot-windows-v0.1.0"
```

- **发行版命名**：`{Product}-windows-v{Version}`，产品名与版本号由脚本通过
  `dotnet msbuild -getProperty` 从 `StreamPilot.App.csproj` 求值（脚本内不重复写版本号，
  也不手工解析 XML——工程文件是 UTF-8，在 Windows PowerShell 5.1 下按 ANSI 读取会破坏中文）。
  目录名与可执行文件名相同，例如 `StreamPilot-windows-v0.1.0\StreamPilot-windows-v0.1.0.exe`，
  便于多版本共存时区分。`-OutputDirectory` 的默认值仍为 `StreamPilot_publish`（产物工作区），
  发行版目录创建在其下；若传入的路径本身已是发行版目录名，则直接使用该路径。

- **发行版压缩包**：与发行版目录同名的 `{发行版名}.zip`，用 `Compress-Archive -CompressionLevel Optimal`
  生成，压缩后仅含发行版目录本身（解压即得到 `StreamPilot-windows-v0.1.0\`，不多套一层壳）。
  压缩包的 SHA256 记录在**包外**的 `{发行版名}.zip.sha256`：
  把 zip 的哈希写进 zip 内部（例如 VERSION.txt）属于自引用——写入后哈希必然改变，无法自洽；
  包内 `VERSION.txt` 只记录 `archive=<zip 文件名>`，可执行文件哈希则记录在包内（`exeSha256`）。
  用 `-SkipArchive` 可只生成目录、不压缩。

- `StreamPilot.App.csproj` 关键属性：

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<SatelliteResourceLanguages>zh-Hans;en</SatelliteResourceLanguages>
<InvariantGlobalization>false</InvariantGlobalization>
<Version>0.1.0</Version>            <!-- 决定发行版目录名 -->
<AssemblyVersion>0.1.0.0</AssemblyVersion>   <!-- 决定运行时自报版本 -->
```

- 产物结构（`publish.ps1` 负责组装，全部写入 `StreamPilot_publish\<发行版目录>`，**源码目录不落任何产物**）：

```
StreamPilot_publish\
  StreamPilot-windows-v0.1.0.zip          # 发行版压缩包（发这个文件给用户）
  StreamPilot-windows-v0.1.0.zip.sha256   # 压缩包校验值（包外记录，避免自引用）
  StreamPilot-windows-v0.1.0\
    StreamPilot-windows-v0.1.0.exe  # 单文件：.NET 运行时 + 托管程序集 + WebView2Loader
    Web\                            # 播放页与库（必须为磁盘文件：虚拟主机映射需要真实目录）
      player.html  player-core.js  mpegts.js  hls.js  HLS-LICENSE.txt  MPEGTS-LICENSE.txt
    tools\                          # 用户自备外部工具（脚本创建，含 README）
    docs\  README.md  THIRD-PARTY-NOTICES.md  LICENSE
    VERSION.txt                     # 产品名、发行版标识、版本、exe 名、构建时间、git describe、exe SHA256、Web 资产清单
```

- `SatelliteResourceLanguages=zh-Hans;en` 避免 WPF 卫星程序集把包撑大（参考项目带了 13 个语言目录）。
- 构建脚本行为：
  1. 校验 `dotnet --list-sdks` 中存在 `10.` 开头的 SDK，否则**报错退出**并打印安装指引（不静默继续）；
  2. 用 MSBuild 求值产品名与版本号，得出发行版目录名与可执行文件名；
  3. `powershell -File build/test.ps1` 运行四阶段测试，非零退出码即中止发布（可用 `-SkipTests` 跳过）；
  4. `dotnet publish` 到发行版目录，并把 `StreamPilot.exe` 重命名为发行版同名 exe；
  5. 复制 `Web/`、`docs/`、`README.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`tools/README.md`；
  6. 生成 `VERSION.txt`（产品名、发行版标识、版本号、可执行文件名、构建时间、`git describe`、
     目标平台、exe 的 SHA256、Web 资产清单、压缩包文件名）；
  7. 压缩为同名的 `{发行版名}.zip` 并把 zip 的 SHA256 写入包外 `{发行版名}.zip.sha256`。
- `build/run-dev.ps1`：开发期 `dotnet run --project src/StreamPilot.App`，并校验桥接端口健康。
- `build/verify-tree.ps1`：静态自检（禁止 `TODO/FIXME/HACK`、禁止注释代码块标记、禁止在 `src/` 出现 `Console.WriteLine`、禁止 `src/` 内出现二进制、检查依赖方向）。

### 6. 不内置 ffmpeg 软解中继（首版）

- 参考项目 MultiLive 用 ffmpeg 把 HEVC 实时转码成 H.264 喂给 WebView2。本 ADR **不在首版实现**，理由：
  1. 与"原始流录制、不转码"的项目定位冲突，容易误导；
  2. 需要捆绑 ffmpeg（6.7MB，GPL 构建），涉及许可证与体积问题（红线：>50MB 依赖需确认，虽未超但趋势不利）；
  3. HEVC 场景已有两条替代路径：mpv 外挂播放（超分/HDR 正是 mpv 的强项）与 Web 端明确提示。
- 保留扩展点：`CandidateFilter` 在发现 HEVC 不可解码时只做"过滤 + 提示"，`PlaybackPlan.UnsupportedCodec` 由 UI 决定后续动作（推荐 mpv）。
- 若未来决定内置，**必须先修订本 ADR** 并评估 GPL 合规与体积。

## 影响

### 正面

- 桥接从外部 PowerShell 脚本变为内嵌服务：无隐藏进程、无脚本执行策略问题、无 3 秒等待窗口、可精确控制超时与 CORS。
- 单文件 exe + `Web/` + `tools/` 的产物形态与需求第 3.4 节一致。
- 中继服务使需要 `Referer` 的流能在 WebView2 内播放，同时保持签名 URL 只在内存中。

### 负面 / 风险

- `HttpListener` 在高并发下性能弱于 Kestrel；本项目并发上限为"1 个播放页 + 1 个录制 + 少量探测"，足够。
- `WebView2Loader.dll` 与 `Web/` 仍需在磁盘上，因此"绝对单文件"不可达；已在 `VERSION.txt`、`README` 与安装说明中明确。
- 中继会占用额外带宽（本地回环 + 上游），但避免了跨域与 Referer 限制。

## 后续

- 若需要支持外网浏览器遥控（例如手机端作为遥控器），需要引入鉴权与 TLS，必须先修订本 ADR。
- 若引入 fMP4 录制或 ffmpeg 软解，同样需要修订本 ADR。

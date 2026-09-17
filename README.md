# StreamPilot

Windows 桌面直播工具：**多平台低延迟播放 + 直播回放录制 + mpv 外挂**。B站 / 抖音 / 虎牙 / 斗鱼 / YY / Bigo 一个界面搞定，解压即用、不需要装任何环境。

> 本文分成两栏：**普通用户**只看第一栏就够了；**开发者**（构建、测试、架构、验证状态）内容在第二栏。

---

# 一、普通用户

## 1. 这是什么

| 想做的事 | StreamPilot 怎么做 |
|----------|-------------------|
| 用电脑看直播，画面比网页更跟手 | 内置播放器，支持 150 / 200 / 250 ms 三档追帧（与参考低延迟播放页逐项对齐） |
| 想要最高画质（B站 4K/原画、虎牙蓝光20M…） | 画面下方「画质」下拉，档位名与官方直播间一致 |
| 一边看一边把直播存下来 | 「直播回放录制」：原样保存、不转码、自动分段、断流自动重连 |
| 想要超分 / HDR / 高画质 | 一键把当前直播交给自己的 mpv 播放 |

## 2. 下载与安装

1. 解压 `StreamPilot-windows-v0.1.0.zip`，得到同名文件夹；
2. 双击文件夹里的 `StreamPilot-windows-v0.1.0.exe`；
3. 首次启动若提示缺少 **WebView2 运行时**，装一次 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10 1803+ 与 Win11 一般自带）。

绿色软件：程序不写注册表、不装服务，卸载就是删掉文件夹（用户数据在另一处，见第 8 节）。

## 3. 三分钟上手

1. 在左栏 **房间号 / 直播间链接** 里粘贴房间号（例如 `660000`）或直播间网址；
2. 点 **解析房间**：解析成功后按设置决定是否立即播放（结论与失败原因写进程序日志，见第 8 节）；
   - 想以后一键打开，点 **新增预设**：在弹出的对话框里填 **主播名称** 与 **直播链接**（直播间网址或房间号），
     程序会先解析这条链接，解析成功才保存并在「预设」列表里选中它；解析失败会说明原因且**不保存**，
     之后在「预设」列表里点它就会自动填入并解析；
3. 右侧画面**下方**点 **开始播放**（解析成功后若开启了自动播放，会自动开始）；
4. 想换画质：画面下方 **画质** 下拉（不同平台档位名不同，例如 B站「4K 原画」、抖音「原画」、虎牙「蓝光20M」）；
5. 想暂停：画面下方 **暂停播放**。暂停**不结束会话**，点 **继续播放** 就从当前位置接着看；
   点 **停止播放** 则结束会话并**关闭画面**（播放器被销毁、地址被清空、本地中继被宿主释放）；
6. 想录制：点 **开始录制**，文件按 `{平台}\{主播}\` 存放，命名为「主播名-房间号-开始时间-分片序号」；
7. 想要更高画质/超分：点 **mpv 播放**（需要你自己准备 mpv，见第 6 节）。

> 所有播放控制（开始播放 / 暂停播放 / 停止播放 / 追帧 / 追帧档位 / 画质 / 全屏 / 音量）只在**画面下方**一处，
> 左栏不再重复。**追帧档位**是 150 / 200 / 250 ms 三档（默认 250），数字越小延迟越低、
> 对网络抖动越敏感；程序**不会**替你改档位（看到的档位就是生效的档位）。

## 4. 画质与码率怎么来的

- 程序会按平台官方接口请求**当前可用的档位列表**，档位名尽量与官方直播间一致（原画 / 4K / HDR / 蓝光20M / 超清 / 高清…）。
- 默认取**能拿到的最高档**；如果某个高档需要登录态，请按第 5 节填 Cookie，否则平台只会给你匿名档位。
- **画质下拉始终显示**：房间只有一档时会显示当前档位（控件置灰，没有别的可选），不会凭空消失。
- 换档会重新向平台请求一次地址（平台地址本身与档位绑定），因此切换后画面会重新连接，这是正常现象。

### 4.1 画质档位说明（各平台口径）

档位名一律**如实照抄平台返回的口径**；程序不会自己发明档位，也不会把没拿到的档位画出来。

| 平台 | 档位口径 | 说明 |
|------|----------|------|
| 哔哩哔哩 | 有 4K 的房间：`4K 原画` → `1080P 高码率` → `1080P 蓝光` → `720P 超清` → `高清` → `流畅`；**没有 4K** 的房间：`1080P 原画` → `1080P 蓝光` → `720P 超清` → `高清` → `流畅`。另有 `杜比原画`（qn=30000）与 `2K 原画`（qn=15000，平台声明时才出现） | 同一档 qn=10000 在**有 4K** 的房间里叫「1080P 高码率」，在**没有 4K** 的房间里叫「1080P 原画」，具体叫法由该房间的 `accept_qn` 是否含 4K 决定 |
| 抖音 | `原画` / `蓝光` / `超清` / `高清` / `标清`（纯音频档 `ao` 排在最后，不会被当成最高档） | 档位键来自 `options.qualities` / `stream_url.flv_pull_url` / `hls_pull_url_map`；**「原画」只在平台下发 `origin` 时出现**，若你的抖音账号能看到原画而这个程序看不到，请填抖音 Cookie 后重解析（见第 5 节） |
| 虎牙 | `真原画` / `蓝光20M` / `2K HDR` / `蓝光10M` / `8M` / `4M` / `原画` | 名称与码率取自平台 `bitRateInfo` / `rateArray`；平台没给某档就不会出现 |
| 斗鱼 | `原画` / `蓝光8M` / `蓝光4M` / `超清` / `高清` | 名称取自 `multirates[].name`，同时显示平台给的 `bitRate` |
| YY | `默认（平台给定）` | 平台接口不下发档位语义，程序只列一条「默认」 |
| Bigo Live | `默认（平台给定）` | 接口只给单条 HLS，没有可选档位 |

**B站 HDR / 高帧率后缀的判定依据**（这两个后缀都不是程序猜的）：

- 来源只有一个：`getRoomPlayInfo` 响应里的 `data.playurl_info.playurl.g_qn_desc[]`。
- 高帧率：该档位的 `media_base_desc.detail_desc.tag` 数组里含「高帧率」才会加后缀。
  实测 `live.bilibili.com/814`（1080P 60 帧）的 `qn=10000` 为
  `media_base_desc.detail_desc.desc="1080P 原画"` + `tag=["高帧率"]` → 显示 `1080P 原画（高帧率）`；
  实测 `live.bilibili.com/1868871278`（1080P 无 60 帧）的 `qn=10000` **没有 `tag` 字段** → 显示 `1080P 原画`。
- HDR：`hdr_type` 非 0、或 `hdr_desc` / `attr_desc` / `detail_desc.tag` 里含 `HDR` 时加 `HDR` 后缀。
- 拿不到 `g_qn_desc` 时**不加任何后缀**（宁可少标，也不假标）。

## 5. （可选）填 Cookie 换取更高画质

**为什么需要**：部分平台把最高档（例如虎牙“蓝光 20M”、B站 4K/HDR）留给登录用户。填上你自己账号的 Cookie 后，程序就能请求到这些档位。

**注意**：Cookie 相当于你的登录凭证，请只填在自己的电脑上；程序把它保存在本机 `%LOCALAPPDATA%\StreamPilot\config.json`，**不会**写进日志、**不会**随程序发布、**不会**发给 CDN。

### 获取步骤（以 B站为例，其它平台同理）

1. 用浏览器打开该平台官网并**登录**；打开任意一个正在直播的直播间；
2. 按 `F12` 打开开发者工具 → 切到 **Network（网络）** 面板 → 按 `F5` 刷新页面；
3. 在请求列表里点任意一个发往该平台域名（B站是 `api.live.bilibili.com`）的请求；
4. 在 **Headers（标头）** → **Request Headers（请求标头）** 里找到 `Cookie:` 一行，**复制整行冒号后面的内容**；
   - 想省事：也可以只复制关键字段，例如 B站的 `SESSDATA=xxxx`（分号分隔的多个 `名字=值` 都行）；
5. 回到 StreamPilot → 右上角 **⚙** → **高级** → **账号与 Cookie** → 把内容粘贴到对应平台输入框 → **保存**；
6. 重新点一次 **解析房间**，画质下拉里就会出现更高档位。

> 不想填也没关系：留空即匿名解析，只是拿不到需要登录的最高档。

> **虎牙 20M 档需要手机 App 扫码登录**：`https://www.huya.com/152792` 这类房间的「蓝光20M」档，
> 平台要求**用手机虎牙 App 扫码登录**后才给 7 天观看权限（用户实测）。StreamPilot 只把平台**如实返回**的档位列出来；
> 没登录时平台不下发该档，程序就显示不出它。本程序**不代做登录、不绕权限**——请按本节第 1~5 步填你自己账号的
> 虎牙 Cookie 后再解析；扫码登录那一步请到虎牙官方 App / 网页自行完成。

> **抖音「原画」同样需要登录态**：匿名请求通常只拿到 `FULL_HD1/HD1/SD1/SD2` 这类老档位，
> `origin`（原画）由平台按登录态下发。请把抖音 Cookie 也填上（设置 → 高级 → 账号与 Cookie）；
> 若填了仍然没有「原画」，请开启详细诊断日志并查看 `抖音档位诊断` 一行：它列出本次响应里
> `options.qualities` / `flv_pull_url` / `hls_pull_url_map` 实际给出的档位键与 `hasOrigin`，
> 可以区分"平台确实没给"与"程序没取到"。

## 6. mpv 外挂播放（可选）

1. 下载 mpv（<https://mpv.io/>），把 `mpv.exe` 放到程序目录的 `tools\mpv\` 下（或 `tools\` 下）；
2. 点界面上的 **mpv 播放** 即可用 mpv 打开当前直播；
3. 想调画质/缓存/倍速：直接改**你自己的 mpv 配置**（`mpv.conf` 等）。StreamPilot 只传窗口标题与防盗链 `Referer`，不会覆盖你的 mpv 设置。

## 7. 常见问题

| 现象 | 原因与处理 |
|------|-----------|
| 日志里出现「解析失败：主播未开播（状态：未开播）」 | 主播确实没在播。等开播后再点解析 |
| 日志里出现「找不到这个直播间」 | 房间号或链接写错了；有的平台要填**直播间链接**而不是主页链接 |
| 日志里出现「平台拒绝了本次请求（可能触发风控）」 | 平台限流，等一会儿再试；B站可填 Cookie 提高成功率 |
| 日志里出现「Bigo 要求登录后才能读取直播信息」 | Bigo 对匿名/部分地区不返回播放信息，填该平台 Cookie 后重试 |
| 点开始播放后提示「所有线路都连不上」 | 多数是该房间的分发线路受限或主播刚下播；换档位或改用 mpv 播放；画面中间会给出提示，详情见日志（见第 8 节） |
| 画面一顿一顿 / 反复重连 | 先看画面下方的「追帧档位」：150 ms 对网络最敏感，仍不稳就手动选 250 ms。程序只在候选多于一条时做探测，且不会按探测耗时改变线路顺序（详见[播放策略](docs/architecture/playback-strategy.md)） |
| 画面停住但缓冲一直涨（例如 80 秒） | 缓冲失控：程序会先恢复播放 / 追帧，无效才重连。判据见[故障排查](docs/runbooks/troubleshooting.md)第 1.4 节 |
| 某个房间看不到「原画」档 | 平台按登录态下发最高档：抖音/B站/虎牙都需要在设置里填自己的 Cookie 后重新解析（见第 5 节） |
| 抖音已能播放但没有「原画」 | 同上，填抖音 Cookie；若填了仍没有，打开详细诊断日志看 `抖音档位诊断` 行的 `hasOrigin`（见第 5 节末） |
| 斗鱼房间「一直在重连」 | 该房间的平台接口只给了 1 条带时效签名的地址，地址一过期就必然连不上。程序现在最多自动重新解析 2 次，之后会停下来提示「已停止自动重试」，请点「开始播放」手动重试或改用「mpv 播放」，不再无限重连 |
| 画面还是要点一下才开始播放 | 宿主 WebView2 已按「无需用户手势即可自动播放」启动；只有在极旧的 WebView2 运行时上才会退回「静音起播 → 立刻恢复音量」的兜底路径。若两种情况都失败，画面中间会给出提示，点一下画面即可 |
| 音量怎么是 70，不是 30 | 30 只对**首次运行**生效。程序不会强改你已经保存过的音量；请在设置里把「默认音量」改为 30 并保存 |
| 画面提示需要 HEVC 解码 | 系统缺少 HEVC 视频扩展：改用 **mpv 播放**，或在画质下拉里换成 H.264 档位 |
| 斗鱼房间没有画面 | 斗鱼多数房间只给 RTMP，网页播放器放不了：用 **mpv 播放**（此时也不能录制） |
| 录制文件打不开 | 直播中断导致的分片末尾不完整属正常，播放器一般能播到断点；分片大小/时长可在设置里调 |
| 想反馈问题 | 设置 → 高级 → 勾选「记录详细诊断日志」，然后把 `%LOCALAPPDATA%\StreamPilot\logs` 里的日志发出来（已自动脱敏签名与 Cookie） |

## 8. 我的数据在哪

| 内容 | 位置 |
|------|------|
| 配置（含 Cookie） | `%LOCALAPPDATA%\StreamPilot\config.json` |
| 预设 | `%LOCALAPPDATA%\StreamPilot\presets.json` |
| 日志 | `%LOCALAPPDATA%\StreamPilot\logs\` |
| 网页内核缓存 | `%LOCALAPPDATA%\StreamPilot\WebView2\` |
| 录制文件 | 默认 `%USERPROFILE%\Videos\StreamPilot\{平台}\{主播}\` |

> **解析结论与失败原因去哪看**：界面上只在**画面下方的状态行**显示播放状态；
> 解析成功/失败、播放/暂停/停止、页面诊断文本都写进同一份日志文件
> （日志里的 `界面事件` 与 `播放遥测` 行），排查问题请按上一节的「想反馈问题」取日志。

> **默认音量**：首次运行（或配置里没有 `playback.volume` 字段）时为 **30**；已经保存过音量的用户保持原值不变，
> 需要改请在设置里手动调整。播放页右下角也能随时用滑块或滚轮调。

**卸载**：删掉程序文件夹 + 上面的 `%LOCALAPPDATA%\StreamPilot` 目录即可。

**隐私说明**：Cookie 只用于「解析」这一步换取播放地址；实际拉流走的是平台 CDN 的匿名地址，程序不会把你的登录态发给 CDN，也不会向平台发送“在看”心跳，因此主播的观众列表里不会出现你。

---

# 二、开发者

## 1. 环境要求

- Windows 10 / 11 x64；
- .NET 10 SDK（`dotnet --list-sdks` 能看到 `10.x`）+ Node.js（仅用于前端回归测试）；
- 可选：`mpv.exe`（外挂播放）。

## 2. 构建与测试

```powershell
# 开发运行
powershell -NoProfile -ExecutionPolicy Bypass -File build\run-dev.ps1

# 全部测试：C# 单测 + 前端回归 + 静态红线自检 + 离线结构分析
powershell -NoProfile -ExecutionPolicy Bypass -File build\test.ps1

# 打包发布（产物写入 D:\文件\实用软件\b站插件\StreamPilot_publish\）
#   StreamPilot-windows-v<版本>\           发行版目录
#   StreamPilot-windows-v<版本>.zip        发行版压缩包
#   StreamPilot-windows-v<版本>.zip.sha256 压缩包校验值（放在 zip 外面，避免自引用）
powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1

# 仅静态红线检查（可附带第三方资产 SHA256）
powershell -NoProfile -ExecutionPolicy Bypass -File build\verify-tree.ps1 -VerifyHashes

# 仅离线 C# 结构分析（无 SDK 环境也能运行）
node build\analyze-csharp.mjs
```

单独跑测试：

```powershell
dotnet run --project tests/StreamPilot.Tests                            # C# 单测
dotnet run --project tests/StreamPilot.Tests -- QueryStringParser       # 按类型名过滤
node --test tests/web/player-core.test.js                               # 播放策略纯函数
# 受限环境（不允许 `node --test` 派生测试运行器子进程）可用：
node --test --experimental-test-isolation=none tests/web/player-core.test.js
```

## 3. 目录结构

```
StreamPilot/
├── CLAUDE.md                    项目规范（质量红线）
├── assets/                      自绘程序图标（ICO，随 exe 嵌入）
├── docs/                        文件名一律 ASCII 英文；正文与注释使用中文
│   ├── adr/                     架构决策记录（0001-technology-stack ~ 0005-bridge-and-packaging）
│   ├── architecture/            依赖规则（含文件名规范）、播放策略、播放消息契约
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
| 哔哩哔哩 | P0 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS | 杜比 / 4K / 2K / 1080P 原画（有 4K 的房间叫「1080P 高码率」）/ 蓝光 / 超清 / 高清（HDR、高帧率后缀取自接口声明） |
| 抖音 | P0 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS | 原画 / 蓝光 / 超清 / 高清 / 标清 |
| 虎牙 | P0 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS | 真原画 / 蓝光20M / 2K HDR / 蓝光10M / 8M / 4M / 原画（20M 档需手机 App 扫码登录，见第 5 节） |
| 斗鱼 | P1 | ✅ | ✅ | ⚠️ 多数房间仅 RTMP，需 mpv | ⚠️ 同上 | 原画 / 蓝光8M / 蓝光4M / 超清 / 高清 |
| YY | P1 | ✅ | ✅ | ✅ FLV / HLS | ✅ FLV / TS | 默认（平台给定，档位语义未公开） |
| Bigo Live | P2 | ✅ | ✅ | ✅ HLS | ✅ TS | 默认（接口只给单条 HLS） |

> 「解析成功」需要直播间**正在开播**；未开播 / 房间不存在 / 轮播中 / 风控被拒 / 解析错误 / 网络错误会分别给出不同提示。

## 5. 当前验证状态（.NET SDK 10.0.401）

| 检查项 | 结果 |
|--------|------|
| `dotnet build StreamPilot.slnx`（Debug / Release） | 本轮改动未在本机编译（沙箱不允许 `dotnet build`），需在集成环境复验 |
| C# 单元测试 | 本轮新增：抖音档位键优先级与「原画只能来自 origin」、抖音 Cookie 合并（含作用域内合并与作用域外不合并）、B站档位声明全缺失时用 `current_qn` 兜底；请以 `build\test.ps1` 的实际输出为准 |
| 播放策略前端测试 | **34 / 34 通过**（`node --test --experimental-test-isolation=none tests/web/player-core.test.js`）；新增缓冲失控保护（阈值 / 处置顺序 / 宽限期）与播放页静态结构（顶部提示已移除、播放控制三键顺序）用例 |
| 静态红线自检 | 通过（无 TODO / Console / 依赖方向 / 通配监听 / 二进制混入） |
| 离线 C# 结构分析 | 无结构性问题 |
| 发布产物 | `StreamPilot-windows-v0.1.0.zip`（约 62 MiB，含单文件自包含发行版目录；校验值见 `zip.sha256`） |
| 运行时冒烟测试 | 启动正常；桥接 `/health` 返回 `{"status":"ok",...}`；播放页报告 `HEVC: 支持，H.264: 支持` |
| 平台解析实测 | 逐房间遍历：虎牙 8 个、B站 8 个、斗鱼 8 个、YY 2 个（另 4 个已下播房间正确判为「未开播」）全部解析成功；抖音/Bigo 因需要真实在播房间，仅验证了失败分类 |
| 画质档位实测 | 上述每个房间都枚举出平台官方档位（虎牙 `蓝光10M/20M/30M`、B站 `原画/蓝光/超清`、斗鱼 `原画1080P60/2K60`…），且指定档位后**确实生效**（虎牙地址变为 `&ratio=4000`、B站 qn 变为 `400`、斗鱼 `rate` 变为 `2`） |
| B站高帧率实测 | `GET .../getRoomPlayInfo?...&room_id=814`（60 帧）与 `room_id=1868871278`（无 60 帧）对比：前者 `g_qn_desc[qn=10000].media_base_desc.detail_desc.tag=["高帧率"]`，后者该字段缺失；据此显示 `1080P 原画（高帧率）` / `1080P 原画` |
| 抖音路径实测 | 房间页 `<script>self.__pace_f.push` 里 `roomStore` 出现 **2 次**（第一次 `roomInfo:{}` 空壳、第二次含 `anchor`/`room`）；`GET live.douyin.com/webcast/room/web/enter/?...&web_rid=745964462470` 带首页 `ttwid` 时返回 `status_code:0` 与完整房间 JSON，而 `webcast.amemv.com/.../reflow/info/` 即使补齐公开参数仍返回 `status_code:10011` |
| 斗鱼重连实测 | 房间 `1811143` 只返回 1 条候选（`rtmp_url`+`rtmp_live`，`video/x-flv`，实测 12 秒 19.5 MB）；同一签名地址第 2 条并发连接被上游在 0.2–0.4 秒切断；`data.cdnsWithName` 的第二条 CDN（`hw-h5`）返回另一主机 `hw1a.douyucdn2.cn`（同样 200 / `video/x-flv`） |
| 播放实测 | 虎牙 `660000`：8/8 候选可用（FLV 经本地中继、HLS 经播放列表改写的本地中继），首帧成功、画面正常；播放中切换画质/重新下发计划不再黑屏（旧中继保留到新会话首帧） |
| 界面验证 | 主按钮与选中档位为白字（`TextBlock` 隐式样式不再覆盖按钮前景色）；「新增预设」在预设卡片头部、始终可点，保存后在列表中选中该项 |
| 界面精简（本轮） | 播放页顶部状态行 `#modeHint` 已彻底删除（元素 / CSS / 全部 JS，含 `formatModeHint`），播放控制三键改为「开始播放 → 暂停/继续播放 → 停止播放」并缩小一号（停止会销毁播放器并通知宿主释放中继）；左栏「当前直播」卡片已删除，状态只在画面下方（改为主题强调蓝）；设置窗口底部常驻文案已删除，只在失败时出现提示 |
| 界面调整（本轮） | 左栏恢复「当前直播」卡片（「直播源」下方、录制面板上方），显示 `开播状态 / 主播 / 标题 / 分区` 四行（**开播状态在最上方且加粗**，状态词取自解析结果：直播中 / 未开播 / 轮播中 / 未知），不含候选线路摘要与桥接状态（这两处仍只在画面下方状态行 / 日志里）；「新增预设」改为两字段专用对话框（`Views/PresetDialog.cs`，主播名称 + 直播链接，确定时先异步解析，失败在对话框内提示且不保存）；录制面板标题下方改为一句规范说明（只点出"要最高画质需在设置里填对应平台 Cookie"）；设置窗口按「常用 / 高级」重排为统一表单（一项一行标签 + 一行控件，窄控件统一 180px，宽输入占满，说明一律进 ToolTip） |
| 录制上限（本轮） | 分片大小上限默认 **10 GiB**、分片时长上限默认 **8 小时**、最长录制时长默认 **8 小时**；设置界面按 GiB / 小时填写并换算成字节 / 分钟（`RecordingLimitUnits`），只校验"必须大于 0"，**不再有最大上限**（0、负数、非数字给出明确提示且不保存）；`SegmentPolicyOptions.Normalize()` 同样只回退非法值、不夹取大值 |
| 本轮界面调整未复验部分 | 对话框与设置窗口的 XAML 只做了良构与绑定名比对，实际布局（缩放 / 高 DPI / 键盘焦点）需在集成环境手动过一遍 |
| 本轮未能在本机复验的部分 | 播放页脚本、XAML、C# 均**未在本机运行**（沙箱禁止 `dotnet build`/`dotnet run`，且本机无法访问平台域名做真实解析）。缓冲失控保护、画质下拉与暂停/继续/停止的端到端表现需在集成环境用真实房间复验（判据见 `docs/runbooks/troubleshooting.md` 第 1.4 节） |

## 6. 已知限制

- **斗鱼**：`getH5PlayV1` 常只返回 RTMP 地址，Web 端无法播放（会明确提示并用 mpv），此时也不支持原始流录制。
- **HLS fMP4 录制**：只支持播放，不支持录制（需要 ISO-BMFF 分片重写能力）。
- **HEVC**：能否播放取决于系统是否装 HEVC 视频扩展；不支持的候选会被过滤并提示改用 mpv，本程序**不内置** ffmpeg 软解转码。
- **抖音签名**：不实现 `a_bogus` / `ms_token` / `__ac_signature` 等风控签名（属绕过平台风控）。因此解析分三条路径：
  1. 房间页内嵌状态（React Flight / `RENDER_DATA` 里的 `roomStore.roomInfo`）——**页面里有两次 `roomStore`，第一次是空壳 `roomInfo:{}`，程序会跳过它取第二次**；
  2. 网页端进房接口 `live.douyin.com/webcast/room/web/enter/`——只需要 UA + `Referer` + 首页 `Set-Cookie` 下发的 `ttwid`，**不需要任何签名**；缺 `ttwid` 时该接口返回「HTTP 200 + 空正文」，程序会识别并降级；
  3. `webcast.amemv.com/webcast/room/reflow/info/`——实测即使补齐 `version_code` / `app_id` 也固定返回 `status_code=10011 Request params error`，只作为最后的兜底。
- **抖音链接形式**：支持 `live.douyin.com/<房间号>`，也支持分享出来的「首页 + 参数」形式（自动读 `live_web_rid` / `web_rid` / `room_id`）。
- **虎牙 URL 有效期**：候选未声明过期时间，依赖"探测失败即切换候选"兜底。
- **B站风控**：`getInfoByRoom` 在部分网络返回 `-352`，此时自动改用 `getRoomBaseInfo` 取主播名与标题，再用播放接口判定开播状态。
- **Bigo**：匿名请求可能返回 `needLogin: true`（部分地区/IP 受限），此时提示"要求登录后才能读取直播信息"，而不是"未开播"。
- **绝对单文件**：WebView2 需要 `WebView2Loader.dll`，播放页与 `tools\` 必须是磁盘文件，因此产物形态是 `exe + Web\ + tools\ + 文档`。

## 7. 架构与质量红线

- 分层：`App`（UI/组合根）→ `Core`（模型/契约）← `Parsers` / `Recording` / `Bridge`；`Core` 不依赖任何 UI 框架，`Bridge` 只监听 `127.0.0.1`。
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

## 9. 参考项目

StreamPilot 的三个能力方向分别参考了以下开源 / 公开项目。**均为只读参考：没有复用其源码，也没有修改其任何文件**；具体复用与重写边界见各 ADR。

| 项目 | 地址 | 参考内容 | 本项目做法 |
|------|------|----------|------------|
| **录播姬**（BililiveRecorder） | <https://github.com/BililiveRecorder/BililiveRecorder> | 直播**原始流录制**的行为：FLV 标签级写入、分片触发条件、"断流后新分片总是重发文件头 + onMetaData + 序列头"、时间戳错位/跳变修复思路、侧车元数据 | 参考行为、**独立实现**（`src/StreamPilot.Recording`），见 [ADR 0004](docs/adr/0004-raw-recording.md) |
| **Lsar** | <https://github.com/alley-rs/lsar> | 多平台**解析思路**：统一结果结构、房间状态分类、各平台接口与签名算法（B站 / 抖音 / 虎牙 / 斗鱼 / YY / Bigo） | 参考思路、**用 C# 全部重写**（`src/StreamPilot.Parsers`），并修正其无超时、`unreachable!` panic、错误分类不一致等问题，见 [ADR 0003](docs/adr/0003-parser-contract.md) |
| **MultiLive** | <https://www.bilibili.com/video/BV1y1tu66ERj/> | **低延迟播放**：WebView2 宿主与页面的消息契约、三档极限追帧参数、CDN 候选并行探测与切换、冻结恢复阈值、WebView2 虚拟主机映射 | 播放逻辑自研重写（`Web/player.html` + `player-core.js`），并复用其随包的 Apache-2.0 前端库 `mpegts.js 1.8.2` / `hls.js 1.6.16`；修正其 PNA 响应头位置错误，见 [ADR 0005](docs/adr/0005-bridge-and-packaging.md) 与 [播放策略](docs/architecture/playback-strategy.md) |
| **biliLive-tools** | <https://github.com/renmu123/biliLive-tools> | 平台**画质/码率档位**的整理：虎牙 `iBitRate` 档位表与 `ratio` 参数、斗鱼 `rate` 档位表、B站 `qn` 表与 HDR 表达方式 | 只参考档位命名与参数含义，**自行实现**（`src/StreamPilot.Parsers`）；未复用其代码 |

> 上述项目各自适用其自身的开源许可；StreamPilot 的分发物中只包含 `mpegts.js` 与 `hls.js` 两个 Apache-2.0 库（版本与 SHA256 登记于 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)）。

## 10. 许可

本项目源码采用 MIT 许可（见 `LICENSE`）。随包分发的 `mpegts.js` 与 `hls.js` 为 Apache-2.0，其许可文本见 `Web\*-LICENSE.txt`，版权与版本见 `THIRD-PARTY-NOTICES.md`。

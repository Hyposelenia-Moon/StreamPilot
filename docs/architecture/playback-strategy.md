# 播放策略（追帧 / 候选选择 / 断流恢复）

本文件说明低延迟播放的完整策略。**唯一实现**在 `Web/player-core.js`（纯函数，被 `node --test tests/web` 覆盖），
`Web/player.html` 只做编排与 DOM 操作，不重复定义任何阈值。

参考来源：参考项目 `MultiLive-Windows-v10.4.16-diag` 的 `Web/player.html`（Apache-2.0 前端库 + 其参数经验值），
本项目重写了实现并修正了其五处缺陷（见第 6 节）。

> **候选探测：以参考播放页为准，但只保留"有收益"的那一半。**
> 早期版本沿用参考实现"先并行探测所有候选、**按耗时排序**、再连接最快的一条"：直播地址是长连接，
> 探测请求（即使拿到 200 就 `abort()`）与紧随其后的 mpegts/hls.js 连接争用同一地址，
> 上游会把第 2 条连接切断，于是"探测成功、播放秒断、反复起播"。
> 现在**恢复探测、但去掉按耗时重排队**（见第 4 节）：探测只用于丢弃返回 4xx/5xx 的线路并留诊断记录，
> 队列顺序仍以宿主（平台优先级）为准，因此不会因为"探测到的最快 CDN"把画面换到更差的线路上。

---

## 1. 模式与档位

| 模式 | 说明 | 首帧超时 | 极限追帧目标 |
|------|------|----------|--------------|
| `stable` | 稳定缓冲，抗抖动优先（**默认**） | 8000 ms | — |
| `extreme` | 极限追帧，低延迟优先（播放页「缓冲」下拉选「极限追帧」后启用） | 6000 ms | 150 / 200 / 250 ms（默认 250） |

模式来自 `StreamPilotOptions.Playback.Mode`，由播放页「缓冲」下拉切换并经 `playback-mode` 消息写回宿主持久化；
目标延迟档位在「设置 → 播放 → 极限追帧目标延迟」里调整（`SettingsViewModel.ExtremeTargetOptions`）。
`PlayAsync` 不再硬编码 extreme。
旧配置（`schemaVersion = 1`）里的 `mode=Extreme` 是当年没有入口时的旧默认值，加载时会迁移为 `stable`（`JsonOptionsStore.Migrate`）。

档位归一化：仅 `150`/`200`/`250` 视为有效毫秒值，其他值一律回落为 **250 ms**
（`normalizeExtremeTargetSeconds`，由单测覆盖）。

### 1.1 不做自动回落

页面**不会**因为抖动把档位改成别的值（早期版本的 `applyStallFallback` 已删除）。

原因：档位入口只有「设置 → 播放」一处，用户看到的档位必须就是真正生效的档位；偷偷把 250 改成"稳定档"
会让"我明明选了 250"变成不可解释的现象。抖动时该做的是按本模式的阈值尽快重连（见第 5 节），
而不是替用户改设置——参考播放页同样只做重连、不改档位。

## 2. mpegts.js 配置（HTTP-FLV 优先链路）

`buildMpegtsConfig(extreme, targetSeconds, autoChase)`（`autoChase` 恒为 `true`：v0.2.3 起「停止追帧」开关已随按钮一并移除，追帧始终开启）：

| 选项 | 极限追帧（150/200/250 ms） | 稳定模式 |
|------|---------------------------|----------|
| `enableWorker` | `true` | `true` |
| `enableStashBuffer` | `false` | `true` |
| `stashInitialSize` | `64`（字节，等价于关闭 stash） | `98304`（96 KiB） |
| `lazyLoad` / `deferLoadAfterSourceOpen` | `false` / `false` | 同左 |
| `autoCleanupSourceBuffer` | `true` | `true` |
| `liveBufferLatencyChasing` | `true` | `true` |
| `liveBufferLatencyMaxLatency` | `max(0.35, target + 0.27)` → 0.42 / 0.47 / 0.52 | `1.25` |
| `liveBufferLatencyMinRemain` | `target` → 0.15 / 0.20 / 0.25 | `0.32` |
| `liveSync` | `true` | `false` |
| `liveSyncMaxLatency` | `max(0.22, target + 0.14)` → 0.29 / 0.34 / 0.39 | `1.2`（稳定模式下无效） |
| `liveSyncTargetLatency` | `target` | `0.8`（稳定模式下无效） |
| `liveSyncPlaybackRate` | `1.08` | `1` |
| `fixAudioTimestampGap` | `true` | `true` |

追帧由 mpegts.js 内部完成：
- **延迟追帧器**：在 MSE `updateend` 时，若 `bufferEnd - currentTime > liveBufferLatencyMaxLatency`，硬跳到 `bufferEnd - liveBufferLatencyMinRemain`；
- **延迟同步器**：在 `timeupdate` 时，若延迟 > `liveSyncMaxLatency`，把 `playbackRate` 提升到 `min(2, max(1, liveSyncPlaybackRate))`；延迟降到 ≤ `liveSyncTargetLatency` 时恢复为 `1`（中间频段不干预）。

> **库内追帧器的两个前提**（决定了"缓冲涨到 80 秒也不追"）：
> ① `_chaseLiveLatency` 带 `liveBufferLatencyChasingOnPaused || !paused` 判断且该配置默认关闭，
> 元素被暂停时它完全不工作；
> ② 库内的下载节流（`notifyBufferedPositionChanged`）只对 `!isLive` 生效，直播流不会被节流，
> 因此"元素不消费"会让缓冲按网络速率一直涨。两者都由播放页的缓冲失控保护兜底（见 5.2）。

同时设置 `video.muted = true` 与 `preservesPitch = true`（保持音调，避免倍速追帧变声）。

## 3. hls.js 配置（HLS 兜底链路）

`buildHlsConfig(extreme)`：

| 选项 | 极限追帧 | 稳定 |
|------|----------|------|
| `enableWorker` | `true` | `true` |
| `lowLatencyMode` | `false`（直播播放列表为普通切片，不启用 LL-HLS） | `false` |
| `backBufferLength` | `30` | `30` |
| `maxBufferLength` | `6` | `12` |
| `liveSyncDurationCount` | `1` | `2` |
| `liveMaxLatencyDurationCount` | `4` | `8` |
| `maxLiveSyncPlaybackRate` | `1.5` | `1.5` |

**注意**：HLS 链路**不区分** 150/200/250 三档（与参考实现保持一致）。

## 4. 候选选择与队列

1. 规范化候选：丢弃空 URL，按 `sourceIndex:url` 去重，补默认 `format=flv`、`codec=avc`。
2. **宿主先排序再截断**（`CandidateOrdering.Prioritize`，只保留 `MaxCandidatesPerRoom = 8` 条）：
   平台返回顺序是"协议 → 容器 → 编码 → CDN"的遍历顺序，既不代表画质，也不保证同档位先给兼容性最好的编码。
   排序键依次为「画质降序 → FLV/HLS-TS/HLS-fMP4/RTMP → AVC/HEVC → 平台顺序」：
   - 画质权重里 **HDR 高于 4K SDR**（B站直播的 HDR 就是"原画"，qn=125），
     否则 HDR 房间的 HEVC 候选会被同房间的 SDR 副本挤掉，永远播不到 HDR；
   - 同画质同容器内 AVC 优先：WebView2 未装 HEVC 扩展时 HEVC 只能交给 mpv；
     但 HEVC 只要画质更高（HDR/杜比）依然排最前，**排序不等于过滤**。
3. 过滤：HLS 家族需 `hls.js` 可用，其余需 `mpegts.js` 可用；`codec=hevc` 需系统支持 HEVC MSE。
4. **候选多于一条时并行探测**（`PROBE_TIMEOUT_MS = 3000`，单条探测的硬超时）：
   `GET` + `cache:'no-store'` + `credentials:'omit'`，**拿到响应头立刻 `abort()`、绝不读响应体**
   （读 body 等于把整条直播流拉下来，探测就变成一次真实下载），超时即 abort 并按"结果不确定"处理。
   - **启动只等 `PROBE_START_DEADLINE_MS = 600`**：探测只用于排序，不该决定首帧何时出现。
     到点后中止仍在飞的探测（它们会占着浏览器的同源连接名额），未完成的候选按 `inconclusive`
     排在已知可用之后。此前 `Promise.all` 会等满最慢一条的 3 秒，实测 B站 8 条候选时每次起播
     固定多等 3.0 秒（日志 `正在并行检查…` → 首帧前 3 s）；
   - 到点被中止的探测仍会向宿主补报 `probe-result`（`errorName='probe-deadline'`），便于排查慢线路。
   - 只有一条候选时**不探测**：探测只用于比较与筛除，单候选零收益却会多占一条上游连接
     （参考播放页自己也补了这条：`docs/parsers/douyu.md`）；
   - 结果分类：`good`（2xx）/ `rejected`（4xx/5xx，直接丢弃）/ `inconclusive`（超时、CORS、其他）；
   - **不按耗时重排队**：走本地中继时该耗时主要由回环与中继转发决定，与实际 CDN 质量无关；
     队列=「已知可用（组内保持宿主顺序）」+「结果不确定（组内保持宿主顺序）」，
     因此第一条永远与"不探测"时相同，探测只可能把后面的死线路提前移出队列；
   - 页面上报 `checking`、每条候选的 `probe-result` / `probe-rejected`，最后上报一次 `candidate-queue`；
   - 连不上时由 `switchToNextCandidate` 按同一顺序切下一条（每条候选最多重连 2 次）。
5. 队列为空 → 请求宿主重新解析（`refresh-needed`，每个会话只请求一次）。以下两条路径都会走它：
   - 全部候选被探测判为 `rejected`（404/410，地址已失效）；
   - `buildPlaybackPlan` 过滤后**零候选**（典型是平台签名已过期，宿主侧候选全被有效期校验剔除）。
   宿主的 `HandleRefreshNeeded` 受 `MaxAutomaticReResolves = 2` 约束，且重新解析会**绕过解析缓存**
   （见 §13.1），不会再把同一批失效地址原样发回来。

宿主侧对这条请求加了**上限**：`ShellViewModel.MaxAutomaticReResolves = 2`。
原因是"重新解析 → 重新下发 play"会开新会话、新会话的"只请求一次"计数被重置，
于是"只有 1 条候选且地址已过期"的房间（实测斗鱼部分房间）会无限循环重连。
达到上限后停止自动重试并给出可操作提示；计数在真的出画后清零，用户手动点
「开始播放」/「解析房间」/载入预设/换档位时也清零。

## 4.1 自动播放（首帧无需点击）

宿主 WebView2 以 `--autoplay-policy=no-user-gesture-required` 启动
（`WebPlayerHost.AutoplayBrowserArgument`）：Chromium 默认策略是
`document-user-activation-required`，没有用户手势时 `video.play()` 会抛 `NotAllowedError`，
表现成"必须先点一下画面才开始播放"。

播放页另有兜底：`playWithAutoplayFallback` 先按当前音量 `play()`，若被
`NotAllowedError` 拦住则改为**静音起播**，起播后立刻用 `applyVolume(state.lastVolume, false)`
恢复用户音量（用户看到的音量不变，只是绕开内核策略）。两条路径都失败时才回到
"点一下画面"的交互（`state.autoplayBlocked`）。

## 5. 断流重连与恢复

| 项 | 取值 |
|----|------|
| 每候选最多重连次数 | `2` |
| 重连退避 | 第 1 次 `250 ms`、第 2 次 `1000 ms`，之后切换候选 |
| 重连计数重置 | 候选存活 ≥ `60000 ms` 后归零 |
| 卡顿判定 | 播放进度（`currentTime` 前进 > 15 ms）停止 |
| 卡顿阈值（极限档，饥饿 / 硬卡） | `4000 ms` / `6500 ms` |
| 卡顿阈值（稳定档，饥饿 / 硬卡） | `6000 ms` / `9000 ms` |
| 手动暂停期间 | 不判卡顿（`isPlaybackStalled` 只看页面记录的 `pausedByUser`，**不**看 `video.paused`） |
| 刚点「继续播放」后 | `RESUME_GRACE_MS = 4000 ms` 内不判卡顿（播放器正在重建缓冲） |
| 缓冲失控（缓冲 > 8 s 且画面停滞 ≥ 2 s） | 先恢复播放 / 追帧，自行处置 2 次无效才重连（见 5.2） |
| 遥测上报间隔 | ≥ `10000 ms` |
| 遥测/卡顿轮询间隔 | `500 ms` |

触发恢复的事件：`video` 错误、mpegts `LOADING_COMPLETE`（仅当画面确实停滞或缓冲已空）、
mpegts `MEDIA_MSE_ERROR`、卡顿超阈值、首帧超时（未开始播放 → 直接切换候选）。
事件处理带**播放器身份校验**（`isCurrentPlayer`）：旧播放器销毁后仍会送达的事件不再作用于新会话，
否则会出现"刚连上就被判定断流、然后立刻重连"的固定间隔卡顿。

> **`LOADING_COMPLETE` 不再无条件重连**：直播流没有"下载结束"，取满缓冲后 mpegts.js 也会报一次
> `LOADING_COMPLETE`，无条件重连等于固定间隔地重建连接，画面必然一顿一顿。
> `shouldRecoverAfterLoadingComplete(run, now, starved)` 只在"缓冲已空 或 画面停滞超过本模式阈值"时返回 true；
> 用户暂停期间的 `LOADING_COMPLETE` 直接忽略。

**HEVC 终止路径**：当 MSE 报错且错误描述匹配 `hvc1|hev1` + `unsupported|不受支持` 时，
页面停止播放、清空当前流地址，并给出明确提示（“请点击 mpv 播放”），同时上报 `error`。
本程序不在 Web 端做软解转码（见 [ADR 0005](../adr/0005-bridge-and-packaging.md)）。

## 5.1 暂停与继续（不销毁会话）

播放页底部的「暂停播放」只是 `video.pause()`：**不销毁播放器、不释放中继、不清空地址**，
因此继续播放时不需要重新解析或重新探测。`paused: false` 时若本地缓冲已超过
`PLAY_RESTORE_TOLERANCE_SECONDS = 3` 秒，先追帧到缓冲末端再 `play()`，避免落后越积越多。
暂停/继续/停止的入口只有播放页底部一处（宿主只通过 `pause` 消息同步状态）。

「停止播放」是另一回事：销毁播放器、清空地址、画面回到空态，并回 `request-stop` 让宿主
调用 `IPlaybackCoordinator.StopActive()` 释放新旧两轮中继（中继由宿主注册，页面无权释放）。

## 5.2 缓冲失控保护（画面停住 + 缓冲一直涨）

**真机证据**（B站 814，发布版连续播放）：正常样本 `bufferedAheadMs` 179–473 ms、`secondsSinceProgress = 0`；
坏状态连续 4 条遥测为 `81896 → 91859 → 101992 → 111891` ms，`secondsSinceProgress` 同步为 `82 → 92 → 102 → 112`，
`droppedVideoFrames` 冻结在 1198，`reconnects` **恒为 0**。
注意 `bufferedAheadMs / 1000 ≈ secondsSinceProgress`：数据一直按实时速率到达（每秒约 1 秒媒体），
而播放位置一动不动。

**根因**：不是 mpegts.js 停止消费，也不只是探测量取错，而是"元素自己没有前进"，
并且**两道保险都以元素自身的 `paused` 为门闩**：

1. 旧 `isPlaybackStalled` 第一条就是 `if (video.paused) return false`：元素被非用户原因暂停
   （播放器重建时 `destroyPlayer` 先 `pause()`、内核暂停元素）后，卡顿重连分支永久失效——
   遥测仍在跑，所以 `reconnects` 一直是 0，看起来"什么都没触发"；
2. mpegts.js 的延迟追帧器 `_chaseLiveLatency` 只在 `_onMSEUpdateEnd` 触发，
   且条件里带 `liveBufferLatencyChasingOnPaused || !paused`；该配置默认为 `false`，
   本项目也未开启，因此元素暂停时它**完全不追帧**；
3. mpegts.js 的下载节流（`notifyBufferedPositionChanged`）对直播流不生效
   （实现里是 `!this._config.isLive && ...`），于是"元素不消费 + 不追帧"下缓冲按网络速率无上限增长；
4. 页面自己的追帧只在用户切到「极限追帧」/ 继续播放时触发，**没有任何按缓冲大小自动触发的路径**——
   这就是"缓冲到 80 秒反而不再追"的原因。

**处置**（`core.isBufferRunaway` / `core.decideBufferRunawayAction`，纯函数、被单测覆盖）：

| 常量 | 取值 | 理由 |
|------|------|------|
| `BUFFER_RUNAWAY_AHEAD_SECONDS` | `8` s | 极限档目标 0.25 s 的 32 倍、稳定档追帧上限 1.25 s 的 6 倍以上，正常播放不可能停在这里；整段丢弃的代价只有几百毫秒重复画面 |
| `BUFFER_RUNAWAY_SILENCE_MS` | `2000` ms | 健康会话的停滞以毫秒计 |
| `BUFFER_RUNAWAY_CHASE_GRACE_MS` | `4000` ms | seek 在大缓冲上需要时间生效，宽限期内不重复处置也不升级 |
| `BUFFER_RUNAWAY_MAX_CHASE_ATTEMPTS` | `2` | 追帧两次仍无效说明不是"落后"而是坏流，交给重连 / 切候选 |

动作顺序：`NONE`（未失控 / 用户暂停）→ `RESUME`（元素被非用户原因暂停：先 `play()`）→
`CHASE`（seek 到缓冲末端前 `CHASE_KEEP_DEFAULT_SECONDS = 0.08` 秒）→ `RECONNECT`（既有重连退避，用尽后切候选）。

**真机可验证判据**：

1. 坏状态复现时，新日志里应出现 `缓冲失控：N 秒缓冲未被消耗（画面停滞 M 秒），已追帧到直播边缘（第 1 次，追帧成功）`；
2. 追帧有效：下一条 `播放遥测` 的 `bufferedAheadMs` 回到 1 s 以内且 `secondsSinceProgress` 归零；
3. 追帧无效：8–12 秒内出现 `缓冲失控：N 秒缓冲未被消耗且追帧无效，按断流处理`
   与 `warning` 消息 `缓冲失控且追帧无效，改为重连`，且随后 `播放遥测` 的 `reconnects` 开始增长（不再是 0）；
4. 遥测新增 `pausedByUser` / `elementPaused`：若坏状态下 `paused = true` 而 `pausedByUser = false`，
   即确认"元素被非用户原因暂停"这一根因；`runawayChaseAttempts` 用于确认保护确实介入过。

## 6. 相对参考实现修正的缺陷

1. **Private Network Access 响应头**：参考项目的 `mpv-bridge.ps1` 把
   `Access-Control-Allow-Private-Network: true` 无条件加到所有响应，规范要求它只出现在预检响应上。
   本项目只在 `OPTIONS` 预检时返回（`BridgeHost.ApplyCorsHeaders`）。
2. **mpegts.js 延迟追帧器的绝对时间戳判断**：上游 1.8.2 在追帧器里先判断
   `bufferedEnd > liveBufferLatencyMaxLatency`（长直播下恒为真）再判断差值。
   本项目保留上游库行为（不改动第三方库），但把阈值设为由目标延迟推导的相对值，
   避免该判断对结果产生影响（`liveBufferLatencyMaxLatency = target + 0.27`）。
3. **外部 PowerShell 桥接**：改为内嵌 `HttpListener` 服务，消除隐藏进程、脚本执行策略与
   3 秒等待窗口问题（`build/` 与 `tools/` 中不再需要任何脚本依赖）。
4. **候选探测按耗时重排队**：参考实现先探测再按耗时排序；本项目保留探测、去掉重排队
   （原因见文首与第 4 节），队列顺序仍以宿主为准。
5. **`LOADING_COMPLETE` 无条件重连**：参考实现把它当断流；本项目只在画面真的停住或缓冲为空时重连。
6. **单候选也探测**：参考实现早期版本对单候选同样探测；本项目跳过（其自身修复版亦如此）。
7. **"元素暂停"吃掉恢复分支**：旧的 `isPlaybackStalled` 用 `video.paused` 当"用户暂停"的门闩，
   元素被非用户原因暂停后卡顿重连永久失效（真机表现为"缓冲 82→112 秒、`reconnects` 恒为 0"）；
   现在只认页面记录的 `pausedByUser`，并新增缓冲失控保护（见 5.2）。

## 7. 测试覆盖

`node --test tests/web/player-core.test.js` 覆盖：
档位归一化、HLS 候选识别、候选规范化与去重、三档 mpegts 参数、hls.js 参数、重连退避序列、
分模式卡顿阈值（含"用户暂停不判卡顿"与恢复宽限期）、缓冲失控阈值与处置顺序（含宽限期与用尽后交回重连）、
探测开关、`LOADING_COMPLETE` 重连条件、手动追帧夹取、进度判定与缓冲计算、播放计划过滤、错误归类、首帧超时选择、
画质下拉规范化（档位键回落与码率标签）、消息契约（含 `log`、页面侧请求消息与 `request-stop`）、
**状态行延迟分段（`formatLatencySegment` / `isValidLatencyMs` 的合法值 / `null` / `NaN` / 负数）**、
**缓冲模式（`shouldAutoChase` 默认开启、`modeLabel` 的稳定缓冲 / 极限追帧 250/150 ms 形态、
极限/稳定两模式对 mpegts.js / hls.js 配置的影响）**、
**状态行整条文案（`formatPlaybackStatusText` 的四种逐字形态，以及旧的 `· 追帧中` / `已停止追帧` 形态不再出现）**、
**引擎配置适配层（能力表判定、注入引擎替身后的热改 / 受控重建、成功-失败的上报差异、不重复重建、
快照与恢复保留的必要状态、页面静态断言"不得再出现裸的引擎 configure 调用"）**、播放页静态结构（顶部提示已彻底移除、
播放控制三键顺序为 开始 → 暂停/继续 → 停止、参数层是「线路 + 缓冲」下拉，无追帧按钮）。

新增覆盖（2026-10）：**探测启动期限**（`PROBE_START_DEADLINE_MS` 必须短于 `PROBE_TIMEOUT_MS`，页面必须使用并把超时标成
`probe-deadline`）、**hls.js 媒体错误分类**（`isHlsCodecErrorDetail` 只认编解码器缺失，其余先尝试恢复）。

`dotnet run --project tests/StreamPilot.Tests` 里的 `RelaySafetyTests` 覆盖中继侧：
目的地址白名单（协议 / 回环 / 私网 / 链路本地 / 未指定 / `localhost` 与整数形式回环字面量）、
重定向**逐跳**校验（被拒目标不收到任何请求，公网跳转链正常跟随）、等响应头超时与播放列表正文超时（判定 + `Warn` 日志 + 504 语义）、
以及"长连接不被这两条超时误杀"（替身在超时窗口之后继续产出数据，断言仍能读完）。
`BridgeTests` 另覆盖中继注册表的空闲淘汰与 `Touch` 续命（长连接不会在第 10 分钟被清掉）；
`OptionsStoreTests` 覆盖 v1 → v2 的播放模式迁移。

## 8. 画质档位切换

- 宿主下发的 `play` 消息里带 `qualities[]` 与 `selectedQualityKey`，页面渲染画面下方（footer）的画质下拉；
  **始终显示**：只有一档时显示当前档位并禁用切换（过去会整个隐藏，用户会以为"没有画质选项"）；
- 用户改档后页面回 `quality` 消息，**宿主按新档位重新解析并按同一房间重发 `play`**
  ——因为平台地址与档位绑定（B站 qn、斗鱼 rate 必须重新请求接口；虎牙可在签名后追加 `ratio`）；
- 换档是**用户主动替换当前播放**：解析阶段不播放，解析成功后恰好下发一次计划、一条 `play` 消息，
  且不受「解析成功后自动播放」开关约束（关掉开关后换档必须真的切流，而不是只解析）；
  自动重解析（`refresh-needed`）同理——解析与启动各管一段，一轮只做一次播放决定；
- 切换期间旧会话号失效，页面会丢弃旧会话的遥测与错误消息，避免"切换瞬间的失败"污染状态；
- 切档时页面先提示「正在切换到新线路…」，宿主的**上一轮中继会保留到新会话首帧到达**才回收
  （`IPlaybackCoordinator.ReleasePreviousRelays` 由宿主在收到「播放已开始」时调用），
  否则旧地址被立刻释放会让画面直接黑屏；
- 档位名与码率由解析层提供（见 [ADR 0003](../adr/0003-parser-contract.md) 第 6.1 节），
  页面不自行编造档位名；显示时若档位名里已含码率（"蓝光4M"）就不再叠加，避免"蓝光4M · 4000 kbps"这种不一致。

## 9. 极限追帧目标档位（在「设置 → 播放」调整）

- 档位入口在「设置 → 播放 → 极限追帧目标延迟」（150 / 200 / 250 ms，默认 250），
  只影响「极限追帧」模式；稳定缓冲不使用它。播放页「缓冲」下拉只负责切换模式，
  不再提供档位选择（档位移到设置，避免与模式下拉挤在同一行）。
- **档位语义是"改配置"，不是"换地址"**：改的是目标延迟与由此推导的阈值，
  候选地址、候选队列、会话号与中继都不变（换地址走 `quality` → 宿主重新解析 → 重发 `play`，见第 8 节）；
- 宿主在下次 `play` 时通过 `extremeTargetMs` 字段下发档位；没有活动会话时只保存，下次播放生效；
- 状态文案只在画面下方的状态行显示（页面顶部不再有提示行）：`modeLabel` 同时接受
  `extremeTargetMs` 与 `extremeTargetSeconds`，避免字段缺失时静默回落成默认值
  （曾表现为"选了 250 仍显示 200"）。

### 9.1 引擎配置适配层（`core.applyEngineConfig`）

**根因（SP-01）**：过去页面把「切换模式 / 换档位」实现成裸调用引擎实例上的 `configure(config)`，
而**两家引擎的实例上都没有这个方法**：

| 库 | 库内事实 | 结论 |
|----|----------|------|
| mpegts.js 1.8.2（`Web/mpegts.js`） | 全文 0 次 `configure`。追帧器 `LiveLatencyChaser` 持有 `_config` 引用并**在运行期**读 `liveBufferLatencyChasing` / `liveBufferLatencyMaxLatency` / `liveBufferLatencyMinRemain`，LiveSync 同样运行期读 `liveSyncMaxLatency` / `liveSyncPlaybackRate`；但这两者**只在构造时按 `_config` 决定是否创建**，`enableStashBuffer` / `stashInitialSize` 等 MSE 侧选项也只在创建时生效 | 改配置只覆盖"关闭"这一半，"从关闭改回开启"无效 → **必须受控重建** |
| hls.js 1.6.16（`Web/hls.js`） | 全文 1 个 `configure`，且只属于 Transmuxer（worker 指令 `cmd:"configure"`）；Hls 实例原型上没有该方法。构造里 `this.config = mergeConfig(defaults, user)`，各控制器（含 LatencyController）保存的是**同一个对象引用**，运行期逐次读取 `maxLiveSyncPlaybackRate` / `liveMaxLatencyDurationCount` / `maxBufferLength` 等 | 改 `hls.config.*` 会真实生效，可立刻读回校验 → **热改** |

因此适配层按能力表决策，两条路径都必须**确认生效**：

| 动作 | 触发条件 | 做法 |
|------|----------|------|
| `hot-apply` | `ENGINE_CAPABILITIES[kind].hotConfig === true` 且运行中的实例有可写配置对象 | 逐键写入后**读回逐键比较**（`verifyConfigApplied`）；值不符或写入抛异常 → 降级重建 |
| `rebuild` | 引擎不支持热改、或热改未生效、或运行中的实例缺配置对象 | 销毁旧实例 → 按新配置创建 → 重新挂载 → 恢复快照 |

- `core.buildEngineConfig(run, candidate)` 是**唯一**的配置推导入口（按候选格式选 mpegts / hls 配置），
  页面的薄调用层不再自己挑函数（挑错了就是"改了但没生效"）；
- `core.snapshotRuntimeState(run, session, media)` / `core.restoreRuntimeState(...)`：重建跨越"销毁 / 创建"
  保留 `currentCandidate`、`queue`、`sessionId`、`pausedByUser`、`statusPhase`、音量、追帧档位、模式与播放位置；
  重建**不**清 `playbackStarted` / `everPlayed`，因此不会重复触发首帧逻辑、不会重复上报、状态行也不会闪回；
- 只有 `{ok: true}` 才允许调用方 `setStatus` 与上报成功；失败一律上报 `warning` 并保持原状态
  （调用方回滚 `run.mode` / `run.extreme`）。
- 页面只调用 `core.applyEngineConfigWithBackoff(...)`（内部仍是 `core.applyEngineConfig` 的判定）：
  受控重建前先断开旧连接、再做一次**有界退避**，最后才按新配置创建。
  退避复用重连退避序列的第一档（`RECONNECT_DELAYS_MS[REBUILD_BACKOFF_STEP] = 250 ms`，
  上限 `MAX_REBUILD_BACKOFF_MS = 1000 ms`）：上游对同一条签名地址的并发连接有限流，
  旧连接刚断就建新连接会撞上还没回收的那一条，表现为首帧偶发变慢。
  退避只在**确实需要重建**时执行一次（热改路径、没有重建回调时都不等待）；退避与重建窗口内
  不改状态行阶段、不产生任何上报，因此状态行不会闪回「播放中」、也不会重复上报，
  重建失败仍按失败上报（见 [ADR 0007](../adr/0007-relay-attack-surface-and-timeouts.md)）。

## 10. 缓冲模式下拉（「稳定缓冲 / 极限追帧」）与暂停播放的区别

播放页底部参数层有一个「缓冲」下拉（`#modeSelect`），选项固定为**稳定缓冲** / **极限追帧**。
它切换的是"缓冲策略"——稳定缓冲允许较大缓冲、抗抖动优先；极限追帧把延迟压到目标档位、
缓冲超过阈值就硬跳。切换走第 9.1 节的适配层（`applyPlayerTargetConfig`），并回一条
`playback-mode` 消息给宿主持久化（下次播放沿用）。

| 维度 | 切换缓冲模式 | 暂停播放 |
|------|-------------|----------|
| 接口 | 页面本地改配置（`run.mode` / `run.extreme`），并回 `playback-mode`（带 `mode`） | 页面回 `toggle-pause`，宿主回下发 `pause` |
| 媒体元素 | 继续播放（**不调用** `video.pause()`）；切到极限追帧后额外追一次帧到直播边缘 | `video.pause()`，画面完全停住 |
| 播放器 | 不销毁会话与地址；配置按第 9.1 节的适配层落地（hls.js 热改、mpegts.js 受控重建，重建保留全部会话状态） | 不销毁 |
| 地址 / 中继 | 不释放 | 不释放 |
| 恢复方式 | 把下拉切回另一项 | 「继续播放」 |

v0.2.3 起「停止追帧」开关随旧按钮一并移除，`core.shouldAutoChase(run)` 恒为 `true`
（自动追帧始终开启）；`run.autoChaseEnabled` 字段仍保留以兼容旧会话与旧调用点。

缓冲失控保护（第 5.2 节）与模式无关：它是"画面真的停住且缓冲涨到 8 s 以上时的兜底重连"，
切换模式不会关闭它。

### 10.1 底部控制条的抗重叠约束

「缓冲」下拉是固定两选项的选择器（稳定缓冲 / 极限追帧，均 4 字），不存在文案切换导致的宽度跳变；
底部控制条（`#controls` + `#statusLine`）仍有三条硬约束（由 `tests/web/player-core.test.js` 静态断言锁定）：

- **#controls 允许换行**：`display: flex` + `flex-wrap: wrap` + `row-gap: 8px`，宽度不够时换行而不是压缩重叠；
- **右下角控件独占行尾**：`#fsBtn` 用 `margin-left: auto` 靠右，换行后独自占一行，不与左侧按钮争同一行空间；
- **#statusLine 只在自己这一行被裁掉**：`white-space: nowrap` + `overflow: hidden` + `text-overflow: ellipsis` + `max-width: 100%`，
  它是 footer 列布局里的独立一行，变长只会出现省略号，不会顶掉或盖住控制条。

#### 10.1.1 实测口径（无头 Edge，`node tests/web/layout-probe/run-probe.js`）

**测量前提**：探针测量前会把 `#qualitySelect` 填成真实运行态的选项
（页面运行时 `renderQualities` 必然会填），下拉宽度由**最宽的选项**决定：
各平台真实档位名，最宽形态为 `1080P 原画（HDR 高帧率）`（受 `max-width: 220px` 收敛）。
「缓冲」下拉是静态两选项，无需填充。

**空下拉的结论不可用**：下拉为空时控件比真实运行态窄一截，早先"860px 起单行"就是这么测出来的偏乐观结论。
探针保留三种快照（「稳定缓冲」/「极限追帧」/ 极限追帧 + 长状态行），在 14 个宽度（1280 → 480）下逐档测量：

| 宽度（px） | 行数（最宽档位名） | 行数（普通档位名） | 「全屏」与播放控制同行 | 碰撞对 |
|------------|--------------------|--------------------|------------------------|--------|
| 1280 | 1 | 1 | 是 | 无 |
| 1120 | 1 | 1 | 是 | 无 |
| 1024 | 2 | 1 | 最宽口径否 / 普通口径是 | 无 |
| 960 – 620 | 2 | 2 | 否（「全屏」换到第二行并独占行尾） | 无 |
| 560 | 3 | 2 | 否 | 无 |
| 480 | 3 | 3 | 否 | 无 |

- **0 重叠**：14 个宽度 × 3 快照共 **42 次测量，碰撞对恒为空**（`collisions=none`）；
- **单行阈值是区间而不是定值**：普通档位名（最宽 `1080P 高码率`）时 **1024 px 起单行**，
  带 HDR / 高帧率 后缀的最宽档位名（下拉顶到 `max-width: 220px`）时后移到 **1120 px**；
- 普通档位名一列是把 `probe.js` 的 `QUALITY_OPTION_LABELS` 里两条带后缀的选项去掉后重跑的结果；
  行数、是否同行、碰撞对直接由探针输出开头的 `lines=` / `fsSameLineAsPlay=` / `collisions=` 给出。

## 11. 状态行显示实际延迟

画面下方状态行（`#statusLine`）在播放状态下显示形如 `播放中 · 318 ms（极限追帧 250 ms）`：

- 数据来源：遥测每轮（`TELEMETRY_INTERVAL_MS = 500 ms`）把 `getLocalBufferSeconds(...)` 的结果写进
  `run.lastLatencyMs`，与 `telemetry` 消息里的 `bufferedAheadMs` **同源同值**；
- 结构固定为「`播放中` + 可选 ` · N ms` + `（追帧状态）`」：**延迟分段紧跟"播放中"，括号分段永远存在且放在最后**；
- 延迟分段是纯函数 `core.formatLatencySegment(latencyMs)`：`latencyMs` 为 `null` / `NaN` / `Infinity` / 负数时
  返回 `null`，调用方**整段省略**，绝不显示占位数字（此时写作 `播放中（极限追帧 250 ms）`）；
- 刻意**不**用追帧档位（150/200/250）冒充延迟：那是目标值，不是实际值；
- 括号分段取值由 `core.chaseStatusLabel(run)` 给出（常量 `core.CHASE_STATUS_LABELS`）：正在追帧时是当前模式与
  目标档位（`modeLabel`，如 `极限追帧 250 ms`），停止追帧时是固定的 `未开启追帧`
  （此时不显示模式与档位）——两种状态都有标记，只标注一侧时"追帧到底开没开"无法确认。

### 11.1 状态行优先级：遥测只能改数值，不能改操作态

状态行是**两级**判定，两级都是纯函数（都被 `tests/web/player-core.test.js` 逐字覆盖）：

| 级别 | 判定函数 | 决定什么 |
|------|----------|----------|
| 1（最高） | `core.resolveStatusLine(hostStatus, playbackText, now)` | 宿主消息（`host-status`）覆盖显示，按级别保留 6/12/20 s 后回落到 `playbackText` |
| 2 | `core.resolvePlaybackStatusText(run, session)` | 页面自己那一份文案：用户暂停 > 停止 / 重连 / 未连接 > 播放中遥测 |

合并后的完整顺序是：**宿主消息 > 用户暂停 > 重连 / 停止 / 未连接 > 播放中遥测**。
判定 2 的输入是页面会话态 `{ statusPhase, pausedByUser, reconnectAttempt }`
（阶段常量 `core.PLAYBACK_PHASES`：`idle` / `playing` / `paused` / `reconnecting` / `stopped`），输出逐字为：

| 会话态 | 文案 |
|--------|------|
| 用户暂停（`pausedByUser` 或阶段 `paused`） | `已暂停（保留当前地址）` |
| 阶段 `stopped`（或运行对象已标记 `stopped`） | `已停止播放（画面已关闭）` |
| 阶段 `reconnecting` | `重连中（第 N 次）…`（`core.formatReconnectingStatusText`） |
| 阶段 `idle` 或没有运行对象 | `未连接` |
| 阶段 `playing` | `core.formatPlaybackStatusText(run)`：`播放中 · N ms（…）` |

**为什么必须收敛到纯函数**：遥测每 500 ms 刷一次状态行，一旦让回调自己拼文案，用户点「暂停播放」后
不到 0.5 秒状态行就会被刷回 `播放中 · N ms（…）`——按钮写着「继续播放」、状态行却说在播放。
现在 `player.html` 的遥测回调只做一件事：把实测延迟写进 `run.lastLatencyMs`，
再调 `core.resolvePlaybackStatusText(run, state)` 取文案（页面侧只有 `applyPlaybackPhase` 一处改阶段），
因此暂停 / 重连 / 停止期间数值照常刷新，文案保持不变；恢复播放后自动回到 `播放中 · N ms（…）`。
宿主消息仍由第 1 级覆盖，超时后回落到的正是第 2 级给的文案（暂停时回落成暂停文案，而不是「播放中」）。

## 12. 桥接中继的稳定性与安全约束

### 12.1 超时分层（各自负责一段，互不牵连）

| 环节 | 约束 | 具名常量 / 实现 |
|------|------|------------------|
| 建立上游连接 | 10 秒 | `UpstreamConnectTimeoutSeconds`（`SocketsHttpHandler.ConnectTimeout`） |
| 等上游响应头 | 10 秒，超时回 **504** 并记 `Warn`（`operation=response-headers` / `playlist`） | `UpstreamHeaderTimeoutSeconds` + `SendUpstreamAsync` 的独立 `CancellationTokenSource` |
| 读 HLS 播放列表正文 | 15 秒，超时回 **504** 并记 `Warn`（`operation=playlist-body`） | `PlaylistDownloadTimeoutSeconds` + `TryReadPlaylistAsync` 的独立 `CancellationTokenSource` |
| FLV / TS 长连接正文 | **空闲看门狗**：连续 30 秒无新数据才断开并记 `Warn`；持续有数据就不计时 | `IdleTimeoutSeconds` + `PumpWithIdleTimeoutAsync` |

- `HttpClient.Timeout = InfiniteTimeSpan` 这个选择**保留**，但理由要说对：中继拉的是直播长连接，
  而 `HttpCompletionOption.ResponseHeadersRead` 下 `HttpClient.Timeout` **只约束到响应头为止**，
  它并不会、也不该把直播流一起取消。**体读取的边界由空闲看门狗与上面两条独立 CTS 负责**；
  （旧注释/旧文档把它写成"否则 HttpClient 会在 30 秒后连直播流一起取消"——这个理由不成立，已订正。）
- 连接池的 `PooledConnectionLifetime = InfiniteTimeSpan`：**连接池不再定时回收正在使用中的流连接**
  （曾设为 10 分钟：回收会把正在读的流一起换掉，表现为固定时长的"看着看着断一下"）；
- **中继注册的保活与单飞**：
  - 活跃时间不能只看"收到新的 HTTP 请求"——FLV 建连后几小时都没有第二次请求。中继泵在转发期间
    每 `RelayTouchIntervalSeconds = 30` 秒调用一次 `RelayRegistry.Touch`，连接活着等于注册有效；
    否则 10 分钟空闲淘汰会把还在播放的条目清掉，第一次断流重连直接 404（`unknown-relay`）。
  - **同一令牌同一时刻只保留一条上游连接**：新连接受理时先 `Cancel` 旧连接的租约
    （`TakeRelayLease`）。斗鱼等平台对同一签名地址只允许 1 条并发，旧连接若还挂在上游读数据，
    新连接会被上游在 0.2~0.4 秒内切断（`docs/parsers/douyu.md`）。

### 12.3 播放列表识别与子地址生命周期

- **按内容识别播放列表**：子地址不一定带 `.m3u8` 后缀（部分平台给 `.../playlist/abc123?token=...`）。
  中继对"200 + 非 Range + 非空正文"的响应先读 32 字节嗅探，以 `#EXTM3U`（允许 BOM/前导空白）开头
  就整篇读完并逐行改写；否则把已读字节原样转发后继续泵。只看后缀会把变体列表当二进制流，
  里面的相对切片地址不被改写，hls.js 拿到 CDN 原始 http 地址后被混合内容拦截。
- **子注册带"试用期"**（`BridgeConstants.ProvisionalRelaySeconds = 90`）：播放页探测 HLS 候选会拉一次
  播放列表（拿到响应头就 `abort()`），改写过程登记的切片子地址不会再被请求。
  这些注册若按普通空闲阈值（10 分钟）淘汰，一次起播就能堆几十条死注册；
  首个真实请求会把注册转正，之后按普通规则续命。
- **注册上限 256 条**（`MaxRelayRegistrations`）：HLS 每条候选保留两条列表 × 若干切片，
  8 条候选已经接近 100 条；旧上限 64 会在起播后不久触发淘汰，把仍在播放的注册挤掉（播放中 404）。
  满额时优先淘汰"仍是试用期且从未被请求过"的子注册。
- **切片代数按"时间 + 数量"双约束保留**：单代至少存活 `PlaylistGenerationGraceSeconds = 60` 秒，
  且每个列表最多保留 8 代。只按代数（旧实现保留 2 代）会让慢链路上下载中的切片在约 4 秒后变成 404。

### 12.2 攻击面收敛（SP-02）

- **中继只有 `GET /relay/{token}`**：注册一律走进程内的 `IPlaybackBridge.RegisterRelay`
  （由 `PlaybackCoordinator` 在宿主进程内调用）。过去还有一条 `POST /relay` 注册路由，
  它没有任何身份校验，且响应头是 `Access-Control-Allow-Origin: *`——本机任意网页都能把自己的
  回环地址注册成"中继目标"再读回数据；播放页从不使用它，因此**直接关闭该路由**（非 GET 一律 404），
  不引入令牌层（最小攻击面，不做过度设计）；
- `Access-Control-Allow-Origin` 固定为播放页的实际来源 `https://appassets.local`
  （`BridgeHost.PlayerPageOrigin`），并带 `Vary: Origin`；
  `Access-Control-Allow-Private-Network: true` **保留**，但只在预检请求声明
  `Access-Control-Request-Private-Network: true` 时返回；
- **预检只为已知路由返回 204**：`/relay/**`、`/play`、`/health` 之外的路径
  （含 `/`、旧文档里并不存在的 `/web/*`、以及 `/relayx/1` 这类前缀相似路径）一律 404。
  判定是纯函数 `BridgeHost.ResolveRoute`，离线用例直接断言；过去任意路径的 `OPTIONS`
  都会拿到 204 + CORS，等于向本机任意页面确认"这个回环端口上什么路径都有人应答"
  （见 [ADR 0007](../adr/0007-relay-attack-surface-and-timeouts.md)）；
- **目的地址白名单**（`IsAllowedUpstreamUrl`，注册与**每一次实际发送之前**都过；重定向逐跳校验后再发）：
  只允许 `http` / `https`（中继是 HTTP 客户端，`rtmp`/`rtmps` 本来就拉不了，已移除），
  拒绝回环 / 私网 / 链路本地 / 未指定地址的字面量与 `localhost` 主机名
  （含 `::ffff:10.0.0.5` 这类 IPv4-mapped IPv6、`[::1]` 这类带方括号的 IPv6、以及
  `2130706433` / `0x7f.1` 这类整数形式的回环）；CDN 都是公网域名，不受影响；
- 判定只做**字面量**，不做 DNS 解析：解析结果会变，且解析-连接之间仍可能被改写（DNS rebinding），
  凭它做安全决策只会给出虚假的保证。这里的目标是去掉"零成本访问本机与内网"。

> 边界说明：桥接**只监听 `127.0.0.1`**（`LoopbackOnlyGuard`），不是公网监听，
> 上述问题不是文件读取 / RCE，而是"本机任意页面可借中继访问内网与回环服务"。

## 13. 解析缓存与地址时效

- 解析结果按 `platform:roomId` 缓存 **60 秒**（`StreamCache.DefaultTtlSeconds`），目的是避免同一房间
  在"解析 → 播放 → 换档"之间重复请求平台接口。
- 候选有效期来自解析器：B站读签名参数 `expires`；抖音/Bigo 等把时效写在地址里的平台由
  `UrlExpiry.TryRead` 读取（`expire`/`expires`/`expires_at` 等，秒或毫秒），
  读不到按"平台未声明"处理（不做更保守的拒绝）。这样"已过期"能在下发前被过滤并触发重新解析，
  而不是先交给页面探测失败。
- 但流地址普遍带时效签名（B站 `expires`、斗鱼 `wsAuth/token`、虎牙 `wsSecret/wsTime`、抖音 `expire`），
  缓存命中意味着**拿到的是同一批旧地址**。因此以下路径显式 `bypassCache`：
  - 播放页 `refresh-needed`（候选全失效）；
  - 换画质（档位与地址绑定，必须重新签发）；
  - 用户手动重播（页面「开始播放」按钮 → 重新解析一次再下发）；
  - `PlaybackPlan.Candidates` 为空（宿主侧有效期校验把所有候选都剔除了）时的一次强制补救。
- 缓存键**不含画质键**：换档统一走 `bypassCache` + 覆盖写入，避免"同一房间多档位互相顶掉"。
- `IRoomResolver.InvalidateCache()` 保留给"平台整体失效"这类场景（例如切换账号/Cookie 后），
  日常路径不依赖它清空整个缓存。

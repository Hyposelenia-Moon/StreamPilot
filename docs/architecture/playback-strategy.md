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
| `extreme` | 极限追帧，低延迟优先 | 6000 ms | 150 / 200 / 250 ms（默认 250） |
| `stable` | 稳定缓冲，抗抖动优先 | 8000 ms | — |

档位归一化：仅 `150`/`200`/`250` 视为有效毫秒值，其他值一律回落为 **250 ms**
（`normalizeExtremeTargetSeconds`，由单测覆盖）。

### 1.1 不做自动回落

页面**不会**因为抖动把档位改成别的值（早期版本的 `applyStallFallback` 已删除）。

原因：档位入口只有画面下方一处，用户看到的档位必须就是真正生效的档位；偷偷把 250 改成"稳定档"
会让"我明明选了 250"变成不可解释的现象。抖动时该做的是按本模式的阈值尽快重连（见第 5 节），
而不是替用户改设置——参考播放页同样只做重连、不改档位。

## 2. mpegts.js 配置（HTTP-FLV 优先链路）

`buildMpegtsConfig(extreme, targetSeconds, autoChase)`（`autoChase === false` 表示用户点了「停止追帧」，见第 10 节）：

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
2. 过滤：HLS 家族需 `hls.js` 可用，其余需 `mpegts.js` 可用；`codec=hevc` 需系统支持 HEVC MSE。
3. **候选多于一条时并行探测**（与参考播放页逐条一致，`PROBE_TIMEOUT_MS = 1200`）：
   `GET` + `cache:'no-store'` + `credentials:'omit'`，**拿到响应头立刻 `abort()`、绝不读响应体**
   （读 body 等于把整条直播流拉下来，探测就变成一次真实下载），超时即 abort 并按"结果不确定"处理。
   - 只有一条候选时**不探测**：探测只用于比较与筛除，单候选零收益却会多占一条上游连接
     （参考播放页自己也补了这条：`docs/parsers/douyu.md`）；
   - 结果分类：`good`（2xx）/ `rejected`（4xx/5xx，直接丢弃）/ `inconclusive`（超时、CORS、其他）；
   - **不按耗时重排队**：走本地中继时该耗时主要由回环与中继转发决定，与实际 CDN 质量无关；
     队列=「已知可用（组内保持宿主顺序）」+「结果不确定（组内保持宿主顺序）」，
     因此第一条永远与"不探测"时相同，探测只可能把后面的死线路提前移出队列；
   - 页面上报 `checking`、每条候选的 `probe-result` / `probe-rejected`，最后上报一次 `candidate-queue`；
   - 连不上时由 `switchToNextCandidate` 按同一顺序切下一条（每条候选最多重连 2 次）。
4. 队列为空 → 请求宿主重新解析（`refresh-needed`，每个会话只请求一次）。

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
4. 页面自己的追帧只在用户点按钮 / 换档位 / 继续播放时触发，**没有任何按缓冲大小自动触发的路径**——
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
**追帧开关（`shouldAutoChase` 的默认开启与显式停止、`chaseButtonLabel` 两侧文案、
停止追帧对 mpegts.js / hls.js 配置的影响，且不改变缓冲策略）**、
**状态行整条文案（`formatPlaybackStatusText` 的四种逐字形态，以及旧的 `· 追帧中` / `已停止追帧` 形态不再出现）**、播放页静态结构（顶部提示已彻底移除、
播放控制三键顺序为 开始 → 暂停/继续 → 停止、追帧相关按钮只剩合并后的 `#chaseBtn` 一个）。

## 8. 画质档位切换

- 宿主下发的 `play` 消息里带 `qualities[]` 与 `selectedQualityKey`，页面渲染画面下方（footer）的画质下拉；
  **始终显示**：只有一档时显示当前档位并禁用切换（过去会整个隐藏，用户会以为"没有画质选项"）；
- 用户改档后页面回 `quality` 消息，**宿主按新档位重新解析并按同一房间重发 `play`**
  ——因为平台地址与档位绑定（B站 qn、斗鱼 rate 必须重新请求接口；虎牙可在签名后追加 `ratio`）；
- 切换期间旧会话号失效，页面会丢弃旧会话的遥测与错误消息，避免"切换瞬间的失败"污染状态；
- 切档时页面先提示「正在切换到新线路…」，宿主的**上一轮中继会保留到新会话首帧到达**才回收
  （`IPlaybackCoordinator.ReleasePreviousRelays` 由宿主在收到「播放已开始」时调用），
  否则旧地址被立刻释放会让画面直接黑屏；
- 档位名与码率由解析层提供（见 [ADR 0003](../adr/0003-parser-contract.md) 第 6.1 节），
  页面不自行编造档位名；显示时若档位名里已含码率（"蓝光4M"）就不再叠加，避免"蓝光4M · 4000 kbps"这种不一致。

## 9. 追帧档位热切换

- 档位入口是播放页底部「追帧档位」下拉（150 / 200 / 250 ms，默认 250），
  **播放中**改动时页面回 `target` 消息给宿主（宿主只记住该值供下次播放沿用），
  同时页面自己热改播放器配置并立即追帧（`configure(buildMpegtsConfig(...))`）；
- 宿主也可下发同名 `target` 消息要求页面切换（`docs/architecture/player-message-contract.md` 第 1.2.1 节）；
- 没有活动会话时只保存档位，下次 `play` 时生效；
- 状态文案只在画面下方的状态行显示（页面顶部不再有提示行）：`modeLabel` 同时接受
  `extremeTargetMs` 与 `extremeTargetSeconds`，避免字段缺失时静默回落成默认值
  （曾表现为"选了 250 仍显示 200"）。

## 10. 合并后的追帧开关（「追帧 / 停止追帧」）与暂停播放的区别

播放页底部只有一个与追帧开关相关的入口：**「追帧 / 停止追帧」**（`#chaseBtn`）。
按钮文案由 `core.chaseButtonLabel(state)`（`state` = `core.shouldAutoChase(run)` 的结果）给出，
它切换的是"要不要按目标延迟把画面拉回直播边缘"这一**策略**，不是"要不要继续播放"。

| 当前状态 | 按钮文案 | 点击后 |
|----------|----------|--------|
| 自动追帧进行中 | 停止追帧 | 关闭自动追帧（热改播放器配置）：库内硬跳与倍速追赶都关掉，画面以正常倍速实时推进 |
| 已停止自动追帧 | 追帧 | 开启自动追帧**并立刻追一次帧**（`chaseToLiveEdge(CHASE_KEEP_DEFAULT_SECONDS)`，跳到缓冲末端前 0.08 s） |

「追帧」保留原一次性追帧的效果：合并前它是独立按钮（只 seek 一次、不改开关），
现在它同时承担"开启策略"与"立刻拉回一次"两件事，用户不必再点两次。

| 维度 | 停止追帧 | 追帧（开启 + 追一次） | 暂停播放 |
|------|----------|----------------------|----------|
| 接口 | 无新消息类型：页面本地改配置，并回一条 `status` 说明（带 `autoChaseEnabled`） | 同左（同一条 `status`） | 页面回 `toggle-pause`，宿主回下发 `pause` |
| 媒体元素 | 继续播放（**不调用** `video.pause()`） | 继续播放 | `video.pause()`，画面完全停住 |
| 播放器 | 不销毁（`configure` 热改配置） | 不销毁 | 不销毁 |
| 地址 / 中继 | 不释放 | 不释放 | 不释放 |
| 延迟走向 | 按实时速率增长（不再被拉回目标值） | 立刻拉回缓冲末端前 0.08 s，之后继续按目标延迟拉回 | 暂停期间缓冲继续增长，继续播放时可能先追帧 |
| 恢复方式 | 再点一次按钮（文案变回「追帧」） | 再点一次按钮（文案变为「停止追帧」） | 「继续播放」 |

`core.shouldAutoChase(run)`（纯函数、被单测覆盖）是唯一判定入口：
**字段缺失视为开启**（`run.autoChaseEnabled !== false`），只有用户显式停止才算关闭，
这样旧会话与旧调用点不会因为字段缺失被误当成"已停止追帧"。

停止追帧后写入播放器的配置（`core.buildMpegtsConfig(extreme, target, false)` / `core.buildHlsConfig(extreme, false)`）：

| 链路 | 停止追帧时 | 默认（自动追帧） |
|------|------------|------------------|
| mpegts.js | `liveBufferLatencyChasing = false`、`liveSync = false`，并把 `liveBufferLatencyMaxLatency` / `liveSyncMaxLatency` 写成永不触发的值 | `true` / `extreme` 时 `true`，阈值为 0.42–0.52 s / 0.29–0.39 s |
| hls.js | `maxLiveSyncPlaybackRate = 1`、`liveMaxLatencyDurationCount` 写成不可能达到的值 | `1.5` 与 `4`（极限档）/ `8`（稳定档） |
| 两者共同 | `autoCleanupSourceBuffer` / `maxBufferLength` / `enableStashBuffer` 等**一律不变** | — |

两点边界必须记住：

1. **停止追帧不关闭缓冲失控保护**（第 5.2 节）。前者是"不主动追"，后者是"画面真的停住且缓冲涨到 8 s 以上时的兜底重连"，
   两者目的相反，不能互相牵连：停止追帧后画面仍在前进，失控保护就不会介入；
2. **停止追帧不改变目标延迟档位**：`150/200/250` 只被记住（重新开启后立即生效），下拉框不回退、不改写。

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

## 12. 桥接中继的稳定性约束

- `BridgeHost` 的中继 `HttpClient`：`Timeout = InfiniteTimeSpan`（长连接不能被整体超时取消），
  且 `PooledConnectionLifetime = InfiniteTimeSpan`，**连接池不再定时回收正在使用中的流连接**
  （曾设为 10 分钟：中继拉的是直播长连接，回收会把正在读的流一起换掉，表现为固定时长的"看着看着断一下"）；
- 断流判定只由 `PumpWithIdleTimeoutAsync` 的**空闲超时**（30 秒无新数据）负责，按"连续无数据"计时，
  不会因为流的分片间隔而误判；
- 中继注册表的活跃时间在每次请求时刷新（`RelayRegistry.TryResolve`），长时间播放的地址不会被闲置淘汰。

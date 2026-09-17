# 播放策略（追帧 / 候选选择 / 断流恢复）

本文件说明低延迟播放的完整策略。**唯一实现**在 `Web/player-core.js`（纯函数，被 `node --test tests/web` 覆盖），
`Web/player.html` 只做编排与 DOM 操作，不重复定义任何阈值。

参考来源：参考项目 `MultiLive-Windows-v10.4.16-diag` 的 `Web/player.html`（Apache-2.0 前端库 + 其参数经验值），
本项目重写了实现并修正了其五处缺陷（见第 6 节）。

> **稳定性优先的取舍**：早期版本沿用参考实现"先并行探测所有候选、按耗时排序、再连接最快的一条"。
> 实测这会让画面持续卡顿：直播地址是长连接，探测请求（即使拿到 200 就 `abort()`）与紧随其后的
> mpegts/hls.js 连接争用同一地址，上游会把第 2 条连接切断，于是"探测成功、播放秒断、反复起播"。
> 现在**完全不做候选探测**，直接按宿主给出的顺序连接，失败才切下一条（见第 4 节）。

---

## 1. 模式与档位

| 模式 | 说明 | 首帧超时 | 极限追帧目标 |
|------|------|----------|--------------|
| `extreme` | 极限追帧，低延迟优先 | 6000 ms | 150 / 200 / 250 ms（默认 250） |
| `stable` | 稳定缓冲，抗抖动优先 | 8000 ms | — |

档位归一化：仅 `150`/`200`/`250` 视为有效毫秒值，其他值一律回落为 **250 ms**
（`normalizeExtremeTargetSeconds`，由单测覆盖）。

### 1.1 自动回落（抖动保护）

极限档受物理延迟上限约束，抖动网络上会反复饥饿与丢帧；硬顶只会让画面一直卡。
`applyStallFallback(run, starved)` 在每个遥测样本（500 ms）上累计"连续饥饿"计数，
连续 `STALL_FALLBACK_SAMPLES = 6` 次（约 3 秒）后把目标延迟放宽到 `FALLBACK_TARGET_MS = 250`，
页面热改播放器配置并提示「网络抖动较多，已自动切回稳定档」；任何一次非饥饿样本都会把计数清零。

## 2. mpegts.js 配置（HTTP-FLV 优先链路）

`buildMpegtsConfig(extreme, targetSeconds)`：

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
3. **不发起任何探测请求**：队列就是宿主给的顺序（`PlaybackCoordinator` 已按平台遍历顺序排好）。
   - 原因见文首"稳定性优先的取舍"：探测与播放争用同一条直播长连接，会让画面反复起播、断流；
   - 页面只上报一次 `candidate-queue`（含 `sourceIndexes`），不再上报 `probe-result`；
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
| 卡顿阈值（饥饿：无缓冲 / `readyState<3` / 本地缓冲 < 0.15 s） | `6000 ms` |
| 卡顿阈值（非饥饿，硬卡顿） | `9000 ms` |
| 手动暂停期间 | 不判卡顿（`isPlaybackStalled` 先看 `video.paused`） |
| 刚点「继续播放」后 | `RESUME_GRACE_MS = 4000 ms` 内不判卡顿（播放器正在重建缓冲） |
| 遥测上报间隔 | ≥ `10000 ms` |
| 遥测/卡顿轮询间隔 | `500 ms` |

触发恢复的事件：`video` 错误、mpegts `LOADING_COMPLETE`（仅当画面确实停滞或缓冲已空）、
mpegts `MEDIA_MSE_ERROR`、卡顿超阈值、首帧超时（未开始播放 → 直接切换候选）。

> **`LOADING_COMPLETE` 不再无条件重连**：直播流没有"下载结束"，取满缓冲后 mpegts.js 也会报一次
> `LOADING_COMPLETE`，无条件重连等于固定间隔地重建连接，画面必然一顿一顿。
> `shouldRecoverAfterLoadingComplete(run, now, starved)` 只在"缓冲已空 或 画面停滞超过阈值"时返回 true。

**HEVC 终止路径**：当 MSE 报错且错误描述匹配 `hvc1|hev1` + `unsupported|不受支持` 时，
页面停止播放、清空当前流地址，并给出明确提示（“请点击 mpv 播放”），同时上报 `error`。
本程序不在 Web 端做软解转码（见 [ADR 0005](../adr/0005-bridge-and-packaging.md)）。

## 5.1 暂停与继续（不销毁会话）

播放页底部的「暂停播放」只是 `video.pause()`：**不销毁播放器、不释放中继、不清空地址**，
因此继续播放时不需要重新解析或重新探测。`paused: false` 时若本地缓冲已超过
`PLAY_RESTORE_TOLERANCE_SECONDS = 3` 秒，先追帧到缓冲末端再 `play()`，避免落后越积越多。
侧栏的「暂停播放 / 继续播放」按钮与播放页按钮走同一条路径（`toggle-pause` ↔ `pause`）。

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
4. **候选并行探测**：参考实现先探测再排序；本项目移除探测（原因见文首），改为按宿主顺序直连。
5. **`LOADING_COMPLETE` 无条件重连**：参考实现把它当断流；本项目只在画面真的停住或缓冲为空时重连。

## 7. 测试覆盖

`node --test tests/web/player-core.test.js` 覆盖：
档位归一化、HLS 候选识别、候选规范化与去重、三档 mpegts 参数、hls.js 参数、重连退避序列、
卡顿阈值选择（含"用户暂停不判卡顿"与恢复宽限期）、自动回落稳定档、`LOADING_COMPLETE` 重连条件、
手动追帧夹取、进度判定与缓冲计算、播放计划过滤、错误归类、首帧超时选择、
画质下拉规范化（档位键回落与码率标签）、消息契约包含播放页侧请求消息。

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
- 页面顶部提示必须显示**实际档位**：`modeLabel` 同时接受 `extremeTargetMs` 与
  `extremeTargetSeconds`，避免字段缺失时静默回落成默认值（曾表现为"选了 250 仍显示 200"）。

## 10. 桥接中继的稳定性约束

- `BridgeHost` 的中继 `HttpClient`：`Timeout = InfiniteTimeSpan`（长连接不能被整体超时取消），
  且 `PooledConnectionLifetime = InfiniteTimeSpan`，**连接池不再定时回收正在使用中的流连接**
  （曾设为 10 分钟：中继拉的是直播长连接，回收会把正在读的流一起换掉，表现为固定时长的"看着看着断一下"）；
- 断流判定只由 `PumpWithIdleTimeoutAsync` 的**空闲超时**（30 秒无新数据）负责，按"连续无数据"计时，
  不会因为流的分片间隔而误判；
- 中继注册表的活跃时间在每次请求时刷新（`RelayRegistry.TryResolve`），长时间播放的地址不会被闲置淘汰。

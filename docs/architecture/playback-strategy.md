# 播放策略（追帧 / 候选探测 / 断流恢复）

本文件说明低延迟播放的完整策略。**唯一实现**在 `Web/player-core.js`（纯函数，被 `node --test tests/web` 覆盖），
`Web/player.html` 只做编排与 DOM 操作，不重复定义任何阈值。

参考来源：参考项目 `MultiLive-Windows-v10.4.16-diag` 的 `Web/player.html`（Apache-2.0 前端库 + 其参数经验值），
本项目重写了实现并修正了其两处缺陷（见第 6 节）。

---

## 1. 模式与档位

| 模式 | 说明 | 首帧超时 | 极限追帧目标 |
|------|------|----------|--------------|
| `extreme` | 极限追帧，低延迟优先 | 6000 ms | 150 / 200 / 250 ms（默认 250） |
| `stable` | 稳定缓冲，抗抖动优先 | 8000 ms | — |

档位归一化：仅 `150`/`200`/`250` 视为有效毫秒值，其他值一律回落为 **200 ms**
（`normalizeExtremeTargetSeconds`，由单测覆盖）。

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

## 4. 候选探测与队列

1. 规范化候选：丢弃空 URL，按 `sourceIndex:url` 去重，补默认 `format=flv`、`codec=avc`。
2. 过滤：HLS 家族需 `hls.js` 可用，其余需 `mpegts.js` 可用；`codec=hevc` 需系统支持 HEVC MSE。
3. **并行探测**全部候选：`fetch(url, { mode:'cors', cache:'no-store', credentials:'omit' })`，超时 **1200 ms**。
   - `2xx` → `good`，按探测耗时升序排到队首；
   - `4xx/5xx` → `rejected`，丢弃；
   - 超时 / 网络异常 / 3xx → `inconclusive`，排在 `good` 之后保留重试机会。
4. 队列为空 → 请求宿主重新解析（`refresh-needed`，每个会话只请求一次）。

## 5. 断流重连与恢复

| 项 | 取值 |
|----|------|
| 每候选最多重连次数 | `2` |
| 重连退避 | 第 1 次 `250 ms`、第 2 次 `1000 ms`，之后切换候选 |
| 重连计数重置 | 候选存活 ≥ `60000 ms` 后归零 |
| 卡顿判定 | 播放进度（`currentTime` 前进 > 15 ms）停止 |
| 卡顿阈值（饥饿：暂停 / 无缓冲 / `readyState<3` / 本地缓冲 < 0.15 s） | 极限 `4000 ms`，稳定 `6000 ms` |
| 卡顿阈值（非饥饿，硬卡顿） | 极限 `6500 ms`，稳定 `9000 ms` |
| 遥测上报间隔 | ≥ `10000 ms` |
| 遥测/卡顿轮询间隔 | `500 ms` |

触发恢复的事件：`video` 错误、mpegts `LOADING_COMPLETE`、mpegts `MEDIA_MSE_ERROR`、卡顿超阈值、
首帧超时（未开始播放 → 直接切换候选）。

**HEVC 终止路径**：当 MSE 报错且错误描述匹配 `hvc1|hev1` + `unsupported|不受支持` 时，
页面停止播放、清空当前流地址，并给出明确提示（“请点击 mpv 播放”），同时上报 `error`。
本程序不在 Web 端做软解转码（见 [ADR 0005](../adr/0005-bridge-and-packaging.md)）。

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

## 7. 测试覆盖

`node --test tests/web/player-core.test.js` 覆盖：
档位归一化、HLS 候选识别、候选规范化与去重、三档 mpegts 参数、hls.js 参数、重连退避序列、
卡顿阈值选择、手动追帧夹取、进度判定与缓冲计算、播放计划过滤、错误归类、首帧超时选择、
画质下拉规范化（档位键回落与码率标签）。

## 8. 画质档位切换

- 宿主下发的 `play` 消息里带 `qualities[]` 与 `selectedQualityKey`，页面渲染右上角画质下拉
  （只有一项时隐藏）；
- 用户改档后页面回 `quality` 消息，**宿主按新档位重新解析并按同一房间重发 `play`**
  ——因为平台地址与档位绑定（B站 qn、斗鱼 rate 必须重新请求接口；虎牙可在签名后追加 `ratio`）；
- 切换期间旧会话号失效，页面会丢弃旧会话的遥测与错误消息，避免"切换瞬间的失败"污染状态；
- 档位名与码率由解析层提供（见 [ADR 0003](../adr/0003-parser-contract.md) 第 6.1 节），
  页面不自行编造档位名。

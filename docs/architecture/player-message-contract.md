# 播放消息契约（WebView2 宿主 ↔ 播放页）

本文件是**唯一权威**的宿主/页面消息契约。宿主实现见
`src/StreamPilot.App/Views/WebPlayerHost.cs` 与 `src/StreamPilot.App/ViewModels/ShellViewModel.cs`；
页面实现见 `Web/player.html`（纯逻辑在 `Web/player-core.js`，被 `node --test` 覆盖）。

通信方式：WebView2 的 `chrome.webview.postMessage` / `PostWebMessageAsJson`。
页面**不暴露**任何可被宿主调用的 `window` 方法（与参考项目一致，避免双向耦合）。

页面地址：`https://appassets.local/player.html`
（由 `CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", <exe>\Web, DenyCors)` 提供；
必须使用 `https` 虚拟主机而非 `file://`，否则候选探测的 `mode:'cors'` 与 Private Network Access 全部失败。）

---

## 1. 宿主 → 页面

### 1.1 `play`

```json
{
  "type": "play",
  "sessionId": 7,
  "mode": "extreme",
  "extremeTargetMs": 250,
  "title": "直播间标题",
  "selectedQualityKey": "20000",
  "qualities": [
    { "key": "20000", "label": "4K 原画", "bitrateKbps": 20000, "isBest": true },
    { "key": "10000", "label": "原画（1080P 高帧率）", "bitrateKbps": null, "isBest": false }
  ],
  "candidates": [
    {
      "sourceIndex": 0,
      "url": "http://127.0.0.1:5566/relay/xxxx",
      "format": "flv",
      "codec": "avc",
      "host": "cn-jsnj-01",
      "urlFingerprint": "len=180;fp=3f2ab1c4d5e6f708",
      "referer": "https://live.bilibili.com/",
      "label": "杜比原画"
    }
  ]
}
```

| 字段 | 必填 | 说明 |
|------|------|------|
| `sessionId` | ✅ | 播放会话号；页面回传时带上，宿主据此丢弃过期会话消息 |
| `mode` | ✅ | `extreme`（极限追帧）或 `stable`（稳定缓冲） |
| `extremeTargetMs` | ✅ | 仅 150 / 200 / 250 有效，其他值页面回落为 250 ms |
| `title` | ➖ | 用于 mpv 窗口标题 |
| `qualities` | ➖ | 平台可选画质档位；**只有一档时也显示**（当前档位可见但不可切换），为空时下拉为空 |
| `qualities[].key` | ✅ | 平台档位键（B站 qn、虎牙码率、斗鱼 rate、抖音拉流键…），页面原样回传 |
| `qualities[].label` | ✅ | 档位显示名（尽量与官方直播间一致） |
| `qualities[].bitrateKbps` | ➖ | 码率（kbps），有则在下拉里显示 |
| `qualities[].isBest` | ➖ | 是否最高档 |
| `selectedQualityKey` | ➖ | 当前候选对应的档位键；页面据此选中下拉项 |
| `candidates[].url` | ✅ | 页面可直接读取的地址（必要时为本地中继地址） |
| `candidates[].sourceIndex` | ✅ | 源索引，用于诊断与去重 |
| `candidates[].format` | ✅ | `flv` / `ts` / `hls` / `fmp4`；`fmp4|ts|hls` 走 hls.js，其余走 mpegts.js |
| `candidates[].codec` | ✅ | `avc` / `hevc` / `av1` / `unknown` |
| `candidates[].host` | ✅ | CDN 主机名（展示与诊断） |
| `candidates[].urlFingerprint` | ✅ | 诊断指纹，页面原样回传 |
| `candidates[].referer` | ➖ | 页面在「mpv 播放」时拼进桥接请求 |
| `candidates[].label` | ➖ | 画质标签（展示用） |

页面行为：规范化候选（丢弃空 URL、按 `sourceIndex:url` 去重）→ 过滤不可播放的格式与不支持的编码 →
**候选多于一条时并行探测**（`GET` + `cache:'no-store'`，拿到响应头立刻 `abort()`，绝不读响应体，
超时 1200 ms；只有一条候选时不探测）→ **不按探测耗时重排队**：把"响应正常"的候选提到前面、
组内仍按宿主顺序，返回 4xx/5xx 的候选直接丢弃 → 连不上时按同顺序依次切换。

探测只承担两件事：提前丢弃返回 HTTP 错误的线路、给宿主留下诊断记录（`checking` / `probe-result` /
`probe-rejected`）。**不按耗时排序**是因为走本地中继时该耗时主要由回环与中继转发决定，
与实际 CDN 质量无关，按它排序只会打乱宿主的平台优先级（详见 `docs/architecture/playback-strategy.md` 第 4 节）。

### 1.2 `target`

```json
{ "type": "target", "extremeTargetMs": 250 }
```

**热切换追帧档位**（不重连、不重新探测）：页面把新档位写到当前运行对象上、刷新顶部提示，
并立即向缓冲末端追一次；页面随后回一条 `status`（"追帧档位已切换为 250 ms"）。
宿主在没有活动会话时只记住该值，下次 `play` 时生效。

### 1.3 `stop`（兼容保留）

```json
{ "type": "stop" }
```

**宿主的「暂停播放」不再下发它**（改用 1.7 的 `pause`）：停止当前会话、销毁播放器、
清空当前流地址、提示回到"等待直播源"。页面保留该处理只为兼容旧版宿主。

### 1.4 `presets`（宿主主动下发）

```json
{ "type": "presets", "items": [ { "name": "预设名", "url": "https://live.bilibili.com/123", "platform": "bilibili" } ] }
```

页面在收到 `ready` 后主动请求一次（通过 `status` 消息中的约定文本），宿主随后下发。

### 1.5 `volume`

```json
{ "type": "volume", "value": 70 }
```

### 1.6 `bridge-info`

```json
{ "type": "bridge-info", "baseAddress": "http://127.0.0.1:5566" }
```

页面据此调用 `GET {baseAddress}/play?url=&title=&referer=` 启动 mpv。
`baseAddress` 为空字符串表示桥接服务未启动，页面会给出明确提示。

### 1.7 `pause`

```json
{ "type": "pause", "paused": true }
```

暂停 / 继续播放。**暂停不销毁会话**：播放器、当前地址与缓冲全部保留，页面只调用
`video.pause()`；`paused: false` 时从当前位置恢复，若落后直播边缘超过 3 秒则先追帧到缓冲末端。
页面随后回一条 `status`（"已暂停播放" / "已继续播放"）。

### 1.8 `mpv`

```json
{ "type": "mpv" }
```

宿主要求播放页用本地桥接服务启动 mpv。播放页当前未内置该按钮（左栏「mpv 播放」直接走宿主），
常量保留在 `INBOUND_MESSAGE_TYPES` 中供后续使用。

---

## 2. 页面 → 宿主

页面通过 `report(type, message, run, candidate, extras)` 上报，统一字段：

```json
{
  "type": "telemetry",
  "message": "遥测",
  "eventSequence": 42,
  "pageElapsedMs": 5312,
  "sessionId": 7,
  "sourceIndex": 0,
  "urlFingerprint": "len=180;fp=3f2ab1c4d5e6f708"
}
```

| `type` | 触发时机 | 关键 `extras` |
|--------|----------|---------------|
| `ready` | 页面脚本加载完成（握手） | `hevc`、`avc`（当前内核 MSE 解码能力，布尔） |
| `checking` | 候选多于一条、开始并行探测 | — |
| `probe-result` | 单条候选探测结束（成功 / 结果不确定） | `outcome`（`good`/`inconclusive`）、`elapsedMs`、`errorName`、`errorMessage` |
| `probe-rejected` | 单条候选返回 4xx/5xx，已被丢弃 | `outcome`、`statusCode` |
| `candidate-queue` | 候选队列生成 | `sourceIndexes`、`knownGoodCount`、`inconclusiveCount` |
| `candidate-active` | 开始连接某候选 | — |
| `status` | 状态变化 | `firstFrameMs`、`mode`；暂停/继续时 `message` 为"已暂停播放"/"已继续播放" |
| `log` | 页面诊断日志（页面不再显示日志面板） | `level`（`info`/`warn`/`error`）；宿主按级别落盘并追加到"最近事件" |
| `telemetry` | 每 ≥10 s 一次 | `currentTime`、`bufferedAheadMs`、`readyState`、`networkState`、`paused`、`ended`、`playbackRate`、`secondsSinceProgress`、`droppedVideoFrames`、`totalVideoFrames`、`mode`、`extremeTargetMs`、`reconnects` |
| `warning` | 可恢复问题（切换候选、重连） | `errorName`、`errorMessage`、`reconnectCount` |
| `error` | 终止性问题（含 HEVC 不受支持） | `errorMessage` |
| `reconnecting` | 断流后重连 | `reconnectCount` |
| `refresh-needed` | 所有候选不可用，请宿主重新解析 | — |
| `quality` | 用户在下拉里换了画质档位 | `key`（档位键）；宿主据此按该档位重新解析并重新下发 `play` |
| `target` | 用户在播放页底部改了追帧档位 | `extremeTargetMs`（150/200/250）；宿主只记住该值供下次播放沿用 |
| `request-play` | 用户点了播放页底部的「开始播放」 | — ；宿主执行与「解析房间」相同的解析与下发流程 |
| `toggle-pause` | 用户点了播放页底部的「暂停播放 / 继续播放」 | — ；宿主切换暂停状态并回下发 `pause` |

宿主对 `sessionId` 做**过期校验**：`sessionId` 与当前活动会话不一致时忽略该消息并记 Debug 日志
（`Ignored message from stale playback session`）。

---

## 3. 页面内的消息类型常量

页面与纯逻辑模块共用 `Web/player-core.js` 中的常量，禁止在页面里写字符串字面量：

```js
core.INBOUND_MESSAGE_TYPES  // play / chase（页面内部追帧按钮使用）/ stop / target / pause / mpv
core.OUTBOUND_MESSAGE_TYPES // ready / checking / probe-result / probe-rejected / ... / log / quality / request-play / toggle-pause
core.LOG_LEVELS             // info / warn / error（`log` 消息的 level 字段）
```

宿主侧的对应常量集中在 `ShellViewModel` 的私有 `const string` 字段中。

---

## 4. 兼容性与演进

- 新增字段必须**向后兼容**：页面忽略未知字段，宿主忽略未知 `type`（记 Warn 日志）。
- 删除或改名 `type` 属于破坏性变更，必须同时更新本文件、`player.html`、`ShellViewModel` 与前端测试。
- 宿主**不再下发** `chase`（追帧入口只在播放页底部，页面自己触发）；`INBOUND_MESSAGE_TYPES.CHASE`
  由页面内部的「追帧」按钮使用，因此常量必须保留。
- `Web/player-core.js` 被 `node --test tests/web` 覆盖：任何阈值或档位改动都必须同步更新用例。

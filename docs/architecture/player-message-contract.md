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
| `extremeTargetMs` | ✅ | 仅 150 / 200 / 250 有效，其他值页面回落为 200 ms |
| `title` | ➖ | 用于 mpv 窗口标题 |
| `qualities` | ➖ | 平台可选画质档位；≤1 项时页面隐藏画质下拉 |
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
并行探测所有候选（1.2 s 上限）→ 已知可用按延迟升序、之后是结果不确定的 → 依次尝试。

### 1.2 `chase`

```json
{ "type": "chase", "keepSeconds": 0.08 }
```

跳到缓冲末端前 `keepSeconds` 秒（夹取到 0.02–0.5 秒，缺省 0.08）。缓冲为空时页面记录一条 Warn 日志。

### 1.2.1 `target`

```json
{ "type": "target", "extremeTargetMs": 250 }
```

**热切换追帧档位**（不重连、不重新探测）：页面把新档位写到当前运行对象上、刷新顶部提示，
并立即向缓冲末端追一次；页面随后回一条 `status`（"追帧档位已切换为 250 ms"）。
宿主在没有活动会话时只记住该值，下次 `play` 时生效。

### 1.3 `stop`

```json
{ "type": "stop" }
```

停止当前会话、销毁播放器、清空当前流地址、提示回到“等待直播源”。

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
| `checking` | 开始探测候选 | — |
| `probe-result` | 单个候选探测完成 | `outcome`（`good`/`inconclusive`）、`elapsedMs`、`statusCode` |
| `probe-rejected` | 候选返回 4xx/5xx | `outcome: rejected`、`statusCode` |
| `candidate-queue` | 候选队列生成 | `sourceIndexes`、`knownGoodCount`、`inconclusiveCount` |
| `candidate-active` | 开始连接某候选 | — |
| `status` | 状态变化 | `firstFrameMs`、`mode`；页面请求预设时 `message` 含“请求预设列表” |
| `telemetry` | 每 ≥10 s 一次 | `currentTime`、`bufferedAheadMs`、`readyState`、`networkState`、`paused`、`ended`、`playbackRate`、`secondsSinceProgress`、`droppedVideoFrames`、`totalVideoFrames`、`mode` |
| `warning` | 可恢复问题（切换候选、重连） | `errorName`、`errorMessage`、`reconnectCount` |
| `error` | 终止性问题（含 HEVC 不受支持） | `errorMessage` |
| `reconnecting` | 断流后重连 | `reconnectCount` |
| `refresh-needed` | 所有候选不可用，请宿主重新解析 | — |
| `quality` | 用户在下拉里换了画质档位 | `key`（档位键）；宿主据此按该档位重新解析并重新下发 `play` |

宿主对 `sessionId` 做**过期校验**：`sessionId` 与当前活动会话不一致时忽略该消息并记 Debug 日志
（`Ignored message from stale playback session`）。

---

## 3. 页面内的消息类型常量

页面与纯逻辑模块共用 `Web/player-core.js` 中的常量，禁止在页面里写字符串字面量：

```js
core.INBOUND_MESSAGE_TYPES  // play / chase / stop
core.OUTBOUND_MESSAGE_TYPES // ready / checking / probe-result / ... / quality
```

宿主侧的对应常量集中在 `ShellViewModel` 的私有 `const string` 字段中。

---

## 4. 兼容性与演进

- 新增字段必须**向后兼容**：页面忽略未知字段，宿主忽略未知 `type`（记 Warn 日志）。
- 删除或改名 `type` 属于破坏性变更，必须同时更新本文件、`player.html`、`ShellViewModel` 与前端测试。
- `Web/player-core.js` 被 `node --test tests/web` 覆盖：任何阈值或档位改动都必须同步更新用例。

# 0006 中继上游请求必须携带 User-Agent

- 状态：已接受
- 日期：2026-09-17
- 相关：[ADR 0005 桥接与打包](0005-bridge-and-packaging.md)、[播放策略](../architecture/playback-strategy.md)、[故障排查 7.1](../runbooks/troubleshooting.md)

## 背景

B站房间 814（真实房间号 856077）解析正常（12 条候选），但 Web 播放页把交给它的
**全部 8 条**候选都判定为「返回 HTTP 错误」并放弃播放：

```
App.Playback     播放计划已准备 mode=extreme candidates=8 relay=true
App.Shell        https://d1--cn-gotcha104.bilivideo.com 拒绝（HTTP 403）
App.Shell        播放错误：所有候选线路都拒绝了内置播放器的请求
```

同期日志显示该房间随后改用「mpv 播放」（`已启动 mpv 外挂播放`）。
mpv 自带 UA，这也解释了"中继播不出、mpv 能播"的分叉。

## 证据

对 `getRoomPlayInfo` 响应里按 `host + base_url + extra` 拼出的 12 条真实地址逐条发请求，
只改变请求头集合（原始 `https.request`，逐字节控制）：

| 请求头 | `d1--cn-gotcha104.bilivideo.com`（m3u8） | `d1--cn-gotcha04b.bilivideo.com`（flv，302 后） |
|--------|------------------------------------------|------------------------------------------------|
| 无任何头 | 403 | 403 |
| 仅 `Accept` / 仅 `Accept-Encoding` | 403 | 403 |
| 仅 `Referer` | 403 | 403 |
| 仅 `User-Agent` | 200 | 302 → 403（该节点还要求 Referer） |
| `Referer` + `User-Agent` | 200 | 302 → 200 |

即：**这些 CDN 节点要求请求带 UA**（不校验 UA 具体是什么：`mpv 0.38.0`、
`Lavf/61.7.100` 与桌面 Chrome 同样通过）；其中一部分节点还要求 `Referer`。
两者同时具备时 12 条候选的 `https.request` 实测 12/12 返回 200；
交给播放页的前 8 条进一步读到真实媒体字节（FLV 文件头、
HLS 播放列表及其 `.ts` 切片的 `0x47` 同步字节）。

而中继构造上游请求时只注入 `Referer`（和透传 `Range`），`HttpClient`（`SocketsHttpHandler`）
**默认不发送 `User-Agent`**，于是中继发出的每条上游请求都是"无 UA"的 → 全部 403。
页面探测与真实播放走的是同一条中继路径，所以两者一起失败——这不是"探测比播放严格"的误判。

## 决策

1. 中继**必须**向上游注入非空 `User-Agent`（CDN 不校验其具体取值），
   取值统一为项目默认的桌面 Chrome UA（`HttpClientFactory.DefaultUserAgent`）；
2. 上游请求只在**一处**构造（`BridgeHost.CreateUpstreamRequest`），
   HLS 播放列表与切片（含播放列表里改写的子地址）共用它，避免两条路径的请求头再次分叉；
3. `Referer` 仍按 `RelayTarget.Referer` 注入（可为空），`Range` 仍由客户端透传；
4. 不为绕过任何平台校验而改造请求：UA 与 Referer 都是浏览器/播放器本来就会发送的头，
   本项决策只是让中继"像浏览器一样"请求，登录态一律沿用用户自备 Cookie，不涉及签名或风控绕过。

## 影响

- 修复后 B站（以及任何对 UA 有要求的 CDN）经中继的候选不再被误判为不可用；
- 其他平台（虎牙、斗鱼、抖音）此前在无 UA 下也能播，加上浏览器 UA 后仍属正常浏览器行为；
- 若将来某平台需要与默认值不同的 UA，应把 UA 放进候选/`RelayTarget` 而不是在各处硬编码；
- 回归用例：`tests/StreamPilot.Tests` 的
  `BridgeTests.UpstreamRequestAlwaysCarriesUserAgent`（离线、无网络）。

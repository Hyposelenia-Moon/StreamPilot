# ADR 0007：中继攻击面与超时分层、引擎热改适配

- 状态：已接受
- 日期：2026-10-10
- 相关：[ADR 0005 桥接与打包](0005-bridge-and-packaging.md)、[ADR 0006 中继上游必须带 UA](0006-relay-upstream-headers.md)、[播放策略 12.1 / 12.2](../architecture/playback-strategy.md)

## 背景

桥接服务（`StreamPilot.Bridge.BridgeHost`）在 ADR 0005 之后经历了两轮安全与稳定性整改，
实现已经偏离 0005 中"端点设计"一节的原描述：

1. 0005 列的 `GET /relay/register`（POST body 注册上游地址）从未按原方案保留——
   它没有任何身份校验，本机任意网页都能把自己的回环地址注册成"中继目标"再读回数据（SP-02）；
2. 0005 写的 `Access-Control-Allow-Origin: *` 与"PNA 无条件返回"都已收敛；
3. 0005 写的 `GET /web/*` 静态回退**在实现里并不存在**（播放页一律走 WebView2 虚拟主机映射）；
4. 任意路径的 `OPTIONS` 都会拿到 `204 + CORS`，等于向本机任意页面确认"这个回环端口上什么路径都有人应答"；
5. 中继是长连接，但"等响应头 / 读播放列表正文 / 长连接正文"三段此前共用一套含糊的超时说法；
6. 播放页"停止追帧 / 换档位"过去直接调用引擎实例上的 `configure`（两家引擎都没有这个方法），
   配置一个字没变却上报成功（SP-01）；受控重建又缺少"旧连接断开"与"新连接建立"之间的间隙。

本 ADR 把上述整改固化为决策，并作为 0005 相应条目的**更正依据**（0005 保持历史原文不动）。

## 决策

### 1. 关闭 HTTP 注册路由：注册只能进程内发起

- 中继只有 `GET /relay/{token}`（只读转发）；注册入口只剩进程内的
  `IPlaybackBridge.RegisterRelay`（`PlaybackCoordinator` 在宿主进程内调用）与播放列表改写时的
  进程内 `RegisterChild`；
- `/relay` 路径上的非 `GET` 请求一律 404 并记 `Warn`；
- 不引入令牌 / 签名层：播放页从不使用 HTTP 注册，补一层校验属于"给不存在的入口加锁"。

### 2. CORS 收敛：固定来源 + 预检最小化

- `Access-Control-Allow-Origin` 固定为播放页来源 `https://appassets.local`
  （`BridgeHost.PlayerPageOrigin`），不再使用 `*`，并带 `Vary: Origin`。
  回环服务 + `*` 会让本机任意网页跨源读取中继响应（含平台签名地址与直播内容），
  而播放页来源只有一个，收敛成固定值不影响任何正常路径；
- `Access-Control-Allow-Private-Network: true` 只在**预检**请求声明
  `Access-Control-Request-Private-Network: true` 时返回（规范要求它只出现在此类预检响应上）；
- **预检只为已知路由返回 204 + CORS**：`/relay/**`、`/play`、`/health` 之外的路径
  （含 `/`、`/web/*`、`/relayx/1` 这类前缀相似路径）一律 404。
  判定抽成纯函数 `BridgeHost.ResolveRoute`，因此离线用例可以直接断言，不必启动 `HttpListener`。

### 3. 目的地址白名单：只有公网 http/https

- 注册与**重定向之后的最终地址**都要过 `IsAllowedUpstreamUrl`：
  只允许 `http` / `https`（`rtmp`/`rtmps` 中继作为 HTTP 客户端本来就拉不了），
  拒绝回环 / 私网 / 链路本地 / 未指定地址的字面量与 `localhost` 主机名
  （含 `::ffff:10.0.0.5` 这类 IPv4-mapped IPv6、`[::1]` 这类带方括号的 IPv6、
  以及 `2130706433` / `0x7f.1` 这类整数形式的回环）；
- 只做**字面量**判定，不做 DNS 解析：解析结果会变，且解析与连接之间仍可能被改写
  （DNS rebinding），拿它做安全决策只会给出虚假的保证。这里的目标是去掉"零成本访问本机与内网"。
- mpv 路径单独保留 `rtmp`/`rtmps`（mpv 自带 RTMP 支持），但与中继路径**不共用**同一份校验。

### 4. 超时分层：三段各自负责，互不牵连

| 环节 | 约束 | 具名常量 |
|------|------|----------|
| 建立上游连接 | 10 秒 | `UpstreamConnectTimeoutSeconds` |
| 等上游响应头 | 10 秒，超时回 504 并记 `Warn` | `UpstreamHeaderTimeoutSeconds` + 独立 `CancellationTokenSource` |
| 读 HLS 播放列表正文 | 15 秒，超时回 504 并记 `Warn` | `PlaylistDownloadTimeoutSeconds` + 独立 `CancellationTokenSource` |
| FLV / TS 长连接正文 | 空闲看门狗：连续 30 秒无新数据才断开 | `IdleTimeoutSeconds` |

- `HttpClient.Timeout` 保持 `InfiniteTimeSpan`，理由说对：`ResponseHeadersRead` 下它只约束到响应头，
  并不会（也不该）取消直播流；体读取的边界由空闲看门狗与上面两条独立 CTS 负责。
- `PooledConnectionLifetime` 同样为 `InfiniteTimeSpan`：定时回收会把正在读的长连接一起换掉，
  表现为固定时长的"看着看着断一下"。

### 5. 引擎热改适配与重建退避

- 引擎能力表（`Web/player-core.js` 的 `ENGINE_CAPABILITIES`）决定路径，依据是两份库内代码：
  hls.js 的 `hls.config` 是各控制器共享的同一对象 → **热改 + 读回校验**；
  mpegts.js 的 `_live_latency_chaser` / `_live_sync_controller` 只在构造时按 `_config` 决定是否创建
  → **受控重建**（保留候选 / 队列 / 会话号 / 暂停态 / 阶段 / 音量 / 档位）；
- 只有 `{ok:true}`（热改读回一致，或重建拿到新实例）才允许调用方改状态行并上报成功，
  失败一律上报失败并保持原状态；
- 受控重建前做**一次有界退避**：先断开旧连接，再等待 `RECONNECT_DELAYS_MS` 的第一档
  （上限 `MAX_REBUILD_BACKOFF_MS = 1000` 毫秒），最后才建新连接。
  上游对同一条签名地址的并发连接有限流（见 `docs/parsers/douyu.md` 的实测结论），
  立刻重连会撞上还没回收的旧连接，表现为首帧偶发变慢；退避只用于给上游留出察觉窗口，
  因此复用重连退避序列而不是新造一个数字；
- 退避窗口内不改状态行阶段、不产生任何上报：状态行不会闪回「播放中」，也不会重复上报。
- 录制侧的同族约定：收尾（关闭末分片）必须使用**独立且有界**的令牌
  （`FlvStreamRecorder.FinalizeTimeoutSeconds` / `TsStreamRecorder.FinalizeTimeoutSeconds` = 10 秒），
  并在 `finally` 中兜底释放分片句柄。

## 被拒方案

1. **给 HTTP 注册路由补令牌校验**：播放页不使用该路由，补校验只是把攻击面留在原地；
   直接删除入口才是最小攻击面（拒绝"为了兼容不存在的调用方而保留危险入口"）。
2. **做 DNS 解析后再判定私网**：解析结果可变、解析与连接之间存在 rebinding 窗口，
   给出的是虚假保证；只做字面量判定的收益（去掉零成本内网访问）已经覆盖真实威胁。
3. **把 `Access-Control-Allow-Private-Network` 无条件加到所有响应**：违反规范且扩大信息暴露，
   0005 已明确这只是参考项目的缺陷，本项目不沿用。
4. **为桥接引入鉴权与 TLS / 换成 Kestrel**：并发上限是"1 个播放页 + 1 个录制 + 少量探测"，
   收益不足；若将来需要外网访问，必须另开 ADR。
5. **给 mpegts.js 写"运行时改配置"的私有字段改写**：`_config` 只在构造期决定控制器是否创建，
   改它属于"看起来成功、实际不生效"，与 SP-01 的根因同类，因此坚持重建。
6. **用假 IO（假 `FileStream`）断言"收尾卡死会有超时上界"**：那会把测试与真实实现脱钩，
   因此收尾有界性只在用例里做**源码级核实**（常量有界 + 收尾构造独立有界 CTS + `finally` 释放句柄）。

## 后果

### 正面

- 回环桥接对"本机任意网页"的攻击面被压到只剩"只读转发 + 目的地址白名单"；
- 超时分层让"上游半开"有明确终点（504 + `Warn`），长连接又不会被误杀；
- 追帧配置的生效判定与重建间隙都有据可依，"显示成功、实际没生效"与"首帧偶发变慢"同时收敛。

### 负面 / 风险

- 中继的上游地址限制在**代理 / 内网 CDN** 场景下会拒绝合法地址（当前六个平台的 CDN 都是公网域名）；
- 受控重建仍会短暂断流（退避把间隙拉长到约 250 毫秒），这是 mpegts.js 无运行时配置入口的代价；
- 收尾的最后一跳（`FileStream.DisposeAsync` 内部的 flush）无法在不泄漏句柄的前提下设置上界，
  与 TS 侧保持同一取舍。

## 相关用例

- `tests/StreamPilot.Tests/Cases/RelaySafetyTests.cs`：目的地址白名单、注册处理器不存在、
  预检只答已知路由（`ResolveRoute`）、三段超时与长连接不被误杀；
- `tests/StreamPilot.Tests/Cases/FlvStreamRecorderFinalizeTests.cs` / `TsStreamRecorderFinalizeTests.cs`：
  收尾必达、句柄释放、收尾有界；
- `tests/web/player-core.test.js`：能力表判定、热改读回校验、重建前退避的顺序与有界性。

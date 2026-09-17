# 斗鱼（douyu）解析器

实现：`src/StreamPilot.Parsers/Douyu/DouyuParser.cs`
优先级：**P1** ｜ 平台：`PlatformId.Douyu` ｜ 链接前缀：`https://www.douyu.com/`

## 输入

- 房间号（`douyu.com/{id}`）；
- 直播间链接。

## 请求序列

| 步骤 | 方法与地址 | 说明 |
|------|-----------|------|
| 1 | `GET https://www.douyu.com/{id}` | 提取最终房间号（正则 `getLegacyFirstStream\(\{\s*roomID:\s*(\d+),`）与标题/主播名/分区 |
| 2 | `GET https://www.douyu.com/betard/{finalRoomId}` | 轮播检测：`room.videoLoop == 1` → `Replaying`（失败只记 Debug，不阻断解析） |
| 3 | `GET https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did={DEVICE_ID}` | 取 `rand_str` / `key` / `enc_time` / `is_special` / `enc_data` |
| 4 | `POST https://www.douyu.com/lapi/live/getH5PlayV1/{finalRoomId}` | 表单字段顺序固定：`enc_data`、`tt`、`did`、`auth`、`cdn`、`rate={档位}`、`hevc=0`、`fa=0`、`ive=0`；`Referer` 必带 |

`DEVICE_ID = 10000000000000000000000000003306`（与参考实现一致）。

## 签名算法

```
ts      = 当前 Unix 秒
signStr = is_special == 1 ? "" : finalRoomId + ts
auth    = rand_str
重复 enc_time 次: auth = md5(auth + key)
auth    = md5(auth + key + signStr)
```

`md5` 为 UTF-8 字节的小写十六进制。`enc_time` 若不在 `0..100` 视为异常 → `ParseError`。

## 候选优先级

1. `http_stream.flv_pull_url` 的每个画质 → `StreamFormat.FlvHttp`（画质由键名映射）；
2. 若 `rtmp_url + "/" + rtmp_live` 以 `http` 开头，也作为 `FlvHttp` 候选（**斗鱼很多房间只有这一条**）；
3. `hls_url` / `hls_url_map` → `StreamFormat.HlsTs`；
4. 以上都没有、但存在 RTMP 地址 → 产出 `StreamFormat.Rtmp` 候选（**不是** `NotLive`），
   由上层提示"该房间仅提供 RTMP，Web 端不可播放，请使用 mpv 外挂播放"；
5. 完全没有任何地址 → `NotLive`。

多 CDN 补取（见下文「一直重连」一节）：主请求（`cdn` 留空）候选数 ≤1 条时，
会按 `data.cdnsWithName[].cdn` 逐个再请求一次播放地址（最多 2 个），
按 **CDN 主机名去重**后合并成多条候选，保证页面真的有线路可切。

## 失败判定

| 状态 | 判定 |
|------|------|
| 房间不存在 | 页面包含 `<span><p>该房间目前没有开放</p></span>` |
| 轮播中 | `/betard` 的 `room.videoLoop == 1` |
| 未开播 | `error == -5`，或 `msg == "房间未开播"` |
| 平台拒绝 | `error == -15`，或 `msg == "非法请求"` → `Rejected`（不重试、不绕过） |
| 解析错误 | 其他非零 `error`、加密参数异常、页面结构变化 |

## 画质档位（`Qualities`）

- `getH5PlayV1` 表单里的 `rate` 就是档位：`0`=原画（最高档，也是默认值）、`8`=蓝光8M、
  `4`=蓝光4M、`3`=超清、`2`=高清；
- 可用档位来自响应 `data.multirates[]`（每项含 `rate`/`name`/`bitRate`），
  实际生效档位取 `data.rate` 写入 `SelectedQualityKey`；
- `data.rateSwitch != 1` 表示平台只提供原画，此时档位列表只有一项；
- **地址签名与档位绑定**：换档必须重新请求接口（不像虎牙那样可以在签名后追加参数）。

## 「一直重连」的原因与处理（实测）

现象：`https://www.douyu.com/1811143?dyshid=...` 能解析成功，但播放页一直显示「重连中（第 N 次）」/
「切换线路」，停不下来。

### 实测证据

复现命令（Node 脚本，只带 UA + `Referer: https://www.douyu.com/`，签名流程与 `DouyuParser` 一致）：

```
GET  https://www.douyu.com/1811143                              → 200, 110718 字节
GET  https://www.douyu.com/betard/1811143                        → 200, videoLoop=0
GET  https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did=10000000000000000000000000003306
                                                                 → {"error":0,"msg":"正常","data":{"enc_time":1,...,"enc_data":"eyJhbGd..."}}
POST https://www.douyu.com/lapi/live/getH5PlayV1/1811143         → 表单 enc_data,tt,did,auth,cdn,rate=0,hevc=0,fa=0,ive=0
```

`getH5PlayV1` 原始响应要点（截断）：

```json
{"error":0,"msg":"ok","data":{
  "rtmp_url":"https://stream-shantou-cmcc-183-239-202-83.edgesrv.com:443/live",
  "rtmp_live":"1811143rVsdBAI3Q.flv?wsAuth=..&token=web-h5-0-1811143-..&logo=0&expire=0&did=..&sid=436357761&...",
  "rateSwitch":1,"rate":0,
  "multirates":[{"rate":0,"name":"原画2K60"},{"rate":2,"name":"高清"}],
  "cdnsWithName":[{"name":"线路1","cdn":"scdncmccgudst"},{"name":"线路7","cdn":"hw-h5"}]}}
```

- **`data.http_stream` / `data.hls_url` / `hls_url_map` 全部缺失** → 解析器只能走
  `rtmp_url` + `rtmp_live` 的 https-http 分支，得到 **1 条 FlvHttp 候选**；
- 该地址本身是健康的：连续读取 12 秒收到 **19.5 MB / 约 13 Mbps**，首字节 138 ms，
  `Content-Type: video/x-flv`，`Access-Control-Allow-Origin: *`；
- 但**同一签名地址的第 2 条并发连接会被上游在 0.2 到 0.4 秒内切断**
  （冷却约 90 秒后首连又恢复正常）——这是斗鱼 CDN 的会话限制；
- 应用日志里的失败链条正好对上这条限制：
  `探测「可用」(200)` → `正在连接 …` →
  `Warn Bridge.Host 处理桥接请求失败 System.IO.IOException: Received an unexpected EOF or 0 bytes from the transport stream`
  → `重连中（第 1 次）：直播连接已结束` → `首帧等待失败：no supported source` →
  `切换线路：首帧超时或失败` → `播放页请求重新解析`，周期约 250 ms。

### 机制与修复

1. 播放页原来在 `buildCandidateQueue` 里用 `fetch` 探测候选，**拿到状态码后不 abort、也不消费响应体**，
   于是这条探测连接一直占着；紧接着 mpegts.js 对**同一个地址**发第 2 条连接 → 被上游秒断 → 首帧必然失败。
   - 修复：新增 `probeCandidate`，拿到状态码后**立即 `controller.abort()`**；
   - 且**候选只有 1 条时直接跳过探测**（探测只用于排序，对单候选零收益却毁掉唯一连接）。
2. 解析器现在会按 `data.cdnsWithName[].cdn` **补取其它 CDN 的地址**
   （`FetchAdditionalCdnPlayInfosAsync`，最多 2 个、仅在主请求候选 ≤1 条时触发，按 CDN 主机去重）。
   实测 `cdn=hw-h5` 返回完全不同的主机 `hw1a.douyucdn2.cn/live`（同样 `video/x-flv`、200），
   于是页面有 2 条**互相独立**的候选可切换，而不是在同一条地址上反复重连。
3. 宿主侧对页面发来的 `refresh-needed` 加了**连续自动重解析上限**
   （`ShellViewModel.MaxAutomaticReResolves = 2`）。原来每次重新解析都会开新会话，
   新会话的"只请求一次"计数被重置，于是"单候选 + 地址失效"会无限循环；
   现在到达上限后停止自动重试并给出可操作提示：
   `已停止自动重试（连续 N 次重新解析都未能出画）。请点「开始播放」手动重试，或改用「mpv 播放」。原因：…`
   计数在**真的出画**后清零；手动点「开始播放」「解析房间」「载入预设」或换档位也会清零。

## 已知限制

- **多数房间仅返回 RTMP**：Web 端（WebView2 + mse）无法播放 RTMP，因此 Web 播放与原始流录制对这类房间不可用。
  这是平台能力限制，不是缺陷；UI 会明确提示改用 mpv（mpv 原生支持 RTMP）。
  不引入 RTMP 客户端库的原因见 [ADR 0004](../../adr/0004-raw-recording.md)。
- **候选时效**：地址带 `wsAuth`/`token` 签名（`expire=0`，没有可解析的绝对过期时间），
  过期后只能通过重新解析换成新地址；程序已限制自动重试次数，到上限后请手动点「开始播放」重试。
- **同一签名地址只允许 1 条并发连接**（实测，见上文）：因此播放页**不对单候选做探测**，
  并且解析器会尽量多给几条不同 CDN 的候选。
- 参考实现把 `rate=-1`（服务器自选画质）与 `hevc=0`（优先 AVC）作为固定参数；本项目改为**显式档位**：`PreferredQualityKey` 为空/`best`/非法时用 `rate=0`（原画 = 最高档），否则用用户选择的 rate（非法值记 Warn 后回退 0）。

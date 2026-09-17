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
2. 若 `rtmp_url + "/" + rtmp_live` 以 `http` 开头，也作为 `FlvHttp` 候选；
3. `hls_url` / `hls_url_map` → `StreamFormat.HlsTs`；
4. 以上都没有、但存在 RTMP 地址 → 产出 `StreamFormat.Rtmp` 候选（**不是** `NotLive`），
   由上层提示"该房间仅提供 RTMP，Web 端不可播放，请使用 mpv 外挂播放"；
5. 完全没有任何地址 → `NotLive`。

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

## 已知限制

- **多数房间仅返回 RTMP**：Web 端（WebView2 + mse）无法播放 RTMP，因此 Web 播放与原始流录制对这类房间不可用。
  这是平台能力限制，不是缺陷；UI 会明确提示改用 mpv（mpv 原生支持 RTMP）。
  不引入 RTMP 客户端库的原因见 [ADR 0004](../../adr/0004-raw-recording.md)。
- 参考实现把 `rate=-1`（服务器自选画质）与 `hevc=0`（优先 AVC）作为固定参数；本项目改为**显式档位**：`PreferredQualityKey` 为空/`best`/非法时用 `rate=0`（原画 = 最高档），否则用用户选择的 rate（非法值记 Warn 后回退 0）。

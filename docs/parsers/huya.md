# 虎牙（huya）解析器

实现：`src/StreamPilot.Parsers/Huya/HuyaParser.cs`
优先级：**P0** ｜ 平台：`PlatformId.Huya` ｜ 链接前缀：`https://www.huya.com/`

## 输入

- 房间号（`huya.com/{id}`，通常是数字，也接受字母数字短号）；
- 直播间链接。

## 请求序列

| 步骤 | 方法与地址 | 说明 |
|------|-----------|------|
| 1 | `GET https://www.huya.com/{idOrShortId}` | 抽取首个 `stream: ` 到 `,"iFrameRate"` 之间的 JSON，补 `}` 后解析，取 `data[0].gameLiveInfo.profileRoom` |
| 2 | `GET https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid={profileRoom}` | 权威直播状态与线路列表（`baseSteamInfoList`，兼容 `baseStreamInfoList`） |
| 3 | `POST https://udblgn.huya.com/web/anonymousLogin` | JSON 体 `{"appId":5002,"byPass":3,"context":"","version":"2.4","data":{}}`，取 `data.uid`（字符串）用于签名 |

## 候选映射

每条线路先 FLV 后 HLS（仅当对应 anticode 非空）：

```
{sFlvUrl}/{sStreamName}.{sFlvUrlSuffix}?{签名后的 anticode}   → FlvHttp
{sHlsUrl}/{sStreamName}.{sHlsUrlSuffix}?{签名后的 anticode}   → HlsTs
```

- `CdnHost` 取基地址主机名，`HttpReferer = https://www.huya.com/`，`Codec = Avc`；
- `Quality` 由 `iBitRate` 阈值推导（≥8 Mbps → `Hd1080HighFps`，≥4 Mbps → `Hd1080`，≥2 Mbps → `Hd720`，缺失 → `Unknown`）。

## anticode 签名算法（完整复刻参考实现）

1. 把 anticode 解析为有序键值表，强制 `ver=1`、`sv=2110211124`；
2. `seqid = parse(uid) + 当前毫秒时间戳`；
3. `uuid = ((now_ms % 10^10) * 1000 + rand(0,1000)) % (2^32 - 1)`；
4. `ss = md5(seqid|ctype|t)`（`ctype`/`t` 缺失 → 该线路 `ParseError`）；
5. `fm = Base64 解码(anticode.fm)`，按顺序替换 `$0`→uid、`$1`→streamName、`$2`→ss、`$3`→wsTime；
6. `wsSecret = md5(替换后的 fm)`；
7. 删除 `fm` 与 `txyp`，其余键值按插入顺序拼成 `k=v&k=v…`（不做二次 URL 编码）。

单条线路签名失败会被容忍（Warn + 继续），**全部**失败才返回 `ParseError`（"虎牙所有线路签名失败"）。

## 失败判定

| 状态 | 判定 |
|------|------|
| 未开播 | `liveStatus == "OFF"`（大小写不敏感）；或线路列表为空 |
| 轮播中 | `liveStatus == "REPLAY"` |
| 房间不存在 | HTTP `status == 422` |
| 解析错误 | `liveStatus` 为其他值（**修正参考实现的 `unreachable!()` panic**）、页面/接口结构变化、`profileRoom` 缺失 |
| 网络错误 | 由 `HttpTextClient` 抛出 |

## 已知限制

- 候选未声明过期时间（`wsTime` 为十六进制 Unix 秒，未强制解析），因此依赖
  「候选探测失败即切换」与「首帧超时即切换」兜底；通常虎牙签名有效期为数小时。
- 虎牙页面结构（`stream: ` 标记）变化会导致 `ParseError`，此时会记录缺失的标记名，便于快速定位。

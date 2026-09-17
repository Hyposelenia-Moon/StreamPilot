# Bigo Live（bigo）解析器

实现：`src/StreamPilot.Parsers/Bigo/BigoParser.cs`
优先级：**P2** ｜ 平台：`PlatformId.Bigo` ｜ 链接前缀：`https://www.bigo.tv/`

## 输入

- 房间号（`bigo.tv/{id}` 的数字 ID）；
- 直播间链接。

## 请求序列

单次请求：

| 步骤 | 方法与地址 | 说明 |
|------|-----------|------|
| 1 | `POST https://ta.bigo.tv/official_website/studio/getInternalStudioInfo` | 表单仅一个字段 `siteId={roomId}`；`Referer: https://www.bigo.tv/` |

响应结构：

```json
{ "msg": "", "data": { "clientBigoId": "abc", "hls_src": "https://.../index.m3u8",
                       "roomType": "0", "roomTopic": "", "nick_name": "", "roomStatus": 0,
                       "needLogin": false } }
```

> 字段名是 `nick_name`（下划线），`siteId`/`roomId` 在匿名响应里恒为空字符串。

## 候选映射

- 仅 `hls_src` 一个候选 → `StreamFormat.HlsTs`、`Codec = Avc`、`Quality = Unknown`、
  `HttpReferer = https://www.bigo.tv/`；
- `Title = roomTopic`、`Anchor = nick_name`、`Category` 为空。

## 失败判定

| 状态 | 判定 |
|------|------|
| 未开播 | `hls_src` 为空/缺失，且 `needLogin` 不为真 |
| 被拒绝 | `hls_src` 为空且 `needLogin == true`（匿名读不到直播信息，绝不是"房间不存在"） |
| 房间不存在 | 响应缺少 `data` |
| 解析错误 | 响应不是合法 JSON |
| 网络错误 | 由 `HttpTextClient` 抛出 |

> 判定顺序是"**有地址即开播**"：`roomStatus` 在匿名请求下恒为 `0`，
> 参考实现（`lsar`）解析了它却从未使用；把它当唯一依据会把在播房间误判为未开播。

## 画质档位（`Qualities`）

- Bigo 的工作室接口**只返回一条 `hls_src`**，没有档位枚举、也没有码率字段，
  因此 `Qualities` 固定只有一项（`Key = "default"`，`Label = "默认（平台自带 HLS）"`），
  播放页不会显示画质下拉；
- 若将来需要多档，只能解析 master playlist 的 `#EXT-X-STREAM-INF`（标准 HLS 推断，需实测确认）。

## 已知限制

- **需要登录**：匿名请求可能返回 `needLogin: true` 且不带主播名/HLS 地址，此时会得到
  `Rejected`（"Bigo 要求登录后才能读取直播信息"），而不是"未开播"；
- **地区限制**：Bigo 对部分地区（含中国大陆 IP）拒绝访问，此时会得到 `NetworkError` 或 `ParseError`；
- 不提供画质/多线路信息（接口只返回单个 HLS 地址）；
- 不解析"轮播"状态。

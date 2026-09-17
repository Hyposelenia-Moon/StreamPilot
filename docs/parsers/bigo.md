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
{ "msg": "", "data": { "clientBigoId": 0, "hls_src": "https://.../index.m3u8",
                       "roomType": 0, "roomTopic": "", "nickName": "", "roomStatus": 0 } }
```

## 候选映射

- 仅 `hls_src` 一个候选 → `StreamFormat.HlsTs`、`Codec = Avc`、`Quality = Unknown`、
  `HttpReferer = https://www.bigo.tv/`；
- `Title = roomTopic`、`Anchor = nickName`、`Category` 为空。

## 失败判定

| 状态 | 判定 |
|------|------|
| 未开播 | `roomStatus == 0`；或 `hls_src` 为空/缺失 |
| 房间不存在 | 响应缺少 `data` |
| 解析错误 | `data` 存在但结构不可识别 |
| 网络错误 | 由 `HttpTextClient` 抛出 |

> 参考实现（`lsar`）解析了 `roomStatus` 却从未使用；本项目把它纳入未开播判定，但保持宽容：
> 只有字面量 `0` 才判定为未开播，避免因平台新增状态值而误报。

## 已知限制

- **地区限制**：Bigo 对部分地区（含中国大陆 IP）拒绝访问，此时会得到 `NetworkError` 或 `ParseError`；
- 不提供画质/多线路信息（接口只返回单个 HLS 地址）；
- 不解析"轮播"状态。

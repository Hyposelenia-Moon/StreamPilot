# YY（yy）解析器

实现：`src/StreamPilot.Parsers/Yy/YyParser.cs`
优先级：**P1** ｜ 平台：`PlatformId.Yy` ｜ 链接前缀：`https://www.yy.com/`

## 输入

- 房间号（`yy.com/{id}`）；
- 直播间链接。

## 请求序列

| 步骤 | 方法与地址 | 说明 |
|------|-----------|------|
| 1 | `GET https://www.yy.com/{roomId}` | 单条正则抽取：`nick: "(.+?)".+?roomName: decodeURIComponent\("(.+?)"\).+?stringBiz: "(.*?)"` → 主播名 / 标题 / 分类 |
| 2 | `POST https://stream-manager.yy.com/v3/channel/streams?uid=3071000363&cid={roomId}&sid={roomId}&appid=0&sequence={now_ms}&encode=json` | `Content-Type: text/plain;charset=UTF-8`，JSON 体结构见下 |

请求体（键与嵌套必须完全一致）：

```json
{"head":{"seq":<now_ms>,"appidstr":"0","bidstr":"120","cidstr":"<roomId>","sidstr":"<roomId>","uid64":0,"client_type":108,"client_ver":"5.19.4","stream_sys_ver":1,"app":"yylive_web","playersdk_ver":"5.19.4","thundersdk_ver":"0","streamsdk_ver":"5.19.4"},"client_attribute":{"client":"web","model":"web0","cpu":"","graphics_card":"","os":"chrome","osversion":"141.0.0.0","vsdk_version":"","app_identify":"","app_version":"","business":"","width":"1536","height":"960","scale":"","client_type":8,"h265":0},"avp_parameter":{"version":1,"client_type":8,"service_type":0,"imsi":0,"send_time":<now_ms/1000>,"line_seq":-1,"gear":2,"ssl":1,"stream_format":0}}
```

## 候选映射

`avp_info_res.stream_line_addr` 中每个条目的 `cdn_info.url`：

- 路径以 `.m3u8` 结尾（忽略查询串）→ `StreamFormat.HlsTs`，否则 `StreamFormat.FlvHttp`；
- `Codec = Avc`、`Quality = Unknown`、`HttpReferer = https://www.yy.com/`、`CdnHost` 取 URL 主机名；
- `SourceIndex` 与接口返回顺序一致。

标题使用**严格**的 JS `decodeURIComponent` 语义解码（`+` 不视为空格，见 `QueryStringParser.DecodeComponentStrict`）。

## 失败判定

| 状态 | 判定 |
|------|------|
| 房间不存在 | 房间页正则不匹配 |
| 未开播 | `avp_info_res.stream_line_addr` 缺失，或没有可用 URL |
| 解析错误 | 关键字段（主播名 / 标题）缺失、响应不是 JSON |
| 网络错误 | 由 `HttpTextClient` 抛出 |

YY 不提供明确的"轮播/重播"状态字段，因此不做该判定。

## 已知限制

- 未实现"轮播中"分类（平台接口无此信息）；
- 请求体中的 `osversion`/`playersdk_ver` 等版本号是客户端指纹，平台升级可能要求新版本号；
  一旦平台拒绝，会得到 `ParseError`（响应非 JSON 或缺少 `avp_info_res`），便于快速定位。

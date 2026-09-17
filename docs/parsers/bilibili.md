# 哔哩哔哩（bilibili）解析器

实现：`src/StreamPilot.Parsers/Bilibili/BilibiliParser.cs`
优先级：**P0** ｜ 平台：`PlatformId.Bilibili` ｜ 链接前缀：`https://live.bilibili.com/`

## 输入

- 数字房间号（例如 `21452505`）；
- 字母数字短号（例如 `22637261` 之外的 `live.bilibili.com/xxx` 短链）→ 需先抓房间页取真实数字房间号；
- 直播间链接（`https://live.bilibili.com/123`，支持带查询串与锚点）；
- 可选 `RoomQuery.BilibiliCookie`：**留空时走匿名解析**（与参考项目"强制校验 Cookie 才解析"不同，避免阻断普通用户）。

## 请求序列

| 步骤 | 方法与地址 | 说明 |
|------|-----------|------|
| 1 | `GET https://live.bilibili.com/{idOrShortId}` | 仅在输入不是纯数字时执行；解析 `defaultRoomId` / `room_id` / `roomid` / `roomId` |
| 2 | `GET https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id={id}` | 标题、主播名、分区、封面；`data: null` → 房间不存在 |
| 3 | `GET https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?protocol=0,1&format=0,1,2&codec=0,1&qn=30000&platform=web&ptype=8&dolby=5&panorama=1&room_id={id}` | 全部候选线路与画质 |

`qn=30000` 为 B站最高画质请求值（杜比原画，其他档位：`20000`=4K、`15000`=2K、`10000`=1080P 高帧率、`400`=1080P）。

## 候选映射

按 `stream[] → format[] → codec[] → url_info[]` 的嵌套顺序生成候选，`SourceIndex` 从 0 递增：

- URL = `url_info.host + codec.base_url + url_info.extra`（直接拼接，不加分隔符）；
- `Format`：`http_stream+flv` → `FlvHttp`；`ts` → `HlsTs`；`fmp4` → `HlsFmp4`；未知组合跳过并记 Debug；
- `Codec`：`avc` / `hevc` / `av1`，其余 `Unknown`；
- `Quality`：优先 `current_qn`，其次 `accept_qn[0]`，经 `QualityNames.FromBilibiliQualityNumber` 映射；
- `CdnHost`：`url_info.host`；`HttpReferer`：`https://live.bilibili.com/`；
- `ExpiresAt`：从 `extra` / `base_url` 的 `expires` 参数解析（Unix 秒或毫秒）。

## 失败判定

| 状态 | 判定 |
|------|------|
| 未开播 | `data.live_status == 0`；或最终候选数为 0 |
| 轮播中 | `data.live_status == 2` |
| 房间不存在 | `getInfoByRoom` 的 `data` 为 `null` 或 `code == -400` |
| 解析错误 | `code != 0`（带平台 `message`）、关键字段缺失 |
| 网络错误 | 由 `HttpTextClient` 抛出 |

## Cookie 说明

- Cookie 仅通过 `Cookie` 请求头注入到 `api.bilibili.com` / `api.live.bilibili.com`；
- **不写入日志**（只记录 `Redacted` 形式）；保存在 `%LOCALAPPDATA%\StreamPilot\config.json`；
- Cookie 无效不会导致解析失败（匿名仍可拿到低画质线路）。

## 已知限制

- 未实现 WBI 签名：`getRoomPlayInfo` 为公开接口，匿名可访问；若平台改为强制签名，本解析器会返回 `ParseError` 并记录缺失字段；
- 不解析重播源（`live_status == 2` 直接返回轮播提示）。

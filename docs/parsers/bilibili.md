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
| 2 | `GET https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id={id}` | 标题、主播名、分区、封面；`data: null` → 房间不存在；被风控（`code=-352/-412/-509`）时降级到第 3 步 |
| 3 | `GET https://api.live.bilibili.com/xlive/web-room/v1/index/getRoomBaseInfo?room_ids={id}&req_biz=web_room_componet` | 仅在第 2 步不可用时调用；取 `data.by_room_ids` 里的 `title` / `uname` / `area_name` / `cover` |
| 4 | `GET https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?protocol=0,1&format=0,1,2&codec=0,1&qn={qn}&platform=web&ptype=8&dolby=5&panorama=1&room_id={id}` | 全部候选线路与画质；`qn` 默认 `30000`，用户选了档位时用该档位的 qn |

`qn` 取值：`30000`=杜比原画、`25000`=默认原画、`20000`=4K、`15000`=2K、`10000`=1080P 高帧率（官方名"原画"）、`400`=蓝光、`250`=超清、`150`=高清、`80`=流畅。

## 画质档位（`Qualities`）

- **可用档位**来自 `playurl_info.playurl.stream[].format[].codec[].accept_qn[]`（同一房间所有编码的并集去重）。
  注意 `accept_qn[0]` 是**最低**档，不能当成最高档使用。
- **官方档位名与 HDR 标记**来自 `playurl_info.playurl.g_qn_desc[]`：`qn` + `desc` + `hdr_desc`
  （HDR 不是独立的 qn，而是某个 qn 上的 `hdr_desc == "HDR"` 属性，显示时追加"（HDR）"）。
- 请求的 `qn` 与实际生效档位（`codec[].current_qn` 或请求值）写入 `SelectedQualityKey`；
  用户选的键不在可用列表里时回退到最高档并记 `Warn`。
- 匿名请求通常只能拿到较低档位（例如 1080P 原画）；要拿 4K/HDR/杜比需要在设置里填自己的 `SESSDATA`。

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
| 被拒绝 | 风控码 `code ∈ {-352, -412, -509}`（房间信息接口失败时降级，播放接口失败时归类为被拒绝） |
| 解析错误 | `code != 0`（带平台 `message`）、关键字段缺失、响应不是 JSON |
| 网络错误 | 由 `HttpTextClient` 抛出 |

> 降级顺序：房间信息接口被风控 → 先试 `getRoomBaseInfo` 取主播名与标题（拿到就用），
> 再只用播放接口判定开播状态；两条元数据通道都不可用时主播名与标题显示为占位文本，
> 但**不会**把可用房间误报成"房间号不存在"。

## Cookie 说明

- Cookie 仅通过 `Cookie` 请求头注入到 `api.bilibili.com` / `api.live.bilibili.com`；
- **不写入日志**（只记录 `Redacted` 形式）；保存在 `%LOCALAPPDATA%\StreamPilot\config.json`；
- Cookie 无效不会导致解析失败（匿名仍可拿到低画质线路）。

## 已知限制

- 未实现 WBI 签名：`getRoomPlayInfo` 为公开接口，匿名可访问；若平台改为强制签名，本解析器会返回 `ParseError` 并记录缺失字段；
- 不解析重播源（`live_status == 2` 直接返回轮播提示）。

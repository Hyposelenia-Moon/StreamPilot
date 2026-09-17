# 抖音（douyin）解析器

实现：`src/StreamPilot.Parsers/Douyin/DouyinParser.cs`
优先级：**P0** ｜ 平台：`PlatformId.Douyin` ｜ 链接前缀：`https://live.douyin.com/`

## 输入

- 房间号（`live.douyin.com/{web_rid}`，通常为数字，也接受字母数字短号）；
- 直播间链接。

## 请求序列

| 步骤 | 方法与地址 | 说明 |
|------|-----------|------|
| 1（主路径） | `GET https://live.douyin.com/{roomId}`，`Referer: https://live.douyin.com/` | 正则抽取页面内嵌状态：`\{\\"state\\":(.+?\}),\\"children\\":` |
| 2（回退路径） | `GET https://webcast.amemv.com/webcast/room/reflow/info/?type_id=0&live_id=1&room_id={roomId}` | 主路径无可用地址时使用 |

主路径解析 `roomStore.roomInfo`：`anchor.nickname` 为主播名、`anchor` 为 `null` → 房间不存在、
`room` 为 `null` → 未开播、`room.title` 为标题、`room.status == 4` → 未开播。

## 候选映射

- FLV：`room.stream_url.flv_pull_url`，画质优先 `FULL_HD1` → `HD1` → `SD1` → `SD2`；
- HLS：`room.stream_url.hls_pull_url_map`，同样的画质顺序 → `StreamFormat.HlsTs`；
- `CdnHost` 取 URL 主机名，`HttpReferer = https://live.douyin.com/`；
- 分类：`partition_road_map.sub_partition.partition.title`，否则 `partition.title`。

参考项目会产出空字符串候选（`vec![flv.unwrap_or_default(), hls.unwrap_or_default()]`），本项目**丢弃空地址**。

## 失败判定

| 状态 | 判定 |
|------|------|
| 房间不存在 | 页面状态中 `anchor` 为 `null`（回退路径 `data.data.user` 缺失同样处理） |
| 未开播 | `room` 为 `null`，或 `status == 4`，或 `stream_url` 为 `null`，或最终候选数为 0 |
| 解析错误 | 页面与 reflow 都无法提供状态；关键字段（标题/主播名）缺失 |
| 网络错误 | 由 `HttpTextClient` 抛出 |

## 明确不支持的能力（合规约束）

- **不实现** `a_bogus`、`ms_token`、`__ac_nonce`/`ttwid` 等请求签名与风控参数
  （参考项目中这些代码本身也被注释掉、未编译）。这属于绕过平台风控，`CLAUDE.md` 红线 3 明令禁止。
- 因此当抖音返回精简页面（无 `roomStore`）时，回退 `reflow` 接口；两者都失败则返回 `ParseError`，
  提示用户改用 mpv 或稍后重试。
- 不解析 `RENDER_DATA` 之外的其它混淆结构，避免随平台前端构建变化而长期失修。

## 画质与编码

- 画质由 `FULL_HD1/HD1/SD1/SD2` 映射（`QualityNames.FromDouyinQualityName`）；
- 抖音部分直播间为 HEVC：编码按 `Unknown` 之外的已知值填充，若 Web 端无法解码，
  播放页会提示使用「mpv 播放」（mpv 支持 HEVC 硬解 + 超分）。

## 画质档位（`Qualities`）

- 档位来源优先 `options.qualities[]`（`sdk_key` + `name` + `v_bit_rate`），
  缺失时退回 `flv_pull_url` / `hls_pull_url_map` / `stream_url` 的键名；
- 官方档位名：`origin`=原画、`uhd`=蓝光、`hd`=超清、`sd`=高清、`ld`=标清；
  老键名（`FULL_HD1`/`HD1`/`SD1`/`SD2`）按同义档位处理，键名原样作为 `QualityOption.Key`；
- 抖音是六个平台里**唯一在接口里直接给出码率**的（`v_bit_rate`），填入 `BitrateKbps`
  （若字段单位是 bps 则换算为 kbps）；
- `PreferredQualityKey` 命中时取该档；否则按"最高档在前"取第一个可用档，
  并且**只把选中档位的地址放进候选列表**（避免播放页选中的档位被其它档位顶掉）。

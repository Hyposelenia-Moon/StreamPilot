# 抖音（douyin）解析器

实现：`src/StreamPilot.Parsers/Douyin/DouyinParser.cs`
优先级：**P0** ｜ 平台：`PlatformId.Douyin` ｜ 链接前缀：`https://live.douyin.com/`

## 输入

- 房间号（`live.douyin.com/{web_rid}`，通常为数字，也接受字母数字短号）；
- 直播间链接。

链接里没有房间号时（分享链接常见的「首页 + 参数」形式
`https://live.douyin.com/?live_web_rid=...`），依次读查询参数
`live_web_rid` / `web_rid` / `room_id` / `roomid`，只接受纯数字值。

## 请求序列

| 步骤 | 方法与地址 | 请求头 | 说明 |
|------|-----------|--------|------|
| 1（主路径） | `GET https://live.douyin.com/{roomId}` | `Referer: https://live.douyin.com/` | 整页只抓一次：主路径抽内嵌状态，备用路径顺带取主播 `sec_uid` |
| 2（回退路径） | `GET https://webcast.amemv.com/webcast/room/reflow/info/?type_id=0&live_id=1&room_id={roomId}[&sec_user_id={secUid}]&version_code=99.99.99&app_id=1128` | `Referer: https://live.douyin.com/` | 主路径无可用地址时使用 |

### 主路径：内嵌状态抽取（不用正则截断）

抖音在不同时期把房间状态放在不同位置，三种形态都要能处理：

| 形态 | 页面写法 | 处理 |
|------|---------|------|
| 原始 JSON | 直接内联的 `{"roomStore":{...}}` | 按大括号配对切出对象 |
| RENDER_DATA | `<script id="RENDER_DATA" ...>` 内为百分号编码 JSON | 先 `decodeURIComponent` 语义解码，再按大括号配对 |
| React Flight | `self.__pace_f.push([1,"{\"roomStore\":...}"])`，JSON 被转义进 JS 字符串 | 按大括号配对切出后做一次 JS 反转义 |

实现要点（对应 `TrySliceRoomStoreJson` / `FindEnclosingObjectStart` / `SliceBalancedObject` /
`IsLogicalQuote` / `UnescapeJavaScriptString`）：

- 逐个 `roomStore` 出现位置尝试，只有「能解析」且「真的含 `roomStore.roomInfo` 对象」才接受，
  因此页面里恰好出现 `roomStore` 字样的干扰片段（字符串值、变量名）会被跳过；
- 括号配对用字符状态机而不是 `.+?` 正则，避免值里含 `{`/`}` 时被截断；
- 引号判定按"前面连续反斜杠个数"做，不用全局 `replace("\\\"", "\"")`：
  - 原始 JSON：前面有偶数个反斜杠的引号才是定界符；
  - JS 字符串形态（JSON 引号写作 `\"`、JSON 反斜杠写作 `\\`）：按周期 4 判定，
    `n ≡ 1 (mod 4)` 才是裸引号，`n = 0` 是 JS 字符串自身的边界，`n ≡ 3 (mod 4)`（如 `\\\"`）是 JSON 里的转义引号；
- JS 反转义只处理 JSON 会用到的转义（`\"` `\\` `\/` `\b` `\f` `\n` `\r` `\t` `\uXXXX`），
  二次转义 `\\uXXXX` 会还原成 `\uXXXX` 交给 `JsonDocument` 处理。

主路径解析 `roomStore.roomInfo`：`anchor.nickname` 为主播名、`anchor` 为 `null` → 房间不存在、
`room` 为 `null` → 未开播、`room.title` 为标题、`room.status == 4` → 未开播。

### 回退路径：reflow 必须补齐公开参数

实测把参数写全之前的调用会被平台拒绝：

```json
{"data":{"message":"Request params error"},"status_code":10011}
```

参考实现（MultiLive v10.4.16，`MultiLiveLowLatency.dll` 里的 `DouyinLiveResolver`）
的写法是拼接三段字符串：`...&room_id=` + roomId + `&sec_user_id=` + secUid +
`&version_code=99.99.99&app_id=1128`，并带 `Referer: https://live.douyin.com/`。
本实现按同样的公开参数拼接（`BuildReflowUrl`）。

**该接口在补齐这三个公开参数后不需要任何平台签名**（参考实现就是匿名调用）。
因此这里不存在"看起来能用其实必失败"的分支；如果平台后续改成强制签名，
本实现只会把它归类为 `Rejected` 并提示改用官方直播页，**不会**去实现签名。

`sec_user_id` 是可选的：页面里取不到时只带 `room_id`（参考实现也有"只用 room_id"的分支）。
`sec_uid` 抽取同时兼容未转义（`"sec_uid":"..."`）与转义（`\"sec_uid\":\"...\"`）两种页面写法。

reflow 响应里的房间数据对象按 `data.data`、`data` 两种包装兼容读取。

## 候选映射

- FLV：`room.stream_url.flv_pull_url`，画质优先 `FULL_HD1` → `HD1` → `SD1` → `SD2`；
- HLS：`room.stream_url.hls_pull_url_map`，同样的画质顺序 → `StreamFormat.HlsTs`；
- `CdnHost` 取 URL 主机名，`HttpReferer = https://live.douyin.com/`；
- 分类：`partition_road_map.sub_partition.partition.title`，否则 `partition.title`。

参考项目会产出空字符串候选（`vec![flv.unwrap_or_default(), hls.unwrap_or_default()]`），本项目**丢弃空地址**。

## 失败判定

四类判定互斥，`ResolveException.Message`（detail）里都带具体上下文：

| 状态 | 判定依据 | detail 示例 |
|------|---------|------------|
| 房间不存在 | 页面状态里 `anchor` 为 `null`；reflow 里 `data.user` 缺失 | `抖音未找到房间 123456：响应缺少主播信息，房间可能不存在或已注销。` |
| 未开播 | `room` 为 `null`（页面/reflow 各自文案），或 `room.status == 4`，或 `stream_url` 不是对象，或最终候选数为 0 | `抖音直播间已结束（来源=room-state，room.status=4）。` |
| 平台拒绝 | reflow 根级 `status_code` 非 0（数值或数字字符串都识别）；或页面是风控/验证码中间页且两条路径都没数据 | `抖音备用接口拒绝了本次请求：status_code=10011，message=Request params error。该接口按公开参数调用，不做平台签名。` |
| 解析错误 | 页面与 reflow 都拿不到可解析状态；关键字段（主播名）缺失；响应不是合法 JSON | `抖音直播间页面与 reflow 接口均未返回可用播放地址。` |
| 网络错误 | 由 `HttpTextClient` 抛出（超时、连接失败、HTTP 4xx/5xx） | — |

主路径"抽不出内嵌 JSON"只记 Debug 并继续走 reflow，**不会**被当成"未开播"；
reflow 被平台拒绝时**不会**回落到通用的"解析错误"。
页面命中风控标记（`__ac_nonce`）时，只有两条路径都拿不到数据才按 `Rejected` 报出，
提示文案明确说明本程序不绕过验证码。

注意一处**有意保留**的行为：页面里的 `roomStore.roomInfo.room` 存在、但 `stream_url` 缺失或为 `null` 时，
判为 `NotLive` 而**不**回退 reflow。原因是"未开播"是抖音匿名页最常见的结果，
若让这条路径依赖 reflow 的可用性，一旦 reflow 参数/风控出问题，离线房间就会被误报成"平台拒绝"
（比现状更难排查）。真正需要回退的是"页面里根本没有可解析的 roomStore"这种结构性变化。

## 明确不支持的能力（合规约束）

- **不实现** `a_bogus`、`ms_token`、`__ac_nonce`/`ttwid`、`__ac_signature` 等请求签名与风控参数。
  这属于绕过平台风控，`CLAUDE.md` 红线 3 明令禁止。
- 页面含验证码/风控标记（`__ac_nonce`）时不尝试任何绕过；两条公开路径都拿不到数据时，
  按 `Rejected` 给出可读提示（提示里明确说明本程序不绕过验证码）。
- 两条路径都失败则按上面的分类返回，提示用户改用官方直播页 / mpv 播放。
- 不解析 `RENDER_DATA`/`roomStore` 之外的其它混淆结构，避免随平台前端构建变化而长期失修。

## 画质与编码

- 画质由 `FULL_HD1/HD1/SD1/SD2` 映射（`QualityNames.FromDouyinQualityName`）；
- 抖音部分直播间为 HEVC：编码按 `Unknown` 之外的已知值填充，若 Web 端无法解码，
  播放页会提示使用「mpv 播放」（mpv 支持 HEVC 硬解 + 超分）。

## 画质档位（`Qualities`）

- 档位来源优先 `options.qualities[]`（`sdk_key` + `name` + `v_bit_rate`），
  缺失时退回 `flv_pull_url` / `hls_pull_url_map` / `stream_url` 的键名；
- 官方档位顺序（从高到低）：`origin` 原画 → `uhd` 蓝光 → `hd` 超清 → `sd` 高清 → `ld` 标清；
  `real_origin`（真原画）紧随 `origin`，纯音频档 `ao` 排在最后（避免有视频档时被当成最高档）；
  老键名（`FULL_HD1`/`HD1`/`SD1`/`SD2`）按同义档位处理，键名原样作为 `QualityOption.Key`；
- `QualityOption.Label` 优先用平台给的 `options.qualities[].name`，
  缺失时才用上面的内置中文名；
- 抖音是六个平台里**唯一在接口里直接给出码率**的（`v_bit_rate`），填入 `BitrateKbps`
  （若字段单位是 bps 则换算为 kbps）；
- `PreferredQualityKey` 命中时取该档；否则按"最高档在前"取第一个可用档，
  并且**只把选中档位的地址放进候选列表**（避免播放页选中的档位被其它档位顶掉）。

## 未确认 / 已知风险

- reflow 是否在所有地区、所有房间都不需要签名：只在参考实现与本次实测范围内确认"补齐公开参数即可"，
  未做跨地区验证；
- `app_id=1128` / `version_code=99.99.99` 的语义未由官方文档确认，直接沿用参考实现的取值；
- 页面请求只带 `Referer`，未带 `Origin`（参考实现里 `https://live.douyin.com/` 出现 3 次，
  无法从 IL 确认是否其中一次是 `Origin`；如遇风控可再评估）；
- 页面内嵌片段里的字符串值含裸双引号（JS 源码里写作 `\\\"`）时已按周期 4 正确处理，
  但 `\\` + `"`（JS 字符串提前结束）这种畸形写法仍可能让该候选被丢弃并回退 reflow。

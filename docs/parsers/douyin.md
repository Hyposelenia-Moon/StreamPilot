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
| 1（主路径） | `GET https://live.douyin.com/{roomId}` | `Referer: https://live.douyin.com/` + 用户自备 Cookie（若已配置） | 整页只抓一次：主路径抽内嵌状态，备用路径顺带取主播 `sec_uid` |
| 2（回退路径 A） | `GET https://live.douyin.com/webcast/room/web/enter/?aid=6383&app_name=douyin_web&live_id=1&device_platform=web&language=zh-CN&enter_from=web_live&cookie_enabled=true&screen_width=1920&screen_height=1080&browser_language=zh-CN&browser_platform=Win32&browser_name=Chrome&browser_version=131.0.0.0&web_rid={roomId}` | `Referer: https://live.douyin.com/{roomId}` + `Cookie: {用户自备 Cookie}; ttwid={首页下发的会话 cookie}` | 主路径没有可解析状态时使用 |
| 2.0（取会话 cookie） | `GET https://live.douyin.com/` | 无 | 只读 `Set-Cookie` 里的 `ttwid`，内存缓存 30 分钟；**这是服务器给每个访客下发的普通会话 cookie，不是签名** |
| 3（回退路径 B） | `GET https://webcast.amemv.com/webcast/room/reflow/info/?type_id=0&live_id=1&room_id={roomId}[&sec_user_id={secUid}]&version_code=99.99.99&app_id=1128` | `Referer: https://live.douyin.com/` + 用户自备 Cookie（若已配置） | 前两条都失败时的最后兜底；实测固定返回 `10011` |

### Cookie 作用域（用户自备 Cookie 必须覆盖全部解析请求）

用户自备 Cookie 通过 `HttpTextClient.UseCookie(query.Cookie)` 设置成 **AsyncLocal 作用域**，
`DouyinParser.OnParseAsync` 在进入时建立该作用域，作用域内**所有** `HttpTextClient` 请求都会自动带上它
（页面、进房、reflow 三条路径都覆盖）。

**实测根因（本轮的修复点）**：进房接口的请求头里过去直接写 `Cookie: ttwid=...`，
而 `HttpTextClient` 的规则是"请求头已显式带 Cookie 就不再补作用域 Cookie"（避免出现两个 Cookie 头）。
结果是**用户自备的登录态在进房请求里被整段丢掉**，而进房接口正是抖音回退路径里唯一给出可用
`stream_url` 的通道：登录态缺失时平台不下发需要登录的最高档（`origin` 原画），
用户看到的现象就是"抖音能播，但没有原画档"。

现在的做法：`BuildRoomEnterCookie(ttwid)` 把
- 用户自备 Cookie（去掉其中的 `ttwid=` 名值对，避免同名重复）放在前面，
- 首页下发的 `ttwid=` 放在最后，

拼成一个 Cookie 头（`Cookie: <用户 Cookie>; ttwid=<...>`），两者同时生效。
全程不涉及任何签名，返回值属于登录凭证，**禁止写入日志**。

### 档位诊断日志

`BuildQualityOptions` 每次都会记一条 `Debug` 级诊断，直接给出本次响应里档位键的出处：

| 字段 | 含义 |
|------|------|
| `declaredQualities` | `options.qualities[].sdk_key` 给出的键（可能为空） |
| `flvPullUrl` | `stream_url.flv_pull_url` 的键集合 |
| `hlsPullUrlMap` | `stream_url.hls_pull_url_map` 的键集合 |
| `streamUrlDirect` | 直接挂在 `stream_url` 上的已知档位键 |
| `hasOrigin` | 上述任一处是否出现 `origin`（"真无 origin"还是"有但没取到"一看即知） |

只记键名，**不记地址与查询参数**（地址里带签名）。排查"没有原画档"时打开
设置 → 高级 → 「记录详细诊断日志」，然后在日志里搜 `抖音档位诊断`。

> 本机开发环境无法访问 `live.douyin.com`（网络受限），因此本条诊断**尚未在真实在播房间上取到原始输出**；
> 代码路径与键集合口径已按线上结构实现并被单测覆盖（见文末"未确认 / 已知风险"）。

### 主路径：内嵌状态抽取（不用正则截断）

抖音在不同时期把房间状态放在不同位置，三种形态都要能处理：

| 形态 | 页面写法 | 处理 |
|------|---------|------|
| 原始 JSON | 直接内联的 `{"roomStore":{...}}` | 按大括号配对切出对象 |
| RENDER_DATA | `<script id="RENDER_DATA" ...>` 内为百分号编码 JSON | 先 `decodeURIComponent` 语义解码，再按大括号配对 |
| React Flight | `self.__pace_f.push([1,"{\"roomStore\":...}"])`，JSON 被转义进 JS 字符串 | 按大括号配对切出后做一次 JS 反转义 |

实现要点（对应 `TrySliceRoomStoreJson` / `FindEnclosingObjectStart` / `SliceBalancedObject` /
`IsLogicalQuote` / `UnescapeJavaScriptString`）：

- 逐个 `roomStore` 出现位置尝试，只有「能解析」且「真的含**非空**的 `roomStore.roomInfo` 对象」才接受，
  因此页面里恰好出现 `roomStore` 字样的干扰片段（字符串值、变量名）会被跳过；
- 括号配对用字符状态机而不是 `.+?` 正则，避免值里含 `{`/`}` 时被截断；
- 引号判定按"前面连续反斜杠个数"做，不用全局 `replace("\\\"", "\"")`：
  - 原始 JSON：前面有偶数个反斜杠的引号才是定界符；
  - JS 字符串形态（JSON 引号写作 `\"`、JSON 反斜杠写作 `\\`）：按周期 4 判定，
    `n ≡ 1 (mod 4)` 才是裸引号，`n = 0` 是 JS 字符串自身的边界，`n ≡ 3 (mod 4)`（如 `\\\"`）是 JSON 里的转义引号；
- JS 反转义只处理 JSON 会用到的转义（`\"` `\\` `\/` `\b` `\f` `\n` `\r` `\t` `\uXXXX`），
  二次转义 `\\uXXXX` 会还原成 `\uXXXX` 交给 `JsonDocument` 处理。

#### 必须跳过空的 `roomStore` 外壳（实测根因）

**用户实测房间 `https://live.douyin.com/745964462470` 的页面里有两次 `roomStore`**：

| 出现位置 | 所在的 `<script>` | `roomStore.roomInfo` |
|----------|-------------------|----------------------|
| 第 1 次（HTML 偏移约 108511） | `self.__pace_f.push([1,"2:[\"$\",\"$L3\",null,{\"odin\":{...` | `{}`（**空壳**） |
| 第 2 次（HTML 偏移约 907854） | `self.__pace_f.push([1,"d:[\"$\",\"$L12\",null,{\"state\":{\"appStore\":...` | `{"room":{"id_str":"7376083140344859455","status":4,"status_str":"4","title":"…","user_count_str":"0",…},"roomId":"…","web_rid":"745964462470","anchor":{"nickname":"喜剧电影笑不停"},"enter_mode":0,"qrcode_url":"","partition_road_map":{},"web_stream_url":null,"auth_cert_info":""}` |

旧实现只要求 `roomInfo` 是 JSON 对象就接受，于是**第一个空壳被当成房间状态**，
`anchor` 取不到 → 被归类成 `RoomNotFound`（"房间不存在"），这就是"抖音一直识别失败"的直接原因。
现在 `HasAnyProperty` 要求 `roomInfo` 非空，空壳会被跳过并继续扫描到第 2 次。

> 房间确实不存在时（实测 `https://live.douyin.com/99999999999999999999`）第 2 次的
> `roomInfo` 为 `{"web_rid":…,"web_stream_url":null}`，仍然没有 `anchor`，
> 因此 `RoomNotFound` 的分类保持不变，只是不再由空壳触发。

主路径解析 `roomStore.roomInfo`：`anchor.nickname` 为主播名、`anchor` 为 `null` → 房间不存在、
`room` 为 `null` → 未开播、`room.title` 为标题、`room.status == 4` → 未开播。

### 回退路径 A：网页端进房接口（实测可用，不需要签名）

实测命令（Node，仅 UA + `Referer` + 首页下发的 `ttwid`）：

```
GET https://live.douyin.com/                                                   # 取 Set-Cookie: ttwid=...
GET https://live.douyin.com/webcast/room/web/enter/?aid=6383&app_name=douyin_web&live_id=1
    &device_platform=web&language=zh-CN&enter_from=web_live&cookie_enabled=true
    &screen_width=1920&screen_height=1080&browser_language=zh-CN&browser_platform=Win32
    &browser_name=Chrome&browser_version=131.0.0.0&web_rid=745964462470
Cookie: ttwid=<首页下发值>       Referer: https://live.douyin.com/745964462470
```

原始响应（HTTP 200，截断）：

```json
{"data":{"data":[{"id_str":"7376083140344859455","status":4,"status_str":"4",
"title":"女性励志剧《俺娘田小草》直播中~快进来看啊！","user_count_str":"0",…}],
"enter_room_id":"7376083140344859455",
"user":{"id_str":"2208256072094896","sec_uid":"MS4wLjABAAAA…","nickname":"喜剧电影笑不停",…},
"qrcode_url":"","enter_mode":0,"room_status":2,"partition_road_map":{},"similar_rooms":[],…},
"extra":{"now":1789646241503},"status_code":0}
```

要点：

- 取房间对象用 `data.data[0]`（数组，兼容 `data.room` 对象形态），主播名用 `data.user.nickname`，
  平台状态用 `status_code`；房间对象与主路径的 `room` 完全同构，因此候选/档位解析共用同一套代码；
- **缺 `ttwid` 时该接口返回「HTTP 200 + 空正文」**（实测：不带 cookie、带伪造的 `ttwid`、带伪造的 `msToken`
  都是 0 字节；带首页真实 `ttwid` 才返回 1358 字节 JSON）。程序把空正文识别为"该路径不可用"并继续降级，
  不会误报成解析错误；
- `ttwid` 由 `DouyinWebSession` 用 `HttpClientFactory`（复用超时与代理配置）请求一次首页取得，
  内存缓存 30 分钟，**cookie 值永不写入日志**（只记录长度）；
- 该接口全程不需要 `a_bogus` / `ms_token` / `__ac_signature`。

### 回退路径 B：reflow（实测已被平台拒绝）

即使按参考实现补齐 `version_code=99.99.99&app_id=1128` 与 `sec_user_id`，实测仍然固定返回：

```json
{"data":{"message":"Request params error","prompts":"Request params error\t"},"extra":{"now":1789646013927},"status_code":10011}
```

该接口只作为最后兜底；被拒时归类为 `Rejected` 并提示改用官方直播页，**不会**去实现签名。

`sec_user_id` 是可选的：页面里取不到时只带 `room_id`。
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
| 房间不存在 | 页面状态里 `anchor` 为 `null`；进房接口里 `data.user` 缺失 | `抖音未找到房间 123456：响应缺少主播信息，房间可能不存在或已注销。` |
| 未开播 | `room` 为 `null`（页面/进房/reflow 各自文案），或 `room.status == 4`，或 `stream_url` 不是对象，或最终候选数为 0 | `抖音直播间已结束（来源=room-state，room.status=4）。` |
| 平台拒绝 | 进房/reflow 根级 `status_code` 非 0（数值或数字字符串都识别）；或页面是风控/验证码中间页且所有路径都没数据 | `抖音进房接口拒绝了本次请求：status_code=10011，message=Request params error。该接口按公开参数调用，不做平台签名。` |
| 解析错误 | 三条路径都拿不到可解析状态；关键字段（主播名）缺失；响应不是合法 JSON | `抖音直播间页面与 reflow 接口均未返回可用播放地址。` |
| 网络错误 | 由 `HttpTextClient` 抛出（超时、连接失败、HTTP 4xx/5xx） | — |

主路径"抽不出内嵌 JSON"只记 Debug 并继续走进房接口，**不会**被当成"未开播"；
进房接口返回空正文（缺 `ttwid`）只记 Warn 并继续降级；
被平台拒绝时**不会**回落到通用的"解析错误"。
页面命中风控标记（`__ac_nonce`）时，只有所有路径都拿不到数据才按 `Rejected` 报出，
提示文案明确说明本程序不绕过验证码。

注意一处**有意保留**的行为：页面里的 `roomStore.roomInfo.room` 存在、但 `stream_url` 缺失或为 `null` 时，
判为 `NotLive` 而**不**回退 reflow。原因是"未开播"是抖音匿名页最常见的结果，
若让这条路径依赖 reflow 的可用性，一旦 reflow 参数/风控出问题，离线房间就会被误报成"平台拒绝"
（比现状更难排查）。真正需要回退的是"页面里根本没有可解析的 roomStore"这种结构性变化。

## 明确不支持的能力（合规约束）

- **不实现**任何请求签名：`a_bogus`、`ms_token`、`__ac_signature` 等一律不涉及。
  这属于绕过平台风控，`CLAUDE.md` 红线 3 明令禁止。
- 进房接口用到的 `ttwid` **不是签名**：它是 `https://live.douyin.com/` 对每个访客
  `Set-Cookie` 下发的普通会话 cookie（和浏览器首次打开该站点拿到的完全一样）。
  程序只做一次普通首页 GET 并复用该 cookie，不计算、不伪造任何值
  （实测伪造的 `ttwid` 会被平台拒绝并返回空正文）。
- 页面含验证码/风控标记（`__ac_nonce`）时不尝试任何绕过；所有公开路径都拿不到数据时，
  按 `Rejected` 给出可读提示（提示里明确说明本程序不绕过验证码）。
- 所有路径都失败则按上面的分类返回，提示用户改用官方直播页 / mpv 播放。
- 不解析 `RENDER_DATA`/`roomStore` 之外的其它混淆结构，避免随平台前端构建变化而长期失修。

## 画质与编码

- 画质按"新键名优先、老键名同义"的规则映射（`MapDouyinQuality`）：
  `origin`/`real_origin` → 1080P 高帧率档、`uhd`/`hd` → 1080P、
  `sd` → 720P、`ld`/`SD2` → 480P，老式 `FULL_HD1`/`HD1` 走 `QualityNames.FromDouyinQualityName`；
  面向用户显示的档位名一律以 `QualityOption.Label` 为准（它优先取平台自己的名字）；
- 抖音部分直播间为 HEVC：编码按 `Unknown` 之外的已知值填充，若 Web 端无法解码，
  播放页会提示使用「mpv 播放」（mpv 支持 HEVC 硬解 + 超分）。

## 画质档位（`Qualities`）

- 档位来源优先 `options.qualities[]`（`sdk_key` + `name` + `v_bit_rate`），
  缺失时退回 `flv_pull_url` / `hls_pull_url_map` / `stream_url` 的键名；
- **键优先级（从高到低，实现见 `KnownQualityKeyOrder`）**：
  `origin` → `real_origin` → `uhd` → `hd` → `sd` → `ld` → `FULL_HD1` → `HD1` → `SD1` → `SD2` → `ao`；
  纯音频档 `ao` 排最后，避免有视频档时被当成最高档；
- **「原画」这一档只能来自 `origin`**：内置中文名映射里只有 `origin` → `原画`，
  `real_origin` → `真原画`（见 `DescribeQualityKey`）。平台没有下发 `origin` 时不会伪造该档；
- `QualityOption.Label` 优先用平台给的 `options.qualities[].name`（平台口径优先），
  缺失时才用上面的内置中文名；
- 抖音是六个平台里**唯一在接口里直接给出码率**的（`v_bit_rate`），填入 `BitrateKbps`
  （若字段单位是 bps 则换算为 kbps）；
- `PreferredQualityKey` 命中时取该档；否则按"最高档在前"取第一个可用档，
  并且**只把选中档位的地址放进候选列表**（避免播放页选中的档位被其它档位顶掉）。

## 未确认 / 已知风险

- **"原画档缺失"的最终确认需要真实在播房间**：本轮修复了"C注入作用域被进房请求的显式 Cookie 顶掉"
  这一确定缺陷，并补齐了档位键诊断日志；但本机开发环境无法访问 `live.douyin.com`，
  因此**没有在真实在播房间上取到 `options.qualities` / `flv_pull_url` / `hls_pull_url_map` 的原始键集合**。
  判定口径：打开详细诊断日志后看 `hasOrigin`——`false` 表示平台确实没下发 `origin`（属平台侧限制，
  需要登录态或该房间本身没有原画），`true` 表示有 `origin` 但未被采用（属解析缺陷，请附日志反馈）；
- **"在播房间的 `stream_url` 未直接观测到"**：本文所有响应片段都来自当前处于**已结束**状态
  （`status=4`）的房间，因此只验证到"能明确判定未开播 / 房间不存在"。
  在播房间的 `stream_url` 结构与主路径 `room` 同构（`flv_pull_url` / `hls_pull_url_map`），
  解析代码与主路径共用同一函数，但**没有在播房间的原始响应作为直接证据**，请以实际解析结果为准；
- reflow 是否在所有地区都不可用：只在本机网络实测固定返回 `10011`，未做跨地区验证；
- `app_id=1128` / `version_code=99.99.99` 的语义未由官方文档确认，直接沿用参考实现（MultiLive v10.4.16）
  IL 里拼接的取值；
- 进房接口的 `ttwid` 有效期未知，程序按 30 分钟内存缓存；被平台提前失效时会返回空正文并自动降级；
- 页面请求只带 `Referer`，未带 `Origin`（参考实现里 `https://live.douyin.com/` 出现 3 次，
  无法从 IL 确认是否其中一次是 `Origin`；如遇风控可再评估）；
- 页面内嵌片段里的字符串值含裸双引号（JS 源码里写作 `\\\"`）时已按周期 4 正确处理，
  但 `\\` + `"`（JS 字符串提前结束）这种畸形写法仍可能让该候选被丢弃并回退进房接口。

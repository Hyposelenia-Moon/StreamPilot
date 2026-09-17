# ADR 0003：平台解析器实现与统一结果结构

- 状态：已接受
- 日期：2025-02-14
- 相关文档：`0002-architecture-layering.md`、`docs/parsers/*.md`

## 背景

需求要求支持 **B站、抖音、虎牙、斗鱼、YY、Bigo** 六个平台，优先级 P0（B站/抖音/虎牙）→ P1（斗鱼/YY）→ P2（Bigo），并且：

- 参考 `lsar-0.3.19/src-tauri/src/parsers/` 的实现思路（Rust → C# 重写）；
- 统一返回结构必须包含：**流地址、格式、CDN host、编码、源索引**；
- 解析失败必须区分：**未开播、房间不存在、轮播中、解析错误、网络错误**。

对参考项目 `lsar` 的完整分析（逐文件阅读 `src-tauri/src/parsers/**`）得到的事实：

1. `lsar` 的统一结构是 `ParsedResult { platform, title, anchor, roomID, category, links: Vec<String> }`，**不含**格式/CDN/编码/源索引，只有 `links` 有序数组表达"质量优先级"。
2. `lsar` 的失败分类是 `RoomStateError::{Offline, NotExists, IsReplay}` + `RequestError::BadRequest` + `MissKeyFieldError` + `HTTPError::{Connect, Timeout, Decode, Other}`，但 **YY/Bigo 完全没有状态检测**，且 `LsarError::Http` 是 `#[error(transparent)]`，导致前端按 `"http error: Connect"` 匹配的分支永远不命中。
3. `lsar` 的 HTTP 层**没有任何超时、重试、代理设置**，任何请求都可能永久挂起；`huya` 在未知 `liveStatus` 时走 `unreachable!()` 会 panic，`query["ctype"]`/`uid.parse()`/base64 解码都是 `unwrap` 风格。
4. 关键签名算法（虎牙 anticode、斗鱼 `getEncryption` 双层 MD5、B站 `qn=30000`）都在解析器内部完成，且 douyin 的 `a_bogus.rs`/`ms_token.rs` 在参考项目里**未被编译**（`mod` 被注释），实际可用的抖音路径是"直接 GET 房间页 + 正则抽 `roomStore` 状态"。

本 ADR 记录基于上述事实做出的取舍。

## 决策

### 1. 统一结果结构 `StreamCandidate`（满足"五字段"硬性要求）

```csharp
namespace StreamPilot.Core.Models;

/// <summary>平台解析产出的单个可播放/可录制流候选。</summary>
public sealed record StreamCandidate
{
    /// <summary>源索引：同一房间内候选的唯一序号，0 表示平台给出的最高优先级流。</summary>
    public required int SourceIndex { get; init; }

    /// <summary>完整流地址（可能带签名与过期参数，禁止写入日志）。</summary>
    public required string Url { get; init; }

    /// <summary>容器/传输格式。</summary>
    public required StreamFormat Format { get; init; }

    /// <summary>CDN 主机名，例如 <c>cn-jsnj-...</c>；无法判定时为 <see cref="UnknownHost"/>。</summary>
    public required string CdnHost { get; init; }

    /// <summary>视频编码。</summary>
    public required VideoCodec Codec { get; init; }

    /// <summary>画质档位（用于排序与展示）。</summary>
    public required StreamQuality Quality { get; init; }

    /// <summary>URL 指纹（长度 + 前缀哈希），仅用于日志与去重，不泄露签名。</summary>
    public required string UrlFingerprint { get; init; }

    /// <summary>有效期（UTC）；<see langword="null"/> 表示平台未声明。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
```

- `StreamFormat`：`FlvHttp`、`HlsTs`、`HlsFmp4`、`Rtmp`（`Rtmp` 仅用于判定"不可 Web 播放/不可直接录制"，见 ADR 0004）。
- `VideoCodec`：`Avc`、`Hevc`、`Av1`、`Unknown`。
- `Quality`：`Dolby`、`Uhd4K`、`Qhd2K`、`Hd1080HighFps`、`Hd1080`、`Hd720`、`Sd480`、`Unknown`（对应 B站 `qn` 档位）。
- 与 `lsar` 的差异：**显式携带五要素**，`SourceIndex` 由解析器按优先级从 0 递增赋值；`list` 顺序仍保持"优先播放的排在前面"，兼容前端"取第一个可播候选"的逻辑。

### 2. 失败分类：五个互斥类别 + 结构化上下文

```csharp
/// <summary>解析失败的分类，UI 据此给出不同提示。</summary>
public enum ResolveFailure
{
    /// <summary>房间存在但当前未开播。</summary>
    NotLive,
    /// <summary>房间号不存在或已注销。</summary>
    RoomNotFound,
    /// <summary>房间正在轮播/重播（不解析重播源）。</summary>
    Replaying,
    /// <summary>响应结构变化或字段缺失导致的解析错误。</summary>
    ParseError,
    /// <summary>网络错误（连接失败、超时、HTTP 4xx/5xx、响应体截断）。</summary>
    NetworkError,
    /// <summary>请求被平台拒绝（风控/非法请求），不重试不绕过。</summary>
    Rejected,
}
```

- 统一抛出 `ResolveException : Exception`，字段：`Failure`、`Platform`、`RoomId`、`Operation`（如 `getRoomPlayInfo`）、`Detail`（脱敏后的原因）。
- **不绕过风控**：遇到验证码/登录墙/风控挑战时抛 `Rejected`，绝不尝试破解（`CLAUDE.md` 红线 3）。
- 与 `lsar` 的差异：把 YY/Bigo 也纳入状态判定（YY/Bigo 无明确状态时按"无有效流地址 → `NotLive`，房间页 404/结构缺失 → `RoomNotFound`"处理），并把 `BadRequest` 归入 `Rejected`。

### 3. HTTP 层：显式超时 + 有界重试 + 脱敏日志

`Core.Http.HttpClientFactory` / `Core.Http.HttpTextClient`：

| 项 | 取值 | 说明 |
|---|---|---|
| 连接 + 响应超时 | 8 秒 | 通过 `CancellationTokenSource` 与 `HttpClient.Timeout` 双重保护 |
| 单次请求重试 | 最多 3 次（含首次） | 仅对"连接失败/超时/5xx/429"重试 |
| 重试退避 | 300ms / 900ms / 2000ms | 指数退避 + 抖动 ±20% |
| 重定向 | 最多 5 跳 | `AllowAutoRedirect = true`，禁止跨协议降级 |
| 代理 | 默认 `UseDefaultCredentials` 关闭、`IWebProxy = null` | 由 `config.json` 的 `network.proxy` 显式开启 |
| 默认 UA | `Chrome/123` Windows UA | 单一来源常量，禁止散落 |
| Cookie | 仅 B站按需注入，**不写日志** | `CookieContainer` 关闭，避免跨请求串号 |

重试实现要求：`RetryPolicy` 为纯函数（输入 `attempt` 输出延迟），便于单元测试，测试覆盖 300/900/2000 与"超过上限不重试"。

### 4. 解析器内部结构（每个平台一致的骨架）

```
PlatformParserBase (Core.Parsers)
  ├── ResolveAsync(PlatformId, RoomQuery, CancellationToken)   ← 模板方法
  │     ├── ValidateQuery()          : 房间号/URL 合法性（外部输入必须校验）
  │     ├── OnResolveAsync()         : 平台具体实现（子类覆写）
  │     └── WrapPlatformError()      : 平台原始错误 → ResolveFailure
  └── 公共工具：TryParseRoomIdFromUrl、ParseQuery、BuildFingerprint
```

- 每个平台一个类（`BilibiliParser`、`DouyinParser`、`HuyaParser`、`DouyuParser`、`YyParser`、`BigoParser`），内部按"取房间信息 → 取播放信息 → 组装候选"拆成 3~5 个私有方法，单方法不超过 300 行、嵌套不超过 5 层。
- 正则表达式集中为 `private static readonly Regex`（`RegexOptions.Compiled`），禁止在方法内重复构造。
- 单平台不引入额外依赖：MD5 用 `System.Security.Cryptography.MD5`，Base64 用 `Convert`，URL 解析用 `Uri` + 自研查询串解析（BCL 的 `HttpUtility` 在 WPF 工程可用，但为保持 `Parsers` 不依赖 `System.Web`，自研 `QueryStringParser`）。

### 5. 各平台实现要点（对参考项目的最小忠实移植 + 修正）

| 平台 | 关键端点 | 说明 |
|---|---|---|
| B站 | `api.bilibili.com/x/web-interface/nav`（Cookie 校验，可跳过）、`api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom`、`.../v2/index/getRoomPlayInfo?protocol=0,1&format=0,1,2&codec=0,1&qn=30000&platform=web&ptype=8&dolby=5&panorama=1` | 链接 = `host + base_url + extra`；`live_status` 0→`NotLive`、2→`Replaying`；`qn=30000` 取最高画质；缺 Cookie 时允许匿名解析（参考项目强制校验，会阻断无 Cookie 用户） |
| 抖音 | `live.douyin.com/{roomId}` 页面 + 正则抽 `roomStore` JSON；失败回退 `webcast.amemv.com/webcast/room/reflow/info/?type_id=0&live_id=1&room_id={id}[&sec_user_id={secUid}]&version_code=99.99.99&app_id=1128` | 画质 `FULL_HD1 → HD1 → SD1 → SD2`；`anchor == null` → `RoomNotFound`；`room == null` 或 `status == 4` → `NotLive`；**不实现** `a_bogus`/`ms_token`（参考项目自身未启用，且属于签名伪造，违反"不绕过风控"） |
| 虎牙 | `www.huya.com/{id}` 页面抽 `stream:` JSON → `mp.huya.com/cache.php?m=Live&do=profileRoom`；`udblgn.huya.com/web/anonymousLogin` 取 uid | `liveStatus`：`ON` / `OFF`→`NotLive` / `REPLAY`→`Replaying` / 其他→`ParseError`（**修掉参考项目的 `unreachable!()` panic**）；FLV 与 HLS 各产出候选；anticode 签名：`ss=md5(seqid|ctype|t)`、`wsSecret=md5(fm 替换 $0..$3)`、`uuid`、`ver=1`、`sv=2110211124` |
| 斗鱼 | `www.douyu.com/{id}` 页面、`/betard/{id}`（轮播检测）、`/wgapi/livenc/liveweb/websec/getEncryption?did=...`、`POST /lapi/live/getH5PlayV1/{id}` | `enc_time` 次 `md5(auth+key)` 后再 `md5(auth+key+signStr)`；`rate=-1`、`hevc=0`；`getH5PlayV1` 的 `rtmp_url`+`rtmp_live` 多数为 RTMP，若同时存在 `http_stream`/`hls` 字段优先取 HTTP 候选，否则返回 `Rtmp` 候选并由上层提示"该房间暂不支持 Web 播放/原始流录制" |
| YY | `www.yy.com/{id}` 页面正则 + `POST stream-manager.yy.com/v3/channel/streams?...` | `stream_line_addr.*.cdn_info.url` 全部作为候选；无状态字段，按"有地址即开播，无地址即未开播"判定 |
| Bigo | `POST ta.bigo.tv/official_website/studio/getInternalStudioInfo`（`siteId`） | `roomStatus` 参与判定（参考项目解析了但未使用）：非直播态→`NotLive`；`hls_src` 为唯一候选 |

### 6. 画质与 CDN 的推导

- **B站**：`qn` 从响应 `current_qn`/`accept_qn` 推导 `Quality`；CDN host 从 `url_info.host` 直接取值。
- **抖音**：`Resolution` 键名映射画质；CDN host 取 URL 的 `Host`。
- **虎牙**：`sFlvUrl` 的主机名即 CDN host；画质从 `bitRate`/`iBitRate` 字段（页面 JSON 内 `data[].gameStreamInfoList`）推导，缺失时 `Unknown`。
- **斗鱼/YY/Bigo**：CDN host 取 URL `Host`，画质 `Unknown`（除斗鱼 `rate` 参数已知时映射）。
- 所有 URL 的合法性在使用前校验：`Uri.TryCreate` + scheme 必须为 `http`/`https`/`rtmp`，非法候选直接丢弃并记 `Warn`。

### 6.1 画质档位（`QualityOption`）

`StreamQuality` 只是"粗档位枚举"，不足以表达各平台官方档位（B站 杜比/4K/HDR、虎牙 蓝光20M/2K HDR、抖音 原画…）。因此在 Core 增加：

- `QualityOption { Key, Label, BitrateKbps?, IsBest }`：`Key` 由平台解释（B站 `qn` 数值、虎牙码率、斗鱼 `rate`、抖音拉流键），`Label` 是**面向用户的官方档位名**；
- `RoomQuery.PreferredQualityKey`：调用方（UI）指定的档位；为空表示"平台最高档"；
- `ResolvedRoom.Qualities` / `SelectedQualityKey`：本次可选的档位列表与实际生效档位；
- `PlaybackPlan.Qualities` / `SelectedQualityKey`：随播放计划下发给页面，页面渲染画质下拉；用户改档时页面回 `quality` 消息，宿主按新档位重新解析并重新下发计划。

约束：

1. **档位键与地址通常绑定**：换档必须重新请求平台接口（虎牙可以复用签名后追加 `ratio`，斗鱼/B站必须重取），因此不允许在候选列表里混入多档位地址；
2. 解析器对未知键必须**回退到最高档**并记 `Warn`，而不是失败；
3. 档位名以平台返回的名称为准，平台没给名字时才使用内置中文映射（避免自造档位名误导用户）；
4. 未确认语义的档位（例如 YY `gear`）只在列表里放一项"默认（平台给定）"，不在文档或 UI 中宣称它等于某个画质。

### 7. URL 有效期校验（`CLAUDE.md` 硬性要求）

- `CandidateValidity.EnsureUsable(candidate, now)`：若 `ExpiresAt` 已过 → 抛 `ResolveException(NetworkError, "候选流地址已过期")`；无法判定有效期时允许使用，但在 `PlaybackCoordinator` 中以"首帧/探测失败即切换候选"兜底。
- B站/虎牙签名 URL 的过期参数（`expires`/`wsTime`/`txTime`）解析后写入 `ExpiresAt`（取最小有效值）。

## 影响

### 正面

- 六个平台共享同一契约，UI 与录制层对平台无感知。
- 失败类别互斥且可测试：每个解析器至少覆盖"正常/未开播/房间不存在/轮播/网络错误/解析错误"六类用例。
- 修掉了参考项目三个已知问题：无超时（会挂死）、`unreachable!` panic、错误分类与前端提示不一致。

### 负面 / 风险

- 平台接口随时可能变化，`ParseError` 会随之上身；已通过"解析错误消息包含缺失字段名 + 端点名"提高可诊断性。
- 抖音未实现 `a_bogus`：部分情况下页面可能返回精简 HTML 导致 `ParseError`；备选是 `reflow` 端点回退（已实现）。这与"不绕过风控"红线一致。
- 斗鱼主路径是 RTMP，Web 端不可播。属于平台能力限制，UI 明确提示并建议使用 mpv 外挂播放（mpv 支持 RTMP）。

## 后续

- 若新增平台（如快手），需要先更新本 ADR 的优先级表并向用户确认。
- 若平台要求签名（`a_bogus` 类），必须先向用户确认合规性，再决定是否支持。

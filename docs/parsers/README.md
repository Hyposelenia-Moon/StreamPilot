# 平台解析器总览

六个平台的解析器放在 `src/StreamPilot.Parsers/<平台>/<平台>Parser.cs`，
全部实现 `StreamPilot.Core.Services.IPlatformParser`，并继承 `StreamPilot.Core.Parsers.PlatformParserBase`。

## 统一契约

```csharp
public interface IPlatformParser
{
    PlatformId Platform { get; }
    string DisplayName { get; }
    string RoomUrlPrefix { get; }
    Task<ResolvedRoom> ParseAsync(RoomQuery query, CancellationToken cancellationToken);
}
```

- 解析成功返回 `ResolvedRoom`，候选列表为 `IReadOnlyList<StreamCandidate>`；
- 解析失败**必须**抛出 `ResolveException`，携带 `ResolveFailure` 分类与出错的 `Operation`；
- 基类统一负责：输入校验（房间号格式 / 链接域名）、空候选 → `NotLive`、未预期异常 → `ParseError`（带 `InnerException`）。

`StreamCandidate` 的五个硬性字段：`Url`、`Format`、`CdnHost`、`Codec`、`SourceIndex`。
URL 指纹由 `StreamCandidateBuilder` 统一生成，解析器不得自行拼接日志用的 URL。

## 画质档位契约（`QualityOption`）

除候选流外，解析器还要在 `ResolvedRoom.Qualities` 里给出**平台官方画质档位**，并按
`RoomQuery.PreferredQualityKey` 取对应档位（为空 = 平台最高档）：

- `QualityOption.Key` 由平台自己解释（B站 `qn` 数值、虎牙码率、斗鱼 `rate`、抖音拉流键、YY `gear`、Bigo `default`）；
- `QualityOption.Label` 必须是**面向用户的档位名**（官方叫什么就写什么，例如「蓝光20M」「4K 原画」）；
- `BitrateKbps` 平台给了就填，没给留 `null`（不要编造）；
- 用户传入的键不在可用列表里时**回退最高档 + Warn 日志**，不允许因此失败；
- 档位与地址通常绑定（B站 qn、斗鱼 rate 必须重新请求接口；虎牙可在签名后追加 `ratio`），
  因此候选列表里**不允许混入多个档位的地址**；
- 语义未经证实的档位（例如 YY `gear`）只放一项"默认（平台给定）"，不得宣称它等于某档画质。

详见 [ADR 0003](../adr/0003-parser-contract.md) 第 6.1 节。

## 用户自备 Cookie（`RoomQuery.Cookie`）

- 用户在「设置 → 高级 → 账号与 Cookie」里按平台填写，宿主调用解析时放进 `RoomQuery.Cookie`；
- 解析器在 `OnParseAsync` 开头用 `using IDisposable cookieScope = _http.UseCookie(query.Cookie);`
  让**本次解析的所有请求**带上该 Cookie（`HttpTextClient` 用 `AsyncLocal` 限定作用域，并发解析互不干扰；
  解析器显式设置的 `Cookie` 头优先，不会出现重复头）；
- Cookie **只用于解析请求**：播放地址本身不带登录态，本地中继客户端 `UseCookies = false`，
  因此 CDN 与平台都收不到用户的登录态；
- 日志只记 Cookie 指纹/键名（`SensitiveData.RedactCookie`），永不写原文。

## 失败分类

| 分类 | 触发条件示例 |
|------|--------------|
| `NotLive` | 平台明确返回未开播、无任何可用线路 |
| `RoomNotFound` | 房间号不存在 / 页面 404 / 平台 `data: null` |
| `Replaying` | 轮播、重播（不解析重播源） |
| `ParseError` | 响应结构变化、关键字段缺失、未知状态值 |
| `NetworkError` | 连接失败、超时、5xx、响应体截断 |
| `Rejected` | 平台风控、验证码、非法请求（不重试、不绕过） |
| `InvalidInput` | 房间号格式错误、链接域名不匹配 |
| `Unsupported` | 平台未注册解析器 |

## HTTP 行为

所有请求经 `Core.Http.HttpTextClient`：

- 单请求超时 8 秒（可覆盖）；
- 最多 3 次尝试，退避 300 / 900 / 2000 ms（±20% 抖动）；
- 仅对连接失败、超时、408/409/425/429/5xx 重试；
- 日志中 URL 一律经 `SensitiveData.RedactUrl`，Cookie 经 `RedactCookie`，签名参数只记指纹。

## 平台文档

- [哔哩哔哩 (bilibili)](bilibili.md)
- [抖音 (douyin)](douyin.md)
- [虎牙 (huya)](huya.md)
- [斗鱼 (douyu)](douyu.md)
- [YY (yy)](yy.md)
- [Bigo Live (bigo)](bigo.md)

## 新增平台的步骤

1. 写 `docs/adr/` 说明（新平台属于需求变更，需先与用户确认）；
2. 新建 `src/StreamPilot.Parsers/<新平台>/<新平台>Parser.cs`，继承 `PlatformParserBase`；
3. 在 `PlatformId` 末尾追加枚举值（**不得**修改已有数值）；
4. 在 `App.xaml.cs` 的 `BuildServices` 中注册：`new XxxParser(textClient, _logger)`；
5. 在 `tests/StreamPilot.Tests` 增加：正常 / 未开播 / 房间不存在 / 轮播 / 网络错误 / 结构变化 六类用例；
6. 更新本文件与 `README.md` 的平台支持表。

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

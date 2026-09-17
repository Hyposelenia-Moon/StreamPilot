namespace StreamPilot.Core.Models;

/// <summary>
/// 平台可选的画质档位。
/// </summary>
/// <remarks>
/// <see cref="Key"/> 由各平台解析器自行解释（B站是 <c>qn</c>、虎牙是 <c>ratio</c> 码率、斗鱼是 <c>rate</c>、
/// 抖音是拉流档位键），宿主只负责原样回传给解析器，不在 Core 层做平台判断。
/// </remarks>
public sealed record QualityOption
{
    /// <summary>平台内部档位键。</summary>
    public required string Key { get; init; }

    /// <summary>面向用户显示的档位名（尽量与官方直播间一致，例如「原画」「蓝光20M」）。</summary>
    public required string Label { get; init; }

    /// <summary>档位码率（kbps）；平台未提供时为 <see langword="null"/>。</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>是否为当前可选的最高档。</summary>
    public bool IsBest { get; init; }

    /// <summary>构造选项时使用的"最高档"标记值。</summary>
    public const string BestFlag = "best";
}

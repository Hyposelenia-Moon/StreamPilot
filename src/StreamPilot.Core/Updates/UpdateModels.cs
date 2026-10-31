namespace StreamPilot.Core.Updates;

/// <summary>检查更新的结果状态。</summary>
public enum UpdateCheckStatus
{
    /// <summary>当前已是最新版本。</summary>
    UpToDate,

    /// <summary>发现可用的新版本。</summary>
    UpdateAvailable,

    /// <summary>检查失败（网络、限流或响应结构异常）。</summary>
    Failed,
}

/// <summary>一个可用的发布版本（对应 GitHub Release）。</summary>
/// <param name="Tag">原始 tag（例如 <c>v0.2.2</c>）。</param>
/// <param name="Version">解析后的版本号。</param>
/// <param name="Name">Release 标题。</param>
/// <param name="Notes">更新说明（Release body，Markdown 原文）。</param>
/// <param name="PublishedAt">发布时间。</param>
/// <param name="AssetName">更新包文件名。</param>
/// <param name="AssetUrl">更新包下载地址。</param>
/// <param name="AssetSize">更新包字节数（未知为 0）。</param>
/// <param name="Sha256">更新包 SHA-256（十六进制小写；API 未提供时为空）。</param>
public sealed record UpdateReleaseInfo(
    string Tag,
    SemanticVersion Version,
    string Name,
    string Notes,
    DateTimeOffset? PublishedAt,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? Sha256);

/// <summary>检查更新的结果。</summary>
/// <param name="Status">结果状态。</param>
/// <param name="Release">发现新版本时的发布信息。</param>
/// <param name="Message">给用户看的说明（失败原因或已是最新的提示）。</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateReleaseInfo? Release, string Message)
{
    /// <summary>构造「已是最新」结果。</summary>
    /// <param name="current">当前版本。</param>
    /// <returns>检查结果。</returns>
    public static UpdateCheckResult UpToDate(SemanticVersion current) =>
        new(UpdateCheckStatus.UpToDate, null, $"当前已是最新版本（{current}）。");

    /// <summary>构造「检查失败」结果。</summary>
    /// <param name="message">失败原因。</param>
    /// <returns>检查结果。</returns>
    public static UpdateCheckResult Failed(string message) => new(UpdateCheckStatus.Failed, null, message);

    /// <summary>构造「发现新版本」结果。</summary>
    /// <param name="release">发布信息。</param>
    /// <returns>检查结果。</returns>
    public static UpdateCheckResult Available(UpdateReleaseInfo release) =>
        new(UpdateCheckStatus.UpdateAvailable, release, $"发现新版本 {release.Version}。");
}

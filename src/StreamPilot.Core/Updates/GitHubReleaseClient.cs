namespace StreamPilot.Core.Updates;

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;

/// <summary>
/// 从 GitHub Releases 读取最新版本信息。
/// </summary>
/// <remarks>
/// 公开仓库不需要令牌，匿名接口有每小时 60 次的限流（按 IP 计）。
/// 因此本类只在「启动检查一次」与「用户手动检查」时被调用，不做轮询。
/// </remarks>
public sealed class GitHubReleaseClient
{
    /// <summary>默认仓库所有者。</summary>
    public const string DefaultOwner = "Hyposelenia-Moon";

    /// <summary>默认仓库名。</summary>
    public const string DefaultRepository = "StreamPilot";

    /// <summary>更新包文件名前缀（与 <c>build/publish.ps1</c> 的产物命名一致）。</summary>
    public const string AssetNamePrefix = "StreamPilot-windows-";

    private const string ApiBase = "https://api.github.com";
    private const string ModuleName = "Core.Updates";

    private readonly HttpClientFactory _factory;
    private readonly IStructuredLogger _logger;
    private readonly string _owner;
    private readonly string _repository;

    /// <summary>初始化客户端。</summary>
    /// <param name="factory">HTTP 客户端工厂（沿用配置里的代理）。</param>
    /// <param name="logger">结构化日志。</param>
    /// <param name="owner">仓库所有者；为空时用默认值。</param>
    /// <param name="repository">仓库名；为空时用默认值。</param>
    public GitHubReleaseClient(
        HttpClientFactory factory,
        IStructuredLogger logger,
        string? owner = null,
        string? repository = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
        _owner = string.IsNullOrWhiteSpace(owner) ? DefaultOwner : owner.Trim();
        _repository = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim();
    }

    /// <summary>
    /// 检查是否有比 <paramref name="current"/> 更新的版本。
    /// </summary>
    /// <param name="current">当前版本号。</param>
    /// <param name="includePrerelease">是否把预发布版也算作候选。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>检查结果；网络或解析失败时返回 <see cref="UpdateCheckStatus.Failed"/>，不抛异常。</returns>
    public async Task<UpdateCheckResult> CheckAsync(
        SemanticVersion current,
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        string path = includePrerelease
            ? $"/repos/{_owner}/{_repository}/releases?per_page=10"
            : $"/repos/{_owner}/{_repository}/releases/latest";

        using HttpRequestMessage request = new(HttpMethod.Get, ApiBase + path);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        try
        {
            HttpClient client = _factory.GetClient();
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // /releases/latest 在仓库只有 draft 或还没有任何 release 时返回 404。
                return UpdateCheckResult.Failed("仓库还没有发布任何可用版本。");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return UpdateCheckResult.Failed("GitHub 接口访问受限（可能超出匿名频率限制），请稍后再试。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"GitHub 返回 HTTP {(int)response.StatusCode}。");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            UpdateCheckResult result = ParseResponse(document.RootElement, current, includePrerelease);
            _logger.Info(ModuleName, "检查更新完成。", new Dictionary<string, object?>
            {
                ["current"] = current.ToString(),
                ["status"] = result.Status.ToString(),
                ["latest"] = result.Release?.Version.ToString(),
            });
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("检查更新超时。");
        }
        catch (HttpRequestException exception)
        {
            _logger.Warn(ModuleName, "检查更新失败（网络）。", new Dictionary<string, object?>
            {
                ["detail"] = exception.Message,
            });
            return UpdateCheckResult.Failed(
                "无法连接 GitHub（" + exception.Message + "）。可在「设置 → 高级」填写代理后重试。");
        }
        catch (JsonException exception)
        {
            _logger.Warn(ModuleName, "检查更新失败（响应解析）。", new Dictionary<string, object?>
            {
                ["detail"] = exception.Message,
            });
            return UpdateCheckResult.Failed("GitHub 返回的内容格式异常，无法解析。");
        }
    }

    /// <summary>
    /// 解析 GitHub 响应（纯函数，便于离线用例覆盖）。
    /// </summary>
    /// <param name="root">响应根节点：单对象（latest）或数组（列表）。</param>
    /// <param name="current">当前版本。</param>
    /// <param name="includePrerelease">是否接受预发布版。</param>
    /// <returns>检查结果。</returns>
    internal static UpdateCheckResult ParseResponse(
        JsonElement root,
        SemanticVersion current,
        bool includePrerelease)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
            {
                if (!TryParseRelease(item, includePrerelease, out UpdateReleaseInfo release))
                {
                    continue;
                }

                // 列表按发布时间倒序：第一条可用的就是候选。
                return release.Version.IsNewerThan(current)
                    ? UpdateCheckResult.Available(release)
                    : UpdateCheckResult.UpToDate(current);
            }

            return UpdateCheckResult.Failed("仓库还没有发布任何可用版本。");
        }

        if (!TryParseRelease(root, includePrerelease, out UpdateReleaseInfo latest))
        {
            return UpdateCheckResult.Failed("没有找到可用的 Windows 更新包。");
        }

        return latest.Version.IsNewerThan(current)
            ? UpdateCheckResult.Available(latest)
            : UpdateCheckResult.UpToDate(current);
    }

    /// <summary>
    /// 从单个 Release 节点解析发布信息；草稿、预发布（按需）与缺少 Windows 包的条目都视为不可用。
    /// </summary>
    /// <param name="element">Release 节点。</param>
    /// <param name="includePrerelease">是否接受预发布版。</param>
    /// <param name="release">解析结果。</param>
    /// <returns>可用时返回 <see langword="true"/>。</returns>
    internal static bool TryParseRelease(JsonElement element, bool includePrerelease, out UpdateReleaseInfo release)
    {
        release = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (ReadBool(element, "draft") == true)
        {
            return false;
        }

        if (!includePrerelease && ReadBool(element, "prerelease") == true)
        {
            return false;
        }

        string tag = ReadString(element, "tag_name") ?? string.Empty;
        if (!SemanticVersion.TryParse(tag, out SemanticVersion version))
        {
            return false;
        }

        if (!TrySelectAsset(element, tag, out string assetName, out string assetUrl, out long assetSize, out string? sha256))
        {
            return false;
        }

        release = new UpdateReleaseInfo(
            tag,
            version,
            ReadString(element, "name") ?? tag,
            ReadString(element, "body") ?? string.Empty,
            ReadTimestamp(element, "published_at"),
            assetName,
            assetUrl,
            assetSize,
            sha256);
        return true;
    }

    /// <summary>
    /// 在 assets 里挑选 Windows 更新包：优先与 tag 同名的 zip，其次任意 <c>StreamPilot-windows-*.zip</c>。
    /// </summary>
    private static bool TrySelectAsset(
        JsonElement element,
        string tag,
        out string name,
        out string url,
        out long size,
        out string? sha256)
    {
        name = string.Empty;
        url = string.Empty;
        size = 0;
        sha256 = null;

        if (!element.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string preferred = AssetNamePrefix + tag + ".zip";
        JsonElement selected = default;
        bool found = false;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? assetName = ReadString(asset, "name");
            if (string.IsNullOrEmpty(assetName) || !IsUpdatePackage(assetName))
            {
                continue;
            }

            if (string.Equals(assetName, preferred, StringComparison.OrdinalIgnoreCase))
            {
                selected = asset;
                found = true;
                break;
            }

            if (!found)
            {
                selected = asset;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        string? downloadUrl = ReadString(selected, "browser_download_url");
        if (string.IsNullOrEmpty(downloadUrl)
            || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            // 更新包必须走 https：避免中间人替换安装文件。
            return false;
        }

        name = ReadString(selected, "name") ?? string.Empty;
        url = downloadUrl;
        size = ReadInt64(selected, "size") ?? 0;
        sha256 = ReadDigest(selected);
        return true;
    }

    /// <summary>判断资产名是否是本软件的 Windows 更新包。</summary>
    /// <param name="assetName">资产名。</param>
    /// <returns>匹配返回 <see langword="true"/>。</returns>
    internal static bool IsUpdatePackage(string assetName) =>
        assetName.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase)
        && assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 拼接实际下载地址：镜像前缀 + Release 给出的 GitHub 地址。
    /// </summary>
    /// <param name="assetUrl">Release 里的 <c>browser_download_url</c>。</param>
    /// <param name="mirrorPrefix">镜像前缀（可为空或非法，非法时回退直连）。</param>
    /// <returns>实际请求的地址。</returns>
    /// <remarks>
    /// 只接受 http/https 前缀：镜像地址是用户手填的，把 <c>file://</c> 或自定义 scheme 拼进来
    /// 会让 HttpClient 直接抛异常，还不如当没填。
    /// </remarks>
    public static string BuildDownloadUrl(string assetUrl, string? mirrorPrefix)
    {
        if (string.IsNullOrWhiteSpace(mirrorPrefix))
        {
            return assetUrl;
        }

        string prefix = mirrorPrefix.Trim();
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out Uri? prefixUri)
            || (prefixUri.Scheme != Uri.UriSchemeHttps && prefixUri.Scheme != Uri.UriSchemeHttp))
        {
            return assetUrl;
        }

        return prefix.EndsWith('/') ? prefix + assetUrl : prefix + "/" + assetUrl;
    }

    private static string? ReadDigest(JsonElement element)
    {
        string? digest = ReadString(element, "digest");
        if (string.IsNullOrEmpty(digest))
        {
            return null;
        }

        // 形如 "sha256:abc..."；不识别的算法直接忽略（调用方按“无摘要”处理）。
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim().ToLowerInvariant()
            : null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static long? ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        string? text = ReadString(element, property);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}

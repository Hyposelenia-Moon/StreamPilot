namespace StreamPilot.Tests.Cases;

using System.Text.Json;
using StreamPilot.Core.Updates;
using StreamPilot.Tests.Framework;

/// <summary>
/// 软件内更新：版本比较、Release 解析与下载地址拼接。
/// </summary>
/// <remarks>
/// 这些逻辑决定"要不要提示升级""装的是不是这一个包"，一旦出错要么漏更新、要么把人引到错误的安装包，
/// 所以全部按纯函数覆盖，不发任何网络请求。
/// </remarks>
[TestClass]
public sealed class UpdateTests
{
    /// <summary>tag 的各种写法都要能解析（GitHub 上常见 v 前缀与不带的混用）。</summary>
    [TestMethod("更新：版本号解析兼容 v 前缀、缺段与预发布")]
    public void ParsesVersionVariants()
    {
        Assert.True(SemanticVersion.TryParse("v0.2.2", out SemanticVersion tagged), "v0.2.2");
        Assert.Equal("0.2.2", tagged.ToString());

        Assert.True(SemanticVersion.TryParse("0.2", out SemanticVersion shortForm), "0.2");
        Assert.Equal("0.2.0", shortForm.ToString(), "缺段补零");

        Assert.True(SemanticVersion.TryParse("0.2.2-beta.1", out SemanticVersion preRelease), "预发布");
        Assert.True(preRelease.IsPreRelease);
        Assert.Equal("beta.1", preRelease.PreRelease);

        Assert.True(SemanticVersion.TryParse("0.2.2+build.7", out SemanticVersion build), "构建元数据");
        Assert.Equal("0.2.2", build.ToString(), "构建元数据不参与版本");

        Assert.False(SemanticVersion.TryParse("", out _), "空串");
        Assert.False(SemanticVersion.TryParse("latest", out _), "非数字");
        Assert.False(SemanticVersion.TryParse("0.2.2.1", out _), "四段式不接受");
    }

    /// <summary>比较规则：数字段优先，同号时正式版高于预发布版。</summary>
    [TestMethod("更新：版本比较同号正式版高于预发布")]
    public void ComparesVersions()
    {
        Assert.True(SemanticVersion.TryParse("0.2.2", out SemanticVersion current), "current");
        Assert.True(SemanticVersion.TryParse("0.2.3", out SemanticVersion newer), "newer");
        Assert.True(SemanticVersion.TryParse("0.2.2-beta.1", out SemanticVersion beta), "beta");
        Assert.True(SemanticVersion.TryParse("0.2.10", out SemanticVersion ten), "ten");

        Assert.True(newer.IsNewerThan(current), "0.2.3 > 0.2.2");
        Assert.False(current.IsNewerThan(newer), "0.2.2 < 0.2.3");
        Assert.True(current.IsNewerThan(beta), "0.2.2 > 0.2.2-beta.1");
        Assert.False(beta.IsNewerThan(current), "预发布不高于同号正式版");
        Assert.True(ten.IsNewerThan(newer), "0.2.10 > 0.2.3（按数字比较而不是字符串）");
    }

    /// <summary>单对象响应（/releases/latest）：有新版本时给出更新包信息。</summary>
    [TestMethod("更新：latest 响应解析出新版本与更新包")]
    public void ParsesLatestRelease()
    {
        const string Json =
            """
            {
              "tag_name": "v0.3.0",
              "name": "StreamPilot v0.3.0",
              "body": "## 变更\n- 修了一些问题",
              "published_at": "2026-11-01T10:00:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt", "size": 10 },
                { "name": "StreamPilot-windows-v0.3.0.zip",
                  "browser_download_url": "https://github.com/o/r/releases/download/v0.3.0/StreamPilot-windows-v0.3.0.zip",
                  "size": 81234567, "digest": "sha256:ABCDEF" }
              ]
            }
            """;

        using JsonDocument document = JsonDocument.Parse(Json);
        Assert.True(SemanticVersion.TryParse("0.2.2", out SemanticVersion current), "current");
        UpdateCheckResult result = GitHubReleaseClient.ParseResponse(document.RootElement, current, includePrerelease: false);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Release);
        Assert.Equal("v0.3.0", result.Release!.Tag);
        Assert.Equal("0.3.0", result.Release.Version.ToString());
        Assert.Equal("StreamPilot-windows-v0.3.0.zip", result.Release.AssetName, "应挑出 Windows 更新包而不是其它附件");
        Assert.Equal(81234567L, result.Release.AssetSize);
        Assert.Equal("abcdef", result.Release.Sha256, "digest 前缀要剥掉并转小写");
        Assert.True(result.Release.Notes.Contains("变更", StringComparison.Ordinal), "更新说明原样保留");
    }

    /// <summary>同版本或更旧版本不该提示更新。</summary>
    [TestMethod("更新：同版本与旧版本判为已是最新")]
    public void TreatsSameOrOlderAsUpToDate()
    {
        Assert.True(SemanticVersion.TryParse("0.2.2", out SemanticVersion current), "current");

        using JsonDocument same = JsonDocument.Parse(
            """{ "tag_name": "v0.2.2", "assets": [ { "name": "StreamPilot-windows-v0.2.2.zip", "browser_download_url": "https://github.com/x/y.zip" } ] }""");
        Assert.Equal(UpdateCheckStatus.UpToDate, GitHubReleaseClient.ParseResponse(same.RootElement, current, false).Status);

        using JsonDocument older = JsonDocument.Parse(
            """{ "tag_name": "v0.2.1", "assets": [ { "name": "StreamPilot-windows-v0.2.1.zip", "browser_download_url": "https://github.com/x/y.zip" } ] }""");
        Assert.Equal(UpdateCheckStatus.UpToDate, GitHubReleaseClient.ParseResponse(older.RootElement, current, false).Status);
    }

    /// <summary>草稿、预发布与「没有 Windows 包」的版本都不能当成可用更新。</summary>
    [TestMethod("更新：草稿/预发布/无安装包的版本被跳过")]
    public void SkipsUnusableReleases()
    {
        Assert.True(SemanticVersion.TryParse("0.2.2", out SemanticVersion current), "current");

        using JsonDocument draft = JsonDocument.Parse(
            """{ "tag_name": "v0.3.0", "draft": true, "assets": [ { "name": "StreamPilot-windows-v0.3.0.zip", "browser_download_url": "https://github.com/x/y.zip" } ] }""");
        Assert.Equal(UpdateCheckStatus.Failed, GitHubReleaseClient.ParseResponse(draft.RootElement, current, false).Status);

        using JsonDocument preRelease = JsonDocument.Parse(
            """{ "tag_name": "v0.3.0-beta.1", "prerelease": true, "assets": [ { "name": "StreamPilot-windows-v0.3.0-beta.1.zip", "browser_download_url": "https://github.com/x/y.zip" } ] }""");
        Assert.Equal(UpdateCheckStatus.Failed, GitHubReleaseClient.ParseResponse(preRelease.RootElement, current, false).Status,
            "未开启预发布时不接受 prerelease");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable,
            GitHubReleaseClient.ParseResponse(preRelease.RootElement, current, true).Status,
            "开启后同一响应应被视为可用更新");

        using JsonDocument noAsset = JsonDocument.Parse(
            """{ "tag_name": "v0.3.0", "assets": [ { "name": "source.tar.gz", "browser_download_url": "https://github.com/x/source.tar.gz" } ] }""");
        Assert.Equal(UpdateCheckStatus.Failed, GitHubReleaseClient.ParseResponse(noAsset.RootElement, current, false).Status,
            "没有 Windows 更新包时不能引导用户去下载");
    }

    /// <summary>非 https 的下载地址直接拒绝：安装文件不能走明文传输。</summary>
    [TestMethod("更新：拒绝非 https 的更新包地址")]
    public void RejectsInsecureAssetUrl()
    {
        const string Json =
            """
            { "tag_name": "v0.3.0", "assets": [
                { "name": "StreamPilot-windows-v0.3.0.zip", "browser_download_url": "http://example.com/pkg.zip" } ] }
            """;

        using JsonDocument document = JsonDocument.Parse(Json);
        Assert.False(GitHubReleaseClient.TryParseRelease(document.RootElement, false, out _), "http 地址应被拒绝");
    }

    /// <summary>列表响应（includePrerelease）取第一条可用条目。</summary>
    [TestMethod("更新：列表响应取最新可用条目")]
    public void ParsesReleaseList()
    {
        const string Json =
            """
            [
              { "tag_name": "v0.3.1-beta.2", "prerelease": true,
                "assets": [ { "name": "StreamPilot-windows-v0.3.1-beta.2.zip", "browser_download_url": "https://github.com/x/a.zip" } ] },
              { "tag_name": "v0.3.0", "prerelease": false,
                "assets": [ { "name": "StreamPilot-windows-v0.3.0.zip", "browser_download_url": "https://github.com/x/b.zip" } ] }
            ]
            """;

        using JsonDocument document = JsonDocument.Parse(Json);
        Assert.True(SemanticVersion.TryParse("0.2.2", out SemanticVersion current), "current");

        UpdateCheckResult stable = GitHubReleaseClient.ParseResponse(document.RootElement, current, includePrerelease: false);
        Assert.Equal("0.3.0", stable.Release?.Version.ToString(), "跳过预发布，取 0.3.0");

        UpdateCheckResult withPre = GitHubReleaseClient.ParseResponse(document.RootElement, current, includePrerelease: true);
        Assert.Equal("0.3.1-beta.2", withPre.Release?.Version.ToString(), "允许预发布时取列表第一条");
    }

    /// <summary>镜像前缀拼接：只接受 http/https，末尾斜杠自动处理。</summary>
    [TestMethod("更新：镜像前缀拼接规则")]
    public void BuildsDownloadUrl()
    {
        const string AssetUrl = "https://github.com/o/r/releases/download/v0.3.0/pkg.zip";

        Assert.Equal(AssetUrl, GitHubReleaseClient.BuildDownloadUrl(AssetUrl, null));
        Assert.Equal(AssetUrl, GitHubReleaseClient.BuildDownloadUrl(AssetUrl, "  "));
        Assert.Equal("https://ghfast.top/" + AssetUrl, GitHubReleaseClient.BuildDownloadUrl(AssetUrl, "https://ghfast.top/"));
        Assert.Equal("https://ghfast.top/" + AssetUrl, GitHubReleaseClient.BuildDownloadUrl(AssetUrl, "https://ghfast.top"));
        Assert.Equal(AssetUrl, GitHubReleaseClient.BuildDownloadUrl(AssetUrl, "ftp://mirror.example.com/"),
            "非 http/https 前缀应回退直连");
        Assert.Equal(AssetUrl, GitHubReleaseClient.BuildDownloadUrl(AssetUrl, "not a url"),
            "非法前缀应回退直连");
    }
}

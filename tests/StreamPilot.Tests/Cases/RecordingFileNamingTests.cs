namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="RecordingFileNaming"/> 的命名与安全测试。
/// </summary>
[TestClass]
public sealed class RecordingFileNamingTests
{
    /// <summary>标准命名格式：{主播名}-{房间号}-{开始时间}-{序号}。</summary>
    [TestMethod("文件命名：标准格式与序号补零")]
    public void BuildsStandardName()
    {
        DateTimeOffset startedAt = new(2025, 2, 14, 12, 0, 0, TimeSpan.FromHours(8));
        string name = RecordingFileNaming.BuildFileName(PlatformId.Bilibili, "主播甲", "123456", startedAt, 7, ".flv");

        Assert.Equal("主播甲-123456-20250214-120000-007.flv", name);
    }

    /// <summary>非法字符被替换，路径穿越被阻断。</summary>
    [TestMethod("文件命名：非法字符与路径穿越")]
    public void SanitizesAnchor()
    {
        Assert.Equal("a_b_c", RecordingFileNaming.SanitizeAnchor("a/b\\c"));
        Assert.Equal("unknown", RecordingFileNaming.SanitizeAnchor(null));
        Assert.Equal("unknown", RecordingFileNaming.SanitizeAnchor("   "));
        Assert.Equal("unknown", RecordingFileNaming.SanitizeAnchor(".."));
        Assert.Equal("unknown", RecordingFileNaming.SanitizeAnchor("..."));
        Assert.DoesNotContain("..", RecordingFileNaming.SanitizeAnchor("../../etc/passwd"));
        Assert.DoesNotContain(":", RecordingFileNaming.SanitizeAnchor("C:\\windows\\system32"));
    }

    /// <summary>超长主播名被截断。</summary>
    [TestMethod("文件命名：超长名截断到 40 字符")]
    public void TruncatesLongAnchor()
    {
        string longName = new('字', 120);
        string sanitized = RecordingFileNaming.SanitizeAnchor(longName);
        Assert.Equal(RecordingFileNaming.MaxAnchorLength, sanitized.Length);
    }

    /// <summary>扩展名跟随流格式。</summary>
    [TestMethod("文件命名：扩展名跟随格式")]
    public void MapsExtension()
    {
        Assert.Equal(".flv", RecordingFileNaming.ExtensionFor(StreamFormat.FlvHttp));
        Assert.Equal(".ts", RecordingFileNaming.ExtensionFor(StreamFormat.HlsTs));
        Assert.Equal(".bin", RecordingFileNaming.ExtensionFor(StreamFormat.Unknown));
    }

    /// <summary>同名文件冲突时追加序号，绝不覆盖已有文件。</summary>
    [TestMethod("文件命名：冲突时追加后缀且不覆盖")]
    public void ResolvesUniquePath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "streampilot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = RecordingFileNaming.ResolveUniquePath(directory, "a-b-20250101-000000-000.flv");
            File.WriteAllText(first, "x");
            string second = RecordingFileNaming.ResolveUniquePath(directory, "a-b-20250101-000000-000.flv");

            Assert.True(first != second, "冲突时应返回不同路径");
            Assert.Contains("-1.flv", second);
            Assert.True(File.Exists(first), "原文件不应被删除");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>元数据侧车文件名与分片同目录同前缀。</summary>
    [TestMethod("文件命名：元数据侧车路径")]
    public void BuildsMetadataPath()
    {
        DateTimeOffset startedAt = new(2025, 2, 14, 12, 0, 0, TimeSpan.FromHours(8));
        string path = RecordingFileNaming.BuildMetadataPath(@"C:\out", "主播甲", "123456", startedAt);
        Assert.Contains("主播甲-123456-20250214-120000.meta.json", path);
    }
}

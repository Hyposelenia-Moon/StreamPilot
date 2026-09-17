namespace StreamPilot.Tests.Support;

/// <summary>
/// 临时目录辅助：用于录制与文件命名测试，退出时确保递归删除。
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>创建唯一的临时目录。</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "streampilot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>目录完整路径。</summary>
    public string Path { get; }

    /// <summary>删除目录及其内容。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不应影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}

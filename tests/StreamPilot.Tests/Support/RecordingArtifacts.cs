namespace StreamPilot.Tests.Support;

using StreamPilot.Tests.Framework;

/// <summary>
/// 录制产物断言的共享辅助：专门用于证明"文件句柄确实已释放"。
/// </summary>
/// <remarks>
/// 只以 <see cref="FileShare.None"/> 重新打开文件即可判定句柄是否释放：
/// 旧实现（SP-03）在取消/失败时不关闭 <see cref="FileStream"/>，此时打开必定抛 <see cref="IOException"/>。
/// 打开同时返回长度，因此断言既能证明释放，也能证明数据真的落盘。
/// </remarks>
internal static class RecordingArtifacts
{
    /// <summary>分片文件扩展名。</summary>
    private const string SegmentExtension = ".ts";

    /// <summary>以独占方式重新打开文件，证明句柄已释放，并返回文件长度。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件长度（字节）。</returns>
    /// <exception cref="AssertionFailedException">文件句柄未释放（或文件不存在）时抛出。</exception>
    public static long AssertHandleReleased(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using FileStream reopened = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return reopened.Length;
        }
        catch (IOException exception)
        {
            throw new AssertionFailedException($"文件句柄未释放（无法以 FileShare.None 打开）：{Path.GetFileName(path)}；{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AssertionFailedException($"文件不可读（可能仍被占用）：{Path.GetFileName(path)}；{exception.Message}");
        }
    }

    /// <summary>列出目录下的全部分片文件。</summary>
    /// <param name="directory">输出目录。</param>
    /// <returns>按名称排序的分片文件完整路径。</returns>
    public static string[] ListSegmentFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string[] files = Directory.GetFiles(directory, "*" + SegmentExtension, SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }
}

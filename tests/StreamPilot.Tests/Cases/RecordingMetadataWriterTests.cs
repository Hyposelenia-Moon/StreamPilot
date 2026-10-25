namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording.Metadata;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="RecordingMetadataWriter"/> 的原子替换回归：读者短暂持有侧车时，收尾状态仍必须落盘。
/// </summary>
/// <remarks>
/// 复审里 <c>RecordingSessionTs</c> 的 ENDLIST 用例稳定 10 秒超时，根因不在录制器：
/// 测试每 10 毫秒用 <c>File.ReadAllText</c>（<see cref="FileShare.Read"/>）轮询侧车，
/// 而 <c>File.Move(overwrite: true)</c> 需要目标文件的删除权限，读者持文件时抛共享冲突。
/// 旧实现只记 Warn、不重试，收尾那次 <c>Complete</c> 一旦撞上就永久丢失结束时间与停止原因。
/// </remarks>
[TestClass]
public sealed class RecordingMetadataWriterTests
{
    /// <summary>读者持文件的时间（毫秒）：要大于一次重试间隔、小于总重试预算。</summary>
    private const int ReaderHoldMilliseconds = 120;

    /// <summary>压力用例的收尾次数。</summary>
    private const int StressIterations = 30;

    /// <summary>读者持有侧车时，收尾写入仍必须成功（原子替换有界重试）。</summary>
    [TestMethod("录制元数据：读者持有侧车时收尾仍落盘")]
    public async Task CompleteSurvivesReaderHoldingSidecar()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "probe.meta.json");
        RecordingMetadataWriter writer = new(path, BuildMetadata(), NullStructuredLogger.Instance);
        Assert.True(writer.Flush(force: true), "初始写入必须成功");

        // 只读打开（FileShare.Read）正是备份/索引程序与测试轮询读的打开方式：它拒绝删除，从而挡住原子替换。
        FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(ReaderHoldMilliseconds).ConfigureAwait(false);
            held.Dispose();
        });

        writer.Complete(RecordingStopReason.UserStopped);
        await release.ConfigureAwait(false);

        string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        Assert.Contains("\"stopReason\": \"UserStopped\"", json, "读者持文件期间收尾仍必须落盘");
        Assert.Contains("\"endedAtUtc\"", json, "结束时间同样必须写入");
    }

    /// <summary>持续并发读取下反复收尾：每一次都必须落盘（复现旧实现 16% 丢失率的场景）。</summary>
    [TestMethod("录制元数据：并发读取下每次收尾都落盘")]
    public async Task EveryCompleteSurvivesConcurrentReader()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "probe.meta.json");
        RecordingMetadata metadata = BuildMetadata();
        RecordingMetadataWriter writer = new(path, metadata, NullStructuredLogger.Instance);
        Assert.True(writer.Flush(force: true), "初始写入必须成功");

        using CancellationTokenSource stop = new();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    // 原子替换的瞬间可能读不到文件，读者重试即可；写者不得因此丢数据。
                }
            }
        });

        int lost = 0;
        for (int index = 0; index < StressIterations; index++)
        {
            RecordingStopReason expected = index % 2 == 0
                ? RecordingStopReason.StreamEnded
                : RecordingStopReason.UserStopped;
            metadata.StopReason = RecordingStopReason.None;
            writer.Complete(expected);

            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (!json.Contains("\"stopReason\": \"" + expected + "\"", StringComparison.Ordinal))
            {
                lost++;
            }
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await reader.ConfigureAwait(false);

        Assert.Equal(0, lost, "并发读取期间不得丢失任何一次收尾写入");
    }

    /// <summary>构造测试用元数据（必填字段全部显式给出）。</summary>
    /// <returns>元数据对象。</returns>
    private static RecordingMetadata BuildMetadata() => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        StartedAtUtc = DateTimeOffset.UtcNow,
        Format = "hls-ts",
        Codec = "avc",
    };
}

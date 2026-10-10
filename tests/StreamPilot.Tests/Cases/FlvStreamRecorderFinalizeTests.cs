namespace StreamPilot.Tests.Cases;

using System.Diagnostics;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Recording.Hls;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="FlvStreamRecorder"/> 的"必达收尾 + 分片句柄释放"回归测试（SP-03 的 FLV 同族问题）。
/// </summary>
/// <remarks>
/// 用例走真实临时目录落盘 + 脚本化数据源替身，断言三件事：
/// <list type="number">
///   <item>落盘失败（替身注入 <see cref="IOException"/>）后分片句柄仍被释放，停止原因是明确取值而不是 <c>None</c>；</item>
///   <item>取消录制时收尾用的是独立且有界的令牌，末分片照样登记（旧实现把已取消的令牌/永不过期的令牌当收尾令牌）；</item>
///   <item>收尾的有界性由具名常量与源码结构保证。</item>
/// </list>
/// </remarks>
[TestClass]
public sealed class FlvStreamRecorderFinalizeTests
{
    /// <summary>FLV 分片扩展名。</summary>
    private const string FlvExtension = ".flv";

    /// <summary>无数据到达判定秒数：取足够长，避免阻塞读先被 stall 超时打断。</summary>
    private const int StallTimeoutSeconds = 10;

    /// <summary>每个媒体标签的时间步长（毫秒）。</summary>
    private const int MediaStepMs = 1_000;

    /// <summary>模拟媒体时长（秒）。</summary>
    private const int MediaSeconds = 2;

    /// <summary>等待分片文件出现的轮询间隔（毫秒）。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>等待上限。</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>收尾超时的可接受上限（秒）：再长就等于"没有上界"。</summary>
    private const int MaxAcceptableFinalizeTimeoutSeconds = 60;

    /// <summary>全零退避：让重连路径不产生真实等待。</summary>
    private static readonly int[] InstantBackoffSeconds = [0, 0, 0, 0, 0];

    /// <summary>测试房间（仅用于命名与日志上下文）。</summary>
    private static readonly ResolvedRoom Room = new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        Candidates = [],
        ResolvedAt = DateTimeOffset.Now,
    };

    /// <summary>分片收尾回调落盘失败：仍必须收尾、释放句柄，并把停止原因写成失败。</summary>
    [TestMethod("FLV 录制：分片回调落盘失败后句柄已释放且停止原因为失败")]
    public async Task ReleasesSegmentHandleWhenSegmentCallbackFails()
    {
        using TempDirectory temp = new();
        RecordingLogger logger = new();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildMediaStream());
        int callbackCount = 0;

        FlvRecordingResult result = await RecordAsync(
            source,
            temp.Path,
            maxReconnectAttempts: 0,
            onSegmentCompleted: _ =>
            {
                callbackCount++;
                throw new IOException("模拟分片收尾时侧车元数据落盘失败。");
            },
            logger: logger).ConfigureAwait(false);

        Assert.Equal(1, callbackCount, "回调只应被调用一次：失败后不得把同一分片重复登记");
        Assert.Equal(RecordingStopReason.Failed, result.StopReason, "收尾落盘失败必须如实上报失败");
        Assert.False(result.StopReason == RecordingStopReason.None, "停止原因不得停留在 None");
        Assert.Equal(1, result.Segments.Count, "已经写完的末分片仍必须登记");
        Assert.True(result.TotalBytes > 0, "分片字节数必须反映真实落盘");
        Assert.True(logger.HasLevel(LogLevel.Error), "落盘失败必须留下 Error 日志");

        string path = SingleSegmentFile(temp.Path);
        long length = RecordingArtifacts.AssertHandleReleased(path);
        Assert.Equal(result.TotalBytes, length, "登记字节数必须等于真实落盘字节数");
        File.Delete(path);
        Assert.False(File.Exists(path), "收尾后分片必须可以立即删除（句柄已释放）");
    }

    /// <summary>取消录制时收尾令牌不得是"已经取消的上层令牌"，末分片必须登记且句柄释放。</summary>
    [TestMethod("FLV 录制：取消后收尾仍登记末分片且句柄已释放")]
    public async Task FinalizesAndReleasesHandleWhenCancelledWithOpenSegment()
    {
        using TempDirectory temp = new();
        RecordingLogger logger = new();
        ScriptedStreamSource source = new ScriptedStreamSource()
            .ThenStream(() => new StallingTailStream(BuildMediaStream()));
        using CancellationTokenSource cancellation = new();

        Task<FlvRecordingResult> recording = RecordAsync(
            source,
            temp.Path,
            maxReconnectAttempts: 0,
            onSegmentCompleted: null,
            logger: logger,
            cancellationToken: cancellation.Token);

        // 分片文件出现 ⇒ 写入器已经打开（收尾必须把它的句柄释放掉）。
        await WaitForSegmentFileAsync(temp.Path).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        FlvRecordingResult result = await recording.ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.UserStopped, result.StopReason);
        Assert.Equal(1, result.Segments.Count, "取消后必须补登记已写入的末分片");
        Assert.True(result.TotalBytes > 0, "末分片必须真的落盘");
        Assert.False(result.StopReason == RecordingStopReason.None, "停止原因不得停留在 None");

        string path = SingleSegmentFile(temp.Path);
        long length = RecordingArtifacts.AssertHandleReleased(path);
        Assert.Equal(result.TotalBytes, length, "登记字节数必须等于真实落盘字节数");
        File.Delete(path);
        Assert.False(File.Exists(path), "取消收尾后分片必须可以立即删除（句柄已释放）");
    }

    /// <summary>
    /// 收尾的有界性：源码级核实收尾使用独立且有界的令牌，且 <c>finally</c> 兜底释放分片句柄。
    /// </summary>
    /// <remarks>
    /// 本地磁盘的 <c>FlushAsync</c> 无法在不引入假 IO（假 <c>FileStream</c>）的前提下被可靠地卡住，
    /// 因此这里不做"读到超时"的行为断言（那会变成假断言），只核实收尾确实构造了有界令牌、
    /// 且不再使用永不过期的令牌。取值上界与 TS 侧保持一致，避免两侧收尾策略漂移。
    /// </remarks>
    [TestMethod("FLV 收尾：使用独立有界令牌且 finally 释放句柄（源码级核实）")]
    public void FinalizeUsesBoundedTokenAndReleasesWriter()
    {
        string path = ResolveRecorderSourcePath();
        Assert.True(File.Exists(path), "找不到被测源文件（静态核实的路径已失效）：" + path);
        string source = File.ReadAllText(path);

        Assert.True(FlvStreamRecorder.FinalizeTimeoutSeconds > 0, "收尾超时必须是正的秒数");
        Assert.True(
            FlvStreamRecorder.FinalizeTimeoutSeconds <= MaxAcceptableFinalizeTimeoutSeconds,
            "收尾超时必须有上界，否则等同没有超时");
        Assert.Equal(
            TsStreamRecorder.FinalizeTimeoutSeconds,
            FlvStreamRecorder.FinalizeTimeoutSeconds,
            "FLV 与 TS 的收尾超时必须取同一量级（同一类本地磁盘）");
        Assert.True(
            source.Contains("CancellationTokenSource bounded = new(TimeSpan.FromSeconds(FinalizeTimeoutSeconds))", StringComparison.Ordinal),
            "收尾必须构造自己的有界 CancellationTokenSource");
        Assert.False(
            source.Contains("onSegmentCompleted, CancellationToken.None)", StringComparison.Ordinal),
            "收尾不得再使用永不过期的 CancellationToken.None");
        Assert.True(
            source.Contains("await session.ReleaseWriterAsync(boundedRelease.Token)", StringComparison.Ordinal),
            "finally 必须兜底释放当前分片写入器（句柄不得泄漏）");
    }

    /// <summary>执行一次 FLV 录制（全零退避，避免测试等待）。</summary>
    /// <param name="source">脚本化数据源。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="maxReconnectAttempts">最大重连次数。</param>
    /// <param name="onSegmentCompleted">分片完成回调。</param>
    /// <param name="logger">日志替身。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>录制结果。</returns>
    private static Task<FlvRecordingResult> RecordAsync(
        ScriptedStreamSource source,
        string outputDirectory,
        int maxReconnectAttempts,
        Action<RecordingSegment>? onSegmentCompleted,
        IStructuredLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        FlvStreamRecorder recorder = new(
            new SegmentPolicy(),
            DateTimeOffset.Now,
            logger ?? NullStructuredLogger.Instance,
            InstantBackoffSeconds);

        return recorder.RecordAsync(
            source,
            Room,
            outputDirectory,
            StreamFormat.FlvHttp,
            maxDurationMinutes: 480,
            StallTimeoutSeconds,
            maxReconnectAttempts,
            onSegmentCompleted,
            cancellationToken);
    }

    /// <summary>等待输出目录里出现分片文件。</summary>
    /// <param name="directory">输出目录。</param>
    /// <returns>异步等待任务。</returns>
    /// <exception cref="AssertionFailedException">超时仍未出现时抛出。</exception>
    private static async Task WaitForSegmentFileAsync(string directory)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (Directory.GetFiles(directory, "*" + FlvExtension, SearchOption.TopDirectoryOnly).Length > 0)
            {
                return;
            }

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        throw new AssertionFailedException($"等待分片文件出现超时（{WaitTimeout.TotalSeconds} 秒）：{directory}");
    }

    /// <summary>取输出目录里唯一的分片文件路径。</summary>
    /// <param name="directory">输出目录。</param>
    /// <returns>分片文件完整路径。</returns>
    /// <exception cref="AssertionFailedException">分片数量不为 1 时抛出。</exception>
    private static string SingleSegmentFile(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*" + FlvExtension, SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        Assert.Equal(1, files.Length, "输出目录必须恰好有一个 FLV 分片");
        return files[0];
    }

    /// <summary>从测试输出目录向上找到仓库根，再拼出被测源文件路径。</summary>
    /// <returns>被测源文件完整路径（可能不存在，由调用方断言）。</returns>
    private static string ResolveRecorderSourcePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StreamPilot.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "StreamPilot.Recording", "Flv", "FlvStreamRecorder.cs");
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }

    /// <summary>构造一段媒体流：视频序列头 + 每秒一个关键帧。</summary>
    /// <returns>完整 FLV 字节流。</returns>
    private static byte[] BuildMediaStream()
    {
        List<byte> buffer = [.. FlvTestData.BuildFileHeader()];
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload()));
        for (int second = 0; second <= MediaSeconds; second++)
        {
            buffer.AddRange(FlvTestData.BuildTag(
                FlvTagType.Video,
                second * MediaStepMs,
                FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)));
        }

        return [.. buffer];
    }

    /// <summary>先产出给定字节、随后一直阻塞到取消的只读流（模拟"分片已打开、上游不再给数据"）。</summary>
    private sealed class StallingTailStream : Stream
    {
        private readonly byte[] _payload;
        private int _offset;

        /// <summary>初始化流。</summary>
        /// <param name="payload">先产出的字节。</param>
        public StallingTailStream(byte[] payload) => _payload = payload;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _payload.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException("只读流不支持定位。");
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _payload.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            int written = Math.Min(buffer.Length, _payload.Length - _offset);
            _payload.AsMemory(_offset, written).CopyTo(buffer);
            _offset += written;
            return written;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = Math.Min(count, _payload.Length - _offset);
            _payload.AsMemory(_offset, written).CopyTo(buffer.AsMemory(offset, written));
            _offset += written;
            return written;
        }

        /// <inheritdoc />
        public override void Flush()
        {
            // 只读流没有需要刷新的内容。
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("只读流不支持定位。");

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("只读流不支持改长度。");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("只读流不支持写入。");
    }
}

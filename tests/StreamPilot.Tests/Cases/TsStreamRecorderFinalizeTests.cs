namespace StreamPilot.Tests.Cases;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Recording.Hls;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="TsStreamRecorder"/> 的"必达收尾"回归测试（SP-03）。
/// </summary>
/// <remarks>
/// 全部用例走真实落盘（临时目录）+ 脚本化 HTTP 替身，因此可以断言：
/// 收尾后文件句柄确实已释放（能以 <see cref="FileShare.None"/> 重新打开）、
/// 末分片确实被登记、结果反映真实字节数/时长/分片列表。
/// 旧实现中取消或上游失败会跳过收尾，导致文件被锁住且结果全 0。
/// </remarks>
[TestClass]
public sealed class TsStreamRecorderFinalizeTests
{
    /// <summary>测试分片包含的完整 TS 包数量。</summary>
    private const int PacketCount = 4;

    /// <summary>每个分片的字节数（整包，无前导垃圾）。</summary>
    private const long SegmentBytes = PacketCount * TsStreamRecorder.TsPacketSize;

    /// <summary>播放列表地址。</summary>
    private const string PlaylistUri = "https://cdn.example/live/index.m3u8";

    /// <summary>三个分片的绝对地址（与播放列表里的相对地址解析结果一致）。</summary>
    private const string SegmentOneUri = "https://cdn.example/live/seg-1.ts";

    private const string SegmentTwoUri = "https://cdn.example/live/seg-2.ts";

    private const string SegmentThreeUri = "https://cdn.example/live/seg-3.ts";

    /// <summary>单个分片的媒体时长（秒）。</summary>
    private const double SegmentDurationSeconds = 4.0;

    /// <summary>等待上游请求的轮询间隔（毫秒）。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>等待上游请求的上限。</summary>
    private static readonly TimeSpan RequestWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>1 分钟分片上限：配合 70 秒的 EXTINF 可在每个分片上触发轮换。</summary>
    private const int ShortSegmentDurationMinutes = 1;

    /// <summary>播放列表声明的目标分片时长（秒，仅作 TARGETDURATION 声明）。</summary>
    private const int TargetDurationSeconds = 10;

    /// <summary>用于"无最长时长限制"用例的单分片媒体时长（秒，1 小时）。</summary>
    private const double HourSegmentDurationSeconds = 3600.0;

    /// <summary>ENDLIST 正常结束：收尾一次、末分片被登记、句柄已释放。</summary>
    [TestMethod("TS 录制：ENDLIST 结束只收尾一次且句柄已释放")]
    public async Task FinalizesOnceOnEndListAndReleasesHandle()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x11);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(endList: true, (SegmentOneUri, SegmentDurationSeconds)))
            .ServeBytes(SegmentOneUri, payload);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: true, ("seg-1.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        FlvRecordingResult result = await RecordAsync(client, playlist, temp.Path, completed);

        Assert.Equal(1, result.Segments.Count, "ENDLIST 播放列表必须登记一个末分片");
        Assert.Equal(1, completed.Count, "末分片只能被关闭并回调一次");
        Assert.Equal(RecordingStopReason.StreamEnded, result.StopReason);
        Assert.Equal(SegmentBytes, result.TotalBytes, "结果字节数必须等于真实写入字节数");
        Assert.EqualDouble(SegmentDurationSeconds, result.DurationSeconds, 0.001, "结果时长必须反映真实媒体时长");

        string path = Path.Combine(temp.Path, result.Segments[0].FileName);
        long length = ReadAfterReleaseCheck(path, expectedBytes: SegmentBytes);
        Assert.Equal(SegmentBytes, length);
        byte[] written = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        Assert.SequenceEqual(payload, written, "写出的内容必须与下载内容一致");

        // 更强的"文件确实已释放"证据：收尾后可以立刻删除。
        File.Delete(path);
        Assert.False(File.Exists(path), "收尾后文件必须可以立即删除（句柄已释放）");
    }

    /// <summary>取消发生在下载分片期间：仍完成收尾、句柄释放、元数据非 0。</summary>
    [TestMethod("TS 录制：拉分片期间取消仍收尾且句柄已释放")]
    public async Task FinalizesWhenCancelledDuringSegmentDownload()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x22);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(endList: false, (SegmentOneUri, SegmentDurationSeconds), (SegmentTwoUri, SegmentDurationSeconds)))
            .ServeBytes(SegmentOneUri, payload)
            .Stall(SegmentTwoUri);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: false, ("seg-1.ts", SegmentDurationSeconds), ("seg-2.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));
        using CancellationTokenSource cancellation = new();

        Task<FlvRecordingResult> recording = RecordAsync(client, playlist, temp.Path, completed, cancellation.Token);

        // "第二个分片已被请求"⇒ 第一个分片已经写完并计入当前分片，取消点因此是确定的。
        await WaitForRequestAsync(stub, SegmentTwoUri, minimumCount: 1).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        FlvRecordingResult result = await recording.ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.UserStopped, result.StopReason);
        Assert.Equal(1, result.Segments.Count, "取消后必须补登记当前分片");
        Assert.Equal(1, completed.Count);
        Assert.Equal(SegmentBytes, result.TotalBytes);
        Assert.EqualDouble(SegmentDurationSeconds, result.DurationSeconds, 0.001);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, result.Segments[0].FileName), SegmentBytes);
    }

    /// <summary>取消发生在刷新播放列表期间：仍完成收尾、句柄释放、元数据非 0。</summary>
    [TestMethod("TS 录制：刷新播放列表期间取消仍收尾且句柄已释放")]
    public async Task FinalizesWhenCancelledDuringPlaylistRefresh()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x33);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeTextThenStall(PlaylistUri, BuildPlaylist(endList: false, (SegmentOneUri, SegmentDurationSeconds)))
            .ServeBytes(SegmentOneUri, payload);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: false, ("seg-1.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));
        using CancellationTokenSource cancellation = new();

        Task<FlvRecordingResult> recording = RecordAsync(
            client,
            playlist,
            temp.Path,
            completed,
            cancellation.Token,
            playlistRefreshSeconds: 1);

        await WaitForRequestAsync(stub, PlaylistUri, minimumCount: 2).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        FlvRecordingResult result = await recording.ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.UserStopped, result.StopReason);
        Assert.Equal(1, result.Segments.Count, "取消后必须补登记当前分片");
        Assert.Equal(SegmentBytes, result.TotalBytes);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, result.Segments[0].FileName), SegmentBytes);
    }

    /// <summary>上游分片下载失败：失败原因被记录，收尾仍然完成。</summary>
    [TestMethod("TS 录制：分片下载失败被记录且收尾完成")]
    public async Task FinalizesAfterSegmentDownloadFailureAndLogsReason()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x44);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecordingLogger logger = new();
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(endList: true, (SegmentOneUri, SegmentDurationSeconds), (SegmentTwoUri, SegmentDurationSeconds)))
            .ServeBytes(SegmentOneUri, payload);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: true, ("seg-1.ts", SegmentDurationSeconds), ("seg-2.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        FlvRecordingResult result = await RecordAsync(client, playlist, temp.Path, completed, logger: logger);

        Assert.Equal(1, result.ReconnectCount, "分片下载失败必须计入重连/跳过次数");
        Assert.True(logger.HasLevel(LogLevel.Warn), "分片下载失败必须记录日志");
        Assert.True(logger.HasMessageContaining("HLS 分片下载失败"), "日志必须说明失败原因");
        Assert.Equal(1, result.Segments.Count, "失败后仍必须收尾已写入的分片");
        Assert.Equal(SegmentBytes, result.TotalBytes);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, result.Segments[0].FileName), SegmentBytes);
    }

    /// <summary>刷新播放列表失败上抛时，收尾必须已经完成。</summary>
    [TestMethod("TS 录制：刷新播放列表失败上抛但收尾已完成")]
    public async Task FinalizesBeforePropagatingPlaylistRefreshFailure()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x55);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecordingLogger logger = new();
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeTextThenFail(PlaylistUri, BuildPlaylist(endList: false, (SegmentOneUri, SegmentDurationSeconds)), HttpStatusCode.InternalServerError)
            .ServeBytes(SegmentOneUri, payload);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: false, ("seg-1.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        RecordingException failure = await Assert.ThrowsAsync<RecordingException>(
            () => RecordAsync(client, playlist, temp.Path, completed, logger: logger, playlistRefreshSeconds: 0)).ConfigureAwait(false);

        Assert.Equal(RecordingErrorCategory.MalformedStream, failure.Category, "上游刷新失败必须保持上游错误分类，而不是收尾错误");
        Assert.Equal(1, completed.Count, "上抛前必须已完成收尾并登记当前分片");
        Assert.Equal(SegmentBytes, completed[0].Bytes);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, completed[0].FileName), SegmentBytes);
        Assert.False(logger.HasLevel(LogLevel.Error), "上游失败本身不在录制器内记 Error（由上层会话记录）");
    }

    /// <summary>连续两次轮换：分片数正确、每个分片独立可读。</summary>
    [TestMethod("TS 录制：连续轮换后各分片独立可读")]
    public async Task WritesSeparateReadableFilesAfterRepeatedRotation()
    {
        byte[] first = BuildSegmentPayload(fill: 0x61);
        byte[] second = BuildSegmentPayload(fill: 0x62);
        byte[] third = BuildSegmentPayload(fill: 0x63);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        const double rotationDurationSeconds = 70.0;
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(
                endList: true,
                (SegmentOneUri, rotationDurationSeconds),
                (SegmentTwoUri, rotationDurationSeconds),
                (SegmentThreeUri, rotationDurationSeconds)))
            .ServeBytes(SegmentOneUri, first)
            .ServeBytes(SegmentTwoUri, second)
            .ServeBytes(SegmentThreeUri, third);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(
                endList: true,
                ("seg-1.ts", rotationDurationSeconds),
                ("seg-2.ts", rotationDurationSeconds),
                ("seg-3.ts", rotationDurationSeconds)),
            new Uri(PlaylistUri));
        SegmentPolicy policy = new(new SegmentPolicyOptions { MaxDurationMinutes = ShortSegmentDurationMinutes });

        FlvRecordingResult result = await RecordAsync(client, playlist, temp.Path, completed, policy: policy);

        Assert.Equal(3, result.Segments.Count, "70 秒分片在 1 分钟上限下必须每片轮换一次");
        Assert.Equal(3, completed.Count);
        Assert.Equal(SegmentBytes * 3, result.TotalBytes);
        Assert.EqualDouble(rotationDurationSeconds * 3, result.DurationSeconds, 0.001);
        Assert.Equal(
            3,
            result.Segments.Select(segment => segment.FileName).Distinct(StringComparer.Ordinal).Count(),
            "分片文件名必须互不相同");

        long firstLength = ReadAfterReleaseCheck(Path.Combine(temp.Path, result.Segments[0].FileName), SegmentBytes);
        long secondLength = ReadAfterReleaseCheck(Path.Combine(temp.Path, result.Segments[1].FileName), SegmentBytes);
        long thirdLength = ReadAfterReleaseCheck(Path.Combine(temp.Path, result.Segments[2].FileName), SegmentBytes);
        Assert.Equal(SegmentBytes, firstLength);
        Assert.Equal(SegmentBytes, secondLength);
        Assert.Equal(SegmentBytes, thirdLength);
        byte[] firstWritten = await File.ReadAllBytesAsync(Path.Combine(temp.Path, result.Segments[0].FileName)).ConfigureAwait(false);
        byte[] secondWritten = await File.ReadAllBytesAsync(Path.Combine(temp.Path, result.Segments[1].FileName)).ConfigureAwait(false);
        byte[] thirdWritten = await File.ReadAllBytesAsync(Path.Combine(temp.Path, result.Segments[2].FileName)).ConfigureAwait(false);
        Assert.SequenceEqual(first, firstWritten, "第一个分片必须是第一段下载内容");
        Assert.SequenceEqual(second, secondWritten, "第二个分片必须是第二段下载内容");
        Assert.SequenceEqual(third, thirdWritten, "第三个分片必须是第三段下载内容");
    }

    /// <summary>TS 录制没有最长时长限制：超过 8 小时媒体时间仍按播放列表 ENDLIST 正常结束。</summary>
    [TestMethod("TS 录制：超过 8 小时媒体时间仍按 ENDLIST 结束")]
    public async Task KeepsRecordingBeyondEightHoursWithoutDurationLimit()
    {
        const int hourSegments = 9;
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        (string Uri, double DurationSeconds)[] segments = new (string, double)[hourSegments];
        for (int index = 0; index < hourSegments; index++)
        {
            segments[index] = ($"seg-{index + 1}.ts", HourSegmentDurationSeconds);
        }

        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(endList: true, segments));
        for (int index = 0; index < hourSegments; index++)
        {
            stub.ServeBytes(new Uri(new Uri(PlaylistUri), segments[index].Uri).ToString(), BuildSegmentPayload(fill: (byte)(0x70 + index)));
        }

        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(BuildPlaylist(endList: true, segments), new Uri(PlaylistUri));
        SegmentPolicy policy = new(new SegmentPolicyOptions { MaxDurationMinutes = RecordingLimits.MinutesPerHour });

        FlvRecordingResult result = await RecordAsync(client, playlist, temp.Path, completed, policy: policy);

        Assert.Equal(hourSegments, result.Segments.Count, "9 小时的媒体时间必须全部录完");
        Assert.Equal(RecordingStopReason.StreamEnded, result.StopReason, "TS 录制不得因时长上限停止");
        Assert.EqualDouble(HourSegmentDurationSeconds * hourSegments, result.DurationSeconds, 0.001);
        Assert.Equal(SegmentBytes * hourSegments, result.TotalBytes);
    }

    /// <summary>执行一次 TS 录制。</summary>
    /// <param name="client">带替身处理器的 HTTP 客户端。</param>
    /// <param name="playlist">已解析的播放列表。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="completed">收集已完成分片的列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="policy">分片策略；为 <see langword="null"/> 时使用默认策略。</param>
    /// <param name="logger">日志替身；为 <see langword="null"/> 时使用空日志。</param>
    /// <param name="playlistRefreshSeconds">播放列表刷新间隔（秒）。</param>
    /// <returns>录制结果。</returns>
    private static Task<FlvRecordingResult> RecordAsync(
        HttpClient client,
        HlsPlaylist playlist,
        string outputDirectory,
        List<RecordingSegment> completed,
        CancellationToken cancellationToken = default,
        SegmentPolicy? policy = null,
        IStructuredLogger? logger = null,
        int? playlistRefreshSeconds = null)
    {
        TsStreamRecorder recorder = new(
            policy ?? new SegmentPolicy(),
            DateTimeOffset.Now,
            logger ?? NullStructuredLogger.Instance,
            playlistRefreshSeconds);

        return recorder.RecordFragmentsAsync(
            client,
            playlist,
            new Uri(PlaylistUri),
            BuildRoom(),
            outputDirectory,
            referer: null,
            completed.Add,
            cancellationToken);
    }

    /// <summary>以 <see cref="FileShare.None"/> 重新打开分片，证明文件句柄已释放，并校验长度。</summary>
    /// <param name="path">分片路径。</param>
    /// <param name="expectedBytes">期望字节数。</param>
    /// <returns>文件实际长度。</returns>
    private static long ReadAfterReleaseCheck(string path, long expectedBytes)
    {
        long length = RecordingArtifacts.AssertHandleReleased(path);
        Assert.Equal(expectedBytes, length, "落盘字节数必须与登记的分片字节数一致");
        return length;
    }

    /// <summary>等待替身收到指定地址的指定次数请求。</summary>
    /// <param name="stub">HTTP 替身。</param>
    /// <param name="uri">请求地址。</param>
    /// <param name="minimumCount">最少请求次数。</param>
    /// <returns>异步等待任务。</returns>
    /// <exception cref="AssertionFailedException">超时仍未达到次数时抛出。</exception>
    private static async Task WaitForRequestAsync(RecorderHttpStub stub, string uri, int minimumCount)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < RequestWaitTimeout)
        {
            if (stub.RequestCount(uri) >= minimumCount)
            {
                return;
            }

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        throw new AssertionFailedException($"等待上游请求超时（{RequestWaitTimeout.TotalSeconds} 秒）：{uri}");
    }

    /// <summary>构造播放列表文本（相对地址由解析器按播放列表地址解析为绝对地址）。</summary>
    /// <param name="endList">是否包含 ENDLIST。</param>
    /// <param name="segments">分片相对地址与时长。</param>
    /// <returns>m3u8 文本。</returns>
    private static string BuildPlaylist(bool endList, params (string Uri, double DurationSeconds)[] segments)
    {
        StringBuilder builder = new();
        builder.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:")
            .Append(TargetDurationSeconds.ToString(CultureInfo.InvariantCulture))
            .Append("\n");
        foreach ((string uri, double durationSeconds) in segments)
        {
            builder.Append("#EXTINF:").Append(durationSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            builder.Append(uri).Append('\n');
        }

        if (endList)
        {
            builder.Append("#EXT-X-ENDLIST\n");
        }

        return builder.ToString();
    }

    /// <summary>构造整包对齐的 TS 分片字节（每个包以同步字节开头）。</summary>
    /// <param name="fill">包内填充字节（用于区分不同分片）。</param>
    /// <returns>分片字节。</returns>
    private static byte[] BuildSegmentPayload(byte fill)
    {
        byte[] payload = new byte[PacketCount * TsStreamRecorder.TsPacketSize];
        for (int packet = 0; packet < PacketCount; packet++)
        {
            int offset = packet * TsStreamRecorder.TsPacketSize;
            payload[offset] = TsStreamRecorder.TsSyncByte;
            for (int index = 1; index < TsStreamRecorder.TsPacketSize; index++)
            {
                payload[offset + index] = fill;
            }
        }

        return payload;
    }

    /// <summary>构造测试房间信息。</summary>
    /// <returns>房间信息。</returns>
    private static ResolvedRoom BuildRoom() => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        Candidates = [],
        ResolvedAt = DateTimeOffset.Now,
    };
}

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

    /// <summary>刷新播放列表连续失败：按重连预算重试，用尽后以"重连上限"结束，且收尾必须已完成。</summary>
    /// <remarks>
    /// 旧实现对刷新失败"一次就上抛"、对分片失败又"完全没有上限"（SP-06）。现在两类失败共用一份
    /// <c>MaxReconnectAttempts</c> 预算：预算内重试，用尽后返回
    /// <see cref="RecordingStopReason.ReconnectLimitReached"/>，不能记成"主播下播"。
    /// </remarks>
    [TestMethod("TS 录制：刷新播放列表失败按重连上限结束且收尾已完成")]
    public async Task StopsWithReconnectLimitWhenPlaylistRefreshKeepsFailing()
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

        FlvRecordingResult result = await RecordAsync(
            client,
            playlist,
            temp.Path,
            completed,
            logger: logger,
            playlistRefreshSeconds: 0,
            maxReconnectAttempts: 1).ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.ReconnectLimitReached, result.StopReason, "刷新失败必须归因为重连上限，而不是自然下播");
        Assert.Equal(2, result.ReconnectCount, "一次失败 + 一次重试 = 2 次尝试（上限为 1 次重试）");
        Assert.Equal(3, stub.RequestCount(PlaylistUri), "播放列表请求次数必须有界（首次 + 1 次刷新 + 1 次重试）");
        Assert.Equal(1, completed.Count, "结束前必须已完成收尾并登记当前分片");
        Assert.Equal(SegmentBytes, completed[0].Bytes);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, completed[0].FileName), SegmentBytes);
        Assert.True(logger.HasLevel(LogLevel.Warn), "刷新失败必须记录日志");
        Assert.False(logger.HasLevel(LogLevel.Error), "上游失败本身不在录制器内记 Error（由上层会话记录）");
    }

    /// <summary>分片连续无法下载：重试次数受上限约束，并以"重连上限"结束而不是"自然下播"。</summary>
    /// <remarks>
    /// 复现 SP-06 的第二半：旧实现里失败分片加入 <c>Seen</c> 后既不重试也不计入上限，
    /// <c>MaxReconnectAttempts</c> 对 TS 完全无效，最终把上游失败记成 <see cref="RecordingStopReason.StreamEnded"/>。
    /// </remarks>
    [TestMethod("TS 录制：分片失败重试受上限约束并归因为重连上限")]
    public async Task StopsWithReconnectLimitWhenSegmentsKeepFailing()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x56);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecordingLogger logger = new();
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(endList: true, (SegmentOneUri, SegmentDurationSeconds), (SegmentTwoUri, SegmentDurationSeconds)))
            .ServeBytes(SegmentOneUri, payload)
            .Fail(SegmentTwoUri, HttpStatusCode.InternalServerError);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: true, ("seg-1.ts", SegmentDurationSeconds), ("seg-2.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        FlvRecordingResult result = await RecordAsync(
            client,
            playlist,
            temp.Path,
            completed,
            logger: logger,
            maxReconnectAttempts: 1).ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.ReconnectLimitReached, result.StopReason, "分片连续失败必须归因为重连上限");
        Assert.Equal(2, result.ReconnectCount, "一次失败 + 一次重试 = 2 次尝试（上限为 1 次重试）");
        Assert.Equal(2, stub.RequestCount(SegmentTwoUri), "失败分片的重试次数必须有界");
        Assert.Equal(1, completed.Count, "已写入的分片仍必须收尾登记");
        Assert.Equal(SegmentBytes, result.TotalBytes);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, completed[0].FileName), SegmentBytes);
    }

    /// <summary>分片持续返回 4xx：跳过但不重试，且**同样计入连续失败上限**。</summary>
    /// <remarks>
    /// 复现上一轮修复的残留：永久失败在预算判断之前就返回，上限永远触发不了，
    /// 直播会一直"进行中"却写不出数据，ENDLIST 则被记成"自然下播"。
    /// </remarks>
    [TestMethod("TS 录制：分片持续 4xx 也计入连续失败上限")]
    public async Task StopsWhenSegmentsKeepReturningClientErrors()
    {
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecordingLogger logger = new();
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(
                PlaylistUri,
                BuildPlaylist(
                    endList: true,
                    (SegmentOneUri, SegmentDurationSeconds),
                    (SegmentTwoUri, SegmentDurationSeconds),
                    (SegmentThreeUri, SegmentDurationSeconds)))
            .Fail(SegmentOneUri, HttpStatusCode.NotFound)
            .Fail(SegmentTwoUri, HttpStatusCode.NotFound)
            .Fail(SegmentThreeUri, HttpStatusCode.NotFound);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(
                endList: true,
                ("seg-1.ts", SegmentDurationSeconds),
                ("seg-2.ts", SegmentDurationSeconds),
                ("seg-3.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        FlvRecordingResult result = await RecordAsync(
            client,
            playlist,
            temp.Path,
            completed,
            logger: logger,
            maxReconnectAttempts: 1).ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.ReconnectLimitReached, result.StopReason, "连续 4xx 必须归因为重连上限");
        Assert.Equal(2, result.ReconnectCount, "两次连续 4xx 达到上限（上限为 1）");
        Assert.Equal(0, result.Segments.Count, "没有分片可登记");
        Assert.Equal(0, result.TotalBytes);
        Assert.Equal(1, stub.RequestCount(SegmentOneUri), "4xx 不重试：每个分片只请求一次");
        Assert.Equal(1, stub.RequestCount(SegmentTwoUri));
        Assert.Equal(0, stub.RequestCount(SegmentThreeUri), "达到上限后不得再尝试后续分片");
        Assert.True(logger.HasLevel(LogLevel.Warn), "连续失败必须记录日志");
    }

    /// <summary>全部分片都取不到、但未达到上限时，归因必须是失败而不是自然下播。</summary>
    /// <remarks>
    /// 旧实现即使只有 3 个分片、上限 5，也会以 <see cref="RecordingStopReason.StreamEnded"/> 结束并写 0 字节，
    /// 用户读起来像"主播下播了，只是没录到"。
    /// </remarks>
    [TestMethod("TS 录制：全部分片取不到时归因为失败")]
    public async Task ReportsFailureWhenNothingCouldBeWritten()
    {
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(
                PlaylistUri,
                BuildPlaylist(
                    endList: true,
                    (SegmentOneUri, SegmentDurationSeconds),
                    (SegmentTwoUri, SegmentDurationSeconds),
                    (SegmentThreeUri, SegmentDurationSeconds)))
            .Fail(SegmentOneUri, HttpStatusCode.NotFound)
            .Fail(SegmentTwoUri, HttpStatusCode.NotFound)
            .Fail(SegmentThreeUri, HttpStatusCode.NotFound);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(
                endList: true,
                ("seg-1.ts", SegmentDurationSeconds),
                ("seg-2.ts", SegmentDurationSeconds),
                ("seg-3.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        FlvRecordingResult result = await RecordAsync(
            client,
            playlist,
            temp.Path,
            completed,
            maxReconnectAttempts: 5).ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.Failed, result.StopReason, "一个字节都没写进去时不得记成自然下播");
        Assert.Equal(3, result.ReconnectCount, "三个分片各失败一次（4xx 不重试）");
        Assert.Equal(0, result.Segments.Count);
        Assert.Equal(0, result.TotalBytes);
    }

    /// <summary>
    /// 持续有数据但整体很慢的分片不能被停滞看门狗误杀。
    /// </summary>
    /// <remarks>
    /// 复现 SP-07：旧看门狗在请求前设置一次 <c>CancelAfter</c>，下载中不按数据进度刷新，
    /// 实际限制的是**总下载耗时**——分片每 400 毫秒稳定到达一个 TS 包、总耗时 2.4 秒（超过 2 秒停滞窗口）时
    /// 会被判成"没有数据返回"并整片丢弃。按读取空闲间隔计时后必须完整落盘。
    /// </remarks>
    [TestMethod("TS 录制：持续有数据的慢分片不被停滞看门狗中断")]
    public async Task SlowButSteadySegmentIsRecordedCompletely()
    {
        const int packetCount = 6;
        const int packetIntervalMs = 400;
        byte[] payload = BuildSegmentPayload(fill: 0x57, packetCount);
        using TempDirectory temp = new();
        List<RecordingSegment> completed = [];
        RecordingLogger logger = new();
        RecorderHttpStub stub = new RecorderHttpStub()
            .ServeText(PlaylistUri, BuildPlaylist(endList: true, (SegmentOneUri, SegmentDurationSeconds)))
            .ServePacedBytes(SegmentOneUri, payload, packetIntervalMs);
        using HttpClient client = new(stub);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            BuildPlaylist(endList: true, ("seg-1.ts", SegmentDurationSeconds)),
            new Uri(PlaylistUri));

        FlvRecordingResult result = await RecordAsync(
            client,
            playlist,
            temp.Path,
            completed,
            logger: logger,
            stallTimeoutSeconds: 2).ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.StreamEnded, result.StopReason, "慢但持续有数据的分片必须正常录完");
        Assert.Equal(0, result.ReconnectCount, "持续有数据时不得判定为上游停滞");
        Assert.Equal(packetCount * TsStreamRecorder.TsPacketSize, result.TotalBytes, "分片必须完整落盘，而不是整片丢弃");
        Assert.Equal(1, completed.Count);
        ReadAfterReleaseCheck(Path.Combine(temp.Path, completed[0].FileName), packetCount * TsStreamRecorder.TsPacketSize);
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
    /// <param name="stallTimeoutSeconds">停滞判定秒数；为 <see langword="null"/> 时使用录制器默认值。</param>
    /// <param name="maxReconnectAttempts">重试上限；为 <see langword="null"/> 时使用录制器默认值。</param>
    /// <returns>录制结果。</returns>
    private static Task<FlvRecordingResult> RecordAsync(
        HttpClient client,
        HlsPlaylist playlist,
        string outputDirectory,
        List<RecordingSegment> completed,
        CancellationToken cancellationToken = default,
        SegmentPolicy? policy = null,
        IStructuredLogger? logger = null,
        int? playlistRefreshSeconds = null,
        int? stallTimeoutSeconds = null,
        int? maxReconnectAttempts = null)
    {
        TsStreamRecorder recorder = new(
            policy ?? new SegmentPolicy(),
            DateTimeOffset.Now,
            logger ?? NullStructuredLogger.Instance,
            playlistRefreshSeconds,
            stallTimeoutSeconds,
            maxReconnectAttempts);

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
    /// <param name="packetCount">包数量；缺省为 <see cref="PacketCount"/>。</param>
    /// <returns>分片字节。</returns>
    private static byte[] BuildSegmentPayload(byte fill, int packetCount = PacketCount)
    {
        byte[] payload = new byte[packetCount * TsStreamRecorder.TsPacketSize];
        for (int packet = 0; packet < packetCount; packet++)
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

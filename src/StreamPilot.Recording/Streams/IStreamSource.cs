namespace StreamPilot.Recording.Streams;

using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 流数据源：提供只读字节流，并支持断开后重新连接。
/// </summary>
/// <remarks>
/// 录制侧只依赖本接口，因此 HTTP-FLV、HLS-TS 甚至测试用的内存源可以互换，
/// 也让"断流重连"逻辑可以在没有网络的情况下被测试。
/// </remarks>
public interface IStreamSource : IAsyncDisposable
{
    /// <summary>用于日志与元数据的描述（不含签名）。</summary>
    string Description { get; }

    /// <summary>
    /// 建立连接并返回可读流；多次调用表示重连。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可读流（由本接口负责释放）。</returns>
    /// <exception cref="Core.Errors.RecordingException">连接失败时抛出。</exception>
    Task<Stream> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 单个 HTTP 流的字节源。
/// </summary>
/// <remarks>
/// 每次 <see cref="OpenAsync"/> 都新建一个 HTTP 响应流；对需要 Referer 的平台会注入 Referer，
/// 并对"响应延迟"设置显式超时（禁止无限等待）。
/// </remarks>
public sealed class HttpStreamSource : IStreamSource
{
    /// <summary>连接超时（秒）。</summary>
    public const int ConnectTimeoutSeconds = 10;

    private readonly HttpClient _client;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.HttpStreamSource";
    private readonly string _url;
    private readonly string? _referer;
    private readonly string _description;
    private HttpResponseMessage? _response;
    private Stream? _stream;

    /// <summary>初始化 HTTP 数据源。</summary>
    /// <param name="client">已配置好的 <see cref="HttpClient"/>。</param>
    /// <param name="url">流地址。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="fingerprint">URL 指纹（用于日志与元数据）。</param>
    /// <param name="logger">结构化日志。</param>
    public HttpStreamSource(HttpClient client, string url, string? referer, string fingerprint, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _url = url;
        _referer = referer;
        _logger = logger;
        _description = $"http-stream[{fingerprint}]";
    }

    /// <inheritdoc />
    public string Description => _description;

    /// <inheritdoc />
    public async Task<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        await CloseCurrentAsync().ConfigureAwait(false);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));

        using HttpRequestMessage request = new(HttpMethod.Get, _url);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(_referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", _referer);
        }

        try
        {
            _response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.OutputUnavailable,
                $"连接直播流超时（{ConnectTimeoutSeconds} 秒）。");
        }
        catch (HttpRequestException exception)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                "连接直播流失败。",
                exception);
        }

        if (!_response.IsSuccessStatusCode)
        {
            int statusCode = (int)_response.StatusCode;
            _response.Dispose();
            _response = null;
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                $"直播流返回 HTTP {statusCode}。");
        }

        _stream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        _logger.Debug(_moduleName, "直播流已连接。", new Dictionary<string, object?>
        {
            ["source"] = _description,
            ["status"] = (int)_response.StatusCode,
        });

        return _stream;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseCurrentAsync().ConfigureAwait(false);

    private async Task CloseCurrentAsync()
    {
        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _logger.LogError(LogLevel.Debug, _moduleName, "关闭直播流时发生 IO 异常。", exception);
            }

            _stream = null;
        }

        _response?.Dispose();
        _response = null;
    }
}

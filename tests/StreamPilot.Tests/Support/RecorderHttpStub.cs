namespace StreamPilot.Tests.Support;

using System.Net;
using System.Text;

/// <summary>
/// 录制器测试用的 HTTP 替身：按地址脚本化响应，并记录每个地址收到的请求次数。
/// </summary>
/// <remarks>
/// 提供四类路由：固定文本（播放列表）、固定字节（TS 分片）、首次成功后失败（刷新播放列表失败）、
/// 以及"永不返回直到调用方取消"（上游卡住）。因此可以在不依赖真实网络的前提下覆盖
/// 分片下载、播放列表刷新、上游失败与取消等路径。
/// </remarks>
internal sealed class RecorderHttpStub : HttpMessageHandler
{
    private readonly Dictionary<string, Func<int, CancellationToken, Task<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _requestCounts = new(StringComparer.Ordinal);

    /// <summary>注册"每次都返回固定文本"的路由。</summary>
    /// <param name="uri">请求地址。</param>
    /// <param name="text">响应文本（按 UTF-8 编码）。</param>
    /// <returns>当前替身（便于链式注册）。</returns>
    public RecorderHttpStub ServeText(string uri, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(text);
        _routes[uri] = (_, _) => Task.FromResult(TextResponse(text));
        return this;
    }

    /// <summary>注册"每次都返回固定字节"的路由。</summary>
    /// <param name="uri">请求地址。</param>
    /// <param name="payload">响应体字节。</param>
    /// <returns>当前替身（便于链式注册）。</returns>
    public RecorderHttpStub ServeBytes(string uri, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(payload);
        _routes[uri] = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        return this;
    }

    /// <summary>注册"第一次返回文本，之后返回错误状态"的路由（模拟刷新播放列表时上游失败）。</summary>
    /// <param name="uri">请求地址。</param>
    /// <param name="text">首次响应文本。</param>
    /// <param name="statusCode">第二次起返回的状态码。</param>
    /// <returns>当前替身（便于链式注册）。</returns>
    public RecorderHttpStub ServeTextThenFail(string uri, string text, HttpStatusCode statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(text);
        _routes[uri] = (count, _) => Task.FromResult(count == 1
            ? TextResponse(text)
            : new HttpResponseMessage(statusCode));
        return this;
    }

    /// <summary>注册"第一次返回文本，之后永不返回"的路由（模拟刷新播放列表时上游卡住）。</summary>
    /// <param name="uri">请求地址。</param>
    /// <param name="text">首次响应文本。</param>
    /// <returns>当前替身（便于链式注册）。</returns>
    public RecorderHttpStub ServeTextThenStall(string uri, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(text);
        _routes[uri] = (count, cancellationToken) => count == 1
            ? Task.FromResult(TextResponse(text))
            : WaitForCancellationAsync(cancellationToken);
        return this;
    }

    /// <summary>注册"永不返回直到调用方取消"的路由（模拟上游卡住）。</summary>
    /// <param name="uri">请求地址。</param>
    /// <returns>当前替身（便于链式注册）。</returns>
    public RecorderHttpStub Stall(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _routes[uri] = (_, cancellationToken) => WaitForCancellationAsync(cancellationToken);
        return this;
    }

    /// <summary>查询指定地址收到的请求次数。</summary>
    /// <param name="uri">请求地址。</param>
    /// <returns>请求次数；从未请求过返回 0。</returns>
    public int RequestCount(string uri) => _requestCounts.TryGetValue(uri, out int count) ? count : 0;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string uri = request.RequestUri?.ToString() ?? string.Empty;
        int count = RequestCount(uri) + 1;
        _requestCounts[uri] = count;

        return _routes.TryGetValue(uri, out Func<int, CancellationToken, Task<HttpResponseMessage>>? route)
            ? route(count, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8),
    };

    /// <summary>构造一个只在令牌取消时才完成的响应任务（不占用计时器）。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>永不正常完成的响应任务。</returns>
    private static async Task<HttpResponseMessage> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        return await pending.Task.ConfigureAwait(false);
    }
}

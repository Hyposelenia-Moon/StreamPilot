namespace StreamPilot.Tests.Support;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamPilot.Tests.Framework;

/// <summary>
/// 录制会话端到端测试用的最小 HTTP/1.1 回环服务器（只监听 127.0.0.1，不解析请求体）。
/// </summary>
/// <remarks>
/// 不使用 <see cref="HttpListener"/>：后者在 Windows 上要求 URL ACL 授权，非管理员进程会直接失败。
/// 这里用 <see cref="TcpListener"/> 手工应答，端口由系统分配（0 端口），无需管理员权限、不占用固定端口。
/// 支持"固定文本 / 固定字节 / 永不响应（卡住）"三类路由，用于真实落盘地覆盖录制会话的取消与收尾。
/// </remarks>
internal sealed class LoopbackHttpServer : IDisposable
{
    /// <summary>HTTP 200 状态行。</summary>
    private const string OkStatusLine = "HTTP/1.1 200 OK\r\n";

    /// <summary>HTTP 404 状态行。</summary>
    private const string NotFoundStatusLine = "HTTP/1.1 404 Not Found\r\n";

    /// <summary>请求头的最大字节数（超过即放弃，避免被恶意长头拖住）。</summary>
    private const int MaxRequestHeadBytes = 8 * 1024;

    /// <summary>读取请求头使用的缓冲区大小。</summary>
    private const int ReadBufferBytes = 1024;

    /// <summary>等待请求的轮询间隔（毫秒）。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>关闭时等待接受循环退出的上限（秒）。</summary>
    private const int AcceptLoopShutdownSeconds = 5;

    private static readonly byte[] EmptyBody = [];

    private readonly Dictionary<string, LoopbackRoute> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
    private readonly List<TcpClient> _heldClients = [];
    private readonly Lock _gate = new();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    /// <summary>启动服务器并绑定一个空闲的回环端口。</summary>
    public LoopbackHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/", UriKind.Absolute);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>服务器根地址（形如 <c>http://127.0.0.1:端口/</c>）。</summary>
    public Uri BaseUri { get; }

    /// <summary>把相对路径解析为绝对地址。</summary>
    /// <param name="path">以 <c>/</c> 开头的相对路径。</param>
    /// <returns>绝对地址字符串。</returns>
    public string AbsoluteUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri(BaseUri, path).ToString();
    }

    /// <summary>注册"返回固定文本"的路由。</summary>
    /// <param name="path">请求路径（以 <c>/</c> 开头）。</param>
    /// <param name="text">响应文本。</param>
    /// <returns>当前服务器（便于链式注册）。</returns>
    public LoopbackHttpServer ServeText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        _routes[path] = new LoopbackRoute(IsStall: false, Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");
        return this;
    }

    /// <summary>注册"返回固定字节"的路由。</summary>
    /// <param name="path">请求路径（以 <c>/</c> 开头）。</param>
    /// <param name="payload">响应体字节。</param>
    /// <returns>当前服务器（便于链式注册）。</returns>
    public LoopbackHttpServer ServeBytes(string path, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(payload);
        _routes[path] = new LoopbackRoute(IsStall: false, payload, "video/mp2t");
        return this;
    }

    /// <summary>注册"连接后永不响应"的路由（模拟上游卡住，客户端只能靠取消令牌脱身）。</summary>
    /// <param name="path">请求路径（以 <c>/</c> 开头）。</param>
    /// <returns>当前服务器（便于链式注册）。</returns>
    public LoopbackHttpServer Stall(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _routes[path] = new LoopbackRoute(IsStall: true, EmptyBody, "application/octet-stream");
        return this;
    }

    /// <summary>查询指定路径收到的请求次数。</summary>
    /// <param name="path">请求路径。</param>
    /// <returns>请求次数；从未请求过返回 0。</returns>
    public int RequestCount(string path)
    {
        lock (_gate)
        {
            return _requestCounts.TryGetValue(path, out int count) ? count : 0;
        }
    }

    /// <summary>
    /// 等待指定路径被请求（带超时）。
    /// </summary>
    /// <param name="path">请求路径。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <returns>异步等待任务。</returns>
    /// <exception cref="AssertionFailedException">超时仍未收到请求时抛出。</exception>
    /// <remarks>录制器按顺序处理分片，因此"下一个分片已被请求"可作为"上一个分片已写完"的确定信号。</remarks>
    public async Task WaitForRequestAsync(string path, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (RequestCount(path) > 0)
            {
                return;
            }

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        throw new AssertionFailedException($"等待上游请求超时（{timeout.TotalSeconds} 秒）：{path}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _listener.Stop();
        lock (_gate)
        {
            foreach (TcpClient held in _heldClients)
            {
                held.Dispose();
            }

            _heldClients.Clear();
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(AcceptLoopShutdownSeconds));
        }
        catch (AggregateException)
        {
            // 关闭期间的取消/套接字异常已由接受循环内部处理，这里只负责收尾。
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // 服务器关闭导致接受循环退出，这是正常收尾路径。
                return;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        bool keepOpen = false;
        try
        {
            string requestHead = await ReadRequestHeadAsync(client).ConfigureAwait(false);
            string path = ExtractPath(requestHead);
            LoopbackRoute? route;
            lock (_gate)
            {
                _requestCounts[path] = (_requestCounts.TryGetValue(path, out int count) ? count : 0) + 1;
                _routes.TryGetValue(path, out route);
            }

            if (route is { IsStall: true })
            {
                // 卡住：一个字节都不回，把连接留给 Dispose 关闭。
                lock (_gate)
                {
                    _heldClients.Add(client);
                }

                keepOpen = true;
                return;
            }

            await WriteResponseAsync(client, OkStatusLine, route?.Body ?? EmptyBody, route?.ContentType ?? "application/octet-stream").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 客户端提前断开（含测试主动取消卡住的请求）不是服务器错误。
        }
        finally
        {
            if (!keepOpen)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>读取请求头（至少包含请求行），直到空行为止。</summary>
    /// <param name="client">已接受的连接。</param>
    /// <returns>请求头文本。</returns>
    private static async Task<string> ReadRequestHeadAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[ReadBufferBytes];
        StringBuilder head = new();
        while (head.Length < MaxRequestHeadBytes)
        {
            int read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return head.ToString();
    }

    /// <summary>从请求头中取出请求路径（去掉查询串）。</summary>
    /// <param name="requestHead">请求头文本。</param>
    /// <returns>请求路径；无法解析时返回空字符串。</returns>
    private static string ExtractPath(string requestHead)
    {
        int firstSpace = requestHead.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0)
        {
            return string.Empty;
        }

        int secondSpace = requestHead.IndexOf(' ', firstSpace + 1);
        if (secondSpace < 0)
        {
            return string.Empty;
        }

        string target = requestHead[(firstSpace + 1)..secondSpace];
        int query = target.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? target : target[..query];
    }

    /// <summary>写回一个带 Content-Length 的完整响应。</summary>
    /// <param name="client">已接受的连接。</param>
    /// <param name="statusLine">状态行。</param>
    /// <param name="body">响应体。</param>
    /// <param name="contentType">内容类型。</param>
    private static async Task WriteResponseAsync(TcpClient client, string statusLine, byte[] body, string contentType)
    {
        StringBuilder head = new();
        head.Append(statusLine);
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body).ConfigureAwait(false);
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>一条脚本化路由。</summary>
    /// <param name="IsStall">是否"永不响应"。</param>
    /// <param name="Body">响应体。</param>
    /// <param name="ContentType">内容类型。</param>
    private sealed record LoopbackRoute(bool IsStall, byte[] Body, string ContentType);
}

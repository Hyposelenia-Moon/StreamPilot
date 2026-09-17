namespace StreamPilot.Bridge;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;

/// <summary>
/// 回环地址守卫：确保桥接服务永远不会绑定到公网可访问的地址。
/// </summary>
/// <remarks>
/// CLAUDE.md 红线：禁止将桥接端口暴露到公网。因此所有监听前缀都必须在构建期
/// 与本类校验；一旦前缀不是 <c>127.0.0.1</c>（或 <c>localhost</c>），直接抛异常终止启动，
/// 而不是"打日志继续跑"。
/// </remarks>
public static class LoopbackOnlyGuard
{
    /// <summary>禁止使用的前缀通配符。</summary>
    private static readonly string[] ForbiddenHostWildcards = ["+", "*", "0.0.0.0", "::", "[::]"];

    /// <summary>
    /// 校验监听前缀只指向本机回环地址。
    /// </summary>
    /// <param name="prefix">HttpListener 前缀，例如 <c>http://127.0.0.1:5566/</c>。</param>
    /// <exception cref="BridgeException">前缀不是回环地址时抛出。</exception>
    public static void EnsureLoopback(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        foreach (string wildcard in ForbiddenHostWildcards)
        {
            if (prefix.Contains(wildcard, StringComparison.Ordinal))
            {
                throw new BridgeException(
                    "validate-prefix",
                    $"桥接服务禁止绑定到 {wildcard}，只允许 {BridgeConstants.LoopbackHost}。");
            }
        }

        if (!prefix.Contains(BridgeConstants.LoopbackHost, StringComparison.OrdinalIgnoreCase)
            && !prefix.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeException(
                "validate-prefix",
                $"桥接服务只允许绑定 {BridgeConstants.LoopbackHost}，当前前缀：{prefix}。");
        }
    }

    /// <summary>
    /// 构造一个回环监听前缀。
    /// </summary>
    /// <param name="port">端口。</param>
    /// <returns>前缀字符串。</returns>
    /// <exception cref="BridgeException">端口不在合法范围时抛出。</exception>
    public static string BuildPrefix(int port)
    {
        if (port is < 1024 or > 65535)
        {
            throw new BridgeException("validate-port", $"桥接端口非法：{port}。");
        }

        return $"http://{BridgeConstants.LoopbackHost}:{port}/";
    }
}

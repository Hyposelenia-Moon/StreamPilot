namespace StreamPilot.Core.Logging;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 敏感信息脱敏工具。所有可能包含 Cookie、Token、签名 URL 的值必须先经过本类处理。
/// </summary>
public static class SensitiveData
{
    /// <summary>指纹前缀长度（十六进制字符数）。</summary>
    private const int FingerprintHexLength = 16;

    /// <summary>可疑的敏感查询参数名（小写），命中后其值会被替换为 <see cref="RedactedPlaceholder"/>。</summary>
    private static readonly HashSet<string> SensitiveParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "auth_key", "access_token", "cookie", "credential", "expire", "expires",
        "fm", "playauth", "secret", "sign", "signature", "token", "trid", "txsecret",
        "txtime", "upsig", "volc_secret", "ws_secret", "wsecret", "ws_time", "wstime",
        "x-signature", "_auth", "_sign", "_token", "key", "pwd", "password", "sessdata",
    };

    /// <summary>被替换后的占位文本。</summary>
    public const string RedactedPlaceholder = "<redacted>";

    /// <summary>
    /// 计算值指纹：长度 + SHA256 前 16 个十六进制字符。用于日志与去重，不泄露原文。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>形如 <c>len=128;fp=3f2a...</c> 的指纹；输入为空时返回 <c>len=0;fp=-</c>。</returns>
    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "len=0;fp=-";
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        string hex = Convert.ToHexString(hash).ToLowerInvariant()[..FingerprintHexLength];
        return $"len={value.Length};fp={hex}";
    }

    /// <summary>
    /// 脱敏 URL：保留 scheme/host/path，把敏感查询参数的值替换为占位符。
    /// </summary>
    /// <param name="url">原始 URL 或任意文本。</param>
    /// <returns>可安全写入日志的文本。</returns>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        int queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return url;
        }

        string head = url[..queryStart];
        string query = url[(queryStart + 1)..];
        StringBuilder builder = new(head.Length + query.Length);
        builder.Append(head).Append('?');

        string[] pairs = query.Split('&', StringSplitOptions.None);
        for (int index = 0; index < pairs.Length; index++)
        {
            if (index > 0)
            {
                builder.Append('&');
            }

            string pair = pairs[index];
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                builder.Append(pair);
                continue;
            }

            string name = pair[..equals];
            builder.Append(name).Append('=');
            string decodedName = Uri.UnescapeDataString(name);
            builder.Append(SensitiveParameterNames.Contains(decodedName) ? RedactedPlaceholder : pair[(equals + 1)..]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 脱敏 Cookie 文本：只保留 Cookie 名，不保留值。
    /// </summary>
    /// <param name="cookie">Cookie 头内容。</param>
    /// <returns>形如 <c>SESSDATA=<redacted>; bili_jct=<redacted></c> 的文本。</returns>
    public static string RedactCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return string.Empty;
        }

        string[] parts = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        StringBuilder builder = new();
        foreach (string part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            int equals = part.IndexOf('=', StringComparison.Ordinal);
            builder.Append(equals <= 0 ? part : part[..equals] + "=" + RedactedPlaceholder);
        }

        return builder.ToString();
    }
}

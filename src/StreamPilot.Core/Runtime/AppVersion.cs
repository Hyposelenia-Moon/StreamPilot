namespace StreamPilot.Core.Runtime;

using System.Reflection;

/// <summary>
/// 应用版本信息。
/// </summary>
public static class AppVersion
{
    /// <summary>无法读取程序集版本时的回退值。</summary>
    private const string FallbackVersion = "0.1.0";

    private static readonly Lazy<string> LazyVersion = new(ResolveVersion);

    /// <summary>语义化版本字符串（例如 <c>0.1.0</c>）。</summary>
    public static string Current => LazyVersion.Value;

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(AppVersion).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        Version? version = assembly.GetName().Version;
        return version is null ? FallbackVersion : version.ToString(3);
    }
}

namespace StreamPilot.Tests.Framework;

/// <summary>
/// 标记一个测试类。运行器会实例化该类并执行所有 <see cref="TestMethodAttribute"/> 方法。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TestClassAttribute : Attribute
{
}

/// <summary>
/// 标记一个测试方法（必须是无参、无返回值、public 的实例方法）。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TestMethodAttribute : Attribute
{
    /// <summary>初始化测试方法标记。</summary>
    /// <param name="displayName">展示名（可为空，默认使用方法名）。</param>
    public TestMethodAttribute(string? displayName = null)
    {
        DisplayName = displayName;
    }

    /// <summary>展示名。</summary>
    public string? DisplayName { get; }
}

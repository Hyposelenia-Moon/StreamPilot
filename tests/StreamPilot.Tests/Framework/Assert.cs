namespace StreamPilot.Tests.Framework;

/// <summary>
/// 断言失败异常。
/// </summary>
public sealed class AssertionFailedException : Exception
{
    /// <summary>初始化异常。</summary>
    /// <param name="message">失败原因。</param>
    public AssertionFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 极简断言库（不依赖任何测试框架包，见 docs/adr/0001-technology-stack.md 决策 5）。
/// </summary>
public static class Assert
{
    /// <summary>断言两个值相等。</summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="expected">期望值。</param>
    /// <param name="actual">实际值。</param>
    /// <param name="context">上下文说明。</param>
    public static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionFailedException($"{Describe(context)}期望 {Format(expected)}，实际 {Format(actual)}。");
        }
    }

    /// <summary>断言两个 double 在容差内相等。</summary>
    /// <param name="expected">期望值。</param>
    /// <param name="actual">实际值。</param>
    /// <param name="tolerance">容差。</param>
    /// <param name="context">上下文说明。</param>
    public static void EqualDouble(double expected, double actual, double tolerance = 1e-9, string? context = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new AssertionFailedException($"{Describe(context)}期望 {expected}±{tolerance}，实际 {actual}。");
        }
    }

    /// <summary>断言条件为真。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="context">上下文说明。</param>
    public static void True(bool condition, string? context = null)
    {
        if (!condition)
        {
            throw new AssertionFailedException($"{Describe(context)}期望为 true，实际为 false。");
        }
    }

    /// <summary>断言条件为假。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="context">上下文说明。</param>
    public static void False(bool condition, string? context = null)
    {
        if (condition)
        {
            throw new AssertionFailedException($"{Describe(context)}期望为 false，实际为 true。");
        }
    }

    /// <summary>断言值不为 <see langword="null"/>。</summary>
    /// <param name="value">值。</param>
    /// <param name="context">上下文说明。</param>
    public static void NotNull(object? value, string? context = null)
    {
        if (value is null)
        {
            throw new AssertionFailedException($"{Describe(context)}期望非 null，实际为 null。");
        }
    }

    /// <summary>断言值等于 <see langword="null"/>。</summary>
    /// <param name="value">值。</param>
    /// <param name="context">上下文说明。</param>
    public static void Null(object? value, string? context = null)
    {
        if (value is not null)
        {
            throw new AssertionFailedException($"{Describe(context)}期望 null，实际为 {Format(value)}。");
        }
    }

    /// <summary>断言字符串包含指定片段。</summary>
    /// <param name="expected">片段。</param>
    /// <param name="actual">被检查的字符串。</param>
    /// <param name="context">上下文说明。</param>
    public static void Contains(string expected, string? actual, string? context = null)
    {
        if (actual is null || !actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"{Describe(context)}期望包含 \"{expected}\"，实际为 \"{actual}\"。");
        }
    }

    /// <summary>断言字符串不包含指定片段。</summary>
    /// <param name="expected">片段。</param>
    /// <param name="actual">被检查的字符串。</param>
    /// <param name="context">上下文说明。</param>
    public static void DoesNotContain(string expected, string? actual, string? context = null)
    {
        if (actual is not null && actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"{Describe(context)}期望不包含 \"{expected}\"，实际为 \"{actual}\"。");
        }
    }

    /// <summary>断言字节序列相等。</summary>
    /// <param name="expected">期望字节。</param>
    /// <param name="actual">实际字节。</param>
    /// <param name="context">上下文说明。</param>
    public static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string? context = null)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new AssertionFailedException(
                $"{Describe(context)}字节序列不一致：期望 {Convert.ToHexString(expected)}，实际 {Convert.ToHexString(actual)}。");
        }
    }

    /// <summary>断言执行指定动作时抛出指定类型的异常。</summary>
    /// <typeparam name="TException">异常类型。</typeparam>
    /// <param name="action">被检查的动作。</param>
    /// <param name="context">上下文说明。</param>
    /// <returns>捕获到的异常。</returns>
    public static TException Throws<TException>(Action action, string? context = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception unexpected)
        {
            throw new AssertionFailedException(
                $"{Describe(context)}期望抛出 {typeof(TException).Name}，实际抛出 {unexpected.GetType().Name}：{unexpected.Message}");
        }

        throw new AssertionFailedException($"{Describe(context)}期望抛出 {typeof(TException).Name}，但未抛出任何异常。");
    }

    /// <summary>断言异步执行时抛出指定类型的异常。</summary>
    /// <typeparam name="TException">异常类型。</typeparam>
    /// <param name="func">被检查的异步动作。</param>
    /// <param name="context">上下文说明。</param>
    /// <returns>捕获到的异常。</returns>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> func, string? context = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(func);
        try
        {
            await func().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception unexpected)
        {
            throw new AssertionFailedException(
                $"{Describe(context)}期望抛出 {typeof(TException).Name}，实际抛出 {unexpected.GetType().Name}：{unexpected.Message}");
        }

        throw new AssertionFailedException($"{Describe(context)}期望抛出 {typeof(TException).Name}，但未抛出任何异常。");
    }

    private static string Describe(string? context) => string.IsNullOrWhiteSpace(context) ? string.Empty : context + "：";

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        _ => value.ToString() ?? "null",
    };
}

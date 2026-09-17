namespace StreamPilot.Tests.Framework;

using System.Diagnostics;
using System.Reflection;

/// <summary>
/// 极简测试运行器：反射发现全部 <see cref="TestClassAttribute"/> 并执行其中的 <see cref="TestMethodAttribute"/>。
/// </summary>
public static class TestRunner
{
    /// <summary>判断测试方法的返回类型是否受支持（<c>void</c> 或 <see cref="Task"/>）。</summary>
    /// <param name="returnType">返回类型。</param>
    /// <returns>受支持返回 <see langword="true"/>。</returns>
    private static bool IsSupportedReturnType(Type returnType) =>
        returnType == typeof(void) || typeof(Task).IsAssignableFrom(returnType);

    /// <summary>执行程序集内的全部测试。</summary>
    /// <param name="assembly">目标程序集。</param>
    /// <param name="filter">类型名过滤（包含匹配），可为 <see langword="null"/>。</param>
    /// <returns>全部通过返回 0，存在失败返回 1。</returns>
    public static int Run(Assembly assembly, string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<(string Name, bool Passed, string? Error, long ElapsedMs)> results = [];
        int passed = 0;
        int failed = 0;
        Stopwatch total = Stopwatch.StartNew();

        foreach (Type type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<TestClassAttribute>() is null)
            {
                continue;
            }

            if (filter is not null && !type.FullName!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int passedInClass = 0;
            int failedInClass = 0;
            List<(string Name, bool Passed, string? Error, long ElapsedMs)> classResults = [];

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                TestMethodAttribute? attribute = method.GetCustomAttribute<TestMethodAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                if (method.GetParameters().Length != 0 || !IsSupportedReturnType(method.ReturnType))
                {
                    classResults.Add(($"{type.Name}.{method.Name}", false, "测试方法必须是无参且返回 void 或 Task 的实例方法。", 0));
                    failedInClass++;
                    continue;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    object? instance = Activator.CreateInstance(type);
                    object? returned = method.Invoke(instance, null);
                    if (returned is Task task)
                    {
                        task.GetAwaiter().GetResult();
                    }

                    stopwatch.Stop();
                    classResults.Add((attribute.DisplayName ?? method.Name, true, null, stopwatch.ElapsedMilliseconds));
                    passedInClass++;
                }
                catch (TargetInvocationException exception)
                {
                    stopwatch.Stop();
                    Exception inner = exception.InnerException ?? exception;
                    classResults.Add((attribute.DisplayName ?? method.Name, false, inner.Message, stopwatch.ElapsedMilliseconds));
                    failedInClass++;
                }
                catch (Exception exception) when (exception is MissingMethodException or MemberAccessException or AggregateException)
                {
                    stopwatch.Stop();
                    classResults.Add((attribute.DisplayName ?? method.Name, false, exception.Message, stopwatch.ElapsedMilliseconds));
                    failedInClass++;
                }
            }

            if (classResults.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"[{type.Name}] {passedInClass} 通过 / {failedInClass} 失败");
            foreach ((string name, bool ok, string? error, long elapsedMs) in classResults)
            {
                if (ok)
                {
                    Console.WriteLine($"  PASS  {name} ({elapsedMs} ms)");
                }
                else
                {
                    Console.WriteLine($"  FAIL  {name} ({elapsedMs} ms)");
                    Console.WriteLine($"        {error}");
                }
            }

            passed += passedInClass;
            failed += failedInClass;
            results.AddRange(classResults);
        }

        total.Stop();
        Console.WriteLine();
        Console.WriteLine($"总计 {results.Count} 个用例：通过 {passed}，失败 {failed}，耗时 {total.ElapsedMilliseconds} ms。");
        return failed == 0 ? 0 : 1;
    }
}

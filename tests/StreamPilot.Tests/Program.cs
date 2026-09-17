using StreamPilot.Tests.Framework;

namespace StreamPilot.Tests;

/// <summary>
/// 测试程序入口。退出码：全部通过为 0，存在失败为 1（供构建脚本判定）。
/// </summary>
public static class Program
{
    /// <summary>执行全部测试。</summary>
    /// <param name="args">可选：第一个参数为类型名过滤关键字。</param>
    /// <returns>进程退出码。</returns>
    public static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        Console.WriteLine("StreamPilot 测试运行器（自研极简框架，见 docs/adr/0001）");
        if (filter is not null)
        {
            Console.WriteLine($"过滤关键字：{filter}");
        }

        Console.WriteLine();
        return TestRunner.Run(typeof(Program).Assembly, filter);
    }
}

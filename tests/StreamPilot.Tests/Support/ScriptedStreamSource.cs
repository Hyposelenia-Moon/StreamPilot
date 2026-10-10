namespace StreamPilot.Tests.Support;

using StreamPilot.Core.Errors;
using StreamPilot.Recording.Streams;

/// <summary>
/// 测试用流数据源：按预设脚本依次返回"可读流"或"连接失败"，并记录打开次数。
/// </summary>
/// <remarks>
/// 每次 <see cref="OpenAsync"/> 对应一次真实的连接尝试（首次连接与重连共用同一队列），
/// 因此可以用它覆盖"首次失败后重试成功""断流后连接失败再成功""预算耗尽失败"等路径。
/// </remarks>
internal sealed class ScriptedStreamSource : IStreamSource
{
    private readonly Queue<Func<Stream>> _script = new();

    /// <summary>连接尝试次数（含失败的尝试）。</summary>
    public int OpenCount { get; private set; }

    /// <inheritdoc />
    public string Description => "scripted-source";

    /// <summary>追加一次"连接成功并返回指定字节构成的只读流"。</summary>
    /// <param name="bytes">流字节。</param>
    /// <returns>自身，便于链式编排脚本。</returns>
    public ScriptedStreamSource ThenStream(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _script.Enqueue(() => new MemoryStream(bytes, writable: false));
        return this;
    }

    /// <summary>追加一次"连接失败"（抛出与真实连接失败同类的异常）。</summary>
    /// <returns>自身，便于链式编排脚本。</returns>
    public ScriptedStreamSource ThenFailure()
    {
        _script.Enqueue(() => throw new RecordingException(
            RecordingErrorCategory.MalformedStream,
            "测试注入的连接失败。"));
        return this;
    }

    /// <summary>
    /// 追加一次"连接成功并返回指定可读流"（用于模拟"给出一段数据后卡住"这类流）。
    /// </summary>
    /// <param name="streamFactory">流工厂：每次连接各调用一次，避免同一个流被复用。</param>
    /// <returns>自身，便于链式编排脚本。</returns>
    public ScriptedStreamSource ThenStream(Func<Stream> streamFactory)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        _script.Enqueue(streamFactory);
        return this;
    }

    /// <inheritdoc />
    public Task<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        if (_script.Count == 0)
        {
            throw new RecordingException(
                RecordingErrorCategory.MalformedStream,
                "测试脚本已耗尽：没有更多可用的连接。");
        }

        return Task.FromResult(_script.Dequeue()());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

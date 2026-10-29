namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// 配置文件迁移测试：v1 的「极限追帧」必须迁移为稳定缓冲，v2 之后保留用户选择。
/// </summary>
/// <remarks>
/// v1 时代界面上没有任何入口能修改播放模式（`PlayAsync` 硬编码 Extreme），
/// 配置文件里的 <c>mode=Extreme</c> 只可能是旧默认值；不迁移的话，升级后仍然一开播就跳帧。
/// </remarks>
[TestClass]
public sealed class OptionsStoreTests
{
    /// <summary>v1 配置（mode=Extreme）加载后应变成 v2 + 稳定缓冲。</summary>
    [TestMethod("配置迁移：v1 的极限追帧迁移为稳定缓冲")]
    public void MigratesLegacyExtremeToStable()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, """{"schemaVersion":1,"playback":{"mode":"Extreme"}}""");

        JsonOptionsStore store = new(path, NullStructuredLogger.Instance);
        StreamPilotOptions options = store.Load();

        Assert.Equal(2, options.SchemaVersion, "迁移后必须写入新的格式版本");
        Assert.Equal(PlaybackMode.Stable, options.Playback.Mode, "旧默认值 Extreme 不是用户选择，必须迁移为稳定缓冲");
    }

    /// <summary>已经迁移过的配置保留用户显式选择的极限追帧。</summary>
    [TestMethod("配置迁移：v2 的极限追帧保持不变")]
    public void KeepsExplicitExtremeOnCurrentSchema()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, """{"schemaVersion":2,"playback":{"mode":"Extreme"}}""");

        JsonOptionsStore store = new(path, NullStructuredLogger.Instance);
        StreamPilotOptions options = store.Load();

        Assert.Equal(2, options.SchemaVersion);
        Assert.Equal(PlaybackMode.Extreme, options.Playback.Mode, "用户显式选择必须保留");
    }
}

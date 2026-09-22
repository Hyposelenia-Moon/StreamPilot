namespace StreamPilot.Tests.Cases;

using StreamPilot.App.Services;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="PresetStore"/> 与 <see cref="RoomPreset"/> 的保存 / 读取用例。
/// </summary>
/// <remarks>
/// 重点回归"预设只能是哔哩哔哩"：写入磁盘再重新加载后，
/// <see cref="RoomPreset.Platform"/> 必须还是用户当初选的那个平台，不能被默认平台顶掉。
/// 每个用例都用 <see cref="TempDirectory"/> 落到临时目录，互不污染。
/// </remarks>
[TestClass]
public sealed class PresetStoreTests
{
    /// <summary>六个平台的预设保存后重新加载，平台字段原样保留（正常）。</summary>
    [TestMethod("预设存储：保存后重新加载平台不丢")]
    public void RoundTripsPlatformsForEveryPlatform()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());

        Assert.True(store.Add(new RoomPreset("某站主播", PlatformId.Bilibili, "21452505")), "B 站预设应保存成功");
        Assert.True(store.Add(new RoomPreset("抖音主播", PlatformId.Douyin, "123456789")), "抖音预设应保存成功");
        Assert.True(store.Add(new RoomPreset("虎牙主播", PlatformId.Huya, "660000")), "虎牙预设应保存成功");
        Assert.True(store.Add(new RoomPreset("斗鱼主播", PlatformId.Douyu, "9999")), "斗鱼预设应保存成功");
        Assert.True(store.Add(new RoomPreset("YY 主播", PlatformId.Yy, "168")), "YY 预设应保存成功");
        Assert.True(store.Add(new RoomPreset("Bigo 主播", PlatformId.Bigo, "abc123")), "Bigo 预设应保存成功");

        // 重新 new 一个实例并 Load：只认磁盘内容，排除"内存里本来就有"的假象。
        PresetStore reloaded = CreateStore(directory, new RecordingLogger());
        reloaded.Load();

        Assert.Equal(6, reloaded.Items.Count);
        Assert.Equal(PlatformId.Bilibili, reloaded.Items[0].Platform);
        Assert.Equal(PlatformId.Douyin, reloaded.Items[1].Platform);
        Assert.Equal(PlatformId.Huya, reloaded.Items[2].Platform);
        Assert.Equal(PlatformId.Douyu, reloaded.Items[3].Platform);
        Assert.Equal(PlatformId.Yy, reloaded.Items[4].Platform);
        Assert.Equal(PlatformId.Bigo, reloaded.Items[5].Platform);
        Assert.Equal("660000", reloaded.Items[2].RoomInput, "房间号也要原样保留");
        Assert.Equal("虎牙主播", reloaded.Items[2].Name, "显示名也要原样保留");

        // 平台必须真的写进 JSON（枚举名），而不是靠读回时的默认值。
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"platform\"", json);
        Assert.Contains("Huya", json);
        Assert.Contains("Bigo", json);
    }

    /// <summary>同名不同平台共存；同平台同名覆盖且保留新平台（正常/边界）。</summary>
    [TestMethod("预设存储：同名跨平台共存与同平台覆盖")]
    public void KeepsSameNameAcrossPlatforms()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());

        Assert.True(store.Add(new RoomPreset("同名主播", PlatformId.Huya, "111")), "虎牙同名预设应保存成功");
        Assert.True(store.Add(new RoomPreset("同名主播", PlatformId.Douyin, "222")), "抖音同名预设应保存成功");
        Assert.True(store.Add(new RoomPreset("同名主播", PlatformId.Huya, "333")), "同平台同名应覆盖");

        // 覆盖是"删掉同平台同名再追加"，所以这里按下标断言会跟实现顺序耦合，改成按平台查找。
        Assert.Equal(2, store.Items.Count);
        RoomPreset? huya = FindPreset(store, PlatformId.Huya);
        RoomPreset? douyin = FindPreset(store, PlatformId.Douyin);
        Assert.NotNull(huya, "虎牙预设应还在");
        Assert.NotNull(douyin, "另一个平台的同名预设不能被删掉");
        Assert.Equal("333", huya!.RoomInput, "同平台同名应被新内容覆盖");
        Assert.Equal("222", douyin!.RoomInput, "抖音那份不应被改动");
    }

    /// <summary>名称为空白或房间输入为空白时拒绝保存，且不产生文件（异常）。</summary>
    [TestMethod("预设存储：空白名称与空白房间号被拒绝")]
    public void RejectsBlankNameOrRoomInput()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());

        Assert.False(store.Add(new RoomPreset("   ", PlatformId.Huya, "660000")), "空白名称应被拒绝");
        Assert.False(store.Add(new RoomPreset("主播", PlatformId.Huya, "   ")), "空白房间号应被拒绝");
        Assert.Equal(0, store.Items.Count);
        Assert.False(File.Exists(store.FilePath), "被拒绝时不应写出预设文件");
    }

    /// <summary>超长名称被截断，平台字段不受影响（边界）。</summary>
    [TestMethod("预设存储：超长名称截断后平台仍保留")]
    public void TruncatesLongNameAndKeepsPlatform()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());
        string longName = new('字', PresetStore.MaxNameLength + 5);

        Assert.True(store.Add(new RoomPreset(longName, PlatformId.Douyu, "9999")), "超长名称应被截断后保存");

        Assert.Equal(PresetStore.MaxNameLength, store.Items[0].Name.Length);
        Assert.Equal(PlatformId.Douyu, store.Items[0].Platform);
    }

    /// <summary>超过上限时淘汰最旧的一条，剩余预设的平台字段保持正确（边界）。</summary>
    [TestMethod("预设存储：超上限淘汰最旧并保留平台")]
    public void TrimsOldestWhenOverCapacity()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());

        for (int index = 0; index <= PresetStore.MaxPresets; index++)
        {
            Assert.True(
                store.Add(new RoomPreset("主播" + index, PlatformId.Huya, "6600" + index)),
                "第 " + index + " 条预设应保存成功");
        }

        Assert.Equal(PresetStore.MaxPresets, store.Items.Count);
        Assert.Equal("主播1", store.Items[0].Name, "最旧的一条应被淘汰");
        Assert.Equal(PlatformId.Huya, store.Items[0].Platform);
    }

    /// <summary>预设文件损坏时回退为空列表并记录 Warn，不抛异常（异常）。</summary>
    [TestMethod("预设存储：文件损坏回退空列表并记日志")]
    public void FallsBackOnCorruptFile()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());
        File.WriteAllText(store.FilePath, "{ 这不是合法的预设文件");

        RecordingLogger logger = new();
        PresetStore reloaded = CreateStore(directory, logger);
        reloaded.Load();

        Assert.Equal(0, reloaded.Items.Count);
        Assert.True(logger.HasLevel(LogLevel.Warn), "文件损坏必须留下 Warn 日志");
    }

    /// <summary>没有 platform 字段的旧预设文件读取为 Unknown，不抛异常（边界/向后兼容）。</summary>
    [TestMethod("预设存储：旧文件缺平台字段读为 Unknown")]
    public void LoadsLegacyPresetWithoutPlatform()
    {
        using TempDirectory directory = new();
        PresetStore store = CreateStore(directory, new RecordingLogger());
        File.WriteAllText(store.FilePath, "[ { \"name\": \"旧预设\", \"roomInput\": \"123456\" } ]");

        PresetStore reloaded = CreateStore(directory, new RecordingLogger());
        reloaded.Load();

        Assert.Equal(1, reloaded.Items.Count);
        Assert.Equal("旧预设", reloaded.Items[0].Name);
        Assert.Equal(PlatformId.Unknown, reloaded.Items[0].Platform, "缺字段时按 Unknown 处理，不能猜成 B 站");
    }

    /// <summary>文件不存在时加载为空列表（边界）。</summary>
    [TestMethod("预设存储：文件不存在时为空列表")]
    public void LoadsEmptyWhenFileMissing()
    {
        using TempDirectory directory = new();
        RecordingLogger logger = new();
        PresetStore store = CreateStore(directory, logger);

        store.Load();

        Assert.Equal(0, store.Items.Count);
        Assert.False(logger.HasLevel(LogLevel.Warn), "文件不存在属正常情况，不应记 Warn");
    }

    /// <summary>创建一个指向临时目录的预设存储。</summary>
    /// <param name="directory">临时目录。</param>
    /// <param name="logger">日志替身。</param>
    /// <returns>预设存储。</returns>
    private static PresetStore CreateStore(TempDirectory directory, IStructuredLogger logger) =>
        new(logger) { FilePath = Path.Combine(directory.Path, "presets.json") };

    /// <summary>按平台查找预设（用例里每个平台最多一条）。</summary>
    /// <param name="store">预设存储。</param>
    /// <param name="platform">目标平台。</param>
    /// <returns>找到的预设；没有时返回 <see langword="null"/>。</returns>
    private static RoomPreset? FindPreset(PresetStore store, PlatformId platform)
    {
        foreach (RoomPreset preset in store.Items)
        {
            if (preset.Platform == platform)
            {
                return preset;
            }
        }

        return null;
    }
}

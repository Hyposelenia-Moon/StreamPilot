namespace StreamPilot.App.ViewModels;

using System.Globalization;
using StreamPilot.Core.Configuration;

/// <summary>
/// 录制上限的单位换算与合法性校验（GiB ↔ 字节、小时 ↔ 分钟）。
/// </summary>
/// <remarks>
/// 设置界面上按"人习惯的单位"填写（GiB / 小时），配置文件里存"引擎使用的单位"（字节 / 分钟），
/// 换算与校验集中在这里，避免界面各处各写一份换算，也便于单元测试覆盖。
/// 规则：只接受大于 0 的数值，**不设上限**；0 与负数按非法处理并给出提示，
/// 远超可存储范围的数值同样按非法处理（这是存储类型本身放不下，不是人为设的上限）。
/// 解析与格式化固定使用 <see cref="CultureInfo.InvariantCulture"/>，避免不同地区的小数点被误读。
/// </remarks>
public static class RecordingLimitUnits
{
    /// <summary>十进制数值的解析样式（允许小数与千位分隔符）。</summary>
    private const NumberStyles ValueStyles = NumberStyles.Number;

    /// <summary>小数格式化模板（最多 6 位小数，尾随零自动去掉）。</summary>
    private const string DecimalFormat = "0.######";

    /// <summary>非法数值（0 或负数）的固定说明。</summary>
    private const string NonPositiveSuffix = "必须是大于 0 的数值。";

    /// <summary>超出可存储范围时的固定说明。</summary>
    private const string TooLargeSuffix = "数值过大，超出了程序可存储的范围。";

    /// <summary>不是数值时的固定说明。</summary>
    private const string NotNumericSuffix = "必须是数字（可带小数），例如 10 或 0.5。";

    /// <summary>空输入的固定说明。</summary>
    private const string EmptySuffix = "不能为空，且必须是大于 0 的数值。";

    /// <summary>
    /// 把界面上的分片大小（GiB）换算成字节。
    /// </summary>
    /// <param name="gibibytes">分片大小（GiB）。</param>
    /// <returns>字节数。</returns>
    /// <exception cref="OverflowException">数值超出 <see cref="long"/> 可表示的范围。</exception>
    public static long ToBytes(decimal gibibytes) => checked((long)(gibibytes * RecordingLimits.BytesPerGibibyte));

    /// <summary>
    /// 把字节换算成界面上的分片大小（GiB）。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>分片大小（GiB）。</returns>
    public static decimal ToGibibytes(long bytes) => bytes / (decimal)RecordingLimits.BytesPerGibibyte;

    /// <summary>
    /// 把界面上的时长（小时）换算成分钟。
    /// </summary>
    /// <param name="hours">时长（小时）。</param>
    /// <returns>分钟数。</returns>
    /// <exception cref="OverflowException">数值超出 <see cref="int"/> 可表示的范围。</exception>
    public static int ToMinutes(decimal hours) => checked((int)(hours * RecordingLimits.MinutesPerHour));

    /// <summary>
    /// 把分钟换算成界面上的时长（小时）。
    /// </summary>
    /// <param name="minutes">分钟数。</param>
    /// <returns>时长（小时）。</returns>
    public static decimal ToHours(int minutes) => minutes / (decimal)RecordingLimits.MinutesPerHour;

    /// <summary>
    /// 把字节数格式化为界面文本（GiB，去掉多余的尾随零）。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>形如 <c>10</c> 或 <c>0.5</c> 的文本。</returns>
    public static string FormatGibibytes(long bytes) => FormatValue(ToGibibytes(bytes));

    /// <summary>
    /// 把分钟数格式化为界面文本（小时，去掉多余的尾随零）。
    /// </summary>
    /// <param name="minutes">分钟数。</param>
    /// <returns>形如 <c>8</c> 或 <c>0.5</c> 的文本。</returns>
    public static string FormatHours(int minutes) => FormatValue(ToHours(minutes));

    /// <summary>
    /// 格式化十进制值：最多保留 6 位小数并去掉尾随零。
    /// </summary>
    /// <param name="value">待格式化的值。</param>
    /// <returns>界面文本。</returns>
    public static string FormatValue(decimal value) => value.ToString(DecimalFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析并校验用户输入：大于 0 即为合法，没有任何人为上限。
    /// </summary>
    /// <param name="text">用户输入的文本。</param>
    /// <param name="unit">单位名称（用于错误提示）。</param>
    /// <param name="value">解析出的数值。</param>
    /// <param name="error">失败时的中文提示；成功时为 <see langword="null"/>。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    public static bool TryParsePositive(string? text, string unit, out decimal value, out string? error)
    {
        value = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = unit + EmptySuffix;
            return false;
        }

        if (!decimal.TryParse(text.Trim(), ValueStyles, CultureInfo.InvariantCulture, out decimal parsed))
        {
            error = unit + NotNumericSuffix;
            return false;
        }

        if (parsed <= 0)
        {
            error = unit + NonPositiveSuffix;
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// 解析界面上的三项录制上限。
    /// </summary>
    /// <param name="segmentSizeText">分片大小上限（GiB）输入文本。</param>
    /// <param name="segmentDurationText">分片时长上限（小时）输入文本。</param>
    /// <param name="maxDurationText">最长录制时长（小时）输入文本。</param>
    /// <param name="limits">解析出的上限（存储单位）。</param>
    /// <param name="error">失败时的中文提示；成功时为 <see langword="null"/>。</param>
    /// <returns>三项都合法返回 <see langword="true"/>。</returns>
    public static bool TryParseLimits(
        string? segmentSizeText,
        string? segmentDurationText,
        string? maxDurationText,
        out RecordingLimitInput? limits,
        out string? error)
    {
        limits = null;
        if (!TryParsePositive(segmentSizeText, RecordingLimitText.SegmentSizeUnit, out decimal gibibytes, out error)
            || !TryParsePositive(segmentDurationText, RecordingLimitText.SegmentDurationUnit, out decimal segmentHours, out error)
            || !TryParsePositive(maxDurationText, RecordingLimitText.MaxDurationUnit, out decimal maxHours, out error))
        {
            return false;
        }

        try
        {
            limits = new RecordingLimitInput(ToBytes(gibibytes), ToMinutes(segmentHours), ToMinutes(maxHours));
            return true;
        }
        catch (OverflowException)
        {
            error = RecordingLimitText.SegmentSizeUnit + TooLargeSuffix;
            return false;
        }
    }
}

/// <summary>
/// 录制上限在界面上的输入单位（标签、校验提示与 ToolTip 共用，避免各处手写）。
/// </summary>
public static class RecordingLimitText
{
    /// <summary>分片大小上限的输入单位。</summary>
    public const string SegmentSizeUnit = "分片大小上限（GiB）";

    /// <summary>分片时长上限的输入单位。</summary>
    public const string SegmentDurationUnit = "分片时长上限（小时）";

    /// <summary>最长录制时长的输入单位。</summary>
    public const string MaxDurationUnit = "最长录制时长（小时）";
}

/// <summary>
/// 解析后的三项录制上限（存储单位：字节 / 分钟）。
/// </summary>
/// <param name="SegmentMaxBytes">单个分片的字节上限。</param>
/// <param name="SegmentMaxMinutes">单个分片的时长上限（分钟）。</param>
/// <param name="MaxRecordingMinutes">最长录制时长（分钟）。</param>
public sealed record RecordingLimitInput(long SegmentMaxBytes, int SegmentMaxMinutes, int MaxRecordingMinutes);

namespace StreamPilot.Parsers.Bilibili;

using System.Globalization;
using System.Text.Json;
using StreamPilot.Core.Models;

/// <summary>
/// B站弹幕消息解析：把 <c>operation = 5</c> 包体里的 JSON 转成统一的 <see cref="DanmakuMessage"/>。
/// </summary>
/// <remarks>
/// <para>
/// 解析策略是<b>宽松</b>的：任何字段缺失、类型不符或结构变更都返回 <see langword="null"/> 而不是抛异常。
/// 弹幕是附加能力，单条畸形消息不应导致整条连接中断。
/// </para>
/// <para>已覆盖的消息类型（实测房间 21144080）：</para>
/// <list type="bullet">
///   <item><c>DANMU_MSG</c>：普通弹幕（info[1] 文本 / info[2][1] 昵称 / info[0][3] 颜色 / info[3] 粉丝牌）；</item>
///   <item><c>SEND_GIFT</c> / <c>SEND_GIFT_V2</c>：礼物；</item>
///   <item><c>INTERACT_WORD</c> / <c>INTERACT_WORD_V2</c>：进场、关注、分享。</item>
/// </list>
/// </remarks>
public static class BilibiliDanmakuMessageParser
{
    /// <summary>白色（默认不染色）。</summary>
    private const int WhiteColor = 16777215;

    /// <summary>颜色通道最大值。</summary>
    private const int ColorChannelMax = 255;

    /// <summary>颜色位移与掩码（0xRRGGBB）。</summary>
    private const int RedShift = 16;
    private const int GreenShift = 8;
    private const int ChannelMask = 0xFF;

    /// <summary>
    /// 解析一个消息包体。
    /// </summary>
    /// <param name="json">包体（UTF-8 JSON）。</param>
    /// <param name="sequence">会话内递增序号。</param>
    /// <returns>解析成功返回消息；未知类型或结构异常返回 <see langword="null"/>。</returns>
    public static DanmakuMessage? Parse(ReadOnlySpan<byte> json, long sequence)
    {
        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cmd", out JsonElement cmdElement)
                || cmdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            // cmd 可能带后缀（如 DANMU_MSG:4:0:2:2:2:0），按冒号切分取主名。
            string cmd = cmdElement.GetString() ?? string.Empty;
            int colon = cmd.IndexOf(':', StringComparison.Ordinal);
            string command = colon > 0 ? cmd[..colon] : cmd;

            return command switch
            {
                "DANMU_MSG" => ParseChat(root, sequence),
                "SEND_GIFT" or "SEND_GIFT_V2" => ParseGift(root, sequence),
                "INTERACT_WORD" or "INTERACT_WORD_V2" => ParseInteract(root, sequence),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // JsonElement 取值类型不符（如把数组当字符串读）也按畸形消息丢弃。
            return null;
        }
    }

    private static DanmakuMessage? ParseChat(JsonElement root, long sequence)
    {
        if (!root.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? textElement = ElementAt(info, 1);
        if (textElement is null || textElement.Value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = textElement.Value.GetString() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        string user = string.Empty;
        int userLevel = 0;
        bool isAdmin = false;
        if (ElementAt(info, 2) is { } userArray && userArray.ValueKind == JsonValueKind.Array)
        {
            if (ElementAt(userArray, 1) is { } nameElement && nameElement.ValueKind == JsonValueKind.String)
            {
                user = nameElement.GetString() ?? string.Empty;
            }

            if (ElementAt(userArray, 2) is { } adminElement && adminElement.ValueKind == JsonValueKind.Number)
            {
                isAdmin = adminElement.GetInt32() == 1;
            }
        }

        int colorValue = WhiteColor;
        if (ElementAt(info, 0) is { } metaArray && metaArray.ValueKind == JsonValueKind.Array
            && ElementAt(metaArray, 3) is { } colorElement && colorElement.ValueKind == JsonValueKind.Number)
        {
            colorValue = colorElement.GetInt32();
        }

        string? medalName = null;
        int medalLevel = 0;
        if (ElementAt(info, 3) is { } medalArray && medalArray.ValueKind == JsonValueKind.Array)
        {
            if (ElementAt(medalArray, 0) is { } levelElement && levelElement.ValueKind == JsonValueKind.Number)
            {
                medalLevel = levelElement.GetInt32();
            }

            if (ElementAt(medalArray, 1) is { } nameElement && nameElement.ValueKind == JsonValueKind.String)
            {
                medalName = nameElement.GetString();
            }
        }

        return new DanmakuMessage
        {
            Sequence = sequence,
            Kind = DanmakuKind.Chat,
            User = user,
            Text = text,
            Color = ToHexColor(colorValue),
            MedalName = string.IsNullOrEmpty(medalName) ? null : medalName,
            MedalLevel = medalLevel,
            UserLevel = userLevel,
            IsPrivileged = isAdmin,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static DanmakuMessage? ParseGift(JsonElement root, long sequence)
    {
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string user = ReadString(data, "uname") ?? string.Empty;
        string giftName = ReadString(data, "giftName") ?? "礼物";
        int count = ReadInt(data, "num") ?? 1;
        string text = string.Create(CultureInfo.InvariantCulture, $"送出 {giftName} ×{count}");

        return new DanmakuMessage
        {
            Sequence = sequence,
            Kind = DanmakuKind.Gift,
            User = user,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static DanmakuMessage? ParseInteract(JsonElement root, long sequence)
    {
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string user = ReadString(data, "uname") ?? string.Empty;
        int messageType = ReadInt(data, "msg_type") ?? 1;
        string text = messageType switch
        {
            1 => "进入直播间",
            2 => "关注了主播",
            3 => "分享了直播间",
            _ => "与直播间互动",
        };

        return new DanmakuMessage
        {
            Sequence = sequence,
            Kind = DanmakuKind.Interact,
            User = user,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static JsonElement? ElementAt(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() <= index)
        {
            return null;
        }

        return array[index];
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string? ToHexColor(int colorValue)
    {
        if (colorValue <= 0 || colorValue == WhiteColor)
        {
            return null;
        }

        int red = (colorValue >> RedShift) & ChannelMask;
        int green = (colorValue >> GreenShift) & ChannelMask;
        int blue = colorValue & ChannelMask;
        if (red > ColorChannelMax || green > ColorChannelMax || blue > ColorChannelMax)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{red:X2}{green:X2}{blue:X2}");
    }
}

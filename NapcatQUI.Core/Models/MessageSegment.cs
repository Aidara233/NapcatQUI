using System.Text.Json;
using System.Text.Json.Serialization;

namespace NapcatQUI.Core.Models;

/// <summary>
/// OneBot v11 消息段类型
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageSegmentType
{
    Text,
    Image,
    At,
    Reply,
    Face,
    Record,
    Video,
    File,
    Json,
    Xml,
    Markdown,
    Mention,
    Location,
    Share,
    Contact,
    Forward,
    Node,
    Music,
    Custom,
    Unknown
}

/// <summary>
/// 通用消息段 — 按 OneBot 规范建模，原始 Data 字典保留以便无损往返
/// </summary>
public class MessageSegment
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MessageSegmentType Type { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object?> Data { get; set; } = new();

    // ---- 便利属性 ----

    [JsonIgnore]
    public string? Text
    {
        get
        {
            if (Type == MessageSegmentType.Text && Data.TryGetValue("text", out var t))
                return t?.ToString();
            return null;
        }
    }

    [JsonIgnore]
    public string? ImageUrl
    {
        get
        {
            if (Type == MessageSegmentType.Image && Data.TryGetValue("url", out var u))
                return u?.ToString();
            return null;
        }
    }

    [JsonIgnore]
    public string? ImageFile
    {
        get
        {
            if (Type == MessageSegmentType.Image && Data.TryGetValue("file", out var f))
                return f?.ToString();
            return null;
        }
    }

    [JsonIgnore]
    public string? AtUserId
    {
        get
        {
            if (Type == MessageSegmentType.At && Data.TryGetValue("qq", out var qq))
                return qq?.ToString();
            return null;
        }
    }

    [JsonIgnore]
    public string? ReplyMessageId
    {
        get
        {
            if (Type == MessageSegmentType.Reply && Data.TryGetValue("id", out var id))
                return id?.ToString();
            return null;
        }
    }

    [JsonIgnore]
    public string? FileName
    {
        get
        {
            if (Type == MessageSegmentType.File && Data.TryGetValue("name", out var n))
                return n?.ToString();
            return null;
        }
    }

    [JsonIgnore]
    public string? FileUrl
    {
        get
        {
            if (Type == MessageSegmentType.File && Data.TryGetValue("url", out var u))
                return u?.ToString();
            return null;
        }
    }

    /// <summary>
    /// 提取此消息段中所有可读文本（用于 FTS 索引）
    /// </summary>
    public string GetSearchableText()
    {
        return Type switch
        {
            MessageSegmentType.Text => Text ?? "",
            MessageSegmentType.At => AtUserId != "all" ? $"@{AtUserId}" : "@全体成员",
            MessageSegmentType.Image => "[图片]",
            MessageSegmentType.Reply => "[回复]",
            MessageSegmentType.Face => "[表情]",
            MessageSegmentType.Record => "[语音]",
            MessageSegmentType.Video => "[视频]",
            MessageSegmentType.File => $"[文件: {FileName ?? ""}]",
            MessageSegmentType.Forward => "[合并转发]",
            MessageSegmentType.Location => "[位置]",
            MessageSegmentType.Contact => "[联系人卡片]",
            MessageSegmentType.Music => "[音乐分享]",
            MessageSegmentType.Share => "[链接分享]",
            _ => ""
        };
    }

    public static MessageSegment CreateText(string text) => new()
    {
        Type = MessageSegmentType.Text,
        Data = new() { ["text"] = text }
    };

    public static MessageSegment CreateImage(string file) => new()
    {
        Type = MessageSegmentType.Image,
        Data = new() { ["file"] = file }
    };

    public static MessageSegment CreateAt(string userId) => new()
    {
        Type = MessageSegmentType.At,
        Data = new() { ["qq"] = userId }
    };

    public static MessageSegment CreateReply(string messageId) => new()
    {
        Type = MessageSegmentType.Reply,
        Data = new() { ["id"] = messageId }
    };

    public override string ToString() => $"[{Type}]{GetSearchableText()}";
}

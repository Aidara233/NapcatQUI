using System.Text.Json.Serialization;

namespace NapcatQUI.Core.Models;

/// <summary>
/// 统一消息模型 — 私聊、群聊、系统通知统一用这个
/// </summary>
public class Message
{
    /// <summary>NapCat 原始 message_id</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>归属账号 UIN</summary>
    public string AccountId { get; set; } = string.Empty;

    public MessageType Type { get; set; }
    public MessageSubType SubType { get; set; }

    /// <summary>发送者 QQ 号</summary>
    public string SenderId { get; set; } = string.Empty;

    /// <summary>发送者昵称（消息事件中直接提供）</summary>
    public string SenderName { get; set; } = string.Empty;

    /// <summary>目标 ID：私聊为对方 QQ 号，群聊为群号</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>消息段列表 — 按原文顺序</summary>
    public List<MessageSegment> Segments { get; set; } = new();

    /// <summary>纯文本摘要 — 从 Segments 提取，用于搜索和预览</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>回复引用的消息 ID</summary>
    public string? ReplyToId { get; set; }

    /// <summary>是否是自己发出的</summary>
    public bool IsSentBySelf { get; set; }

    /// <summary>消息时间戳</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>原始 JSON（保留未解析字段）</summary>
    public string? RawJson { get; set; }

    // ---- 通知事件特有字段 ----

    /// <summary>是否为系统通知事件</summary>
    public bool IsSystemEvent { get; set; }

    /// <summary>通知子类型：group_upload / group_admin / group_decrease / group_increase / group_ban / group_card / friend_add / group_recall / friend_recall / poke / essence / offline_file</summary>
    public string? NoticeType { get; set; }

    /// <summary>通知涉及的 QQ 号（操作者或被操作者）</summary>
    public string? NoticeUserId { get; set; }

    /// <summary>通知事件额外数据（拍一拍、文件信息等）</summary>
    public Dictionary<string, object?>? NoticeData { get; set; }

    /// <summary>生成搜索用文本</summary>
    public string BuildSearchableContent()
    {
        return string.Join(" ", Segments.Select(s => s.GetSearchableText()));
    }

    /// <summary>提取第一条文本内容（用于列表预览）</summary>
    public string GetPreviewText(int maxLength = 60)
    {
        var text = BuildSearchableContent().Trim();
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageType
{
    Private = 0,  // 私聊
    Group = 1,    // 群聊
    System = 2    // 系统通知
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageSubType
{
    Normal = 0,
    Anonymous = 1,
    Notice = 2
}

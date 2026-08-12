namespace NapcatQUI.Core.Database.Entities;

using SQLite;

[Table("message")]
public class MessageEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull]
    public string AccountId { get; set; } = string.Empty;

    [NotNull]
    public string MessageId { get; set; } = string.Empty;

    public int MessageType { get; set; } // 0=Private, 1=Group, 2=System

    public int SubType { get; set; } // 0=Normal, 1=Anonymous, 2=Notice

    [NotNull]
    public string SenderId { get; set; } = string.Empty;

    /// <summary>发送者昵称（消息事件中直接提供）</summary>
    public string SenderName { get; set; } = string.Empty;

    [Indexed, NotNull]
    public string TargetId { get; set; } = string.Empty;

    /// <summary>纯文本摘要（用于搜索）</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>完整消息段 JSON</summary>
    public string SegmentsJson { get; set; } = "[]";

    public string? ReplyToId { get; set; }
    public bool IsSentBySelf { get; set; }
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>原始报文（保留未识别字段）</summary>
    public string? RawJson { get; set; }

    // 通知事件字段
    public bool IsSystemEvent { get; set; }
    public string? NoticeType { get; set; }
    public string? NoticeUserId { get; set; }
    public string? NoticeDataJson { get; set; }
}

namespace NapcatQUI.Core.Database.Entities;

using SQLite;

[Table("read_state")]
public class ReadStateEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull]
    public string AccountId { get; set; } = string.Empty;

    [NotNull]
    public string TargetId { get; set; } = string.Empty;

    /// <summary>0=Private, 1=Group（与 MessageEntity.MessageType 对齐）</summary>
    public int MessageType { get; set; }

    /// <summary>最后已读消息时间（ISO-8601 "o" 格式，字典序 == 时间序）。未读 = Timestamp 晚于该值的消息。</summary>
    public string LastReadTimestamp { get; set; } = string.Empty;
}

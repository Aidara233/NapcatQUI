namespace NapcatQUI.Core.Database.Entities;

using SQLite;

[Table("file_record")]
public class FileRecordEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull]
    public string AccountId { get; set; } = string.Empty;

    [NotNull]
    public string FileId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Url { get; set; }
    public string? LocalPath { get; set; }
    public int Source { get; set; } // 0=Private, 1=Group
    public string CreatedAt { get; set; } = string.Empty;
}

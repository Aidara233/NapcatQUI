namespace NapcatQUI.Core.Models;

public class FileRecord
{
    public int Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Url { get; set; }
    public string? LocalPath { get; set; }
    public FileSource Source { get; set; } = FileSource.Private;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum FileSource
{
    Private = 0,
    Group = 1
}

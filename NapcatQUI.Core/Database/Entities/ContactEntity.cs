namespace NapcatQUI.Core.Database.Entities;

using SQLite;

[Table("contact")]
public class ContactEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull]
    public string AccountId { get; set; } = string.Empty;

    [NotNull]
    public string UserId { get; set; } = string.Empty;

    public string Nickname { get; set; } = string.Empty;
    public string? Remark { get; set; }
    public string? AvatarUrl { get; set; }
    public string? AvatarLocalPath { get; set; }
    public string? Category { get; set; }
}

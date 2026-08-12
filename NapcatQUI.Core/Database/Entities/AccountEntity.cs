namespace NapcatQUI.Core.Database.Entities;

using SQLite;

[Table("account")]
public class AccountEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Unique, NotNull]
    public string Uin { get; set; } = string.Empty;

    public string Nickname { get; set; } = string.Empty;

    [NotNull]
    public string NapCatWsUrl { get; set; } = "ws://localhost:3001";

    public string? AccessToken { get; set; }
    public bool IsEnabled { get; set; } = true;

    public string? LastConnectedAt { get; set; }
}

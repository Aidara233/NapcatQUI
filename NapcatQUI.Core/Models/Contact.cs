namespace NapcatQUI.Core.Models;

public class Contact
{
    public int Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string? Remark { get; set; }
    public string? AvatarUrl { get; set; }
    public string? AvatarLocalPath { get; set; }
    public string? Category { get; set; }

    public string DisplayName => Remark ?? Nickname;
}

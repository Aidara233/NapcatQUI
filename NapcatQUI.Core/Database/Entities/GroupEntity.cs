namespace NapcatQUI.Core.Database.Entities;

using SQLite;

[Table("group_info")]
public class GroupEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull]
    public string AccountId { get; set; } = string.Empty;

    [NotNull]
    public string GroupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public int MaxMemberCount { get; set; }
    public string? AvatarUrl { get; set; }
    public string? AvatarLocalPath { get; set; }
    public int SelfRole { get; set; } // 0=Member, 1=Admin, 2=Owner
}

[Table("group_member")]
public class GroupMemberEntity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull]
    public int GroupDbId { get; set; }

    [NotNull]
    public string GroupId { get; set; } = string.Empty;

    [NotNull]
    public string UserId { get; set; } = string.Empty;

    public string Nickname { get; set; } = string.Empty;
    public string? Card { get; set; }
    public int Role { get; set; } // 0=Member, 1=Admin, 2=Owner
    public string? SpecialTitle { get; set; }
    public string? TitleExpireTime { get; set; }
    public string? JoinTime { get; set; }
    public string? LastSpeakTime { get; set; }
}

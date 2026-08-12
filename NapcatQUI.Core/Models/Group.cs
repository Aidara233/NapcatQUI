namespace NapcatQUI.Core.Models;

public class Group
{
    public int Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public int MaxMemberCount { get; set; }
    public string? AvatarUrl { get; set; }
    public string? AvatarLocalPath { get; set; }
    public GroupRole SelfRole { get; set; } = GroupRole.Member;
}

public enum GroupRole
{
    Member = 0,
    Admin = 1,
    Owner = 2
}

public class GroupMember
{
    public int Id { get; set; }
    public int GroupDbId { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string? Card { get; set; }
    public GroupRole Role { get; set; } = GroupRole.Member;
    public string? SpecialTitle { get; set; }
    public DateTimeOffset? TitleExpireTime { get; set; }
    public DateTimeOffset? JoinTime { get; set; }
    public DateTimeOffset? LastSpeakTime { get; set; }

    public string DisplayName => Card ?? Nickname;
}

using NapcatQUI.Core.Database.Entities;

namespace NapcatQUI.Core.Database.Repositories;

public class GroupRepository
{
    private readonly DatabaseManager _db;

    public GroupRepository(DatabaseManager db) => _db = db;

    public async Task<List<GroupEntity>> GetGroupsAsync(string accountId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<GroupEntity>()
            .Where(g => g.AccountId == accountId)
            .ToListAsync();
    }

    public async Task<GroupEntity?> GetAsync(string accountId, string groupId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<GroupEntity>()
            .Where(g => g.AccountId == accountId && g.GroupId == groupId)
            .FirstOrDefaultAsync();
    }

    public async Task DeleteAsync(string accountId, string groupId)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM group_info WHERE AccountId = ? AND GroupId = ?", accountId, groupId);
    }

    public async Task UpsertAsync(GroupEntity group)
    {
        var conn = await _db.GetConnectionAsync();
        // 见 ContactRepository：InsertOrReplaceAsync 会把 Id=0 原样写入，覆盖前一条
        await conn.ExecuteAsync(@"
            INSERT INTO group_info (AccountId, GroupId, Name, MemberCount, MaxMemberCount, AvatarUrl, AvatarLocalPath, SelfRole)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(AccountId, GroupId) DO UPDATE SET
                Name = excluded.Name,
                MemberCount = excluded.MemberCount,
                MaxMemberCount = excluded.MaxMemberCount,
                AvatarUrl = excluded.AvatarUrl,
                AvatarLocalPath = excluded.AvatarLocalPath,
                SelfRole = excluded.SelfRole",
            group.AccountId, group.GroupId, group.Name, group.MemberCount,
            group.MaxMemberCount, group.AvatarUrl, group.AvatarLocalPath, group.SelfRole);
    }

    public async Task<List<GroupMemberEntity>> GetMembersAsync(string groupId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<GroupMemberEntity>()
            .Where(m => m.GroupId == groupId)
            .ToListAsync();
    }

    public async Task<GroupMemberEntity?> GetMemberAsync(string groupId, string userId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<GroupMemberEntity>()
            .Where(m => m.GroupId == groupId && m.UserId == userId)
            .FirstOrDefaultAsync();
    }

    public async Task UpsertMemberAsync(GroupMemberEntity member)
    {
        var conn = await _db.GetConnectionAsync();
        // 同上：InsertOrReplaceAsync 会覆盖前一条，必须用 ON CONFLICT
        await conn.ExecuteAsync(@"
            INSERT INTO group_member (GroupDbId, GroupId, UserId, Nickname, Card, Role, SpecialTitle, TitleExpireTime, JoinTime, LastSpeakTime)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(GroupId, UserId) DO UPDATE SET
                GroupDbId = excluded.GroupDbId,
                Nickname = excluded.Nickname,
                Card = excluded.Card,
                Role = excluded.Role,
                SpecialTitle = excluded.SpecialTitle,
                TitleExpireTime = excluded.TitleExpireTime,
                JoinTime = excluded.JoinTime,
                LastSpeakTime = excluded.LastSpeakTime",
            member.GroupDbId, member.GroupId, member.UserId, member.Nickname, member.Card,
            member.Role, member.SpecialTitle, member.TitleExpireTime, member.JoinTime, member.LastSpeakTime);
    }

    public async Task DeleteMemberAsync(string groupId, string userId)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM group_member WHERE GroupId = ? AND UserId = ?", groupId, userId);
    }

    public async Task DeleteAllMembersAsync(string groupId)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM group_member WHERE GroupId = ?", groupId);
    }

    public async Task<int> GetMemberDbIdAsync(string groupId)
    {
        var conn = await _db.GetConnectionAsync();
        var g = await conn.Table<GroupEntity>()
            .Where(g => g.GroupId == groupId)
            .FirstOrDefaultAsync();
        return g?.Id ?? 0;
    }
}

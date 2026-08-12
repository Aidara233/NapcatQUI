using NapcatQUI.Core.Database.Entities;

namespace NapcatQUI.Core.Database.Repositories;

public class ContactRepository
{
    private readonly DatabaseManager _db;

    public ContactRepository(DatabaseManager db) => _db = db;

    public async Task<List<ContactEntity>> GetFriendsAsync(string accountId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<ContactEntity>()
            .Where(c => c.AccountId == accountId)
            .ToListAsync();
    }

    public async Task<ContactEntity?> GetAsync(string accountId, string userId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<ContactEntity>()
            .Where(c => c.AccountId == accountId && c.UserId == userId)
            .FirstOrDefaultAsync();
    }

    public async Task UpsertAsync(ContactEntity contact)
    {
        var conn = await _db.GetConnectionAsync();
        // 不能用 InsertOrReplaceAsync：它把自增主键 Id=0 原样写入 INSERT OR REPLACE，
        // 后续插入撞上 Id=0 会把前一条覆盖，导致联系人只剩最后一条。
        // 用 ON CONFLICT(AccountId, UserId) 做真正的 upsert。
        await conn.ExecuteAsync(@"
            INSERT INTO contact (AccountId, UserId, Nickname, Remark, AvatarUrl, AvatarLocalPath, Category)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(AccountId, UserId) DO UPDATE SET
                Nickname = excluded.Nickname,
                Remark = excluded.Remark,
                AvatarUrl = excluded.AvatarUrl,
                AvatarLocalPath = excluded.AvatarLocalPath,
                Category = excluded.Category",
            contact.AccountId, contact.UserId, contact.Nickname, contact.Remark,
            contact.AvatarUrl, contact.AvatarLocalPath, contact.Category);
    }

    public async Task DeleteAsync(string accountId, string userId)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM contact WHERE AccountId = ? AND UserId = ?", accountId, userId);
    }
}

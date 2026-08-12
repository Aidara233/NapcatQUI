using NapcatQUI.Core.Database.Entities;

namespace NapcatQUI.Core.Database.Repositories;

public class AccountRepository
{
    private readonly DatabaseManager _db;

    public AccountRepository(DatabaseManager db) => _db = db;

    public async Task<List<AccountEntity>> GetAllAsync()
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<AccountEntity>().ToListAsync();
    }

    public async Task<List<AccountEntity>> GetEnabledAsync()
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<AccountEntity>()
            .Where(a => a.IsEnabled)
            .ToListAsync();
    }

    public async Task<AccountEntity?> GetAsync(string uin)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<AccountEntity>()
            .Where(a => a.Uin == uin)
            .FirstOrDefaultAsync();
    }

    public async Task UpsertAsync(AccountEntity account)
    {
        var conn = await _db.GetConnectionAsync();
        // 同上：InsertOrReplaceAsync 会把自增主键 Id=0 原样写入，覆盖前一条
        await conn.ExecuteAsync(@"
            INSERT INTO account (Uin, Nickname, NapCatWsUrl, AccessToken, IsEnabled, LastConnectedAt)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(Uin) DO UPDATE SET
                Nickname = excluded.Nickname,
                NapCatWsUrl = excluded.NapCatWsUrl,
                AccessToken = excluded.AccessToken,
                IsEnabled = excluded.IsEnabled,
                LastConnectedAt = excluded.LastConnectedAt",
            account.Uin, account.Nickname, account.NapCatWsUrl, account.AccessToken,
            account.IsEnabled, account.LastConnectedAt);
    }

    public async Task UpdateLastConnectedAsync(string uin, string timestamp)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE account SET LastConnectedAt = ? WHERE Uin = ?", timestamp, uin);
    }

    /// <summary>改名（用于连接成功后从占位符解析出真实 QQ 号），保留主键与关联数据</summary>
    public async Task UpdateUinAsync(string oldUin, string newUin)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE account SET Uin = ? WHERE Uin = ?", newUin, oldUin);
    }

    /// <summary>更新昵称（连接成功后从 get_login_info 解析）</summary>
    public async Task UpdateNicknameAsync(string uin, string nickname)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE account SET Nickname = ? WHERE Uin = ?", nickname, uin);
    }

    public async Task DeleteAsync(string uin)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM account WHERE Uin = ?", uin);
    }
}

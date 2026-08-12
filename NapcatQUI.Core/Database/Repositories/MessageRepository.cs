using SQLite;
using NapcatQUI.Core.Database.Entities;

namespace NapcatQUI.Core.Database.Repositories;

public class MessageRepository
{
    private readonly DatabaseManager _db;

    public MessageRepository(DatabaseManager db) => _db = db;

    public async Task<MessageEntity?> GetByIdAsync(string accountId, string messageId)
    {
        var conn = await _db.GetConnectionAsync();
        return await conn.Table<MessageEntity>()
            .Where(m => m.AccountId == accountId && m.MessageId == messageId)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> ExistsAsync(string accountId, string messageId)
    {
        var conn = await _db.GetConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM message WHERE AccountId = ? AND MessageId = ?",
            accountId, messageId);
        return count > 0;
    }

    public async Task InsertAsync(MessageEntity message)
    {
        var conn = await _db.GetConnectionAsync();
        await conn.InsertAsync(message);
    }

    public async Task InsertOrIgnoreAsync(MessageEntity message)
    {
        var conn = await _db.GetConnectionAsync();
        // 真正的 INSERT OR IGNORE：依赖 UNIQUE(AccountId, MessageId) 去重，
        // 绝不覆盖已有行（InsertOrReplaceAsync 会改写自增主键、破坏 FTS 映射）
        await conn.InsertAsync(message, "OR IGNORE");
    }

    public async Task<List<MessageEntity>> GetHistoryAsync(
        string accountId, string targetId, int limit = 50, string? beforeTimestamp = null)
    {
        var conn = await _db.GetConnectionAsync();
        // Timestamp 是 ISO-8601 "o" 格式，字典序 == 时间序，直接字符串比较。
        // 不能把 string.Compare 写进 LINQ 表达式 —— sqlite-net 会翻译成不存在的 compare() 函数
        var sql = "SELECT * FROM message WHERE AccountId = ? AND TargetId = ? "
                  + (beforeTimestamp != null ? "AND Timestamp < ? " : "")
                  + "ORDER BY Timestamp DESC LIMIT ?";
        var args = beforeTimestamp != null
            ? new object[] { accountId, targetId, beforeTimestamp, limit }
            : new object[] { accountId, targetId, limit };
        return await conn.QueryAsync<MessageEntity>(sql, args);
    }

    public async Task<List<MessageEntity>> SearchAsync(string accountId, string query, int limit = 20)
    {
        var conn = await _db.GetConnectionAsync();

        // trigram 分词器至少需要 3 个字符，短查询（如两字中文）退回 LIKE 子串匹配
        if (query.Length < 3)
        {
            return await conn.QueryAsync<MessageEntity>(
                "SELECT * FROM message WHERE AccountId = ? AND (Content LIKE ? OR SenderName LIKE ?) ORDER BY Id DESC LIMIT ?",
                accountId, "%" + query + "%", "%" + query + "%", limit);
        }

        var sql = @"
            SELECT m.* FROM message m
            INNER JOIN message_fts fts ON m.Id = fts.rowid
            WHERE m.AccountId = ? AND message_fts MATCH ?
            ORDER BY rank LIMIT ?";
        return await conn.QueryAsync<MessageEntity>(sql, accountId, BuildFtsQuery(query), limit);
    }

    /// <summary>
    /// 每个会话的最新一条消息摘要（会话列表预览 + 排序用）。
    /// 私聊按对方 uin 分组，群聊按群号分组。
    /// </summary>
    public async Task<List<ConversationSummary>> GetConversationSummariesAsync(string accountId, int limit = 500)
    {
        var conn = await _db.GetConnectionAsync();
        // ROW_NUMBER 窗口函数取每个会话最新一条（GROUP BY 会取任意一行，内容不可靠）
        return await conn.QueryAsync<ConversationSummary>(@"
            SELECT TargetId, MessageType, Content, SenderName, SenderId, Timestamp
            FROM (
                SELECT TargetId, MessageType, Content, SenderName, SenderId, Timestamp,
                       ROW_NUMBER() OVER (PARTITION BY TargetId, MessageType ORDER BY Timestamp DESC) AS rn
                FROM message
                WHERE AccountId = ? AND MessageType IN (0, 1)
            )
            WHERE rn = 1
            ORDER BY Timestamp DESC
            LIMIT ?", accountId, limit);
    }

    /// <summary>
    /// 把用户输入转成安全的 FTS5 短语查询：去掉所有语法字符后整体加引号，
    /// 防止用户输入引号 / AND / NOT / 括号等导致 MATCH 语法错误
    /// </summary>
    private static string BuildFtsQuery(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return "\"" + sb.ToString().Trim() + "\"";
    }
}

/// <summary>会话摘要：某个会话的最新一条消息（用于列表预览与排序）</summary>
public class ConversationSummary
{
    public string TargetId { get; set; } = string.Empty;
    public int MessageType { get; set; } // 0=Private, 1=Group
    public string Content { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
}

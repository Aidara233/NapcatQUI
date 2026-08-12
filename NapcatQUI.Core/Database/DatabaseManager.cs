using Microsoft.Extensions.Logging;
using SQLite;
using NapcatQUI.Core.Database.Entities;

namespace NapcatQUI.Core.Database;

public class DatabaseManager
{
    private readonly string _dbPath;
    private readonly ILogger<DatabaseManager> _logger;
    private readonly Task _initTask;
    private SQLiteAsyncConnection? _connection;

    private readonly TaskCompletionSource<bool> _readyTcs = new();

    public DatabaseManager(string dbPath, ILogger<DatabaseManager> logger)
    {
        _dbPath = dbPath;
        _logger = logger;
        _initTask = InitAsync();
    }

    public async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        await _initTask.ConfigureAwait(false);
        return _connection!;
    }

    public SQLiteAsyncConnection Connection
    {
        get
        {
            if (_connection == null)
                throw new InvalidOperationException("Database not initialized yet. Use GetConnectionAsync.");
            return _connection;
        }
    }

    private async Task InitAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connection = new SQLiteAsyncConnection(_dbPath,
            SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.SharedCache);

        // sqlite-net-pcl 的 ExecuteAsync 不处理返回结果行的 PRAGMA，会抛 "not an error"，
        // journal_mode=WAL 恰好返回一行（当前模式），必须用 ExecuteScalarAsync
        await _connection.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");
        await _connection.ExecuteAsync("PRAGMA foreign_keys=ON;");

        await CreateTablesAsync();
        _logger.LogInformation("Database initialized at {Path}", _dbPath);
    }

    private async Task CreateTablesAsync()
    {
        await _connection!.CreateTableAsync<AccountEntity>();
        await _connection.CreateTableAsync<ContactEntity>();
        await _connection.CreateTableAsync<GroupEntity>();
        await _connection.CreateTableAsync<GroupMemberEntity>();
        await _connection.CreateTableAsync<MessageEntity>();
        await _connection.CreateTableAsync<FileRecordEntity>();
        await _connection.CreateTableAsync<ReadStateEntity>();

        await _connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_message_unique ON message(AccountId, MessageId);");
        await _connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_read_state_unique ON read_state(AccountId, TargetId, MessageType);");
        await _connection.ExecuteAsync(
            "CREATE INDEX IF NOT EXISTS idx_message_target_time ON message(AccountId, TargetId, Timestamp);");
        await _connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_contact_unique ON contact(AccountId, UserId);");
        await _connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_group_unique ON group_info(AccountId, GroupId);");
        await _connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_group_member_unique ON group_member(GroupId, UserId);");
        // FTS：external-content 表，列名必须与 message 表的列名匹配（大小写不敏感）。
        // 旧版本声明过 sender_name/target_name，与 message.SenderName/TargetId 对不上，
        // 查询会抛 "no such column" —— 先删后建，schema 自愈，再回填已有索引。
        await _connection.ExecuteAsync("DROP TRIGGER IF EXISTS message_ai;");
        await _connection.ExecuteAsync("DROP TRIGGER IF EXISTS message_ad;");
        await _connection.ExecuteAsync("DROP TABLE IF EXISTS message_fts;");
        await _connection.ExecuteAsync(
            "CREATE VIRTUAL TABLE IF NOT EXISTS message_fts USING fts5(content, tokenize='trigram', content='message', content_rowid='Id');");
        await _connection.ExecuteAsync(@"
            CREATE TRIGGER IF NOT EXISTS message_ai AFTER INSERT ON message BEGIN
                INSERT INTO message_fts(rowid, content) VALUES (new.Id, new.Content);
            END;");
        await _connection.ExecuteAsync(@"
            CREATE TRIGGER IF NOT EXISTS message_ad AFTER DELETE ON message BEGIN
                INSERT INTO message_fts(message_fts, rowid, content) VALUES ('delete', old.Id, old.Content);
            END;");
        await _connection.ExecuteAsync(
            "INSERT INTO message_fts(rowid, content) SELECT Id, Content FROM message;");

        _logger.LogInformation("Database tables created/migrated");
    }
}

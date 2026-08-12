using System.Text.Json;
using Microsoft.Extensions.Logging;
using NapcatQUI.Core.Database;
using NapcatQUI.Core.Database.Entities;
using NapcatQUI.Core.Database.Repositories;
using NapcatQUI.Core.Events;
using NapcatQUI.Core.Models;

namespace NapcatQUI.Core.Adapter;

public class AccountSession : IAsyncDisposable
{
    private readonly AccountEntity _account;
    private readonly ILogger<AccountSession> _logger;
    private readonly ILogger<NapCatConnection> _connectionLogger;
    private readonly OneBotMessageParser _parser;
    private readonly MessageRepository _messageRepo;
    private readonly ContactRepository _contactRepo;
    private readonly GroupRepository _groupRepo;
    private readonly AccountRepository _accountRepo;
    private CancellationTokenSource? _cts;
    private NapCatConnection? _connection;
    private Task? _reconnectTask;

    public string AccountId => _account.Uin;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>消息到达事件</summary>
    public event Func<Message, Task>? OnMessage;
    public event Func<ConnectionState, Task>? OnConnectionStateChanged;
    public event Func<ContactEntity, Task>? OnContactUpdated;
    public event Func<GroupEntity, Task>? OnGroupUpdated;
    public event Func<GroupMemberEntity, Task>? OnGroupMemberUpdated;

    /// <summary>连接成功后解析出真实 QQ 号（占位符 → 真实 uin）时触发</summary>
    public event Func<string, string, Task>? OnSelfUinResolved;

    public AccountSession(
        AccountEntity account,
        ILogger<AccountSession> logger,
        ILogger<NapCatConnection> connectionLogger,
        OneBotMessageParser parser,
        MessageRepository messageRepo,
        ContactRepository contactRepo,
        GroupRepository groupRepo,
        AccountRepository accountRepo)
    {
        _account = account;
        _logger = logger;
        _connectionLogger = connectionLogger;
        _parser = parser;
        _messageRepo = messageRepo;
        _contactRepo = contactRepo;
        _groupRepo = groupRepo;
        _accountRepo = accountRepo;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        // 立即返回，连接在后台循环里跑（含自动重连），不阻塞调用方/后续账号启动
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectTask = RunConnectionLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        var conn = _connection;
        _connection = null;
        if (conn != null)
            await conn.DisconnectAsync();
        SetState(ConnectionState.Disconnected);
    }

    public async Task<string?> SendPrivateMessageAsync(string userId, List<MessageSegment> segments)
    {
        if (_connection == null) return null;
        var result = await _connection.SendApiRequestAsync("send_private_msg", new()
        {
            ["user_id"] = userId,
            ["message"] = SerializeSegments(segments)
        });
        var messageId = ExtractMessageId(result);
        if (messageId != null)
            await SaveSentMessageAsync(MessageType.Private, userId, segments, messageId);
        return messageId;
    }

    public async Task<string?> SendGroupMessageAsync(string groupId, List<MessageSegment> segments)
    {
        if (_connection == null) return null;
        var result = await _connection.SendApiRequestAsync("send_group_msg", new()
        {
            ["group_id"] = groupId,
            ["message"] = SerializeSegments(segments)
        });
        var messageId = ExtractMessageId(result);
        if (messageId != null)
            await SaveSentMessageAsync(MessageType.Group, groupId, segments, messageId);
        return messageId;
    }

    /// <summary>发送戳一戳（NapCat 扩展动作 send_poke）</summary>
    public async Task<bool> SendPokeAsync(string userId, string? groupId = null)
    {
        if (_connection == null) return false;
        try
        {
            var @params = new Dictionary<string, object?> { ["user_id"] = userId };
            if (!string.IsNullOrEmpty(groupId))
                @params["group_id"] = groupId;

            var result = await _connection.SendApiRequestAsync("send_poke", @params);
            return result is not null &&
                   result.RootElement.TryGetProperty("status", out var st) &&
                   st.GetString() == "ok";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send poke to {UserId}", userId);
            return false;
        }
    }

    public async Task<JsonDocument?> CallApiAsync(string action, Dictionary<string, object?>? @params = null)
    {
        return _connection != null
            ? await _connection.SendApiRequestAsync(action, @params)
            : null;
    }

    // ---- 连接管理 ----

    /// <summary>
    /// 单一连接循环：连接 → 解析自身 uin → 同步 → 阻塞到掉线 → 指数退避重连。
    /// 首次连接失败同样走重连，不会停在 Disconnected 等用户手动点。
    /// </summary>
    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            SetState(attempt == 1 ? ConnectionState.Connecting : ConnectionState.Reconnecting);

            NapCatConnection? conn = null;
            try
            {
                conn = new NapCatConnection(_account.NapCatWsUrl, _account.AccessToken, _connectionLogger);
                conn.OnMessageReceived += OnRawMessage;
                _connection = conn;

                await conn.ConnectAsync(ct);

                // 只填了 WS 地址、还没填 QQ 号时，连上后自动解析
                await ResolveSelfAsync(conn);

                SetState(ConnectionState.Connected);
                await SyncContactsAsync();
                await SyncGroupsAsync();

                _logger.LogInformation("Account {Uin} connected", _account.Uin);

                // 阻塞到连接断开（Close 帧 / 收发异常），随后进入重连
                await conn.Closed;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection attempt #{Attempt} failed for {Uin}", attempt, _account.Uin);
            }
            finally
            {
                if (conn != null)
                {
                    conn.OnMessageReceived -= OnRawMessage;
                    if (ReferenceEquals(_connection, conn)) _connection = null;
                    await conn.DisposeAsync();
                }
            }

            if (ct.IsCancellationRequested) return;

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 30));
        }
    }

    /// <summary>
    /// 通过 get_login_info 解析真实 QQ 号。占位符 uin 会被改写为真实值，
    /// 并同步更新数据库与 config（经 AccountManager 转发），让"只填 WS 地址"可用。
    /// </summary>
    private async Task ResolveSelfAsync(NapCatConnection conn)
    {
        try
        {
            var result = await conn.SendApiRequestAsync("get_login_info");
            if (result == null) return;
            var root = result.RootElement;

            if (!root.TryGetProperty("data", out var data)) return;
            var resolvedUin = data.TryGetProperty("user_id", out var uid)
                ? uid.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => uid.GetInt64().ToString(),
                    System.Text.Json.JsonValueKind.String => uid.GetString(),
                    _ => null
                }
                : null;

            // 昵称：仅当用户没手动填时才用登录信息覆盖
            var resolvedNick = GetJsonString(data, "nickname");
            if (!string.IsNullOrEmpty(resolvedNick) && string.IsNullOrEmpty(_account.Nickname))
            {
                _account.Nickname = resolvedNick;
                try { await _accountRepo.UpdateNicknameAsync(_account.Uin, resolvedNick); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist nickname"); }
            }

            if (string.IsNullOrEmpty(resolvedUin) || resolvedUin == _account.Uin) return;

            var oldUin = _account.Uin;
            _logger.LogInformation("Resolved self uin: {OldUin} -> {NewUin}", oldUin, resolvedUin);
            _account.Uin = resolvedUin;

            try
            {
                await _accountRepo.UpdateUinAsync(oldUin, resolvedUin);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist resolved uin {NewUin}", resolvedUin);
            }

            if (OnSelfUinResolved != null)
                await OnSelfUinResolved.Invoke(oldUin, resolvedUin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve self uin for {Uin}", _account.Uin);
        }
    }

    private void SetState(ConnectionState newState)
    {
        if (State == newState) return;
        State = newState;
        try
        {
            _ = OnConnectionStateChanged?.Invoke(newState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnConnectionStateChanged handler threw for {Uin}", _account.Uin);
        }
    }

    // ---- 联系人/群同步 ----

    public async Task SyncContactsAsync()
    {
        if (_connection == null) return;
        try
        {
            var result = await _connection.SendApiRequestAsync("get_friend_list");
            if (result == null) return;

            var data = result.RootElement;
            if (data.TryGetProperty("data", out var friendList) && friendList.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var friend in friendList.EnumerateArray())
                {
                    var contact = new ContactEntity
                    {
                        AccountId = _account.Uin,
                        UserId = GetId(friend, "user_id") ?? "",
                        Nickname = GetJsonString(friend, "nickname") ?? "",
                        Remark = GetJsonString(friend, "remark")
                    };
                    await _contactRepo.UpsertAsync(contact);
                    _ = OnContactUpdated?.Invoke(contact);
                }

                // 清理历史 bug 产生的空 ID 脏数据（旧版把数字 ID 解析成 ""，全部折叠成一行）
                await _contactRepo.DeleteAsync(_account.Uin, "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync contacts for {Uin}", _account.Uin);
        }
    }

    public async Task SyncGroupsAsync()
    {
        if (_connection == null) return;
        try
        {
            var result = await _connection.SendApiRequestAsync("get_group_list");
            if (result == null) return;

            var data = result.RootElement;
            if (data.TryGetProperty("data", out var groupList) && groupList.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var g in groupList.EnumerateArray())
                {
                    var group = new GroupEntity
                    {
                        AccountId = _account.Uin,
                        GroupId = GetId(g, "group_id") ?? "",
                        Name = GetJsonString(g, "group_name") ?? "",
                        MemberCount = GetJsonInt(g, "member_count"),
                        MaxMemberCount = GetJsonInt(g, "max_member_count")
                    };
                    await _groupRepo.UpsertAsync(group);
                    _ = OnGroupUpdated?.Invoke(group);
                }

                // 清理历史 bug 产生的空 ID 脏数据
                await _groupRepo.DeleteAsync(_account.Uin, "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync groups for {Uin}", _account.Uin);
        }
    }

    public async Task SyncGroupMembersAsync(string groupId)
    {
        if (_connection == null) return;
        try
        {
            var result = await _connection.SendApiRequestAsync("get_group_member_list", new()
            {
                ["group_id"] = groupId
            });
            if (result == null) return;

            var data = result.RootElement;
            if (data.TryGetProperty("data", out var memberList) && memberList.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in memberList.EnumerateArray())
                {
                    var member = new GroupMemberEntity
                    {
                        GroupId = groupId,
                        UserId = GetId(m, "user_id") ?? "",
                        Nickname = GetJsonString(m, "nickname") ?? "",
                        Card = GetJsonString(m, "card"),
                        Role = GetJsonString(m, "role") switch
                        {
                            "owner" => 2,
                            "admin" => 1,
                            _ => 0
                        },
                        SpecialTitle = GetJsonString(m, "title"),
                        TitleExpireTime = GetTimestampString(m, "title_expire_time"),
                        JoinTime = GetTimestampString(m, "join_time"),
                        LastSpeakTime = GetTimestampString(m, "last_sent_time")
                    };
                    await _groupRepo.UpsertMemberAsync(member);
                    _ = OnGroupMemberUpdated?.Invoke(member);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync group members for {GroupId}", groupId);
        }
    }

    public async Task<List<Message>> FetchHistoryAsync(string targetId, MessageType type, int count = 50)
    {
        if (_connection == null) return new();
        var action = type == MessageType.Group ? "get_group_msg_history" : "get_friend_msg_history";
        var key = type == MessageType.Group ? "group_id" : "user_id";

        try
        {
            var result = await _connection.SendApiRequestAsync(action, new()
            {
                [key] = targetId,
                ["count"] = count
            });

            if (result == null) return new();

            var messages = new List<Message>();
            var data = result.RootElement;
            var msgArray = data.TryGetProperty("data", out var d) && d.TryGetProperty("messages", out var ms)
                ? ms : default;

            if (msgArray.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in msgArray.EnumerateArray())
                {
                    var raw = m.GetRawText();
                    var msg = _parser.Parse(raw);
                    if (msg != null)
                    {
                        msg.AccountId = _account.Uin;
                        messages.Add(msg);
                    }
                }
            }

            messages.Reverse();
            foreach (var msg in messages)
                await SaveMessageAsync(msg);

            return messages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch history for {TargetId}", targetId);
            return new();
        }
    }

    // ---- 内部 ----

    private async Task OnRawMessage(string json)
    {
        var msg = _parser.Parse(json);
        if (msg == null) return;

        msg.AccountId = _account.Uin;
        await SaveMessageAsync(msg);

        if (OnMessage != null)
            await OnMessage.Invoke(msg);
    }

    /// <summary>
    /// 发送成功后把消息入库（用正确的 TargetId + 真实 message_id）。
    /// 回声事件里私聊自发消息没有接收者信息，若依赖回声入库会把消息挂到自己名下。
    /// </summary>
    private async Task SaveSentMessageAsync(MessageType type, string targetId, List<MessageSegment> segments, string messageId)
    {
        try
        {
            var msg = new Message
            {
                MessageId = messageId,
                AccountId = _account.Uin,
                Type = type,
                SubType = MessageSubType.Normal,
                SenderId = _account.Uin,
                SenderName = _account.Nickname,
                TargetId = targetId,
                Segments = segments,
                IsSentBySelf = true,
                Timestamp = DateTimeOffset.UtcNow
            };
            msg.Content = msg.BuildSearchableContent();
            await SaveMessageAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sent message {MessageId}", messageId);
        }
    }

    private async Task SaveMessageAsync(Message msg)
    {
        try
        {
            // 私聊自发消息的回声：事件里没有接收者，TargetId 落在自己身上，
            // 已由发送路径用正确 TargetId 入库，这里跳过避免脏数据
            if (msg.IsSentBySelf && msg.Type == MessageType.Private && msg.TargetId == msg.AccountId)
                return;

            if (await _messageRepo.ExistsAsync(_account.Uin, msg.MessageId))
                return;

            var entity = new MessageEntity
            {
                AccountId = _account.Uin,
                MessageId = msg.MessageId,
                MessageType = (int)msg.Type,
                SubType = (int)msg.SubType,
                SenderId = msg.SenderId,
                SenderName = msg.SenderName,
                TargetId = msg.TargetId,
                Content = msg.Content,
                SegmentsJson = System.Text.Json.JsonSerializer.Serialize(msg.Segments),
                ReplyToId = msg.ReplyToId,
                IsSentBySelf = msg.IsSentBySelf,
                Timestamp = msg.Timestamp.ToString("o"),
                RawJson = msg.RawJson,
                IsSystemEvent = msg.IsSystemEvent,
                NoticeType = msg.NoticeType,
                NoticeUserId = msg.NoticeUserId,
                NoticeDataJson = msg.NoticeData != null
                    ? System.Text.Json.JsonSerializer.Serialize(msg.NoticeData) : null
            };

            await _messageRepo.InsertOrIgnoreAsync(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId}", msg.MessageId);
        }
    }

    private static string? ExtractMessageId(JsonDocument? doc)
    {
        if (doc == null) return null;
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("message_id", out var mid))
            return null;

        // message_id 可能是数字也可能是字符串，且可能超出 int 范围
        return mid.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => mid.GetInt64().ToString(),
            System.Text.Json.JsonValueKind.String => mid.GetString(),
            _ => null
        };
    }

    private static List<Dictionary<string, object?>> SerializeSegments(List<MessageSegment> segments)
    {
        return segments.Select(s => new Dictionary<string, object?>
        {
            ["type"] = s.Type.ToString().ToLowerInvariant(),
            ["data"] = s.Data
        }).ToList();
    }

    private static string? GetJsonString(System.Text.Json.JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() : null;
    }

    /// <summary>读取 ID 类字段（QQ 号/群号）：OneBot v11 是数字，兼容字符串</summary>
    private static string? GetId(System.Text.Json.JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number) return v.GetInt64().ToString();
            if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        }
        return null;
    }

    /// <summary>读取时间戳：unix 秒（数字）转 ISO-8601，兼容字符串</summary>
    private static string? GetTimestampString(System.Text.Json.JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number)
                return DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()).ToString("o");
            if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        }
        return null;
    }

    private static int GetJsonInt(System.Text.Json.JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
            return v.GetInt32();
        if (el.TryGetProperty(prop, out v) && v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), out var n))
            return n;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_connection != null)
        {
            _connection.OnMessageReceived -= OnRawMessage;
            await _connection.DisposeAsync();
        }
        _cts?.Dispose();
    }
}

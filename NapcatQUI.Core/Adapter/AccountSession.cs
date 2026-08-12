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

    /// <summary>历史补偿并发上限：NapCat 同源多会话并行拉取，控一下别把通道打爆</summary>
    private readonly SemaphoreSlim _catchUpThrottle = new(6, 6);

    public string AccountId => _account.Uin;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>消息到达事件</summary>
    public event Func<Message, Task>? OnMessage;
    /// <summary>启动/重连后的历史补偿全部完成时触发，UI 据此重算未读数</summary>
    public event Func<Task>? OnHistoryCaughtUp;
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
            ["message"] = SerializeSegmentsForSend(segments)
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
            ["message"] = SerializeSegmentsForSend(segments)
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

    /// <summary>
    /// 上传并发送一个文件（作为独立文件卡片消息）。file 用 base64:// 内嵌，
    /// 因为 NapCat 常在远程服务器，本地路径读不到（和图片同理）。
    /// </summary>
    public async Task<bool> UploadFileAsync(string targetId, MessageType type, string filePath, string fileName)
    {
        if (_connection == null) return false;
        try
        {
            if (!File.Exists(filePath)) return false;
            var bytes = File.ReadAllBytes(filePath);

            var action = type == MessageType.Group ? "upload_group_file" : "upload_private_file";
            var key = type == MessageType.Group ? "group_id" : "user_id";
            // 大文件 base64 上传在 NapCat 端解码+上传后才回包，20s 默认超时不够
            var result = await _connection.SendApiRequestAsync(action, new()
            {
                [key] = targetId,
                ["file"] = "base64://" + Convert.ToBase64String(bytes),
                ["name"] = fileName
            }, TimeSpan.FromMinutes(5));
            return result is not null &&
                   result.RootElement.TryGetProperty("status", out var st) &&
                   st.GetString() == "ok";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file {Name} to {TargetId}", fileName, targetId);
            return false;
        }
    }

    /// <summary>取文件下载信息（base64/url），供远程 NapCat 场景下载收到的文件。</summary>
    public async Task<JsonDocument?> GetFileAsync(string fileId)
    {
        return await CallApiAsync("get_file", new() { ["file_id"] = fileId });
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
                StartHistoryCatchUp(ct);

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
    /// 连接建立后后台跑全量历史补偿：对每个好友/群拉最近 ~100 条入库（未读由此得出）。
    /// 有并发上限，不阻塞连接循环；全部完成后触发 OnHistoryCaughtUp 让 UI 重算未读。
    /// 会话停止时随 _cts 取消。
    /// </summary>
    private void StartHistoryCatchUp(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var targets = new List<(string TargetId, MessageType Type)>();
                foreach (var c in await _contactRepo.GetFriendsAsync(_account.Uin))
                    targets.Add((c.UserId, MessageType.Private));
                foreach (var g in await _groupRepo.GetGroupsAsync(_account.Uin))
                    targets.Add((g.GroupId, MessageType.Group));

                if (targets.Count == 0) return;

                var tasks = targets.Select(async t =>
                {
                    await _catchUpThrottle.WaitAsync(ct);
                    try
                    {
                        await CatchUpHistoryAsync(t.TargetId, t.Type, 100, ct);
                    }
                    finally
                    {
                        _catchUpThrottle.Release();
                    }
                });
                await Task.WhenAll(tasks);

                if (OnHistoryCaughtUp != null)
                    await OnHistoryCaughtUp.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "History catch-up failed for {Uin}", _account.Uin);
            }
        }, ct);
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

    // ---- 历史拉取（分页补偿 / 手动翻页共用一套） ----

    /// <summary>
    /// 单页历史：message_seq="0" 取最新一批，否则以该短 message_id 为起点往回翻。
    /// 返回按时间从旧到新。历史是按会话拉的，接收者已知，这里强制 TargetId=targetId ——
    /// 这是自发私聊消息能落对会话的关键（NapCat 事件里没有接收者，只能靠历史补偿定位）。
    /// 返回 null 表示连接不可用或接口报错（可重试）；空列表表示确实没有更早消息（终态）。
    /// </summary>
    private async Task<List<Message>?> FetchHistoryPageAsync(string targetId, MessageType type, string messageSeq, int count)
    {
        if (_connection == null) return null;
        var action = type == MessageType.Group ? "get_group_msg_history" : "get_friend_msg_history";
        var key = type == MessageType.Group ? "group_id" : "user_id";

        try
        {
            var result = await _connection.SendApiRequestAsync(action, new()
            {
                [key] = targetId,
                ["message_seq"] = messageSeq,
                ["count"] = count,
                ["reverse_order"] = true
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
                    var msg = _parser.Parse(m.GetRawText());
                    if (msg != null)
                    {
                        msg.AccountId = _account.Uin;
                        msg.TargetId = targetId;
                        messages.Add(msg);
                    }
                }
            }

            // 接口按新→旧返回，转成旧→新（历史界面最上面是最早的）
            messages.Reverse();
            return messages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch history page for {TargetId} seq={Seq}", targetId, messageSeq);
            return null;
        }
    }

    /// <summary>拉页带重试：瞬时错误（null）最多重试 3 次，仍失败才返回 null 让调用方收尾。</summary>
    private async Task<List<Message>?> FetchHistoryPageWithRetryAsync(
        string targetId, MessageType type, string messageSeq, int count, CancellationToken ct = default)
    {
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchHistoryPageAsync(targetId, type, messageSeq, count);
            if (page is not null) return page;
            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);
        }
        return null;
    }

    /// <summary>
    /// 启动/重连补偿：从最新一批往回翻，逐批入库，直到达到预算或翻到历史起点。
    /// 单页 20（QQNT 原生上限，count 设大不可靠）。
    ///
    /// 特意不把「某页全是已入库消息」当作停批条件：上一轮追赶若在中间被瞬时错误中断，
    /// 会留下中间空洞；靠「已入库就不拉」会让洞被永久跳过。无条件把预算内的窗口全部从
    /// 远端过一遍（INSERT OR IGNORE 天然去重）才能兜住。代价是每连接多拉几页，由
    /// maxMessages 封顶，下轮连接仍会重跑全窗口。
    /// </summary>
    public async Task<int> CatchUpHistoryAsync(string targetId, MessageType type, int maxMessages = 100, CancellationToken ct = default)
    {
        const int pageSize = 20;
        var maxPages = Math.Max(1, maxMessages / pageSize);
        var inserted = 0;
        var fetched = 0;
        string? seq = null;

        for (var page = 0; page < maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageMsgs = await FetchHistoryPageWithRetryAsync(targetId, type, seq ?? "0", pageSize, ct);
            if (pageMsgs is null)
            {
                // 重试耗尽：本轮没追完，但下轮连接会重跑全窗口，不会留死洞
                _logger.LogWarning("History catch-up for {TargetId} aborted: page fetch failed after retries", targetId);
                break;
            }
            fetched += pageMsgs.Count;
            if (pageMsgs.Count == 0) break;          // 确实没有更早的了

            foreach (var msg in pageMsgs)
                if (await SaveMessageAsync(msg)) inserted++;

            if (pageMsgs.Count < pageSize) break;    // 不满整页 → 到历史起点
            if (fetched >= maxMessages) break;       // 达到预算

            seq = pageMsgs[0].MessageId;             // 以最老一条为起点继续往回翻
            if (string.IsNullOrEmpty(seq)) break;
        }

        return inserted;
    }

    /// <summary>打开一个零本地历史的会话时兜底拉一份（最多 count 条），入库并返回。</summary>
    public async Task<List<Message>> FetchHistoryAsync(string targetId, MessageType type, int count = 50)
    {
        var maxMessages = Math.Max(count, 20);
        var maxPages = Math.Max(1, maxMessages / 20);
        var collected = new List<Message>();
        string? seq = null;

        for (var page = 0; page < maxPages; page++)
        {
            var pageMsgs = await FetchHistoryPageWithRetryAsync(targetId, type, seq ?? "0", 20);
            if (pageMsgs is null || pageMsgs.Count == 0) break;
            collected.AddRange(pageMsgs);
            if (pageMsgs.Count < 20 || collected.Count >= maxMessages) break;
            seq = pageMsgs[0].MessageId;
            if (string.IsNullOrEmpty(seq)) break;
        }

        foreach (var msg in collected)
            await SaveMessageAsync(msg);

        collected.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return collected;
    }

    /// <summary>
    /// 手动"加载更早"：以 beforeMessageId（该会话最老一条的短 id）为起点往回翻一页并入库。
    /// 返回旧→新的更早消息；返回空表示已到最早。reverse_order 会把起点消息也带回，
    /// 由调用方按 MessageId 去重。
    /// </summary>
    public async Task<List<Message>> FetchOlderMessagesAsync(string targetId, MessageType type, string beforeMessageId, int count = 20)
    {
        var msgs = await FetchHistoryPageWithRetryAsync(targetId, type, beforeMessageId, count);
        if (msgs is null) return new();
        foreach (var msg in msgs)
            await SaveMessageAsync(msg);
        return msgs;
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

    /// <summary>入库一条消息，返回是否真正新增（已存在则 false）。历史补偿靠它累计新增数。</summary>
    private async Task<bool> SaveMessageAsync(Message msg)
    {
        try
        {
            // 私聊自发消息：NapCat 事件里没有接收者（无 target_id），TargetId 回落成自己。
            // 本程序发出的回声已由发送路径用正确 TargetId 入库，message_id 查得到 → 跳过即可。
            // 其他设备发的（message_id 查不到）实时无法定位接收者，跳过等历史补偿按会话捞回。
            if (msg.IsSentBySelf && msg.Type == MessageType.Private && msg.TargetId == msg.AccountId)
            {
                if (await _messageRepo.ExistsAsync(_account.Uin, msg.MessageId))
                    return false;
                _logger.LogDebug("Self-sent private message {MessageId} has no recipient in event, deferring to history catch-up", msg.MessageId);
                return false;
            }

            if (await _messageRepo.ExistsAsync(_account.Uin, msg.MessageId))
                return false;

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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId}", msg.MessageId);
            return false;
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

    /// <summary>
    /// 发送前序列化：把本地图片文件转成 base64:// 内嵌。
    /// NapCat 可能不在本机（远程服务器/Docker），裸本地路径 existsSync 命中不了必然失败；
    /// base64:// 由 NapCat 解码落盘再上传，不依赖双方共享文件系统。
    /// </summary>
    private static List<Dictionary<string, object?>> SerializeSegmentsForSend(List<MessageSegment> segments)
    {
        var result = new List<Dictionary<string, object?>>(segments.Count);
        foreach (var s in segments)
        {
            var data = s.Data;
            if (s.Type == MessageSegmentType.Image && data.TryGetValue("file", out var f) &&
                f is string file && file.Length > 0 && !file.Contains("://") && File.Exists(file))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    data = new Dictionary<string, object?>(data) { ["file"] = "base64://" + Convert.ToBase64String(bytes) };
                }
                catch (Exception)
                {
                    // 读不到就原样交给 NapCat，由它报具体错误
                }
            }
            result.Add(new Dictionary<string, object?>
            {
                ["type"] = s.Type.ToString().ToLowerInvariant(),
                ["data"] = data
            });
        }
        return result;
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
        _catchUpThrottle.Dispose();
    }
}

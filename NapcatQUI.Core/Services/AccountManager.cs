using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NapcatQUI.Core.Adapter;
using NapcatQUI.Core.Configuration;
using NapcatQUI.Core.Database.Repositories;
using NapcatQUI.Core.Database.Entities;
using NapcatQUI.Core.Models;

namespace NapcatQUI.Core.Services;

public class AccountManager : IAsyncDisposable
{
    private readonly AccountRepository _accountRepo;
    private readonly ILogger<AccountManager> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, AccountSession> _sessions = new();

    public event Func<Message, Task>? OnMessage;
    public event Func<string, ConnectionState, Task>? OnAccountStateChanged;

    /// <summary>账号的占位符 QQ 号被解析为真实 uin 时触发（oldUin → newUin）</summary>
    public event Func<string, string, Task>? OnAccountUinResolved;

    /// <summary>该账号的好友/群数据有更新（同步完成或运行中变化），UI 可据此刷新会话列表</summary>
    public event Func<string, Task>? OnContactsChanged;
    public event Func<string, Task>? OnGroupsChanged;

    public IReadOnlyDictionary<string, AccountSession> Sessions => _sessions;
    public IEnumerable<string> AccountIds => _sessions.Keys;

    public AccountManager(
        AccountRepository accountRepo,
        ILogger<AccountManager> logger,
        IServiceProvider serviceProvider,
        ConfigManager configManager)
    {
        _accountRepo = accountRepo;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configManager = configManager;
    }

    public async Task InitializeAsync()
    {
        var config = _configManager.Load();

        if (config.Accounts.Count > 0)
        {
            var existing = await _accountRepo.GetAllAsync();
            var existingUins = new HashSet<string>(existing.Select(a => a.Uin));

            foreach (var acc in config.Accounts)
            {
                if (!existingUins.Contains(acc.Uin))
                {
                    await _accountRepo.UpsertAsync(new AccountEntity
                    {
                        Uin = acc.Uin,
                        Nickname = acc.Nickname,
                        NapCatWsUrl = acc.NapCatWsUrl,
                        AccessToken = acc.AccessToken,
                        IsEnabled = acc.IsEnabled
                    });
                }
            }
        }
    }

    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var accounts = await _accountRepo.GetEnabledAsync();
        _logger.LogInformation("Starting {Count} account(s)", accounts.Count);

        foreach (var account in accounts)
            await StartAccountAsync(account, ct);
    }

    public async Task StartAccountAsync(AccountEntity account, CancellationToken ct = default)
    {
        if (_sessions.ContainsKey(account.Uin))
        {
            _logger.LogWarning("Account {Uin} already running", account.Uin);
            return;
        }

        var session = new AccountSession(
            account,
            (_serviceProvider.GetService(typeof(ILogger<AccountSession>)) as ILogger<AccountSession>)!,
            (_serviceProvider.GetService(typeof(ILogger<NapCatConnection>)) as ILogger<NapCatConnection>)!,
            _serviceProvider.GetService(typeof(OneBotMessageParser)) as OneBotMessageParser
                ?? throw new InvalidOperationException("OneBotMessageParser not registered"),
            _serviceProvider.GetService(typeof(MessageRepository)) as MessageRepository
                ?? throw new InvalidOperationException("MessageRepository not registered"),
            _serviceProvider.GetService(typeof(ContactRepository)) as ContactRepository
                ?? throw new InvalidOperationException("ContactRepository not registered"),
            _serviceProvider.GetService(typeof(GroupRepository)) as GroupRepository
                ?? throw new InvalidOperationException("GroupRepository not registered"),
            _serviceProvider.GetService(typeof(AccountRepository)) as AccountRepository
                ?? throw new InvalidOperationException("AccountRepository not registered"));

        // 占位符 uin 解析成真实 QQ 号后：迁移会话 key + 通知客户端刷新
        session.OnSelfUinResolved += (oldUin, newUin) =>
        {
            try
            {
                if (_sessions.TryRemove(oldUin, out var s) && ReferenceEquals(s, session))
                    _sessions[newUin] = session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-key session {OldUin} -> {NewUin}", oldUin, newUin);
            }
            try
            {
                _ = (OnAccountUinResolved?.Invoke(oldUin, newUin) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnAccountUinResolved handler for {NewUin}", newUin);
            }
            return Task.CompletedTask;
        };

        session.OnMessage += msg =>
        {
            try
            {
                _ = (OnMessage?.Invoke(msg) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnMessage handler for {Uin}", account.Uin);
            }
            return Task.CompletedTask;
        };

        session.OnConnectionStateChanged += state =>
        {
            try
            {
                _ = (OnAccountStateChanged?.Invoke(account.Uin, state) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnAccountStateChanged handler for {Uin}", account.Uin);
            }
            return Task.CompletedTask;
        };

        session.OnContactUpdated += contact =>
        {
            try { _ = (OnContactsChanged?.Invoke(account.Uin) ?? Task.CompletedTask); }
            catch (Exception ex) { _logger.LogError(ex, "Error in OnContactsChanged handler for {Uin}", account.Uin); }
            return Task.CompletedTask;
        };

        session.OnGroupUpdated += group =>
        {
            try { _ = (OnGroupsChanged?.Invoke(account.Uin) ?? Task.CompletedTask); }
            catch (Exception ex) { _logger.LogError(ex, "Error in OnGroupsChanged handler for {Uin}", account.Uin); }
            return Task.CompletedTask;
        };

        await session.StartAsync(ct);
        _sessions[account.Uin] = session;

        _logger.LogInformation("Account {Uin} started", account.Uin);
    }

    public async Task StopAccountAsync(string uin)
    {
        if (_sessions.TryRemove(uin, out var session))
        {
            await session.StopAsync();
            await session.DisposeAsync();
            _logger.LogInformation("Account {Uin} stopped", uin);
        }
    }

    public AccountSession? GetSession(string uin)
    {
        return _sessions.TryGetValue(uin, out var session) ? session : null;
    }

    public async Task<AccountEntity> AddAccountAsync(
        string uin, string wsUrl, string? accessToken = null, string nickname = "")
    {
        var account = new AccountEntity
        {
            Uin = uin,
            Nickname = nickname,
            NapCatWsUrl = wsUrl,
            AccessToken = accessToken,
            IsEnabled = true
        };
        await _accountRepo.UpsertAsync(account);
        return account;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _sessions.ToArray())
        {
            try
            {
                await kvp.Value.StopAsync();
                await kvp.Value.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing session {Uin}", kvp.Key);
            }
        }
        _sessions.Clear();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapcatQUI.Client.Models;
using NapcatQUI.Core.Configuration;
using NapcatQUI.Core.Database.Entities;
using NapcatQUI.Core.Database.Repositories;
using NapcatQUI.Core.Models;
using NapcatQUI.Core.Services;

namespace NapcatQUI.Client.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AccountManager? _accountManager;
    private readonly HistoryService? _historyService;
    private readonly ContactSyncService? _contactSyncService;
    private readonly ConfigManager? _configManager;
    private readonly AccountRepository? _accountRepo;
    private readonly ImageCacheService? _imageCache;

    /// <summary>账号级头像缓存（url → Bitmap），切换账号时释放</summary>
    private readonly Dictionary<string, Bitmap> _avatarCache = new();

    /// <summary>群成员名字映射（groupId → userId → 显示名），解析 @ 用</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _memberNameMaps = new();

    private List<ConversationItem> _allConversations = new();
    private readonly Dictionary<string, ConversationItem> _conversationMap = new();
    private readonly Dictionary<string, List<MessageItem>> _messageCache = new();

    /// <summary>数据刷新防抖：同步时大量事件涌入，只等平息后再刷一次，避免和写入抢 SQLite 锁</summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>当前已加载数据的账号；切换账号时清空会话/消息缓存，避免多账号数据串扰</summary>
    private string? _loadedAccountUin;

    /// <summary>会话列表窗口大小：只渲染最近这一批，滚动/点"加载更多"再按需加载</summary>
    private const int ConversationPageSize = 60;
    private int _conversationLoadedCount;

    /// <summary>每个会话是否还有更早的本地历史可加载（聊天分页用）</summary>
    private readonly Dictionary<string, bool> _hasMoreHistory = new();

    public bool HasMoreConversations => _conversationLoadedCount < _allConversations.Count;

    /// <summary>当前选中会话是否还能加载更早的消息（顶部"加载更早的消息"按钮）</summary>
    public bool HasMoreMessages =>
        SelectedConversation is not null &&
        _hasMoreHistory.TryGetValue(SelectedConversation.Id, out var more) && more;

    private static readonly string[] Palette =
        { "#B47742", "#667A71", "#7C718C", "#718C79", "#8E7559", "#6D7F91", "#8C6E67", "#7D718B" };

    public MainViewModel()
    {
        StatusMessage = "离线模式: 核心服务未加载";
    }

    public MainViewModel(
        AccountManager accountManager,
        HistoryService historyService,
        ContactSyncService contactSyncService,
        ConfigManager configManager,
        AccountRepository accountRepo,
        ImageCacheService imageCache)
    {
        _accountManager = accountManager;
        _historyService = historyService;
        _contactSyncService = contactSyncService;
        _configManager = configManager;
        _accountRepo = accountRepo;
        _imageCache = imageCache;

        StatusMessage = "核心服务已加载";
        SubscribeToCoreEvents();
        // 维护"至少一个文本块"不变量：大文本框绑定的当前文本块始终存在
        _activeTextBlock = ComposeSegment.CreateText();
        ComposeSegments.Add(_activeTextBlock);
        // 片段增删/文字变化时刷新"能否发送"与待发条/待发内容可见性
        ComposeSegments.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (ComposeSegment s in e.OldItems) s.PropertyChanged -= OnComposeSegmentPropertyChanged;
            if (e.NewItems != null)
                foreach (ComposeSegment s in e.NewItems) s.PropertyChanged += OnComposeSegmentPropertyChanged;
            SendMessageCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasComposeContent));
            OnPropertyChanged(nameof(HasInlineBlocks));
        };
        _ = InitializeAsync();
    }

    private void OnComposeSegmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposeSegment.Text))
        {
            SendMessageCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasComposeContent));
        }
    }

    // ---- 集合 ----

    /// <summary>文件选择器（由 MainWindow 注入 TopLevel.StorageProvider），发送图片时用</summary>
    public IStorageProvider? StorageProvider { get; set; }

    public ObservableCollection<AccountItem> Accounts { get; } = new();
    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();
    public ObservableCollection<ContactItem> Contacts { get; } = new();
    public ObservableCollection<ConversationItem> Groups { get; } = new();
    public ObservableCollection<GroupMemberItem> GroupMembers { get; } = new();

    /// <summary>待发送片段（文字/@/图片按序组成一条消息，可上移下移交叉排列）</summary>
    public ObservableCollection<ComposeSegment> ComposeSegments { get; } = new();

    /// <summary>当前正在编辑的文本块（大文本框绑定它）。始终存在一个文本块作锚点，可插入多个。</summary>
    private ComposeSegment? _activeTextBlock;

    /// <summary>待发条显示条件：含 @/图片，或已插入多个片段（多文本块也要能看见和操作）</summary>
    public bool HasInlineBlocks =>
        ComposeSegments.Count > 1 || ComposeSegments.Any(s => s.Kind != ComposeSegmentKind.Text);

    public bool HasComposeContent => ComposeSegments.Any(s => !s.IsEmptyText);

    /// <summary>大文本框绑定：读写当前激活的文本块内容</summary>
    public string ComposerText
    {
        get => _activeTextBlock?.Text ?? "";
        set
        {
            if (_activeTextBlock is null)
                _activeTextBlock = CreateTextBlock();
            _activeTextBlock!.Text = value;
        }
    }

    private ComposeSegment CreateTextBlock()
    {
        var block = ComposeSegment.CreateText();
        ComposeSegments.Add(block);
        return block;
    }

    /// <summary>切换当前编辑的文本块并通知大文本框刷新</summary>
    private void SetActiveTextBlock(ComposeSegment block)
    {
        _activeTextBlock = block;
        OnPropertyChanged(nameof(ComposerText));
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>待发图片上限：base64 内嵌膨胀约 33%，超大会导致报文过大或上传失败</summary>
    private const long MaxPendingImageBytes = 15L * 1024 * 1024;

    // ---- 可观察属性 ----

    [ObservableProperty]
    private AccountItem? _currentAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedConversation))]
    [NotifyPropertyChangedFor(nameof(ShowChatEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsSelectedGroup))]
    [NotifyPropertyChangedFor(nameof(ConversationSubtitle))]
    private ConversationItem? _selectedConversation;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _currentPage = "chat";

    [ObservableProperty]
    private string _currentContactTab = "friends";

    [ObservableProperty]
    private bool _isGroupDetailsOpen;

    [ObservableProperty]
    private bool _isAccountSwitcherOpen;

    /// <summary>正在回复的目标消息（输入区上方显示引用条）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyTarget))]
    private MessageItem? _selectedReply;

    /// <summary>@ 成员选择面板是否打开</summary>
    [ObservableProperty]
    private bool _isAtPickerOpen;

    [ObservableProperty]
    private string _themeMode = "跟随系统";

    [ObservableProperty]
    private bool _isAddAccountFormOpen;

    [ObservableProperty]
    private string _newUin = string.Empty;

    [ObservableProperty]
    private string _newNickname = string.Empty;

    [ObservableProperty]
    private string _newWsUrl = "ws://localhost:3001";

    [ObservableProperty]
    private string _newAccessToken = string.Empty;

    [ObservableProperty]
    private bool _isRemovingAccount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _groupDetailsTitle = string.Empty;

    [ObservableProperty]
    private string _groupDetailsSubtitle = string.Empty;

    // ---- 派生属性 ----

    public bool HasSelectedConversation => SelectedConversation is not null;
    public bool ShowChatEmptyState => SelectedConversation is null;
    public bool IsSelectedGroup => SelectedConversation?.IsGroup == true;
    public bool HasReplyTarget => SelectedReply is not null;
    public bool IsChatPage => CurrentPage == "chat";
    public bool IsContactsPage => CurrentPage == "contacts";
    public bool IsSettingsPage => CurrentPage == "settings";
    public bool IsFriendsTab => CurrentContactTab == "friends";
    public bool IsGroupsTab => CurrentContactTab == "groups";
    public string FriendsTabBackground => CurrentContactTab == "friends" ? "#E0DBD4" : "Transparent";
    public string GroupsTabBackground => CurrentContactTab == "groups" ? "#E0DBD4" : "Transparent";
    public string NavChatBackground => CurrentPage == "chat" ? "#E0DBD4" : "Transparent";
    public string NavContactBackground => CurrentPage == "contacts" ? "#E0DBD4" : "Transparent";
    public string NavSettingsBackground => CurrentPage == "settings" ? "#E0DBD4" : "Transparent";
    public bool HasConversations => Conversations.Count > 0;
    public bool ShowSearchEmptyState => !HasConversations;
    public string ConversationSubtitle => SelectedConversation?.Subtitle ?? "选择一个会话开始聊天";
    public string AccountSummary => CurrentAccount is null
        ? "未选择账号"
        : $"{CurrentAccount.Nickname} · {CurrentAccount.StatusText}";

    // ---- 页面切换 ----

    [RelayCommand]
    private void SelectPage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        CurrentPage = page;
        IsGroupDetailsOpen = false;
        IsAccountSwitcherOpen = false;

        // 联系人页按需加载，避免每次刷新都全量构建联系人列表
        if (page == "contacts")
            _ = LoadContactsAsync();
    }

    [RelayCommand]
    private void ShowFriends() => CurrentContactTab = "friends";

    [RelayCommand]
    private void ShowGroups() => CurrentContactTab = "groups";

    partial void OnCurrentContactTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsFriendsTab));
        OnPropertyChanged(nameof(IsGroupsTab));
        OnPropertyChanged(nameof(FriendsTabBackground));
        OnPropertyChanged(nameof(GroupsTabBackground));
    }

    partial void OnSearchTextChanged(string value) => RefreshConversationFilter();

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsContactsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(NavChatBackground));
        OnPropertyChanged(nameof(NavContactBackground));
        OnPropertyChanged(nameof(NavSettingsBackground));
    }

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        OnPropertyChanged(nameof(ConversationSubtitle));
        OnPropertyChanged(nameof(HasMoreMessages));
        if (value is null) return;

        foreach (var c in _allConversations)
            c.IsSelected = ReferenceEquals(c, value);
        value.UnreadCount = 0;
        IsGroupDetailsOpen = false;
        IsAccountSwitcherOpen = false;
        IsAtPickerOpen = false;
        SelectedReply = null;
        ResetComposeSegments();
        CurrentPage = "chat";
        _ = LoadConversationMessagesAsync(value);
        _ = MarkConversationReadAsync(value);
    }

    /// <summary>打开会话即标记已读：把已读标记推进到该会话最新一条消息的时间，未读跨重启保留</summary>
    private async Task MarkConversationReadAsync(ConversationItem conv)
    {
        if (_historyService is null || CurrentAccount is null) return;
        try
        {
            await _historyService.MarkConversationReadAsync(
                conv.AccountId, conv.TargetId, conv.IsGroup ? MessageType.Group : MessageType.Private);
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("MarkConversationReadAsync", ex);
        }
    }

    partial void OnCurrentAccountChanged(AccountItem? value)
    {
        OnPropertyChanged(nameof(AccountSummary));
    }

    // ---- 会话选择 ----

    private async Task LoadConversationMessagesAsync(ConversationItem conv)
    {
        conv.UnreadCount = 0;
        SetGifsVisible(Messages, false); // 停掉旧会话正在播的 GIF
        Messages.Clear(); // 立即清空，避免切会话时残留上个会话的消息
        // 注意：实时消息进的是 _messageCache，不受这里 Clear 影响，加载完会 merge 回来

        if (_messageCache.TryGetValue(conv.Id, out var cached) && cached.Count > 0)
        {
            foreach (var m in cached) Messages.Add(m);
            SetGifsVisible(cached, true); // 缓存会话回来，恢复动画
            OnPropertyChanged(nameof(HasMoreMessages));
            return;
        }

        if (_historyService is null) return;
        try
        {
            var atMap = await EnsureMemberNameMapAsync(conv);

            var list = await _historyService.GetHistoryAsync(conv.AccountId, conv.TargetId, 100);
            if (list.Count == 0)
            {
                // 本地没有历史 → 从 NapCat 拉一份（会存库），新会话也能看到之前的消息
                var session = _accountManager?.GetSession(conv.AccountId);
                if (session is not null)
                {
                    try
                    {
                        await session.FetchHistoryAsync(conv.TargetId,
                            conv.IsGroup ? MessageType.Group : MessageType.Private, 50);
                    }
                    catch (Exception ex)
                    {
                        Program.WriteCrashLog("FetchHistory on open", ex);
                    }
                    list = await _historyService.GetHistoryAsync(conv.AccountId, conv.TargetId, 100);
                }
            }

            var items = list.Select(e => ToMessageItem(e, conv, atMap)).ToList();
            items.Reverse(); // 历史是倒序，转成旧的在上新的在下

            // 历史加载/拉取期间实时到达的消息（已进 cache 和 Messages）合并进末尾，避免被覆盖
            if (_messageCache.TryGetValue(conv.Id, out var realtime) && realtime.Count > 0)
                items = MergeHistoryWithRealtime(items, realtime);

            // 按顺序计算时间分隔条（首条 / 跨天 / 间隔≥5分钟）
            MessageItem? prevMsg = null;
            foreach (var m in items)
            {
                m.UpdateTimeDivider(prevMsg?.Timestamp);
                prevMsg = m;
            }

            _messageCache[conv.Id] = items;
            // 有消息就显示"加载更早"按钮（本地没有更早时点了会提示，见 LoadEarlierMessages）
            _hasMoreHistory[conv.Id] = items.Count > 0;
            OnPropertyChanged(nameof(HasMoreMessages));

            Messages.Clear();
            foreach (var m in items) Messages.Add(m);
            SetGifsVisible(items, true);

            // 异步补全引用内容（先命中内存缓存，再查 DB）
            foreach (var m in items)
                if (!string.IsNullOrEmpty(m.ReplyToMessageId))
                    _ = ResolveReplyAsync(m, conv);
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("LoadConversationMessagesAsync", ex);
        }
    }

    /// <summary>把历史批次与加载期间实时到达的消息按 MessageId 去重合并且保持时间序</summary>
    private static List<MessageItem> MergeHistoryWithRealtime(List<MessageItem> history, List<MessageItem> realtime)
    {
        var historyIds = new HashSet<string>(history.Select(m => m.MessageId).Where(id => id.Length > 0));
        var merged = new List<MessageItem>(history);
        foreach (var m in realtime)
        {
            if (m.MessageId.Length > 0 && historyIds.Contains(m.MessageId)) continue;
            merged.Add(m);
        }
        return merged;
    }

    /// <summary>
    /// 加载更早的历史消息（顶部按钮）。先翻本地库（免费瞬时），本地翻空后走 NapCat：
    /// 以最老一条有真实 id 的消息为游标往回翻一页（见 AccountSession.FetchOlderMessagesAsync）。
    /// </summary>
    [RelayCommand]
    private async Task LoadEarlierMessages()
    {
        var conv = SelectedConversation;
        if (conv is null || _historyService is null) return;
        if (!_messageCache.TryGetValue(conv.Id, out var cache) || cache.Count == 0) return;

        try
        {
            var oldest = cache[0];
            var list = await _historyService.GetHistoryAsync(conv.AccountId, conv.TargetId, 60, oldest.Timestamp.ToString("o"));

            if (list.Count > 0)
            {
                var atMap = TryGetMemberNameMap(conv);
                var items = list.Select(e => ToMessageItem(e, conv, atMap)).ToList();
                items.Reverse(); // 倒序转正

                // 这批内部的时间分隔
                MessageItem? prevMsg = null;
                foreach (var m in items)
                {
                    m.UpdateTimeDivider(prevMsg?.Timestamp);
                    prevMsg = m;
                }

                // 预插到 cache 头部；修正边界分隔（原最老一条与新批次的衔接）
                cache.InsertRange(0, items);
                if (cache.Count > items.Count)
                    cache[items.Count].UpdateTimeDivider(items[^1].Timestamp);

                // 预插到 Messages 头部（代码后置 OnMessagesChanged 检测到顶部插入就不滚底）
                for (int i = 0; i < items.Count; i++)
                    Messages.Insert(i, items[i]);

                SetGifsVisible(items, true);
                // 本地还有或 NapCat 还有更早，留按钮让下一次点击继续判定
                _hasMoreHistory[conv.Id] = true;
                OnPropertyChanged(nameof(HasMoreMessages));
                return;
            }

            // 本地翻空 → 走 NapCat 往前翻
            var session = _accountManager?.GetSession(conv.AccountId);
            if (session is null)
            {
                _hasMoreHistory[conv.Id] = false;
                OnPropertyChanged(nameof(HasMoreMessages));
                return;
            }

            // 游标取最老一条有真实 id 的消息（系统通知等无 id，跳过）
            var cursor = cache.FirstOrDefault(m => m.MessageId.Length > 0);
            if (cursor is null)
            {
                _hasMoreHistory[conv.Id] = false;
                OnPropertyChanged(nameof(HasMoreMessages));
                return;
            }

            var type = conv.IsGroup ? MessageType.Group : MessageType.Private;
            var older = await session.FetchOlderMessagesAsync(conv.TargetId, type, cursor.MessageId, 20);
            if (older.Count == 0)
            {
                _hasMoreHistory[conv.Id] = false;
                OnPropertyChanged(nameof(HasMoreMessages));
                StatusMessage = "已是最早的消息";
                return;
            }

            // reverse_order 会把游标消息也带回，去掉已显示过的
            var known = new HashSet<string>(cache.Select(m => m.MessageId).Where(id => id.Length > 0));
            var atMap2 = TryGetMemberNameMap(conv);
            var items2 = older
                .Where(m => string.IsNullOrEmpty(m.MessageId) || !known.Contains(m.MessageId))
                .Select(m => ToMessageItem(m, conv, atMap2))
                .ToList();
            if (items2.Count == 0)
            {
                _hasMoreHistory[conv.Id] = false;
                OnPropertyChanged(nameof(HasMoreMessages));
                return;
            }

            MessageItem? prev = null;
            foreach (var m in items2)
            {
                m.UpdateTimeDivider(prev?.Timestamp);
                prev = m;
            }

            cache.InsertRange(0, items2);
            if (cache.Count > items2.Count)
                cache[items2.Count].UpdateTimeDivider(items2[^1].Timestamp);

            for (int i = 0; i < items2.Count; i++)
                Messages.Insert(i, items2[i]);

            SetGifsVisible(items2, true);
            _hasMoreHistory[conv.Id] = older.Count >= 20; // 满页说明可能还有更早
            OnPropertyChanged(nameof(HasMoreMessages));
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("LoadEarlierMessages", ex);
        }
    }

    /// <summary>批量控制 GIF 播放状态（会话切换时）</summary>
    private static void SetGifsVisible(IEnumerable<MessageItem> items, bool visible)
    {
        foreach (var item in items)
            foreach (var img in item.Images)
                img.SetGifVisible(visible);
    }

    /// <summary>批量释放 GIF 资源（账号切换/数据清空时）</summary>
    private static void DisposeGifs(IEnumerable<MessageItem> items)
    {
        foreach (var item in items)
            foreach (var img in item.Images)
                img.DisposeGif();
    }

    // ---- 发送 ----

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessage()
    {
        var conv = SelectedConversation;
        if (conv is null) return;
        if (!HasComposeContent) return;

        var segments = new List<MessageSegment>();
        if (SelectedReply is not null && !string.IsNullOrEmpty(SelectedReply.MessageId))
            segments.Add(MessageSegment.CreateReply(SelectedReply.MessageId));
        foreach (var seg in ComposeSegments)
        {
            switch (seg.Kind)
            {
                case ComposeSegmentKind.Text when !seg.IsEmptyText:
                    segments.Add(MessageSegment.CreateText(seg.Text));
                    break;
                case ComposeSegmentKind.At:
                    segments.Add(MessageSegment.CreateAt(seg.UserId));
                    break;
                case ComposeSegmentKind.Image:
                    var src = seg.Image?.LocalPath ?? seg.Image?.Source;
                    if (!string.IsNullOrEmpty(src))
                        segments.Add(MessageSegment.CreateImage(src));
                    break;
            }
        }

        var sent = await SendSegmentsAsync(conv, segments);
        if (!sent)
        {
            // 失败不乐观上屏：保留片段，便于重试，不污染会话
            StatusMessage = "发送失败，请检查网络后重试";
            return;
        }

        // 只有发送成功才乐观上屏（NapCat 回声会带真实 message_id，走 IsSentBySelf 去重）
        var imagePaths = segments
            .Where(s => s.Type == MessageSegmentType.Image && s.ImageFile is not null)
            .Select(s => s.ImageFile!)
            .ToList();
        var item = BuildOptimisticItem(conv, segments, imagePaths.Count > 0 ? imagePaths : null, true);
        if (SelectedReply is not null)
        {
            item.ReplyToMessageId = SelectedReply.MessageId;
            item.ReplyToItemId = SelectedReply.Id;
            item.ReplySenderName = SelectedReply.SenderName;
            item.ReplyPreview = ReplyPreviewText(SelectedReply.Kind, SelectedReply.Text);
        }
        AppendToConversation(item, conv);

        SelectedReply = null;
        ResetComposeSegments();
    }

    private bool CanSendMessage() =>
        SelectedConversation is not null && HasComposeContent;

    /// <summary>清空待发片段并补一个空文本块（发送成功后为下一条输入待命）</summary>
    private void ResetComposeSegments()
    {
        foreach (var seg in ComposeSegments)
            seg.Image?.DisposeGif();
        ComposeSegments.Clear();
        var block = ComposeSegment.CreateText();
        ComposeSegments.Add(block);
        _activeTextBlock = block;
        OnPropertyChanged(nameof(ComposerText)); // 大文本框绑定它，必须通知清空
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>彻底清空待发片段并补一个空文本块（切会话/切账号/清空数据）</summary>
    private void ClearComposeSegments()
    {
        foreach (var seg in ComposeSegments)
            seg.Image?.DisposeGif();
        ComposeSegments.Clear();
        var block = ComposeSegment.CreateText();
        ComposeSegments.Add(block);
        _activeTextBlock = block;
        OnPropertyChanged(nameof(ComposerText));
    }

    /// <summary>请求聚焦大文本框（code-behind 聚焦 Composer）</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>上移片段（边界不越界）</summary>
    [RelayCommand]
    private void MoveSegmentUp(ComposeSegment? seg) => MoveSegment(seg, -1);

    /// <summary>下移片段（边界不越界）</summary>
    [RelayCommand]
    private void MoveSegmentDown(ComposeSegment? seg) => MoveSegment(seg, +1);

    private void MoveSegment(ComposeSegment? seg, int delta)
    {
        if (seg is null) return;
        var idx = ComposeSegments.IndexOf(seg);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= ComposeSegments.Count) return;
        ComposeSegments.Move(idx, target);
    }

    /// <summary>
    /// 在指定块之前插入文本块（双击该块左侧缝隙）。任一侧相邻是文本块则不建，
    /// 避免两个文本块挨在一起（相邻文本块合并为一个更合理）。
    /// </summary>
    [RelayCommand]
    private void InsertTextBefore(ComposeSegment? anchor)
    {
        if (anchor is null) return;
        var idx = ComposeSegments.IndexOf(anchor);
        if (idx < 0) return;
        if (anchor.Kind == ComposeSegmentKind.Text) return;                    // 右侧（anchor）是文本块
        if (idx > 0 && ComposeSegments[idx - 1].Kind == ComposeSegmentKind.Text) return; // 左侧是文本块
        InsertTextBlock(idx);
    }

    /// <summary>
    /// 在指定块之后插入文本块（双击该块右侧缝隙 / 列表末尾尾缝，anchor=null 表示末尾）。
    /// 任一侧相邻是文本块则不建。
    /// </summary>
    [RelayCommand]
    private void InsertTextAfter(ComposeSegment? anchor)
    {
        int idx;
        if (anchor is null)
        {
            idx = ComposeSegments.Count; // 末尾
            if (idx > 0 && ComposeSegments[idx - 1].Kind == ComposeSegmentKind.Text) return; // 左侧是文本块
        }
        else
        {
            idx = ComposeSegments.IndexOf(anchor) + 1;
            if (idx < 0) return;
            if (anchor.Kind == ComposeSegmentKind.Text) return;                // 左侧（anchor）是文本块
            if (idx < ComposeSegments.Count && ComposeSegments[idx].Kind == ComposeSegmentKind.Text) return; // 右侧是文本块
        }
        InsertTextBlock(Math.Min(idx, ComposeSegments.Count));
    }

    private void InsertTextBlock(int index)
    {
        var block = ComposeSegment.CreateText();
        ComposeSegments.Insert(index, block);
        SetActiveTextBlock(block);
    }

    /// <summary>点击待发条上的文本块：切为大文本框当前编辑的文本块</summary>
    [RelayCommand]
    private void ActivateTextBlock(ComposeSegment? seg)
    {
        if (seg is { Kind: ComposeSegmentKind.Text } && !ReferenceEquals(_activeTextBlock, seg))
            SetActiveTextBlock(seg);
    }

    /// <summary>删除一个片段；仅剩一个文本块时只清空不删除（大文本框需要它），@ 段同步取消 AtPicker 勾选态</summary>
    [RelayCommand]
    private void RemoveSegment(ComposeSegment? seg)
    {
        if (seg is null) return;
        if (seg.Kind == ComposeSegmentKind.Text)
        {
            // 有多个文本块才真删；否则只清空内容
            if (ComposeSegments.Count(s => s.Kind == ComposeSegmentKind.Text) > 1)
            {
                ComposeSegments.Remove(seg);
                if (ReferenceEquals(_activeTextBlock, seg))
                {
                    _activeTextBlock = ComposeSegments.FirstOrDefault(s => s.Kind == ComposeSegmentKind.Text);
                    OnPropertyChanged(nameof(ComposerText));
                }
            }
            else
            {
                ComposerText = "";
                OnPropertyChanged(nameof(ComposerText)); // 大文本框绑定了它，需通知清空
                ComposerFocusRequested?.Invoke();
            }
            return;
        }
        if (seg.Kind == ComposeSegmentKind.At)
            foreach (var m in GroupMembers)
                if (m.UserId == seg.UserId) m.IsSelected = false;
        if (seg.Kind == ComposeSegmentKind.Image)
            seg.Image?.DisposeGif();
        ComposeSegments.Remove(seg);
    }

    // ---- 引用 / @ / 戳一戳 ----

    [RelayCommand]
    private void SetReply(MessageItem? item)
    {
        if (item is null || item.IsSystem) return;
        SelectedReply = item;
    }

    [RelayCommand]
    private void ClearReply() => SelectedReply = null;

    [RelayCommand]
    private void CloseAtPicker() => IsAtPickerOpen = false;

    /// <summary>打开 @ 成员选择面板（仅群聊）</summary>
    [RelayCommand]
    private async Task OpenAtPicker()
    {
        if (SelectedConversation is not { IsGroup: true } group) return;
        IsGroupDetailsOpen = false;
        IsAtPickerOpen = true;
        await LoadGroupMembersAsync(group);
        // 把已加入的 @ 片段同步成面板勾选态
        foreach (var m in GroupMembers)
            m.IsSelected = ComposeSegments.Any(s => s.Kind == ComposeSegmentKind.At && s.UserId == m.UserId);
    }

    /// <summary>勾选/取消勾选 @ 成员：在片段列表末尾加/删对应 @ 段</summary>
    [RelayCommand]
    private void ToggleAtMember(GroupMemberItem? member)
    {
        if (member is null) return;
        var existing = ComposeSegments.FirstOrDefault(s => s.Kind == ComposeSegmentKind.At && s.UserId == member.UserId);
        if (existing is not null)
        {
            ComposeSegments.Remove(existing);
            member.IsSelected = false;
        }
        else
        {
            ComposeSegments.Add(ComposeSegment.CreateAt(member.UserId, member.Name));
            member.IsSelected = true;
        }
    }

    /// <summary>快捷 @ 消息发送者（悬停操作按钮），已 @ 过则忽略</summary>
    [RelayCommand]
    private void AddAtMember(MessageItem? item)
    {
        if (item is null || item.IsMine || !item.IsGroup) return;
        if (ComposeSegments.Any(s => s.Kind == ComposeSegmentKind.At && s.UserId == item.SenderId)) return;

        var name = _memberNameMaps.TryGetValue(SelectedConversation?.TargetId ?? "", out var map) &&
                   map.TryGetValue(item.SenderId, out var n)
            ? n
            : item.SenderName;
        ComposeSegments.Add(ComposeSegment.CreateAt(item.SenderId, name));
        StatusMessage = $"已 @ {name}，输入内容后发送";
    }

    /// <summary>戳一戳消息发送者（允许戳自己；群聊戳该成员，私聊戳对方）</summary>
    [RelayCommand]
    private async Task PokeSender(MessageItem? item)
    {
        if (item is null || item.IsSystem) return;
        var conv = SelectedConversation;
        if (conv is null) return;
        var session = _accountManager?.GetSession(conv.AccountId);
        if (session is null) return;

        var ok = await session.SendPokeAsync(item.SenderId, conv.IsGroup ? conv.TargetId : null);
        StatusMessage = ok ? $"戳了 {item.SenderName} 一下" : "戳一戳发送失败";
    }

    /// <summary>选择本地图片加入待发片段（点发送时按列表顺序组装，支持多图）</summary>
    [RelayCommand]
    private async Task SendImages()
    {
        var conv = SelectedConversation;
        if (conv is null || StorageProvider is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发送的图片",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片文件")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" },
                    MimeTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/bmp", "image/webp" }
                }
            }
        });
        if (files.Count == 0) return;

        var added = 0;
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            if (AddImageSegment(p)) added++;
        }

        StatusMessage = added > 0
            ? $"已添加 {added} 张图片，可调整顺序后发送"
            : "所选图片无法添加（超过 15MB 或文件不可读）";
    }

    /// <summary>把本地图片加入待发片段末尾；超限/无效返回 false</summary>
    private bool AddImageSegment(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (info.Length > MaxPendingImageBytes)
            {
                StatusMessage = "图片超过 15MB，无法发送";
                return false;
            }
        }
        catch
        {
            return false;
        }

        // ComposeSegments.CollectionChanged 会自动刷新发送按钮与待发内容可见性
        ComposeSegments.Add(ComposeSegment.CreateImage(path, _imageCache));
        return true;
    }

    /// <summary>粘贴图片入队（MainWindow 捕获 Ctrl+V 剪贴板图片后调用，bitmap 已编码为 PNG 落盘）</summary>
    public Task SendClipboardImageAsync(Bitmap bitmap)
    {
        if (SelectedConversation is null || _imageCache is null || bitmap is null)
            return Task.CompletedTask;

        try
        {
            var path = _imageCache.CreatePasteImagePath();
            bitmap.Save(path, PngBitmapEncoderOptions.Default); // 输出 PNG
            if (AddImageSegment(path))
                StatusMessage = "已添加图片，可调整顺序后发送";
            else
                StatusMessage = "粘贴图片失败";
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("SendClipboardImageAsync", ex);
            StatusMessage = "粘贴图片失败";
        }
        return Task.CompletedTask;
    }

    /// <summary>发送消息段到指定会话，返回是否成功（失败时置 StatusMessage）</summary>
    private async Task<bool> SendSegmentsAsync(ConversationItem conv, List<MessageSegment> segments)
    {
        var attempted = false;
        var session = _accountManager?.GetSession(conv.AccountId);
        if (session is not null)
        {
            attempted = true;
            try
            {
                return conv.IsGroup
                    ? await session.SendGroupMessageAsync(conv.TargetId, segments) is not null
                    : await session.SendPrivateMessageAsync(conv.TargetId, segments) is not null;
            }
            catch (Exception ex)
            {
                Program.WriteCrashLog("SendSegmentsAsync", ex);
            }
        }

        if (attempted)
            StatusMessage = "发送失败：未连接或请求超时";
        return false;
    }

    /// <summary>构建乐观上屏消息项；segments 决定显示文本/类型，imagePaths 非空时逐张附加图片</summary>
    private MessageItem BuildOptimisticItem(ConversationItem conv, List<MessageSegment> segments, List<string>? imagePaths, bool sent)
    {
        var (kind, reply, fileName, _, _, displayText, _) =
            BuildSegments(segments, "", false, TryGetMemberNameMap(conv));
        var item = new MessageItem
        {
            ConversationId = conv.Id,
            SenderId = CurrentAccount?.Uin ?? "",
            SenderName = CurrentAccount?.Nickname ?? "我",
            SenderInitials = CurrentAccount?.Initials ?? "我",
            AvatarColor = CurrentAccount?.AvatarColor ?? "#C4873C",
            Text = displayText.Length > 0 ? displayText : "[图片]",
            Time = DateTime.Now.ToString("HH:mm"),
            Timestamp = DateTimeOffset.Now,
            IsMine = true,
            Kind = kind == MessageKind.System ? MessageKind.Text : kind,
            IsGroup = conv.IsGroup,
            StatusText = sent ? "✓✓" : "✓"
        };

        if (imagePaths is not null)
        {
            foreach (var p in imagePaths)
            {
                var img = new MessageImage(p, null, _imageCache);
                item.AddImage(img);
                _ = img.ResolveAsync();
            }
        }
        return item;
    }

    // ---- 群详情 / 账号切换 ----

    [RelayCommand]
    private async Task ToggleGroupDetails()
    {
        if (!IsSelectedGroup) return;
        IsGroupDetailsOpen = !IsGroupDetailsOpen;
        IsAtPickerOpen = false;
        if (IsGroupDetailsOpen && SelectedConversation is not null)
            await LoadGroupMembersAsync(SelectedConversation);
    }

    private async Task LoadGroupMembersAsync(ConversationItem group)
    {
        GroupMembers.Clear();
        if (_contactSyncService is null) return;
        try
        {
            var members = await EnsureMembersAsync(group);

            // 群名片可能更新过，顺手刷新 @ 解析用的名字映射
            _memberNameMaps[group.TargetId] = members.ToDictionary(m => m.UserId, MemberDisplayName);

            foreach (var m in members)
            {
                var member = new GroupMemberItem
                {
                    UserId = m.UserId,
                    Name = MemberDisplayName(m),
                    Initials = Initials(MemberDisplayName(m)),
                    AvatarColor = ColorForId(m.UserId),
                    Role = RoleName(m.Role),
                    SpecialTitle = m.SpecialTitle ?? ""
                };
                GroupMembers.Add(member);
                ResolveUserAvatar(m.UserId, b => member.AvatarBitmap = b);
            }
            GroupDetailsTitle = group.Name;
            GroupDetailsSubtitle = $"{group.TargetId} · {members.Count} 位成员";
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("LoadGroupMembersAsync", ex);
        }
    }

    [RelayCommand]
    private void ToggleAccountSwitcher()
    {
        IsAccountSwitcherOpen = !IsAccountSwitcherOpen;
        IsGroupDetailsOpen = false;
    }

    [RelayCommand]
    private void SwitchAccount(AccountItem? account)
    {
        if (account is null || ReferenceEquals(account, CurrentAccount))
        {
            IsAccountSwitcherOpen = false;
            return;
        }

        foreach (var a in Accounts)
            a.IsCurrent = ReferenceEquals(a, account);

        CurrentAccount = account;
        SelectedConversation = null;
        DisposeGifs(_messageCache.Values.SelectMany(v => v)); // 账号切换，释放旧账号 GIF
        ClearComposeSegments();
        Messages.Clear();
        IsAccountSwitcherOpen = false;
        IsGroupDetailsOpen = false;
        _ = LoadAccountDataAsync(account);
    }

    // ---- 联系人页打开会话 ----

    [RelayCommand]
    private void OpenChat(ContactItem? contact)
    {
        if (contact is null || CurrentAccount is null) return;
        var convId = "p:" + contact.UserId;
        if (!_conversationMap.TryGetValue(convId, out var conv))
        {
            conv = new ConversationItem(convId, CurrentAccount.Uin, contact.Name, false,
                contact.Initials, contact.AvatarColor, contact.UserId);
            _conversationMap[convId] = conv;
            _allConversations.Insert(0, conv);
        }
        SearchText = string.Empty;
        RefreshConversationFilter();
        SelectedConversation = conv;
    }

    [RelayCommand]
    private void OpenGroupChat(ConversationItem? group)
    {
        if (group is null || CurrentAccount is null) return;
        var convId = "g:" + group.TargetId;
        if (!_conversationMap.TryGetValue(convId, out var conv))
        {
            conv = new ConversationItem(convId, CurrentAccount.Uin, group.Name, true,
                group.Initials, group.AvatarColor, group.TargetId) { Subtitle = group.Subtitle };
            _conversationMap[convId] = conv;
            _allConversations.Insert(0, conv);
        }
        SearchText = string.Empty;
        RefreshConversationFilter();
        SelectedConversation = conv;
    }

    // ---- 主题 ----

    [RelayCommand]
    private void SetTheme(string? mode)
    {
        ThemeMode = mode switch
        {
            "Light" => "浅色",
            "Dark" => "深色",
            _ => "跟随系统"
        };

        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = mode switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>加载下一批会话（点"加载更多"或滚动到底时调用）</summary>
    [RelayCommand]
    private void LoadMoreConversations()
    {
        if (SearchText.Length > 0) return; // 搜索时已全量匹配
        if (_conversationLoadedCount >= _allConversations.Count) return;
        _conversationLoadedCount = Math.Min(_allConversations.Count, _conversationLoadedCount + ConversationPageSize);
        RefreshConversationFilter();
    }

    // ---- 账号管理 ----

    [RelayCommand]
    private void ToggleAddAccountForm()
    {
        IsAddAccountFormOpen = !IsAddAccountFormOpen;
        if (IsAddAccountFormOpen)
        {
            NewUin = string.Empty;
            NewNickname = string.Empty;
            NewWsUrl = "ws://localhost:3001";
            NewAccessToken = string.Empty;
        }
    }

    [RelayCommand]
    private async Task AddAccount()
    {
        if (_accountManager is null || _accountRepo is null)
        {
            StatusMessage = "错误: 核心服务未初始化，请重启应用";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewWsUrl))
        {
            StatusMessage = "请输入 WebSocket 地址";
            return;
        }

        // QQ 号可选：留空则用占位符，连接成功后经 get_login_info 自动解析
        var uin = NewUin.Trim();
        if (string.IsNullOrEmpty(uin))
            uin = $"pending-{Guid.NewGuid():N}";
        var nickname = NewNickname.Trim();
        var token = string.IsNullOrWhiteSpace(NewAccessToken) ? null : NewAccessToken.Trim();
        var wsUrl = NewWsUrl.Trim();

        try
        {
            await _accountManager.AddAccountAsync(uin, wsUrl, token, nickname);

            var config = _configManager?.Load();
            if (config is not null)
            {
                config.Accounts.RemoveAll(a => a.Uin == uin);
                config.Accounts.Add(new AccountConfig
                {
                    Uin = uin,
                    Nickname = nickname,
                    NapCatWsUrl = wsUrl,
                    AccessToken = token,
                    IsEnabled = true
                });
                _configManager?.Save();
            }

            IsAddAccountFormOpen = false;
            await RefreshAccountListAsync();

            var dbAcc = await _accountRepo.GetAsync(uin);
            if (dbAcc is not null)
                await _accountManager.StartAccountAsync(dbAcc);

            StatusMessage = string.IsNullOrEmpty(NewUin.Trim())
                ? $"已添加 {wsUrl}，连接成功后自动识别 QQ 号"
                : $"账号 {uin} 添加成功";
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("AddAccount", ex);
            StatusMessage = $"添加失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveAccount(AccountItem? account)
    {
        if (account is null || _accountManager is null || _accountRepo is null)
            return;

        IsRemovingAccount = true;
        try
        {
            await _accountManager.StopAccountAsync(account.Uin);
            await _accountRepo.DeleteAsync(account.Uin);

            var config = _configManager?.Load();
            config?.Accounts.RemoveAll(a => a.Uin == account.Uin);
            _configManager?.Save();

            await RefreshAccountListAsync();
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("RemoveAccount", ex);
            StatusMessage = $"移除失败: {ex.Message}";
        }
        finally
        {
            IsRemovingAccount = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAccount(AccountItem? account)
    {
        if (account is null || _accountManager is null || _accountRepo is null) return;
        var dbAcc = await _accountRepo.GetAsync(account.Uin);
        if (dbAcc is not null)
            await _accountManager.StartAccountAsync(dbAcc);
    }

    [RelayCommand]
    private async Task DisconnectAccount(AccountItem? account)
    {
        if (account is null || _accountManager is null) return;
        await _accountManager.StopAccountAsync(account.Uin);
        account.Status = AccountStatus.Offline;
        OnPropertyChanged(nameof(AccountSummary));
    }

    // ---- 数据加载 ----

    private async Task InitializeAsync()
    {
        if (_accountRepo is null) return;
        try
        {
            await RefreshAccountListAsync();
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("InitializeAsync", ex);
            StatusMessage = $"初始化失败: {ex.Message}";
        }
    }

    private async Task RefreshAccountListAsync()
    {
        if (_accountRepo is null) return;
        var dbAccounts = await _accountRepo.GetAllAsync();
        var currentUin = CurrentAccount?.Uin;

        Accounts.Clear();
        var colorIdx = 0;
        foreach (var dbAcc in dbAccounts)
        {
            var status = AccountStatus.Offline;
            if (_accountManager!.Sessions.TryGetValue(dbAcc.Uin, out var session))
                status = ToAccountStatus(session.State);

            var name = string.IsNullOrEmpty(dbAcc.Nickname) ? dbAcc.Uin : dbAcc.Nickname;
            var item = new AccountItem(dbAcc.Uin, dbAcc.Nickname, Initials(name),
                Palette[colorIdx++ % Palette.Length], status);
            Accounts.Add(item);

            // 账号头像（占位 UIN 跳过）
            if (dbAcc.Uin.Length > 0 && dbAcc.Uin.All(char.IsDigit))
                ResolveUserAvatar(dbAcc.Uin, b => item.AvatarBitmap = b);

            if (dbAcc.Uin == currentUin || (currentUin is null && Accounts.Count == 1))
                CurrentAccount = item;
        }

        if (CurrentAccount is null && Accounts.Count > 0)
        {
            CurrentAccount = Accounts[0];
            CurrentAccount.IsCurrent = true;
        }

        OnPropertyChanged(nameof(AccountSummary));

        if (CurrentAccount is not null)
            await LoadAccountDataAsync(CurrentAccount);
        else
            ClearAccountData();
    }

    private void ClearAccountData()
    {
        DisposeGifs(_messageCache.Values.SelectMany(v => v)); // 释放 GIF 帧与定时器
        DisposeAvatarCache();
        _memberNameMaps.Clear();
        _hasMoreHistory.Clear();
        _allConversations = new();
        _conversationMap.Clear();
        _messageCache.Clear();
        _conversationLoadedCount = 0;
        Conversations.Clear();
        Contacts.Clear();
        Groups.Clear();
        GroupMembers.Clear();
        Messages.Clear();
        ClearComposeSegments();
        SelectedConversation = null;
        OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(ShowSearchEmptyState));
        OnPropertyChanged(nameof(HasMoreConversations));
    }

    private async Task LoadAccountDataAsync(AccountItem account)
    {
        if (!ReferenceEquals(account, CurrentAccount)) return;

        // 账号切换（或首次加载）时清空旧账号的会话与消息缓存
        if (account.Uin != _loadedAccountUin)
        {
            _loadedAccountUin = account.Uin;
            _allConversations = new();
            _conversationMap.Clear();
            _messageCache.Clear();
            DisposeAvatarCache();
            _memberNameMaps.Clear();
            _hasMoreHistory.Clear();
            _conversationLoadedCount = 0;
        }

        try
        {
            await LoadConversationsAsync();
            // 联系人页按需加载（SelectPage / ScheduleDataRefresh 中处理），不在每次刷新时全量构建
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("LoadAccountDataAsync", ex);
        }
    }

    private async Task LoadConversationsAsync()
    {
        if (CurrentAccount is null) return;
        var accountId = CurrentAccount.Uin;

        var friends = _contactSyncService is null
            ? new List<ContactEntity>()
            : await _contactSyncService.GetFriendsAsync(accountId);
        var groups = _contactSyncService is null
            ? new List<GroupEntity>()
            : await _contactSyncService.GetGroupsAsync(accountId);
        var summaries = _historyService is null
            ? new List<ConversationSummary>()
            : await _historyService.GetConversationSummariesAsync(accountId);
        var unreadCounts = _historyService is null
            ? new Dictionary<(string, int), int>()
            : await _historyService.GetUnreadCountsAsync(accountId);

        var summaryByKey = summaries.ToDictionary(s => (s.MessageType, s.TargetId));
        var list = new List<ConversationItem>();

        // 复用已有会话对象：刷新时保持 ListBox 选中态，不重建实例
        foreach (var f in friends)
        {
            var id = "p:" + f.UserId;
            var conv = _conversationMap.TryGetValue(id, out var existing)
                ? existing
                : new ConversationItem(id, accountId, ContactDisplayName(f), false,
                    Initials(ContactDisplayName(f)), ColorForId(f.UserId), f.UserId);
            _conversationMap[id] = conv; // 实时消息按 id 查这个 map 判断会话是否在，必须登记
            conv.Name = ContactDisplayName(f);
            conv.Subtitle = string.Empty;
            conv.Preview = string.Empty;
            conv.SortTime = null;
            conv.Time = string.Empty;
            if (summaryByKey.TryGetValue((0, f.UserId), out var s))
            {
                conv.Preview = s.Content;
                conv.SortTime = ParseTimestamp(s.Timestamp);
                conv.Time = FormatTime(s.Timestamp);
            }
            conv.UnreadCount = unreadCounts.TryGetValue((f.UserId, 0), out var fu) ? fu : 0;
            ResolveUserAvatar(f.UserId, b => conv.AvatarBitmap = b);
            list.Add(conv);
        }

        foreach (var g in groups)
        {
            var id = "g:" + g.GroupId;
            var conv = _conversationMap.TryGetValue(id, out var existing)
                ? existing
                : new ConversationItem(id, accountId, g.Name, true,
                    Initials(g.Name), ColorForId(g.GroupId), g.GroupId);
            _conversationMap[id] = conv; // 实时消息按 id 查这个 map 判断会话是否在，必须登记
            conv.Name = g.Name;
            conv.Subtitle = $"{g.MemberCount} 位成员";
            conv.Preview = string.Empty;
            conv.SortTime = null;
            conv.Time = string.Empty;
            if (summaryByKey.TryGetValue((1, g.GroupId), out var s))
            {
                conv.Preview = s.Content;
                conv.SortTime = ParseTimestamp(s.Timestamp);
                conv.Time = FormatTime(s.Timestamp);
            }
            conv.UnreadCount = unreadCounts.TryGetValue((g.GroupId, 1), out var gu) ? gu : 0;
            ResolveGroupAvatar(g.GroupId, b => conv.AvatarBitmap = b);
            list.Add(conv);
        }

        // 清掉已不存在的会话
        var liveIds = new HashSet<string>(list.Select(c => c.Id));
        foreach (var stale in _conversationMap.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _conversationMap.Remove(stale);

        _allConversations = list;

        // 窗口式加载：默认只展示最近的一批，避免全量渲染卡顿
        _conversationLoadedCount = Math.Min(ConversationPageSize, list.Count);

        // 当前选中的会话必须留在窗口里，否则切会话后列表找不到它
        if (SelectedConversation is not null)
        {
            var selIdx = list.FindIndex(c => c.Id == SelectedConversation.Id);
            if (selIdx >= 0 && selIdx >= _conversationLoadedCount)
                _conversationLoadedCount = Math.Min(list.Count, selIdx + 1);
        }

        OnPropertyChanged(nameof(HasMoreConversations));
        RefreshConversationFilter();
    }

    private async Task LoadContactsAsync()
    {
        if (CurrentAccount is null)
        {
            Contacts.Clear();
            Groups.Clear();
            return;
        }
        var accountId = CurrentAccount.Uin;

        var friends = _contactSyncService is null
            ? new List<ContactEntity>()
            : await _contactSyncService.GetFriendsAsync(accountId);
        var groups = _contactSyncService is null
            ? new List<GroupEntity>()
            : await _contactSyncService.GetGroupsAsync(accountId);

        Contacts.Clear();
        foreach (var f in friends)
        {
            var contact = new ContactItem
            {
                UserId = f.UserId,
                Name = ContactDisplayName(f),
                Initials = Initials(ContactDisplayName(f)),
                AvatarColor = ColorForId(f.UserId)
            };
            Contacts.Add(contact);
            ResolveUserAvatar(f.UserId, b => contact.AvatarBitmap = b);
        }

        Groups.Clear();
        foreach (var g in groups)
        {
            var groupItem = new ConversationItem("g:" + g.GroupId, accountId, g.Name, true,
                Initials(g.Name), ColorForId(g.GroupId), g.GroupId)
            {
                Subtitle = $"{g.MemberCount} 位成员"
            };
            Groups.Add(groupItem);
            ResolveGroupAvatar(g.GroupId, b => groupItem.AvatarBitmap = b);
        }
    }

    private void RefreshConversationFilter()
    {
        var query = SearchText.Trim();
        var selectedId = SelectedConversation?.Id;

        IEnumerable<ConversationItem> source = _allConversations
            .Where(c => query.Length == 0 ||
                        c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Preview.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.SortTime ?? DateTimeOffset.MinValue);

        // 无搜索时只展示已加载窗口；搜索时全量匹配（搜索要能命中任何会话）
        if (query.Length == 0)
            source = source.Take(_conversationLoadedCount);

        var desired = source.ToList();

        foreach (var c in desired)
            c.IsSelected = c.Id == selectedId;

        // 增量对齐集合（增删/挪动，绝不 Clear+Add），虚拟化列表才不崩、不卡
        SyncCollection(Conversations, desired);

        OnPropertyChanged(nameof(HasConversations));
        OnPropertyChanged(nameof(ShowSearchEmptyState));
        OnPropertyChanged(nameof(HasMoreConversations));
    }

    /// <summary>
    /// 把 target 集合对齐到 desired：移除多余的、补上缺失的、按顺序挪动。
    /// 顺序没变化时零操作，不会触发虚拟化列表重建。
    /// </summary>
    private static void SyncCollection(ObservableCollection<ConversationItem> target, List<ConversationItem> desired)
    {
        var desiredSet = new HashSet<ConversationItem>(desired);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (int idx = 0; idx < desired.Count; idx++)
        {
            var want = desired[idx];
            if (idx < target.Count && ReferenceEquals(target[idx], want)) continue;
            var cur = target.IndexOf(want);
            if (cur < 0)
                target.Insert(Math.Min(idx, target.Count), want);
            else
                target.Move(cur, Math.Min(idx, target.Count - 1));
        }
    }

    /// <summary>合并多次数据刷新请求为一次：同步时每条联系人都会触发事件，防抖 400ms 后统一刷新</summary>
    private void ScheduleDataRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            }
            catch (OperationCanceledException)
            {
                return; // 又被新的刷新请求顶掉了
            }

            if (ct.IsCancellationRequested || CurrentAccount is null) return;
            try
            {
                await LoadConversationsAsync();
                // 联系人页只在可见时刷新，避免每次同步都全量构建
                if (CurrentPage == "contacts")
                    await LoadContactsAsync();
            }
            catch (Exception ex)
            {
                Program.WriteCrashLog("ScheduleDataRefresh", ex);
            }
        });
    }

    // ---- Core 事件 ----

    private void SubscribeToCoreEvents()
    {
        if (_accountManager is null) return;

        _accountManager.OnMessage += OnCoreMessage;

        _accountManager.OnAccountStateChanged += (uin, state) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var account = Accounts.FirstOrDefault(a => a.Uin == uin);
                    if (account is not null)
                    {
                        account.Status = ToAccountStatus(state);
                        OnPropertyChanged(nameof(AccountSummary));
                    }
                    // 连接成功且数据同步完成后，刷新会话/联系人
                    if (state == ConnectionState.Connected)
                        ScheduleDataRefresh();
                }
                catch (Exception ex)
                {
                    Program.WriteCrashLog("OnAccountStateChanged", ex);
                }
            });
            return Task.CompletedTask;
        };

        _accountManager.OnAccountUinResolved += (oldUin, newUin) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var config = _configManager?.Load();
                    var acc = config?.Accounts.FirstOrDefault(a => a.Uin == oldUin);
                    if (acc is not null)
                    {
                        acc.Uin = newUin;
                        _configManager?.Save();
                    }
                    _ = RefreshAccountListAsync();
                    StatusMessage = $"账号 {newUin} 已连接";
                }
                catch (Exception ex)
                {
                    Program.WriteCrashLog("OnAccountUinResolved", ex);
                }
            });
            return Task.CompletedTask;
        };

        _accountManager.OnContactsChanged += accountId =>
        {
            if (CurrentAccount?.Uin == accountId)
                ScheduleDataRefresh();
            return Task.CompletedTask;
        };

        _accountManager.OnGroupsChanged += accountId =>
        {
            if (CurrentAccount?.Uin == accountId)
                ScheduleDataRefresh();
            return Task.CompletedTask;
        };

        _accountManager.OnHistoryCaughtUp += accountId =>
        {
            if (CurrentAccount?.Uin == accountId)
                ScheduleDataRefresh();
            return Task.CompletedTask;
        };
    }

    private Task OnCoreMessage(Message msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (CurrentAccount?.Uin != msg.AccountId) return;
                var convId = ConversationId(msg.Type, msg.TargetId);
                if (!_conversationMap.TryGetValue(convId, out var conv)) return;

                var atMap = TryGetMemberNameMap(conv);
                var item = ToMessageItem(msg, conv, atMap);

                // 群聊成员表还没加载时后台补一次，后续 @ 消息能解析出名字
                if (conv.IsGroup && atMap is null)
                    _ = EnsureMemberNameMapAsync(conv);

                // 非当前会话收到的图片 GIF 先不播，避免后台空转
                if (!ReferenceEquals(SelectedConversation, conv))
                    SetGifsVisible(new[] { item }, false);

                // 自回声去重：刚乐观上屏的同会话同文本消息
                if (msg.IsSentBySelf &&
                    Messages.Any(m => m.IsMine && m.Text == item.Text &&
                                      DateTime.Now - m.AddedAt < TimeSpan.FromSeconds(10)))
                    return;

                AppendToConversation(item, conv);
                if (!string.IsNullOrEmpty(item.ReplyToMessageId))
                    _ = ResolveReplyAsync(item, conv);
            }
            catch (Exception ex)
            {
                Program.WriteCrashLog("OnMessage handler", ex);
            }
        });
        return Task.CompletedTask;
    }

    private void AppendToConversation(MessageItem item, ConversationItem conv)
    {
        if (!_messageCache.TryGetValue(conv.Id, out var cache))
        {
            cache = new List<MessageItem>();
            _messageCache[conv.Id] = cache;
        }

        // 相对上一条消息决定是否插时间分隔条
        var prev = cache.Count > 0 ? cache[^1] : null;
        item.UpdateTimeDivider(prev?.Timestamp);

        cache.Add(item);

        if (ReferenceEquals(SelectedConversation, conv))
            Messages.Add(item);
        else
            conv.UnreadCount++;

        conv.Preview = item.Text.Length > 0
            ? (conv.IsGroup && !item.IsMine ? $"{item.SenderName}: {item.Text}" : item.Text)
            : item.Text;
        conv.SortTime = DateTimeOffset.UtcNow;
        conv.Time = DateTime.Now.ToString("HH:mm");

        // 新消息让会话跳到顶部（置顶之后）。已经在顶部则不动，
        // 避免每次消息都 O(n) 挪动整表导致卡顿
        if (!conv.IsPinned && SearchText.Length == 0)
        {
            var idx = Conversations.IndexOf(conv);
            var pinnedCount = 0;
            while (pinnedCount < Conversations.Count && Conversations[pinnedCount].IsPinned)
                pinnedCount++;
            if (idx > pinnedCount)
                Conversations.Move(idx, Math.Min(pinnedCount, Conversations.Count - 1));
        }
    }

    // ---- 映射 ----

    private MessageItem ToMessageItem(Message m, ConversationItem conv, IReadOnlyDictionary<string, string>? atNameMap = null)
    {
        var (kind, reply, fileName, fileSize, imageCaption, text, images) =
            BuildSegments(m.Segments, m.Content, m.IsSystemEvent, atNameMap);
        var isMine = m.IsSentBySelf;
        var senderName = isMine
            ? (CurrentAccount?.Nickname ?? "我")
            : (string.IsNullOrEmpty(m.SenderName) ? "未知" : m.SenderName);

        var item = new MessageItem
        {
            MessageId = m.MessageId,
            ConversationId = conv.Id,
            SenderId = m.SenderId,
            SenderName = senderName,
            SenderInitials = Initials(senderName),
            AvatarColor = isMine ? (CurrentAccount?.AvatarColor ?? "#C4873C") : ColorForId(m.SenderId),
            Text = text,
            Time = FormatTime(m.Timestamp),
            Timestamp = m.Timestamp,
            IsMine = isMine,
            Kind = kind,
            ShowSenderName = conv.IsGroup && !isMine,
            IsGroup = conv.IsGroup,
            ReplyText = reply,
            ReplyToMessageId = m.ReplyToId ?? "",
            FileName = fileName,
            FileSize = fileSize,
            ImageCaption = imageCaption,
            StatusText = isMine ? "✓✓" : ""
        };
        AttachImages(item, images);
        if (!string.IsNullOrEmpty(m.SenderId))
            ResolveUserAvatar(m.SenderId, b => item.AvatarBitmap = b);
        return item;
    }

    private MessageItem ToMessageItem(MessageEntity e, ConversationItem conv, IReadOnlyDictionary<string, string>? atNameMap = null)
    {
        List<MessageSegment> segments;
        try
        {
            segments = string.IsNullOrEmpty(e.SegmentsJson)
                ? new List<MessageSegment>()
                : JsonSerializer.Deserialize<List<MessageSegment>>(e.SegmentsJson) ?? new();
        }
        catch
        {
            segments = new List<MessageSegment>();
        }

        var ts = ParseTimestamp(e.Timestamp) ?? DateTimeOffset.UnixEpoch;
        var (kind, reply, fileName, fileSize, imageCaption, text, images) =
            BuildSegments(segments, e.Content, e.IsSystemEvent, atNameMap);

        var isMine = e.IsSentBySelf;
        var senderName = isMine
            ? (CurrentAccount?.Nickname ?? "我")
            : (string.IsNullOrEmpty(e.SenderName) ? "未知" : e.SenderName);

        var item = new MessageItem
        {
            MessageId = e.MessageId,
            ConversationId = conv.Id,
            SenderId = e.SenderId,
            SenderName = senderName,
            SenderInitials = Initials(senderName),
            AvatarColor = isMine ? (CurrentAccount?.AvatarColor ?? "#C4873C") : ColorForId(e.SenderId),
            Text = text,
            Time = FormatTime(ts),
            Timestamp = ts,
            IsMine = isMine,
            Kind = kind,
            ShowSenderName = conv.IsGroup && !isMine,
            IsGroup = conv.IsGroup,
            ReplyText = reply,
            ReplyToMessageId = e.ReplyToId ?? "",
            FileName = fileName,
            FileSize = fileSize,
            ImageCaption = imageCaption,
            StatusText = isMine ? "✓✓" : ""
        };
        AttachImages(item, images);
        if (!string.IsNullOrEmpty(e.SenderId))
            ResolveUserAvatar(e.SenderId, b => item.AvatarBitmap = b);
        return item;
    }

    /// <summary>把图片源（URL/本地路径/base64）附加到消息项并异步解析</summary>
    private void AttachImages(MessageItem item, List<string> sources)
    {
        foreach (var src in sources)
        {
            var img = new MessageImage(src, null, _imageCache);
            item.AddImage(img);
            _ = img.ResolveAsync();
        }
    }

    // ---- 头像 ----

    /// <summary>按 URL 解析头像并赋值（账号级缓存去重；失败保持字母占位）</summary>
    private void ResolveAvatar(string url, Action<Bitmap?> setter)
    {
        if (_imageCache is null) return;
        _ = ResolveAvatarCoreAsync(url, setter);
    }

    private async Task ResolveAvatarCoreAsync(string url, Action<Bitmap?> setter)
    {
        try
        {
            if (_avatarCache.TryGetValue(url, out var cached))
            {
                setter(cached);
                return;
            }
            var path = await _imageCache!.ResolveToLocalPathAsync(url);
            if (string.IsNullOrEmpty(path)) return;
            var bmp = await Task.Run(() => new Bitmap(path));
            _avatarCache[url] = bmp;
            setter(bmp);
        }
        catch
        {
            // 头像失败不影响使用，保持字母占位
        }
    }

    /// <summary>解析好友/成员头像</summary>
    private void ResolveUserAvatar(string qq, Action<Bitmap?> setter)
    {
        if (string.IsNullOrEmpty(qq)) return;
        ResolveAvatar(AvatarService.UserAvatarUrl(qq), setter);
    }

    /// <summary>解析群头像</summary>
    private void ResolveGroupAvatar(string groupId, Action<Bitmap?> setter)
    {
        if (string.IsNullOrEmpty(groupId)) return;
        ResolveAvatar(AvatarService.GroupAvatarUrl(groupId), setter);
    }

    private void DisposeAvatarCache()
    {
        foreach (var bmp in _avatarCache.Values) bmp.Dispose();
        _avatarCache.Clear();
    }

    // ---- @ 成员名解析 ----

    /// <summary>确保某群的成员已同步到本地（成员表为空时从 NapCat 拉一次），返回成员列表</summary>
    private async Task<List<GroupMemberEntity>> EnsureMembersAsync(ConversationItem conv)
    {
        var members = _contactSyncService is null
            ? new List<GroupMemberEntity>()
            : await _contactSyncService.GetGroupMembersAsync(conv.TargetId);

        // 成员表可能从未同步过（群同步只拉群列表），首次访问时从 NapCat 拉一次
        if (members.Count == 0)
        {
            var session = _accountManager?.GetSession(conv.AccountId);
            if (session is not null)
            {
                await session.SyncGroupMembersAsync(conv.TargetId);
                members = _contactSyncService is null
                    ? new List<GroupMemberEntity>()
                    : await _contactSyncService.GetGroupMembersAsync(conv.TargetId);
            }
        }
        return members;
    }

    /// <summary>确保某群的成员名映射已加载（群聊 @ 显示名字而非 QQ 号）</summary>
    private async Task<IReadOnlyDictionary<string, string>> EnsureMemberNameMapAsync(ConversationItem conv)
    {
        if (!conv.IsGroup) return new Dictionary<string, string>();
        if (_memberNameMaps.TryGetValue(conv.TargetId, out var map)) return map;
        try
        {
            var members = await EnsureMembersAsync(conv);
            var dict = members.ToDictionary(
                m => m.UserId,
                m => string.IsNullOrEmpty(m.Card) ? m.Nickname : m.Card);
            // 空结果不缓存，下次打开会重试（避免一次失败后永远显示 QQ 号）
            if (dict.Count > 0)
                _memberNameMaps[conv.TargetId] = dict;
            return dict;
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("EnsureMemberNameMapAsync", ex);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>同步取某群的成员名映射（无则返回空，消息先显示 QQ 号兜底）</summary>
    private IReadOnlyDictionary<string, string>? TryGetMemberNameMap(ConversationItem conv)
        => conv.IsGroup && _memberNameMaps.TryGetValue(conv.TargetId, out var map)
            ? map
            : null;

    // ---- 引用消息 ----

    /// <summary>按引用 message_id 补全引用内容：先查本会话内存缓存，再查 DB</summary>
    private async Task ResolveReplyAsync(MessageItem item, ConversationItem conv)
    {
        if (string.IsNullOrEmpty(item.ReplyToMessageId)) return;
        try
        {
            var target = _messageCache.TryGetValue(conv.Id, out var list)
                ? list.FirstOrDefault(m => m.MessageId == item.ReplyToMessageId)
                : null;

            if (target is not null)
            {
                item.ReplyToItemId = target.Id;
                item.ReplySenderName = target.SenderName;
                item.ReplyPreview = ReplyPreviewText(target.Kind, target.Text);
                return;
            }

            if (_historyService is not null)
            {
                var entity = await _historyService.GetMessageAsync(conv.AccountId, item.ReplyToMessageId);
                if (entity is not null)
                {
                    var segments = ParseSegments(entity);
                    var atMap = TryGetMemberNameMap(conv);
                    var (kind, _, _, _, _, text, _) =
                        BuildSegments(segments, entity.Content, entity.IsSystemEvent, atMap);
                    item.ReplySenderName = entity.IsSentBySelf ? (CurrentAccount?.Nickname ?? "我") : entity.SenderName;
                    item.ReplyPreview = ReplyPreviewText(kind, text);
                }
            }
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog("ResolveReplyAsync", ex);
        }
    }

    private static string ReplyPreviewText(MessageKind kind, string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return kind switch
            {
                MessageKind.Image => "[图片]",
                MessageKind.File => "[文件]",
                _ => ""
            };
        return t.Length > 80 ? t[..80] + "…" : t;
    }

    /// <summary>反序列化历史消息的消息段（供引用预览用）</summary>
    private static List<MessageSegment> ParseSegments(MessageEntity e)
    {
        try
        {
            return string.IsNullOrEmpty(e.SegmentsJson)
                ? new List<MessageSegment>()
                : JsonSerializer.Deserialize<List<MessageSegment>>(e.SegmentsJson) ?? new();
        }
        catch
        {
            return new List<MessageSegment>();
        }
    }

    /// <summary>供界面点击引用后定位目标消息（按本地会话 Id）</summary>
    public MessageItem? FindMessageById(string? localId)
        => string.IsNullOrEmpty(localId)
            ? null
            : Messages.FirstOrDefault(m => m.Id == localId);

    private static (MessageKind Kind, string? Reply, string? FileName, string? FileSize, string? ImageCaption, string Text, List<string> Images)
        BuildSegments(List<MessageSegment> segments, string fallbackContent, bool isSystem,
            IReadOnlyDictionary<string, string>? atNameMap = null)
    {
        if (isSystem)
            return (MessageKind.System, null, null, null, null, string.IsNullOrEmpty(fallbackContent) ? "[系统]" : fallbackContent, new List<string>());

        if (segments.Count == 0)
            return (MessageKind.Text, null, null, null, null, fallbackContent, new List<string>());

        var kind = MessageKind.Text;
        string? reply = null, fileName = null, imageCaption = null;
        var text = new StringBuilder();
        var images = new List<string>();
        var hasImage = false;

        foreach (var seg in segments)
        {
            switch (seg.Type)
            {
                case MessageSegmentType.Text:
                    text.Append(seg.Text);
                    break;
                case MessageSegmentType.At:
                    // 群聊优先显示成员名（群名片/昵称），查不到或非群聊回退 QQ 号
                    if (seg.AtUserId == "all")
                        text.Append("@全体");
                    else if (atNameMap is not null && seg.AtUserId is not null &&
                             atNameMap.TryGetValue(seg.AtUserId, out var atName))
                        text.Append("@" + atName);
                    else
                        text.Append("@" + seg.AtUserId);
                    break;
                case MessageSegmentType.Image:
                    hasImage = true;
                    // 优先 URL（收到的消息），本地路径（自己发的）兜底
                    var src = seg.ImageUrl ?? seg.ImageFile;
                    if (!string.IsNullOrEmpty(src)) images.Add(src);
                    break;
                case MessageSegmentType.File:
                    kind = MessageKind.File;
                    fileName = seg.FileName;
                    text.Append("[文件] ");
                    break;
                case MessageSegmentType.Reply:
                    reply ??= "引用了一条消息";
                    break;
                case MessageSegmentType.Record:
                    text.Append("[语音]");
                    break;
                case MessageSegmentType.Video:
                    text.Append("[视频]");
                    break;
                case MessageSegmentType.Face:
                    text.Append("[表情]");
                    break;
                default:
                    text.Append(seg.GetSearchableText());
                    break;
            }
        }

        var display = text.ToString().Trim();
        if (hasImage)
        {
            // 纯图片消息：文字只留给列表预览，气泡里显示图片本体
            if (display.Length == 0)
            {
                kind = MessageKind.Image;
                display = imageCaption ?? "[图片]";
            }
            else
            {
                // 图文混排：文字照常显示，图片跟在文字下面
                kind = MessageKind.Text;
            }
        }
        else if (display.Length == 0)
        {
            display = fallbackContent;
        }

        return (kind, reply, fileName, null, imageCaption, display, images);
    }

    // ---- 工具 ----

    private static string ContactDisplayName(ContactEntity c) =>
        string.IsNullOrEmpty(c.Remark) ? c.Nickname : c.Remark;

    private static string MemberDisplayName(GroupMemberEntity m) =>
        string.IsNullOrEmpty(m.Card) ? m.Nickname : m.Card;

    private static AccountStatus ToAccountStatus(ConnectionState s) => s switch
    {
        ConnectionState.Connected => AccountStatus.Online,
        ConnectionState.Connecting => AccountStatus.Connecting,
        ConnectionState.Reconnecting => AccountStatus.Reconnecting,
        _ => AccountStatus.Offline
    };

    private static string RoleName(int role) => role switch
    {
        2 => "群主",
        1 => "管理员",
        _ => "成员"
    };

    private static string ConversationId(MessageType type, string targetId) =>
        type == MessageType.Group ? "g:" + targetId : "p:" + targetId;

    private static string Initials(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

    private static string ColorForId(string id) =>
        Palette[Math.Abs(id.GetHashCode()) % Palette.Length];

    private static DateTimeOffset? ParseTimestamp(string ts) =>
        DateTimeOffset.TryParse(ts, out var d) ? d : (DateTimeOffset?)null;

    private static string FormatTime(string ts) =>
        ParseTimestamp(ts) is DateTimeOffset d ? FormatTime(d) : string.Empty;

    private static string FormatTime(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "昨天";
        return local.ToString("MM-dd");
    }
}

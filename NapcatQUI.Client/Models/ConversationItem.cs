using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NapcatQUI.Client.Models;

/// <summary>会话项（好友会话 / 群会话统一建模）</summary>
public partial class ConversationItem : ObservableObject
{
    public ConversationItem(string id, string accountId, string name, bool isGroup,
        string initials, string avatarColor, string targetId)
    {
        Id = id;
        AccountId = accountId;
        Name = name;
        IsGroup = isGroup;
        Initials = initials;
        AvatarColor = avatarColor;
        TargetId = targetId;
    }

    public string Id { get; }
    public string AccountId { get; }
    public bool IsGroup { get; }
    public string Initials { get; }
    public string AvatarColor { get; }
    public string TargetId { get; }

    /// <summary>排序用：最新一条消息时间（非 UI 展示）</summary>
    public DateTimeOffset? SortTime { get; set; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string _time = string.Empty;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>头像（异步解析，解析前显示字母占位）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarVisible))]
    private Bitmap? _avatarBitmap;

    public bool AvatarVisible => AvatarBitmap is not null;

    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public string TypeLabel => IsGroup ? "群聊" : "好友";

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadText));
    }
}

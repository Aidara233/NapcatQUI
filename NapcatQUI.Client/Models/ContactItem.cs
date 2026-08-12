using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NapcatQUI.Client.Models;

/// <summary>联系人（好友）显示项</summary>
public partial class ContactItem : ObservableObject
{
    public string UserId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Initials { get; init; } = string.Empty;
    public string AvatarColor { get; init; } = "#D5D0C9";
    public string Category { get; init; } = "好友";
    public string StatusText { get; init; } = "离线";

    /// <summary>头像（异步解析，解析前显示字母占位）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarVisible))]
    private Bitmap? _avatarBitmap;

    public bool AvatarVisible => AvatarBitmap is not null;
}

/// <summary>群成员显示项</summary>
public partial class GroupMemberItem : ObservableObject
{
    public string UserId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Initials { get; init; } = string.Empty;
    public string AvatarColor { get; init; } = "#D5D0C9";
    public string Role { get; init; } = "成员";
    public string SpecialTitle { get; init; } = string.Empty;
    public bool IsOnline { get; init; }

    /// <summary>头像（异步解析，解析前显示字母占位）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarVisible))]
    private Bitmap? _avatarBitmap;

    public bool AvatarVisible => AvatarBitmap is not null;

    /// <summary>AT 选择面板里的选中态</summary>
    [ObservableProperty]
    private bool _isSelected;

    public bool HasSpecialTitle => !string.IsNullOrWhiteSpace(SpecialTitle);
}

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NapcatQUI.Client.Models;

/// <summary>账号显示模型（状态跟随 Core 连接状态实时更新）</summary>
public partial class AccountItem : ObservableObject
{
    public AccountItem(string uin, string nickname, string initials, string avatarColor, AccountStatus status)
    {
        Uin = uin;
        Nickname = nickname;
        Initials = initials;
        AvatarColor = avatarColor;
        _status = status;
        UpdateDerivedProperties();
    }

    public string Uin { get; }
    public string Initials { get; }
    public string AvatarColor { get; }

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private AccountStatus _status;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    private string _statusColor = "#B8B3AC";

    /// <summary>头像（异步解析，解析前显示字母占位）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarVisible))]
    private Bitmap? _avatarBitmap;

    public bool AvatarVisible => AvatarBitmap is not null;

    partial void OnStatusChanged(AccountStatus value) => UpdateDerivedProperties();

    private void UpdateDerivedProperties()
    {
        StatusText = Status switch
        {
            AccountStatus.Online => "已连接",
            AccountStatus.Connecting => "连接中",
            AccountStatus.Reconnecting => "正在重连",
            _ => "未连接"
        };
        StatusColor = Status switch
        {
            AccountStatus.Online => "#5B8C5A",
            AccountStatus.Connecting or AccountStatus.Reconnecting => "#C4943C",
            _ => "#B8B3AC"
        };
    }
}

public enum AccountStatus
{
    Offline,
    Connecting,
    Online,
    Reconnecting
}

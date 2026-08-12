using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NapcatQUI.Client.Media;
using NapcatQUI.Core.Services;

namespace NapcatQUI.Client.Models;

/// <summary>聊天区消息项（由 Core 的 MessageEntity / Message 映射而来）</summary>
public partial class MessageItem : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>真实 NapCat message_id（用于自回声去重）</summary>
    public string MessageId { get; init; } = string.Empty;

    public string ConversationId { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public string SenderName { get; init; } = string.Empty;
    public string SenderInitials { get; init; } = string.Empty;
    public string AvatarColor { get; init; } = "#D5D0C9";
    public string Text { get; init; } = string.Empty;

    /// <summary>消息时间（UTC/带偏移，用于计算时间分隔）</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>本条消息前是否显示时间分隔条（首条 / 跨天 / 间隔≥5分钟）</summary>
    [ObservableProperty]
    private bool _showTimeDivider;

    /// <summary>时间分隔条文案</summary>
    [ObservableProperty]
    private string _time = string.Empty;

    /// <summary>
    /// 依据上一条消息的时间决定是否在此条前显示时间分隔，并生成文案。
    /// 规则：首条必显示；跨天必显示；间隔≥5 分钟显示。
    /// </summary>
    public void UpdateTimeDivider(DateTimeOffset? previousTimestamp)
    {
        if (previousTimestamp is null)
        {
            ShowTimeDivider = true;
        }
        else
        {
            var cur = Timestamp.ToLocalTime();
            var prev = previousTimestamp.Value.ToLocalTime();
            ShowTimeDivider = cur.Date != prev.Date || (cur - prev).TotalMinutes >= 5;
        }
        if (ShowTimeDivider)
            Time = FormatDividerTime(Timestamp);
    }

    /// <summary>时间分隔文案：今天 HH:mm / 昨天 HH:mm / 今年 M月d日 HH:mm / 跨年 yyyy年M月d日 HH:mm</summary>
    private static string FormatDividerTime(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        if (local.Year == today.Year) return local.ToString("M月d日 HH:mm");
        return local.ToString("yyyy年M月d日 HH:mm");
    }

    public bool IsMine { get; init; }
    public MessageKind Kind { get; init; } = MessageKind.Text;

    public string? ReplyText { get; init; }

    /// <summary>引用消息的 NapCat message_id（导航定位用）</summary>
    public string ReplyToMessageId { get; set; } = string.Empty;

    /// <summary>引用消息的本地会话 Id（目标在本会话内时设置，用于滚动定位）</summary>
    [ObservableProperty]
    private string? _replyToItemId;

    /// <summary>引用消息的发送者昵称（异步补全）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyContent))]
    private string? _replySenderName;

    /// <summary>引用消息的内容摘要（异步补全）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyContent))]
    [NotifyPropertyChangedFor(nameof(ReplyDisplay))]
    private string? _replyPreview;

    public bool HasReplyContent => !string.IsNullOrWhiteSpace(ReplySenderName) || !string.IsNullOrWhiteSpace(ReplyPreview);

    /// <summary>引用框显示文本：优先真实内容，其次占位</summary>
    public string ReplyDisplay =>
        !string.IsNullOrWhiteSpace(ReplyPreview)
            ? ReplyPreview!
            : (!string.IsNullOrWhiteSpace(ReplyText) ? ReplyText! : "消息已过期或不存在");

    /// <summary>被点击引用后高亮当前消息</summary>
    [ObservableProperty]
    private bool _isHighlighted;

    /// <summary>头像（异步解析，解析前显示字母占位）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarVisible))]
    private Bitmap? _avatarBitmap;

    public bool AvatarVisible => AvatarBitmap is not null;

    public string? FileName { get; init; }
    public string? FileSize { get; init; }
    public string? ImageCaption { get; init; }
    public string StatusText { get; init; } = "✓✓";

    /// <summary>群聊且非自己时显示发送者昵称</summary>
    public bool ShowSenderName { get; init; }

    /// <summary>是否群聊消息（右键 @ 只在群聊显示）</summary>
    public bool IsGroup { get; init; }

    /// <summary>本机加入列表的时间，用于识别自回声去重窗口</summary>
    public DateTime AddedAt { get; init; } = DateTime.Now;

    /// <summary>消息里的图片（可能多张），URL 解析完成后更新 Bitmap</summary>
    public ObservableCollection<MessageImage> Images { get; } = new();

    private bool _hasImages;
    public bool HasImages => _hasImages;

    public void AddImage(MessageImage img)
    {
        Images.Add(img);
        _hasImages = true;
        OnPropertyChanged(nameof(HasImages));
    }

    public bool IsSystem => Kind == MessageKind.System;
    public bool IsOther => !IsMine && !IsSystem;
    public bool IsGroupAndOther => IsGroup && IsOther;

    /// <summary>可戳：群聊里（含自己）或私聊对方</summary>
    public bool CanPoke => IsOther || IsGroup;
    public bool IsNotSystem => !IsSystem;
    public bool HasReply => !string.IsNullOrWhiteSpace(ReplyText);
    public bool IsImage => Kind == MessageKind.Image;
    public bool IsFile => Kind == MessageKind.File;
    public bool IsTextLike => Kind is MessageKind.Text or MessageKind.At or MessageKind.Reply;
    public bool ShowName => ShowSenderName;
    public HorizontalAlignment BubbleAlignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
}

public enum MessageKind
{
    Text,
    At,
    Reply,
    Image,
    File,
    System,
    Unknown
}

/// <summary>
/// 单张图片：Source 是 URL / 本地路径 / base64，解析完成后 Bitmap 非空。
/// 需在 UI 线程调用 <see cref="ResolveAsync"/>，内部下载在后台线程，赋值回 UI 线程。
/// </summary>
public partial class MessageImage : ObservableObject
{
    private readonly ImageCacheService? _cache;

    public string Source { get; }
    public string? Caption { get; }

    [ObservableProperty]
    private Bitmap? _bitmap;

    /// <summary>解析成功后的本地缓存文件路径（查看器用它打开大图）</summary>
    [ObservableProperty]
    private string? _localPath;

    [ObservableProperty]
    private bool _loading = true;

    [ObservableProperty]
    private bool _failed;

    private GifPlayer? _gifPlayer;
    private bool _gifPlay = true;   // 会话可见时是否需要播放
    private bool _gifLoaded;
    private bool _gifPlaying;
    private bool _gifDisposed;      // 解码过程中被释放则丢弃结果

    public MessageImage(string source, string? caption, ImageCacheService? cache)
    {
        Source = source;
        Caption = caption;
        _cache = cache;
    }

    public async Task ResolveAsync()
    {
        try
        {
            string? local;
            if (_cache is not null)
                local = await _cache.ResolveToLocalPathAsync(Source);
            else
                local = File.Exists(Source) ? Source : null;

            if (string.IsNullOrEmpty(local))
            {
                Failed = true;
                return;
            }
            LocalPath = local;

            // GIF 动画优先；失败/非动画退化为静态首帧
            if (GifPlayer.IsGifPath(local) && await LoadGifAsync(local))
                return;

            // 静态图解码放后台线程，避免大图卡 UI
            var bmp = await Task.Run(() =>
            {
                using var fs = File.OpenRead(local);
                return new Bitmap(fs);
            });
            Bitmap = bmp;
        }
        catch
        {
            Failed = true;
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task<bool> LoadGifAsync(string local)
    {
        try
        {
            var player = new GifPlayer();
            var ok = await player.LoadAsync(local);
            if (!ok || _gifDisposed) // 解码期间被释放则丢弃
            {
                player.Dispose();
                return false;
            }
            _gifPlayer = player;
            _gifLoaded = true;
            if (_gifPlay)
            {
                _gifPlaying = true;
                player.Start(f => Bitmap = f);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>会话可见性变化：离开会话停播，回来继续（帧已缓存，不重新解码）</summary>
    public void SetGifVisible(bool visible)
    {
        _gifPlay = visible;
        if (visible)
        {
            if (_gifLoaded && _gifPlayer is not null && !_gifPlaying)
            {
                _gifPlaying = true;
                _gifPlayer.Start(f => Bitmap = f);
            }
        }
        else
        {
            _gifPlaying = false;
            _gifPlayer?.Stop();
        }
    }

    /// <summary>彻底释放 GIF 帧与定时器（账号切换/清空数据时调用）</summary>
    public void DisposeGif()
    {
        _gifDisposed = true;
        _gifPlay = false;
        _gifPlaying = false;
        _gifLoaded = false;
        _gifPlayer?.Dispose();
        _gifPlayer = null;
    }
}

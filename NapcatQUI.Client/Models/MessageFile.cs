using CommunityToolkit.Mvvm.ComponentModel;

namespace NapcatQUI.Client.Models;

public enum MessageFileState
{
    Idle,        // 未下载
    Downloading, // 下载中
    Done,        // 已下载（可打开）
    Failed       // 下载失败
}

/// <summary>
/// 消息里的一个文件块（纯状态模型，无下载逻辑）。下载/打开由 ViewModel 命令驱动，
/// 命令负责解析 NapCat 直链/get_file 并把结果写回 LocalPath/State。
/// </summary>
public partial class MessageFile : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public string? FileId { get; init; }
    public string? Url { get; init; }

    [ObservableProperty]
    private string? _localPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsLocalFile))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(SubLine))]
    private MessageFileState _state = MessageFileState.Idle;

    public bool IsIdle => State == MessageFileState.Idle;
    public bool IsDownloading => State == MessageFileState.Downloading;
    public bool IsDone => State == MessageFileState.Done;
    public bool IsFailed => State == MessageFileState.Failed;

    /// <summary>发送端本地文件已存在，直接可打开，无需下载</summary>
    public bool IsLocalFile => !string.IsNullOrEmpty(LocalPath) && State == MessageFileState.Idle;

    public string FormattedSize => Size > 0 ? FormatSize(Size) : "";

    /// <summary>状态动作提示（下载/下载中/已下载/下载失败），与大小拼成卡片副行</summary>
    public string StateText => State switch
    {
        MessageFileState.Idle => "点击下载",
        MessageFileState.Downloading => "下载中…",
        MessageFileState.Done => "已下载，点击打开",
        MessageFileState.Failed => "下载失败，点击重试",
        _ => ""
    };

    /// <summary>卡片副行：大小 · 状态</summary>
    public string SubLine => string.IsNullOrEmpty(FormattedSize) ? StateText : $"{FormattedSize} · {StateText}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.#") + " GB";
    }
}

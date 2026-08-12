using CommunityToolkit.Mvvm.ComponentModel;
using NapcatQUI.Core.Services;

namespace NapcatQUI.Client.Models;

public enum ComposeSegmentKind
{
    Text,
    At,
    Image,
    File
}

/// <summary>
/// 输入区待发消息的单个片段：文字 / @ 成员 / 图片。
/// 一个 ComposeSegments 列表按顺序组成一条消息，支持 @/图片/文字任意交叉排列。
/// </summary>
public partial class ComposeSegment : ObservableObject
{
    private ComposeSegment(ComposeSegmentKind kind) => Kind = kind;

    public ComposeSegmentKind Kind { get; }

    /// <summary>@ 段：被 @ 的成员 QQ 号</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>@ 段显示文本（"@名字"）</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>图片段：缩略图（ResolveAsync 异步加载）</summary>
    public MessageImage? Image { get; init; }

    /// <summary>文件段：本地文件路径</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>文件段：文件名</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>文件段：字节大小</summary>
    public long FileSize { get; init; }

    /// <summary>文字段内容（可直接编辑）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextPreview))]
    private string _text = string.Empty;

    public bool IsEmptyText => Kind == ComposeSegmentKind.Text && string.IsNullOrWhiteSpace(Text);

    // 模板按 Kind 切换显示用（Kind 只读不变，无需通知）
    public bool IsText => Kind == ComposeSegmentKind.Text;
    public bool IsAt => Kind == ComposeSegmentKind.At;
    public bool IsImage => Kind == ComposeSegmentKind.Image;
    public bool IsFile => Kind == ComposeSegmentKind.File;

    /// <summary>待发条 ToolTip：图片文件名</summary>
    public string? ImageTip => Image is null ? null : System.IO.Path.GetFileName(Image.Source);

    /// <summary>待发条 ToolTip：被 @ 成员</summary>
    public string AtTip => $"成员 {DisplayName} ({UserId})";

    /// <summary>待发条 ToolTip：文件名 + 大小</summary>
    public string FileTip => FileSize > 0 ? $"{FileName}（{FormatSize(FileSize)}）" : FileName;

    /// <summary>待发条文本块显示：空文本时显示"文本"占位，避免出现空白灰块</summary>
    public string TextPreview => string.IsNullOrWhiteSpace(Text) ? "文本" : Text;

    public static ComposeSegment CreateText() => new(ComposeSegmentKind.Text);

    public static ComposeSegment CreateAt(string userId, string name) =>
        new(ComposeSegmentKind.At) { UserId = userId, DisplayName = "@" + name };

    public static ComposeSegment CreateImage(string path, ImageCacheService? cache)
    {
        var img = new MessageImage(path, null, cache);
        var seg = new ComposeSegment(ComposeSegmentKind.Image) { Image = img };
        _ = img.ResolveAsync();
        return seg;
    }

    public static ComposeSegment CreateFile(string path)
    {
        long size = 0;
        try { size = new System.IO.FileInfo(path).Length; } catch { /* 取不到就不显示大小 */ }
        return new ComposeSegment(ComposeSegmentKind.File)
        {
            FilePath = path,
            FileName = System.IO.Path.GetFileName(path),
            FileSize = size
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.#") + " GB";
    }
}

namespace NapcatQUI.Client.Models;

public enum MessageDisplayBlockKind
{
    Text,
    Image
}

/// <summary>
/// 消息气泡里的一个有序渲染块：文本或图片。按消息段原始顺序排列，
/// 使「文字-图片-文字」的混排消息能按原顺序显示，而非图片恒在前。
/// </summary>
public class MessageDisplayBlock
{
    private MessageDisplayBlock(MessageDisplayBlockKind kind) => Kind = kind;

    public MessageDisplayBlockKind Kind { get; }

    /// <summary>文本块内容</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>图片块（与 MessageItem.Images 中的实例一致）</summary>
    public MessageImage? Image { get; init; }

    public bool IsText => Kind == MessageDisplayBlockKind.Text;
    public bool IsImage => Kind == MessageDisplayBlockKind.Image;

    public static MessageDisplayBlock CreateText(string text) =>
        new(MessageDisplayBlockKind.Text) { Text = text };

    public static MessageDisplayBlock CreateImage(MessageImage img) =>
        new(MessageDisplayBlockKind.Image) { Image = img };
}

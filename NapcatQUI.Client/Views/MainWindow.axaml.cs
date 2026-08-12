using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NapcatQUI.Client.Models;
using NapcatQUI.Client.ViewModels;

namespace NapcatQUI.Client.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? _messageScroll;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        _messageScroll = this.FindControl<ScrollViewer>("MessageScroll");
        DataContextChanged += (_, _) => OnDataContextChanged();

        // 回车发送必须用 Tunnel 路由：在 TextBox 默认换行处理之前拦截。
        // （AcceptsReturn=True 的 TextBox 会吞掉 Enter，bubble 收不到；中文输入法组词选字时回车由输入法框架消费）
        var composerGrid = this.FindControl<Grid>("ComposerGrid");
        if (composerGrid is not null)
            composerGrid.AddHandler(InputElement.KeyDownEvent, OnComposeTextKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged()
    {
        if (_vm is not null)
        {
            _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm.ComposerFocusRequested -= OnComposerFocusRequested;
        }

        _vm = DataContext as MainViewModel;

        if (_vm is not null)
        {
            _vm.Messages.CollectionChanged += OnMessagesChanged;
            _vm.ComposerFocusRequested += OnComposerFocusRequested;
            _messageScroll = this.FindControl<ScrollViewer>("MessageScroll");
            // 文件选择器由窗口注入（发送图片用）
            _vm.StorageProvider = this.StorageProvider;
        }
    }

    /// <summary>请求聚焦大文本框（发送成功/切会话后自动聚焦）</summary>
    private void OnComposerFocusRequested()
    {
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("Composer")?.Focus());
    }

    /// <summary>
    /// 消息集合变化：新消息滚到底；顶部加载更早消息时不滚底，并把已插入内容的高度
    /// 加到 offset 上，保持原阅读位置。
    /// </summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_messageScroll is null || _vm is null) return;

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0 &&
            e.NewStartingIndex < _vm.Messages.Count - 1)
        {
            // 顶部插入（加载更早）：按插入项高度上移 offset，保持阅读位置
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm is null || _messageScroll is null) return;
                var idx = e.NewStartingIndex;
                if (idx < 0 || idx >= _vm.Messages.Count) return;
                var container = FindMessageContainer(_vm.Messages[idx]);
                if (container is not null)
                    _messageScroll.Offset = new Vector(0, _messageScroll.Offset.Y + container.Bounds.Height);
            });
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_messageScroll is not null)
                _messageScroll.Offset = new Vector(0, _messageScroll.Extent.Height);
        });
    }

    /// <summary>在消息列表里找 DataContext 是给定消息项的容器</summary>
    private Control? FindMessageContainer(MessageItem item)
    {
        if (_messageScroll?.Content is not ItemsControl items) return null;
        return items.FindDescendantOfType<Control>(true, c => ReferenceEquals(c.DataContext, item));
    }

    private void OnAvatarClicked(object? sender, PointerPressedEventArgs e)
    {
        _vm?.ToggleAccountSwitcherCommand.Execute(null);
    }

    private void OnAccountClicked(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is AccountItem account)
            _vm?.SwitchAccountCommand.Execute(account);
    }

    private void OnConversationClicked(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is ConversationItem conv && _vm is not null)
            _vm.SelectedConversation = conv;
    }

    private void OnContactClicked(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is ContactItem contact)
            _vm?.OpenChatCommand.Execute(contact);
    }

    private void OnGroupClicked(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is ConversationItem group)
            _vm?.OpenGroupChatCommand.Execute(group);
    }

    /// <summary>
    /// 右键气泡 → 弹出消息操作菜单。菜单在代码里构建、用闭包捕获消息，
    /// 完全不依赖 DataContext 绑定 / PlacementTarget，保证拿得到消息。
    /// </summary>
    private void OnBubblePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Control bubble || bubble.DataContext is not MessageItem item) return;

        var menu = new ContextMenu();

        var reply = new MenuItem { Header = "回复" };
        reply.Click += (_, _) => _vm?.SetReplyCommand.Execute(item);
        menu.Items.Add(reply);

        if (item.CanPoke)
        {
            var poke = new MenuItem { Header = "戳一戳" };
            poke.Click += (_, _) => _vm?.PokeSenderCommand.Execute(item);
            menu.Items.Add(poke);
        }

        if (item.IsGroupAndOther)
        {
            var at = new MenuItem { Header = "@ TA" };
            at.Click += (_, _) =>
            {
                _vm?.AddAtMemberCommand.Execute(item);
                this.FindControl<TextBox>("Composer")?.Focus();
            };
            menu.Items.Add(at);
        }

        menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = "复制" };
        copy.Click += async (_, _) =>
        {
            var text = string.IsNullOrWhiteSpace(item.Text) ? "（无文字内容）" : item.Text;
            if (this.Clipboard is { } cb)
                await cb.SetTextAsync(text);
        };
        menu.Items.Add(copy);

        menu.Open(bubble);
    }

    /// <summary>@ 选择面板：点成员切换选中</summary>
    private void OnAtMemberClicked(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is GroupMemberItem member)
            _vm?.ToggleAtMemberCommand.Execute(member);
    }

    /// <summary>双击聊天里的图片 → 打开独立查看器（同一消息多图可切换）</summary>
    private void OnImageCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is not Control cell || cell.DataContext is not MessageImage clicked) return;
        if (clicked.Bitmap is null || clicked.LocalPath is null) return;

        // 收集同一消息里已加载完成的图片，按消息内顺序打开
        var paths = new List<string>();
        if (cell.FindAncestorOfType<ItemsControl>()?.DataContext is MessageItem msgItem)
        {
            foreach (var img in msgItem.Images)
                if (img.LocalPath is not null)
                    paths.Add(img.LocalPath);
        }
        else
        {
            paths.Add(clicked.LocalPath);
        }

        var index = paths.IndexOf(clicked.LocalPath);
        if (index < 0) index = 0;
        new ImageViewerWindow(paths, index).Show();
    }

    /// <summary>点击引用框 → 滚动到被引用消息并高亮（仅左键，右键交给消息菜单）</summary>
    private void OnReplyClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if ((sender as Control)?.DataContext is not MessageItem item) return;
        var target = _vm?.FindMessageById(item.ReplyToItemId);
        if (target is null) return;
        ScrollToMessage(target);
    }

    private void ScrollToMessage(MessageItem target)
    {
        if (_messageScroll?.Content is not ItemsControl items || _vm is null) return;
        // 找 DataContext 是目标消息的容器（消息列表非虚拟化，所有容器都在）
        var container = items.FindDescendantOfType<Control>(true, c => ReferenceEquals(c.DataContext, target));
        if (container is null) return;

        var pt = container.TranslatePoint(new Point(0, 0), _messageScroll);
        if (pt is Point p)
            _messageScroll.Offset = new Vector(0, Math.Max(0, _messageScroll.Offset.Y + p.Y - 24));

        target.IsHighlighted = true;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(1600);
            target.IsHighlighted = false;
        });
    }

    private async void OnComposeTextKeyDown(object? sender, KeyEventArgs e)
    {
        // 隧道路由先于目标到达，只处理大文本框里的按键
        if (e.Source is not TextBox) return;

        // Ctrl+V：剪贴板有图片时直接入队，否则交回 TextBox 粘文本
        if (e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) != 0 && !e.Handled)
        {
            var cb = this.Clipboard;
            if (cb is not null)
            {
                var bmp = await cb.TryGetBitmapAsync();
                if (bmp is not null)
                {
                    e.Handled = true;
                    if (_vm is not null)
                        await _vm.SendClipboardImageAsync(bmp);
                    return;
                }
            }
        }

        if (e.Key != Key.Enter || e.Handled) return;

        // Shift/Ctrl+Enter → 换行（交给 TextBox 默认处理）
        if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
            return;

        _vm?.SendMessageCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>双击块间/两端缝隙：在此处插入文本块。前缝 Tag=右侧块；尾缝 Tag=null</summary>
    private void OnInsertionGapDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border b || _vm is null) return;
        if (b.Tag is ComposeSegment anchor)
            _vm.InsertTextBeforeCommand.Execute(anchor);
        else
            _vm.InsertTextAfterCommand.Execute(null);
    }

    /// <summary>
    /// 待发条块点击：
    /// - 文本块：左键切为大文本框当前编辑，右键快速移除。
    /// - @/图片块：左键/右键都弹操作菜单（上移/下移/移除）。
    /// 菜单在代码里构建、用闭包捕获块。
    /// </summary>
    private void OnComposeBlockClicked(object? sender, PointerPressedEventArgs e)
    {
        var right = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        if (sender is not Control block || block.DataContext is not ComposeSegment seg || _vm is null) return;

        if (seg.Kind == ComposeSegmentKind.Text)
        {
            if (right)
                _vm.RemoveSegmentCommand.Execute(seg);
            else
                _vm.ActivateTextBlockCommand.Execute(seg);
            return;
        }

        OpenBlockContextMenu(block, seg);
    }

    private void OpenBlockContextMenu(Control block, ComposeSegment seg)
    {
        var menu = new ContextMenu();

        var up = new MenuItem { Header = "上移" };
        up.Click += (_, _) => _vm!.MoveSegmentUpCommand.Execute(seg);
        menu.Items.Add(up);

        var down = new MenuItem { Header = "下移" };
        down.Click += (_, _) => _vm!.MoveSegmentDownCommand.Execute(seg);
        menu.Items.Add(down);

        menu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "移除" };
        remove.Click += (_, _) => _vm!.RemoveSegmentCommand.Execute(seg);
        menu.Items.Add(remove);

        menu.Open(block);
    }
}

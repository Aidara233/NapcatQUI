using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NapcatQUI.Client.Media;

namespace NapcatQUI.Client.Views;

/// <summary>
/// 独立图片查看器 — 聊天里双击图片打开。支持多图切换、滚轮缩放、拖拽平移、双击还原、Esc 关闭。
/// GIF 也在此逐帧播放。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly List<string> _paths = new();
    private int _index;
    private double _zoom = 1.0;
    private int _loadVersion;

    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _panTransform = new();
    private GifPlayer? _gifPlayer;

    private bool _panning;
    private Point _lastPanPos;

    public ImageViewerWindow()
    {
        InitializeComponent();
        ViewerImage.RenderTransform = new TransformGroup
        {
            Children = { _scaleTransform, _panTransform }
        };
        Closed += (_, _) =>
        {
            _gifPlayer?.Dispose();
            _gifPlayer = null;
            if (ViewerImage.Source is Bitmap b) b.Dispose();
            ViewerImage.Source = null;
        };
    }

    public ImageViewerWindow(IEnumerable<string> paths, int index) : this()
    {
        _paths.AddRange(paths);
        _index = Math.Clamp(index, 0, Math.Max(0, _paths.Count - 1));
        PrevButton.IsVisible = _paths.Count > 1;
        NextButton.IsVisible = _paths.Count > 1;
        _ = LoadCurrent();
    }

    private async Task LoadCurrent()
    {
        var version = ++_loadVersion;
        if (_paths.Count == 0)
        {
            Close();
            return;
        }

        if (ViewerImage.Source is Bitmap old) old.Dispose();
        ViewerImage.Source = null;
        _gifPlayer?.Dispose();
        _gifPlayer = null;
        _zoom = 1.0;
        _panTransform.X = 0;
        _panTransform.Y = 0;
        UpdateZoom();

        var path = _paths[_index];
        if (GifPlayer.IsGifPath(path))
        {
            var player = new GifPlayer();
            var ok = await player.LoadAsync(path); // 后台解码全部帧
            if (version != _loadVersion)
            {
                player.Dispose();
                return;
            }
            if (ok)
            {
                _gifPlayer = player;
                player.Start(f => ViewerImage.Source = f);
            }
            else
            {
                player.Dispose();
                ViewerImage.Source = new Bitmap(path); // 静态兜底（首帧）
            }
        }
        else
        {
            try
            {
                var bmp = await Task.Run(() => new Bitmap(path));
                if (version != _loadVersion)
                {
                    bmp.Dispose();
                    return;
                }
                ViewerImage.Source = bmp;
            }
            catch (Exception ex)
            {
                Program.WriteCrashLog("ImageViewerWindow.LoadCurrent", ex);
            }
        }

        IndexText.Text = $"{_index + 1} / {_paths.Count}";
    }

    private void OnPrev(object? sender, RoutedEventArgs e)
    {
        if (_paths.Count == 0) return;
        _index = (_index - 1 + _paths.Count) % _paths.Count;
        _ = LoadCurrent();
    }

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_paths.Count == 0) return;
        _index = (_index + 1) % _paths.Count;
        _ = LoadCurrent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                OnPrev(sender, e);
                break;
            case Key.Right:
                OnNext(sender, e);
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomBy(1.2);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomBy(1 / 1.2);
                break;
        }
    }

    private void OnViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ZoomBy(e.Delta.Y > 0 ? 1.1 : 0.9);
    }

    private void OnViewerDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 双击：回到适应窗口大小
        _zoom = 1.0;
        _panTransform.X = 0;
        _panTransform.Y = 0;
        UpdateZoom();
    }

    private void OnViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _panning = true;
            _lastPanPos = e.GetPosition(this);
            e.Pointer.Capture(ViewerImage);
        }
    }

    private void OnViewerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var pos = e.GetPosition(this);
        _panTransform.X += pos.X - _lastPanPos.X;
        _panTransform.Y += pos.Y - _lastPanPos.Y;
        _lastPanPos = pos;
    }

    private void OnViewerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = false;
        if (ReferenceEquals(e.Pointer.Captured, ViewerImage))
            e.Pointer.Capture(null);
    }

    private void OnViewerPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _panning = false;
    }

    private void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.2, 8.0);
        UpdateZoom();
    }

    private void UpdateZoom()
    {
        _scaleTransform.ScaleX = _zoom;
        _scaleTransform.ScaleY = _zoom;
    }
}

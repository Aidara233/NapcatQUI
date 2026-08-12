using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace NapcatQUI.Client.Media;

/// <summary>
/// GIF 逐帧播放 — SkiaSharp 的 SKCodec 解码全部帧（含帧间合成），
/// DispatcherTimer 按每帧时长在 UI 线程轮播。
/// 解码放后台线程，避免大 GIF 卡界面。
/// </summary>
public sealed class GifPlayer : IDisposable
{
    private const int MaxFrames = 150; // 帧数上限，防止超大 GIF 撑爆内存

    private readonly List<Bitmap> _frames = new();
    private readonly List<int> _durationsMs = new();
    private DispatcherTimer? _timer;
    private int _frameIndex;

    /// <summary>解码成功的帧数（&gt;1 才是动画）</summary>
    public int FrameCount => _frames.Count;

    public static bool IsGifPath(string path) =>
        !string.IsNullOrEmpty(path) &&
        path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 后台解码 GIF 全部帧；非 GIF / 帧数不足 / 解码失败返回 false。
    /// 逐帧单独解码后按 GIF disposal 规则手工合成到累积画布 —— 不用 Skia 的
    /// priorFrame 合成（该路径在部分 GIF 上会凭空产生透明噪点）。
    /// </summary>
    public Task<bool> LoadAsync(string path) => Task.Run(() =>
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var codec = SKCodec.Create(fs);
            if (codec == null || codec.FrameCount <= 1 || codec.FrameCount > MaxFrames)
                return false;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height);

            using var canvas = new SKBitmap(info);     // 累积画布（透明起步）
            using var gifCanvas = new SKCanvas(canvas);
            using var tmp = new SKBitmap(info);        // 当前帧单独解码
            SKBitmap? stateBefore = null;              // 本帧绘制前的状态（RestorePrevious 用）

            for (int i = 0; i < codec.FrameCount; i++)
            {
                // 上一帧的 disposal 先作用在画布上
                if (i > 0)
                {
                    var prevDisp = codec.FrameInfo[i - 1].DisposalMethod;
                    if (prevDisp == SKCodecAnimationDisposalMethod.RestoreBackgroundColor)
                        gifCanvas.Clear();
                    else if (prevDisp == SKCodecAnimationDisposalMethod.RestorePrevious && stateBefore != null)
                    {
                        gifCanvas.Clear();
                        gifCanvas.DrawBitmap(stateBefore, 0, 0);
                    }
                }

                // 保存画本帧前的状态（供本帧的 RestorePrevious 用）
                stateBefore?.Dispose();
                stateBefore = canvas.Copy();

                // 单独解码本帧（不合成），再 1:1 画到累积画布
                if (codec.GetPixels(info, tmp.GetPixels(), new SKCodecOptions(i, -1)) != SKCodecResult.Success)
                    continue;
                gifCanvas.DrawBitmap(tmp, 0, 0);

                using var outBmp = canvas.Copy();
                using var img = SKImage.FromBitmap(outBmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                if (data == null) continue;

                using var ms = new MemoryStream(data.ToArray());
                _frames.Add(new Bitmap(ms));

                var dur = codec.FrameInfo[i].Duration;
                _durationsMs.Add(dur > 0 ? dur : 60);
            }

            stateBefore?.Dispose();
            return _frames.Count >= 2;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>在 UI 线程开始轮播，首帧立即回调。可重复调用（会从首帧重来）。</summary>
    public void Start(Action<Bitmap> onFrame)
    {
        Stop();
        if (_frames.Count < 2) return;

        _frameIndex = 0;
        onFrame(_frames[0]);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(_durationsMs[0], 40)) };
        _timer.Tick += (_, _) =>
        {
            _frameIndex = (_frameIndex + 1) % _frames.Count;
            onFrame(_frames[_frameIndex]);
            _timer!.Interval = TimeSpan.FromMilliseconds(Math.Max(_durationsMs[_frameIndex], 40));
        };
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    public void Dispose()
    {
        Stop();
        foreach (var f in _frames) f.Dispose();
        _frames.Clear();
        _durationsMs.Clear();
    }
}

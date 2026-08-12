using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NapcatQUI.Core.Services;

/// <summary>
/// 图片缓存 — 把 NapCat 消息里的远程图片 URL 下载到本地缓存目录，
/// 发送的本地图片 / 粘贴图片也统一落在这里。按 URL 去重，已存在的不重复下载。
/// </summary>
public class ImageCacheService
{
    private readonly ILogger<ImageCacheService> _logger;
    private readonly string _imageDir;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly SemaphoreSlim _downloadGate = new(8); // 并发下载上限，避免批量头像打爆连接

    public ImageCacheService(string appDataDir, ILogger<ImageCacheService> logger)
    {
        _logger = logger;
        _imageDir = Path.Combine(appDataDir, "images");
        Directory.CreateDirectory(_imageDir);

        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        _http.Timeout = TimeSpan.FromSeconds(25);
        // QQ CDN 对 UA/Referer 敏感，用浏览器 UA，QQ 域名补 Referer
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("image/*, */*;q=0.5");
    }

    /// <summary>生成粘贴图片的落盘路径（PNG），发送前由调用方写入字节</summary>
    public string CreatePasteImagePath() => Path.Combine(_imageDir, $"clip-{Guid.NewGuid():N}.png");

    /// <summary>
    /// 把任意图片来源解析成本地缓存文件路径：
    /// http(s) URL → 下载缓存；base64:// → 解码落盘；本地文件路径 → 原样返回。
    /// 解析失败返回 null。
    /// </summary>
    public async Task<string?> ResolveToLocalPathAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        // base64:// 内嵌数据
        if (source.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
            return await DecodeBase64Async(source);

        // 本地文件路径（不含协议头）
        if (!source.Contains("://") && File.Exists(source))
            return source;

        // 远程 URL
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return await DownloadAsync(uri, ct);

        return null;
    }

    private async Task<string?> DownloadAsync(Uri uri, CancellationToken ct)
    {
        // 已缓存过就直接用
        var existing = FindCached(uri.AbsoluteUri);
        if (existing != null) return existing;

        var key = uri.AbsoluteUri;
        var task = _inflight.GetOrAdd(key, _ => DownloadCoreAsync(uri, ct));
        var result = await task;

        // 失败的不留在并发表里，下次可重试
        if (result == null)
            _inflight.TryRemove(key, out _);
        return result;
    }

    private async Task<string?> DownloadCoreAsync(Uri uri, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct); // 限制并发，避免大量头像同时下载
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // 腾讯图床（gchat.qpic.cn / multimedia.nt.qq.com.cn 等）校验 Referer
            if (IsTencentHost(uri.Host))
                req.Headers.Referrer = new Uri("https://qun.qq.com/");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Image download failed: {Status} {Url}", (int)resp.StatusCode, MaskUrl(uri.AbsoluteUri));
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var path = Path.Combine(_imageDir, $"{HashUrl(uri.AbsoluteUri)}.{DetectExt(bytes)}");
            await File.WriteAllBytesAsync(path, bytes, ct);
            _logger.LogDebug("Image cached: {Path}", path);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image download error: {Url}", MaskUrl(uri.AbsoluteUri));
            return null;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static bool IsTencentHost(string host) =>
        host.EndsWith(".qpic.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".qq.com.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> DecodeBase64Async(string source)
    {
        try
        {
            var bytes = Convert.FromBase64String(source["base64://".Length..]);
            var path = Path.Combine(_imageDir, $"{HashUrl(source)}.{DetectExt(bytes)}");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode base64 image");
            return null;
        }
    }

    private string? FindCached(string url)
    {
        var hash = HashUrl(url);
        var matches = Directory.GetFiles(_imageDir, hash + ".*");
        return matches.Length > 0 ? matches[0] : null;
    }

    private static string HashUrl(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

    /// <summary>根据文件头魔数推断扩展名（供 Bitmap 解码与后续复用）</summary>
    private static string DetectExt(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "jpg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "gif";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "webp";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "bmp";
        return "jpg";
    }

    private static string MaskUrl(string url) =>
        url.Length <= 96 ? url : url[..96] + "…";
}

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
/// 文件缓存 — 把收到的文件下载到本地缓存目录，供打开/另存复用。
/// 镜像 ImageCacheService：按 fileId/url 去重、并发限流、腾讯域名补 Referer。
/// </summary>
public class FileCacheService
{
    private readonly ILogger<FileCacheService> _logger;
    private readonly string _fileDir;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly SemaphoreSlim _downloadGate = new(8);

    public FileCacheService(string appDataDir, ILogger<FileCacheService> logger)
    {
        _logger = logger;
        _fileDir = Path.Combine(appDataDir, "files");
        Directory.CreateDirectory(_fileDir);

        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    /// <summary>下载远程文件到缓存，按 url 去重。返回本地路径，失败返回 null。</summary>
    public async Task<string?> DownloadAsync(string url, string fileName, CancellationToken ct = default)
    {
        var existing = FindCached(url);
        if (existing != null) return existing;

        var task = _inflight.GetOrAdd(url, _ => DownloadCoreAsync(url, fileName, ct));
        var result = await task;
        if (result == null)
            _inflight.TryRemove(url, out _);
        return result;
    }

    /// <summary>把 base64 内容解码落盘到缓存。返回本地路径，失败返回 null。</summary>
    public async Task<string?> DecodeBase64Async(string base64, string fileName, CancellationToken ct = default)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var path = Path.Combine(_fileDir, $"{HashKey("base64:" + base64)}{Ext(fileName)}");
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode base64 file {Name}", fileName);
            return null;
        }
    }

    private async Task<string?> DownloadCoreAsync(string url, string fileName, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var host = new Uri(url).Host;
            if (IsTencentHost(host))
                req.Headers.Referrer = new Uri("https://qun.qq.com/");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("File download failed: {Status} {Url}", (int)resp.StatusCode, Mask(url));
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var path = Path.Combine(_fileDir, $"{HashKey(url)}{Ext(fileName)}");
            await using var fs = File.Create(path);
            await stream.CopyToAsync(fs, ct);
            _logger.LogDebug("File cached: {Path}", path);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File download error: {Url}", Mask(url));
            return null;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private string? FindCached(string key)
    {
        var hash = HashKey(key);
        var matches = Directory.GetFiles(_fileDir, hash + ".*");
        return matches.Length > 0 ? matches[0] : null;
    }

    /// <summary>从文件名取扩展名（含点），无扩展名回退 .bin。</summary>
    private static string Ext(string fileName)
    {
        var ext = string.IsNullOrEmpty(fileName) ? null : Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? ".bin" : ext;
    }

    private static bool IsTencentHost(string host) =>
        host.EndsWith(".qpic.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".qq.com.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase);

    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string Mask(string url) => url.Length <= 96 ? url : url[..96] + "…";
}

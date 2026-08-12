using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
        // 文件可能较大，5 分钟不够；整体超时放宽，具体请求仍受调用方 CancellationToken 控制
        _http.Timeout = TimeSpan.FromMinutes(30);
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

    /// <summary>把 base64 内容解码落盘到缓存（原子写，失败不留半成品）。返回本地路径，失败返回 null。</summary>
    public async Task<string?> DecodeBase64Async(string base64, string fileName, CancellationToken ct = default)
    {
        var key = "base64:" + base64;
        var hash = HashKey(key);
        var finalPath = Path.Combine(_fileDir, hash + Ext(fileName));
        var tempPath = Path.Combine(_fileDir, hash + ".part");

        try
        {
            var bytes = Convert.FromBase64String(base64);
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            Commit(tempPath, finalPath);
            return finalPath;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            _logger.LogWarning(ex, "Failed to decode base64 file {Name}", fileName);
            return null;
        }
    }

    private async Task<string?> DownloadCoreAsync(string url, string fileName, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct);
        try
        {
            var hash = HashKey(url);
            var tempPath = Path.Combine(_fileDir, hash + ".part");
            var finalPath = Path.Combine(_fileDir, hash + Ext(fileName));

            // 腾讯 CDN 防盗链：先不带 Referer 请求，403 再带 Referer 重试一次。
            // 图片图床强制要 Referer，但文件直链多数不带也能下；对齐 NapCat httpDownload
            // 「先裸下、403 再补 Referer」的策略，避免盲目加 Referer 反而被拒。
            var withReferer = false;
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var host = new Uri(url).Host;
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (withReferer && IsTencentHost(host))
                        req.Headers.Referrer = new Uri("https://qun.qq.com/");

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    // 腾讯域名 403（防盗链）→ 带 Referer 重试一次
                    if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden && !withReferer && IsTencentHost(host))
                    {
                        withReferer = true;
                        _logger.LogDebug("File download 403, retrying with Referer: {Url}", Mask(url));
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        // 4xx：URL 过期/资源不存在等永久性错误，重试无意义，快速失败让上层降级到 get_file
                        var permanent = (int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500;
                        if (permanent)
                        {
                            _logger.LogWarning("File download rejected: {Status} {Url}", (int)resp.StatusCode, Mask(url));
                            return null;
                        }
                        // 5xx：服务端瞬时错误，退避重试
                        _logger.LogWarning("File download server error: {Status} {Url}, attempt {Attempt}", (int)resp.StatusCode, Mask(url), attempt);
                        if (attempt < maxAttempts) { await Task.Delay(500 * attempt, ct); continue; }
                        return null;
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    await using (var fs = File.Create(tempPath))
                    {
                        await stream.CopyToAsync(fs, ct);
                        await fs.FlushAsync(ct);
                    }

                    Commit(tempPath, finalPath);
                    _logger.LogDebug("File cached: {Path}", finalPath);
                    return finalPath;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(tempPath);
                    throw; // 外部主动取消，不重试
                }
                catch (Exception ex)
                {
                    TryDelete(tempPath);
                    _logger.LogWarning(ex, "File download error: {Url}, attempt {Attempt}", Mask(url), attempt);
                    if (attempt < maxAttempts) { await Task.Delay(500 * attempt, ct); continue; }
                    return null;
                }
            }
            return null;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>把临时 .part 文件原子替换成最终文件（同目录 move 原子；覆盖旧文件）。</summary>
    private static void Commit(string tempPath, string finalPath)
    {
        if (File.Exists(finalPath))
        {
            try { File.Delete(finalPath); } catch { }
        }
        File.Move(tempPath, finalPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private string? FindCached(string key)
    {
        var hash = HashKey(key);
        // 排除 .part 临时文件：下载中断留下的半成品不能当完整文件返回
        var matches = Directory.GetFiles(_fileDir, hash + ".*")
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .ToArray();
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

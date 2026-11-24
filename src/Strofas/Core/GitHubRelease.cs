using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Strofas.Core;

/// <summary>Один ассет релиза GitHub.</summary>
internal sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>
/// Работа с релизами GitHub.
/// Ключевой приём: проверка последнего тега и скачивание идут через сам github.com
/// (редиректы), а НЕ через api.github.com — квоты60/ч это не ест.
/// API нужен только для списка ассетов (редактор) и там кэшируется по ETag:
/// ответ304 не считается против лимита.
/// </summary>
internal static class GitHubRelease
{
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan, // таймауты — через CancellationToken
    };

    private static string? _token;
    private static DateTime _tokenAt;

    private static string? Token
    {
        get
        {
            if (DateTime.UtcNow - _tokenAt > TimeSpan.FromSeconds(60))
            {
                _tokenAt = DateTime.UtcNow;
                var env = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                var cfg = ConfigService.Load().GithubToken;
                _token = string.IsNullOrWhiteSpace(cfg) ? env : cfg.Trim();
            }
            return _token;
        }
    }

    // ---------- разбор ссылок ----------

    /// <summary>
    /// Принимает "owner/repo" или любую ссылку вида https://github.com/owner/repo[/releases...].
    /// </summary>
    public static bool TryParseRepo(string? input, out string ownerRepo)
    {
        ownerRepo = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        var m = Regex.Match(s, @"github\.com[/:](?<or>[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+)");
        if (m.Success)
        {
            ownerRepo = m.Groups["or"].Value.TrimEnd('/');
            return true;
        }

        if (Regex.IsMatch(s, @"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$"))
        {
            ownerRepo = s;
            return true;
        }
        return false;
    }

    // ---------- версии ----------

    /// <summary>Последний тег через редирект /releases/latest (без API).</summary>
    public static async Task<string?> GetLatestTagAsync(string ownerRepo, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var req = NewRequest($"https://github.com/{ownerRepo}/releases/latest");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null; // релизов нет

            var uri = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var m = Regex.Match(uri, @"/releases/tag/(?<tag>[^/?#]+)");
            return m.Success ? Uri.UnescapeDataString(m.Groups["tag"].Value) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"GetLatestTag({ownerRepo}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Скачать ссылку на ассет последнего/указанного тега без API.</summary>
    public static string DownloadUrl(string ownerRepo, string tag, string asset)
    {
        var a = Uri.EscapeDataString(asset);
        return string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase)
            ? $"https://github.com/{ownerRepo}/releases/latest/download/{a}"
            : $"https://github.com/{ownerRepo}/releases/download/{Uri.EscapeDataString(tag)}/{a}";
    }

    // ---------- ассеты (API, для редактора) ----------

    private sealed record CachedAssets(string Etag, List<ReleaseAsset> Assets, DateTime At);

    private static readonly Dictionary<string, CachedAssets> AssetCache = new();
    private static readonly object CacheLock = new();

    public static async Task<List<ReleaseAsset>> GetAssetsAsync(string ownerRepo, string tag, CancellationToken ct)
    {
        var cacheKey = $"{ownerRepo}|{tag}";
        lock (CacheLock)
        {
            if (AssetCache.TryGetValue(cacheKey, out var cached) &&
                DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(30))
            {
                return cached.Assets;
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var url = string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase)
            ? $"https://api.github.com/repos/{ownerRepo}/releases/latest"
            : $"https://api.github.com/repos/{ownerRepo}/releases/tags/{Uri.EscapeDataString(tag)}";

        using var req = NewRequest(url, api: true);
        string? etag = null;
        lock (CacheLock)
        {
            if (AssetCache.TryGetValue(cacheKey, out var prev)) etag = prev.Etag;
        }
        if (etag is not null)
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotModified)
        {
            lock (CacheLock)
            {
                if (AssetCache.TryGetValue(cacheKey, out var prev))
                {
                    AssetCache[cacheKey] = prev with { At = DateTime.UtcNow };
                    return prev.Assets;
                }
            }
        }
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn($"GetAssets({url}): HTTP {(int)resp.StatusCode}");
            return new List<ReleaseAsset>();
        }

        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        var list = new List<ReleaseAsset>();
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var aurl = a.GetProperty("browser_download_url").GetString() ?? "";
                var size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                if (name.Length > 0) list.Add(new ReleaseAsset(name, aurl, size));
            }
        }

        var newEtag = resp.Headers.ETag?.Tag;
        if (newEtag is not null)
        {
            lock (CacheLock)
            {
                AssetCache[cacheKey] = new CachedAssets(newEtag, list, DateTime.UtcNow);
            }
        }
        return list;
    }

    // ---------- скачивание ----------

    /// <summary>Скачивание файла с прогрессом (0..100) в указанный путь.</summary>
    public static async Task DownloadAsync(string url, string destPath, IProgress<int>? progress,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(15));

        using var req = NewRequest(url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} при скачивании {url}");

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var dst = File.Create(destPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, cts.Token)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), cts.Token);
            read += n;
            if (total > 0 && progress is not null)
                progress.Report((int)Math.Min(100, read * 100 / total));
        }
        progress?.Report(100);
    }

    /// <summary>Шаблон имени ассета: точное совпадение или wildcard (* ?).</summary>
    public static bool MatchAsset(string pattern, string name)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
    }

    private static HttpRequestMessage NewRequest(string url, bool api = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Strofas");
        if (api)
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            var token = Token;
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }
        return req;
    }
}

using System.Diagnostics;
using Strofas.Models;

namespace Strofas.Core;

/// <summary>
/// Жизненный цикл пунктов типа "github": проверка последней версии (без API-квоты),
/// фоновое авто-обновление, установка по клику, отложенное применение staged-копий.
/// Состояние синхронное (для рендера), сетевая часть — fire-and-forget async.
/// </summary>
internal sealed class GithubService
{
    private const int MaxParallelRequests = 2;

    private sealed class Entry
    {
        public string Repo = "";
        public string Asset = "";
        public string LatestTag = "";
        public UpdateVisual Visual = UpdateVisual.None;
        public int Progress;
        public string? Error;
        public DateTime CheckedAt = DateTime.MinValue;
        public bool Busy;       // проверка/скачивание уже идёт
    }

    private readonly ActionExecutor _exec;
    private readonly AppInstaller _installer = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _net = new(MaxParallelRequests);

    /// <summary>Зона/статус изменились — окно перерисовывает бейджи.</summary>
    public event Action? StateChanged;

    /// <summary>Операция (установка/обновление) завершена: ok — меню можно закрыть.</summary>
    public event Action<string, bool, string>? Finished;

    public GithubService(ActionExecutor exec) => _exec = exec;

    // ---------- ключи / записи ----------

    private static string NormalizeRepo(string raw)
        => GitHubRelease.TryParseRepo(raw, out var or) ? or : raw.Trim();

    private static string KeyOf(MenuItemConfig item)
    {
        var a = item.Action!;
        return $"{NormalizeRepo(a.Repo ?? "")}|{a.Asset ?? ""}|{(a.Tag ?? "latest").ToLowerInvariant()}";
    }

    private Entry EntryFor(MenuItemConfig item)
    {
        var a = item.Action!;
        var key = KeyOf(item);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var e)) return e;
            e = new Entry
            {
                Repo = NormalizeRepo(a.Repo ?? ""),
                Asset = a.Asset ?? "",
            };
            // отложенное применение из прошлой сессии
            if (_installer.HasStaging(e.Repo, e.Asset))
                e.Visual = UpdateVisual.Staged;
            _entries[key] = e;
            return e;
        }
    }

    // ---------- синхронные запросы состояния (рендер) ----------

    public UpdateVisual GetVisual(MenuItemConfig? item)
    {
        if (item?.Action is not { IsGithub: true }) return UpdateVisual.None;
        lock (_sync) return EntryFor(item).Visual;
    }

    public int GetProgress(MenuItemConfig? item)
    {
        if (item?.Action is not { IsGithub: true }) return 0;
        lock (_sync) return EntryFor(item).Progress;
    }

    /// <summary>Текст статуса для хаба (или null — показывать breadcrumb как обычно).</summary>
    public string? GetStatusText(MenuItemConfig? item, PressZone zone)
    {
        if (item?.Action is not { IsGithub: true }) return null;
        Entry e;
        lock (_sync) e = EntryFor(item);

        if (zone == PressZone.Update)
        {
            return e.Visual switch
            {
                UpdateVisual.Downloading => $"{e.Progress}%",
                UpdateVisual.Staged => "готово",
                UpdateVisual.Error => "ошибка",
                _ => string.IsNullOrEmpty(e.LatestTag) ? "обновить" : $"→ {e.LatestTag}",
            };
        }

        return e.Visual switch
        {
            UpdateVisual.NotInstalled => "не установлено",
            UpdateVisual.Error => "ошибка",
            _ => null,
        };
    }

    // ---------- проверка версии ----------

    public void CheckIfNeeded(MenuItemConfig item)
    {
        var e = EntryFor(item);
        lock (_sync)
        {
            if (e.Busy) return; // уже идёт проверка/скачивание
        }
        // проверка идёт через редирект github.com (без API-квоты) —
        // выполняем при каждом открытии меню, без TTL
        _ = CheckCoreAsync(item, e);
    }

    /// <summary>Полный проход по пунктам при открытии меню: применить staged + проверить.</summary>
    public void OnMenuOpening(IEnumerable<MenuItemConfig> items)
    {
        _ = ApplyStagedAsync();
        foreach (var it in items)
            if (it.Action is { IsGithub: true })
                CheckIfNeeded(it);
    }

    private async Task CheckCoreAsync(MenuItemConfig item, Entry e)
    {
        lock (_sync)
        {
            if (e.Busy) return;
            e.Busy = true;
        }

        string? downloadTag = null; // запускаем после освобождения Busy — иначе гонка
        try
        {
            await _net.WaitAsync();
            try
            {
                var a = item.Action!;
                var latest = await GitHubRelease.GetLatestTagAsync(e.Repo, CancellationToken.None);
                e.CheckedAt = DateTime.UtcNow;

                if (latest is null)
                {
                    e.Visual = UpdateVisual.Error;
                    e.Error = "нет релизов";
                    return;
                }

                e.LatestTag = latest;
                var installed = _installer.GetInstalled(e.Repo, e.Asset);
                if (installed is null)
                {
                    e.Visual = UpdateVisual.NotInstalled;
                    return;
                }

                if (IsNewer(latest, installed.Version))
                {
                    e.Visual = UpdateVisual.Available;
                    e.Error = null;
                    if (a.AutoUpdate)
                        downloadTag = latest;
                }
                else
                {
                    e.Visual = UpdateVisual.None; // свежая версия
                    e.Error = null;
                }
            }
            finally
            {
                _net.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // таймаут — оставляем прежнее состояние
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                e.Visual = UpdateVisual.Error;
                e.Error = ex.Message;
            }
            Log.Warn($"Check({e.Repo}): {ex.Message}");
        }
        finally
        {
            lock (_sync) e.Busy = false;
            StateChanged?.Invoke();
            if (downloadTag is not null)
                _ = DownloadStageAsync(item, e, downloadTag, triggerLaunch: false);
        }
    }

    /// <summary>Сравнение версий: "v1.10.2" > "v1.9.9", иначе — лексикографически.</summary>
    public static bool IsNewer(string remote, string? local)
    {
        var r = (remote ?? "").Trim().TrimStart('v', 'V');
        var l = (local ?? "").Trim().TrimStart('v', 'V');
        if (string.Equals(r, l, StringComparison.OrdinalIgnoreCase)) return false;

        var rp = r.Split('.');
        var lp = l.Split('.');
        var numeric = true;
        for (var i = 0; i < Math.Max(rp.Length, lp.Length); i++)
        {
            var rs = i < rp.Length ? rp[i] : "0";
            var ls = i < lp.Length ? lp[i] : "0";
            if (!int.TryParse(rs, out var rn) || !int.TryParse(ls, out var ln)) { numeric = false; break; }
            if (rn != ln) return rn > ln;
        }
        if (numeric) return false; // все сегменты равны, но строки разные (суффикс) — не новее
        return string.Compare(r, l, StringComparison.OrdinalIgnoreCase) > 0;
    }

    // ---------- установка / скачивание ----------

    private async Task DownloadStageAsync(MenuItemConfig item, Entry e, string tag,
        bool triggerLaunch, string? launchId = null)
    {
        var id = KeyOf(item);
        lock (_sync)
        {
            if (e.Busy) return;
            e.Busy = true;
            e.Visual = UpdateVisual.Downloading;
            e.Progress = 0;
            e.Error = null;
        }
        StateChanged?.Invoke();

        try
        {
            await _net.WaitAsync();
            try
            {
                var url = GitHubRelease.DownloadUrl(e.Repo, tag, e.Asset);
                Log.Info($"Скачивание (авто) {url}");
                var progress = new Progress<int>(p =>
                {
                    lock (_sync) e.Progress = p;
                    if (p is 25 or 50 or 75 or 100)
                        Log.Info($"  {e.Asset}: {p}%");
                });
                await _installer.StageAsync(e.Repo, tag, e.Asset, url,
                    item.Action!.Sha256, progress, CancellationToken.None);
                _installer.WritePendingTag(e.Repo, e.Asset, tag);
                Log.Info($"stage готов: {tag}");
            }
            finally
            {
                _net.Release();
            }

            // применяем сразу, если целевой exe не запущен
            if (!AppInstaller.IsTargetRunning(e.Repo, e.Asset) &&
                _installer.TryApply(e.Repo, e.Asset, tag))
            {
                _installer.ClearPendingTag(e.Repo, e.Asset);
                lock (_sync)
                {
                    e.Visual = UpdateVisual.None;
                    e.LatestTag = tag;
                }
            }
            else
            {
                lock (_sync) e.Visual = UpdateVisual.Staged;
            }

            if (triggerLaunch) LaunchInstalled(item, e, launchId ?? id);
            Finished?.Invoke(launchId ?? id, true, $"обновлено до {tag}");
        }
        catch (OperationCanceledException)
        {
            lock (_sync) e.Visual = UpdateVisual.Available;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                e.Visual = UpdateVisual.Error;
                e.Error = ex.Message;
            }
            Log.Warn($"Download({e.Repo}): {ex.Message}");
            Finished?.Invoke(id, false, ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                e.Busy = false;
                e.Progress = 0;
            }
            StateChanged?.Invoke();
        }
    }

    private async Task InstallAsync(string id, MenuItemConfig item, Entry e, string? tag)
    {
        lock (_sync)
        {
            if (e.Busy) return;
            e.Visual = UpdateVisual.Downloading;
            e.Progress = 0;
            e.Error = null;
        }
        StateChanged?.Invoke();

        try
        {
            await _net.WaitAsync();
            string usedTag;
            try
            {
                usedTag = tag ?? await GitHubRelease.GetLatestTagAsync(e.Repo, CancellationToken.None)
                    ?? throw new InvalidOperationException("нет релизов");
                var url = GitHubRelease.DownloadUrl(e.Repo, usedTag, e.Asset);
                Log.Info($"Скачивание {url}");
                var progress = new Progress<int>(p =>
                {
                    lock (_sync) e.Progress = p;
                    if (p is 25 or 50 or 75 or 100)
                        Log.Info($"  {e.Asset}: {p}%");
                });
                await _installer.StageAsync(e.Repo, usedTag, e.Asset, url,
                    item.Action!.Sha256, progress, CancellationToken.None);
                _installer.WritePendingTag(e.Repo, e.Asset, usedTag);
                Log.Info($"stage готов: {usedTag}");
            }
            finally
            {
                _net.Release();
            }

            if (!AppInstaller.IsTargetRunning(e.Repo, e.Asset) &&
                _installer.TryApply(e.Repo, e.Asset, usedTag))
            {
                _installer.ClearPendingTag(e.Repo, e.Asset);
                lock (_sync)
                {
                    e.Visual = UpdateVisual.None;
                    e.LatestTag = usedTag;
                }
                LaunchInstalled(item, e, id);
                Finished?.Invoke(id, true, $"установлено {usedTag}");
            }
            else
            {
                lock (_sync) e.Visual = UpdateVisual.Staged;
                Finished?.Invoke(id, false, "файл занят — применится при следующем открытии");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync)
            {
                e.Visual = UpdateVisual.Error;
                e.Error = ex.Message;
            }
            Log.Warn($"Install({e.Repo}): {ex.Message}");
            Finished?.Invoke(id, false, ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                e.Busy = false;
                e.Progress = 0;
            }
            StateChanged?.Invoke();
        }
    }

    private void LaunchInstalled(MenuItemConfig item, Entry e, string id)
    {
        var installed = _installer.GetInstalled(e.Repo, e.Asset);
        if (installed is null)
        {
            Finished?.Invoke(id, false, "не установлено");
            return;
        }
        _exec.LaunchPath(installed.ExePath, item, id);
    }

    // ---------- публичные действия по клику ----------

    public void Execute(string id, MenuItemConfig item, PressZone zone)
    {
        var e = EntryFor(item);

        if (zone == PressZone.Update)
        {
            // обновить: staged уже скачан — применяем+запускаем; иначе качаем свежее
            if (e.Visual == UpdateVisual.Staged && _installer.HasStaging(e.Repo, e.Asset))
            {
                var pending = _installer.GetPending()
                    .FirstOrDefault(p => p.OwnerRepo == e.Repo && p.Asset == e.Asset);
                var tag = string.IsNullOrEmpty(pending.Version) ? e.LatestTag : pending.Version;
                if (!AppInstaller.IsTargetRunning(e.Repo, e.Asset) &&
                    _installer.TryApply(e.Repo, e.Asset, tag))
                {
                    _installer.ClearPendingTag(e.Repo, e.Asset);
                    lock (_sync) e.Visual = UpdateVisual.None;
                    LaunchInstalled(item, e, id);
                    Finished?.Invoke(id, true, $"обновлено до {tag}");
                    StateChanged?.Invoke();
                    return;
                }
                lock (_sync) e.Visual = UpdateVisual.Staged;
            }

            _ = UpdateNowAsync(id, item, e);
            return;
        }

        // зона запуска
        var installed = _installer.GetInstalled(e.Repo, e.Asset);
        if (installed is null)
        {
            _ = InstallAsync(id, item, e, tag: null); // установка + запуск
            return;
        }
        _exec.LaunchPath(installed.ExePath, item, id);
        Finished?.Invoke(id, true, "");
    }

    private async Task UpdateNowAsync(string id, MenuItemConfig item, Entry e)
    {
        try
        {
            var tag = string.IsNullOrEmpty(e.LatestTag)
                ? await GitHubRelease.GetLatestTagAsync(e.Repo, CancellationToken.None)
                : e.LatestTag;
            if (tag is null) throw new InvalidOperationException("нет релизов");
            e.LatestTag = tag;
            await DownloadStageAsync(item, e, tag, triggerLaunch: true);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                e.Visual = UpdateVisual.Error;
                e.Error = ex.Message;
            }
            Finished?.Invoke(id, false, ex.Message);
            StateChanged?.Invoke();
        }
    }

    // ---------- отложенные применения ----------

    private async Task ApplyStagedAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                foreach (var (repo, asset, ver) in _installer.GetPending())
                {
                    if (AppInstaller.IsTargetRunning(repo, asset)) continue;
                    if (_installer.TryApply(repo, asset, ver))
                    {
                        _installer.ClearPendingTag(repo, asset);
                        Log.Info($"Staged применён: {repo} {ver}");
                    }
                }
            });

            // синхронизируем состояние записей с реальностью
            lock (_sync)
            {
                foreach (var e in _entries.Values)
                {
                    if (e.Visual == UpdateVisual.Staged && !_installer.HasStaging(e.Repo, e.Asset))
                        e.Visual = UpdateVisual.None;
                }
            }
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"ApplyStaged: {ex.Message}");
        }
    }
}

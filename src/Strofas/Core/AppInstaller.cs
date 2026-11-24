using System.Diagnostics;
using System.Security.Cryptography;

namespace Strofas.Core;

/// <summary>Состояние установленного приложения из GitHub-релиза.</summary>
internal sealed record InstalledApp(string ExePath, string? Version);

/// <summary>
/// Каталог скачанных приложений: %LOCALAPPDATA%\Strofas\apps\{owner}\{repo}\.
/// Обновление идёт через staging: качаем рядом, применяем атомарно, когда
/// целевой exe не запущен (Windows не даёт перезаписать работающий файл).
/// </summary>
internal sealed class AppInstaller
{
    public static string AppsRoot => Path.Combine(ConfigService.Dir, "apps");

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string DirFor(string ownerRepo)
    {
        var parts = ownerRepo.Split('/');
        return Path.Combine(AppsRoot, Sanitize(parts[0]), Sanitize(parts[1]));
    }

    private static string ExeName(string asset) => Path.GetFileName(asset); // защита от путей в конфиге

    public InstalledApp? GetInstalled(string ownerRepo, string asset)
    {
        try
        {
            var exe = Path.Combine(DirFor(ownerRepo), ExeName(asset));
            if (!File.Exists(exe)) return null;
            var verFile = Path.Combine(DirFor(ownerRepo), "version.txt");
            var ver = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : null;
            return new InstalledApp(exe, ver);
        }
        catch (Exception ex)
        {
            Log.Warn($"GetInstalled({ownerRepo}): {ex.Message}");
            return null;
        }
    }

    private static string StagingPath(string ownerRepo, string asset)
        => Path.Combine(DirFor(ownerRepo), "staging", ExeName(asset));

    public bool HasStaging(string ownerRepo, string asset)
        => File.Exists(StagingPath(ownerRepo, asset));

    /// <summary>Качает ассет в staging (прогресс0..100), сверяет sha256 если задан.</summary>
    public async Task StageAsync(string ownerRepo, string tag, string asset, string url,
        string? sha256, IProgress<int>? progress, CancellationToken ct)
    {
        var tmp = StagingPath(ownerRepo, asset) + ".part";
        await GitHubRelease.DownloadAsync(url, tmp, progress, ct);

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            await using var fs = File.OpenRead(tmp);
            var hash = await SHA256.HashDataAsync(fs, ct);
            var actual = Convert.ToHexString(hash);
            if (!actual.Equals(sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidOperationException($"sha256 не совпал: ожидался {sha256}, получен {actual}");
            }
        }

        File.Move(tmp, StagingPath(ownerRepo, asset), overwrite: true);
    }

    /// <summary>
    /// Переносит staging на место, если целевой exe не запущен.
    /// false = файл занят (процесс работает) — staging остаётся до следующей попытки.
    /// </summary>
    public bool TryApply(string ownerRepo, string asset, string version)
    {
        var staging = StagingPath(ownerRepo, asset);
        if (!File.Exists(staging)) return false;

        var target = Path.Combine(DirFor(ownerRepo), ExeName(asset));
        try
        {
            File.Copy(staging, target, overwrite: true);
            File.Delete(staging);
            File.WriteAllText(Path.Combine(DirFor(ownerRepo), "version.txt"), version);
            Log.Info($"Применено обновление {ownerRepo} → {version}");
            return true;
        }
        catch (IOException)
        {
            return false; // залочен — останется staged
        }
        catch (Exception ex)
        {
            Log.Warn($"TryApply({ownerRepo}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Целевой exe занят процессом? (для решения «качать/применять сейчас»)</summary>
    public static bool IsTargetRunning(string ownerRepo, string asset)
    {
        try
        {
            var exe = Path.Combine(DirFor(ownerRepo), ExeName(asset));
            var name = Path.GetFileNameWithoutExtension(exe);
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // недоступен (доступ/системный) — считаем по имени ниже
                    }
                }
            }
            // запасной приём: процесс с этим именем вообще есть
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Полный список staging-копий, ожидающих применения.</summary>
    public IEnumerable<(string OwnerRepo, string Asset, string Version)> GetPending()
    {
        if (!Directory.Exists(AppsRoot)) yield break;
        foreach (var ownerDir in Directory.GetDirectories(AppsRoot))
        foreach (var repoDir in Directory.GetDirectories(ownerDir))
        {
            var stagingDir = Path.Combine(repoDir, "staging");
            if (!Directory.Exists(stagingDir)) continue;
            foreach (var file in Directory.GetFiles(stagingDir))
            {
                if (file.EndsWith(".part")) continue;
                var ownerRepo = Path.GetFileName(ownerDir) + "/" + Path.GetFileName(repoDir);
                yield return (ownerRepo, Path.GetFileName(file), NextVersionFor(repoDir, file));
            }
        }
    }

    // версия, которая будет записана при применении, известна только чекеру;
    // для отложенного применения хранится рядом со staging
    private static string NextVersionFor(string repoDir, string stagingFile)
    {
        var v = stagingFile + ".tag";
        return File.Exists(v) ? File.ReadAllText(v).Trim() : "";
    }

    public void WritePendingTag(string ownerRepo, string asset, string tag)
    {
        try
        {
            var f = StagingPath(ownerRepo, asset) + ".tag";
            File.WriteAllText(f, tag);
        }
        catch (Exception ex)
        {
            Log.Warn($"WritePendingTag: {ex.Message}");
        }
    }

    public void ClearPendingTag(string ownerRepo, string asset)
    {
        try
        {
            var f = StagingPath(ownerRepo, asset) + ".tag";
            if (File.Exists(f)) File.Delete(f);
        }
        catch
        {
            // ignore
        }
    }
}

namespace Strofas.Core;

/// <summary>Простой файловый лог в %LOCALAPPDATA%\Strofas\strofas.log с ротацией.</summary>
internal static class Log
{
    private static readonly object Sync = new();
    private static string _path = "";

    public static void Init()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Strofas");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "strofas.log");
        RotateIfNeeded();
        Info("=== Strofas started ===");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 512 * 1024)
            {
                var bak = _path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(_path, bak);
            }
        }
        catch
        {
            // ротация — не критично
        }
    }

    private static void Write(string level, string message)
    {
        if (string.IsNullOrEmpty(_path)) return;
        lock (Sync)
        {
            try
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // лог не должен ронять приложение
            }
        }
    }
}

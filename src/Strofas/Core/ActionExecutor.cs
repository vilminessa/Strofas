using System.Diagnostics;
using Strofas.Models;

namespace Strofas.Core;

/// <summary>
/// Выполнение действий пунктов меню: запуск, остановка, toggle.
/// PID запущенного из меню отслеживаются — «стоп» сначала бьёт по ним.
/// </summary>
public sealed class ActionExecutor
{
    private readonly Dictionary<string, int> _pids = new();

    public void Execute(string id, MenuItemConfig item)
    {
        var a = item.Action;
        if (a is null)
        {
            Log.Warn($"Пункт '{item.Label}' без действия");
            return;
        }

        switch (a.Type.Trim().ToLowerInvariant())
        {
            case "launch":
            case "open":
                Launch(id, a, item.Label);
                break;
            case "stop":
                Stop(id, a, item.Label);
                break;
            case "toggle":
                if (IsRunning(id, item)) Stop(id, a, item.Label);
                else Launch(id, a, item.Label);
                break;
            default:
                Log.Warn($"Неизвестный тип действия '{a.Type}' у пункта '{item.Label}'");
                break;
        }
    }

    /// <summary>Есть ли живой процесс, который запускал этот пункт (или по имени из конфига).</summary>
    public bool IsRunning(string id, MenuItemConfig item)
    {
        if (_pids.TryGetValue(id, out var pid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) return true;
            }
            catch
            {
                // процесс уже умер
            }
            _pids.Remove(id);
        }

        var a = item.Action;
        if (a is not null && !string.IsNullOrWhiteSpace(a.ProcessName))
        {
            // явное имя процесса в конфиге — надёжный признак «запущено»
            // (для программ-заглушек вроде notepad.exe на Win11 PID не живёт)
            try
            {
                var name = Path.GetFileNameWithoutExtension(a.ProcessName.Trim());
                return Process.GetProcessesByName(name).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private void Launch(string id, ActionConfig a, string label)
    {
        if (string.IsNullOrWhiteSpace(a.Path))
        {
            Log.Warn($"Пустой путь запуска у '{label}'");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(a.Path.Trim()),
            Arguments = a.Args ?? "",
            UseShellExecute = true,
        };
        if (!string.IsNullOrWhiteSpace(a.WorkingDir))
            psi.WorkingDirectory = Environment.ExpandEnvironmentVariables(a.WorkingDir);
        if (a.Hidden)
            psi.WindowStyle = ProcessWindowStyle.Hidden;

        try
        {
            var p = Process.Start(psi);
            if (p is not null)
            {
                try { _pids[id] = p.Id; }
                catch { /* для URL/документов PID может быть недоступен */ }
                Log.Info($"Запущено '{label}': {psi.FileName} (pid {p.Id})");
            }
            else
            {
                Log.Info($"Запущено '{label}': {psi.FileName}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось запустить '{label}' ({psi.FileName})", ex);
        }
    }

    private void Stop(string id, ActionConfig a, string label)
    {
        // 1. процесс, запущенный этим пунктом меню
        if (_pids.TryGetValue(id, out var pid))
        {
            _pids.Remove(id);
            try
            {
                using var p = Process.GetProcessById(pid);
                Kill(p, label);
                Log.Info($"Остановлено '{label}' (pid {pid})");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"pid {pid} для '{label}' недоступен: {ex.Message}");
            }
        }

        // 2. по имени процесса из конфига
        if (string.IsNullOrWhiteSpace(a.ProcessName))
        {
            Log.Warn($"У '{label}' нет ни PID, ни ProcessName для остановки");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(a.ProcessName.Trim());
        Process[] targets;
        try
        {
            targets = Process.GetProcessesByName(name);
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось найти процесс '{name}'", ex);
            return;
        }

        if (targets.Length == 0)
        {
            Log.Info($"Процесс '{name}' для '{label}' не запущен");
            return;
        }

        foreach (var p in targets)
        {
            using (p)
            {
                try
                {
                    Kill(p, label);
                    Log.Info($"Остановлено '{label}': {name} (pid {p.Id})");
                }
                catch (Exception ex)
                {
                    Log.Error($"Не удалось остановить {name} (pid {p.Id})", ex);
                }
            }
        }
    }

    /// <summary>Сначала мягко (CloseMainWindow), при неудаче — Kill всего дерева.</summary>
    private static void Kill(Process p, string label)
    {
        try
        {
            if (p.CloseMainWindow() && !p.WaitForExit(600))
                p.Kill(entireProcessTree: true);
        }
        catch
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* уже мёртв или нет прав */ }
        }
    }
}

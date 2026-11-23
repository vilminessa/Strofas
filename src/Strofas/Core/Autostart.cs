using Microsoft.Win32;

namespace Strofas.Core;

/// <summary>Автозапуск через HKCU\...\Run.</summary>
internal static class Autostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Strofas";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is not null;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                if (value)
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe)) return;
                    key.SetValue(ValueName, $"\"{exe}\"");
                    Log.Info("Автозапуск включён");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Log.Info("Автозапуск выключен");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось изменить автозапуск", ex);
            }
        }
    }
}

using System.Windows.Media;
using Microsoft.Win32;

namespace Strofas.Core;

/// <summary>
/// Акцентный цвет: по умолчанию — из темы Windows (DWM\AccentColor),
/// можно переопределить hex-значением в конфиге ("accent": "#5B9DFF").
/// </summary>
internal static class ThemeTokens
{
    public const string DefaultAccent = "#7C9BFF";

    public static Color Accent { get; private set; } = ParseHex(DefaultAccent);

    /// <true> — акцент следует за темой Windows и обновляется при её смене.</true>
    public static bool FollowTheme { get; private set; } = true;

    public static event Action? Changed;

    /// <summary>Применяет настройку из конфига: "auto"/пусто — из темы, иначе hex.</summary>
    public static void Apply(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) ||
            setting.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            FollowTheme = true;
            LoadFromTheme();
        }
        else if (TryParseHex(setting, out var color))
        {
            FollowTheme = false;
            Set(color);
        }
        else
        {
            FollowTheme = true;
            LoadFromTheme();
        }
    }

    /// <summary>Читает AccentColor из HKCU\Software\Microsoft\Windows\DWM (ABGR).</summary>
    public static void LoadFromTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw && (raw & 0xFF000000) != 0)
            {
                var r = (byte)(raw & 0xFF);
                var g = (byte)((raw >> 8) & 0xFF);
                var b = (byte)((raw >> 16) & 0xFF);
                if (r != 0 || g != 0 || b != 0)
                {
                    Set(Color.FromRgb(r, g, b));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Чтение AccentColor: {ex.Message}");
        }
        // тема недоступна — оставляем текущий акцент
    }

    private static void Set(Color color)
    {
        if (color == Accent) return;
        Accent = color;
        Log.Info($"Акцент: #{color.R:X2}{color.G:X2}{color.B:X2}");
        Changed?.Invoke();
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6) return false;
        try
        {
            var r = Convert.ToByte(t[..2], 16);
            var g = Convert.ToByte(t[2..4], 16);
            var b = Convert.ToByte(t[4..6], 16);
            color = Color.FromRgb(r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseHex(string text)
    {
        TryParseHex(text, out var c);
        return c;
    }
}

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Strofas.Core;

/// <summary>
/// Системная горячка через RegisterHotKey на message-only окне.
/// Не перехватывает ввод у других программ.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private const int HotkeyId = 0x5354; // "ST"
    private bool _registered;

    /// <summary>Срабатывает на нажатие горячки (UI-поток).</summary>
    public event Action? Pressed;

    /// <summary>Последняя успешно зарегистрированная строка.</summary>
    public string? Current { get; private set; }

    public HotkeyService()
    {
        var ps = new HwndSourceParameters("StrofasHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ParentWindow = new IntPtr(-3),            // HWND_MESSAGE
        };
        _source = new HwndSource(ps);
        _source.AddHook(WndProc);
    }

    /// <summary>Регистрирует горячку из строки вида "Ctrl+B", "Ctrl+Alt+Space", "F12".</summary>
    public bool Register(string hotkey)
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        if (!TryParse(hotkey, out var modifiers, out var vk))
        {
            Log.Warn($"Не удалось разобрать горячку: '{hotkey}'");
            return false;
        }

        _registered = Native.RegisterHotKey(_source.Handle, HotkeyId, modifiers | Native.MOD_NOREPEAT, vk);
        if (_registered)
        {
            Current = hotkey;
            Log.Info($"Горячка зарегистрирована: {hotkey}");
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"RegisterHotKey не прошёл для '{hotkey}' (ошибка {err}) — возможно, занята другой программой");
        }
        return _registered;
    }

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= Native.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= Native.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= Native.MOD_SHIFT;
                    break;
                case "win" or "windows":
                    modifiers |= Native.MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        var keyToken = parts[^1];
        if (keyToken.Length == 1)
        {
            var c = char.ToUpperInvariant(keyToken[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        // именованные клавиши: Space, Escape, F12, Left, ...
        if (Enum.TryParse<Key>(keyToken, ignoreCase: true, out var key) && key != Key.None)
        {
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

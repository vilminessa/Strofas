using System.Runtime.InteropServices;

namespace Strofas.Core;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    private const uint SPI_GETCLIENTAREAANIMATION = 0x0404;

    /// <summary>Включена ли анимация в системе (Settings → Accessibility → Animation effects).</summary>
    public static bool IsAnimationEnabled()
    {
        try
        {
            var value = 0;
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref value, 0))
                return value != 0;
        }
        catch (EntryPointNotFoundException)
        {
            // старая Windows — считаем включённой
        }
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    /// <summary>DPI точки (физический, PerMonitorV2). Win10 1703+.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();
}

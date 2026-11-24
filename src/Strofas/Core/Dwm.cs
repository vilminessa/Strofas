using System.Runtime.InteropServices;

namespace Strofas.Core;

/// <summary>
/// Win11-бэкдроп окна: Acrylic через DWM (DWMWA_SYSTEMBACKDROP_TYPE),
/// скруглённые углы и тёмный non-client.
/// </summary>
internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_NONE = 0;
    private const int DWMSBT_TRANSIENTWINDOW = 2; // Acrylic

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pv, int cb);

    /// <summary>
    /// Применяет Acrylic-фон, скруглённые углы и тёмный заголовок.
    /// false = Windows старше Win11 22H2 или DWM отказал — рисуем стеклянный фолбэк.
    /// </summary>
    public static bool TryApplyAcrylic(IntPtr hwnd, bool quiet = false)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            var corner = DWMWCP_ROUND;
            var hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            var backdrop = DWMSBT_TRANSIENTWINDOW;
            var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            if (!quiet)
                Log.Info($"DWM: backdrop hr=0x{hr:X8} ({(hr == 0 ? "ok" : "fail")}), corner hr=0x{hrCorner:X8}");
            return hr == 0;
        }
        catch (EntryPointNotFoundException)
        {
            if (!quiet) Log.Warn("DwmSetWindowAttribute недоступен (старая Windows)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"DwmSetWindowAttribute: {ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public IntPtr Attrib;
        public IntPtr PvData;
        public IntPtr Size;
    }

    /// <summary>
    /// Legacy-акрил через SetWindowCompositionAttribute — работает с layered-окнами
    /// (WS_EX_LAYERED), где DWMWA_SYSTEMBACKDROP_TYPE может не применяться.
    /// GradientColor — 0xAABBGGRR.
    /// </summary>
    public static bool TryApplyWcaAcrylic(IntPtr hwnd, byte alpha = 0xB0)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            // тёмный тон #10121A с заданной альфой
            var accent = new ACCENT_POLICY
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = (alpha << 24) | (0x1A << 16) | (0x12 << 8) | 0x10,
            };

            var size = Marshal.SizeOf<ACCENT_POLICY>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attrib = new IntPtr(WCA_ACCENT_POLICY),
                    PvData = ptr,
                    Size = new IntPtr(size),
                };
                var result = SetWindowCompositionAttribute(hwnd, ref data);
                Log.Info($"WCA acrylic result=0x{result:X8}");
                return result == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"SetWindowCompositionAttribute: {ex.Message}");
            return false;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    /// <summary>
    /// Скруглённая карточка: режем окно регионом (физические пиксели).
    /// Handle региона забирает система.
    /// </summary>
    public static void ApplyRoundedRegion(IntPtr hwnd, int sizePx, int radiusPx)
    {
        if (hwnd == IntPtr.Zero || sizePx <= 0 || radiusPx <= 0) return;
        try
        {
            var rgn = CreateRoundRectRgn(0, 0, sizePx + 1, sizePx + 1, radiusPx * 2, radiusPx * 2);
            if (rgn != IntPtr.Zero)
                SetWindowRgn(hwnd, rgn, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"SetWindowRgn: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// Исключает окно из screen-capture (WDA_EXCLUDEFROMCAPTURE): наше меню
    /// не попадает в снимок экрана — иначе живой захват ловит сам себя (призраки).
    /// </summary>
    public static void ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                Log.Warn($"SetWindowDisplayAffinity: win32={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Warn($"SetWindowDisplayAffinity: {ex.Message}");
        }
    }

    public static void ClearBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }
        catch
        {
            // ignore
        }
    }
}

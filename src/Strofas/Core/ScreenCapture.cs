using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Strofas.Core;

/// <summary>
/// Статический снимок экрана (для первого кадра при открытии меню).
/// </summary>
internal static class ScreenCapture
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public static BitmapSource? Capture(Rect physicalRect)
    {
        try
        {
            var w = (int)Math.Ceiling(physicalRect.Width);
            var h = (int)Math.Ceiling(physicalRect.Height);
            if (w <= 0 || h <= 0) return null;

            using var bmp = new Drawing.Bitmap(w, h);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen((int)physicalRect.X, (int)physicalRect.Y, 0, 0,
                    new Drawing.Size(w, h));
            }

            var hBitmap = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ScreenCapture: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Живой захват экрана для фона под лепестками. Владеет переиспользуемыми
/// буферами (Bitmap + byte[]), поэтому почти не создаёт мусора; Capture()
/// безопасно вызывать из фонового потока.
/// </summary>
internal sealed class LiveCapture : IDisposable
{
    private readonly Drawing.Bitmap _bmp;
    private readonly Drawing.Graphics _g;
    private readonly Drawing.Rectangle _rect;
    private readonly byte[] _buffer;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }

    public LiveCapture(Rect physicalRect)
    {
        Width = (int)Math.Ceiling(physicalRect.Width);
        Height = (int)Math.Ceiling(physicalRect.Height);
        _rect = new Drawing.Rectangle((int)physicalRect.X, (int)physicalRect.Y, Width, Height);
        _bmp = new Drawing.Bitmap(Width, Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        _g = Drawing.Graphics.FromImage(_bmp);
        _stride = Width * 4;
        _buffer = new byte[_stride * Height];
    }

    /// <summary>Копирует текущий кадр экрана в переиспользуемый буфер (BGRA).</summary>
    public byte[]? Capture()
    {
        try
        {
            _g.CopyFromScreen(_rect.Location, Drawing.Point.Empty, _rect.Size);
            // LockBits — в координатах самого битмапа (0,0,w,h), не экранных!
            var data = _bmp.LockBits(new Drawing.Rectangle(0, 0, Width, Height),
                Drawing.Imaging.ImageLockMode.ReadOnly,
                Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(data.Scan0, _buffer, 0, _stride * Height);
            }
            finally
            {
                _bmp.UnlockBits(data);
            }
            return _buffer;
        }
        catch (Exception ex)
        {
            Log.Warn($"LiveCapture: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _g.Dispose();
        _bmp.Dispose();
    }
}

/// <summary>Один переиспользуемый WriteableBitmap для всех кадров живого фона.</summary>
internal static class BackdropFrames
{
    private static WriteableBitmap? _wb;

    public static WriteableBitmap? Present(byte[] pixels, int width, int height)
    {
        try
        {
            if (_wb is null || _wb.PixelWidth != width || _wb.PixelHeight != height)
                _wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);

            _wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            return _wb;
        }
        catch (Exception ex)
        {
            Log.Warn($"BackdropFrames: {ex.Message}");
            return null;
        }
    }
}

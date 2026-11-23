using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Strofas.Core;
using Strofas.Models;
using WpfPath = System.Windows.Shapes.Path;

namespace Strofas.Menu;

/// <summary>Пункт меню, привязанный к стабильному id (индексный путь).</summary>
internal sealed record MenuItemVm(string Id, MenuItemConfig Config);

internal enum HitKind
{
    None,
    Center,
    Outer,
    Inner,
}

internal readonly struct MenuHit
{
    public HitKind Kind { get; }
    public int Index { get; }

    public MenuHit(HitKind kind, int index = -1)
    {
        Kind = kind;
        Index = index;
    }

    public static readonly MenuHit Miss = new(HitKind.None);
}

/// <summary>
/// Отрисовка кругового меню из «лепестков»: внешнее кольцо — корень,
/// внутреннее — дети выбранной категории, в центре — кнопка «назад/закрыть».
/// Геометрия считается в DIP-координатах окна.
/// </summary>
internal sealed class RadialMenuRenderer
{
    private const double StartDeg = -90; // первый пункт — сверху
    private const double GapDeg = 3.0;   // зазор между лепестками

    private static readonly Brush PetalBase = F(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly Brush PetalHot = F(0x3D, 0xFF, 0xFF, 0xFF);
    private static readonly Brush StrokeBase = F(0x38, 0xFF, 0xFF, 0xFF);
    private static readonly Brush StrokeHot = F(0xB3, 0xFF, 0xFF, 0xFF);
    private static readonly Brush TextBase = F(0xE8, 0xE8, 0xF0, 0xFF);
    private static readonly Brush TextHot = F(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Brush HintBrush = F(0x9A, 0x9A, 0xB0, 0xFF);
    private static readonly Brush CenterFill = F(0x26, 0xFF, 0xFF, 0xFF);
    private static readonly Brush CenterHot = F(0x4D, 0xFF, 0xFF, 0xFF);
    private static readonly Brush CenterStroke = F(0x59, 0xFF, 0xFF, 0xFF);
    private static readonly Brush RunningDot = F(0xE6, 0x46, 0xD3, 0x6B);

    private readonly Canvas _canvas;
    private readonly double _size;
    private readonly double _cx;
    private readonly double _cy;
    private readonly double _centerR;
    private readonly double _innerMin;
    private readonly double _innerMax;
    private readonly double _outerMin;
    private readonly double _outerMax;
    private readonly double _centerTextW;
    private readonly Func<MenuItemVm, bool> _isRunning;

    private List<MenuItemVm> _root = new();
    private List<MenuItemVm>? _inner;
    private readonly List<Petal> _rootPetals = new();
    private readonly List<Petal> _innerPetals = new();
    private Petal? _hovered;
    private bool _centerHot;

    private readonly Ellipse _centerRing;
    private readonly TextBlock _centerMain;
    private readonly TextBlock _centerHint;

    private sealed class Petal
    {
        public required MenuItemVm Item { get; init; }
        public required WpfPath Path { get; init; }
        public required ScaleTransform Scale { get; init; }
        public TextBlock? Label { get; set; }
        public TextBlock? Letter { get; set; }
        public Image? Icon { get; set; }
        public UIElement? Dot { get; set; }
    }

    public RadialMenuRenderer(Canvas canvas, double size, Func<MenuItemVm, bool> isRunning)
    {
        _canvas = canvas;
        _size = size;
        _cx = size / 2;
        _cy = size / 2;
        _isRunning = isRunning;

        _centerR = size * 0.097;
        _innerMin = size * 0.110;
        _innerMax = size * 0.270;
        _outerMin = size * 0.282;
        _outerMax = size * 0.478;
        _centerTextW = _centerR * 2 - 14;

        _centerRing = new Ellipse
        {
            Width = _centerR * 2,
            Height = _centerR * 2,
            Fill = CenterFill,
            Stroke = CenterStroke,
            StrokeThickness = 2,
        };
        Canvas.SetLeft(_centerRing, _cx - _centerR);
        Canvas.SetTop(_centerRing, _cy - _centerR);

        _centerMain = new TextBlock
        {
            FontSize = Math.Max(12, size * 0.023),
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBase,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = size * 0.075,
            Width = _centerTextW,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _centerHint = new TextBlock
        {
            FontSize = Math.Max(10, size * 0.017),
            Foreground = HintBrush,
            TextAlignment = TextAlignment.Center,
            Width = _centerTextW,
        };
    }

    private static Brush F(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---------- Построение ----------

    public void SetRoot(List<MenuItemVm> items)
    {
        _root = items;
        Rebuild();
    }

    public void SetInner(List<MenuItemVm>? items)
    {
        _inner = items;
        Rebuild();
    }

    public void Rebuild()
    {
        _canvas.Children.Clear();
        _rootPetals.Clear();
        _innerPetals.Clear();
        _hovered = null;
        _centerHot = false;

        BuildRing(_root, _rootPetals, _outerMin, _outerMax, animate: false);
        if (_inner is { Count: > 0 })
            BuildRing(_inner, _innerPetals, _innerMin, _innerMax, animate: true);

        // центр поверх колец
        _canvas.Children.Add(_centerRing);
        _canvas.Children.Add(_centerMain);
        _canvas.Children.Add(_centerHint);
        SetCenterHot(false);
    }

    private void BuildRing(List<MenuItemVm> items, List<Petal> petals, double rIn, double rOut, bool animate)
    {
        var count = items.Count;
        if (count == 0) return;

        var step = 360.0 / count;
        var rMid = (rIn + rOut) / 2;
        var chord = 2 * rMid * Math.Sin(step * Math.PI / 360);
        var maxLabelW = Math.Max(56.0, chord * 0.86);
        var iconSize = count > 9 ? 18.0 : count > 6 ? 22.0 : 26.0;
        var fontSize = count > 9 ? 11.0 : count > 6 ? 12.0 : 13.0;

        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            var a1 = StartDeg + i * step + GapDeg / 2;
            var a2 = StartDeg + (i + 1) * step - GapDeg / 2;
            var mid = (a1 + a2) / 2;
            var pos = Polar(rMid, mid);

            var scale = new ScaleTransform(1, 1, _cx, _cy);
            var path = new WpfPath
            {
                Data = BuildSector(a1, a2, rIn, rOut),
                Fill = PetalBase,
                Stroke = StrokeBase,
                StrokeThickness = 5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = scale,
            };
            _canvas.Children.Add(path);

            var petal = new Petal { Item = item, Path = path, Scale = scale };

            // иконка или буква
            var iconCy = pos.Y - iconSize * 0.9;
            var source = IconCache.Get(item.Config.Icon);
            if (source is not null)
            {
                var img = new Image
                {
                    Source = source,
                    Width = iconSize,
                    Height = iconSize,
                    Stretch = Stretch.Uniform,
                };
                img.RenderTransform = Grouped(pos.X - iconSize / 2, iconCy - iconSize / 2, scale);
                _canvas.Children.Add(img);
                petal.Icon = img;
            }
            else
            {
                var letter = new TextBlock
                {
                    Text = FirstLetter(item.Config.Label),
                    FontSize = iconSize + 4,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TextBase,
                };
                letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                letter.RenderTransform = Grouped(
                    pos.X - letter.DesiredSize.Width / 2,
                    iconCy - letter.DesiredSize.Height / 2,
                    scale);
                _canvas.Children.Add(letter);
                petal.Letter = letter;
            }

            // подпись
            var label = new TextBlock
            {
                Text = item.Config.Label,
                FontSize = fontSize,
                Foreground = TextBase,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = maxLabelW,
            };
            label.MaxHeight = _size * 0.058;
            label.Measure(new Size(maxLabelW, double.PositiveInfinity));
            label.RenderTransform = Grouped(
                pos.X - label.DesiredSize.Width / 2,
                pos.Y - label.DesiredSize.Height / 2 + iconSize * 1.15,
                scale);
            _canvas.Children.Add(label);
            petal.Label = label;

            // индикатор «запущено»
            if (_isRunning(item))
            {
                const double dot = 10;
                var dp = Polar(rOut - 13, mid);
                var ellipse = new Ellipse
                {
                    Width = dot,
                    Height = dot,
                    Fill = RunningDot,
                    Stroke = F(0xE0, 0xFF, 0xFF, 0xFF),
                    StrokeThickness = 1,
                };
                ellipse.RenderTransform = Grouped(dp.X - dot / 2, dp.Y - dot / 2, scale);
                _canvas.Children.Add(ellipse);
                petal.Dot = ellipse;
            }

            petals.Add(petal);
        }

        if (animate) AnimateIn(petals);
    }

    private static void AnimateIn(List<Petal> petals)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        foreach (var p in petals)
        {
            p.Path.Opacity = 0;
            p.Path.BeginAnimation(UIElement.OpacityProperty, anim);
            if (p.Label is not null)
            {
                p.Label.Opacity = 0;
                p.Label.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            if (p.Icon is not null)
            {
                p.Icon.Opacity = 0;
                p.Icon.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            if (p.Letter is not null)
            {
                p.Letter.Opacity = 0;
                p.Letter.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            if (p.Dot is not null)
            {
                p.Dot.Opacity = 0;
                p.Dot.BeginAnimation(UIElement.OpacityProperty, anim);
            }
        }
    }

    private TransformGroup Grouped(double tx, double ty, ScaleTransform scale)
    {
        var tg = new TransformGroup();
        tg.Children.Add(new TranslateTransform(tx, ty));
        tg.Children.Add(scale);
        return tg;
    }

    private Geometry BuildSector(double a1, double a2, double rIn, double rOut)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(rOut, a1), isFilled: true, isClosed: true);
            ctx.ArcTo(Polar(rOut, a2), new Size(rOut, rOut), 0, isLargeArc: false,
                SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(Polar(rIn, a2), true, false);
            ctx.ArcTo(Polar(rIn, a1), new Size(rIn, rIn), 0, isLargeArc: false,
                SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
        }
        g.Freeze();
        return g;
    }

    private Point Polar(double r, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(_cx + r * Math.Cos(rad), _cy + r * Math.Sin(rad));
    }

    private static string FirstLetter(string label)
    {
        var t = label.Trim();
        return t.Length == 0 ? "?" : char.ToUpperInvariant(t[0]).ToString();
    }

    // ---------- Попадания ----------

    public MenuHit HitTest(Point p)
    {
        var dx = p.X - _cx;
        var dy = p.Y - _cy;
        var r = Math.Sqrt(dx * dx + dy * dy);

        if (r <= _centerR) return new MenuHit(HitKind.Center);

        if (r >= _outerMin - 5 && r <= _outerMax + 5)
        {
            var i = IndexAt(p, _root.Count);
            return i >= 0 ? new MenuHit(HitKind.Outer, i) : MenuHit.Miss;
        }

        if (_inner is { Count: > 0 } && r >= _innerMin - 5 && r <= _innerMax + 5)
        {
            var i = IndexAt(p, _inner.Count);
            return i >= 0 ? new MenuHit(HitKind.Inner, i) : MenuHit.Miss;
        }

        return MenuHit.Miss;
    }

    private int IndexAt(Point p, int count)
    {
        if (count <= 0) return -1;
        var deg = Math.Atan2(p.Y - _cy, p.X - _cx) * 180.0 / Math.PI;
        var step = 360.0 / count;
        var rel = deg - StartDeg;
        rel = ((rel % 360) + 360) % 360;
        var idx = (int)(rel / step);
        if (idx < 0 || idx >= count) return -1;
        var within = rel - idx * step;
        if (within < GapDeg / 2 || within > step - GapDeg / 2) return -1;
        return idx;
    }

    // ---------- Подсветка ----------

    public void SetHover(in MenuHit hit)
    {
        Petal? hot = hit.Kind switch
        {
            HitKind.Outer when hit.Index < _rootPetals.Count => _rootPetals[hit.Index],
            HitKind.Inner when hit.Index < _innerPetals.Count => _innerPetals[hit.Index],
            _ => null,
        };

        if (!ReferenceEquals(_hovered, hot))
        {
            if (_hovered is not null) ApplyState(_hovered, false);
            _hovered = hot;
            if (_hovered is not null) ApplyState(_hovered, true);
        }

        SetCenterHot(hit.Kind == HitKind.Center);
    }

    private static void ApplyState(Petal petal, bool hot)
    {
        petal.Path.Fill = hot ? PetalHot : PetalBase;
        petal.Path.Stroke = hot ? StrokeHot : StrokeBase;
        petal.Scale.ScaleX = petal.Scale.ScaleY = hot ? 1.045 : 1.0;
        var fg = hot ? TextHot : TextBase;
        if (petal.Label is not null) petal.Label.Foreground = fg;
        if (petal.Letter is not null) petal.Letter.Foreground = fg;
    }

    private void SetCenterHot(bool hot)
    {
        if (_centerHot == hot) return;
        _centerHot = hot;
        _centerRing.Fill = hot ? CenterHot : CenterFill;
        _centerRing.Stroke = hot ? TextHot : CenterStroke;
        _centerMain.Foreground = hot ? TextHot : TextBase;
    }

    public void SetCenter(string main, string hint)
    {
        _centerMain.Text = main;
        _centerMain.Measure(new Size(_centerTextW, double.PositiveInfinity));
        Canvas.SetLeft(_centerMain, _cx - _centerTextW / 2);
        Canvas.SetTop(_centerMain, _cy - _size * 0.022 - _centerMain.DesiredSize.Height / 2);

        _centerHint.Text = hint;
        _centerHint.Measure(new Size(_centerTextW, double.PositiveInfinity));
        Canvas.SetLeft(_centerHint, _cx - _centerTextW / 2);
        Canvas.SetTop(_centerHint, _cy + _size * 0.035 - _centerHint.DesiredSize.Height / 2);
    }
}

/// <summary>Кэш иконок: exe/dll → обёртка иконки, png/ico → картинка.</summary>
internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static ImageSource? Get(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (!System.IO.Path.IsPathRooted(path))
        {
            var nextToExe = System.IO.Path.Combine(AppContext.BaseDirectory, path);
            var nextToConfig = System.IO.Path.Combine(ConfigService.Dir, path);
            path = System.IO.File.Exists(nextToExe) ? nextToExe
                : System.IO.File.Exists(nextToConfig) ? nextToConfig
                : path;
        }

        lock (Sync)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            var src = Load(path);
            Cache[path] = src;
            return src;
        }
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".exe" or ".dll")
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            Log.Warn($"Иконка '{path}': {ex.Message}");
            return null;
        }
    }
}

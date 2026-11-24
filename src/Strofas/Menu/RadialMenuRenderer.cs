using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    public PressZone Zone { get; }

    public MenuHit(HitKind kind, int index = -1, PressZone zone = PressZone.Launch)
    {
        Kind = kind;
        Index = index;
        Zone = zone;
    }

    public static readonly MenuHit Miss = new(HitKind.None);

    public bool SameAs(in MenuHit other) => Kind == other.Kind && Index == other.Index;
}

/// <summary>
/// Круговое меню: внешнее кольцо — корень, внутреннее — текущая категория
/// (nested to any depth via a category chain), the center holds the hub with breadcrumb.
/// Визуал: тёмный «виньетированный» диск поверх Acrylic-фона, стеклянные
/// petals, accent from the Windows theme, staggered entrance, spring hover,
/// wheel pagination for >MaxItems items, hit-test hysteresis at boundaries.
/// </summary>
internal sealed class RadialMenuRenderer
{
    private const double StartDeg = -90;
    private const double GapDeg = 8.0; // visual gap: narrower, more elongated petals
    private const double HysteresisDeg = 2.2;
    private const double HysteresisPx = 6.0;

    private readonly Canvas _canvas;
    private readonly double _size;
    private readonly double _cx;
    private readonly double _cy;
    private readonly double _centerR;
    private readonly double _innerMin;
    private readonly double _innerMax;
    private readonly double _outerMin;
    private readonly double _outerMax;

    // hit-границы шире визуальных: зазоры между лепестками кликабельны,
    // hit bounds are wider than the visual petals: gaps stay clickable,
    private readonly double _centerHitR;
    private readonly double _innerHitMin;
    private readonly double _innerHitMax;
    private readonly double _outerHitMin;
    private readonly double _outerHitMax;
    private readonly double _hubTextW;
    private readonly double _hubCrumbY;
    private readonly double _hubMainY;
    private readonly double _hubHintY;
    private readonly double _hubDotsY;
    private readonly Func<MenuItemVm, bool> _isRunning;
    private readonly Func<MenuItemVm, UpdateVisual>? _updateVisual;

    private List<MenuItemVm> _rootAll = new();
    private List<MenuItemVm>? _innerAll;
    private int _rootPage;
    private int _innerPage;

    private List<MenuItemVm> _rootView = new();
    private List<MenuItemVm>? _innerView;
    private readonly List<Petal> _rootPetals = new();
    private readonly List<Petal> _innerPetals = new();
    private Petal? _hovered;
    private bool _centerHot;
    private MenuHit _lastHit = MenuHit.Miss;
    private int _hoverRing; // 0 = корень, 1 = внутреннее

    private readonly Ellipse _hubRing;
    private readonly TextBlock _hubCrumb;
    private readonly TextBlock _hubMain;
    private readonly TextBlock _hubHint;
    private readonly List<Ellipse> _dots = new();

    // кисти — пересоздаются при смене акцента
    private Brush _petalFill = null!;
    private Brush _petalStroke = null!;
    private Brush _hoverFill = null!;
    private Brush _hoverStroke = null!;
    private Brush _text = null!;
    private Brush _textDim = null!;
    private Brush _hubFill = null!;
    private Brush _hubStroke = null!;
    private Brush _hubStrokeHot = null!;
    private Brush _runningDot = null!;
    private Brush _dotOn = null!;
    private Brush _dotOff = null!;

    // зоны обновления на внешней части лепестка
    private Brush _zoneFill = null!;
    private Brush _zoneHot = null!;
    private Brush _zoneStaged = null!;
    private Brush _zoneStagedHot = null!;
    private Brush _zoneError = null!;
    private Brush _zoneErrorHot = null!;

    /// <summary>Максимум пунктов на кольцо (страницы).</summary>
    public int MaxItems { get; set; }

    /// <summary>Снимок экрана под меню: рисуется под каждым лепестком с блюром.</summary>
    public ImageSource? BackdropSource { get; private set; }

    private readonly List<Image> _blurImages = new();

    /// <summary>Живое обновление фона (по таймеру) без перестройки колеса.</summary>
    public void UpdateBackdrop(ImageSource? source)
    {
        BackdropSource = source;
        foreach (var img in _blurImages)
            img.Source = source;
    }

    private sealed class Petal
    {
        public required MenuItemVm Item { get; init; }
        public required WpfPath Path { get; init; }

        /// <summary>Внешняя зона «Обновить» (null — лепесток цельный).</summary>
        public WpfPath? Path2 { get; init; }
        public double? SplitR { get; init; }
        public UpdateVisual Visual { get; init; }
        public TextBlock? ZoneGlyph { get; init; }

        public required ScaleTransform Hover { get; init; }
        public required ScaleTransform EnterScale { get; init; }
        public required RotateTransform EnterRotate { get; init; }
        public TextBlock? Label { get; set; }
        public TextBlock? Letter { get; set; }
        public TextBlock? Marker { get; set; }
        public Image? Icon { get; set; }
        public UIElement? Dot { get; set; }
        public UIElement? BlurLayer { get; set; }

        public IEnumerable<UIElement> Elements()
        {
            if (BlurLayer is not null) yield return BlurLayer;
            yield return Path;
            if (Path2 is not null) yield return Path2;
            if (Label is not null) yield return Label;
            if (Letter is not null) yield return Letter;
            if (Marker is not null) yield return Marker;
            if (Icon is not null) yield return Icon;
            if (Dot is not null) yield return Dot;
            if (ZoneGlyph is not null) yield return ZoneGlyph;
        }
    }

    public RadialMenuRenderer(Canvas canvas, double size, int maxItems,
        Func<MenuItemVm, bool> isRunning, Func<MenuItemVm, UpdateVisual>? updateVisual = null)
    {
        _canvas = canvas;
        _size = size;
        _cx = size / 2;
        _cy = size / 2;
        MaxItems = maxItems;
        _isRunning = isRunning;
        _updateVisual = updateVisual;

        _centerR = size * 0.085;
        _innerMin = size * 0.097;
        _innerMax = size * 0.266;
        _outerMin = size * 0.277;
        _outerMax = size * 0.463;

        _centerHitR = _centerR + size * 0.006;
        _innerHitMin = _centerHitR;
        _innerHitMax = (_innerMax + _outerMin) / 2;
        _outerHitMin = _innerHitMax;
        _outerHitMax = _outerMax + size * 0.009;

        _hubTextW = _centerR * 2 - 12;
        _hubCrumbY = _cy - size * 0.058;
        _hubMainY = _cy - size * 0.012;
        _hubHintY = _cy + size * 0.030;
        _hubDotsY = _cy + size * 0.054;

        _hubRing = new Ellipse
        {
            Width = _centerR * 2,
            Height = _centerR * 2,
            StrokeThickness = 1.5,
        };
        Canvas.SetLeft(_hubRing, _cx - _centerR);
        Canvas.SetTop(_hubRing, _cy - _centerR);

        _hubCrumb = new TextBlock
        {
            FontSize = Math.Max(9, size * 0.0155),
            Foreground = _textDim,
            TextAlignment = TextAlignment.Center,
            Width = _hubTextW,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = size * 0.024,
        };

        _hubMain = new TextBlock
        {
            FontSize = Math.Max(12, size * 0.0235),
            FontWeight = FontWeights.SemiBold,
            Foreground = _text,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = _hubTextW,
            MaxHeight = size * 0.072,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _hubHint = new TextBlock
        {
            FontSize = Math.Max(9, size * 0.0165),
            Foreground = _textDim,
            TextAlignment = TextAlignment.Center,
            Width = _hubTextW,
        };

        RebuildBrushes();
    }

    // ---------- Кисти ----------

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Brush Solid(byte a, byte r, byte g, byte b)
        => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    private void RebuildBrushes()
    {
        var acc = ThemeTokens.Accent;

        // dark glass over the blurred backdrop: tint keeps labels readable
        _petalFill = Freeze(new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x73, 0x0C, 0x0D, 0x14), 0),
                new GradientStop(Color.FromArgb(0x4D, 0x0C, 0x0D, 0x14), 1),
            },
        });
        _petalStroke = Solid(0x38, 0xFF, 0xFF, 0xFF);

        _hoverFill = Freeze(new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xB3, acc.R, acc.G, acc.B), 0),
                new GradientStop(Color.FromArgb(0x59, acc.R, acc.G, acc.B), 1),
            },
        });
        _hoverStroke = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, acc.R, acc.G, acc.B)));

        _zoneFill = Solid(0x66, acc.R, acc.G, acc.B);      // есть обновление
        _zoneHot = Solid(0xE6, acc.R, acc.G, acc.B);
        _zoneStaged = Solid(0x42, 0xFF, 0xFF, 0xFF);       // скачано, ждёт применения
        _zoneStagedHot = Solid(0x73, 0xFF, 0xFF, 0xFF);
        _zoneError = Solid(0x73, 0xE0, 0x52, 0x52);        // ошибка
        _zoneErrorHot = Solid(0xB3, 0xE0, 0x52, 0x52);

        _text = Solid(0xF2, 0xF2, 0xF4, 0xF8);
        _textDim = Solid(0xB3, 0x9A, 0xA0, 0xB4);

        _hubFill = Freeze(new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x59, acc.R, acc.G, acc.B), 0),
                new GradientStop(Color.FromArgb(0x33, 0x0D, 0x0E, 0x16), 0.75),
                new GradientStop(Color.FromArgb(0x66, 0x0D, 0x0E, 0x16), 1),
            },
        });
        _hubStroke = Solid(0x8C, acc.R, acc.G, acc.B);
        _hubStrokeHot = Solid(0xE0, acc.R, acc.G, acc.B);

        _runningDot = Solid(0xE6, 0x46, 0xD3, 0x6B);
        _dotOn = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, acc.R, acc.G, acc.B)));
        _dotOff = Solid(0x4D, 0xFF, 0xFF, 0xFF);

        _hubCrumb.Foreground = _textDim;
        _hubMain.Foreground = _text;
        _hubHint.Foreground = _textDim;
        _hubRing.Fill = _hubFill;
        _hubRing.Stroke = _hubStroke;
    }

    // ---------- Данные / страницы ----------

    private static List<MenuItemVm> Slice(List<MenuItemVm> all, int page, int max)
    {
        if (all.Count <= max) return all;
        var start = Math.Clamp(page, 0, Pages(all.Count, max) - 1) * max;
        var count = Math.Min(max, all.Count - start);
        return all.GetRange(start, count);
    }

    private static int Pages(int count, int max) => Math.Max(1, (count + max - 1) / max);

    private int RootPages => Pages(_rootAll.Count, MaxItems);
    private int InnerPages => _innerAll is null ? 1 : Pages(_innerAll.Count, MaxItems);

    public void SetRoot(List<MenuItemVm> items)
    {
        _rootAll = items;
        _rootPage = Math.Clamp(_rootPage, 0, RootPages - 1);
        Rebuild();
    }

    public void SetInner(List<MenuItemVm>? items, bool animate = true)
    {
        _innerAll = items;
        _innerPage = 0;
        _lastHit = MenuHit.Miss;
        Rebuild();
        if (items is { Count: > 0 } && animate) PlayInnerAnimation();
    }

    /// <summary>Листание страниц кольца под курсором. true — страница сменилась.</summary>
    public bool Wheel(int delta, Point p)
    {
        var dx = p.X - _cx;
        var dy = p.Y - _cy;
        var r = Math.Sqrt(dx * dx + dy * dy);
        var innerRing = _innerAll is { Count: > 0 } && r < _outerHitMin;

        var pages = innerRing ? InnerPages : RootPages;
        if (pages <= 1) return false;

        var step = delta > 0 ? -1 : 1;
        if (innerRing)
        {
            _innerPage = (_innerPage + step + pages) % pages;
        }
        else
        {
            _rootPage = (_rootPage + step + pages) % pages;
        }

        Rebuild();
        return true;
    }

    /// <summary>Перелистывает страницу так, чтобы глобальный пункт был виден.</summary>
    public void EnsureVisible(bool inner, int globalIndex)
    {
        if (inner)
        {
            if (_innerAll is null) return;
            var page = globalIndex / Math.Max(1, MaxItems);
            if (page != _innerPage && page < InnerPages)
            {
                _innerPage = page;
                Rebuild();
            }
        }
        else
        {
            var page = globalIndex / Math.Max(1, MaxItems);
            if (page != _rootPage && page < RootPages)
            {
                _rootPage = page;
                Rebuild();
            }
        }
    }

    // ---------- Построение ----------

    public void Rebuild()
    {
        _rootView = Slice(_rootAll, _rootPage, MaxItems);
        _innerView = _innerAll is null ? null : Slice(_innerAll, _innerPage, MaxItems);

        RebuildBrushes();
        _canvas.Children.Clear();
        _rootPetals.Clear();
        _innerPetals.Clear();
        _blurImages.Clear();
        _hovered = null;
        _centerHot = false;
        _dots.Clear();

        if (_rootView.Count > 0) BuildRing(_rootView, _rootPetals, _outerMin, _outerMax);
        if (_innerView is { Count: > 0 }) BuildRing(_innerView, _innerPetals, _innerMin, _innerMax);

        _canvas.Children.Add(_hubRing);
        _canvas.Children.Add(_hubCrumb);
        _canvas.Children.Add(_hubMain);
        _canvas.Children.Add(_hubHint);
        SetCenterHot(false);
        DrawHubDots(_hoverRing);
    }

    private void BuildRing(List<MenuItemVm> items, List<Petal> petals, double rIn, double rOut)
    {
        var count = items.Count;
        var step = 360.0 / count;
        var rMid = (rIn + rOut) / 2;
        var iconSize = count > 9 ? 18.0 : count > 6 ? 22.0 : 26.0;
        var fontSize = count > 9 ? 11.0 : count > 6 ? 12.0 : 13.0;

        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            var a1 = StartDeg + i * step + GapDeg / 2;
            var a2 = StartDeg + (i + 1) * step - GapDeg / 2;
            var mid = (a1 + a2) / 2;

            // github-пункт с обновлением: лепесток делится на две зоны —
            // внутренняя «Запустить», внешняя «Обновить»
            var visual = _updateVisual?.Invoke(item) ?? UpdateVisual.None;
            var splitR = visual is UpdateVisual.Available or UpdateVisual.Downloading
                or UpdateVisual.Staged or UpdateVisual.Error
                ? rIn + (rOut - rIn) * 0.6
                : (double?)null;
            var displayMid = splitR is double sr ? (rIn + sr) / 2 : rMid;
            var pos = Polar(displayMid, mid);
            var chord = 2 * displayMid * Math.Sin(step * Math.PI / 360);
            var maxLabelW = Math.Max(56.0, chord * 0.72);

            var hover = new ScaleTransform(1, 1, _cx, _cy);
            var enterScale = new ScaleTransform(1, 1, _cx, _cy);
            var enterRotate = new RotateTransform(0, _cx, _cy);

            // размытый фон под лепестком: снимок экрана, обрезанный геометрией сектора
            Grid? blurLayer = null;
            if (BackdropSource is not null)
            {
                blurLayer = new Grid
                {
                    Width = _size,
                    Height = _size,
                    Clip = BuildSector(a1, a2, rIn, rOut),
                };
                var blurImg = new Image
                {
                    Source = BackdropSource,
                    Width = _size,
                    Height = _size,
                    Stretch = Stretch.Fill,
                    Effect = new BlurEffect
                    {
                        Radius = 16,
                        KernelType = KernelType.Gaussian,
                    },
                };
                Canvas.SetLeft(blurImg, 0);
                Canvas.SetTop(blurImg, 0);
                blurLayer.Children.Add(blurImg);
                blurLayer.RenderTransform = ElementTransform(0, 0, enterScale, enterRotate, hover);
                _blurImages.Add(blurImg);
                // в canvas добавляется через petal.Elements() — порядок=z: blur под path
            }

            var path = new WpfPath
            {
                Data = BuildSector(a1, a2, rIn, splitR is double srIn ? srIn - 1.5 : rOut),
                Fill = _petalFill,
                Stroke = _petalStroke,
                StrokeThickness = 5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            path.RenderTransform = ElementTransform(0, 0, enterScale, enterRotate, hover);

            WpfPath? path2 = null;
            TextBlock? zoneGlyph = null;
            if (splitR is double sr2)
            {
                path2 = new WpfPath
                {
                    Data = BuildSector(a1, a2, sr2 + 1.5, rOut),
                    Fill = ZoneBrush(visual),
                    Stroke = _petalStroke,
                    StrokeThickness = 5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                path2.RenderTransform = ElementTransform(0, 0, enterScale, enterRotate, hover);

                zoneGlyph = new TextBlock
                {
                    Text = ZoneGlyphText(visual),
                    FontSize = fontSize + 3,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _textDim,
                };
                zoneGlyph.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var gPos = Polar((sr2 + rOut) / 2, mid);
                zoneGlyph.RenderTransform = ElementTransform(
                    gPos.X - zoneGlyph.DesiredSize.Width / 2,
                    gPos.Y - zoneGlyph.DesiredSize.Height / 2,
                    enterScale, enterRotate, hover);
            }

            var petal = new Petal
            {
                Item = item,
                Path = path,
                Path2 = path2,
                SplitR = splitR,
                Visual = visual,
                ZoneGlyph = zoneGlyph,
                Hover = hover,
                EnterScale = enterScale,
                EnterRotate = enterRotate,
                BlurLayer = blurLayer,
            };

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
                img.RenderTransform = ElementTransform(
                    pos.X - iconSize / 2, iconCy - iconSize / 2, enterScale, enterRotate, hover);
                petal.Icon = img;
            }
            else
            {
                var letter = new TextBlock
                {
                    Text = FirstLetter(item.Config.Label),
                    FontSize = iconSize + 4,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _text,
                };
                letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                letter.RenderTransform = ElementTransform(
                    pos.X - letter.DesiredSize.Width / 2,
                    iconCy - letter.DesiredSize.Height / 2,
                    enterScale, enterRotate, hover);
                petal.Letter = letter;
            }

            // подпись
            var label = new TextBlock
            {
                Text = item.Config.Label,
                FontSize = fontSize,
                Foreground = _text,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = maxLabelW,
            };
            label.MaxHeight = _size * 0.058;
            label.Measure(new Size(maxLabelW, double.PositiveInfinity));
            label.RenderTransform = ElementTransform(
                pos.X - label.DesiredSize.Width / 2,
                pos.Y - label.DesiredSize.Height / 2 + iconSize * 1.15,
                enterScale, enterRotate, hover);
            petal.Label = label;

            // category marker '>' at the inner edge of the petal
            if (item.Config.IsCategory)
            {
                var markerPos = Polar(rIn + _size * 0.024, mid);
                var marker = new TextBlock
                {
                    Text = "›",
                    FontSize = fontSize + 4,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _textDim,
                };
                marker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                marker.RenderTransform = ElementTransform(
                    markerPos.X - marker.DesiredSize.Width / 2,
                    markerPos.Y - marker.DesiredSize.Height / 2,
                    enterScale, enterRotate, hover);
                petal.Marker = marker;
            }

            // 'running' indicator
            if (_isRunning(item))
            {
                const double dot = 10;
                // при разделённом лепестке точка уходит во внутреннюю зону, чтобы не спорить с глифом
                var dp = Polar(splitR is not null ? rIn + 14 : rOut - 13, mid);
                var ellipse = new System.Windows.Shapes.Ellipse
                {
                    Width = dot,
                    Height = dot,
                    Fill = _runningDot,
                    Stroke = Solid(0xE0, 0xFF, 0xFF, 0xFF),
                    StrokeThickness = 1,
                };
                ellipse.RenderTransform = ElementTransform(
                    dp.X - dot / 2, dp.Y - dot / 2, enterScale, enterRotate, hover);
                petal.Dot = ellipse;
            }

            foreach (var el in petal.Elements())
                _canvas.Children.Add(el);

            petals.Add(petal);
        }
    }

    private TransformGroup ElementTransform(double tx, double ty,
        ScaleTransform enterScale, RotateTransform enterRotate, ScaleTransform hover)
    {
        var enter = new TransformGroup();
        enter.Children.Add(enterScale);
        enter.Children.Add(enterRotate);

        var tg = new TransformGroup();
        tg.Children.Add(new TranslateTransform(tx, ty));
        tg.Children.Add(enter);
        tg.Children.Add(hover);
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

    // ---------- Hit testing (with hysteresis) ----------

    public MenuHit HitTest(Point p)
    {
        // strict pass: angular gaps are clickable (gap=0), radius uses hit bounds
        var strict = HitTestCore(p, 0, 0);
        if (strict.Kind != HitKind.None)
        {
            _lastHit = strict;
            return strict;
        }

        // удержание последнего сектора в зазоре/на границе — против мерцания
        if (_lastHit.Kind != HitKind.None)
        {
            var loose = HitTestCore(p, HysteresisDeg, HysteresisPx);
            if (loose.SameAs(_lastHit)) return _lastHit;
        }

        _lastHit = MenuHit.Miss;
        return MenuHit.Miss;
    }

    private MenuHit HitTestCore(Point p, double gapDeg, double slack)
    {
        var dx = p.X - _cx;
        var dy = p.Y - _cy;
        var r = Math.Sqrt(dx * dx + dy * dy);

        if (r <= _centerHitR + slack) return new MenuHit(HitKind.Center);

        if (r >= _outerHitMin - slack && r <= _outerHitMax + slack && _rootView.Count > 0)
        {
            var i = IndexAt(p, _rootView.Count, gapDeg);
            if (i >= 0)
                return new MenuHit(HitKind.Outer, _rootPage * MaxItems + i, ZoneOf(_rootPetals, i, r));
            if (slack == 0) return MenuHit.Miss;
        }

        if (_innerView is { Count: > 0 } && r >= _innerHitMin - slack && r <= _innerHitMax + slack)
        {
            var i = IndexAt(p, _innerView.Count, gapDeg);
            if (i >= 0)
                return new MenuHit(HitKind.Inner, _innerPage * MaxItems + i, ZoneOf(_innerPetals, i, r));
            if (slack == 0) return MenuHit.Miss;
        }

        return MenuHit.Miss;
    }

    /// <summary>Какая зона лепестка под точкой: внутренняя (запуск) или внешняя (обновление).</summary>
    private static PressZone ZoneOf(List<Petal> petals, int localIndex, double r)
    {
        if (localIndex < 0 || localIndex >= petals.Count) return PressZone.Launch;
        var sr = petals[localIndex].SplitR;
        return sr is double s && r >= s ? PressZone.Update : PressZone.Launch;
    }

    private int IndexAt(Point p, int count, double gapDeg)
    {
        if (count <= 0) return -1;
        var deg = Math.Atan2(p.Y - _cy, p.X - _cx) * 180.0 / Math.PI;
        var step = 360.0 / count;
        var rel = deg - StartDeg;
        rel = ((rel % 360) + 360) % 360;
        var idx = (int)(rel / step);
        if (idx < 0 || idx >= count) return -1;
        var within = rel - idx * step;
        if (within < gapDeg / 2 || within > step - gapDeg / 2) return -1;
        return idx;
    }

    // ---------- Подсветка ----------

    public void SetHover(in MenuHit hit)
    {
        Petal? hot = hit.Kind switch
        {
            HitKind.Outer => PetalByGlobal(_rootPetals, _rootPage, hit.Index),
            HitKind.Inner => PetalByGlobal(_innerPetals, _innerPage, hit.Index),
            _ => null,
        };

        var ring = hit.Kind == HitKind.Inner ? 1 : 0;
        if (ring != _hoverRing)
        {
            _hoverRing = ring;
            DrawHubDots(ring);
        }

        if (!ReferenceEquals(_hovered, hot))
        {
            if (_hovered is not null) AnimatePetal(_hovered, false, _hoverZone);
            _hovered = hot;
            _hoverZone = hit.Zone;
            if (_hovered is not null) AnimatePetal(_hovered, true, _hoverZone);
        }
        else if (_hovered is not null && _hoverZone != hit.Zone)
        {
            // тот же лепесток, другая зона — перекрасить без анимации масштаба
            _hoverZone = hit.Zone;
            AnimatePetal(_hovered, true, _hoverZone);
        }

        SetCenterHot(hit.Kind == HitKind.Center);
    }

    private PressZone _hoverZone;

    private Petal? PetalByGlobal(List<Petal> petals, int page, int globalIndex)
    {
        var local = globalIndex - page * MaxItems;
        return local >= 0 && local < petals.Count ? petals[local] : null;
    }

    private void AnimatePetal(Petal petal, bool on, PressZone zone)
    {
        var ease = on
            ? new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut }
            : (IEasingFunction)new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(on ? 150 : 120));
        var target = on ? 1.045 : 1.0;

        petal.Hover.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, dur) { EasingFunction = ease });
        petal.Hover.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, dur) { EasingFunction = ease });

        var launchHot = on && zone == PressZone.Launch;
        petal.Path.Fill = launchHot ? _hoverFill : _petalFill;
        petal.Path.Stroke = launchHot ? _hoverStroke : _petalStroke;

        if (petal.Path2 is not null)
        {
            var zoneHot = on && zone == PressZone.Update;
            petal.Path2.Fill = zoneHot ? ZoneBrushHot(petal.Visual) : ZoneBrush(petal.Visual);
            petal.Path2.Stroke = zoneHot ? _hoverStroke : _petalStroke;
            if (petal.ZoneGlyph is not null)
                petal.ZoneGlyph.Foreground = zoneHot ? Brushes.White : _textDim;
        }

        var fg = on ? Brushes.White : _text;
        if (petal.Label is not null) petal.Label.Foreground = fg;
        if (petal.Letter is not null) petal.Letter.Foreground = fg;
        if (petal.Marker is not null) petal.Marker.Foreground = on ? Brushes.White : _textDim;
    }

    private Brush ZoneBrush(UpdateVisual v) => v switch
    {
        UpdateVisual.Staged => _zoneStaged,
        UpdateVisual.Error => _zoneError,
        _ => _zoneFill,
    };

    private Brush ZoneBrushHot(UpdateVisual v) => v switch
    {
        UpdateVisual.Staged => _zoneStagedHot,
        UpdateVisual.Error => _zoneErrorHot,
        _ => _zoneHot,
    };

    private static string ZoneGlyphText(UpdateVisual v) => v switch
    {
        UpdateVisual.Downloading => "↻",
        UpdateVisual.Staged => "✓",
        UpdateVisual.Error => "!",
        _ => "↑",
    };

    private void SetCenterHot(bool hot)
    {
        if (_centerHot == hot) return;
        _centerHot = hot;
        _hubRing.StrokeThickness = hot ? 2.5 : 1.5;
        _hubRing.Stroke = hot ? _hubStrokeHot : _hubStroke;
    }

    // ---------- Hub ----------

    public void SetCenter(string main, string hint, string? breadcrumb)
    {
        SetText(_hubCrumb, breadcrumb);
        SetText(_hubMain, main);
        SetText(_hubHint, hint);
        LayoutHub();
    }

    private static void SetText(TextBlock tb, string? text)
    {
        text ??= "";
        if (tb.Text == text) return;
        var animate = tb.Text.Length == 0 || text.Length == 0 || tb.Text[0] != text[0];
        tb.Text = text;
        if (animate)
            tb.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(70)))
                {
                    From = 0,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
    }

    private void LayoutHub()
    {
        _hubCrumb.Visibility = string.IsNullOrEmpty(_hubCrumb.Text)
            ? Visibility.Collapsed : Visibility.Visible;

        _hubCrumb.Measure(new Size(_hubTextW, double.PositiveInfinity));
        Canvas.SetLeft(_hubCrumb, _cx - _hubTextW / 2);
        Canvas.SetTop(_hubCrumb, _hubCrumbY - _hubCrumb.DesiredSize.Height / 2);

        _hubMain.Measure(new Size(_hubTextW, double.PositiveInfinity));
        Canvas.SetLeft(_hubMain, _cx - _hubTextW / 2);
        Canvas.SetTop(_hubMain, _hubMainY - _hubMain.DesiredSize.Height / 2);

        _hubHint.Measure(new Size(_hubTextW, double.PositiveInfinity));
        Canvas.SetLeft(_hubHint, _cx - _hubTextW / 2);
        Canvas.SetTop(_hubHint, _hubHintY - _hubHint.DesiredSize.Height / 2);
    }

    private void DrawHubDots(int ring)
    {
        foreach (var d in _dots) _canvas.Children.Remove(d);
        _dots.Clear();

        var pages = ring == 1 ? InnerPages : RootPages;
        if (pages <= 1) return;

        var page = ring == 1 ? _innerPage : _rootPage;
        var spacing = 9.0;
        var startX = _cx - (pages - 1) * spacing / 2;

        for (var i = 0; i < pages; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = i == page ? _dotOn : _dotOff,
            };
            Canvas.SetLeft(dot, startX + i * spacing - 2.5);
            Canvas.SetTop(dot, _hubDotsY - 2.5);
            _dots.Add(dot);
            _canvas.Children.Add(dot);
        }
    }

    // ---------- Анимации ----------

    public void PlayOpenAnimation()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < _rootPetals.Count; i++)
            AnimatePetalIn(_rootPetals[i], i * 12, -6, 0.86, ease);

        FadeHubIn();
    }

    private void PlayInnerAnimation()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var step = _innerView is { Count: > 0 } ? 360.0 / _innerView.Count : 90;
        var startAngle = Math.Min(step * 0.4, 30);
        for (var i = 0; i < _innerPetals.Count; i++)
            AnimatePetalIn(_innerPetals[i], i * 10, startAngle, 0.9, ease);
    }

    private static void AnimatePetalIn(Petal petal, int delayMs, double fromAngle,
        double fromScale, IEasingFunction ease)
    {
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var dur = new Duration(TimeSpan.FromMilliseconds(170));

        petal.EnterScale.ScaleX = petal.EnterScale.ScaleY = fromScale;
        petal.EnterScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, dur) { EasingFunction = ease, BeginTime = begin });
        petal.EnterScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, dur) { EasingFunction = ease, BeginTime = begin });

        petal.EnterRotate.Angle = fromAngle;
        petal.EnterRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, dur) { EasingFunction = ease, BeginTime = begin });

        foreach (var el in petal.Elements())
        {
            el.Opacity = 0;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, dur) { EasingFunction = ease, BeginTime = begin });
        }
    }

    private void FadeHubIn()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            From = 0,
            EasingFunction = ease,
        };
        _hubRing.BeginAnimation(UIElement.OpacityProperty, anim);
        foreach (var tb in new[] { _hubCrumb, _hubMain, _hubHint })
            tb.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(260)))
                {
                    From = 0,
                    BeginTime = TimeSpan.FromMilliseconds(80),
                    EasingFunction = ease,
                });
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

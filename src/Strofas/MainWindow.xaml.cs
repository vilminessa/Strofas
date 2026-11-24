using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Strofas.Core;
using Strofas.Menu;
using Strofas.Models;
using WinForms = System.Windows.Forms;

namespace Strofas;

/// <summary>
/// Оверлей кругового меню: Acrylic-диск Win11, появляется по горячке под курсором.
/// Вложенность любой глубины через цепочку категорий; breadcrumbs в хабе.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    private readonly ActionExecutor _executor;
    private readonly GithubService _github;
    private MenuConfig _config;
    private RadialMenuRenderer _renderer;
    private double _lastSize;
    private readonly List<MenuItemVm> _rootItems = new();

    /// <summary>Цепочка категорий от корня до текущего уровня.</summary>
    private readonly List<MenuItemVm> _chain = new();

    /// <summary>Клавиатурный выбор: индекс в активном списке (-1 — нет).</summary>
    private int _selection = -1;
    private bool _selectionInner;

    private bool _closing;
    private Native.POINT _cursorTarget;
    private double _physSize;
    private IntPtr _hwnd;
    private readonly DispatcherTimer _uiTimer;

    /// <summary>Живой фон: перехватываем экран под меню ~50 раз/сек.</summary>
    private void StartBackdropLoop()
    {
        _backdropCts?.Cancel();
        _backdropCts = new CancellationTokenSource();
        var token = _backdropCts.Token;
        _backdropFrames = 0;
        _backdropSw = System.Diagnostics.Stopwatch.StartNew();

        var rect = new Rect(
            _cursorTarget.X - _physSize / 2,
            _cursorTarget.Y - _physSize / 2,
            _physSize, _physSize);

        _ = Task.Run(async () =>
        {
            LiveCapture? cap = null;
            try
            {
                cap = new LiveCapture(rect);
                while (!token.IsCancellationRequested)
                {
                    var tick = System.Diagnostics.Stopwatch.StartNew();
                    var pixels = cap.Capture();
                    if (pixels is null) break;

                    // WritePixels + смена Source — на UI-потоке, до следующего захвата
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsVisible || token.IsCancellationRequested) return;
                        var wb = BackdropFrames.Present(pixels, cap.Width, cap.Height);
                        if (wb is not null)
                        {
                            _renderer.UpdateBackdrop(wb);
                            _backdropFrames++;
                        }
                    });

                    var wait = BackdropIntervalMs - (int)tick.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay(wait, token);
                }
            }
            catch (OperationCanceledException)
            {
                // штатная остановка
            }
            catch (Exception ex)
            {
                Log.Error("Фоновый цикл фона меню", ex);
            }
            finally
            {
                cap?.Dispose();
            }
        }, token);
    }

    private const int BackdropIntervalMs = 20; // ~50 fps живого фона

    private void StopBackdropLoop()
    {
        _backdropCts?.Cancel();
        _backdropCts = null;
        if (_backdropSw is { IsRunning: true })
        {
            _backdropSw.Stop();
            if (_backdropFrames > 0)
            {
                var fps = _backdropFrames * 1000.0 / Math.Max(1, _backdropSw.ElapsedMilliseconds);
                Log.Info($"живой фон: {_backdropFrames} кадров за {_backdropSw.ElapsedMilliseconds} мс ({fps:F0} fps)");
            }
            _backdropSw = null;
        }
    }

    private int _backdropFrames;
    private System.Diagnostics.Stopwatch? _backdropSw;
    private CancellationTokenSource? _backdropCts;

    internal MainWindow(MenuConfig cfg, ActionExecutor executor, GithubService github)
    {
        InitializeComponent();
        _config = cfg;
        _executor = executor;
        _github = github;
        Width = Height = cfg.MenuSize;
        _lastSize = cfg.MenuSize;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _hwnd = hwnd;
        // меню исключается из screen-capture: живой блюр-захват не ловит сами кнопки
        // (STROFAS_NO_EXCLUDE=1 — временно для скриншотов при отладке)
        if (Environment.GetEnvironmentVariable("STROFAS_NO_EXCLUDE") != "1")
            Dwm.ExcludeFromCapture(_hwnd);
        _renderer = new RadialMenuRenderer(MenuCanvas, cfg.MenuSize, cfg.MaxItemsPerRing, IsRunning,
            vm => _github.GetVisual(vm.Config));

        LoadRootItems();

        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftClick;
        MouseRightButtonUp += OnRightClick;
        PreviewKeyDown += OnKeyDown;
        MouseWheel += OnMouseWheel;
        Deactivated += (_, _) => HideMenu();

        ThemeTokens.Changed += OnAccentChanged;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        _github.StateChanged += OnGithubStateChanged;
        _github.Finished += OnGithubFinished;

        // опрос процентов скачивания — хаб показывает прогресс живьём
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _uiTimer.Tick += (_, _) => { if (IsVisible) UpdateFromPointer(); };
        _uiTimer.Start();
    }

    private void OnGithubStateChanged()
    {
        if (!IsVisible) return;
        _renderer.Rebuild();
        UpdateFromPointer();
    }

    private void OnGithubFinished(string id, bool ok, string message)
    {
        Log.Info($"github[{id}]: {(ok ? "ok" : "ошибка")} {message}");
        if (!IsVisible) return;
        if (ok && _config.CloseAfterAction)
        {
            HideMenu();
            return;
        }
        _renderer.Rebuild();
        UpdateFromPointer();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DWMCOLORIZATIONCOLORCHANGED && ThemeTokens.FollowTheme)
            ThemeTokens.LoadFromTheme();
        return IntPtr.Zero;
    }

    private void OnAccentChanged()
    {
        _renderer.Rebuild();
        if (IsVisible) UpdateFromPointer();
    }

    private bool IsRunning(MenuItemVm item) => _executor.IsRunning(item.Id, item.Config);

    private void LoadRootItems()
    {
        _rootItems.Clear();
        for (var i = 0; i < _config.Items.Count; i++)
            _rootItems.Add(new MenuItemVm(i.ToString(), _config.Items[i]));
    }

    /// <summary>Применение нового конфига из редактора.</summary>
    public void ApplyConfig(MenuConfig cfg)
    {
        HideMenu();
        _config = cfg;
        Width = Height = cfg.MenuSize;

        if (Math.Abs(cfg.MenuSize - _lastSize) > 0.5)
        {
            _lastSize = cfg.MenuSize;
            _renderer = new RadialMenuRenderer(MenuCanvas, cfg.MenuSize, cfg.MaxItemsPerRing, IsRunning,
                vm => _github.GetVisual(vm.Config));
        }
        else
        {
            _renderer.MaxItems = cfg.MaxItemsPerRing;
        }

        LoadRootItems();
    }

    // ---------- Показ / скрытие ----------

    public void ShowMenu()
    {
        if (_closing)
        {
            StopWindowAnimations();
            _closing = false;
        }

        LoadRootItems();
        _chain.Clear();
        _selection = -1;
        _renderer.MaxItems = _config.MaxItemsPerRing;

        PositionAtCursor();
        _moveLog = 20;

        // снимок экрана под меню — размытый фон под каждым лепестком;
        // дальше фон обновляется живьём по таймеру (StartBackdropLoop)
        _renderer.UpdateBackdrop(ScreenCapture.Capture(new Rect(
            _cursorTarget.X - _physSize / 2,
            _cursorTarget.Y - _physSize / 2,
            _physSize, _physSize)));
        _renderer.SetRoot(_rootItems);
        _renderer.SetInner(null, animate: false);

        Show();
        Activate();
        Native.SetCursorPos(_cursorTarget.X, _cursorTarget.Y);
        StartBackdropLoop();

        Log.Info("Меню показано");

        AnimateOpen();
        _renderer.PlayOpenAnimation();
        UpdateFromPointer();

        // фоновые проверки github-обновлений (без квоты API) + применение staged
        _github.OnMenuOpening(_config.Items);
    }

    public void HideMenu()
    {
        if (!IsVisible || _closing) return;
        _closing = true;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(90));

        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            Hide();
            StopBackdropLoop();
            StopWindowAnimations();
            _closing = false;
            Log.Info("Меню скрыто");
        };

        Root.BeginAnimation(UIElement.OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, dur) { EasingFunction = ease });
        RootRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(3, dur) { EasingFunction = ease });
    }

    private void StopWindowAnimations()
    {
        Root.BeginAnimation(UIElement.OpacityProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        Root.Opacity = 1;
        RootScale.ScaleX = RootScale.ScaleY = 1;
        RootRotate.Angle = 0;
    }

    private void AnimateOpen()
    {
        StopWindowAnimations();
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(160));

        RootScale.ScaleX = RootScale.ScaleY = 0.85;
        RootRotate.Angle = -6;
        Root.Opacity = 0;

        Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        RootRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
    }

    private void PositionAtCursor()
    {
        Native.GetCursorPos(out var pt);
        var center = pt;
        uint dpi = 96;
        double phys = 0;

        for (var i = 0; i < 2; i++)
        {
            dpi = SafeDpi(center);
            phys = _config.MenuSize * dpi / 96.0;
            var wa = WinForms.Screen.FromPoint(new System.Drawing.Point(center.X, center.Y)).WorkingArea;
            center.X = ClampAxis(center.X, wa.Left, wa.Right, phys);
            center.Y = ClampAxis(center.Y, wa.Top, wa.Bottom, phys);
        }

        Left = (center.X - phys / 2) * 96.0 / dpi;
        Top = (center.Y - phys / 2) * 96.0 / dpi;
        _cursorTarget = center;
        _physSize = phys;
    }

    private static int ClampAxis(int value, int min, int max, double physSize)
    {
        var half = (int)(physSize / 2);
        if (max - min <= physSize) return min + (max - min) / 2;
        return Math.Clamp(value, min + half, max - half);
    }

    private static uint SafeDpi(Native.POINT pt)
    {
        try
        {
            var d = Native.GetDpiForPoint(pt);
            if (d is > 0 and <= 960) return d;
        }
        catch
        {
            // старая Windows — используем 96
        }
        return 96;
    }

    // ---------- Наведение / активация ----------

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsVisible) return;
        var pos = e.GetPosition(this);
        var hit = _renderer.HitTest(pos);
        if (_moveLog > 0)
        {
            _moveLog--;
            Log.Info($"move pos=({pos.X:F0},{pos.Y:F0}) hit={hit.Kind} idx={hit.Index}");
        }
        if (_selection >= 0) _selection = -1; // мышь забирает управление
        ApplyHit(hit);
    }

    private int _moveLog;

    /// <summary>
    /// Наведение на корневой пункт переключает внутреннее кольцо — но только
    /// на глубине ≤1. Глубокая цепочка меняется только кликом (путь не теряется).
    /// </summary>
    private void ApplyHit(in MenuHit hit)
    {
        var chainChanged = false;

        if (hit.Kind == HitKind.Outer && hit.Index < _rootItems.Count)
        {
            var item = _rootItems[hit.Index];
            if (_chain.Count <= 1)
            {
                if (item.Config.IsCategory)
                {
                    if (_chain.Count != 1 || !ReferenceEquals(_chain[0], item))
                    {
                        _chain.Clear();
                        _chain.Add(item);
                        chainChanged = true;
                    }
                }
                else if (_chain.Count > 0)
                {
                    _chain.Clear();
                    chainChanged = true;
                }
            }
        }

        if (chainChanged)
        {
            if (_selectionInner) _selection = -1;
            _renderer.SetInner(InnerItems());
        }

        _renderer.SetHover(hit);
        _renderer.SetCenter(CenterMain(hit), CenterHint(), CrumbFor(hit));
    }

    private string CenterMain(in MenuHit hit)
    {
        if (hit.Zone == PressZone.Update) return "Обновить";
        var item = HitItem(hit);
        return item is null ? "Strofas" : item.Config.Label;
    }

    /// <summary>Статус github-пункта в верхней строке хаба, иначе — breadcrumb пути.</summary>
    private string? CrumbFor(in MenuHit hit)
    {
        var status = _github.GetStatusText(HitItem(hit)?.Config, hit.Zone);
        return status ?? Breadcrumb();
    }

    private string CenterHint() => _chain.Count > 0 ? "Назад" : "Закрыть";

    private string? Breadcrumb()
    {
        if (_chain.Count == 0) return null;
        if (_chain.Count == 1) return _chain[0].Config.Label;
        var s = string.Join(" › ", _chain.Select(c => c.Config.Label));
        return s.Length > 36 ? $"{_chain[0].Config.Label} … {_chain[^1].Config.Label}" : s;
    }

    private MenuItemVm? HitItem(in MenuHit hit) => hit.Kind switch
    {
        HitKind.Outer when hit.Index < _rootItems.Count => _rootItems[hit.Index],
        HitKind.Inner when InnerItems() is { } inner && hit.Index < inner.Count => inner[hit.Index],
        _ => null,
    };

    private List<MenuItemVm>? InnerItems()
    {
        if (_chain.Count == 0) return null;
        var last = _chain[^1];
        var children = last.Config.Children;
        if (children is not { Count: > 0 }) return null;
        var list = new List<MenuItemVm>(children.Count);
        for (var i = 0; i < children.Count; i++)
            list.Add(new MenuItemVm($"{last.Id}/{i}", children[i]));
        return list;
    }

    private void UpdateFromPointer()
    {
        if (!IsVisible) return;
        ApplyHit(_renderer.HitTest(Mouse.GetPosition(this)));
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsVisible) return;
        ActivateHit(_renderer.HitTest(e.GetPosition(this)));
        e.Handled = true;
    }

    private void ActivateHit(in MenuHit hit)
    {
        switch (hit.Kind)
        {
            case HitKind.Center:
                if (_chain.Count > 0) GoBack();
                else HideMenu();
                break;

            case HitKind.Outer when hit.Index < _rootItems.Count:
            {
                var item = _rootItems[hit.Index];
                if (item.Config.IsCategory) JumpToCategory(item);
                else Execute(item, hit.Zone);
                break;
            }

            case HitKind.Inner:
            {
                var inner = InnerItems();
                if (inner is null || hit.Index >= inner.Count) break;
                var item = inner[hit.Index];
                if (item.Config.IsCategory) EnterCategory(item);
                else Execute(item, hit.Zone);
                break;
            }

            default:
                HideMenu(); // клик по пустоте между лепестками
                break;
        }
    }

    /// <summary>Клик по корневой категории — прыжок в неё (глубокий путь сбрасывается осознанно).</summary>
    private void JumpToCategory(MenuItemVm item)
    {
        if (_chain.Count == 1 && ReferenceEquals(_chain[0], item))
        {
            UpdateFromPointer();
            return;
        }

        _chain.Clear();
        _chain.Add(item);
        _selection = -1;
        _renderer.SetInner(InnerItems());
        UpdateFromPointer();
    }

    /// <summary>Клик по категории во внутреннем кольце — погружение глубже.</summary>
    private void EnterCategory(MenuItemVm item)
    {
        if (_chain.Count > 0 && ReferenceEquals(_chain[^1], item))
        {
            UpdateFromPointer();
            return;
        }

        _chain.Add(item);
        _selection = -1;
        _renderer.SetInner(InnerItems());
        UpdateFromPointer();
    }

    private void GoBack()
    {
        if (_chain.Count == 0) return;
        _chain.RemoveAt(_chain.Count - 1);
        _selection = -1;
        _renderer.SetInner(InnerItems());
        UpdateFromPointer();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsVisible) return;
        if (_chain.Count > 0) GoBack();
        else HideMenu();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsVisible) return;
        if (_renderer.Wheel(e.Delta, e.GetPosition(this)))
            UpdateFromPointer();
        e.Handled = true;
    }

    // ---------- Клавиатура ----------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        switch (e.Key)
        {
            case Key.Escape:
                HideMenu();
                e.Handled = true;
                break;

            case Key.Back:
                if (_chain.Count > 0)
                {
                    GoBack();
                    e.Handled = true;
                }
                break;

            case Key.Left or Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Right or Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;

            case Key.Enter or Key.Space:
                ActivateKeyboard();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int step)
    {
        bool inner;
        List<MenuItemVm>? list;

        if (_selection >= 0)
        {
            inner = _selectionInner;
            list = inner ? InnerItems() : _rootItems;
            if (list is null || list.Count == 0)
            {
                inner = !inner;
                list = inner ? InnerItems() : _rootItems;
            }
        }
        else
        {
            inner = _chain.Count > 0;
            list = inner ? InnerItems() : _rootItems;
            if (list is null || list.Count == 0)
            {
                inner = !inner;
                list = inner ? InnerItems() : _rootItems;
            }
        }

        if (list is null || list.Count == 0) return;

        int idx;
        if (_selection < 0 || _selectionInner != inner)
            idx = step > 0 ? 0 : list.Count - 1;
        else
            idx = (_selection + step + list.Count) % list.Count;

        _selection = idx;
        _selectionInner = inner;
        _renderer.EnsureVisible(inner, idx);
        ApplyHit(new MenuHit(inner ? HitKind.Inner : HitKind.Outer, idx));
    }

    private void ActivateKeyboard()
    {
        if (_selection >= 0)
        {
            var hit = new MenuHit(_selectionInner ? HitKind.Inner : HitKind.Outer, _selection);
            ActivateHit(hit);
        }
        else if (_chain.Count > 0)
        {
            GoBack();
        }
        else
        {
            HideMenu();
        }
    }

    // ---------- Выполнение ----------

    private void Execute(MenuItemVm item, PressZone zone)
    {
        if (item.Config.Action is null)
        {
            Log.Warn($"Пункт '{item.Config.Label}' без действия");
            return;
        }

        _executor.Execute(item.Id, item.Config, zone);
        _selection = -1;

        // github-пункты закрывают меню по Finished (установка/обновление асинхронны)
        if (item.Config.Action.IsGithub) return;

        if (_config.CloseAfterAction)
        {
            HideMenu();
        }
        else
        {
            _renderer.Rebuild();
            UpdateFromPointer();
        }
    }
}

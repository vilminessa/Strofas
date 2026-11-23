using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Strofas.Core;
using Strofas.Menu;
using Strofas.Models;
using WinForms = System.Windows.Forms;

namespace Strofas;

/// <summary>Оверлей кругового меню: появляется по горячке в позиции курсора.</summary>
public sealed partial class MainWindow : Window
{
    private readonly ActionExecutor _executor;
    private MenuConfig _config;
    private RadialMenuRenderer _renderer;
    private readonly List<MenuItemVm> _rootItems = new();

    /// <summary>Цепочка категорий, открытых кликом (для вложенных подменю).</summary>
    private readonly List<MenuItemVm> _chain = new();

    private Native.POINT _cursorTarget;

    public MainWindow(MenuConfig cfg, ActionExecutor executor)
    {
        InitializeComponent();
        _config = cfg;
        _executor = executor;
        Width = Height = cfg.MenuSize;
        _renderer = new RadialMenuRenderer(MenuCanvas, cfg.MenuSize, IsRunning);

        LoadRootItems();

        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftClick;
        MouseRightButtonUp += OnRightClick;
        PreviewKeyDown += OnKeyDown;
        Deactivated += (_, _) => HideMenu();
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
        _renderer = new RadialMenuRenderer(MenuCanvas, cfg.MenuSize, IsRunning);
        LoadRootItems();
    }

    // ---------- Показ / скрытие ----------

    public void ShowMenu()
    {
        LoadRootItems();
        _chain.Clear();
        _renderer.SetRoot(_rootItems);
        _renderer.SetInner(null);

        PositionAtCursor();
        Show();
        Activate();
        Native.SetCursorPos(_cursorTarget.X, _cursorTarget.Y);

        AnimateOpen();
        UpdateFromPointer();
        Log.Info("Меню показано");
    }

    public void HideMenu()
    {
        if (!IsVisible) return;
        Hide();
        Log.Info("Меню скрыто");
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

    private void AnimateOpen()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(140);

        RootScale.ScaleX = RootScale.ScaleY = 0.82;
        Root.Opacity = 0;

        Root.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, dur) { EasingFunction = ease });
    }

    // ---------- Логика наведения / кликов ----------

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsVisible) return;
        var hit = _renderer.HitTest(e.GetPosition(this));
        ApplyHit(hit);
    }

    /// <summary>
    /// Наведение на пункт внешнего кольца переключает внутреннее кольцо
    /// (категория — показать детей, обычный пункт — скрыть).
    /// Внутреннее кольцо и центр на цепочку не влияют.
    /// </summary>
    private void ApplyHit(in MenuHit hit)
    {
        var chainChanged = false;

        if (hit.Kind == HitKind.Outer && hit.Index < _rootItems.Count)
        {
            var item = _rootItems[hit.Index];
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

        if (chainChanged)
            _renderer.SetInner(InnerItems());

        _renderer.SetHover(hit);
        _renderer.SetCenter(CenterMain(hit), CenterHint());
    }

    private string CenterMain(in MenuHit hit)
    {
        var item = HitItem(hit);
        return item is null ? "Strofas" : item.Config.Label;
    }

    private string CenterHint() => _chain.Count > 0 ? "Назад" : "Закрыть";

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
        var hit = _renderer.HitTest(e.GetPosition(this));

        switch (hit.Kind)
        {
            case HitKind.Center:
                if (_chain.Count > 0) GoBack();
                else HideMenu();
                break;

            case HitKind.Outer when hit.Index < _rootItems.Count:
            {
                var item = _rootItems[hit.Index];
                if (item.Config.IsCategory) EnterCategory(item);
                else Execute(item);
                break;
            }

            case HitKind.Inner:
            {
                var inner = InnerItems();
                if (inner is null || hit.Index >= inner.Count) break;
                var item = inner[hit.Index];
                if (item.Config.IsCategory) EnterCategory(item);
                else Execute(item);
                break;
            }

            default:
                HideMenu(); // клик по пустоте между лепестками
                break;
        }

        e.Handled = true;
    }

    private void EnterCategory(MenuItemVm item)
    {
        // hover-preview мог уже поставить этот пункт в цепочку — не дублируем,
        // иначе «Назад» придётся жать дважды
        if (_chain.Count > 0 && ReferenceEquals(_chain[^1], item))
        {
            UpdateFromPointer();
            return;
        }

        _chain.Add(item);
        _renderer.SetInner(InnerItems());
        UpdateFromPointer();
    }

    private void GoBack()
    {
        if (_chain.Count == 0) return;
        _chain.RemoveAt(_chain.Count - 1);
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

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideMenu();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _chain.Count > 0)
        {
            GoBack();
            e.Handled = true;
        }
    }

    private void Execute(MenuItemVm item)
    {
        if (item.Config.Action is null)
        {
            Log.Warn($"Пункт '{item.Config.Label}' без действия");
            return;
        }

        _executor.Execute(item.Id, item.Config);

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

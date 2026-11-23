using System.Windows;
using Strofas.Core;
using Strofas.Editor;
using Strofas.Models;

namespace Strofas;

public partial class App : Application
{
    private Mutex? _mutex;
    private HotkeyService? _hotkey;
    private TrayIconService? _tray;
    private MainWindow? _menu;
    private ActionExecutor? _executor;
    private EditorWindow? _editor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "Strofas.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // вторая копия уже запущена — выходим молча
            Shutdown();
            return;
        }

        Log.Init();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error("Unhandled exception", ex.ExceptionObject as Exception);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _executor = new ActionExecutor();
        var cfg = ConfigService.Load();
        _menu = new MainWindow(cfg, _executor);

        _hotkey = new HotkeyService();
        if (!_hotkey.Register(cfg.Hotkey))
            Log.Warn("Горячка не зарегистрирована — открывайте меню из трея");
        _hotkey.Pressed += ToggleMenu;

        _tray = new TrayIconService();
        _tray.ShowMenuRequested += ToggleMenu;
        _tray.EditRequested += OpenEditor;
        _tray.ExitRequested += ExitApp;

        if (e.Args.Any(a => a is "--menu" or "--show"))
            ToggleMenu();
        if (e.Args.Any(a => a is "--editor"))
            OpenEditor();
    }

    private void ToggleMenu()
    {
        if (_menu is null) return;
        if (_menu.IsVisible) _menu.HideMenu();
        else _menu.ShowMenu();
    }

    private void OpenEditor()
    {
        if (_editor is { IsVisible: true })
        {
            _editor.Activate();
            return;
        }

        _editor = new EditorWindow(ConfigService.Load());
        _editor.ConfigApplied += ApplyConfig;
        _editor.Show();
        _editor.Activate();
    }

    private void ApplyConfig(MenuConfig cfg)
    {
        _hotkey?.Register(cfg.Hotkey);
        _menu?.ApplyConfig(cfg);
    }

    private void ExitApp() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkey?.Dispose();
        Log.Info("=== Strofas stopped ===");
        base.OnExit(e);
        _mutex?.Dispose();
    }
}

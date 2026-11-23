using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Strofas.Core;

/// <summary>Иконка в трее с контекстным меню.</summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _tray;

    public event Action? ShowMenuRequested;
    public event Action? EditRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Показать меню", null, (_, _) => ShowMenuRequested?.Invoke());
        menu.Items.Add("Редактор меню", null, (_, _) => EditRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var autostart = new WinForms.ToolStripMenuItem("Автозапуск")
        {
            CheckOnClick = true,
            Checked = Autostart.Enabled,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Enabled = autostart.Checked;
        menu.Items.Add(autostart);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Strofas",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMenuRequested?.Invoke();
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ExtractAssociatedIcon: {ex.Message}");
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
    }
}

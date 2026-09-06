using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VProxies;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _menu = new Forms.ContextMenuStrip();
        var showItem = new Forms.ToolStripMenuItem("Open VProxies");
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        showItem.Click += (_, _) => ShowRequested?.Invoke();
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(showItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        var icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : SystemIcons.Application;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "VProxies",
            Icon = icon ?? SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void ShowMinimizedNotice() => _notifyIcon.ShowBalloonTip(
        1800,
        "VProxies is still running",
        "Double-click the tray icon to reopen it. Choose Exit to stop the proxy and close completely.",
        Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private static class SystemIcons
    {
        public static Drawing.Icon Application => Drawing.SystemIcons.Application;
    }
}

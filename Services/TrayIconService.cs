using System.Drawing;
using System.Windows.Threading;
using WindowsGSH.Core.Windows;
using WinForms = System.Windows.Forms;

namespace WindowsGSH.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DesktopCommandRouter _router;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService(Dispatcher dispatcher, DesktopCommandRouter router)
    {
        _dispatcher = dispatcher;
        _router = router;
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "WindowsGSH",
            Icon = LoadIcon(),
            ContextMenuStrip = BuildMenu(),
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(CreateItem("Show WindowsGSH", DesktopCommand.ShowWindowsGsh, bold: true));
        menu.Items.Add(CreateItem("Open Discord settings", DesktopCommand.OpenDiscordSettings));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateItem("Stop all servers...", DesktopCommand.StopAllServers));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateItem("Exit", DesktopCommand.Exit));
        return menu;
    }

    private WinForms.ToolStripMenuItem CreateItem(string text, DesktopCommand command, bool bold = false)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        if (bold)
        {
            item.Font = new Font(item.Font, FontStyle.Bold);
        }

        item.Click += (_, _) => Dispatch(command);
        return item;
    }

    private void NotifyIcon_MouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            Dispatch(DesktopCommand.ShowWindowsGsh);
        }
    }

    private void NotifyIcon_MouseDoubleClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            Dispatch(DesktopCommand.ShowWindowsGsh);
        }
    }

    private void Dispatch(DesktopCommand command)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await _router.ExecuteAsync(command);
            }
            catch (Exception ex)
            {
                AppLogService.Add($"Tray command failed: {ex.Message}");
            }
        }));
    }

    private static Icon LoadIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                return Icon.ExtractAssociatedIcon(processPath) ?? (Icon)SystemIcons.Application.Clone();
            }
            catch
            {
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Forms = System.Windows.Forms;

namespace MergePool.Ui.Services;

/// <summary>
/// MergePool's presence in the notification area. The window can be closed without the app going
/// away, which is what "always running" means for something that manages drives: the service keeps
/// the pools mounted either way, and this keeps a way back to the window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayPresence : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Action _show;
    private readonly Action _exit;

    public TrayPresence(Action show, Action exit)
    {
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MergePool",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _icon.DoubleClick += (_, _) => _show();
    }

    /// <summary>Updates the hover text, so the pools' state is readable without opening anything.</summary>
    public void SetStatus(string status)
    {
        // The shell truncates anything past 63 characters, and throws past 127.
        _icon.Text = status.Length <= 63 ? status : status[..60] + "…";
    }

    /// <summary>A balloon for the few things worth interrupting someone over.</summary>
    public void Notify(string title, string message, bool warning = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000);
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open MergePool", null, (_, _) => _show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _exit());
        return menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/MergePool.ico"))?.Stream;

            if (stream is not null)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            // Falls through to the system icon: a missing icon must not stop the app from running.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

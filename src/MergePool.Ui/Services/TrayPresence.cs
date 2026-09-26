using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MergePool.Core.Config;

namespace MergePool.Ui.Services;

/// <summary>
/// MergePool's presence in the notification area. The window can be closed without the app going
/// away, which is what "always running" means for something that manages drives: the service keeps
/// the pools mounted either way, and this keeps a way back to the window.
/// </summary>
/// <remarks>
/// Talks to the shell directly rather than through Windows Forms. Pulling in Windows Forms for one
/// icon would drag its whole application model into a WPF app — including a DPI model that fights
/// the per-monitor awareness declared in the manifest.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayPresence : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;

    private readonly Action _show;
    private readonly Action _openWebPage;
    private readonly Action _exit;
    private readonly HwndSource _messageWindow;
    private readonly ContextMenu _menu;
    private readonly uint _id = 1;

    private NotifyIconData _icon;
    private nint _iconHandle;
    private bool _added;
    private bool _disposed;

    public TrayPresence(Action show, Action openWebPage, Action exit)
    {
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _openWebPage = openWebPage ?? throw new ArgumentNullException(nameof(openWebPage));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        // A message-only window: never shown, never in the taskbar, exists purely to receive the
        // shell's callbacks about the icon.
        _messageWindow = new HwndSource(new HwndSourceParameters("MergePool.Tray")
        {
            ParentWindow = MessageOnlyParent,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        });

        _messageWindow.AddHook(OnMessage);

        _menu = BuildMenu();
        _iconHandle = LoadApplicationIcon();

        _icon = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = _messageWindow.Handle,
            Id = _id,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            Icon = _iconHandle,
            Tip = "MergePool",

            // Fixed-length string fields: the marshaller needs something to copy, not null.
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

        _added = Shell_NotifyIconW(NimAdd, ref _icon);
    }

    /// <summary>Updates the hover text, so the pools' state is readable without opening anything.</summary>
    public void SetStatus(string status)
    {
        if (_disposed || !_added)
        {
            return;
        }

        // The shell truncates anything past 127 characters.
        _icon.Tip = status.Length <= 120 ? status : status[..119] + "…";
        _icon.Flags = NifTip;
        Shell_NotifyIconW(NimModify, ref _icon);
    }

    /// <summary>A balloon for the few things worth interrupting someone over.</summary>
    public void Notify(string title, string message, bool warning = false)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var balloon = _icon;
        balloon.Flags = NifInfo;
        balloon.InfoTitle = Truncate(title, 60);
        balloon.Info = Truncate(message, 250);
        balloon.InfoFlags = warning ? NiifWarning : NiifInfo;

        Shell_NotifyIconW(NimModify, ref balloon);
    }

    private ContextMenu BuildMenu()
    {
        var open = new MenuItem { Header = "Open MergePool", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _show();

        // The web page is here because this is where people look for it, and because it is the one
        // way into MergePool that does not need this window at all.
        var web = new MenuItem
        {
            Header = "Open the web page",
            ToolTip = string.Create(
                CultureInfo.CurrentCulture,
                $"Serves MergePool in a browser, on port {WebOptions.DefaultPort} unless you have chosen another. Turns it on if it is off."),
        };

        web.Click += (_, _) => _openWebPage();

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => _exit();

        var menu = new ContextMenu { StaysOpen = false };
        menu.Items.Add(open);
        menu.Items.Add(web);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != CallbackMessage)
        {
            return nint.Zero;
        }

        switch ((int)lParam)
        {
            case WmLeftButtonUp:
            case WmLeftButtonDoubleClick:
                _show();
                handled = true;
                break;

            case WmRightButtonUp:
                ShowMenu();
                handled = true;
                break;
        }

        return nint.Zero;
    }

    private void ShowMenu()
    {
        // Without foreground ownership the menu would stay open after a click elsewhere, which is
        // the classic notification-area bug.
        SetForegroundWindow(_messageWindow.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>
    /// The first icon in this executable, which is the application icon the project embeds. Falls
    /// back to the shell's generic application icon rather than showing nothing.
    /// </summary>
    private static nint LoadApplicationIcon()
    {
        var path = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(path))
        {
            var handle = ExtractIconW(nint.Zero, path, 0);
            if (handle != nint.Zero && handle != 1)
            {
                return handle;
            }
        }

        return LoadIconW(nint.Zero, IdiApplication);
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            Shell_NotifyIconW(NimDelete, ref _icon);
            _added = false;
        }

        if (_iconHandle != nint.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
        }

        _messageWindow.RemoveHook(OnMessage);
        _messageWindow.Dispose();
    }

    // ---- Win32 ----

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;

    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;

    private const uint NiifInfo = 0x01;
    private const uint NiifWarning = 0x02;

    /// <summary>HWND_MESSAGE: the window exists only to receive messages.</summary>
    private static readonly nint MessageOnlyParent = -3;

    private static readonly nint IdiApplication = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint Version;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint ExtractIconW(nint instance, string executablePath, int iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}

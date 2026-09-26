using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using MergePool.Core.Update;
using MergePool.Ui.Services;
using MergePool.Ui.ViewModels;
using MergePool.Ui.Views;

namespace MergePool.Ui;

[SupportedOSPlatform("windows")]
public partial class App : Application
{
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayPresence? _tray;
    private SingleInstance? _instance;
    private bool _reallyExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (HandOverToNewerVersion(e.Args))
        {
            return;
        }

        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirst)
        {
            // Already running — usually launched again from the Start menu while sitting in the
            // notification area. Bring that window up instead of opening a second one.
            _instance.SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // An entry written by an older version may name a version directory rather than the link.
        StartupRegistration.RepairPinnedEntry();

        ApplyTheme();

        var startHidden = e.Args.Any(argument =>
            string.Equals(argument, StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));

        _viewModel = new MainViewModel
        {
            ConfirmAction = Confirm,
            OpenInExplorer = OpenInExplorer,
        };

        _viewModel.Web.CopyToClipboard = CopyToClipboard;
        _viewModel.Web.OpenInBrowser = OpenInExplorer;
        _viewModel.Updates.RelaunchWindow = RelaunchWindow;

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += OnWindowClosing;

        // The window can be closed without the app going away: MergePool is a thing you leave
        // running, and the tray is where it lives when it is not in front of you.
        _tray = new TrayPresence(ShowWindow, OpenWebPage, ExitForReal);
        _viewModel.StatusChanged += (_, status) => _tray?.SetStatus(status);
        _viewModel.Updates.NewVersionFound += (_, version) =>
            _tray?.Notify("MergePool update", $"Version {version} is available and will install itself.");

        _instance.ListenForActivation(() => Dispatcher.Invoke(ShowWindow));

        if (!startHidden)
        {
            _window.Show();
        }

        await _viewModel.StartAsync();
    }

    /// <summary>
    /// Starts the newest installed version instead of this one, when this one was launched from a
    /// version directory and a newer version has since been installed. Returns whether it did.
    /// </summary>
    /// <remarks>
    /// This runs before anything else, and before the single-instance check in particular: the
    /// version being handed to has to be free to take the instance, which it cannot do while this
    /// process is holding it. A hand-over only ever moves forward to a version that exists on disk,
    /// so it cannot loop.
    /// </remarks>
    private bool HandOverToNewerVersion(string[] arguments)
    {
        string? newer;

        try
        {
            newer = VersionHandoff.NewerExecutable(Environment.ProcessPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (newer is null || !Restart(newer, arguments))
        {
            // Carrying on as the older version is a worse outcome than not, but it is a working
            // window, which is better than no window at all.
            return false;
        }

        Shutdown();
        return true;
    }

    /// <summary>
    /// Replaces this window with the one at <paramref name="executable"/>, which is how a window
    /// left behind by an update catches up.
    /// </summary>
    /// <remarks>
    /// The single-instance hold goes first: the version being started has to be able to take it,
    /// and it cannot while this process is holding it — it would signal this window and exit, and
    /// nothing would have changed. If starting the new copy fails, the hold is taken back, because
    /// running without it is how a second window ends up open.
    /// </remarks>
    private bool RelaunchWindow(string executable)
    {
        _instance?.Dispose();
        _instance = null;

        // No --tray: the person asked for this window, so the new one opens in front of them.
        if (!Restart(executable, []))
        {
            _instance = SingleInstance.Acquire();
            _instance.ListenForActivation(() => Dispatcher.Invoke(ShowWindow));
            return false;
        }

        _reallyExiting = true;
        Shutdown();
        return true;
    }

    /// <summary>Launches another copy of MergePool's window with this one's arguments.</summary>
    private static bool Restart(string executable, IEnumerable<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            return Process.Start(start) is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Follows the system's app theme, since the window has no theme picker of its own.</summary>
    private void ApplyTheme()
    {
        var dark = false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Light it is.
        }

        if (!dark)
        {
            return;
        }

        var palette = Resources.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) == true);

        if (palette is not null)
        {
            palette.Source = new Uri("Themes/Dark.xaml", UriKind.Relative);
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>
    /// Turns the web interface on if it is off and opens it in a browser. Reached from the
    /// notification area, so it works without the window ever being opened.
    /// </summary>
    private async void OpenWebPage()
    {
        if (_viewModel is null)
        {
            return;
        }

        await _viewModel.Web.OpenWebPageAsync();

        // Nothing opened, and there is no window in front of anyone to read the reason in.
        if (!_viewModel.Web.IsListening && _viewModel.Web.Message is { Length: > 0 } problem)
        {
            _tray?.Notify("MergePool web page", problem, warning: true);
        }
    }

    /// <summary>Closing the window hides it. Quitting is a deliberate act, from the tray menu.</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        _tray?.Notify(
            "MergePool is still running",
            "Your pools stay mounted. Open it again from the notification area, or quit from its menu.");
    }

    private void ExitForReal()
    {
        _reallyExiting = true;
        Shutdown();
    }

    private static bool Confirm(string title, string detail, string confirmLabel)
    {
        var answer = MessageBox.Show(
            $"{detail}\n\n{confirmLabel}?",
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        return answer == MessageBoxResult.OK;
    }

    /// <summary>
    /// The clipboard is occasionally locked by another process, and a failed copy is not worth
    /// taking the window down for.
    /// </summary>
    private static void CopyToClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(80);
            }
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path.EndsWith(':') ? path + "\\" : path,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(
                $"MergePool could not open {path}.\n\n{exception.Message}",
                "MergePool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// A UI crash must not look like a broken pool: the service keeps serving whatever happens
    /// here, so we tell the user that and carry on.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"MergePool's window hit an unexpected error:\n\n{e.Exception.Message}\n\n"
            + "Your pools are unaffected — the MergePool service keeps them mounted.",
            "MergePool",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();

        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
        }

        base.OnExit(e);
    }
}

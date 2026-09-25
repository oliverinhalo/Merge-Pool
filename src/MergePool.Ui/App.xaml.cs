using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
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

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += OnWindowClosing;

        // The window can be closed without the app going away: MergePool is a thing you leave
        // running, and the tray is where it lives when it is not in front of you.
        _tray = new TrayPresence(ShowWindow, ExitForReal);
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

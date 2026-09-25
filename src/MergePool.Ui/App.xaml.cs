using System.Windows;
using System.Windows.Threading;
using MergePool.Ui.ViewModels;
using MergePool.Ui.Views;

namespace MergePool.Ui;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        _viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = _viewModel };
        window.Show();

        await _viewModel.StartAsync();
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
        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
        }

        base.OnExit(e);
    }
}

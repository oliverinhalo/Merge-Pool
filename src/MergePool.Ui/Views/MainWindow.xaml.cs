using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MergePool.Ui.Services;
using MergePool.Ui.ViewModels;

namespace MergePool.Ui.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ApplyPaneRatio(StartupRegistration.PaneRatio());

        // Saved on the way out rather than on every drag: the ratio only matters next time.
        Closing += (_, _) => StartupRegistration.SetPaneRatio(CurrentPaneRatio());
    }

    /// <summary>
    /// The two shortcuts worth having: Ctrl+Shift+A shows or hides the advanced settings, and F5
    /// asks the service for the current state.
    /// </summary>
    /// <remarks>
    /// Handled here rather than as <c>KeyBinding</c>s because an input binding is not part of the
    /// visual tree, so it does not reliably inherit the window's data context to find the commands
    /// on.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (DataContext is MainViewModel model)
        {
            if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                model.ToggleAdvancedCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                model.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Double-clicking the divider evens the two panels up, as a way back from any drag.</summary>
    private void OnSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplyPaneRatio(StartupRegistration.DefaultPaneRatio);
        e.Handled = true;
    }

    /// <summary>
    /// The columns are star-sized, so the ratio is expressed as the two star values rather than as
    /// pixels. That keeps the split proportional when the window itself is resized.
    /// </summary>
    private void ApplyPaneRatio(double leftShare)
    {
        var left = Math.Clamp(leftShare, 0.2, 0.8);

        LeftPane.Width = new GridLength(left, GridUnitType.Star);
        RightPane.Width = new GridLength(1 - left, GridUnitType.Star);
    }

    private double CurrentPaneRatio()
    {
        var left = LeftPane.Width.Value;
        var right = RightPane.Width.Value;
        var total = left + right;

        return total > 0 ? left / total : StartupRegistration.DefaultPaneRatio;
    }
}

using Avalonia.Controls;

namespace WaydroidEditor;

/// <summary>
/// Main window; on open it hands itself to the view model as the dialog and picker parent and
/// starts the initial package load.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Wires the Opened handler that starts the view model once the window exists.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is not PrefsViewModel viewModel)
                return;
            viewModel.Host = this;
            try
            {
                viewModel.Start();
            }
            catch (Exception ex)
            {
                ErrorDialog.Show(this, "Startup failed", ex.ToString());
            }
        };
    }
}

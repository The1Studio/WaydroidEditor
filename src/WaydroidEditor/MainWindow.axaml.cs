using Avalonia.Controls;

namespace WaydroidEditor;

public partial class MainWindow : Window
{
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

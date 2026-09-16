using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace WaydroidPrefsEditor.App;

public partial class App : Application
{
    /// <summary>Set by <see cref="Program"/> before the Avalonia app is built.</summary>
    public static StartupOptions Startup { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new PrefsViewModel(Startup) };

        base.OnFrameworkInitializationCompleted();
    }
}

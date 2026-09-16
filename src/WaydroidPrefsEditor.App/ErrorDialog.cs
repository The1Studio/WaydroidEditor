using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WaydroidPrefsEditor.App;

/// <summary>Small modal message box — the Avalonia 12 core ships no ContentDialog.</summary>
internal static class ErrorDialog
{
    public static void Show(Window? owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var text = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
        };

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        ok.Click += (_, _) => dialog.Close();

        var panel = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(text);
        dialog.Content = panel;

        if (owner is null)
            dialog.Show();
        else
            _ = dialog.ShowDialog(owner);
    }
}

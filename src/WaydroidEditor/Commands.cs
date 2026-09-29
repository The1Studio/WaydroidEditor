using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WaydroidEditor;

/// <summary>Minimal INotifyPropertyChanged base — no MVVM toolkit dependency needed.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    /// <summary>Raised with the name of any property whose setter called <see cref="Raise"/>.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Notifies bindings that one property changed; defaults to the calling member's name.</summary>
    /// <param name="name">The property name, or null to use the caller's name.</param>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A synchronous <see cref="ICommand"/> wrapping one parameterless action.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    /// <summary>Raised after <see cref="RaiseCanExecuteChanged"/>; this command never disables itself.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Always true — a <see cref="RelayCommand"/> has no enabled state of its own.</summary>
    /// <param name="parameter">Unused; the action takes no argument.</param>
    public bool CanExecute(object? parameter) => true;

    /// <summary>Runs the wrapped action.</summary>
    /// <param name="parameter">Unused; the action takes no argument.</param>
    public void Execute(object? parameter) => execute();

    /// <summary>Tells bindings to re-query <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runs one action at a time; failures are reported through <paramref name="onError"/>.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Action<Exception> onError) : ICommand
{
    bool _running;

    /// <summary>Raised when the running state flips, so bindings re-query <see cref="CanExecute"/>.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>False while the previous invocation is still in flight.</summary>
    /// <param name="parameter">Unused; the action takes no argument.</param>
    public bool CanExecute(object? parameter) => !_running;

    /// <summary>Awaits the wrapped task, disabling the command meanwhile and routing any throw to the error handler.</summary>
    /// <param name="parameter">Unused; the action takes no argument.</param>
    public async void Execute(object? parameter)
    {
        if (_running)
            return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            onError(ex);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

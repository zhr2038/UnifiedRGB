using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace UnifiedRgb.App.Mvvm;

/// <summary>Minimal INotifyPropertyChanged base (no toolkit dependency).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Marshals view-model updates onto the UI thread; tests run inline.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs actions on the caller's thread, one at a time (like a UI thread would), for headless tests.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    private readonly object _gate = new();

    public static InlineDispatcher Instance { get; } = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            action();
        }
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute)
        : this(_ => execute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command: one execution at a time; failures are routed to <paramref name="onError"/> (shown in the status bar)
/// instead of crashing the dispatcher. <see cref="ExecuteAsync"/> lets tests await completion.
/// </summary>
public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception> onError, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public AsyncCommand(Func<Task> execute, Action<Exception> onError)
        : this(_ => execute(), onError)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

#pragma warning disable VSTHRD100, AsyncFixer03 // ICommand.Execute is void by contract; exceptions are routed to onError
    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);
#pragma warning restore VSTHRD100, AsyncFixer03

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // UI boundary: report, never crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            onError(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

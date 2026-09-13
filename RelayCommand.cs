using System.Windows.Input;

namespace MarantzController;

/// <summary>
/// Egyszerű ICommand implementáció. Külső csomag (CommunityToolkit.Mvvm)
/// nélkül, hogy a projekt restore-mentes maradjon.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _executeAsync;
    private readonly Action? _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        if (_executeAsync is not null)
            await _executeAsync();
        else
            _execute?.Invoke();
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Paraméteres ICommand (pl. forrás-token vagy Quick Select sorszám átadására).
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Func<T?, Task>? _executeAsync;
    private readonly Action<T?>? _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<T?, Task> executeAsync, Func<T?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Convert(parameter)) ?? true;

    public async void Execute(object? parameter)
    {
        var arg = Convert(parameter);
        if (_executeAsync is not null)
            await _executeAsync(arg);
        else
            _execute?.Invoke(arg);
    }

    private static T? Convert(object? parameter) => parameter is T t ? t : default;

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

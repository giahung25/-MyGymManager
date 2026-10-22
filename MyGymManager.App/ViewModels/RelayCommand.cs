namespace MyGymManager.App.ViewModels;

using System.Windows.Input;

/// <summary>
/// Triển khai ICommand cơ bản cho mô hình MVVM trong WPF.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>
/// Triển khai ICommand có kiểu tham số chung (Generic RelayCommand<T>).
/// Hỗ trợ ép kiểu tham số truyền từ XAML CommandParameter.
/// </summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter == null && typeof(T).IsValueType)
            return _canExecute?.Invoke(default) ?? true;

        if (parameter is T typedParam)
            return _canExecute?.Invoke(typedParam) ?? true;

        return _canExecute?.Invoke(default) ?? true;
    }

    public void Execute(object? parameter)
    {
        if (parameter is T typedParam)
        {
            _execute(typedParam);
            return;
        }

        // Hỗ trợ convert CommandParameter dạng string "0", "1", "2" sang int
        if (parameter is string str && typeof(T) == typeof(int) && int.TryParse(str, out int intVal))
        {
            _execute((T)(object)intVal);
            return;
        }

        if (parameter == null && typeof(T).IsValueType)
        {
            _execute(default);
            return;
        }

        _execute((T?)parameter);
    }
}

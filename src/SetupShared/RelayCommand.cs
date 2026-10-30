using System.Windows.Input;

namespace BiliDownloader.SetupShared;

/// <summary>最小 ICommand 实现，支持同步/异步执行与 CanExecute 动态刷新。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task?>? _executeAsync;
    private readonly Action? _execute;
    private readonly Func<bool>? _canExecute;
    private bool _executing;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        HookRequery();
    }

    public RelayCommand(Func<Task?> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
        HookRequery();
    }

    /// <summary>
    /// 带 CanExecute 条件的命令挂到 CommandManager 上：WPF 会在用户交互时自动重查，
    /// 否则状态变量变了按钮也不会从禁用态恢复（安装器「取消」按钮点不动就是这个问题）。
    /// </summary>
    private void HookRequery()
    {
        if (_canExecute is null) return;
        CommandManager.RequerySuggested += (_, _) => RaiseCanExecuteChanged();
    }

    public bool CanExecute(object? parameter)
    {
        if (_executing) return false;
        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _executing = true;
        RaiseCanExecuteChanged();
        try
        {
            if (_execute is not null) _execute();
            else if (_executeAsync is not null)
            {
                var t = _executeAsync();
                if (t is not null) await t;
            }
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

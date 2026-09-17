namespace StreamPilot.App.ViewModels;

using System.Windows.Input;

/// <summary>
/// 无参数同步命令。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>初始化命令。</summary>
    /// <param name="execute">执行逻辑。</param>
    /// <param name="canExecute">可用性判定，可为 <see langword="null"/>。</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>通知可用性变化。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令：执行期间自动禁用，异常交由调用方提供的处理器统一上报。
/// </summary>
/// <remarks>
/// 刻意不使用 <c>async void</c>：这里用 <see cref="Task"/> 串起来的 async 方法是
/// 唯一允许的 fire-and-forget 形式，并且异常一定会被处理器捕获（禁止吞异常）。
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<Exception, Task> _onError;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    /// <summary>初始化命令。</summary>
    /// <param name="execute">异步执行逻辑。</param>
    /// <param name="onError">异常处理器（必须处理异常，不允许空实现）。</param>
    /// <param name="canExecute">可用性判定，可为 <see langword="null"/>。</param>
    public AsyncRelayCommand(Func<object?, Task> execute, Func<Exception, Task> onError, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(onError);
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public void Execute(object? parameter) => _ = RunAsync(parameter);

    /// <summary>通知可用性变化。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync(object? parameter)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消不属于错误。
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await _onError(exception).ConfigureAwait(true);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }
}

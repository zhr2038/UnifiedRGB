using System.Reflection;
using StreamJsonRpc;
using UnifiedRgb.App.Ipc;

namespace UnifiedRgb.App;

public enum UiExceptionDisposition
{
    /// <summary>A known connection-state condition; the UI keeps working and shows the error.</summary>
    Recoverable,

    /// <summary>Anything else: the view may be partially updated, so the UI stops issuing operations and asks for a restart.</summary>
    Unsafe,
}

public enum UiFaultLogLevel
{
    Warning,
    Error,
    Fatal,
}

/// <summary>
/// Conservative classification of exceptions that escaped to the WPF dispatcher (AsyncCommand already routes its own
/// failures). Only the disconnect family is recoverable: the Host was not reachable (<see cref="HostNotConnectedException"/>)
/// or the session dropped mid-request (<see cref="ConnectionLostException"/>); reconnect re-reads Host state. Everything
/// else — including IOException, generic InvalidOperationException, Host-side rejections (RemoteInvocationException),
/// cancellations and <see cref="HostAccessBlockedException"/> — is unsafe.
/// </summary>
public static class UiExceptionPolicy
{
    private const int MaxUnwrapDepth = 8;
    private const int MaxAggregateInners = 16;

    public static UiExceptionDisposition Classify(Exception? exception) =>
        IsRecoverable(exception, 0) ? UiExceptionDisposition.Recoverable : UiExceptionDisposition.Unsafe;

    private static bool IsRecoverable(Exception? ex, int depth)
    {
        if (ex is null || depth > MaxUnwrapDepth)
        {
            return false;
        }

        switch (ex)
        {
            case TargetInvocationException { InnerException: { } inner }:
                return IsRecoverable(inner, depth + 1);
            case AggregateException aggregate:
                var inners = aggregate.InnerExceptions;
                if (inners.Count == 0 || inners.Count > MaxAggregateInners)
                {
                    return false;
                }

                foreach (var inner in inners)
                {
                    if (!IsRecoverable(inner, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case HostNotConnectedException:
            case ConnectionLostException:
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Dispatcher-exception state machine. Recoverable: log (bounded) and report to the view. First unsafe fault: block Host
/// access first, log the full exception at Fatal, then show the in-window fault state exactly once — no dialogs. Later
/// exceptions are only logged up to a bound and counted. Every callback is isolated; <see cref="Handle"/> never throws and
/// tolerates re-entrant calls.
/// </summary>
public sealed class UiExceptionCoordinator
{
    public const int MaxLoggedRecoverable = 50;
    public const int MaxLoggedAfterFault = 50;

    public const string FaultMessage =
        "界面出现未预期错误，已禁止新操作并断开界面连接；后台继续运行，已发送的请求仍可能完成。"
        + "请从托盘菜单退出并重新打开光域。详细信息已写入界面日志。";

    private readonly object _gate = new();
    private readonly Action<UiFaultLogLevel, Exception?, string> _log;
    private readonly Action<string> _blockHost;
    private readonly Action<string> _showFault;
    private readonly Action<Exception> _reportRecoverable;
    private readonly Func<bool> _isShuttingDown;
    private bool _handling;
    private int _recoverable;
    private int _afterFault;

    public UiExceptionCoordinator(
        Action<UiFaultLogLevel, Exception?, string> log,
        Action<string> blockHost,
        Action<string> showFault,
        Action<Exception> reportRecoverable,
        Func<bool> isShuttingDown)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _blockHost = blockHost ?? throw new ArgumentNullException(nameof(blockHost));
        _showFault = showFault ?? throw new ArgumentNullException(nameof(showFault));
        _reportRecoverable = reportRecoverable ?? throw new ArgumentNullException(nameof(reportRecoverable));
        _isShuttingDown = isShuttingDown ?? throw new ArgumentNullException(nameof(isShuttingDown));
    }

    /// <summary>The in-window fault text once an unsafe exception happened, otherwise null.</summary>
    public string? FaultText { get; private set; }

    public bool IsFaulted => FaultText is not null;

    /// <summary>Exceptions after the fault (or re-entrant ones) that were counted but not individually logged.</summary>
    public int SuppressedCount { get; private set; }

    public static string FaultTextFor(Exception exception) =>
        FaultMessage + "（错误类型：" + PipeClientDiagnostics.Sanitize(exception?.GetType().Name, 64) + "）";

    /// <summary>Returns the value for DispatcherUnhandledExceptionEventArgs.Handled (always true: the window and drafts stay alive).</summary>
    public bool Handle(Exception? exception)
    {
#pragma warning disable CA1031 // the dispatcher fault handler must never throw
        try
        {
            lock (_gate)
            {
                if (_handling)
                {
                    SuppressedCount++;
                    return true;
                }

                _handling = true;
                try
                {
                    HandleCore(exception ?? new InvalidOperationException("Null dispatcher exception."));
                }
                finally
                {
                    _handling = false;
                }
            }
        }
        catch (Exception)
        {
            // swallowed deliberately: recursion here would re-enter the dispatcher handler
        }
#pragma warning restore CA1031

        return true;
    }

    private void HandleCore(Exception ex)
    {
        if (FaultText is not null)
        {
            _afterFault++;
            if (_afterFault <= MaxLoggedAfterFault)
            {
                Try(() => _log(UiFaultLogLevel.Error, ex, "UI exception while UI is faulted"));
            }
            else
            {
                SuppressedCount++;
            }

            return;
        }

        if (IsShuttingDownSafe())
        {
            Try(() => _log(UiFaultLogLevel.Warning, ex, "UI exception during shutdown"));
            return;
        }

        if (UiExceptionPolicy.Classify(ex) == UiExceptionDisposition.Recoverable)
        {
            _recoverable++;
            if (_recoverable <= MaxLoggedRecoverable)
            {
                Try(() => _log(UiFaultLogLevel.Warning, ex, "Recoverable UI exception (Host connection)"));
            }
            else
            {
                SuppressedCount++;
            }

            Try(() => _reportRecoverable(ex));
            return;
        }

        var text = FaultTextFor(ex);
        FaultText = text;
        Try(() => _blockHost(text));
        Try(() => _log(UiFaultLogLevel.Fatal, ex, "Unsafe UI exception; Host operations blocked, restart required"));
        Try(() => _showFault(text));
    }

    private bool IsShuttingDownSafe()
    {
#pragma warning disable CA1031
        try
        {
            return _isShuttingDown();
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    private static void Try(Action action)
    {
#pragma warning disable CA1031 // isolate each callback: logging/UI failures must not prevent blocking or recurse
        try
        {
            action();
        }
        catch (Exception)
        {
            // ignored
        }
#pragma warning restore CA1031
    }
}

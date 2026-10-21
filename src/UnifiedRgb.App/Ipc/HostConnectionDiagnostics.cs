using System.Globalization;
using System.IO;
using System.Text;

namespace UnifiedRgb.App.Ipc;

/// <summary>Why the most recent Host connection attempt or session ended (<see cref="None"/> while connected/never failed).</summary>
public enum HostConnectionFailure
{
    None,

    /// <summary>The pipe did not accept a connection within <see cref="PipeClient.ConnectTimeout"/> (Host not running or busy).</summary>
    ConnectTimeout,

    /// <summary>The client itself was stopped/disposed (caller shutdown), not a Host-side failure.</summary>
    ClientStopped,

    /// <summary>Windows refused to open the pipe for this user.</summary>
    AccessDenied,

    /// <summary>The pipe opened but its server identity failed the strict trust check, so the session was refused.</summary>
    TrustRejected,

    /// <summary>A pipe I/O error while connecting or verifying.</summary>
    PipeIo,

    /// <summary>An established JSON-RPC session ended (Host exit, broken stream, protocol error).</summary>
    SessionLost,

    /// <summary>Anything else; only the exception type/HRESULT is recorded.</summary>
    Unexpected,
}

/// <summary>Where in one connection attempt a failure happened.</summary>
internal enum PipeConnectPhase
{
    Connect,
    Verify,
    Session,
}

/// <summary>Kind plus bounded, sanitized operator text; published atomically so readers never see a mismatched pair.</summary>
internal sealed record ConnectionDiagnostic(HostConnectionFailure Kind, string Text);

/// <summary>
/// Builds <see cref="PipeClient.LastError"/> text. Exception messages are only quoted for OS-originated connect/verify
/// failures (pipe/ACL/trust messages carry no RPC content). Session and unexpected failures record only type names, the
/// disconnect reason and the HRESULT, so a request/response payload echoed in an exception message can never be stored.
/// </summary>
internal static class PipeClientDiagnostics
{
    public const int MaxDetailLength = 160;
    public const int MaxTextLength = 256;

    public static HostConnectionFailure Classify(PipeConnectPhase phase, Exception ex, bool stopRequested)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (stopRequested)
        {
            return HostConnectionFailure.ClientStopped;
        }

        return phase switch
        {
            PipeConnectPhase.Connect => ex switch
            {
                OperationCanceledException or TimeoutException => HostConnectionFailure.ConnectTimeout,
                UnauthorizedAccessException => HostConnectionFailure.AccessDenied,
                IOException => HostConnectionFailure.PipeIo,
                _ => HostConnectionFailure.Unexpected,
            },

            // Any authorization failure while reading the server owner fails closed as a trust rejection.
            PipeConnectPhase.Verify => ex switch
            {
                UnauthorizedAccessException => HostConnectionFailure.TrustRejected,
                IOException => HostConnectionFailure.PipeIo,
                _ => HostConnectionFailure.Unexpected,
            },
            _ => HostConnectionFailure.SessionLost,
        };
    }

    public static ConnectionDiagnostic FromException(PipeConnectPhase phase, Exception ex, bool stopRequested, string? sessionReason = null)
    {
        var kind = Classify(phase, ex, stopRequested);
        return new ConnectionDiagnostic(kind, Describe(kind, ex, sessionReason));
    }

    public static ConnectionDiagnostic SessionEnded(string? reason, Exception? ex) =>
        new(HostConnectionFailure.SessionLost, Describe(HostConnectionFailure.SessionLost, ex, reason));

    public static ConnectionDiagnostic Stopped() =>
        new(HostConnectionFailure.ClientStopped, Describe(HostConnectionFailure.ClientStopped, null, null));

    public static string Describe(HostConnectionFailure kind, Exception? ex, string? sessionReason)
    {
        var text = kind switch
        {
            HostConnectionFailure.None => "",
            HostConnectionFailure.ConnectTimeout => string.Format(
                CultureInfo.InvariantCulture, "连接后台超时（{0:0.#} 秒内无响应），后台可能未运行", PipeClient.ConnectTimeout.TotalSeconds),
            HostConnectionFailure.ClientStopped => "界面已停止连接后台",
            HostConnectionFailure.AccessDenied => "无权打开后台管道：" + OsDetail(ex),
            HostConnectionFailure.TrustRejected => "后台身份校验未通过，已拒绝连接：" + OsDetail(ex),
            HostConnectionFailure.PipeIo => "后台管道读写失败：" + OsDetail(ex),
            HostConnectionFailure.SessionLost => "与后台的会话已断开（" + SessionDetail(sessionReason, ex) + "）",
            _ => "连接后台时出现意外错误（" + TypeDetail(ex) + "）",
        };
        return Truncate(text, MaxTextLength);
    }

    /// <summary>Single line, no control characters, bounded length.</summary>
    public static string Sanitize(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var sb = new StringBuilder(Math.Min(value.Length, max + 1));
        var space = false;
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
            if (sb.Length > max)
            {
                break;
            }
        }

        return Truncate(sb.ToString(), max);
    }

    private static string OsDetail(Exception? ex)
    {
        if (ex is null)
        {
            return "原因未知";
        }

        var message = Sanitize(ex.Message, MaxDetailLength);
        return message.Length == 0 ? TypeDetail(ex) : message + " (" + Hresult(ex) + ")";
    }

    private static string SessionDetail(string? reason, Exception? ex)
    {
        var r = Sanitize(reason, 48);
        var parts = r.Length == 0 ? "原因未知" : r;
        return ex is null ? parts : parts + "，" + TypeDetail(ex);
    }

    private static string TypeDetail(Exception? ex)
    {
        if (ex is null)
        {
            return "原因未知";
        }

        var inner = ex.InnerException is null ? "" : " ← " + Sanitize(ex.InnerException.GetType().Name, 64);
        return Sanitize(ex.GetType().Name, 64) + inner + ", " + Hresult(ex);
    }

    private static string Hresult(Exception ex) => "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}

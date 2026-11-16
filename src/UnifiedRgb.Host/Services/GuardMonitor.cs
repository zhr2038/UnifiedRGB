using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// ARCHITECTURE §7.4–7.5: every <see cref="UnifiedRgbOptions.GuardRescanSeconds"/> re-runs WriterGuard (pause/resume,
/// open newly cleared controllers); a hotplug event triggers a full rescan on the next 1 s tick. Power/S0ix resume wakes
/// immediately and runs <see cref="OnPowerResumeAsync"/> to reset stale handles/static caches and resync loops.
/// </summary>
public sealed partial class GuardMonitor(UnifiedRgbOptions options, LightingEngine engine, ILogger<GuardMonitor> logger) : BackgroundService
{
    private const uint DeviceNotifyCallbackFlag = 2;
    private const uint PbtApmResumeSuspend = 0x0007;
    private const uint PbtApmResumeAutomatic = 0x0012;
    private static readonly TimeSpan OsResumeCoalesceWindow = TimeSpan.FromMilliseconds(1500);

    // [LoggerMessage] in .NET 8 needs a field; primary-constructor parameters are not seen by the generator.
    private readonly ILogger _logger = logger;
    private readonly SemaphoreSlim _resumeWake = new(0, 1);
    private int _powerResumeRequested;
    private long _lastOsResumeTimestamp;

    public override void Dispose()
    {
        base.Dispose();
        _resumeWake.Dispose();
    }

    internal void RequestPowerResume()
    {
        Interlocked.Exchange(ref _powerResumeRequested, 1);
        try
        {
            _resumeWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal Task<bool> OnPowerResumeAsync(CancellationToken ct)
    {
        LogPowerResumeRecovery();
        return engine.OnPowerResumeAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = Math.Max(1, options.GuardRescanSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        var powerNotifications = false;
        nint suspendResumeRegistration = 0;
        DeviceNotifyCallback? s0ixCallback = null;
        Task<bool>? tickTask = null;
        Task? wakeTask = null;
        try
        {
            try
            {
                SystemEvents.PowerModeChanged += PowerModeChanged;
                powerNotifications = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                LogPowerNotificationsUnavailable(ex);
            }

            try
            {
                s0ixCallback = OnSuspendResumeCallback;
                var parameters = new DeviceNotifySubscribeParameters
                {
                    Callback = Marshal.GetFunctionPointerForDelegate(s0ixCallback),
                    Context = 0,
                };
                if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallbackFlag, ref parameters, out suspendResumeRegistration) != 0)
                {
                    suspendResumeRegistration = 0;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                suspendResumeRegistration = 0;
                LogS0ixNotificationsUnavailable(ex);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                tickTask ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                wakeTask ??= _resumeWake.WaitAsync(stoppingToken);
                var completed = await Task.WhenAny(tickTask, wakeTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, wakeTask))
                {
                    await wakeTask.ConfigureAwait(false);
                    wakeTask = null;
                }
                else
                {
                    if (!await tickTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    tickTask = null;
                    tick++;
                }

                try
                {
                    if (Interlocked.Exchange(ref _powerResumeRequested, 0) != 0)
                    {
                        await OnPowerResumeAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (engine.Adapters.RescanRequested)
                    {
                        await engine.RescanAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (tick > 0 && tick % every == 0)
                    {
                        await engine.RecheckAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
#pragma warning disable CA1031 // the monitor must keep running; a failed pass is retried on the next tick
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogPassFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            if (suspendResumeRegistration != 0)
            {
                try
                {
                    _ = PowerUnregisterSuspendResumeNotification(suspendResumeRegistration);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
                {
                }
            }

            GC.KeepAlive(s0ixCallback);
            if (powerNotifications) SystemEvents.PowerModeChanged -= PowerModeChanged;
        }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume)
        {
            RequestPowerResumeFromOs();
        }
    }

    private uint OnSuspendResumeCallback(nint context, uint type, nint setting)
    {
        if (type is PbtApmResumeSuspend or PbtApmResumeAutomatic)
        {
            RequestPowerResumeFromOs();
        }

        return 0;
    }

    private void RequestPowerResumeFromOs()
    {
        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Read(ref _lastOsResumeTimestamp);
        if (prev != 0 && Stopwatch.GetElapsedTime(prev, now) < OsResumeCoalesceWindow)
        {
            return;
        }

        Interlocked.Exchange(ref _lastOsResumeTimestamp, now);
        RequestPowerResume();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint DeviceNotifyCallback(nint context, uint type, nint setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out nint registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(nint registrationHandle);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Warning, Message = "System resume notifications are unavailable; periodic guard checks remain active")]
    private partial void LogPowerNotificationsUnavailable(Exception ex);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Debug, Message = "S0ix suspend/resume callback registration is unavailable; falling back to SystemEvents")]
    private partial void LogS0ixNotificationsUnavailable(Exception ex);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Information, Message = "Power/S0ix resume detected; clearing static caches, reopening direct adapters and resyncing loops")]
    private partial void LogPowerResumeRecovery();

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "WriterGuard re-scan failed")]
    private partial void LogPassFailed(Exception ex);
}

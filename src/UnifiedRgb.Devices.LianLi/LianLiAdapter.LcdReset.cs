using System.Diagnostics;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>
/// Outcome of one manual LCD-group reset. <see cref="Accepted"/> means a FRESH receiver report (advanced RF counter) carried
/// exactly the sent command sequence; sends alone are never acceptance, and acceptance is not proof the screens came back.
/// PWM bytes are the receiver's report before and after; this operation never writes them.
/// </summary>
public sealed record LcdGroupResetResult(
    bool Accepted, int Attempts, byte SlaveIndex, byte PreviousSequence, byte Sequence, string PwmBefore, string? PwmAfter, string Detail);

public sealed partial class LianLiAdapter
{
    /// <summary>At most this many identical-sequence RF 22 packets per manual reset.</summary>
    public const int LcdResetMaxAttempts = 3;

    /// <summary>Wait after each RF 22 send before the receiver is queried (and before the next identical send).</summary>
    public static readonly TimeSpan LcdResetAttemptGap = TimeSpan.FromMilliseconds(200);

    /// <summary>Longest wait for the feedback observation and an in-flight upload/keep-alive before giving up (no send).</summary>
    public static readonly TimeSpan LcdResetGateWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The no-I/O precondition of <see cref="ResetLcdGroupAsync"/>: wire format verified, a verified receiver query bound,
    /// exactly one open sender, <paramref name="id"/> open in this adapter (owned by our writer) and configured as a TL chain of
    /// exactly <paramref name="fans"/> = 3 fans. Throws <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/>.
    /// </summary>
    public void ValidateLcdGroupReset(ControllerId id, int fans)
    {
        if (fans != 3)
        {
            throw new ArgumentOutOfRangeException(nameof(fans), fans, "Only the verified three-fan TL LCD group can be reset.");
        }

        if (!_options.WireFormatVerified)
        {
            throw new InvalidOperationException("Lian Li wire format is not verified (LianLi:WireFormatVerified=false); no RF command is sent.");
        }

        if (_receiverProbe is null || !Guid.TryParse(_options.ReceiverInterfaceGuid, out var guid) || guid == Guid.Empty)
        {
            throw new InvalidOperationException("No verified RF receiver query is bound; the LCD group command cannot be acknowledged.");
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                throw new InvalidOperationException($"{id} is not open in this Host (not owned by our writer); no RF command is sent.");
            }

            if (_senders.Count != 1)
            {
                throw new InvalidOperationException($"Exactly one open RF sender is required; {_senders.Count} are open.");
            }

            if (s.Device.Assembler.Family != "TL" || s.Device.Assembler.UnitsIn(s.Device.LedCount) != fans)
            {
                throw new ArgumentException($"{id} is configured as {s.Device.Assembler.Family} with {s.Device.Assembler.UnitsIn(s.Device.LedCount)} unit(s), not a {fans}-fan TL group.", nameof(id));
            }
        }
    }

    /// <summary>
    /// Manual LCD-group reset (<see cref="RfLcdGroupReset"/>). Runs on a worker thread. Holds the receiver-feedback gate (so the
    /// periodic observation neither interleaves its query nor sees our replies) and the sender's <see cref="RfSender.UploadGate"/>
    /// (so no upload, retry, keep-alive query or ClockSync interleaves), each waited for at most <see cref="LcdResetGateWait"/>.
    /// Reads the sender's master (USB 17) and one COMPLETE receiver list, pins the live target and its rfList index, then sends
    /// at most <see cref="LcdResetMaxAttempts"/> identical packets through the sender's firewalled chain, each followed by
    /// <see cref="LcdResetAttemptGap"/> and a receiver query; it stops at the first fresh report of the new sequence, when the
    /// sequence moves to another value, or when the reported PWM differs. No PWM, bind, save, scene or effect packet is sent.
    /// <paramref name="beforeFirstSend"/> runs once immediately before the first RF 22 frame (the caller's cooldown starts
    /// there); failures before it consume nothing and throw. Failures after it return a non-accepted result.
    /// </summary>
    public Task<LcdGroupResetResult> ResetLcdGroupAsync(ControllerId id, int fans, Action? beforeFirstSend, CancellationToken ct)
    {
        ValidateLcdGroupReset(id, fans);
        return Task.Run(() => ResetLcdGroup(id, fans, beforeFirstSend, ct), ct);
    }

    private LcdGroupResetResult ResetLcdGroup(ControllerId id, int fans, Action? beforeFirstSend, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (!Monitor.TryEnter(_feedbackGate, 50))
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.Elapsed > LcdResetGateWait)
            {
                throw new TimeoutException("The receiver feedback query stayed busy; no RF command was sent.");
            }
        }

        try
        {
            ValidateLcdGroupReset(id, fans); // a close/reopen may have raced the first check
            Session s;
            lock (_gate)
            {
                s = _sessions[id];
            }

            var sender = s.Sender;
            if (!sender.UploadGate.Wait(LcdResetGateWait, ct))
            {
                throw new TimeoutException("An RF upload stayed in flight; no RF command was sent.");
            }

            try
            {
                return ResetHeld(s, fans, beforeFirstSend, ct);
            }
            finally
            {
                sender.UploadGate.Release();
            }
        }
        finally
        {
            Monitor.Exit(_feedbackGate);
        }
    }

    // Caller holds _feedbackGate and the sender's UploadGate.
    private LcdGroupResetResult ResetHeld(Session s, int fans, Action? beforeFirstSend, CancellationToken ct)
    {
        var observationClock = Stopwatch.StartNew();
        var probe = _receiverProbe!;
        var master = s.Sender.ReadMaster().Mac; // fresh USB 17 under the gate: the same query the keep-alive makes
        ct.ThrowIfCancellationRequested();
        var before = RfLcdGroupReset.Resolve(RfReceiverReport.ParseComplete(probe()), s.Device.Mac, master, s.Device.Address, fans);
        var previous = before.Report.Device.CmdSeq;
        var sequence = RfLcdGroupReset.NextSequence(previous);
        var pwmBefore = Convert.ToHexString(before.Report.Device.Pwm);
        var packet = RfLcdGroupReset.Packet(s.Device.Mac, master, s.Device.Address, before.SlaveIndex, sequence);
        ct.ThrowIfCancellationRequested();

        LogLcdReset(s.Device.Mac.ToString(), before.SlaveIndex, previous, sequence);
        beforeFirstSend?.Invoke();
        var attempts = 0;
        string? pwmAfter = null;
        var detail = "no fresh receiver report after the RF 22 sends";
        try
        {
            while (attempts < LcdResetMaxAttempts)
            {
                attempts++;
                s.Sender.Relay(s.Device.Address, packet); // OpcodeFirewall → FrameValidator, packet budget; same 4×64 relay as every RF packet
                if (ct.WaitHandle.WaitOne(LcdResetAttemptGap))
                {
                    detail = "cancelled after the send";
                    break;
                }

                RfReceiverReport? report;
                try
                {
                    report = RfReceiverReport.ParseComplete(probe()).SingleOrDefault(r => r.Device.Mac == s.Device.Mac);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
                {
                    detail = "receiver query failed: " + ex.Message;
                    continue;
                }

                if (report is null || report.Device.MasterMac != master || report.Device.Address != s.Device.Address)
                {
                    detail = "the target was missing from, or moved in, the receiver list";
                    continue;
                }

                pwmAfter = Convert.ToHexString(report.Device.Pwm);
                if (pwmAfter != pwmBefore)
                {
                    detail = $"reported PWM changed {pwmBefore} -> {pwmAfter} (this command never writes PWM); stopped";
                    break;
                }

                var counterDelta = unchecked(report.Counter - before.Report.Counter);
                if (counterDelta == 0 || counterDelta > (observationClock.Elapsed.TotalSeconds + 2) * RfSender.ClockTicksPerSecond)
                {
                    detail = "stale or out-of-order receiver report (RF counter did not advance within its clock bound)";
                    continue;
                }

                if (report.Device.CmdSeq == sequence)
                {
                    return new LcdGroupResetResult(true, attempts, before.SlaveIndex, previous, sequence, pwmBefore, pwmAfter,
                        $"receiver reported command sequence {sequence} (was {previous}) after {attempts} send(s); PWM unchanged");
                }

                if (report.Device.CmdSeq != previous)
                {
                    detail = $"receiver reported command sequence {report.Device.CmdSeq}, neither {previous} nor {sequence}: another sender is active; stopped";
                    break;
                }

                detail = $"receiver still reports command sequence {previous}";
            }
        }
#pragma warning disable CA1031 // after the first send every failure is a non-accepted RESULT (the cooldown already started)
        catch (Exception ex) when (ex is not ForbiddenOpcodeException and not OperationCanceledException)
#pragma warning restore CA1031
        {
            detail = "RF send failed: " + ex.Message;
        }

        ct.ThrowIfCancellationRequested();
        return new LcdGroupResetResult(false, attempts, before.SlaveIndex, previous, sequence, pwmBefore, pwmAfter, detail);
    }

    [LoggerMessage(EventId = 5009, Level = LogLevel.Warning, Message = "RF 22 LCD-group reset of {Mac} (rfList index {Index}, command sequence {Previous} -> {Sequence}) requested manually")]
    private partial void LogLcdReset(string mac, byte index, byte previous, byte sequence);
}

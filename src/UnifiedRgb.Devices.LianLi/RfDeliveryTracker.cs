using System.Buffers.Binary;
using UnifiedRgb.Devices.LianLi.Protocol;

namespace UnifiedRgb.Devices.LianLi;

/// <summary>A complete receiver report, including its RF counter, not merely the USB receipt time.</summary>
public sealed record RfReceiverReport(RfDeviceRecord Device, uint Counter)
{
    public static IReadOnlyList<RfReceiverReport> ParseComplete(ReadOnlySpan<byte> reply)
    {
        // Header bytes2/3 alternate between firmware version (bit7 set) and PWM counters in MasterDevice.RefreshList.
        // They are NOT an error code or reserved zeroes. Observe neither field as a control command.
        if (reply.Length < 4 || reply[0] != 16 || reply[1] > 10 || reply.Length < 4 + reply[1] * RfDeviceRecord.Length)
        { throw new InvalidDataException("Incomplete or unsupported RF receiver list."); }
        var records = RfDeviceRecord.ParseList(reply);
        if (records.Count != reply[1] || records.Select(r => r.Mac).Distinct().Count() != records.Count)
        { throw new InvalidDataException("Invalid or duplicate RF receiver records."); }
        var result = new RfReceiverReport[records.Count];
        for (var i = 0; i < result.Length; i++)
        { result[i] = new(records[i], BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(4 + i * RfDeviceRecord.Length + 14, 4))); }
        return result;
    }
}

/// <summary>Per-session/upload evidence and finite retry budget. A token match never certifies optical playback.</summary>
public sealed class RfDeliveryTracker(uint expected, RfMac mac, RfMac master, RfAddress address, TimeSpan uploadedElapsed)
{
    private RfReceiverReport? _previous;
    private TimeSpan? _previousElapsed;
    private TimeSpan? _mismatchSince;
    private int _mismatchSamples;
    private TimeSpan _lastRetry = -TimeSpan.FromDays(1);
    private bool _blocked;
    private string? _failure;

    public RfDeliveryStatus Status { get; private set; } = new("HostWritten", expected, null, null, null, 0,
        "USB write completed; receiver observation pending.");

    public void Unknown(string reason)
    {
        _previous = null;
        _mismatchSamples = 0;
        Status = Status with { State = "Unknown", Detail = reason };
    }

    public bool Observe(RfReceiverReport? report, DateTimeOffset at, bool allowRetry, TimeSpan elapsed)
    {
        if (elapsed <= uploadedElapsed || report is null || report.Device.Mac != mac || report.Device.MasterMac != master || report.Device.Address != address)
        { Unknown("Missing or wrong receiver identity/address."); return false; }
        var seconds = _previousElapsed is { } previous ? (elapsed - previous).TotalSeconds : 0;
        var delta = _previous is null ? 0 : unchecked(report.Counter - _previous.Counter);
        var fresh = _previous is not null && seconds > 0 && seconds <= 5 && delta > 0 && delta <= (seconds + 2) * RfSender.ClockTicksPerSecond;
        _previous = report;
        _previousElapsed = elapsed;
        Status = Status with { ReportedEffectIndex = report.Device.EffectIndex, ReceiverCounter = report.Counter, ObservedAt = at };
        if (!fresh)
        {
            _mismatchSamples = 0;
            Status = Status with { State = "Unknown", Detail = "Waiting for an advancing receiver counter." };
            return false;
        }
        if (report.Device.EffectIndex == expected)
        {
            _mismatchSamples = 0;
            Status = Status with { State = "ReportedMatch", Detail = "Fresh receiver generation matches; optical playback is not verified." };
            return false;
        }
        if (_blocked) { Status = Status with { State = "Unknown", Detail = _failure ?? "Recovery blocked after device failure." }; return false; }
        _mismatchSince ??= elapsed;
        _mismatchSamples++;
        var exhausted = Status.Retries >= 2 || elapsed - _mismatchSince > TimeSpan.FromSeconds(12);
        Status = Status with { State = exhausted ? "RetryExhausted" : "ReportedMismatch", Detail = "Fresh receiver still reports another animation generation." };
        return allowRetry && _mismatchSamples >= 2 && CanCommitRetry(elapsed);
    }

    public bool CanCommitRetry(TimeSpan elapsed) => !_blocked && Status.Retries < 2 && _mismatchSince is { } since
        && elapsed >= since && elapsed - since <= TimeSpan.FromSeconds(12)
        && elapsed - uploadedElapsed >= TimeSpan.FromSeconds(2) && elapsed - _lastRetry >= TimeSpan.FromSeconds(2);

    /// <summary>Charge once at the current-generation commit boundary, BEFORE any header goes on the wire.</summary>
    public void Retried(TimeSpan elapsed)
    {
        _lastRetry = elapsed;
        _mismatchSamples = 0;
        Status = Status with { Retries = Status.Retries + 1, State = "RetryPending",
            Detail = "Same generation/payload retry committed; waiting for a fresh receiver report." };
    }

    public void Failed(string reason, bool block)
    {
        _blocked |= block;
        _failure = reason;
        Unknown(reason);
    }

    public RfDeliveryStatus Snapshot(TimeSpan elapsed) => _previousElapsed is { } at && elapsed - at > TimeSpan.FromSeconds(5)
        ? Status with { State = "Unknown", Detail = "Receiver observation is stale." }
        : Status;
}

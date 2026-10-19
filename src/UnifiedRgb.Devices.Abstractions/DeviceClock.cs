using System.Diagnostics;

namespace UnifiedRgb.Devices;

/// <summary>
/// The identity of one device clock as it was opened (NATIVE-RF-CLOCK): <paramref name="Kind"/> names the counter, and
/// <paramref name="Generation"/> is unique per opened source instance, so a reopened sender or a reconnect is a different
/// domain even on the same device path. Two samples belong to one physical clock only when their domains are equal.
/// </summary>
public sealed record DeviceClockDomain(string Kind, long Generation)
{
    private static long s_generation;

    /// <summary>A domain with a process-wide fresh generation.</summary>
    public static DeviceClockDomain Next(string kind) => new(kind, Interlocked.Increment(ref s_generation));

    public override string ToString() => $"{Kind}#{Generation}";
}

/// <summary>
/// One validated reading of a free-running device counter: <see cref="Ticks"/> was in the device's reply that completed at
/// <see cref="Timestamp"/> (<see cref="Stopwatch"/> units). The counter was latched somewhere inside the query
/// [<see cref="QueryStartedTimestamp"/>, <see cref="Timestamp"/>], so <see cref="QueryLatencySeconds"/> bounds the stamp's
/// error. Immutable: a reader always sees counter, stamp and rate of the same reply. A successful query is not a playback ACK.
/// </summary>
public sealed class DeviceClockSample
{
    /// <summary>The counter is an unsigned 32-bit value: it wraps to 0 after 2³² ticks.</summary>
    public const double CounterModulus = 4294967296.0;

    public DeviceClockSample(DeviceClockDomain domain, uint ticks, long timestamp, double ticksPerSecond, long queryStartedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (!(ticksPerSecond > 0) || double.IsInfinity(ticksPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), ticksPerSecond, "The tick rate must be finite and positive.");
        }

        Domain = domain;
        Ticks = ticks;
        Timestamp = timestamp;
        TicksPerSecond = ticksPerSecond;
        QueryStartedTimestamp = Math.Min(queryStartedTimestamp, timestamp);
    }

    public DeviceClockDomain Domain { get; }

    /// <summary>The raw unsigned counter from the reply.</summary>
    public uint Ticks { get; }

    /// <summary>Monotonic host time (Stopwatch ticks) the validated reply completed at.</summary>
    public long Timestamp { get; }

    /// <summary>Monotonic host time (Stopwatch ticks) the query was sent at.</summary>
    public long QueryStartedTimestamp { get; }

    /// <summary>Nominal counter rate (1600 per second for the Lian Li RF sender: 0.625 ms per tick).</summary>
    public double TicksPerSecond { get; }

    /// <summary>Send-to-reply time of the query that produced this sample: the bound on when the counter was latched.</summary>
    public double QueryLatencySeconds => (Timestamp - QueryStartedTimestamp) / (double)Stopwatch.Frequency;

    /// <summary>Host seconds from the reply to <paramref name="nowTimestamp"/> (negative if the sample is newer).</summary>
    public double AgeSeconds(long nowTimestamp) => (nowTimestamp - Timestamp) / (double)Stopwatch.Frequency;

    /// <summary>
    /// The counter extrapolated to <paramref name="nowTimestamp"/> at <see cref="TicksPerSecond"/>, as an unsigned value in
    /// [0, 2³²): the uint32 wrap is applied explicitly, never as a negative signed value. An extrapolation, not a reading.
    /// </summary>
    public double TicksAt(long nowTimestamp)
    {
        var x = Ticks + (AgeSeconds(nowTimestamp) * TicksPerSecond);
        var r = x % CounterModulus;
        return r < 0 ? r + CounterModulus : r;
    }
}

/// <summary>
/// A device clock as the render thread sees it: <see cref="Latest"/> is the newest validated sample, or null when there is
/// none (never read, or the source was closed). Reading it performs no I/O, takes no lock, does not wait and does not
/// allocate.
/// </summary>
public interface IDeviceClock
{
    DeviceClockSample? Latest { get; }
}

/// <summary>
/// Optional adapter capability (NATIVE-RF-CLOCK): the clock of the opened session that plays a controller's uploaded loops.
/// The handle is stable per controller and resolved off the hot path; it follows the controller's current session (null
/// <see cref="IDeviceClock.Latest"/> while closed). Null for a controller the adapter has no device clock for.
/// </summary>
public interface IDeviceClockProvider
{
    IDeviceClock? ClockOf(ControllerId id);
}

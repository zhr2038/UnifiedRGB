namespace UnifiedRgb.Devices;

/// <summary>
/// Optional contract of a native adapter (NATIVE design §2, §6). An adapter implementing it is opened by AdapterHost only
/// when <see cref="IsControlVerified"/> is true; otherwise it is opened solely inside a bring-up session through
/// <see cref="OpenForBringupAsync"/>, its DeviceWriter is never started and no scene frame reaches the device.
/// Every packet still passes the adapter's normal firewall chain; nothing here bypasses it.
/// </summary>
public interface INativeBringup
{
    /// <summary>Probe names this adapter accepts in <see cref="RunProbeAsync"/> (e.g. "fwinfo").</summary>
    IReadOnlyList<string> Probes { get; }

    /// <summary>True once Codex has set the class's ControlVerified gate(s) after E3c; only then may a writer start.</summary>
    bool IsControlVerified(ControllerId id);

    /// <summary>
    /// Status reason while <see cref="IsControlVerified"/> is false, e.g. <c>"MsiGpu control not verified"</c> or, with the
    /// overnight hold active, <c>"MsiGpu control not verified; hold-black active"</c> (overnight addendum §4).
    /// </summary>
    string UnverifiedReason(ControllerId id);

    /// <summary>
    /// Opens the device for probes and markers without starting a writer. Idempotent while a session runs. Sends nothing that
    /// changes a device mode: host/direct mode is entered only by the first marker (overnight addendum §9).
    /// </summary>
    Task OpenForBringupAsync(ControllerId id, CancellationToken ct);

    /// <summary>Runs one named probe; a probe name not in <see cref="Probes"/> throws <see cref="ArgumentException"/>.</summary>
    Task<NativeProbeResult> RunProbeAsync(ControllerId id, string probe, CancellationToken ct);

    /// <summary>
    /// Writes one bounded marker frame (E3c smoke / E3m mapping). A failure mid-way reports the step; the caller then runs
    /// <see cref="ReleaseAsync"/> and ends the session (no automatic retry).
    /// </summary>
    Task<NativeProbeResult> BeginMarkerAsync(ControllerId id, NativeMarker marker, CancellationToken ct);

    /// <summary>Writes black over the last marker's range.</summary>
    Task<NativeProbeResult> EndMarkerAsync(ControllerId id, CancellationToken ct);

    /// <summary>
    /// Runs the class release sequence (GPU 0x06 ← 1, ENE direct off, Aura: nothing in evidence). Idempotent. With the
    /// hold-black option the GPU sends black instead and ENE skips direct-off (overnight addendum §4); the result's
    /// <c>release</c> field says which (<c>done</c>, <c>deferred-hold-black</c>, <c>not-applicable</c>).
    /// </summary>
    Task<NativeProbeResult> ReleaseAsync(ControllerId id, CancellationToken ct);
}

/// <summary>
/// A marker: <paramref name="Count"/> records from <paramref name="Start"/> (GPU index, Aura/ENE LED offset) on
/// <paramref name="Channel"/> (Aura: 0 = onboard, i + 1 = addressable header i; others: 0) set to <paramref name="Color"/>.
/// </summary>
public sealed record NativeMarker(int Start, int Count, Rgb Color, int Channel = 0);

/// <summary>One recorded step of a probe or marker: request bytes, native status, response, wall time.</summary>
/// <param name="Status">Native status verbatim (NVAPI status, HID Win32 error, PawnIO HRESULT); 0 = success.</param>
public sealed record NativeProbeRow(string Step, string RequestHex, int Status, string? ResponseHex, double WallMs, string? Note = null);

/// <summary>Outcome of a probe, marker or release with every row it produced and the decoded fields.</summary>
public sealed record NativeProbeResult(string Probe, bool Ok, string Summary, IReadOnlyList<NativeProbeRow> Rows)
{
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Optional: a Streamed adapter that writes one controller frame as a unit (all zones in one operation set), so its
/// FrameBudget measures complete frames rather than per-zone calls (NATIVE design §9). DeviceWriter calls it once per
/// frame instead of <see cref="IDeviceAdapter.WriteFrame"/> per zone. <paramref name="zones"/> is in descriptor order.
/// Returns false when nothing was sent (budget refused or unchanged cached frame): it is not counted as written.
/// A cache hit is not an error or a budget skip and adds no timing sample. True means an actual normal frame send.
/// </summary>
public interface IWholeFrameAdapter : IDeviceAdapter
{
    bool WriteFrame(ControllerId id, IReadOnlyList<Rgb[]> zones);
}

/// <summary>Optional: an adapter reporting complete-frame timing of a controller (Diagnostics, NATIVE design §9).</summary>
public interface IFrameBudgetSource
{
    Safety.FrameBudgetStats? FrameBudget(ControllerId id);
}

/// <summary>
/// Optional: a Streamed native adapter whose FrameBudget is the single frame-pacing authority of a controller
/// (NATIVE-DYNAMIC-SYNC-FIX §B). DeviceWriter waits out <see cref="DelayUntilNextFrame"/> instead of its own MaxFps grid, then
/// hands the newest frame to the adapter, which still admits it strictly through the same budget.
/// </summary>
public interface IFramePacingSource
{
    /// <summary>
    /// The controller's <see cref="Safety.FrameBudget.DelayUntilNextFrame"/>, from the same budget and member its frames are
    /// admitted by (a shared ENE port budget: the stick's own member). Zero when due, before the first frame, or for an id the
    /// adapter does not drive. Side-effect free; a hint, never admission.
    /// </summary>
    TimeSpan DelayUntilNextFrame(ControllerId id);
}

/// <summary>Helpers shared by the native adapters' aggregate (unmapped) zones (NATIVE design §2).</summary>
public static class AggregateZone
{
    /// <summary>Zone name for an aggregate zone of <paramref name="device"/>: "&lt;device&gt; (aggregate, unmapped)".</summary>
    public static string NameFor(string device) => $"{device} (aggregate, unmapped)";

    /// <summary>The one aggregate Point zone (<see cref="ZoneDescriptor.Mapped"/> = false) of a controller.</summary>
    public static ZoneDescriptor Descriptor(string device, int zoneId = 0) =>
        new(new ZoneId(zoneId), NameFor(device), ZoneLayout.Point) { Mapped = false };

    /// <summary>
    /// Replicates the single sampled colour of an aggregate zone to <paramref name="records"/> physical records. Empty input
    /// is black. The engine samples one colour for a Point zone; every index/LED the family owns shows it.
    /// </summary>
    public static Rgb[] Replicate(ReadOnlySpan<Rgb> sampled, int records)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(records);
        var c = sampled.Length > 0 ? sampled[0] : Rgb.Black;
        var result = new Rgb[records];
        Array.Fill(result, c);
        return result;
    }

    /// <summary>In-place variant of <see cref="Replicate(ReadOnlySpan{Rgb}, int)"/>.</summary>
    public static void Replicate(ReadOnlySpan<Rgb> sampled, Span<Rgb> destination)
    {
        destination.Fill(sampled.Length > 0 ? sampled[0] : Rgb.Black);
    }
}

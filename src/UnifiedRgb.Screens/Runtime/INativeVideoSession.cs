namespace UnifiedRgb.Screens.Runtime;

/// <summary>Cumulative numeric transport facts. No device presentation or decoder completion is implied.</summary>
internal readonly record struct NativeVideoWork(double DataWrite = 0, double DataRead = 0, double OverlayWrite = 0,
    double OverlayRead = 0, double ControlWrite = 0, double ControlRead = 0, double QueueSleep = 0,
    long Reads = 0, long Timeouts = 0, long Zlp = 0, long Matched = 0, long Correlated = 0,
    double FirstRead = 0, double Drain = 0)
{
    public NativeVideoWork Since(NativeVideoWork previous) => new(DataWrite - previous.DataWrite, DataRead - previous.DataRead,
        OverlayWrite - previous.OverlayWrite, OverlayRead - previous.OverlayRead, ControlWrite - previous.ControlWrite,
        ControlRead - previous.ControlRead, QueueSleep - previous.QueueSleep, Reads - previous.Reads,
        Timeouts - previous.Timeouts, Zlp - previous.Zlp, Matched - previous.Matched, Correlated - previous.Correlated,
        FirstRead - previous.FirstRead, Drain - previous.Drain);
}

internal interface INativeVideoSession
{
    long AccessUnits { get; }
    long OverlayWrites { get; }
    int LastQueueDepth { get; }
    long BytesWritten { get; }
    double PacketMs { get; }
    double WriteMs { get; }
    double ReadMs { get; }
    NativeVideoWork Work => default;
    void Begin(byte[] transparentPng);
    void SendFrame(byte[] accessUnit);
    void SendOverlay(byte[] png);
    void Stop();
}

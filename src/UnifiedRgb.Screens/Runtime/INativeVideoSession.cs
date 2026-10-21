namespace UnifiedRgb.Screens.Runtime;

internal interface INativeVideoSession
{
    long AccessUnits { get; }
    long OverlayWrites { get; }
    int LastQueueDepth { get; }
    long BytesWritten { get; }
    double PacketMs { get; }
    double WriteMs { get; }
    double ReadMs { get; }
    void Begin(byte[] transparentPng);
    void SendFrame(byte[] accessUnit);
    void SendOverlay(byte[] png);
    void Stop();
}

using AppOnlyWriter;
using BootReadOnly;

namespace UnifiedRgb.Firmware.Worker;

/// <summary>Below the reviewed BootProtocolSession, so its trace/capacity/flush type checks remain intact.
/// Checks only BETWEEN protocol/native transfers while still read-only; after the destructive barrier
/// the supplied predicate is permanently false. Never cancels an in-flight destructive native request.</summary>
public sealed class SafePreEraseReportIo(IReportIo inner, Func<bool> shouldCancel) : IReportIo
{
    private void Check() { if (shouldCancel()) throw new OperationCanceledException("Cancelled at a safe preerase protocol boundary"); }
    public IoResult Write(byte[] report, int timeoutMs, CancellationToken ct) { Check(); return inner.Write(report, timeoutMs, ct); }
    public IoResult Read(int timeoutMs, CancellationToken ct) { Check(); return inner.Read(timeoutMs, ct); }
    public void Dispose() => inner.Dispose();
}

/// <summary>Fake/protocol seam. Production MUST use SafePreEraseReportIo below BootProtocolSession.</summary>
internal sealed class SafePreEraseProtocol : IBootProtocol, IBootPageProtocol
{
    private readonly IBootProtocol _inner;
    private readonly Func<bool> _shouldCancel;
    public SafePreEraseProtocol(IBootProtocol inner, Func<bool> shouldCancel)
    {
        if (inner is BootProtocolSession) { inner.Dispose(); throw new InvalidOperationException("Native trace checks require cancellation below BootProtocolSession"); }
        if (inner is not IBootPageProtocol) { inner.Dispose(); throw new InvalidOperationException("Official all55 page capability required before any protocol I/O"); }
        _inner = inner; _shouldCancel = shouldCancel;
    }
    private void Check() { if (_shouldCancel()) throw new OperationCanceledException("Cancelled at a safe preerase protocol boundary"); }
    public byte[] QueryVersion(CancellationToken ct) { Check(); return _inner.QueryVersion(ct); }
    public byte[] QueryStatus(CancellationToken ct) { Check(); return _inner.QueryStatus(ct); }
    public byte[] QueryResult(CancellationToken ct) { Check(); return _inner.QueryResult(ct); }
    public void SetAddress(int address, CancellationToken ct) { Check(); _inner.SetAddress(address, ct); }
    public void SetLength32(CancellationToken ct) { Check(); _inner.SetLength32(ct); }
    public void Marker(CancellationToken ct) { Check(); _inner.Marker(ct); }
    public void Stage(ReadOnlySpan<byte> data, CancellationToken ct) { Check(); _inner.Stage(data, ct); }
    public void Request(byte action, CancellationToken ct) { Check(); _inner.Request(action, ct); }
    public void SetLength4096(CancellationToken ct) { Check(); ((IBootPageProtocol)_inner).SetLength4096(ct); }
    public void StagePageChunk(int offset, ReadOnlySpan<byte> data, CancellationToken ct) { Check(); ((IBootPageProtocol)_inner).StagePageChunk(offset, data, ct); }
    public void Dispose() => _inner.Dispose();
}

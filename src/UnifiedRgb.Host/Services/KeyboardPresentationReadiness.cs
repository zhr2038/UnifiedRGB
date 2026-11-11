using UnifiedRgb.Devices.AsusRog;

namespace UnifiedRgb.Host.Services;

internal sealed class KeyboardPresentationNotReadyException(Exception inner)
    : IOException("Fresh read-only keyboard recovery is not ready; no recovery setting was written.", inner);

public sealed partial class KeyboardDisplayService
{
    private static async Task RequireFreshPresentationReadinessAsync(RogWiredOledSession session, IRogOledConnection connection,
        CancellationToken ct)
    {
        if (session.MutationAdmitted) throw new InvalidOperationException("Native readiness must be observed before any session mutation.");
        byte[] reply;
        try { reply = await connection.ExchangeAsync(RogNativeMenuCodec.CapabilityRequest(), false, ct).ConfigureAwait(false); }
        catch (Exception ex) when (connection.ConnectionKind == "wireless"
            && ex is RogOledEnvelopeRefusedException or RogOledTimeoutException or RogOledUnexpectedReplyException)
        { throw new KeyboardPresentationNotReadyException(ex); }
        // Liveness/grammar only. This grants no ARM, image acceptance or renewal; every complete frame still needs its own
        // exact epoch/cookie/READY proof. Malformed capabilities are not a sleeping-peer classification.
        _ = RogNativeMenuCodec.ParseCapability(reply, 0x12, connection.ConnectionKind == "wireless" ? (byte)2 : (byte)1);
    }
}

namespace UnifiedRgb.Devices.AsusRog;

/// <summary>Single OLED actor. Upload/lease readers are joined before any next transaction or restoration.</summary>
public sealed class RogWiredOledSession(IRogOledConnection connection)
{
    public long Frames { get; private set; }
    public long Renewals { get; private set; }
    public byte OriginalBrightness { get; private set; }
    public byte CurrentNativeBrightness { get; private set; }
    public byte VerifiedProfileToken { get; private set; }
    public bool Touched { get; private set; }
    public bool LightingTouched { get; private set; }
    public RogKeyboardNativeState? OriginalLighting { get; private set; }
    public RogKeyboardNativeState? OriginalStaticBank { get; private set; }
    private bool _identityAccepted;
    private byte? _softwareControl;
    private bool _keyOutputActive;

    // Retain the actor's fake-only delay seam; spacing belongs to the real RF connection, wired remains strict/unpaced.
    internal Func<TimeSpan, CancellationToken, Task> KeyReportDelay
    {
        init { if (connection is RogWirelessOledConnection wireless) wireless.KeyReportDelay = value; }
    }

    public Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct) =>
        connection.ExchangeAsync(request, zeroAck, ct);

    public async Task<byte> SnapshotAsync(string firmwareHeader, CancellationToken ct)
    {
        var identity = await ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, ct).ConfigureAwait(false);
        // A new RF owner's first identity can precede its keyboard link: receiver envelope, empty keyboard bytes. Nothing was
        // written and the reply is not accepted; this owner closes and a caller may acquire a fresh one.
        if (!_identityAccepted && connection.ConnectionKind == "wireless" && RogRuntimeCompatibility.HasWirelessIdentityEnvelope(identity)
            && !RogRuntimeCompatibility.WirelessKeyboardIdentityAvailable(identity))
            throw new RogKeyboardNotReadyException("Wireless keyboard identity is unavailable (possibly asleep, switching or disconnected): receiver replied without keyboard firmware before any write; this owner is closed.");
        connection.RequireFirmwareIdentity(identity, firmwareHeader);
        if (identity[9] is < 1 or > 6 || identity[11] < 1 || identity[11] > identity[9])
            throw new InvalidDataException("Keyboard active profile unavailable; no output.");
        VerifiedProfileToken = identity[11];
        if ((await ExchangeAsync(RogWiredOledProtocol.Query(0x21, 0), false, ct).ConfigureAwait(false))[5] != 3)
            throw new InvalidDataException("Stored animation selector changed; no output.");
        if ((await ExchangeAsync(RogWiredOledProtocol.Query(0x23, 2), false, ct).ConfigureAwait(false))[5] != 1)
            throw new InvalidDataException("Keyboard OLED content disabled; preserve user setting.");
        OriginalBrightness = (await ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, ct).ConfigureAwait(false))[5];
        if (OriginalBrightness > 100) throw new InvalidDataException("Unknown original OLED brightness.");
        CurrentNativeBrightness = OriginalBrightness;
        _identityAccepted = true;
        return OriginalBrightness;
    }

    public async Task SnapshotLightingAsync(CancellationToken ct)
    {
        if (!_identityAccepted) throw new InvalidOperationException("Verify the wired device first.");
        var basic = await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        var softwareControl = await ReadSoftwareControlAsync(ct).ConfigureAwait(false);
        OriginalLighting = (await ReadBankAsync(basic[10], ct).ConfigureAwait(false)) with { SoftwareControl = softwareControl };
        OriginalStaticBank = OriginalLighting.EffectType == 0 ? OriginalLighting : await ReadBankAsync(0, ct).ConfigureAwait(false);
        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        LightingTouched = false;
    }

    // Stock GET27/SET74 software control is independent of native lever 71 state. The original nullable flag travels in
    // the existing lighting journal, which the Host saves before attaching the lighting owner or allowing any key frame.
    private static byte[] SoftwareControlPacket(byte command, byte value = 0)
    {
        var packet = new byte[65]; packet[1] = command; packet[5] = value; return packet;
    }

    private static byte SoftwareControlReply(byte[] reply, byte command)
    {
        if (reply.Length != 65 || reply[0] != 0 || reply[1] != command || reply.AsSpan(2, 3).ContainsAnyExcept((byte)0)
            || reply[5] > 1 || reply.AsSpan(6).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Software-control reply is not the stock boolean grammar; no retry.");
        return reply[5];
    }

    private async Task<byte> ReadSoftwareControlAsync(CancellationToken ct)
    {
        var current = SoftwareControlReply(await ExchangeAsync(SoftwareControlPacket(0x27), false, ct).ConfigureAwait(false), 0x27);
        _softwareControl = current;
        return current;
    }

    private async Task SetSoftwareControlAsync(byte desired, CancellationToken ct)
    {
        if (desired > 1) throw new InvalidDataException("Original software-control flag is not boolean.");
        var current = _softwareControl ?? await ReadSoftwareControlAsync(ct).ConfigureAwait(false);
        if (current == desired) return; // Stock 74 toggles only when different; no setter is sent for an unchanged flag.
        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        LightingTouched = true; // The pre-saved journal must survive an uncertain setter or readback.
        try
        {
            var echoed = SoftwareControlReply(await ExchangeAsync(SoftwareControlPacket(0x74, desired), false, ct).ConfigureAwait(false), 0x74);
            if (echoed != desired || await ReadSoftwareControlAsync(ct).ConfigureAwait(false) != desired)
                throw new InvalidDataException("Software-control setter echo/readback differs; no retry.");
        }
        catch { _softwareControl = null; throw; }
    }

    // The stock protocol has no compare-and-set. These checks reject a known
    // changed firmware/profile; they cannot make a subsequent write atomic
    // against a physical profile change between the reply and that write.
    private async Task<byte[]> RequireCurrentScopeAsync(CancellationToken ct)
    {
        if (!_identityAccepted) throw new InvalidOperationException("Verify the wired device first.");
        var basic = await ExchangeAsync(RogWiredOledProtocol.Query(0x12, 0), false, ct).ConfigureAwait(false);
        connection.RequireFirmwareIdentity(basic, connection.FirmwareHeader);
        if (basic[9] is < 1 or > 6 || basic[11] != VerifiedProfileToken || basic[11] > basic[9])
            throw new InvalidDataException("Keyboard firmware/profile scope changed; no setting or lighting write.");
        return basic;
    }

    private async Task<RogKeyboardNativeState> ReadBankAsync(byte effect, CancellationToken ct)
    {
        var payloads = new List<string>();
        for (var i = 0; i < RogProtocol.KeyboardPayloadLengths(effect).Count; i++)
        {
            var reply = await ExchangeAsync(ToWired(RogProtocol.KeyboardNativeQuery(2, effect, i)), false, ct).ConfigureAwait(false);
            if (reply[64] != 0) throw new InvalidDataException("Wired effect tail changed.");
            reply[0] = 2;
            payloads.Add(RogProtocol.ParseKeyboardNativePayload(reply.AsSpan(0, 64), 2, effect, i));
        }
        return new(effect, payloads);
    }

    private static byte[] ToWired(byte[] packet)
    { var result = new byte[65]; packet.CopyTo(result, 0); result[0] = 0; return result; }

    private async Task WriteNativeAsync(RogKeyboardNativeState state, CancellationToken ct)
    {
        if (!_identityAccepted) throw new InvalidOperationException("Verify the wired device first.");
        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        foreach (var raw in RogProtocol.KeyboardNativeReports(2, state))
        {
            var p = ToWired(raw); LightingTouched = true;
            // Measured USB setters echo the ENTIRE payload; RF setters intentionally have different receive behaviour.
            if (!(await ExchangeAsync(p, false, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(p))
                throw new InvalidDataException("Wired lighting payload echo differs; no retry.");
        }
    }

    public async Task RestoreLightingAsync(RogKeyboardNativeState original, RogKeyboardNativeState staticBank, CancellationToken ct)
    {
        if (!connection.SameDevice()) throw new InvalidOperationException("Physical keyboard changed; no lighting recovery sent.");
        if (staticBank.EffectType != 0) throw new InvalidDataException("Original static bank missing.");
        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        // Legacy journals lack this field: leave their unknown flag unchanged rather than guessing 0 or 1.
        if (original.SoftwareControl is { } softwareControl) await SetSoftwareControlAsync(softwareControl, ct).ConfigureAwait(false);
        await WriteNativeAsync(staticBank, ct).ConfigureAwait(false);
        if (original.EffectType != 0) await WriteNativeAsync(original, ct).ConfigureAwait(false);
        var basic = await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        if (basic[10] != original.EffectType || !(await ReadBankAsync(original.EffectType, ct).ConfigureAwait(false)).SameContent(original)
            || !(await ReadBankAsync(0, ct).ConfigureAwait(false)).SameContent(staticBank))
            throw new InvalidDataException("Original wired lighting bank readback differs.");
        if (original.SoftwareControl is { } expected && await ReadSoftwareControlAsync(ct).ConfigureAwait(false) != expected)
            throw new InvalidDataException("Original software-control flag readback differs.");
        _keyOutputActive = false;
    }

    public async Task StartOutputAsync(byte brightness, CancellationToken ct)
    {
        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        Touched = true; // Missing acknowledgement must still trigger bounded recovery.
        await ExchangeAsync(RogWiredOledProtocol.Preset(3), true, ct).ConfigureAwait(false);
        await SetBrightnessAsync(brightness, ct).ConfigureAwait(false);
    }

    private async Task SetBrightnessAsync(byte brightness, CancellationToken ct)
    {
        var p = RogWiredOledProtocol.Brightness(brightness);
        if (!(await ExchangeAsync(p, false, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(p))
            throw new InvalidDataException("OLED brightness echo differs.");
        if ((await ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, ct).ConfigureAwait(false))[5] != brightness)
            throw new InvalidDataException("OLED brightness readback differs.");
    }

    public async Task RestoreAsync(byte brightness, CancellationToken ct)
    {
        if (!connection.SameDevice()) throw new InvalidOperationException("Physical keyboard changed; recovery not sent.");
        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
        await ExchangeAsync(RogWiredOledProtocol.Preset(3), true, ct).ConfigureAwait(false);
        await SetBrightnessAsync(brightness, ct).ConfigureAwait(false);
        if ((await ExchangeAsync(RogWiredOledProtocol.Query(0x21, 0), false, ct).ConfigureAwait(false))[5] != 3)
            throw new InvalidDataException("OLED original selector readback differs.");
        CurrentNativeBrightness = brightness;
    }

    public async Task RunAsync(Func<byte[]> latest, Action acknowledged, CancellationToken ct,
        RogWiredLightingBridge.Owner? lighting = null, bool display = true, Func<bool>? displayEnabled = null,
        byte brightness = 100, Action<bool>? displayChanged = null,
        Func<CancellationToken, Task<byte>>? restoreNativeGoal = null,
        Func<byte, CancellationToken, Task>? nativeBrightnessChanged = null,
        Func<CancellationToken, Task<bool>>? nativeTick = null, Func<bool>? nativeWake = null,
        Func<CancellationToken, Task>? nativeStopped = null)
    {
        byte[]? sent = null;
        var nativeWasActive = false;
        // PER-KEY: service complete key frames only on this actor's owned route.
        if (lighting is not null && RogPerKeyLighting.TransportFor(connection.ConnectionKind) is { } keyTransport) lighting.AcceptKeyFrames(keyTransport);
        try
        {
        while (!ct.IsCancellationRequested)
        {
            // This boundary follows the completed prior lease/read/upload. Native code borrows THIS actor only.
            var nativeActive = nativeTick is not null && await nativeTick(ct).ConfigureAwait(false);
            var desired = displayEnabled?.Invoke() ?? display;
            if (!nativeActive && (desired != display || nativeWasActive))
            {
                if (desired) await StartOutputAsync(brightness, ct).ConfigureAwait(false);
                else
                {
                    var goal = restoreNativeGoal is null ? OriginalBrightness : await restoreNativeGoal(ct).ConfigureAwait(false);
                    await RestoreAsync(goal, ct).ConfigureAwait(false);
                }
                display = desired; sent = null; displayChanged?.Invoke(display);
            }
            var pixels = latest();
            if (!nativeActive && display && (sent is null || !pixels.AsSpan().SequenceEqual(sent)))
            {
                await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
                using var upload = CancellationTokenSource.CreateLinkedTokenSource(ct); upload.CancelAfter(8000);
                foreach (var p in RogWiredOledProtocol.Pack(pixels, connection.PixelPayloadCapacity)) await ExchangeAsync(p, true, upload.Token).ConfigureAwait(false);
                sent = (byte[])pixels.Clone(); Frames++; acknowledged();
            }
            if (lighting is not null && lighting.TryTake(out var work) && work is not null)
            {
                if (!work.Cancelled)
                {
                    try
                    {
                        var original = OriginalLighting ?? throw new InvalidOperationException("Wired lighting not snapshotted.");
                        var parameters = Convert.FromHexString(original.Payloads[0]);
                        if (_keyOutputActive && original.SoftwareControl is { } softwareControl)
                            await SetSoftwareControlAsync(softwareControl, ct).ConfigureAwait(false);
                        await WriteNativeAsync(RogProtocol.KeyboardColourFrame(work.Colour, parameters[1], parameters[0]), ct).ConfigureAwait(false);
                        _keyOutputActive = false;
                        work.Completion.TrySetResult();
                    }
                    catch (Exception ex) { work.Completion.TrySetException(ex); throw; }
                }
                else work.Completion.TrySetCanceled();
            }
            if (lighting is not null && lighting.TryTakeKeys(out var keys) && keys is not null)
            {
                if (!keys.Cancelled)
                {
                    try
                    {
                        _ = OriginalLighting ?? throw new InvalidOperationException("Wired lighting not snapshotted.");
                        await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
                        _keyOutputActive = true;
                        await SetSoftwareControlAsync(1, ct).ConfigureAwait(false);
                        LightingTouched = true; // An unknown reply must still trigger the original-lighting restore.
                        await connection.WriteLightingFrameAsync(keys.Keys, ct).ConfigureAwait(false);
                        keys.Completion.TrySetResult();
                    }
                    catch (Exception ex) { keys.Completion.TrySetException(ex); throw; }
                }
                else keys.Completion.TrySetCanceled();
            }
            if (!nativeActive && !display)
            {
                await RequireCurrentScopeAsync(ct).ConfigureAwait(false);
                var current = (await ExchangeAsync(RogWiredOledProtocol.Query(0x23, 3), false, ct).ConfigureAwait(false))[5];
                if (current > 100) throw new InvalidDataException("Native brightness observation is invalid.");
                if (current != CurrentNativeBrightness)
                {
                    CurrentNativeBrightness = current;
                    if (current > 0 && nativeBrightnessChanged is not null)
                        await nativeBrightnessChanged(current, ct).ConfigureAwait(false);
                }
            }
            nativeWasActive = nativeActive;
            if (nativeActive || display) await LeaseAsync(() => lighting?.HasWork == true || nativeWake?.Invoke() == true
                || (!nativeActive && (displayEnabled?.Invoke() ?? display) != display)
                || (!nativeActive && !latest().AsSpan().SequenceEqual(sent)), ct).ConfigureAwait(false);
            else if (lighting is not null)
            {
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct); poll.CancelAfter(200);
                try { if (!await lighting.WaitAsync(poll.Token).ConfigureAwait(false)) return; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && poll.IsCancellationRequested) { }
            }
            else await Task.Delay(250, ct).ConfigureAwait(false);
        }
        }
        finally { if (nativeStopped is not null) await nativeStopped(CancellationToken.None).ConfigureAwait(false); }
    }

    private Task LeaseAsync(Func<bool> changed, CancellationToken ct) =>
        connection.LeaseAsync(changed, () => Renewals++, ct);
}

using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class RogNativeMenuSourceTests
{
    [Fact]
    public async Task CapThrottle_thirty_quiet_ticks_reuse_only_the_bounded_observation_without_advancing_RF_proof()
    {
        await using var rig = new CapRig(); await rig.Warm();
        var reads = rig.Caps; var evidence = rig.Link.PairingEvidence;
        for (var i = 0; i < 30; i++)
        {
            var evaluations = 0;
            (await rig.Actor.StepAsync(() => { evaluations++; return null; }, default)).Should().BeFalse();
            evaluations.Should().Be(1); rig.Time.Advance(TimeSpan.FromMilliseconds(3));
        }
        rig.Caps.Should().Be(reads); rig.Link.PairingEvidence.Should().Be(evidence);
        rig.Time.Advance(TimeSpan.FromMilliseconds(10)); // exactly100ms since the actual readonly CAP
        await rig.Actor.StepAsync(() => null, default);
        rig.Caps.Should().Be(reads + 1); rig.Link.PairingEvidence!.Observations.Should().Be(evidence!.Observations + 1);
        // Recheck age after selection too: time spent in a selector cannot make a >100ms observation look current.
        await rig.Actor.StepAsync(() => { rig.Time.Advance(TimeSpan.FromMilliseconds(100)); return null; }, default);
        rig.Caps.Should().Be(reads + 2);
        rig.Io.ImageBegins.Should().Be(0); rig.Io.Arms.Should().Be(1);
    }

    [Fact]
    public async Task CapThrottle_pending_genuine_input_requires_an_actual_CAP_before_any_press_is_emitted()
    {
        await using var rig = new CapRig(); await rig.Warm(); var reads = rig.Caps;
        rig.Input.Send(Notice(1, 0, 3, 2)); rig.Input.Send(Notice(2, 2, 1, 2));
        await rig.Input.WaitReads(2);
        await rig.Actor.StepAsync(() => null, default);
        rig.Caps.Should().Be(reads + 1); rig.Events.OfType<NativeMenuPress>().Should().ContainSingle();
        rig.PressObservedAtCap.Should().Be(reads + 1);
        rig.Io.ImageBegins.Should().Be(0);
    }

    [Fact]
    public async Task CapThrottle_changed_artwork_selects_once_and_fresh_CAP_rejects_a_changed_native_mode_before_upload()
    {
        await using var rig = new CapRig(); await rig.Warm(); var reads = rig.Caps; var evaluations = 0;
        var pixels = Pixels();
        (await rig.Actor.StepAsync(() => { evaluations++; return pixels; }, default)).Should().BeTrue();
        evaluations.Should().Be(1); rig.Caps.Should().Be(reads + 1); rig.Io.Rebuilt.Should().Equal(pixels);
        // Same pixels can use a quiet observation. A different frame must discover the changed mode first.
        rig.Io.Mode = 8; var next = (byte[])pixels.Clone(); next[0]++;
        reads = rig.Caps; evaluations = 0;
        (await rig.Actor.StepAsync(() => { evaluations++; return next; }, default)).Should().BeFalse();
        evaluations.Should().Be(1); rig.Caps.Should().Be(reads + 1); rig.Io.ImageBegins.Should().Be(1);
    }

    [Fact]
    public async Task CapThrottle_stop_retirement_and_future_or_stale_proof_never_borrow_a_cached_CAP()
    {
        await using (var rig = new CapRig())
        {
            await rig.Warm(); var reads = rig.Caps;
            await rig.Actor.StopAsync(default);
            rig.Caps.Should().Be(reads + 1); rig.Io.Disarms.Should().Be(1); rig.Input.LiveHandles.Should().Be(0);
        }
        await using (var rig = new CapRig())
        {
            await rig.Warm(); var reads = rig.Caps;
            rig.Source.RequestEnabled(false); await rig.Actor.StepAsync(() => null, default);
            rig.Caps.Should().Be(reads + 1); rig.Io.Disarms.Should().Be(1);
        }
        foreach (var future in new[] { false, true })
        {
            await using var rig = new CapRig(); await rig.Warm();
            var proof = rig.Link.PairingEvidence!; var reads = rig.Caps;
            if (future) rig.Link.View = proof with { LastObservedTimestamp = rig.Time.GetTimestamp() + rig.Time.TimestampFrequency };
            else { rig.Time.Advance(TimeSpan.FromMinutes(4)); rig.Link.View = proof; }
            (await rig.Actor.StepAsync(() => null, default)).Should().BeFalse();
            rig.Caps.Should().Be(reads + 1); rig.Source.IsPresent.Should().BeFalse();
            rig.Link.PairingEvidence.Should().Be(rig.Link.View, "a skip must never rewrite an actual proof snapshot");
        }
    }

    private sealed class CapRig : IAsyncDisposable
    {
        public ManualTimeProvider Time { get; } = new();
        public Input Input { get; }
        public Connection Io { get; }
        public CapEvidenceOwner Link { get; }
        public RogNativeMenuSource Source { get; }
        public RogNativeMenuSource.Actor Actor { get; }
        public List<NativeMenuSourceEvent> Events { get; } = [];
        public int Caps => Io.Writes.Count(w => w == "12/7D");
        public int PressObservedAtCap { get; private set; }
        private readonly IDisposable _registration;
        public CapRig()
        {
            Input = new(Time, 2); Io = new(Time, 2) { Input = Input }; Link = new(Io, Time);
            ulong cookie = Cookie;
            Source = new(Policy(2), Input, Input, Time, () => cookie++);
            _registration = Source.Connect(e => { Events.Add(e); if (e is NativeMenuPress) PressObservedAtCap = Caps; });
            Actor = Source.Attach(Link); Input.Admitted = () => Actor.ReceivedNotifications; Source.RequestEnabled(true);
        }
        public async Task Warm()
        {
            await Actor.StepAsync(() => null, default); // fresh CAP then actual ARM (new epoch)
            await Actor.StepAsync(() => null, default); // first actual readonly CAP of that new epoch
            Caps.Should().Be(2);
        }
        public async ValueTask DisposeAsync()
        { await Actor.DisposeAsync(); _registration.Dispose(); Input.Dispose(); Io.Dispose(); }
    }

    private sealed class CapEvidenceOwner(Connection inner, TimeProvider time) : IRogOledConnection
    {
        private RogPairingEvidence? _evidence;
        public RogPairingEvidence? View { get; set; }
        public string DeviceKey => inner.DeviceKey;
        public string FirmwareHeader => inner.FirmwareHeader;
        public string ConnectionKind => inner.ConnectionKind;
        public int PixelPayloadCapacity => inner.PixelPayloadCapacity;
        public RogNativeControlBinding? NativeControlBinding => inner.NativeControlBinding;
        public RogPairingEvidence? PairingEvidence => View ?? (_evidence ??= new(NativeControlBinding!.Keyboard!,
            NativeControlBinding.PairingTimestamp, NativeControlBinding.PairingTimestamp, 0, null));
        public bool SameDevice() => inner.SameDevice();
        public void RequireFirmwareIdentity(byte[] identity, string expectedHeader) => inner.RequireFirmwareIdentity(identity, expectedHeader);
        public async Task<byte[]> ExchangeAsync(byte[] request, bool zeroAck, CancellationToken ct)
        {
            var reply = await inner.ExchangeAsync(request, zeroAck, ct);
            if (request[2] == 0x7D && request[1] is 0x12 or 0x71)
            {
                RogNativeMenuCodec.ParseCapability(reply, request[1], 2);
                var old = _evidence ?? PairingEvidence!;
                _evidence = old with { Observations = old.Observations + 1, LastObservedTimestamp = time.GetTimestamp() };
            }
            return reply;
        }
        public Task LeaseAsync(Func<bool> changed, Action renewed, CancellationToken ct) => inner.LeaseAsync(changed, renewed, ct);
        public void Dispose() { }
    }
}

using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using UnifiedRgb.Devices.LianLi.Codec;
using UnifiedRgb.Transports.Smbus;

namespace UnifiedRgb.Devices.Tests;

public sealed class NativePathAndMutexAuditTests
{
    private static readonly byte[] DummyModule = [0xDE, 0xAD];

    [Fact]
    public void Explicit_relative_vendor_codec_path_is_resolved_from_the_application_directory()
    {
        var relative = Path.Combine("codec", "yuz.dll");
        YuzNative.ResolveConfiguredPath(relative).Should().Be(Path.GetFullPath(relative, AppContext.BaseDirectory));
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "user-provided", "yuz.dll"));
        YuzNative.ResolveConfiguredPath(absolute).Should().Be(absolute);
        YuzNative.ResolveConfiguredPath(null).Should().BeNull();
        YuzNative.ResolveConfiguredPath("").Should().BeEmpty();
    }

    [Fact]
    public void Abandoned_bus_ownership_is_observable_without_rejecting_a_valid_windows_acquisition()
    {
        var name = "Local\\urgb-audit-bus-" + Guid.NewGuid().ToString("N");
        using var observed = new GlobalSmbusMutex(name);
        var previous = new Thread(() => { using var mutex = new Mutex(false, name); mutex.WaitOne(); });
        previous.Start(); previous.Join(); // deliberate test-only abandoned owner; never the production SMBus mutex
        observed.Wait(0).Should().BeTrue();
        try { observed.AbandonedAcquisitions.Should().Be(1); }
        finally { observed.Release(); }
        observed.Wait(0).Should().BeTrue();
        try { observed.AbandonedAcquisitions.Should().Be(1); }
        finally { observed.Release(); }
    }

    [Fact]
    public void PawnIoSmbus_execute_selects_port_under_mutex_and_restores_external_port_2()
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 2 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 0, mutex, 50);
        lib.HardwarePort.Should().Be(2, "Open(0) restores prior external port selection before releasing the mutex");
        mutex.Held.Should().BeFalse();

        lib.Calls.Clear();
        lib.HardwarePort = 2; // external monitoring software switched 0xFED80302 to Port 2
        var r = port.Execute(SmbusCompound.RegWrite(0x70, 0x8020, 1));

        r.Ok.Should().BeTrue();
        lib.HardwarePort.Should().Be(2, "prior port 2 is restored before releasing the mutex");
        mutex.Held.Should().BeFalse();
        mutex.Waits.Should().Be(2, "once for Open, once for Execute");
        mutex.Releases.Should().Be(2);

        lib.Calls.Should().HaveCount(4);
        lib.Calls[0].Should().Match<RecordingCall>(c => c.Function == PawnIoSmbus.IoctlPortSelect && c.Input0 == 0 && c.MutexHeld);
        lib.Calls[1].Should().Match<RecordingCall>(c => c.Function == PawnIoSmbus.IoctlXfer && c.PortSeen == 0 && c.MutexHeld);
        lib.Calls[2].Should().Match<RecordingCall>(c => c.Function == PawnIoSmbus.IoctlXfer && c.PortSeen == 0 && c.MutexHeld);
        lib.Calls[3].Should().Match<RecordingCall>(c => c.Function == PawnIoSmbus.IoctlPortSelect && c.Input0 == 2 && c.MutexHeld);
    }

    [Fact]
    public void PawnIoSmbus_execute_skips_restore_when_hardware_is_already_on_configured_port()
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 0 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 0, mutex, 50);
        lib.Calls.Clear();

        var r = port.Execute(SmbusCompound.RegRead(0x70, 0x1000));

        r.Ok.Should().BeTrue();
        lib.Calls.Select(c => c.Function).Should().Equal(
            PawnIoSmbus.IoctlPortSelect,
            PawnIoSmbus.IoctlXfer,
            PawnIoSmbus.IoctlXfer);
        lib.Calls[0].Input0.Should().Be(0);
    }

    [Fact]
    public void PawnIoSmbus_execute_batch_selects_once_runs_all_compounds_and_restores_port_under_one_mutex_hold()
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 0 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 0, mutex, 50);
        lib.Calls.Clear();

        lib.HardwarePort = 2;
        var batch = Enumerable.Range(0, 16)
            .Select(i => SmbusCompound.RegBlock(0x70, (ushort)(0x8100 + (3 * i)), [(byte)i, 0x20, 0x30]))
            .ToArray();

        var results = port.ExecuteBatch(batch);

        results.Should().HaveCount(16).And.OnlyContain(x => x.Ok);
        mutex.Waits.Should().Be(2, "one for Open, one for the entire 16-LED batch");
        mutex.Releases.Should().Be(2);
        mutex.Held.Should().BeFalse();
        lib.HardwarePort.Should().Be(2);

        lib.Calls.Should().HaveCount(1 + (16 * 2) + 1);
        lib.Calls[0].Function.Should().Be(PawnIoSmbus.IoctlPortSelect);
        lib.Calls[0].Input0.Should().Be(0);
        lib.Calls.Skip(1).Take(32).Should().OnlyContain(c => c.Function == PawnIoSmbus.IoctlXfer && c.PortSeen == 0 && c.MutexHeld);
        lib.Calls[^1].Function.Should().Be(PawnIoSmbus.IoctlPortSelect);
        lib.Calls[^1].Input0.Should().Be(2);
        lib.Calls[^1].MutexHeld.Should().BeTrue();
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000001), 1)]
    [InlineData(0, 0)]
    public void PawnIoSmbus_aborts_before_any_smbus_xfer_when_port_select_fails(int hr, int returnedCells)
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 2 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 0, mutex, 50);
        lib.Calls.Clear();

        lib.PortSelectHr = hr;
        lib.PortSelectCells = (nuint)returnedCells;

        var single = port.Execute(SmbusCompound.RegWrite(0x70, 0x8020, 1));
        single.Ok.Should().BeFalse();
        single.Step.Should().Be(PawnIoSmbus.IoctlPortSelect);
        lib.Calls.Should().ContainSingle(c => c.Function == PawnIoSmbus.IoctlPortSelect && c.Input0 == 0);
        lib.HardwarePort.Should().Be(2);
        mutex.Held.Should().BeFalse();

        lib.Calls.Clear();
        var batch = port.ExecuteBatch([SmbusCompound.RegBlock(0x70, 0x8100, [1, 2, 3])]);
        batch.Should().ContainSingle();
        batch[0].Ok.Should().BeFalse();
        batch[0].Step.Should().Be(PawnIoSmbus.IoctlPortSelect);
        lib.Calls.Should().ContainSingle(c => c.Function == PawnIoSmbus.IoctlPortSelect);
        mutex.Held.Should().BeFalse();
    }

    [Fact]
    public void PawnIoSmbus_restores_prior_port_even_when_batch_xfer_fails_mid_transaction()
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 0 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 0, mutex, 50);
        lib.Calls.Clear();

        lib.HardwarePort = 2;
        lib.FailOnXferIndex = 2; // pointer of compound #1 fails
        var batch = new[]
        {
            SmbusCompound.RegBlock(0x70, 0x8100, [1, 2, 3]),
            SmbusCompound.RegBlock(0x70, 0x8103, [4, 5, 6]),
            SmbusCompound.RegBlock(0x70, 0x8106, [7, 8, 9]),
        };

        var results = port.ExecuteBatch(batch);

        results.Should().HaveCount(2);
        results[0].Ok.Should().BeTrue();
        results[1].Ok.Should().BeFalse();
        lib.HardwarePort.Should().Be(2, "prior port is restored in finally even after an SMBus error");
        mutex.Held.Should().BeFalse();
        lib.Calls[^1].Should().Match<RecordingCall>(c => c.Function == PawnIoSmbus.IoctlPortSelect && c.Input0 == 2 && c.MutexHeld);
    }

    [Fact]
    public void PawnIoSmbus_execute_batch_drops_on_mutex_timeout_without_ioctls()
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 2 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 0, mutex, 50);
        lib.Calls.Clear();

        mutex.Acquire = false;
        var results = port.ExecuteBatch([SmbusCompound.RegBlock(0x70, 0x8100, [1, 2, 3])]);

        results.Should().ContainSingle();
        results[0].Ok.Should().BeFalse();
        results[0].Dropped.Should().BeTrue();
        results[0].Step.Should().Be("mutex");
        port.MutexTimeouts.Should().Be(1);
        lib.Calls.Should().BeEmpty();
        mutex.Releases.Should().Be(1, "only Open released");
    }

    [Fact]
    public void GlobalSmbusMutex_creates_permissive_acl_allowing_authenticated_and_interactive_users_synchronize_and_modify()
    {
        var security = GlobalSmbusMutex.CreatePermissiveSecurity();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToList();

        var authSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        rules.Should().Contain(r =>
            r.IdentityReference.Equals(authSid)
            && r.AccessControlType == AccessControlType.Allow
            && (r.MutexRights & (MutexRights.Synchronize | MutexRights.Modify)) == (MutexRights.Synchronize | MutexRights.Modify));
        rules.Should().Contain(r =>
            r.IdentityReference.Equals(interactiveSid)
            && r.AccessControlType == AccessControlType.Allow
            && (r.MutexRights & (MutexRights.Synchronize | MutexRights.Modify)) == (MutexRights.Synchronize | MutexRights.Modify));
        rules.Should().Contain(r =>
            r.IdentityReference.Equals(adminsSid)
            && r.AccessControlType == AccessControlType.Allow
            && (r.MutexRights & MutexRights.FullControl) == MutexRights.FullControl);
        rules.Should().Contain(r =>
            r.IdentityReference.Equals(systemSid)
            && r.AccessControlType == AccessControlType.Allow
            && (r.MutexRights & MutexRights.FullControl) == MutexRights.FullControl);

        var name = "Local\\urgb-audit-acl-" + Guid.NewGuid().ToString("N");
        using var primary = new GlobalSmbusMutex(name);
        MutexAcl.TryOpenExisting(name, MutexRights.Synchronize | MutexRights.Modify | MutexRights.ReadPermissions, out var opened).Should().BeTrue();
        using (opened)
        {
            var kernelRules = opened!.GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<MutexAccessRule>()
                .ToList();
            kernelRules.Should().Contain(r =>
                r.IdentityReference.Equals(authSid)
                && r.AccessControlType == AccessControlType.Allow
                && (r.MutexRights & (MutexRights.Synchronize | MutexRights.Modify)) == (MutexRights.Synchronize | MutexRights.Modify));
            kernelRules.Should().Contain(r =>
                r.IdentityReference.Equals(interactiveSid)
                && r.AccessControlType == AccessControlType.Allow
                && (r.MutexRights & (MutexRights.Synchronize | MutexRights.Modify)) == (MutexRights.Synchronize | MutexRights.Modify));

            opened!.WaitOne(0).Should().BeTrue();
            opened.ReleaseMutex();
        }
    }

    [Fact]
    public void GlobalSmbusMutex_falls_back_safely_when_mutex_exists_without_full_control_or_acl_creation_fails()
    {
        // Fallback when ACL creation fails.
        var fallbackName = "Local\\urgb-audit-acl-fallback-" + Guid.NewGuid().ToString("N");
        using (var fallback = new GlobalSmbusMutex(fallbackName, () => throw new PlatformNotSupportedException("simulated ACL creation failure")))
        {
            fallback.Wait(0).Should().BeTrue();
            fallback.Release();
        }

        // Fallback when mutex already exists granting Synchronize | Modify (not FullControl / Delete).
        var restrictedName = "Local\\urgb-audit-restricted-" + Guid.NewGuid().ToString("N");
        var restrictedSecurity = new MutexSecurity();
        restrictedSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restrictedSecurity.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            MutexRights.Synchronize | MutexRights.Modify,
            AccessControlType.Allow));
        using var creator = MutexAcl.Create(false, restrictedName, out var createdNew, restrictedSecurity);
        createdNew.Should().BeTrue();

        var fullAccess = () => MutexAcl.OpenExisting(restrictedName, MutexRights.FullControl);
        fullAccess.Should().Throw<UnauthorizedAccessException>("FullControl is denied by the Synchronize | Modify DACL");

        using var opened = new GlobalSmbusMutex(restrictedName);
        opened.Wait(0).Should().BeTrue();
        opened.Release();
    }

    [Fact]
    public void PawnIoSmbus_port_1_verifies_port_select_without_altering_primary_mux_register()
    {
        var mutex = new RecordingMutex();
        var lib = new RecordingLibrary(mutex) { HardwarePort = 2 };
        using var port = PawnIoSmbus.Open(lib, DummyModule, 1, mutex, 50);
        lib.HardwarePort.Should().Be(2, "port 1 uses aux base 0x0B20 and never modifies primary 0xFED80302");
        lib.Calls.Clear();

        var r = port.Execute(SmbusCompound.RegWrite(0x70, 0x8020, 1));

        r.Ok.Should().BeTrue();
        lib.HardwarePort.Should().Be(2);
        lib.Calls.Select(c => c.Function).Should().Equal(
            PawnIoSmbus.IoctlPortSelect,
            PawnIoSmbus.IoctlXfer,
            PawnIoSmbus.IoctlXfer);
        lib.Calls[0].Input0.Should().Be(1UL);
        lib.Calls[0].MutexHeld.Should().BeTrue();
    }

    private sealed record RecordingCall(string Function, ulong Input0, ulong PortSeen, bool MutexHeld);

    private sealed class RecordingMutex : ISmbusMutex
    {
        public bool Acquire { get; set; } = true;

        public bool Held { get; private set; }

        public int Waits { get; private set; }

        public int Releases { get; private set; }

        public bool Wait(int milliseconds)
        {
            Waits++;
            if (!Acquire)
            {
                return false;
            }

            Held = true;
            return true;
        }

        public void Release()
        {
            Held = false;
            Releases++;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLibrary(RecordingMutex mutex) : IPawnIoLibrary
    {
        private int _xferIndex;
        private ulong _smbaPort;

        public ulong HardwarePort { get; set; }

        public int PortSelectHr { get; set; }

        public nuint PortSelectCells { get; set; } = 1;

        public int FailOnXferIndex { get; set; } = -1;

        public List<RecordingCall> Calls { get; } = [];

        public int Version(out uint version)
        {
            version = 0x00020000;
            return 0;
        }

        public int Open(out IntPtr handle)
        {
            handle = 0x1234;
            return 0;
        }

        public int Load(IntPtr handle, ReadOnlySpan<byte> blob) => 0;

        public int Execute(IntPtr handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output, out nuint returned)
        {
            var arg0 = input.Length > 0 ? input[0] : 0;
            Calls.Add(new RecordingCall(function, arg0, HardwarePort, mutex.Held));
            switch (function)
            {
                case PawnIoSmbus.IoctlIdentity:
                    output[0] = 0x3458494950;
                    output[1] = 0xB00;
                    output[2] = 0x1022UL | (0x790BUL << 16);
                    returned = 3;
                    return 0;
                case PawnIoSmbus.IoctlPortSelect:
                    if (PortSelectHr != 0)
                    {
                        returned = 0;
                        return PortSelectHr;
                    }

                    if (PortSelectCells != 1)
                    {
                        returned = PortSelectCells;
                        return 0;
                    }

                    if (arg0 == 1)
                    {
                        output[0] = _smbaPort == 1 ? 1UL : HardwarePort;
                        _smbaPort = 1;
                    }
                    else
                    {
                        output[0] = _smbaPort == 1 ? 1UL : HardwarePort;
                        HardwarePort = arg0;
                        _smbaPort = 0;
                    }

                    returned = 1;
                    return 0;
                case PawnIoSmbus.IoctlSleepMode:
                    returned = 0;
                    return 0;
                default:
                    var idx = _xferIndex++;
                    if (idx == FailOnXferIndex)
                    {
                        returned = 0;
                        return unchecked((int)0x8007045D);
                    }

                    if (output.Length > 0)
                    {
                        output[0] = 0x42;
                    }

                    returned = (nuint)output.Length;
                    return 0;
            }
        }

        public int Close(IntPtr handle) => 0;
    }
}

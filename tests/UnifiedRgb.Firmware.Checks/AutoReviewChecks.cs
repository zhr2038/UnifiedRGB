using System.Text.Json;
using BootReadOnly;
using UnifiedRgb.Firmware.Worker;

namespace UnifiedRgb.Firmware.Checks;

internal static class AutoReviewChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var count = 0;
        void Check(bool result, string label) { check(result, label); count++; }
        void Deny(IReadOnlyList<Collection> rows, string label)
        {
            try { BootEntryMetadataSelector.Select(rows, AutoEntryChecks.Target.PhysicalLocation, DateTimeOffset.UtcNow); }
            catch (InvalidDataException) { Check(true, label); return; }
            throw new InvalidOperationException("Expected metadata selection denial: " + label);
        }
        var expected = AutoEntryChecks.Boot;
        var vendor = new Collection(expected.HidPath, expected.HidInstanceId, expected.UsbAncestor, [expected.PhysicalLocation], expected.ContainerId,
            0x0B05, 0x1C26, 0xFF00, 1, 65, 65, [0], [0], [], expected.CapsSha256, null);
        var keyboard = vendor with { Path = "\\\\?\\hid#vid_0b05&pid_1c26&mi_00#keyboard", InstanceId = "HID\\VID_0B05&PID_1C26&MI_00\\keyboard",
            UsagePage = 1, Usage = 6, InputLength = 8, OutputLength = 1 };
        var mouse = vendor with { Path = "\\\\?\\hid#vid_0b05&pid_1c26&mi_02#mouse", InstanceId = "HID\\VID_0B05&PID_1C26&MI_02\\mouse",
            UsagePage = 0, Usage = 0, InputLength = 0, OutputLength = 0, InputIds = [], OutputIds = [], CapsSha256 = "",
            Error = "Access0 denied5", Access0ErrorCode = 5, DeviceClassGuid = "4d36e96f-e325-11ce-bfc1-08002be10318",
            HardwareIds = ["HID_DEVICE_SYSTEM_MOUSE", "HID_DEVICE_UP:0001_U:0002"] };
        Check(vendor.Eligible && !keyboard.Eligible && !mouse.Eligible && mouse.PositivelyIdentifiedOsMouse,
            "review fixtures distinguish vendor, readable keyboard and positively identified access0-denied mouse");
        Check(BootEntryMetadataSelector.Select([], expected.PhysicalLocation, DateTimeOffset.UtcNow) is null,
            "empty native metadata snapshot has no boot role");
        foreach (var rows in new Collection[][] { [vendor], [vendor, keyboard], [vendor, mouse], [mouse, keyboard, vendor] })
        {
            var at = DateTimeOffset.UtcNow;
            var selected = BootEntryMetadataSelector.Select(rows, expected.PhysicalLocation, at)!;
            Check(selected.HidPath == vendor.Path && selected.ContainerId == vendor.ContainerId && selected.UsbAncestor == vendor.UsbAncestor &&
                selected.ContainerId != AutoEntryChecks.Target.ContainerId && selected.UsbAncestor != AutoEntryChecks.Target.UsbAncestor && selected.ObservedAtUtc == at,
                "native pure selector accepts unique vendor with readable/nonvendor and proven same-physical mouse; preserves actual boot identity");
            using var evidence = JsonDocument.Parse(selected.InventoryJson);
            var saved = evidence.RootElement.GetProperty("rows");
            Check(saved.GetArrayLength() == rows.Length && saved.EnumerateArray().Select(row => row.GetProperty("Path").GetString()).SequenceEqual(rows.Select(row => row.Path)),
                "complete inventory rows retained in evidence, not just eligible vendor");
        }
        Deny([keyboard, mouse], "nonempty inventory without eligible vendor rejected");
        Deny([vendor, vendor with { Path = vendor.Path + "-second" }], "ambiguous eligible vendor collections rejected");
        Deny([vendor with { OutputLength = 64 }, keyboard, mouse], "wrong vendor caps rejected");
        Deny([vendor with { LocationPaths = [expected.PhysicalLocation + "#USB(99)"] }], "wrong-port eligible vendor rejected");
        foreach (var badMouse in new[] {
            mouse with { HardwareIds = [] }, mouse with { DeviceClassGuid = "" }, mouse with { Access0ErrorCode = 32 },
            mouse with { Path = mouse.Path.Replace("mi_02", "mi_03", StringComparison.Ordinal) },
            mouse with { InstanceId = "HID\\VID_0B05&PID_1C26&MI_03\\unknown" },
            mouse with { ContainerId = Guid.NewGuid().ToString("D") }, mouse with { UsbAncestor = vendor.UsbAncestor + "-foreign" },
            mouse with { LocationPaths = [expected.PhysicalLocation + "#USB(99)"] }, mouse with { LocationPaths = [] } })
            Deny([vendor, keyboard, badMouse], "FULL unchanged R6 selection rejects unknown/uninspectable/foreign or incomplete mouse exclusion");
        foreach (var foreignReadable in new[] { keyboard with { ContainerId = Guid.NewGuid().ToString("D") }, keyboard with { UsbAncestor = "" },
            keyboard with { LocationPaths = [expected.PhysicalLocation + "#USB(99)"] }, keyboard with { Pid = 0x1C24 } })
            Deny([vendor, foreignReadable, mouse], "foreign/incomplete readable nonvendor collection remains rejected");

        Task<BootObservation> CaptureWatch(EntryMetadataSnapshot snapshot, CancellationToken ct) =>
            BootTransitionWatcher.WatchAsync(AutoEntryChecks.Target, () => snapshot, _ => Task.CompletedTask, ct);
        Task<BootObservation> NativeWatch(IReadOnlyList<Collection> rows, CancellationToken ct) =>
            BootTransitionWatcher.WatchAsync(AutoEntryChecks.Target, () =>
            {
                var boot = BootEntryMetadataSelector.Select(rows, expected.PhysicalLocation, DateTimeOffset.UtcNow);
                return new([], boot is null ? [] : [boot]);
            }, _ => Task.CompletedTask, ct);
        (string Label, Func<NormalObservation, CancellationToken, Task<BootObservation>> Watch)[] initialObservers = [
            ("initial observer fault", (_, _) => Task.FromException<BootObservation>(new IOException("Injected first-capture failure"))),
            ("initial ambiguous boot", (_, ct) => CaptureWatch(new([], [expected, expected]), ct)),
            ("initial wrong-port boot", (_, ct) => CaptureWatch(new([], [expected with { PhysicalLocation = expected.PhysicalLocation + "#USB(99)" }]), ct)),
            ("initial preexisting boot with normal still present", (_, ct) => CaptureWatch(new([AutoEntryChecks.Target], [expected]), ct)),
            ("initial completed boot observation cannot be ours", (_, ct) => CaptureWatch(new([], [expected]), ct)),
            ("initial native ambiguous vendor inventory", (_, ct) => NativeWatch([vendor, vendor], ct)),
            ("initial native wrong-port inventory", (_, ct) => NativeWatch([vendor with { LocationPaths = [expected.PhysicalLocation + "#USB(99)"] }], ct)),
            ("initial native unknown mouse exclusion", (_, ct) => NativeWatch([vendor, mouse with { HardwareIds = [] }], ct)),
            ("initial native completed vendor+keyboard+mouse boot cannot be ours", (_, ct) => NativeWatch([vendor, keyboard, mouse], ct)),
        ];
        foreach (var (label, watch) in initialObservers)
        {
            var directory = Path.Combine(root, "auto-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var channel = new EntryChannel(); var admissions = 0;
            var normal = new NormalObservation("1C24", "001200000025000100", AutoEntryChecks.Target.ContainerId,
                AutoEntryChecks.Target.PhysicalLocation, DateTimeOffset.UtcNow, "fake", RetainedBaselinePolicy.Origin);
            var entry = new AutomaticBootEntry(() => AutoEntryChecks.Target, _ => channel, watch);
            var operation = entry.ExecuteAsync(normal, AutoEntryChecks.Target, directory, new(Guid.NewGuid(), true, true),
                () => admissions++, CancellationToken.None);
            Exception? failure = null;
            try { await operation.ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { failure = ex; }
            Check(failure is not null && failure is not TransitionUncertainException && operation.IsCompleted &&
                channel.Writes == 0 && channel.Reads == 0 && channel.Disposals == 1 && admissions == 0,
                label + " rejected BEFORE write admission: zero writes/reads/new boot owner; observer joined and normal handle closed");
            Check(File.Exists(Path.Combine(directory, "transition-intent.json")) && !File.Exists(Path.Combine(directory, "transition-write-outcome.json")),
                label + " preserves intent without pretending any write occurred");
        }
        Console.WriteLine($"PASS {count} auto-review metadata/zero-send fake checks; hardware/process starts=0");
    }
}

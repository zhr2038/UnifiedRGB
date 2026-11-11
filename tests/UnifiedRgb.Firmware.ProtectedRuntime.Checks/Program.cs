using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Firmware;
using UnifiedRgb.Firmware.ProtectedRuntime;
using UnifiedRgb.Firmware.ProtectedRuntime.Checks;

var passed = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Test(string name, Action action)
{
    action(); passed.Add(name); Console.WriteLine("PASS " + name);
}
void Rejected(string name, Action<Fixture> mutate)
{
    Test(name, () =>
    {
        var fixture = new Fixture(); mutate(fixture);
        var result = fixture.Resolve();
        Check(!result.HardwareEnabled && result.FeatureError is not null && fixture.Fs.Writes.Count == 0,
            name + " must fail closed before provisioning: " + result.FeatureError);
    });
}
var live = FirmwareRuntimeResolver.LiveDirectory;
Test("pure-native-acl-shape-valid-three-conventional-allows", () =>
{
    var descriptor = new DirectorySecurity();
    descriptor.SetAccessRuleProtection(true, false);
    foreach (var rule in FirmwareRuntimeSecurity.Required(true).Rules)
        descriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(rule.Sid), rule.Rights,
            rule.Inheritance, rule.Propagation, rule.Type));
    FirmwareRuntimeSecurity.ValidateNativeAceShape(descriptor, 3);
});
foreach (var kind in new[] { "callback", "unprojected-ace" })
    Test("pure-native-acl-shape-reject-" + kind, () =>
    {
        var raw = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            new SecurityIdentifier(FirmwareRuntimeSecurity.Administrators), null, null, new RawAcl(2, 1));
        raw.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)FileSystemRights.WriteData,
            new SecurityIdentifier(FirmwareRuntimeSecurity.Users), kind == "callback", null));
        var binary = new byte[raw.BinaryLength]; raw.GetBinaryForm(binary, 0);
        var descriptor = new DirectorySecurity(); descriptor.SetSecurityDescriptorBinaryForm(binary);
        var rejected = false;
        try { FirmwareRuntimeSecurity.ValidateNativeAceShape(descriptor, kind == "callback" ? 1 : 0); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Opaque/callback/projected-count mismatch was accepted");
    });
Test("virtual-zero-filesystem-identity-and-lease-calls", () =>
{
    var f = new Fixture(); var result = f.Resolve(true, _ => throw new InvalidOperationException("Lease read forbidden"));
    Check(!result.HardwareEnabled && f.Fs.Calls == 0 && f.Fs.Writes.Count == 0 && f.Fs.ElevationReads == 0, "Virtual is not zero IO");
});
Test("missing-staging-zero-writes", () =>
{
    var f = new Fixture(); f.Fs.Nodes.Clear();
    Check(!f.Resolve().HardwareEnabled && f.Fs.Writes.Count == 0 && f.Fs.ElevationReads == 0, "Missing staging migrated");
});
Test("disabled-staging-zero-writes", () =>
{
    var f = new Fixture(); f.Config(false, null);
    Check(!f.Resolve().HardwareEnabled && f.Fs.Writes.Count == 0 && f.Fs.ElevationReads == 0, "Disabled staging migrated");
});
Rejected("nonadmin-zero-writes", f => f.Fs.Admin = false);
foreach (var state in new[] { "Pending maintenance owner", "Malformed/unknown firmware lease retained" })
    Test("lease-" + state, () =>
    {
        var f = new Fixture(); var result = f.Resolve(guard: options => options with { HardwareEnabled = false, FeatureError = state });
        Check(!result.HardwareEnabled && result.FeatureError == state && f.Fs.Calls == 0 && f.Fs.ElevationReads == 0, "Lease did not block before IO");
    });
Test("first-admin-provision-verified-bytes-pinned-only-secure-nested-live", () =>
{
    var f = new Fixture(); var result = f.Resolve();
    Check(result.HardwareEnabled && result.PrivateDirectory == live && result.CatalogPath == Path.Combine(live, "trusted-catalog.json"), result.FeatureError ?? "Wrong live paths");
    Check(!f.Fs.Nodes.ContainsKey(FakeFileSystem.Key(Path.Combine(live, "ignored.txt"))), "Unlisted data copied");
    Check(f.Fs.Writes[0] == FakeFileSystem.Key(live), "Root not secured before first copy");
    foreach (var path in f.Fs.Writes)
    {
        FirmwareRuntimeSecurity.Validate(f.Fs.Nodes[path].Entry);
        if (!f.Fs.Nodes[path].Entry.Directory)
        {
            var relative = Path.GetRelativePath(live, path);
            Check(f.Fs.Nodes[path].Bytes.SequenceEqual(f.Fs.Nodes[FakeFileSystem.Key(Path.Combine(Fixture.Staging, relative))].Bytes), "Not verified snapshot bytes");
        }
    }
    Check(f.Fs.ReadBounds[FakeFileSystem.Key(Path.Combine(Fixture.Staging, "worker.dll"))] == 64 * 1024 * 1024, "DLL bound");
});
Test("source-mutated-after-locked-snapshot-copies-captured-bytes", () =>
{
    var f = new Fixture(); var source = FakeFileSystem.Key(Path.Combine(Fixture.Staging, "worker.dll"));
    var original = f.Fs.Nodes[source].Bytes.ToArray();
    f.Fs.AfterRead = path => { if (path == source) f.Fs.Put(path, [222]); };
    Check(f.Resolve().HardwareEnabled, "Snapshot source mutation should not affect admitted bytes");
    Check(f.Fs.Nodes[FakeFileSystem.Key(Path.Combine(live, "worker.dll"))].Bytes.SequenceEqual(original), "Reopened staging for copy");
});
Rejected("tampered-catalog-schema", f => f.SetCatalog(f.Catalog with { Schema = 9 }));
Rejected("tampered-catalog-unreviewed-field", f => f.Put("trusted-catalog.json", Encoding.UTF8.GetBytes("{\"Schema\":1,\"Unknown\":true}")));
Rejected("all-release-hashes-validated", f => f.ChangeRelease(x => x with { SoftwareReviewSha256 = "BAD" }));
Rejected("duplicate-release-id-case-insensitive", f => f.SetCatalog(f.Catalog with { Releases = [f.Release, f.Release with { ReleaseId = "RELEASE-1" }] }));
Rejected("empty-catalog", f => f.SetCatalog(f.Catalog with { Releases = [] }));
Rejected("tampered-runtime", f => f.Put("worker.dll", [42]));
Rejected("tampered-retained-image-hash", f => f.Put("retained/image.bin", [42]));
Rejected("tampered-retained-receipt-hash", f => f.Put("retained/receipt.json", [42]));
Rejected("unpinned-extra-dll", f => f.Put("unexpected.dll", [42]));
Rejected("unpinned-extra-exe", f => f.Put("unexpected.exe", [42]));
Rejected("unpinned-extra-deps", f => f.Put("unexpected.deps.json", [42]));
Rejected("missing-required-worker-managed-pin", f => f.ChangeRelease(x => x with { Worker = x.Worker with { RuntimeFiles = x.Worker.RuntimeFiles.Where(p => p.Key != "worker.dll").ToDictionary() } }));
Rejected("runtime-core-contract-mismatch", f => f.ChangeRelease(x => x with { Worker = x.Worker with { CoreManifestSha256 = new string('0', 64) } }));
Rejected("runtime-traversal", f => f.ChangeRelease(x => x with { Worker = x.Worker with { RuntimeFiles = new(x.Worker.RuntimeFiles) { ["../escape.dll"] = new string('0', 64) } } }));
Rejected("runtime-backslash", f => f.ChangeRelease(x => x with { Worker = x.Worker with { RuntimeFiles = new(x.Worker.RuntimeFiles) { ["nested\\escape.dll"] = new string('0', 64) } } }));
Rejected("runtime-absolute", f => f.ChangeRelease(x => x with { Worker = x.Worker with { RuntimeFiles = new(x.Worker.RuntimeFiles) { ["C:/escape.dll"] = new string('0', 64) } } }));
Rejected("case-alias-pin", f => f.ChangeRelease(x => x with { Worker = x.Worker with { RuntimeFiles = new(x.Worker.RuntimeFiles) { ["WORKER.dll"] = x.Worker.RuntimeFiles["worker.dll"] } } }));
Rejected("tree-case-alias", f => f.Fs.DuplicateAlias = true);
Rejected("conflicting-cross-release-pin", f =>
{
    var conflict = f.Release with { ReleaseId = "release-2", Worker = f.Release.Worker with { RuntimeFiles = new(f.Release.Worker.RuntimeFiles) { ["worker.dll"] = new string('0', 64) } } };
    f.SetCatalog(f.Catalog with { Releases = [f.Release, conflict] });
});
foreach (var path in new[] { Fixture.Staging, "E:/mutable", Path.Combine(Fixture.Staging, "worker.dll"), Path.Combine(Fixture.Staging, "retained"), "C:/ProgramData" })
    Rejected("reparse-" + path, f =>
    {
        if (!f.Fs.Nodes.ContainsKey(FakeFileSystem.Key(path))) f.Fs.Put(path);
        f.Fs.ReplaceEntry(path, x => x with { Reparse = true });
    });
Rejected("configuration-bound", f => f.Put("firmware-installed.json", new byte[4097]));
Rejected("catalog-bound", f => f.Put("trusted-catalog.json", new byte[1024 * 1024 + 1]));
Rejected("dll-bound", f => f.Put("worker.dll", new byte[64 * 1024 * 1024 + 1]));
Rejected("apphost-exe-bound", f => f.Put("worker.exe", new byte[1024 * 1024 + 1]));
Rejected("data-bound", f => f.Put("worker.deps.json", new byte[1024 * 1024 + 1]));
Rejected("total-128MiB-bound-before-provision", f =>
{
    var data = new byte[44 * 1024 * 1024];
    var pins = new Dictionary<string, string>(f.Release.Worker.RuntimeFiles);
    for (var i = 0; i < 3; i++) { var name = "bulk" + i + ".dll"; f.Put(name, data); pins[name] = FirmwareFiles.Sha(data); }
    f.ChangeRelease(x => x with { Worker = x.Worker with { RuntimeFiles = pins } });
});
Rejected("tree-entry-bound", f =>
{
    for (var i = 0; i < 257; i++) f.Put("extra" + i + ".txt", []);
});
Rejected("tree-depth-bound", f => f.Put(string.Join('/', Enumerable.Repeat("nested", 17)) + "/file.txt", []));
Rejected("config-catalog-traversal", f => f.Config(true, "../catalog.json"));
Rejected("config-catalog-not-fixed", f => f.Config(true, "different.json"));
Rejected("config-catalog-null-enabled", f => f.Config(true, null));
Rejected("configuration-duplicate-property", f => f.Put("firmware-installed.json", Encoding.UTF8.GetBytes("{\"Schema\":1,\"HardwareEnabled\":true,\"hardwareenabled\":false,\"CatalogFile\":\"trusted-catalog.json\"}")));
Rejected("configuration-missing-explicit-hardware-enabled", f => f.Put("firmware-installed.json", Encoding.UTF8.GetBytes("{\"Schema\":1,\"CatalogFile\":\"trusted-catalog.json\"}")));
Rejected("access-denied-live-root-is-not-absence", f => f.Fs.DeniedPath = live);
Rejected("access-denied-staging-config-is-not-absence", f => f.Fs.DeniedPath = Path.Combine(Fixture.Staging, "firmware-installed.json"));
Rejected("retained-checkword-even-with-reviewed-hash", f =>
{
    var bad = new byte[FirmwareFiles.ImageLength]; bad[0] = 1; f.Put("retained/image.bin", bad);
    f.ChangeRelease(x => x with { RollbackSha256 = FirmwareFiles.Sha(bad), RetainedBaseline = x.RetainedBaseline! with { Image = new("retained/image.bin", FirmwareFiles.Sha(bad)) } });
});
foreach (var bad in new[] { "schema", "model", "header", "container", "location", "future", "empty-note", "image-hash" })
    Rejected("retained-receipt-" + bad, f =>
    {
        var original = f.Fs.Nodes[FakeFileSystem.Key(Path.Combine(Fixture.Staging, "retained/receipt.json"))].Bytes;
        var receipt = FirmwareFiles.Parse<RetainedBaselineReceipt>(original);
        receipt = bad switch
        {
            "schema" => receipt with { Schema = 2 }, "model" => receipt with { NormalModel = "bad" },
            "header" => receipt with { NormalFirmwareHeader = "bad" }, "container" => receipt with { ContainerId = Guid.Empty.ToString() },
            "location" => receipt with { PhysicalLocation = "bad" }, "future" => receipt with { OwnerConfirmedAtUtc = DateTimeOffset.MaxValue },
            "empty-note" => receipt with { EvidenceNote = "" }, _ => receipt with { ImageSha256 = new string('0', 64) },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt); f.Put("retained/receipt.json", bytes);
        f.ChangeRelease(x => x with { RetainedBaseline = x.RetainedBaseline! with { Receipt = new("retained/receipt.json", FirmwareFiles.Sha(bytes)) } });
    });
Test("existing-valid-live-without-admin-or-staging-no-overwrite", () =>
{
    var f = new Fixture(); f.ExistingLive(); f.Fs.Admin = false; f.Fs.Nodes.Remove(FakeFileSystem.Key(Path.Combine(Fixture.Staging, "firmware-installed.json")));
    var result = f.Resolve();
    Check(result.HardwareEnabled && f.Fs.Writes.Count == 0 && f.Fs.ElevationReads == 1 &&
        f.Fs.Reads.All(x => x.StartsWith(FakeFileSystem.Key(live), StringComparison.Ordinal)), "Existing live depended on staging/admin");
    f.Config(true); f.Put("worker.dll", [201]);
    Check(f.Resolve().HardwareEnabled && f.Fs.Writes.Count == 0, "Different staging replaced live");
});
Test("existing-live-virtual-remains-zero-io", () =>
{
    var f = new Fixture(); f.ExistingLive(); var calls = f.Fs.Calls;
    Check(!f.Resolve(true).HardwareEnabled && calls == f.Fs.Calls && f.Fs.Writes.Count == 0, "Virtual inspected live");
});
Test("precreated-hostile-root-preserved-never-blessed", () =>
{
    var f = new Fixture(); f.Fs.Put(live); var original = f.Fs.Nodes[FakeFileSystem.Key(live)]; var result = f.Resolve();
    Check(!result.HardwareEnabled && result.FeatureError is not null && f.Fs.Writes.Count == 0 && f.Fs.Nodes[FakeFileSystem.Key(live)] == original, "Hostile root blessed");
});
Test("partial-copy-preserved-and-next-startup-never-repairs", () =>
{
    var f = new Fixture(); f.Fs.FailAtFile = 2;
    Check(!f.Resolve().HardwareEnabled && f.Fs.Writes.Count > 1, "Partial copy did not fail");
    var writes = f.Fs.Writes.Count; f.Fs.FailAtFile = -1;
    Check(!f.Resolve().HardwareEnabled && writes == f.Fs.Writes.Count, "Partial root repaired");
});
Test("exclusive-root-race-fails-closed-no-acl-mutation", () =>
{
    var f = new Fixture(); f.Fs.RootRace = true; var result = f.Resolve();
    Check(!result.HardwareEnabled && result.FeatureError is not null && f.Fs.Writes.Count == 0 && f.Fs.Nodes[FakeFileSystem.Key(live)].Entry.Security is null, "Race blessed existing directory");
});
foreach (var target in new[] { "", "firmware-installed.json", "trusted-catalog.json", "worker.exe", "retained", "retained/receipt.json" })
    foreach (var fault in new[] { "owner", "unprotected", "write", "append", "delete", "delete-child", "write-dac", "write-owner", "inherited", "wrong-inheritance", "wrong-propagation", "deny", "extra-everyone", "extra-authenticated" })
        Test("live-acl-" + target + "-" + fault, () =>
        {
            var f = new Fixture(); f.ExistingLive(); var path = Path.Combine(live, target);
            f.Fs.ReplaceEntry(path, entry =>
            {
                var security = entry.Security!; var rules = security.Rules.ToArray();
                var users = rules[2];
                switch (fault)
                {
                    case "owner": security = security with { OwnerSid = "S-1-5-21-123" }; break;
                    case "unprotected": security = security with { DaclProtected = false }; break;
                    case "extra-everyone": rules = [.. rules, users with { Sid = "S-1-1-0" }]; break;
                    case "extra-authenticated": rules = [.. rules, users with { Sid = "S-1-5-11" }]; break;
                    case "inherited": rules[2] = users with { Inherited = true }; break;
                    case "wrong-inheritance": rules[2] = users with { Inheritance = entry.Directory ? InheritanceFlags.None : InheritanceFlags.ObjectInherit }; break;
                    case "wrong-propagation": rules[2] = users with { Propagation = PropagationFlags.InheritOnly }; break;
                    case "deny": rules[2] = users with { Type = AccessControlType.Deny }; break;
                    default:
                        var rights = fault switch { "write" => FileSystemRights.WriteData, "append" => FileSystemRights.AppendData,
                            "delete" => FileSystemRights.Delete, "delete-child" => FileSystemRights.DeleteSubdirectoriesAndFiles,
                            "write-dac" => FileSystemRights.ChangePermissions, _ => FileSystemRights.TakeOwnership };
                        rules[2] = users with { Rights = users.Rights | rights }; break;
                }
                return entry with { Security = security with { Rules = rules } };
            });
            var result = f.Resolve();
            Check(!result.HardwareEnabled && result.FeatureError is not null && f.Fs.Writes.Count == 0 && f.Fs.Reads.All(x => !x.StartsWith(FakeFileSystem.Key(Fixture.Staging), StringComparison.Ordinal)), "Bad live ACL repaired or admitted");
        });
Test("system-owned-live-users-readexecute-admitted", () =>
{
    var f = new Fixture(); f.ExistingLive();
    foreach (var path in f.Fs.Nodes.Keys.Where(x => x.StartsWith(FakeFileSystem.Key(live), StringComparison.Ordinal)).ToArray())
        f.Fs.ReplaceEntry(path, x => x with { Security = x.Security! with { OwnerSid = FirmwareRuntimeSecurity.System } });
    Check(f.Resolve().HardwareEnabled && f.Fs.Writes.Count == 0, "System-owned RX live rejected");
});
Test("post-copy-acl-validation-failure-preserves-partial", () =>
{
    var f = new Fixture();
    f.Fs.AfterCreate = path => { if (path.EndsWith("worker.exe", StringComparison.Ordinal)) f.Fs.ReplaceEntry(path, x => x with { Security = null }); };
    Check(!f.Resolve().HardwareEnabled && f.Fs.Nodes.ContainsKey(FakeFileSystem.Key(Path.Combine(live, "worker.exe"))), "Bad resulting ACL not preserved/disabled");
});
Test("post-copy-hash-verification-failure", () =>
{
    var f = new Fixture();
    f.Fs.AfterCreate = path => { if (path.EndsWith("worker.exe", StringComparison.Ordinal)) f.Fs.Put(path, [200], true); };
    Check(!f.Resolve().HardwareEnabled, "Resulting copy hash not verified");
});
foreach (var name in new[] { "worker.dll", "retained/image.bin", "retained/receipt.json", "trusted-catalog.json", "firmware-installed.json" })
    Test("existing-live-tampered-" + name + "-preserved-no-staging", () =>
    {
        var f = new Fixture(); f.ExistingLive(); f.Fs.Put(Path.Combine(live, name), [200], true);
        var result = f.Resolve();
        Check(!result.HardwareEnabled && result.FeatureError is not null && f.Fs.Writes.Count == 0 &&
            f.Fs.Reads.All(x => !x.StartsWith(FakeFileSystem.Key(Fixture.Staging), StringComparison.Ordinal)), "Live tamper repaired/fell back");
    });
foreach (var target in new[] { "", "worker.dll", "retained" })
    Test("existing-live-reparse-" + target, () =>
    {
        var f = new Fixture(); f.ExistingLive(); f.Fs.ReplaceEntry(Path.Combine(live, target), x => x with { Reparse = true });
        Check(!f.Resolve().HardwareEnabled && f.Fs.Writes.Count == 0, "Live reparse admitted/repaired");
    });
Console.WriteLine($"Protected runtime fake-only checks passed: {passed.Count}; native adapter calls: 0; hardware/process actions: 0");

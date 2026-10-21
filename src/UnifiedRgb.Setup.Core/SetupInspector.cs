using System.Globalization;
using System.Text.RegularExpressions;
using UnifiedRgb.Devices.AsusAura;
using UnifiedRgb.Devices.EneDram;
using UnifiedRgb.Devices.LianLi;
using UnifiedRgb.Devices.LianLi.Protocol;
using UnifiedRgb.Devices.MsiGpu;
using UnifiedRgb.Screens;
using UnifiedRgb.Transports.Nvapi;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup;

/// <summary>
/// Read-only, bounded inspection of THIS computer. Nothing here writes lighting/screen/PWM data, resets USB, installs drivers or
/// stops services; the only device I/O is the single literal receiver list query, and only with exactly one measured receiver
/// and one sender present. Enumeration never marks anything control-verified.
/// </summary>
public sealed partial class SetupInspector(ISetupSystem system, SetupEnvironment environment, TimeProvider? clock = null)
{
    public const string CodecResource = "LianLiCodec";
    public const string PawnIoLibraryResource = "PawnIOLib";
    public const string PawnIoModuleResource = "PawnIOModule";

    /// <summary>The PIIX4-compatible SMBus controller the pinned module drives (identity 1022:790B).</summary>
    /// <summary>WinUSB device-interface class of the Lian Li RF dongles (the Host's own default is empty, i.e. discovery off).</summary>
    public const string LianLiInterfaceGuid = "dee824ef-729b-4a0e-9c14-b7117d33a817";

    public const string SmbusControllerPrefix = @"PCI\VEN_1022&DEV_790B";

    private readonly ISetupSystem _system = system ?? throw new ArgumentNullException(nameof(system));
    private readonly SetupEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<SetupReport> InspectAsync(CancellationToken ct) => Task.Run(() => Inspect(ct), ct);

    public SetupReport Inspect(CancellationToken ct)
    {
        var findings = new List<SetupFinding>();
        var instanceIds = Guarded(findings, "DeviceEnumeration", () => _system.PresentDeviceInstanceIds(), []);
        ct.ThrowIfCancellationRequested();

        var codec = new SetupResourceReport(CodecResource, null, SetupResourceState.Verified, null, string.Empty, null,
            "Built-in managed tinyuz encoder; no manufacturer DLL is required or loaded by setup.");
        var module = InstallResource(PawnIoModuleResource, _environment.PawnIoModuleRelativePath, EneDramDefaults.ModuleSha256);
        ct.ThrowIfCancellationRequested();
        var library = PawnIoLibrary();
        ct.ThrowIfCancellationRequested();

        var lianLi = InspectLianLi(codec, findings, out var lianLiFamily);
        ct.ThrowIfCancellationRequested();
        var families = new List<SetupFamilyReport>
        {
            lianLiFamily,
            InspectGpu(instanceIds),
            InspectAura(findings),
            InspectEne(instanceIds, library, module),
        };
        families.AddRange(InspectScreens(instanceIds));

        if (!families.Any(f => f.Support == SetupSupport.Supported))
        {
            findings.Add(new(SetupSeverity.Info, "NoSupportedHardware", "No supported hardware was found; demo mode remains available."));
        }

        return new(_clock.GetUtcNow(), _environment, families, lianLi, [codec, library, module], findings);
    }

    private SetupResourceReport InstallResource(string name, string relative, string sha)
    {
        if (!SetupPaths.TryResolveInstallRelative(_environment.InstallDirectory, relative, out var full, out var error))
        {
            return new(name, null, SetupResourceState.InvalidPath, null, sha, null, error);
        }

        return Pinned(name, full, sha);
    }

    private SetupResourceReport PawnIoLibrary()
    {
        var root = _system.ProgramFilesDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return new(PawnIoLibraryResource, null, SetupResourceState.InvalidPath, null, EneDramDefaults.PawnIoLibSha256, null,
                "The Program Files directory is unavailable.");
        }

        var report = Pinned(PawnIoLibraryResource, Path.Combine(root, "PawnIO", "PawnIOLib.dll"), EneDramDefaults.PawnIoLibSha256);
        if (report.State != SetupResourceState.Verified)
        {
            return report;
        }

        try
        {
            var evidence = _system.ProbePawnIoApi(report.FullPath!, EneDramDefaults.PawnIoLibSha256, EneDramDefaults.PawnIoApiVersion);
            return report with { Detail = $"Pinned file in an administrator-only location; {evidence}." };
        }
#pragma warning disable CA1031 // any refusal of the API gate is reported, never fatal to inspection
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return report with { State = SetupResourceState.ApiMismatch, Detail = $"API gate refused: {ex.Message}" };
        }
    }

    private SetupResourceReport Pinned(string name, string full, string sha)
    {
        var facts = _system.InspectFile(full);
        if (!facts.Exists)
        {
            return new(name, full, SetupResourceState.Missing, null, sha, null, "Not installed.");
        }

        if (facts.Sha256 is null)
        {
            return new(name, full, SetupResourceState.Unreadable, null, sha, facts.Version, facts.Error ?? "Unreadable.");
        }

        if (!string.Equals(facts.Sha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            return new(name, full, SetupResourceState.HashMismatch, facts.Sha256, sha, facts.Version,
                "SHA-256 differs from the reviewed pin; it is not used (a new pin needs review, setup never re-pins).");
        }

        if (!_system.IsAdminOnlyWritable(full, out var reason))
        {
            return new(name, full, SetupResourceState.UntrustedLocation, facts.Sha256, sha, facts.Version, reason);
        }

        return new(name, full, SetupResourceState.Verified, facts.Sha256, sha, facts.Version, "Pinned file in an administrator-only location.");
    }

    private LianLiPairingResult InspectLianLi(SetupResourceReport codec, List<SetupFinding> findings, out SetupFamilyReport family)
    {
        var options = new LianLiOptions { ReceiverInterfaceGuid = LianLiInterfaceGuid };
        var guid = Guid.Parse(LianLiInterfaceGuid);
        var interfaces = Guarded(findings, "LianLiEnumeration", () => _system.DeviceInterfaces(guid), []);
        var senders = LianLiDiscovery.Senders(options, new FixedEnumerator(interfaces));
        var receivers = interfaces.Where(d => LianLiUsbIds.RoleOf(d.Vid, d.Pid) == RfDongleRole.Receiver)
            .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        byte[]? reply = null;
        string? queryError = null;
        if (receivers.Length == 1 && RfReceiverQuery.Supports(receivers[0]) && senders.Count == 1)
        {
            try
            {
                reply = _system.QueryRfReceiverList(receivers[0]);
            }
#pragma warning disable CA1031 // a failed query is a reported pairing state, not an inspection failure
            catch (Exception ex)
#pragma warning restore CA1031
            {
                queryError = ex.Message;
            }
        }

        var result = LianLiPairingPlanner.Analyse(receivers, senders, reply, queryError);
        var identities = senders.Concat(receivers).Select(d => $"USB {d.Vid:X4}:{d.Pid:X4}").ToArray();
        var ready = result.State == LianLiPairingState.Ready;
        var codecOk = codec.State == SetupResourceState.Verified;
        var support = senders.Count == 0 && receivers.Length == 0 ? SetupSupport.NotPresent
            : !ready ? SetupSupport.Unsupported
            : codecOk ? SetupSupport.Supported : SetupSupport.PrerequisiteMissing;
        var detail = codecOk || !ready ? result.Detail : $"{result.Detail} Packaged codec: {codec.Detail}";
        family = new(SetupFamily.LianLi, support, identities, CanEnable: senders.Count == 1, CanAuthorizeOutput: ready && codecOk, detail);
        return result;
    }

    private static SetupFamilyReport InspectGpu(IReadOnlyList<string> instanceIds)
    {
        var supported = new List<string>();
        var other = new List<string>();
        foreach (var id in instanceIds)
        {
            var m = PciIdPattern().Match(id);
            if (!m.Success)
            {
                continue;
            }

            var vendor = ushort.Parse(m.Groups["ven"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var device = ushort.Parse(m.Groups["dev"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var subsys = uint.Parse(m.Groups["sub"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (vendor != MsiGpuDiscovery.NvidiaVendor)
            {
                continue;
            }

            var text = $"PCI {vendor:X4}:{device:X4} subsystem {subsys:X8}";
            (MsiGpuDiscovery.IsSupported(new NvPciIds(((uint)device << 16) | vendor, subsys, 0, 0)) ? supported : other).Add(text);
        }

        if (supported.Count > 0)
        {
            return new(SetupFamily.MsiGpu, SetupSupport.Supported, supported.Distinct().ToArray(), true, true,
                "Supported MSI GPU identity (PCI ids only; NVAPI/I2C not opened). Lighting writes stay blocked until supervised verification.");
        }

        return other.Count > 0
            ? new(SetupFamily.MsiGpu, SetupSupport.Unsupported, other.Distinct().ToArray(), false, false,
                "NVIDIA GPU present, but its subsystem is not a supported/validated lighting identity.")
            : new(SetupFamily.MsiGpu, SetupSupport.NotPresent, [], false, false, "No supported GPU lighting controller found.");
    }

    private SetupFamilyReport InspectAura(List<SetupFinding> findings)
    {
        var collections = Guarded(findings, "HidEnumeration", () => _system.HidCollections(AsusAuraDefaults.Vid, AsusAuraDefaults.Pid), []);
        if (collections.Count == 0)
        {
            return new(SetupFamily.AsusAura, SetupSupport.NotPresent, [], false, false, "No ASUS Aura USB controller found.");
        }

        var selected = AsusAuraDefaults.Rule.Select(collections, out var rejected);
        var identity = $"HID {AsusAuraDefaults.Vid:X4}:{AsusAuraDefaults.Pid:X4}";
        return selected.Count == 1
            ? new(SetupFamily.AsusAura, SetupSupport.Supported, [identity], true, true,
                "Pinned Aura collection (capabilities only, no report traffic). Lighting writes stay blocked until supervised verification.")
            : new(SetupFamily.AsusAura, SetupSupport.Unsupported, [identity], false, false,
                selected.Count == 0 ? $"No collection matched the pinned identity: {string.Join("; ", rejected)}"
                    : $"{selected.Count} matching collections; refusing an ambiguous controller.");
    }

    private static SetupFamilyReport InspectEne(IReadOnlyList<string> instanceIds, SetupResourceReport library, SetupResourceReport module)
    {
        var smbus = instanceIds.Any(id => id.StartsWith(SmbusControllerPrefix, StringComparison.OrdinalIgnoreCase));
        if (!smbus)
        {
            return new(SetupFamily.EneDram, SetupSupport.NotPresent, [], false, false,
                "The supported SMBus controller (1022:790B) is not present.");
        }

        var identities = new[] { "PCI 1022:790B SMBus" };
        if (library.State != SetupResourceState.Verified || module.State != SetupResourceState.Verified)
        {
            return new(SetupFamily.EneDram, SetupSupport.PrerequisiteMissing, identities, false, false,
                $"PawnIO prerequisites: library {library.State}, module {module.State}.");
        }

        return new(SetupFamily.EneDram, SetupSupport.Unknown, identities, true, true,
            "Prerequisites pinned. ENE DRAM presence cannot be established without SMBus probing, which setup never does; "
            + "Host discovery runs after enabling. Lighting writes stay blocked until supervised verification.");
    }

    private static IEnumerable<SetupFamilyReport> InspectScreens(IReadOnlyList<string> instanceIds)
    {
        var seen = new Dictionary<ScreenFamily, HashSet<string>>();
        foreach (var id in instanceIds)
        {
            var m = UsbIdPattern().Match(id);
            if (!m.Success || m.Groups["mi"].Success)
            {
                continue; // composite children repeat their parent device
            }

            var vid = ushort.Parse(m.Groups["vid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var pid = ushort.Parse(m.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (ScreenFamilyInfo.FromUsb(vid, pid) is { } info)
            {
                if (!seen.TryGetValue(info.Family, out var set))
                {
                    seen[info.Family] = set = new(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(id);
            }
        }

        foreach (var (family, setupFamily) in new[]
                 {
                     (ScreenFamily.TlLcd, SetupFamily.ScreenTlLcd), (ScreenFamily.H2, SetupFamily.ScreenH2),
                     (ScreenFamily.Turzx, SetupFamily.ScreenTurzx),
                 })
        {
            var info = ScreenFamilyInfo.Of(family);
            var count = seen.TryGetValue(family, out var ids) ? ids.Count : 0;
            yield return count == 0
                ? new(setupFamily, SetupSupport.NotPresent, [], false, false, $"No {info.DisplayName} found.")
                : new(setupFamily, SetupSupport.Supported, [$"USB {ScreenFamilyInfo.VendorId:X4}:{info.ProductId:X4} x{count}"], true, true,
                    $"{count} {info.DisplayName}(s). Physical position/orientation and playback are unverified until observed; a vendor program holding a screen blocks it.");
        }
    }

    private static T Guarded<T>(List<SetupFinding> findings, string code, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // an enumeration failure is reported as a finding; inspection continues read-only
        catch (Exception ex)
#pragma warning restore CA1031
        {
            findings.Add(new(SetupSeverity.Warning, code, $"{code} failed: {ex.Message}"));
            return fallback;
        }
    }

    [GeneratedRegex(@"^PCI\\VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})&SUBSYS_(?<sub>[0-9A-F]{8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PciIdPattern();

    [GeneratedRegex(@"^USB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})(?<mi>&MI_[0-9A-F]{2})?\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdPattern();

    private sealed class FixedEnumerator(IReadOnlyList<UsbDeviceInfo> devices) : IUsbEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass) => devices;
    }
}

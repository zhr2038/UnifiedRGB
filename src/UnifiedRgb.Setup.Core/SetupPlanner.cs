using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnifiedRgb.Setup;

/// <summary>
/// Pure planning: (inspection report, explicit selection, current file) → the exact new appsettings bytes plus a readable list of
/// every change. Only the keys named below are touched; every other field, unknown ones included, is kept verbatim. Setup never
/// sets a <c>*Verified</c> gate, with the single listed exception of <c>LianLi:WireFormatVerified</c> on explicit Lian Li output
/// authorization (a protocol gate; physical playback is still reported UNVERIFIED). Errors never throw: they make the plan
/// unsavable.
/// </summary>
public static class SetupPlanner
{
    public const string Root = "UnifiedRgb";

    /// <summary>The only <c>*Verified</c> key setup may raise, and only on explicit Lian Li output authorization.</summary>
    public const string LianLiWireGatePath = "UnifiedRgb:LianLi:WireFormatVerified";

    private static readonly (SetupFamily Family, string EnableKey, string Section, string[] Gates)[] NativeClasses =
    [
        (SetupFamily.MsiGpu, "EnableMsiGpu", "MsiGpu", ["RegisterMapVerified", "ControlVerified"]),
        (SetupFamily.AsusAura, "EnableAsusAura", "AsusAura", ["ControlVerified"]),
        (SetupFamily.EneDram, "EnableEneDram", "EneDram", ["PawnIoModuleVerified", "ControlVerified"]),
    ];

    private static readonly (SetupFamily Family, string Key)[] ScreenFamilies =
    [
        (SetupFamily.ScreenTlLcd, "TlLcd"), (SetupFamily.ScreenH2, "H2"), (SetupFamily.ScreenTurzx, "Turzx"),
    ];

    public static SetupPlan BuildPlan(SetupReport report, SetupSelection selection, SetupConfigSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(current);
        var findings = new List<SetupFinding>();
        var outcomes = new List<SetupFamilyOutcome>();

        if (current.Error is not null || (current.Exists && current.Content is null))
        {
            return Unsavable(current, findings, "ConfigRefused", $"The existing configuration is not modified: {current.Error ?? "unreadable"}.");
        }

        JsonObject baseRoot, root;
        try
        {
            baseRoot = current.Exists ? SetupConfigDocument.Parse(current.Content) : new JsonObject();
            root = current.Exists ? SetupConfigDocument.Parse(current.Content) : new JsonObject();
        }
        catch (SetupConfigException ex)
        {
            return Unsavable(current, findings, "ConfigRefused", ex.Message);
        }

        var editor = new Editor(findings);
        var choices = Validate(report, selection, findings);
        var app = editor.Section(root, Root, Root);
        if (app is not null && !findings.Any(f => f.Severity == SetupSeverity.Error))
        {
            if (selection.Mode == SetupMode.Demo)
            {
                PlanDemo(editor, app, outcomes);
            }
            else
            {
                PlanHardware(editor, app, report, selection, choices, outcomes, findings);
            }

            CheckGateInvariant(baseRoot, root, findings);
        }

        var canSave = !findings.Any(f => f.Severity == SetupSeverity.Error);
        var hasChanges = canSave && editor.Changes.Count > 0;
        var proposedBytes = hasChanges ? SetupConfigDocument.Serialize(root) : current.Content ?? [];
        return new(canSave, hasChanges, current.Exists, current.Sha256, Encoding.UTF8.GetString(proposedBytes),
            SetupConfigDocument.Sha256(proposedBytes), editor.Changes, editor.Gates, outcomes, findings);
    }

    private static Dictionary<SetupFamily, SetupFamilySelection> Validate(SetupReport report, SetupSelection selection, List<SetupFinding> findings)
    {
        var map = new Dictionary<SetupFamily, SetupFamilySelection>();
        foreach (var s in selection.Families ?? [])
        {
            if (!Enum.IsDefined(s.Family) || !map.TryAdd(s.Family, s))
            {
                findings.Add(Error("SelectionInvalid", $"Family {s.Family} is unknown or selected more than once."));
                continue;
            }

            if (s.AuthorizeOutput && !s.Enable)
            {
                findings.Add(Error("SelectionInvalid", $"{s.Family}: output authorization requires enabling the family."));
            }

            if (selection.Mode == SetupMode.Demo && (s.Enable || s.AuthorizeOutput))
            {
                findings.Add(Error("SelectionInvalid", $"{s.Family}: demo mode enables no hardware."));
            }

            var family = report.Families.FirstOrDefault(f => f.Family == s.Family);
            if (selection.Mode == SetupMode.Hardware && s.Enable && family is not { CanEnable: true })
            {
                findings.Add(Error("NotAvailable", $"{s.Family} cannot be enabled on this computer: {family?.Detail ?? "not inspected"}"));
            }

            if (selection.Mode == SetupMode.Hardware && s.AuthorizeOutput && family is not { CanAuthorizeOutput: true })
            {
                findings.Add(Error("NotAvailable", $"{s.Family} output cannot be authorized on this computer: {family?.Detail ?? "not inspected"}"));
            }
        }

        if (selection.AcceptedLianLiMacs is { } macs)
        {
            if (selection.Mode == SetupMode.Demo || !(map.TryGetValue(SetupFamily.LianLi, out var l) && l.Enable))
            {
                findings.Add(Error("SelectionInvalid", "A Lian Li device list can only be saved with the Lian Li family enabled."));
            }

            if (macs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != macs.Count)
            {
                findings.Add(Error("SelectionInvalid", "A Lian Li device is accepted more than once."));
            }

            foreach (var mac in macs.Where(m => !report.LianLi.Proposals.Any(p => string.Equals(p.Mac, m, StringComparison.OrdinalIgnoreCase))))
            {
                findings.Add(Error("UnknownDevice", $"Lian Li device '{mac}' is not a proposal of this computer's receiver report."));
            }
        }

        return map;
    }

    private static void PlanDemo(Editor editor, JsonObject app, List<SetupFamilyOutcome> outcomes)
    {
        editor.SetBool(app, Root, "UseVirtualAdapter", true);
        foreach (var key in new[] { "EnableLianLi", "EnableMsiGpu", "EnableAsusAura", "EnableEneDram", "EnableOpenRgb" })
        {
            editor.SetBool(app, Root, key, false);
        }

        if (editor.Section(app, "Screens", $"{Root}:Screens") is { } screens)
        {
            editor.SetBool(screens, $"{Root}:Screens", "Enabled", false, "Demo mode: no screen output.");
        }

        outcomes.AddRange(Enum.GetValues<SetupFamily>().Select(f => new SetupFamilyOutcome(f, false, false, SetupPlaybackState.Off, "Demo mode.")));
    }

    private static void PlanHardware(Editor editor, JsonObject app, SetupReport report, SetupSelection selection,
        Dictionary<SetupFamily, SetupFamilySelection> choices, List<SetupFamilyOutcome> outcomes, List<SetupFinding> findings)
    {
        SetupFamilySelection Choice(SetupFamily f) => choices.TryGetValue(f, out var s) ? s : new(f, false, false);

        editor.SetBool(app, Root, "UseVirtualAdapter", false);
        if (Editor.GetBool(app, "EnableOpenRgb") == true)
        {
            findings.Add(new(SetupSeverity.Warning, "OpenRgbUnmanaged", "The existing OpenRGB route stays as configured; setup does not manage it."));
        }

        PlanLianLi(editor, app, report, selection, Choice(SetupFamily.LianLi), outcomes, findings);
        foreach (var (family, enableKey, sectionName, gates) in NativeClasses)
        {
            var choice = Choice(family);
            editor.SetBool(app, Root, enableKey, choice.Enable);
            var path = $"{Root}:{sectionName}";
            var section = choice.Enable ? editor.Section(app, sectionName, path) : null;
            if (section is not null && family == SetupFamily.EneDram)
            {
                editor.SetString(section, path, "ModulePath", report.Environment.PawnIoModuleRelativePath);
                if (report.Resource(SetupInspector.PawnIoLibraryResource)?.FullPath is { } lib)
                {
                    editor.SetString(section, path, "PawnIoLibPath", lib);
                }
            }

            var closed = gates.Where(g => section is null || Editor.GetBool(section, g) != true).ToArray();
            outcomes.Add(NativeOutcome(choice, closed, gates, findings));
        }

        var screens = editor.Section(app, "Screens", $"{Root}:Screens");
        if (screens is null)
        {
            return;
        }

        var anyOutput = ScreenFamilies.Any(s => Choice(s.Family).AuthorizeOutput);
        editor.SetBool(screens, $"{Root}:Screens", "Enabled", anyOutput,
            anyOutput ? "Explicit screen output authorization (master gate)." : "No screen output authorized.");
        foreach (var (family, key) in ScreenFamilies)
        {
            var choice = Choice(family);
            if (editor.Section(screens, key, $"{Root}:Screens:{key}") is { } section)
            {
                editor.SetBool(section, $"{Root}:Screens:{key}", "Output", choice.AuthorizeOutput,
                    choice.AuthorizeOutput ? "Explicit per-family screen output authorization." : "Screen output not authorized.");
            }

            outcomes.Add(new(family, choice.Enable, choice.AuthorizeOutput,
                choice.AuthorizeOutput ? SetupPlaybackState.OutputUnverified : choice.Enable ? SetupPlaybackState.DiscoveryOnly : SetupPlaybackState.Off,
                choice.AuthorizeOutput ? "Screen output enabled; position, orientation and playback are UNVERIFIED until observed."
                    : "Read-only screen discovery and previews only."));
        }

        if (!choices.Values.Any(c => c.Enable))
        {
            findings.Add(new(SetupSeverity.Warning, "NothingEnabled", "No hardware family is enabled; the program will run without devices. Demo mode shows simulated devices."));
        }
    }

    private static void PlanLianLi(Editor editor, JsonObject app, SetupReport report, SetupSelection selection, SetupFamilySelection choice,
        List<SetupFamilyOutcome> outcomes, List<SetupFinding> findings)
    {
        editor.SetBool(app, Root, "EnableLianLi", choice.Enable);
        if (!choice.Enable)
        {
            outcomes.Add(new(SetupFamily.LianLi, false, false, SetupPlaybackState.Off, "Disabled."));
            return;
        }

        const string path = Root + ":LianLi";
        var section = editor.Section(app, "LianLi", path);
        if (section is null)
        {
            return;
        }

        // The Host default is empty (no sender/receiver discovery at all); keep any valid existing value.
        if (!(SetupConfigDocument.Member(section, "ReceiverInterfaceGuid") is { Value: JsonValue g }
              && g.GetValueKind() == JsonValueKind.String && Guid.TryParse(g.GetValue<string>(), out var existing) && existing != Guid.Empty))
        {
            editor.SetString(section, path, "ReceiverInterfaceGuid", SetupInspector.LianLiInterfaceGuid);
        }

        if (selection.AcceptedLianLiMacs is { } macs)
        {
            var accepted = report.LianLi.Proposals.Where(p => macs.Contains(p.Mac, StringComparer.OrdinalIgnoreCase)).ToArray();
            var existingDevices = SetupConfigDocument.Member(section, "Devices")?.Value as JsonArray;
            var devices = new JsonArray(accepted.Select(p =>
            {
                var previous = existingDevices?.OfType<JsonObject>().FirstOrDefault(o =>
                    SetupConfigDocument.Member(o, "Mac")?.Value is JsonValue v && v.TryGetValue<string>(out var mac)
                    && string.Equals(mac.Replace('-', ':'), p.Mac.Replace('-', ':'), StringComparison.OrdinalIgnoreCase));
                // Preserve this computer's mounting and unknown fields. A foreign record is never copied to a new identity.
                var device = previous?.DeepClone().AsObject() ?? new JsonObject();
                Set("Mac", p.Mac); Set("Name", p.Name); Set("Family", p.Family);
                Set("RecType", p.RecType); Set("DevType", p.DevType); Set("FanNum", p.FanNum);
                Set("Leds", p.Leds); Set("Sender", p.Sender); Set("Channel", p.Channel); Set("Rx", p.Rx);
                return (JsonNode)device;
                void Set(string key,JsonNode value) => device[SetupConfigDocument.Member(device,key)?.Key ?? key] = value;
            }).ToArray());
            editor.SetNode(section, path, "Devices", devices, JsonValueKind.Array);
            if (report.LianLi.Channel is { } channel)
            {
                editor.SetInt(section, path, "MasterChannel", channel);
            }
        }
        else
        {
            CheckConfiguredDevices(section, report, findings);
        }

        if (choice.AuthorizeOutput)
        {
            editor.SetString(section, path, "Codec", "Managed");
            editor.SetBool(section, path, "WireFormatVerified", true,
                "Explicit Lian Li output authorization: protocol gate for the validated TL/H2 wire format. Physical playback stays UNVERIFIED.");
            outcomes.Add(new(SetupFamily.LianLi, true, true, SetupPlaybackState.OutputUnverified,
                "Uploads enabled; physical playback is UNVERIFIED until optically confirmed. Mounting is identity (no calibration copied)."));
        }
        else
        {
            editor.SetBool(section, path, "WireFormatVerified", false, "Discovery only: no Lian Li RF output without explicit authorization.");
            outcomes.Add(new(SetupFamily.LianLi, true, false, SetupPlaybackState.DiscoveryOnly, "Configured, but the adapter will not open the sender (no output)."));
        }
    }

    private static void CheckConfiguredDevices(JsonObject section, SetupReport report, List<SetupFinding> findings)
    {
        if (SetupConfigDocument.Member(section, "Devices") is not { Value: JsonArray existing } || existing.Count == 0)
        {
            findings.Add(new(SetupSeverity.Warning, "NoLianLiDevices", "No Lian Li devices are configured; nothing will be driven."));
            return;
        }

        if (report.LianLi.State != LianLiPairingState.Ready)
        {
            findings.Add(new(SetupSeverity.Warning, "DevicesUnconfirmed", $"The {existing.Count} configured Lian Li device(s) could not be compared with this computer's receiver ({report.LianLi.State})."));
            return;
        }

        foreach (var node in existing)
        {
            var mac = node is JsonObject o && SetupConfigDocument.Member(o, "Mac") is { Value: JsonValue v } && v.TryGetValue<string>(out var s) ? s : "?";
            if (!report.LianLi.Proposals.Any(p => string.Equals(p.Mac, mac.Replace('-', ':'), StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new(SetupSeverity.Warning, "ForeignDevice", $"Configured Lian Li device '{mac}' is not reported by this computer's receiver (copied configuration?)."));
            }
        }
    }

    private static SetupFamilyOutcome NativeOutcome(SetupFamilySelection choice, string[] closed, string[] gates, List<SetupFinding> findings)
    {
        if (!choice.Enable)
        {
            return new(choice.Family, false, false, SetupPlaybackState.Off, "Disabled.");
        }

        if (closed.Length == 0 && !choice.AuthorizeOutput)
        {
            findings.Add(Error("OutputAlreadyGranted",
                $"{choice.Family}: the existing configuration already passed its verification gates ({string.Join(", ", gates)}), so enabling it WILL write. "
                + "Authorize output or leave it disabled; setup does not clear verification evidence."));
        }

        if (closed.Length == 0)
        {
            return new(choice.Family, true, true, SetupPlaybackState.PreviouslyVerified,
                "Existing configuration already carries its verification gates; preserved, not re-verified by setup.");
        }

        var gateList = string.Join(", ", closed);
        return choice.AuthorizeOutput
            ? new(choice.Family, true, true, SetupPlaybackState.BlockedByVerificationGate,
                $"Output authorized, but the writer stays blocked by {gateList}=false. Complete supervised Native Bring-up (bounded marker test + owner observation) to verify.")
            : new(choice.Family, true, false, SetupPlaybackState.DiscoveryOnly, $"Discovery and leased Native Bring-up only ({gateList}=false).");
    }

    /// <summary>Defence in depth: no <c>*Verified</c> boolean anywhere may go from not-true to true, except the Lian Li wire gate.</summary>
    private static void CheckGateInvariant(JsonObject before, JsonObject after, List<SetupFinding> findings)
    {
        var old = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Walk(before, string.Empty, (p, v) => old[p] = v);
        Walk(after, string.Empty, (p, v) =>
        {
            if (v && !(old.TryGetValue(p, out var was) && was) && !string.Equals(p, LianLiWireGatePath, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Error("GateInvariant", $"Refusing to raise verification gate {p}."));
            }
        });

        static void Walk(JsonNode? node, string path, Action<string, bool> visit)
        {
            if (node is JsonObject o)
            {
                foreach (var kv in o)
                {
                    var p = path.Length == 0 ? kv.Key : $"{path}:{kv.Key}";
                    if (kv.Key.EndsWith("Verified", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    {
                        visit(p, value.GetValueKind() == JsonValueKind.True);
                    }

                    Walk(kv.Value, p, visit);
                }
            }
            else if (node is JsonArray a)
            {
                for (var i = 0; i < a.Count; i++)
                {
                    Walk(a[i], $"{path}:{i}", visit);
                }
            }
        }
    }

    private static SetupPlan Unsavable(SetupConfigSnapshot current, List<SetupFinding> findings, string code, string message)
    {
        findings.Add(Error(code, message));
        var bytes = current.Content ?? [];
        return new(false, false, current.Exists, current.Sha256, Encoding.UTF8.GetString(bytes), SetupConfigDocument.Sha256(bytes), [], [], [], findings);
    }

    private static SetupFinding Error(string code, string message) => new(SetupSeverity.Error, code, message);

    /// <summary>Type-checked, change-recording edits that keep the existing key spelling.</summary>
    private sealed class Editor(List<SetupFinding> findings)
    {
        public List<SetupConfigChange> Changes { get; } = [];

        public List<SetupGateChange> Gates { get; } = [];

        public JsonObject? Section(JsonObject parent, string name, string path)
        {
            switch (SetupConfigDocument.Member(parent, name))
            {
                case null:
                    var created = new JsonObject();
                    parent[name] = created;
                    return created;
                case { Value: JsonObject o }:
                    return o;
                default:
                    findings.Add(Error("UnexpectedJson", $"{path} is not a JSON object; the configuration is not modified."));
                    return null;
            }
        }

        public static bool? GetBool(JsonObject parent, string name) =>
            SetupConfigDocument.Member(parent, name) is { Value: JsonValue v } && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? v.GetValueKind() == JsonValueKind.True : null;

        public void SetBool(JsonObject parent, string path, string name, bool value, string? gateReason = null)
        {
            var before = Set(parent, path, name, JsonValue.Create(value), JsonValueKind.True, JsonValueKind.False);
            if (gateReason is not null && before is { } b && b != JsonValue.Create(value).ToJsonString() && (b != "absent" || value))
            {
                Gates.Add(new($"{path}:{name}", b == "true", value, gateReason));
            }
        }

        public void SetString(JsonObject parent, string path, string name, string value) =>
            Set(parent, path, name, JsonValue.Create(value), JsonValueKind.String);

        public void SetInt(JsonObject parent, string path, string name, int value) =>
            Set(parent, path, name, JsonValue.Create(value), JsonValueKind.Number);

        public void SetNode(JsonObject parent, string path, string name, JsonNode value, JsonValueKind kind) => Set(parent, path, name, value, kind);

        /// <summary>Returns the previous JSON text ("absent" when missing), or null when refused.</summary>
        private string? Set(JsonObject parent, string path, string name, JsonNode value, params JsonValueKind[] kinds)
        {
            var member = SetupConfigDocument.Member(parent, name);
            var key = member?.Key ?? name;
            var full = $"{path}:{key}";
            string before;
            if (member is { Value: var existing })
            {
                if (existing is null || !kinds.Contains(existing.GetValueKind()))
                {
                    findings.Add(Error("UnexpectedJson", $"{full} has an unexpected type; the configuration is not modified."));
                    return null;
                }

                before = existing.ToJsonString();
                if (before == value.ToJsonString())
                {
                    return before;
                }
            }
            else
            {
                before = "absent";
            }

            parent[key] = value;
            Changes.Add(new(full, member is null ? null : before, value.ToJsonString()));
            return before;
        }
    }
}

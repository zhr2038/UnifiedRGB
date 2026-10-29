using System.Globalization;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.LianLi;

namespace UnifiedRgb.Host.Services;

/// <summary>Engine ↔ wire conversions. Only Contracts DTOs cross the pipe.</summary>
public static class DtoMapper
{
    /// <summary>
    /// No backend reports a complete original-lighting snapshot with a positively verified restore path yet, so every per-key
    /// status carries restoration Unproven and no per-key output request is admitted (PerKeyAdmission). A backend restoration
    /// source (INTERFACE-PROPOSAL) replaces this; nothing here may turn it Verified.
    /// </summary>
    internal const string RestorationNotReported =
        "No backend reports a complete original-lighting snapshot with a verified restore path; per-key output is not admitted.";

    public static ControllerDto ToDto(ControllerEntry e)
    {
        var d = e.Descriptor;
        // One Writer snapshot: a retirement on another thread may clear e.Writer between two reads.
        var writer = e.Writer;
        var state = e.BringupLease is not null ? ControllerStates.BringUp
            : writer is not null
            ? (writer.Paused ? ControllerStates.Contended : ControllerStates.Active)
            : e.Verdict.State == Services.GuardState.Clear ? ControllerStates.Offline : e.Verdict.State.ToString();
        var m = e.Adapter is LianLiAdapter lianLi ? lianLi.MountingOf(d.Id).Settings : null;
        return new ControllerDto(d.Id.Value, d.Vendor, d.Name, d.Class.ToString(), d.DeviceType.ToString(), d.Serial,
            d.Zones.Select(z => ToDto(z, m?.ReverseChain == true) with
            {
                RingOffsetLeds = m?.RingOffsetLeds ?? 0,
                HalfStep = m?.HalfStep == true,
                FaceOnFill = m?.FaceOnFill == true,
            }).ToArray(), d.Capabilities.MaxFps, state, e.BringupLease ?? e.Verdict.Writer,
            e.Adapter is not INativeBringup native || native.IsControlVerified(d.Id),
            ToDto(ReadPerKeyFrame(e)));
    }

    // Review data only: a failing diagnostics read must not hide the controller from the list.
    private static PerKeyFrame? ReadPerKeyFrame(ControllerEntry e)
    {
        if (e.Adapter is not IPerKeyFrameSource source) return null;
#pragma warning disable CA1031 // A per-key review frame is optional; any failure only omits it.
        try { return source.ReadPerKeyFrame(e.Descriptor.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
#pragma warning restore CA1031
    }

    public static ZoneDto ToDto(ZoneDescriptor z) => ToDto(z, false);

    public static ZoneDto ToDto(ZoneDescriptor z, bool chainReversed) =>
        new(z.Id.Value, z.Name, z.Layout.Kind.ToString(), z.Layout.LedCount, z.Layout.Width, z.Layout.Height, z.Family, z.Units, chainReversed)
        {
            Mapped = z.Mapped,
            PerKey = ToDto(z.PerKey),
        };

    public static PerKeyStatusDto? ToDto(PerKeyCapability? capability) => capability is null ? null
        : new(capability.State.ToString(), capability.Reason, capability.Transport, capability.KeyCount, capability.MaximumKeys,
            PerKeyRestorationStates.Unproven, RestorationNotReported);

    public static PerKeyFrameDto? ToDto(PerKeyFrame? frame) => frame is null ? null
        : new(frame.Index, frame.Keys.Select(key => new PerKeyPixelDto(key.LedId, key.Key, key.Row, key.Column, FormatColor(key.Color))).ToArray());

    public static EffectDto ToDto(IEffectGenerator g) =>
        new(g.Id, g.Schema.DisplayName, g.Schema.MinColors, g.Schema.MaxColors, g.Schema.UsesSpeed, g.Schema.UsesDirection,
            g.Schema.Wireless19, g.Schema.DefaultColors.Select(FormatColor).ToArray());

    public static SceneDto ToDto(Scene scene) =>
        new(scene.Assignments.Select(a => new AssignmentDto(
            a.Targets.Select(t => new ZoneRefDto(t.Controller.Value, t.Zone.Value)).ToArray(),
            a.EffectId,
            a.Params.Colors.Select(FormatColor).ToArray(),
            a.Params.Speed,
            a.Params.Brightness,
            a.Params.Direction == Direction.Reverse,
            a.Params.Seed,
            a.GroupName,
            a.Policy.ToString(),
            a.Strategy.ToString(),
            a.FrameSetId,
            ToDto(a.Path),
            ToDto(a.LoopReturn))).ToArray(),
            Package: scene.Package is { } pk ? new ScenePackageRefDto(pk.ContentId, pk.BindingId, pk.PlaybackContract) : null);

    /// <summary>STACK-RETURN: null stays null (older profiles and every assignment without a return).</summary>
    public static LoopReturnDto? ToDto(LoopReturn? r) =>
        r is null ? null : new LoopReturnDto(r.DurationMs, r.HoldMs, r.WaveWidthDots, r.WaveStrength);

    /// <summary>STACK-RETURN: the typed options; ranges and admission are checked by <see cref="Assignment.ValidateLoopReturn"/>.</summary>
    public static LoopReturn? FromDto(LoopReturnDto? r) =>
        r is null ? null : new LoopReturn(r.DurationMs, r.HoldMs, r.WaveWidthDots, r.WaveStrength);

    /// <summary>SPATIAL-PATH: null stays null (older profiles and every assignment without a path).</summary>
    public static SpatialPathDto? ToDto(SpatialPath? path) =>
        path is null
            ? null
            : new SpatialPathDto(path.Stages.Select(s => new SpatialStageDto(s.Length,
                s.Bindings.Select(b => new SpatialBindingDto(new ZoneRefDto(b.Target.Controller.Value, b.Target.Zone.Value), b.Reverse)).ToArray())).ToArray());

    /// <summary>Throws ArgumentException for a null stage list, stage, binding list, binding or target.</summary>
    public static SpatialPath? FromDto(SpatialPathDto? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.Stages is null)
        {
            throw new ArgumentException("A spatial path needs a stage list.", nameof(path));
        }

        return new SpatialPath(path.Stages.Select((s, i) =>
        {
            if (s?.Bindings is null)
            {
                throw new ArgumentException($"Spatial stage {i} needs a binding list.", nameof(path));
            }

            return new SpatialStage(s.Length, s.Bindings.Select(b => b?.Target is { } t && t.ControllerId is not null
                ? new SpatialBinding(new ZoneRef(new ControllerId(t.ControllerId), new ZoneId(t.ZoneId)), b.Reverse)
                : throw new ArgumentException($"Spatial stage {i} has a binding without a target.", nameof(path))).ToArray());
        }).ToArray());
    }

    /// <summary>Throws ArgumentException on malformed input; zone/effect existence is checked by RenderLoop.SetScene.</summary>
    public static Scene FromDto(SceneDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(dto.Assignments, nameof(dto));
        // OPUS-GENERIC-SCENE-PACKAGES: provenance pins content/binding/contract; null (every legacy scene) stays null
        var package = dto.Package is { } pk ? FromDto(pk) : null;
        return new Scene(dto.Assignments.Select(FromDto).ToArray()) { Package = package };
    }

    /// <summary>A package reference needs all three fields; nothing is resolved here (the frame source does that).</summary>
    public static ScenePackageProvenance FromDto(ScenePackageRefDto pk)
    {
        ArgumentNullException.ThrowIfNull(pk);
        if (string.IsNullOrWhiteSpace(pk.ContentId) || string.IsNullOrWhiteSpace(pk.BindingId) || string.IsNullOrWhiteSpace(pk.PlaybackContract))
        {
            throw new ArgumentException("A scene package reference needs a content id, a binding id and a playback contract.", nameof(pk));
        }

        return new ScenePackageProvenance(pk.ContentId, pk.BindingId, pk.PlaybackContract);
    }

    public static Assignment FromDto(AssignmentDto a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Targets is null || a.Targets.Count == 0)
        {
            throw new ArgumentException("Assignment needs at least one target zone.", nameof(a));
        }

        if (string.IsNullOrWhiteSpace(a.EffectId))
        {
            throw new ArgumentException("Assignment needs an effect id.", nameof(a));
        }

        CheckUnit(a.Speed, "Speed");
        CheckUnit(a.Brightness, "Brightness");
        var colors = (a.Colors ?? []).Select(ParseColor).ToArray();
        var p = new EffectParams(colors, a.Speed, a.Brightness, a.Reverse ? Direction.Reverse : Direction.Forward, a.Seed);
        var targets = a.Targets.Select(t => new ZoneRef(new ControllerId(t.ControllerId), new ZoneId(t.ZoneId))).ToArray();
        var strategy = a.Strategy is not null ? ParseStrategy(a.Strategy) : Assignment.FromPolicy(ParsePolicy(a.LoopPolicy));
        if (strategy == ContentStrategy.Frames && string.IsNullOrWhiteSpace(a.FrameSetId))
        {
            throw new ArgumentException("A Frames assignment needs a frame set id.", nameof(a));
        }

        var assignment = new Assignment(targets, a.EffectId, p, a.GroupName, strategy)
        {
            FrameSetId = a.FrameSetId,
            Path = FromDto(a.SpatialPath),
            LoopReturn = FromDto(a.LoopReturn),
        };
        assignment.Validate(); // SPATIAL-PATH / STACK-RETURN: rejected before activation or persistence (SaveProfile maps first)
        return assignment;
    }

    /// <summary>NATIVE design §7.2: "Vendor", "Program" or "Frames" (case-insensitive); numbers and other names are rejected.</summary>
    /// <summary>The manual streamed reference-clock calibration (NATIVE-PLAYBACK-CALIBRATION C5).</summary>
    public static PlaybackCalibrationDto ToDto(PlaybackCalibration c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new PlaybackCalibrationDto(c.StreamedPlaybackRate, c.PhaseTrimMs);
    }

    /// <summary>NATIVE-RF-CLOCK: the streamed reference clock of each assignment (Diagnostics; rounded for display).</summary>
    public static RfClockStatusDto ToDto(bool enabled, IReadOnlyList<StreamedClockReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        return new RfClockStatusDto(enabled, reports.Select(r => new StreamedClockDto(
            r.Assignment,
            r.Mode.ToString(),
            r.FallbackReason,
            r.Domain,
            r.RawTicks,
            r.SampleAgeSeconds is { } age ? Math.Round(age * 1000.0, 3) : null,
            r.QueryLatencyMs is { } q ? Math.Round(q, 3) : null,
            r.TicksPerSecond,
            r.ExtrapolatedTicks is { } x ? Math.Round(x, 1) : null,
            r.ReferenceFrames,
            r.ReferenceInterval,
            Math.Round(r.NominalPeriodMs, 3),
            r.DerivedPeriodMs is { } d ? Math.Round(d, 3) : null,
            r.WirePeriodsDiffer,
            r.ReferenceFrame,
            r.UploadedFrames,
            r.UploadedInterval)).ToArray());
    }

    /// <summary>Unvalidated: <see cref="RenderLoop.SetPlaybackCalibration"/> validates both values before anything changes.</summary>
    public static PlaybackCalibration FromDto(PlaybackCalibrationDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new PlaybackCalibration(dto.StreamedPlaybackRate, dto.PhaseTrimMs);
    }

    public static ContentStrategy ParseStrategy(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.All(char.IsAsciiLetter) || !Enum.TryParse<ContentStrategy>(s, ignoreCase: true, out var v) || !Enum.IsDefined(v))
        {
            throw new ArgumentException($"Strategy '{s}' is not one of {string.Join(", ", Enum.GetNames<ContentStrategy>())}.", nameof(s));
        }

        return v;
    }

    /// <summary>Absent → LConnect (profiles written before WP-11); otherwise a defined name, numbers rejected.</summary>
    public static LoopPolicy ParsePolicy(string? s)
    {
        if (s is null)
        {
            return LoopPolicy.LConnect;
        }

        if (!s.All(char.IsAsciiLetter) || !Enum.TryParse<LoopPolicy>(s, ignoreCase: true, out var policy) || !Enum.IsDefined(policy))
        {
            throw new ArgumentException($"Loop policy '{s}' is not one of {string.Join(", ", Enum.GetNames<LoopPolicy>())}.", nameof(s));
        }

        return policy;
    }

    public static string FormatColor(Rgb c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    public static Rgb ParseColor(string s)
    {
        var t = s?.Trim().TrimStart('#') ?? string.Empty;
        if (t.Length != 6 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            throw new ArgumentException($"Color '{s}' is not #RRGGBB.", nameof(s));
        }

        return Rgb.FromUInt32(v);
    }

    private static void CheckUnit(float v, string name)
    {
        if (!(v >= 0f && v <= 1f))
        {
            throw new ArgumentException($"{name} must be within 0..1 (got {v.ToString(CultureInfo.InvariantCulture)}).", name);
        }
    }
}

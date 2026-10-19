using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using StreamJsonRpc;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Cli;

/// <summary>
/// Thin JSON-RPC client for scripting and HW checks.
/// <code>
/// urgb [--pipe NAME] list | effects | status [--json] | scene get
/// urgb [--pipe NAME] scene set EFFECT [--targets all|CTRL:ZONE,...] [--colors #RRGGBB,...] [--speed 0..1]
///                                     [--brightness 0..1] [--reverse] [--seed N] [--group NAME]
/// urgb [--pipe NAME] scene load FILE.json
/// </code>
/// Exit codes: 0 ok, 1 RPC/connection error, 2 usage error.
/// </summary>
public static class CliApp
{
    private static readonly JsonSerializerOptions Json = ContractsJson.Configure(new JsonSerializerOptions { WriteIndented = true });

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, PipeTrust trust = PipeTrust.ElevatedOwner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var a = new List<string>(args);
        var pipeName = Take(a, "--pipe") ?? PipeNames.Default;
        if (a.Count == 0 || a[0] is "-h" or "--help" or "help")
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return a.Count == 0 ? 2 : 0;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, ct).ConfigureAwait(false);
            PipeServerIdentity.Verify(pipe, trust);
            var formatter = new SystemTextJsonFormatter();
            ContractsJson.Configure(formatter.JsonSerializerOptions);
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, formatter));
            var svc = rpc.Attach<IRgbService>();
            rpc.StartListening();
            return await DispatchAsync(svc, a, stdout, stderr, ct).ConfigureAwait(false);
        }
        catch (UsageException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
        catch (TimeoutException)
        {
            await stderr.WriteLineAsync($"Cannot connect to \\\\.\\pipe\\{pipeName} (is UnifiedRgb.Host running?)").ConfigureAwait(false);
            return 1;
        }
        catch (RemoteInvocationException ex)
        {
            await stderr.WriteLineAsync($"Host rejected the request: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConnectionLostException)
        {
            await stderr.WriteLineAsync($"Pipe error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private const string Usage =
        "urgb [--pipe NAME] list | effects | status [--json] | scene get\n" +
        "urgb [--pipe NAME] scene set EFFECT [--targets all|CTRL:ZONE,...] [--colors #RRGGBB,...] [--speed 0..1] [--brightness 0..1] [--reverse] [--seed N] [--group NAME]\n" +
        "urgb [--pipe NAME] scene load FILE.json";

    private static async Task<int> DispatchAsync(IRgbService svc, List<string> a, TextWriter o, TextWriter e, CancellationToken ct)
    {
        switch (a[0])
        {
            case "list":
                foreach (var c in await svc.ListControllersAsync(ct).ConfigureAwait(false))
                {
                    var zones = string.Join(", ", c.Zones.Select(z => $"{z.Id}:{z.Name}[{z.Layout} {z.LedCount}]"));
                    var by = c.BlockedBy is null ? string.Empty : $" by {c.BlockedBy}";
                    await o.WriteLineAsync($"{c.Id,-24} {c.State + by,-12} {c.MaxFps,3} fps  {c.Name}  {zones}").ConfigureAwait(false);
                }

                return 0;

            case "effects":
                foreach (var fx in await svc.ListEffectsAsync(ct).ConfigureAwait(false))
                {
                    await o.WriteLineAsync($"{fx.Id,-18} colors {fx.MinColors}-{fx.MaxColors}{(fx.Wireless ? "  wireless" : string.Empty)}").ConfigureAwait(false);
                }

                return 0;

            case "status":
                var st = await svc.GetStatusAsync(a.Contains("--diag"), ct).ConfigureAwait(false);
                if (a.Contains("--json"))
                {
                    await o.WriteLineAsync(JsonSerializer.Serialize(st, Json)).ConfigureAwait(false);
                    return 0;
                }

                await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"uptime {st.UptimeSeconds:F0}s  frames {st.FramesRendered}  clock {st.Clock.Fps:F1}/{st.Clock.TargetFps} fps  jitter p50 {st.Clock.JitterP50Ms:F2} ms p99 {st.Clock.JitterP99Ms:F2} ms  overruns {st.Clock.Overruns}")).ConfigureAwait(false);
                foreach (var d in st.Devices)
                {
                    await o.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{d.Id,-24} {d.State,-10} {d.Fps,5:F1}/{d.MaxFps} fps  written {d.Written}  dropped {d.Dropped}  superseded {d.Superseded}{(d.LastError is null ? string.Empty : "  last error: " + d.LastError)}")).ConfigureAwait(false);
                }

                return 0;

            case "scene" when a.Count >= 2 && a[1] == "get":
                await o.WriteLineAsync(JsonSerializer.Serialize(await svc.GetSceneAsync(ct).ConfigureAwait(false), Json)).ConfigureAwait(false);
                return 0;

            case "scene" when a.Count >= 3 && a[1] == "load":
                var scene = JsonSerializer.Deserialize<SceneDto>(await File.ReadAllTextAsync(a[2], ct).ConfigureAwait(false), Json)
                    ?? throw new UsageException("Scene file is empty.");
                await svc.SetSceneAsync(scene, ct).ConfigureAwait(false);
                await o.WriteLineAsync("Scene applied.").ConfigureAwait(false);
                return 0;

            case "scene" when a.Count >= 3 && a[1] == "set":
                var assignment = await BuildAssignmentAsync(svc, a, ct).ConfigureAwait(false);
                await svc.SetSceneAsync(new SceneDto([assignment]), ct).ConfigureAwait(false);
                await o.WriteLineAsync($"Scene applied: {assignment.EffectId} on {assignment.Targets.Count} zone(s).").ConfigureAwait(false);
                return 0;

            default:
                await e.WriteLineAsync(Usage).ConfigureAwait(false);
                return 2;
        }
    }

    private static async Task<AssignmentDto> BuildAssignmentAsync(IRgbService svc, List<string> a, CancellationToken ct)
    {
        var effect = a[2];
        var targetsArg = Take(a, "--targets") ?? "all";
        var colors = (Take(a, "--colors") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var speed = ParseUnit(Take(a, "--speed"), 0.5f, "--speed");
        var brightness = ParseUnit(Take(a, "--brightness"), 1f, "--brightness");
        var seedArg = Take(a, "--seed");
        var seed = seedArg is null ? 1u : uint.TryParse(seedArg, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : throw new UsageException("--seed must be a non-negative integer.");
        var group = Take(a, "--group");
        var reverse = a.Contains("--reverse");

        List<ZoneRefDto> targets;
        if (targetsArg == "all")
        {
            var controllers = await svc.ListControllersAsync(ct).ConfigureAwait(false);
            targets = controllers.Where(c => c.State == ControllerStates.Active)
                .SelectMany(c => c.Zones.Select(z => new ZoneRefDto(c.Id, z.Id))).ToList();
            group ??= "All";
        }
        else
        {
            targets = targetsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseZone).ToList();
        }

        if (targets.Count == 0)
        {
            throw new UsageException("No target zones.");
        }

        return new AssignmentDto(targets, effect, colors, speed, brightness, reverse, seed, group);
    }

    /// <summary>"virtual:fan1:0" → controller "virtual:fan1", zone 0 (the zone index is after the last colon).</summary>
    private static ZoneRefDto ParseZone(string s)
    {
        var i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var zone))
        {
            throw new UsageException($"Target '{s}' must be CONTROLLER:ZONE.");
        }

        return new ZoneRefDto(s[..i], zone);
    }

    private static float ParseUnit(string? s, float fallback, string name)
    {
        if (s is null)
        {
            return fallback;
        }

        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 1
            ? v
            : throw new UsageException($"{name} must be within 0..1.");
    }

    private static string? Take(List<string> a, string name)
    {
        var i = a.IndexOf(name);
        if (i < 0)
        {
            return null;
        }

        if (i + 1 >= a.Count)
        {
            throw new UsageException($"{name} needs a value.");
        }

        var v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    private sealed class UsageException(string message) : Exception(message);
}

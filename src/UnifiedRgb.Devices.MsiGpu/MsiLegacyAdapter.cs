using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Devices.Safety;
using UnifiedRgb.Transports.Nvapi;
using UnifiedRgb.Transports.Safety;

namespace UnifiedRgb.Devices.MsiGpu;

/// <summary>
/// Dormant legacy profile (<see cref="MsiGpuOptions.LegacyGlobal"/>): the provisional MSIGPUv2 register map, gated by
/// <see cref="MsiGpuOptions.RegisterMapVerified"/>. Not the 5090 path (that is <see cref="MsiNv5000Adapter"/>). Direct mode is
/// one Point zone, latest frame wins, ≤ 20 fps. Every write passes I2cRegisterFirewall → FrameValidator → RateLimiter(20/s).
/// Unchanged colors are not resent except a 1 s keep-alive.
/// </summary>
public sealed class MsiLegacyAdapter : IDeviceAdapter
{
    private static readonly long RefreshTicks = Stopwatch.Frequency;

    private readonly INvapi _nvapi;
    private readonly MsiGpuOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<ControllerId, MsiGpuDevice> _found = [];
    private readonly Dictionary<ControllerId, Session> _open = [];
    private readonly object _gate = new();

    public MsiLegacyAdapter(INvapi nvapi, MsiGpuOptions options, ILogger? logger = null)
    {
        _nvapi = nvapi ?? throw new ArgumentNullException(nameof(nvapi));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
    }

    public event Action<HotplugEvent>? Hotplug
    {
        add { }
        remove { }
    }

    public string Vendor => "MSI";

    public DeviceClass Class => DeviceClass.MsiGpu;

    public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        var devices = MsiGpuDiscovery.Discover(_nvapi);
        lock (_gate)
        {
            _found.Clear();
            foreach (var d in devices)
            {
                _found[d.Descriptor.Id] = d;
            }
        }

        return Task.FromResult<IReadOnlyList<ControllerDescriptor>>(devices.Select(d => d.Descriptor).ToArray());
    }

    public Task OpenAsync(ControllerId id, CancellationToken ct)
    {
        if (!_options.RegisterMapVerified)
        {
            throw new InvalidOperationException(
                "MSI GPU register map is not verified on hardware (MsiGpu:RegisterMapVerified=false); the GPU is not opened.");
        }

        MsiGpuDevice dev;
        lock (_gate)
        {
            dev = _found.TryGetValue(id, out var d) ? d : throw new ArgumentException($"Unknown MSI GPU {id}", nameof(id));
        }

        var session = new Session(BuildChain(new NvapiI2cTransport(_nvapi, dev.Handle), new DeviceState(), _logger));
        foreach (var op in MsiGpuEncoder.EnterDirect())
        {
            session.Send(op);
        }

        lock (_gate)
        {
            _open[id] = session;
        }

        return Task.CompletedTask;
    }

    /// <summary>Firewall → validator → rate limiter, exactly the decorator order of IMPLEMENTATION_PLAN §1.</summary>
    public static ITransport BuildChain(ITransport inner, IDeviceState state, ILogger? logger = null)
    {
        var rules = new FrameValidationRules { MinLength = 3, MaxLength = 5 };
        ITransport t = new I2cRegisterFirewall(new PacketTap(inner, "MsiGpu", f => f.Length > 1 ? f.Span[1] : -1), MsiGpuV2Registers.I2cAddress, MsiGpuV2Registers.Writable, logger);
        t = new FrameValidator(t, rules, state);
        return new RateLimiter(t, RateLimits.NvapiI2cFps);
    }

    public void WriteFrame(ControllerId id, ZoneId zone, ReadOnlySpan<Rgb> colors)
    {
        var s = Get(id);
        if (zone.Value != 0 || colors.Length != 1)
        {
            throw new ArgumentException("MSI GPU has one single-LED zone.", nameof(colors));
        }

        var now = Stopwatch.GetTimestamp();
        if (s.InDirect && s.Last == colors[0] && now - s.LastSent < RefreshTicks)
        {
            return;
        }

        if (!s.InDirect)
        {
            foreach (var op in MsiGpuEncoder.EnterDirect())
            {
                s.Send(op);
            }

            s.InDirect = true;
        }

        s.Send(MsiGpuEncoder.DirectColor(colors[0]));
        s.Last = colors[0];
        s.LastSent = now;
    }

    public Task SetFirmwareModeAsync(ControllerId id, FirmwareMode mode, CancellationToken ct)
    {
        var s = Get(id);
        var ops = MsiGpuEncoder.FirmwareMode(mode);
        foreach (var op in ops)
        {
            s.Send(op);
        }

        s.InDirect = false;
        return Task.CompletedTask;
    }

    public Telemetry? ReadTelemetry(ControllerId id)
    {
        MsiGpuDevice? dev;
        lock (_gate)
        {
            _found.TryGetValue(id, out dev);
        }

        return dev is null ? null : GpuTelemetry.Read(_nvapi, dev.Handle);
    }

    public Task CloseAsync(ControllerId id)
    {
        lock (_gate)
        {
            _open.Remove(id);
        }

        return Task.CompletedTask;
    }

    private Session Get(ControllerId id)
    {
        lock (_gate)
        {
            return _open.TryGetValue(id, out var s) ? s : throw new InvalidOperationException($"{id} is not open");
        }
    }

    private sealed class Session(ITransport chain)
    {
        public bool InDirect = true;
        public Rgb Last;
        public long LastSent = long.MinValue / 2;

        public void Send(I2cWrite op)
        {
            var vt = chain.SendAsync(op.ToFrame(), CancellationToken.None);
            if (!vt.IsCompletedSuccessfully)
            {
                vt.AsTask().GetAwaiter().GetResult();
            }
        }
    }
}

public static class GpuTelemetry
{
    /// <summary>Read-only temperature; never throws (telemetry is a bonus feature).</summary>
    public static Telemetry? Read(INvapi nvapi, IntPtr gpu)
    {
        ArgumentNullException.ThrowIfNull(nvapi);
        try
        {
            var t = nvapi.GetGpuTemperature(gpu);
            return t is null ? null : new Telemetry(DateTimeOffset.UtcNow, [], t.Value);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed class MsiGpuAdapterFactory : IDeviceAdapterFactory
{
    public DeviceClass Class => DeviceClass.MsiGpu;

    public IDeviceAdapter Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        var options = sp.GetService(typeof(MsiGpuOptions)) as MsiGpuOptions ?? new MsiGpuOptions();
        var loggers = sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        return Create(new NvapiNative(), options, loggers, sp.GetService(typeof(INativeMapSource)) as INativeMapSource);
    }

    /// <summary>Selects the adapter by <see cref="MsiGpuOptions.Profile"/>; an unknown profile is refused, never substituted.</summary>
    public static IDeviceAdapter Create(INvapi nvapi, MsiGpuOptions options, ILoggerFactory? loggers = null, INativeMapSource? maps = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Profile switch
        {
            MsiGpuOptions.Nv5000Indexed => new MsiNv5000Adapter(nvapi, options, loggers?.CreateLogger<MsiNv5000Adapter>(), maps),
            MsiGpuOptions.LegacyGlobal => new MsiLegacyAdapter(nvapi, options, loggers?.CreateLogger<MsiLegacyAdapter>()),
            _ => throw new ArgumentException(
                $"MsiGpu:Profile '{options.Profile}' is not {MsiGpuOptions.Nv5000Indexed} or {MsiGpuOptions.LegacyGlobal}; no adapter is substituted.",
                nameof(options)),
        };
    }
}

using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Screens.Telemetry;

/// <summary>
/// Samples PC metrics on demand (thread-safe). CPU % is the busy share of all logical processors between this sample and the
/// previous one, so the very first sample has no CPU value. A metric that cannot be read is null, never 0. The GPU reader is
/// created lazily on the first sample when enabled; a failed creation is retried at most once a minute, and a reader that
/// starts failing is dropped and recreated later.
/// </summary>
public sealed class PcTelemetrySampler : IDisposable
{
    public const string BaseSource = "GetSystemTimes+GlobalMemoryStatusEx";

    private static readonly TimeSpan GpuRetry = TimeSpan.FromMinutes(1);

    private readonly ICpuMemoryReader _os;
    private readonly Func<(IGpuReader? Reader, string? Error)>? _gpuFactory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CpuTimes? _previousCpu;
    private IGpuReader? _gpu;
    private string? _gpuError;
    private long _gpuAttemptAt;
    private bool _gpuAttempted;
    private PcTelemetryDto? _latest;
    private bool _disposed;

    /// <param name="gpuFactory">Null disables GPU metrics.</param>
    public PcTelemetrySampler(ICpuMemoryReader os, Func<(IGpuReader? Reader, string? Error)>? gpuFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(time);
        _os = os;
        _gpuFactory = gpuFactory;
        _time = time;
    }

    /// <summary>The most recent sample, or null before the first.</summary>
    public PcTelemetryDto? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public PcTelemetryDto Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetUtcNow();
            var errors = new List<string>(2);

            double? cpu = null;
            var times = _os.ReadCpuTimes();
            if (times is { } t && _previousCpu is { } p && t.Kernel >= p.Kernel && t.User >= p.User && t.Idle >= p.Idle)
            {
                var total = (t.Kernel - p.Kernel) + (t.User - p.User);
                var idle = t.Idle - p.Idle;
                if (total > 0 && idle <= total)
                {
                    cpu = Math.Round((total - idle) * 100.0 / total, 1);
                }
            }
            else if (times is null)
            {
                errors.Add("GetSystemTimes failed");
            }

            _previousCpu = times ?? _previousCpu;

            long? memUsed = null;
            long? memTotal = null;
            if (_os.ReadMemory() is { } m)
            {
                memTotal = m.TotalBytes;
                memUsed = m.TotalBytes - m.AvailableBytes;
            }
            else
            {
                errors.Add("GlobalMemoryStatusEx failed");
            }

            var gpu = ReadGpu();
            if (gpu.Error is { } ge)
            {
                errors.Add(ge);
            }

            var source = _gpu is null ? BaseSource : BaseSource + "+" + _gpu.Name;
            _latest = new PcTelemetryDto(now, cpu, memUsed, memTotal, gpu.UsagePercent, gpu.TemperatureC, gpu.MemoryUsedBytes,
                gpu.MemoryTotalBytes, source, errors.Count == 0 ? null : string.Join("; ", errors));
            return _latest;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _gpu?.Dispose();
            _gpu = null;
        }
    }

    private GpuSample ReadGpu()
    {
        if (_gpuFactory is null)
        {
            return default;
        }

        if (_gpu is null)
        {
            if (_gpuAttempted && _time.GetElapsedTime(_gpuAttemptAt) < GpuRetry)
            {
                return new GpuSample(null, null, null, null, _gpuError);
            }

            _gpuAttempted = true;
            _gpuAttemptAt = _time.GetTimestamp();
            try
            {
                (_gpu, _gpuError) = _gpuFactory();
            }
#pragma warning disable CA1031 // optional metrics: any loader failure only disables the GPU fields
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _gpu = null;
                _gpuError = "GPU metrics unavailable: " + ex.Message;
            }

            if (_gpu is null)
            {
                return new GpuSample(null, null, null, null, _gpuError ?? "GPU metrics unavailable");
            }
        }

        try
        {
            var s = _gpu.Read();
            if (s.UsagePercent is null && s.TemperatureC is null && s.MemoryTotalBytes is null)
            {
                // nothing readable (GPU lost, driver reset): drop the reader and retry creation later
                DropGpu(s.Error ?? "GPU metrics unavailable");
            }

            return s;
        }
#pragma warning disable CA1031 // optional metrics
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DropGpu("GPU read failed: " + ex.Message);
            return new GpuSample(null, null, null, null, _gpuError);
        }
    }

    private void DropGpu(string error)
    {
        _gpu?.Dispose();
        _gpu = null;
        _gpuError = error;
        _gpuAttemptAt = _time.GetTimestamp();
    }
}

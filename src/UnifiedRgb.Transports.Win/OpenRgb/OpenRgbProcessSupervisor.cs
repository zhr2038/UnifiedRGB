using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Transports.OpenRgb;

public sealed class OpenRgbSupervisorOptions
{
    /// <summary>Pinned OpenRGB executable (default: tools/openrgb/OpenRGB.exe next to the application).</summary>
    public string ExecutablePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "openrgb", "OpenRGB.exe");

    /// <summary>"&lt;sha256&gt;  &lt;file name&gt;" lines; '#' starts a comment. A binary not listed here is never launched.</summary>
    public string HashFile { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "openrgb", "SHA256SUMS");

    /// <summary>Headless detector configuration, copied to &lt;ConfigDirectory&gt;/OpenRGB.json before every launch.</summary>
    public string ConfigTemplate { get; set; } = Path.Combine(AppContext.BaseDirectory, "openrgb-headless.json");

    public string ConfigDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UnifiedRgb", "openrgb");

    public int Port { get; set; } = TcpSdkTransport.DefaultPort;
    public int MaxConsecutiveStarts { get; set; } = 5;
    public TimeSpan InitialRestartDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumRestartDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan StablePeriod { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class OpenRgbRestartLimitException : InvalidOperationException
{
    public OpenRgbRestartLimitException() { }
    public OpenRgbRestartLimitException(string message) : base(message) { }
    public OpenRgbRestartLimitException(string message, Exception innerException) : base(message, innerException) { }
}

public interface IOwnedProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    void Kill();
}

public interface IProcessLauncher
{
    IOwnedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

public sealed class OpenRgbBinaryRejectedException : InvalidOperationException
{
    public OpenRgbBinaryRejectedException(string message)
        : base(message)
    {
    }

    public OpenRgbBinaryRejectedException()
    {
    }

    public OpenRgbBinaryRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Launches and keeps alive our own headless OpenRGB: SHA-256 pinned (verified before every launch while the file is held
/// open against writes), SDK server bound to 127.0.0.1 only, private config directory, PID registered as owned with
/// <see cref="ProcessScanner"/> so WriterGuard does not see it as a foreign writer.
/// </summary>
public sealed partial class OpenRgbProcessSupervisor : IDisposable
{
    private readonly OpenRgbSupervisorOptions _options;
    private readonly IProcessLauncher _launcher;
    private readonly ProcessScanner? _scanner;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IOwnedProcess? _process;
    private readonly TimeProvider _time;
    private long _startedAt, _nextAttempt;
    private int _consecutiveStarts;
    private bool _disposed;
    private bool _hasLaunched;

    public OpenRgbProcessSupervisor(OpenRgbSupervisorOptions options, IProcessLauncher launcher, ProcessScanner? scanner = null, ILogger? logger = null, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _scanner = scanner;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        if (options.MaxConsecutiveStarts is < 1 or > 20 || options.InitialRestartDelay < TimeSpan.Zero
            || options.MaximumRestartDelay < options.InitialRestartDelay || options.MaximumRestartDelay > TimeSpan.FromMinutes(1)
            || options.StablePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public int Restarts { get; private set; }
    public string? LastFailure { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public static IReadOnlyList<string> Arguments(OpenRgbSupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            "--server",
            "--server-host", "127.0.0.1",
            "--server-port", options.Port.ToString(CultureInfo.InvariantCulture),
            "--config", options.ConfigDirectory,
        ];
    }

    /// <summary>Parses SHA256SUMS and returns the pinned hash for <paramref name="fileName"/>, or null when not listed.</summary>
    public static string? PinnedHash(string hashFileText, string fileName)
    {
        ArgumentNullException.ThrowIfNull(hashFileText);
        foreach (var raw in hashFileText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToUpperInvariant();
            }
        }

        return null;
    }

    /// <summary>Starts OpenRGB if it is not running. Returns true when a (re)launch happened.</summary>
    public bool EnsureRunning()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false })
            {
                if (_time.GetElapsedTime(_startedAt) >= _options.StablePeriod) { _consecutiveStarts = 0; _nextAttempt = 0; LastFailure = null; }
                return false;
            }

            var restarting = _hasLaunched;
            ReleaseProcess();
            if (_consecutiveStarts >= _options.MaxConsecutiveStarts)
                throw new OpenRgbRestartLimitException(LastFailure = $"OpenRGB stopped after {_consecutiveStarts} consecutive starts without observed stability. Restart the optional adapter explicitly after resolving the cause.");
            var now = _time.GetTimestamp();
            if (now < _nextAttempt)
                throw new IOException(LastFailure = $"OpenRGB restart backoff: {1000.0 * (_nextAttempt - now) / _time.TimestampFrequency:0} ms remaining.");
            _consecutiveStarts++;
            var delay = Math.Min(_options.MaximumRestartDelay.TotalMilliseconds,
                _options.InitialRestartDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, _consecutiveStarts - 2)));
            try { _process = Launch(); }
            catch (OpenRgbBinaryRejectedException ex)
            {
                // No process was started. Keep pin failures explicit even if the owner repairs the hash file immediately.
                _consecutiveStarts--;
                _nextAttempt = 0;
                LastFailure = ex.Message;
                throw;
            }
            catch
            {
                _nextAttempt = now + (long)(delay * _time.TimestampFrequency / 1000);
                LastFailure = $"OpenRGB launch attempt {_consecutiveStarts} failed; retry is bounded by backoff and the start limit.";
                throw;
            }
            _startedAt = _time.GetTimestamp();
            _hasLaunched = true;
            _nextAttempt = _consecutiveStarts > 1 ? _startedAt + (long)(delay * _time.TimestampFrequency / 1000) : 0;
            LastFailure = null;
            _scanner?.RegisterOwnedProcess(_process.Id);
            if (restarting)
            {
                Restarts++;
                LogRestarted(_process.Id);
            }
            else
            {
                LogStarted(_process.Id, _options.Port);
            }

            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false } p)
            {
                p.Kill();
            }

            ReleaseProcess();
            _consecutiveStarts = 0; _nextAttempt = 0; LastFailure = null;
        }
    }

    public void Dispose() { lock (_gate) { if (_disposed) return; Stop(); _disposed = true; } }

    private IOwnedProcess Launch()
    {
        var exe = Path.GetFullPath(_options.ExecutablePath);
        if (!File.Exists(_options.HashFile))
        {
            throw new OpenRgbBinaryRejectedException($"Hash file {_options.HashFile} is missing; OpenRGB is not launched.");
        }

        var pinned = PinnedHash(File.ReadAllText(_options.HashFile), Path.GetFileName(exe))
            ?? throw new OpenRgbBinaryRejectedException($"{Path.GetFileName(exe)} has no pinned hash in {_options.HashFile}; OpenRGB is not launched.");

        // Hold the binary open without write/delete sharing from hashing until the process has started (no TOCTOU swap).
        using var image = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(image));
        if (!string.Equals(actual, pinned, StringComparison.Ordinal))
        {
            LogRejected(exe, actual);
            throw new OpenRgbBinaryRejectedException($"{exe} SHA-256 {actual} does not match the pinned {pinned}; OpenRGB is not launched.");
        }

        Directory.CreateDirectory(_options.ConfigDirectory);
        File.Copy(_options.ConfigTemplate, Path.Combine(_options.ConfigDirectory, "OpenRGB.json"), overwrite: true);
        return _launcher.Start(exe, Arguments(_options), Path.GetDirectoryName(exe)!);
    }

    private void ReleaseProcess()
    {
        if (_process is not null)
        {
            _scanner?.UnregisterOwnedProcess(_process.Id);
            _process.Dispose();
            _process = null;
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Headless OpenRGB started (pid {Pid}, 127.0.0.1:{Port})")]
    private partial void LogStarted(int pid, int port);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Headless OpenRGB exited; restarted as pid {Pid}")]
    private partial void LogRestarted(int pid);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Critical, Message = "Refused to launch {Path}: SHA-256 {Hash} is not pinned")]
    private partial void LogRejected(string path, string hash);
}

/// <summary>Real launcher: no shell, no window, arguments passed as a list.</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public IOwnedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {executable}");
        return new Owned(p);
    }

    private sealed class Owned(System.Diagnostics.Process p) : IOwnedProcess
    {
        public int Id => p.Id;

        public bool HasExited => p.HasExited;

        public void Kill() => p.Kill(entireProcessTree: true);

        public void Dispose() => p.Dispose();
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UnifiedRgb.Host;
using UnifiedRgb.Host.Autostart;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;
using UnifiedRgb.Contracts;

// Maintenance verbs used by tools/install.ps1 and tools/uninstall.ps1; they never start the lighting engine.
if (args.Length == 1 && args[0] is "--register-task" or "--unregister-task" or "--restore-services")
{
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
    switch (args[0])
    {
        case "--register-task":
            return ScheduledTaskInstaller.Register(exe);
        case "--unregister-task":
            return ScheduledTaskInstaller.Unregister();
        default:
            if (!HardwareHostLease.TryAcquire(out var ownership, out var failure)) { Console.Error.WriteLine(failure); return 2; }
            using (ownership)
            {
            var cfg = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).Build();
            var o = new UnifiedRgbOptions();
            cfg.GetSection(UnifiedRgbOptions.Section).Bind(o);
            using (var guard = new WriterGuard(new ProcessScanner(new WindowsSystemSnapshot()), new WindowsServiceControl(), new TakeoverStore(o)))
            {
                await guard.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return 0;
            }
    }
}

// A rejected duplicate must exit before configuration/DI can construct or start any device service.
using var startupMaintenance = InstallationGate.EnterWhenAvailable(TimeSpan.FromSeconds(30));
if (startupMaintenance is null) return 3; // installer still owns the switch; no device/DI construction
HardwareHostLease hardwareOwnership;
try { hardwareOwnership = new HardwareHostLease(); }
catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
using var heldHardwareOwnership = hardwareOwnership;
startupMaintenance.Dispose(); // no awaits while owning the thread-affine maintenance mutex
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true);

var options = new UnifiedRgbOptions();
builder.Configuration.GetSection(UnifiedRgbOptions.Section).Bind(options);
Log.Logger = HostLogging.Configure(new LoggerConfiguration(), builder.Configuration, options).CreateLogger();

builder.Services.AddSerilog();
builder.Services.AddUnifiedRgb(options);

try
{
    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
#pragma warning disable CA1031 // top-level crash barrier: log and exit non-zero
catch (Exception ex)
#pragma warning restore CA1031
{
    hardwareOwnership.RetainUntilProcessExit(); // a timed-out writer must never overlap a replacement Host before process termination
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

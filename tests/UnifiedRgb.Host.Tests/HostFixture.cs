using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Host.Ipc;
using UnifiedRgb.Host.Services;
using UnifiedRgb.Transports.Process;

namespace UnifiedRgb.Host.Tests;

[CollectionDefinition("Host", DisableParallelization = true)]
public class HostGroup
{
}

/// <summary>A real Host (Generic Host + full DI) against the Virtual adapter, own temp data dir and unique pipe name.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private TestHost(IHost host, UnifiedRgbOptions options)
    {
        App = host;
        Options = options;
    }

    public IHost App { get; }

    public UnifiedRgbOptions Options { get; }

    public string StatusPath => Path.Combine(Options.ResolvedDataDirectory, "status.json");

    public T Get<T>()
        where T : notnull => App.Services.GetRequiredService<T>();

    public static async Task<TestHost> StartAsync(Action<IServiceCollection>? configure = null, Action<UnifiedRgbOptions>? seed = null)
    {
        var options = new UnifiedRgbOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "urgb-tests", Guid.NewGuid().ToString("N")),
            PipeName = "UnifiedRgb-test-" + Guid.NewGuid().ToString("N"),
            UseVirtualAdapter = true,
        };
        seed?.Invoke(options);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddUnifiedRgb(options);

        // Tests must never reach the real SCM (vendor services run on the dev machine); a test that needs SCM injects a fake.
        builder.Services.AddSingleton<IServiceControl, RefusingServiceControl>();
        configure?.Invoke(builder.Services);
        var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);
        await host.Services.GetRequiredService<PipeServer>().Listening.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return new TestHost(host, options);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync().ConfigureAwait(false);
        App.Dispose();
        try
        {
            Directory.Delete(Options.ResolvedDataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    public LightingEngine Engine => Get<LightingEngine>();
}

internal sealed class RefusingServiceControl : IServiceControl
{
    public ServiceInfo? Query(string name) => throw new InvalidOperationException("SCM access in a test");

    public void Stop(string name, TimeSpan timeout) => throw new InvalidOperationException("SCM access in a test");

    public void Start(string name, TimeSpan timeout) => throw new InvalidOperationException("SCM access in a test");

    public void SetStartType(string name, string startType) => throw new InvalidOperationException("SCM access in a test");
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
using Xunit.Abstractions;

namespace UnifiedRgb.Host.Tests;

[Collection("Host")]
public sealed partial class KeyboardArtworkLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public void Repeated_renders_reuse_one_background_sta_thread_and_keep_dispatcher_alive_after_failure()
    {
        var threads = new List<(int ThreadId, ApartmentState Apartment, bool Background, Dispatcher Dispatcher)>();

        KeyboardStatusArtwork.Rasterize((_, _) => threads.Add(Capture()));
        Action fail = () => KeyboardStatusArtwork.Rasterize((_, _) =>
        {
            threads.Add(Capture());
            throw new InvalidOperationException("Injected drawing failure");
        });
        fail.Should().Throw<InvalidOperationException>().WithInnerException<InvalidOperationException>();
        KeyboardStatusArtwork.Rasterize((_, _) => threads.Add(Capture()));

        threads.Should().HaveCount(3);
        threads.Select(t => t.ThreadId).Distinct().Should().ContainSingle("all renders must reuse one persistent STA thread");
        threads.Select(t => t.Dispatcher).Distinct().Should().ContainSingle("all renders must reuse one persistent Dispatcher");
        threads[0].Apartment.Should().Be(ApartmentState.STA);
        threads[0].Background.Should().BeTrue("a persistent artwork worker must not block process shutdown");
        threads[0].Dispatcher.HasShutdownFinished.Should().BeFalse();

        static (int, ApartmentState, bool, Dispatcher) Capture() =>
            (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsBackground, Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void Owner_dispose_cleanly_retires_the_persistent_artwork_dispatcher()
    {
        using var renderer = new KeyboardArtworkRenderer();
        var seen = new List<int>();
        renderer.Rasterize((_, _) => seen.Add(Environment.CurrentManagedThreadId));
        renderer.Render(new(CaseStatusState.Blackout, "熄灯模式", "屏幕 1/1 上报休眠")).Should().HaveCount(6656);
        renderer.RenderLogo().Should().HaveCount(6656);
        renderer.Rasterize((_, _) => seen.Add(Environment.CurrentManagedThreadId));

        seen.Should().HaveCount(2);
        seen.Distinct().Should().ContainSingle();
        renderer.Thread.GetApartmentState().Should().Be(ApartmentState.STA);
        renderer.Thread.IsBackground.Should().BeTrue();
        renderer.Dispatcher.HasShutdownFinished.Should().BeFalse();

        renderer.Dispose();
        renderer.Dispatcher.HasShutdownFinished.Should().BeTrue("Dispose must shut down WPF's hidden window cleanly");
        renderer.Thread.IsAlive.Should().BeFalse();
        ((Action)(() => renderer.RenderLogo())).Should().Throw<ObjectDisposedException>();

        var dir = Path.Combine(Path.GetTempPath(), "UnifiedRgb-ArtworkOwner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rgb = System.Reflection.DispatchProxy.Create<IRgbService, KeyboardDisplayServiceTests.ReadProxy>();
            var screens = System.Reflection.DispatchProxy.Create<IScreenService, KeyboardDisplayServiceTests.ReadProxy>();
            var options = new UnifiedRgbOptions { DataDirectory = dir };
            KeyboardArtworkRenderer? owned;
            using (var service = new KeyboardDisplayService(options, rgb, screens, new NoopWiredFactory(), new NoopGuard()))
            {
                service.PresentCaseObservation(new(CaseStatusState.Blackout, "熄灯模式", "屏幕 1/1 上报休眠"), () => true).Should().BeTrue();
                service.EntryLogoPixels.Length.Should().Be(6656);
                owned = service.ArtworkRenderer;
                owned.Should().NotBeNull();
                owned!.Dispatcher.HasShutdownFinished.Should().BeFalse();
                owned.Thread.IsBackground.Should().BeTrue();
            }
            owned!.Dispatcher.HasShutdownFinished.Should().BeTrue("KeyboardDisplayService.Dispose must shut down its artwork STA worker");
            owned.Thread.IsAlive.Should().BeFalse();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class NoopWiredFactory : IRogWiredOledFactory
    {
        public RogWiredOledConnection? TryOpen(string deviceKey) => null;
    }

    private sealed class NoopGuard : IWriterGuard
    {
        public GuardVerdict Check(DeviceClass deviceClass) => GuardVerdict.Clear;
        public bool IsTakeoverEnabled(DeviceClass deviceClass) => false;
        public Task<TakeoverResult> SetTakeoverAsync(DeviceClass deviceClass, bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task<TakeoverResult> TakeoverAsync(DeviceClass deviceClass, CancellationToken ct) => throw new NotSupportedException();
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Repeated_real_artwork_and_captions_do_not_accumulate_window_atoms_or_handles()
    {
        for (var i = 0; i < 8; i++) Draw(i);
        Collect();
        var before = Resources();
        for (var i = 0; i < 512; i++) Draw(i);
        Collect();
        var after = Resources();
        output.WriteLine($"1024 renders: USER artwork/caption atoms {before.Atoms}->{after.Atoms}, handles {before.Handles}->{after.Handles}");
        after.Atoms.Should().BeLessThanOrEqualTo(before.Atoms,
            "neither the full OLED frame nor the scene caption may leave a registered window class after its STA exits");
        after.Handles.Should().BeLessThanOrEqualTo(before.Handles + 32,
            "1024 offscreen renders must have bounded native resource use after warmup");
    }

    private static void Draw(int index)
    {
        KeyboardCodexTaskArtwork.Render(new(CodexTaskViewKind.Task, "光域任务窗口资源回归检查", "00:03:15", 1, 2,
            TimeSpan.FromSeconds(index))).Should().HaveCount(6656);
        KeyboardSceneCycleOverlay.RenderStrip("场景 " + index, "已应用").Should().HaveCount(KeyboardSceneCycleOverlay.StripBytes);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static unsafe (int Atoms, int Handles) Resources()
    {
        Span<char> name = stackalloc char[256];
        var atoms = 0;
        fixed (char* buffer = name)
        {
            for (uint atom = 0xc000; atom <= 0xffff; atom++)
            {
                var length = GetClipboardFormatName(atom, buffer, name.Length);
                ReadOnlySpan<char> text = name[..length];
                if (length > 0 && (text.Contains(";Keyboard artwork;", StringComparison.Ordinal)
                    || text.Contains(";Keyboard scene caption;", StringComparison.Ordinal))) atoms++;
            }
        }
        using var process = Process.GetCurrentProcess();
        return (atoms, process.HandleCount);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetClipboardFormatNameW")]
    private static unsafe partial int GetClipboardFormatName(uint format, char* name, int count);
}

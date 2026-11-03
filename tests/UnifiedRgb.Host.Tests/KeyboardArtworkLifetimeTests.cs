using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using UnifiedRgb.Contracts;
using UnifiedRgb.Host.Services;
using Xunit.Abstractions;

namespace UnifiedRgb.Host.Tests;

[CollectionDefinition("Keyboard artwork lifetime", DisableParallelization = true)]
public sealed class KeyboardArtworkLifetimeGroup;

[Collection("Keyboard artwork lifetime")]
public sealed partial class KeyboardArtworkLifetimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rendering_retires_its_dispatcher_on_success_and_drawing_failure(bool fail)
    {
        Dispatcher? dispatcher = null;
        Action render = () => KeyboardStatusArtwork.Rasterize((_, _) =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            if (fail) throw new InvalidOperationException("Injected drawing failure");
        });

        if (fail) render.Should().Throw<InvalidOperationException>().WithInnerException<InvalidOperationException>();
        else render();

        dispatcher.Should().NotBeNull();
        dispatcher!.HasShutdownFinished.Should().BeTrue("thread termination alone does not dispose WPF's hidden window");
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

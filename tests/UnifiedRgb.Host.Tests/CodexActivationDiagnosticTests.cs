using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CSharp.RuntimeBinder;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexActivationDiagnosticTests
{
    private const string Explorer = @"C:\Windows\explorer.exe";
    private const int FailureCode = unchecked((int)0x80004002);
    private static CodexProcessIdentity Identity(bool elevated) => new(elevated ? 50u : 100u, "S-1-5-21-fake", 2,
        elevated ? 0x3000u : 0x2000u, elevated, false, true, elevated ? @"C:\fake\host.exe" : Explorer);
    private sealed class Platform : ICodexActivationPlatform
    {
        internal string? FaultStage;
        // Deliberately simulate the COM runtime's native error without making a native call.
#pragma warning disable CA2201
        internal Exception Fault = new COMException("FAKE_SECRET_MUST_NOT_BE_RETAINED", FailureCode);
#pragma warning restore CA2201
        internal Desktop Desktop { get; } = new();
        public string ExplorerPath => Explorer;
        public CodexProcessIdentity? Caller() => FaultStage == "Caller.Identity" ? throw Fault : Identity(true);
        public ICodexDesktopLease? AcquireDesktop() => FaultStage == "Desktop.Acquire" ? throw Fault : Desktop;
        public CodexActivationResult ActivateApplication(string aumid, string? arguments, uint options) =>
            throw new InvalidOperationException("High fallback must never be called");
    }
    private sealed class Desktop : ICodexDesktopLease
    {
        internal Exception? OpenFailure;
        internal Automation Automation { get; } = new();
        public CodexProcessIdentity Identity => CodexActivationDiagnosticTests.Identity(false);
        public bool OwnsWindow(nint window) => window is 101 or 102;
        public bool Revalidate() => true;
        public ICodexDesktopAutomation OpenAutomation() => OpenFailure is { } failure ? throw failure : Automation;
        public void Dispose() { }
    }
    private sealed class Automation : ICodexDesktopAutomation
    {
        internal int Calls;
        internal Exception? Failure;
        internal CodexActivationResult Result = new("Requested", 0);
        public nint BrowserWindow => 101;
        public nint ViewWindow => 102;
        public CodexActivationResult ShellExecute(string file, string arguments, string directory, string verb, int show)
        { Calls++; return Failure is { } failure ? throw failure : Result; }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("Caller.Identity")]
    [InlineData("Desktop.Acquire")]
    public async Task Pre_dispatch_query_failure_preserves_stage_type_and_HResult_without_message(string stage)
    {
        var platform = new Platform { FaultStage = stage };
        var result = await new WindowsCodexWindowActivator(platform).RequestAsync();
        result.Status.Should().Be("Unknown"); result.Stage.Should().Be(stage); result.HResult.Should().Be(FailureCode);
        result.ExceptionType.Should().Be(typeof(COMException).FullName); result.DispatchAttempted.Should().BeFalse();
        JsonSerializer.Serialize(result).Should().NotContain("FAKE_SECRET"); platform.Desktop.Automation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Nested_native_constructor_failure_keeps_specific_COM_stage_and_original_HResult()
    {
        var platform = new Platform();
        platform.Desktop.OpenFailure = new CodexActivationStageException("Desktop.FindWindowSW", platform.Fault);
        var result = await new WindowsCodexWindowActivator(platform).RequestAsync();
        result.Status.Should().Be("Unknown"); result.Stage.Should().Be("Desktop.FindWindowSW"); result.HResult.Should().Be(FailureCode);
        result.ExceptionType.Should().Be(typeof(COMException).FullName); result.DispatchAttempted.Should().BeFalse();
        platform.Desktop.Automation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Dynamic_binding_failure_is_retained_as_unknown_dispatch_boundary_without_fallback_or_retry()
    {
        var platform = new Platform(); var failure = new RuntimeBinderException("FAKE_SECRET_BINDER_TEXT");
        platform.Desktop.Automation.Failure = failure;
        var result = await new WindowsCodexWindowActivator(platform).RequestAsync();
        result.Status.Should().Be("Unknown"); result.Stage.Should().Be("Desktop.ShellExecute"); result.HResult.Should().Be(failure.HResult);
        result.ExceptionType.Should().Be(typeof(RuntimeBinderException).FullName); result.DispatchAttempted.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().NotContain("FAKE_SECRET"); platform.Desktop.Automation.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Explicit_native_result_stage_and_failure_are_not_lost_at_outer_broker_boundary()
    {
        var platform = new Platform();
        platform.Desktop.Automation.Result = new("Unknown", FailureCode, 100, "Desktop.ShellExecute.Invoke", "System.Runtime.InteropServices.COMException", true);
        var result = await new WindowsCodexWindowActivator(platform).RequestAsync();
        result.Stage.Should().Be("Desktop.ShellExecute.Invoke"); result.HResult.Should().Be(FailureCode);
        result.ExceptionType.Should().Be(typeof(COMException).FullName); result.DispatchAttempted.Should().BeTrue(); result.ProcessId.Should().BeNull();
        platform.Desktop.Automation.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Coordinator_retains_completed_unknown_result_and_does_not_replay_it()
    {
        var time = new ManualTimeProvider(); var expected = new CodexActivationResult("Unknown", FailureCode,
            Stage: "Desktop.FindWindowSW", ExceptionType: typeof(COMException).FullName);
        var fake = new CodexLaunchTests.FakeActivator { Next = () => Task.FromResult(expected) };
        using var coordinator = new CodexLaunchCoordinator(fake, time);
        var intent = new CodexLaunchIntent("fake-diagnostic", 2, 123, 0, 2, 1, time.GetUtcNow(), 10, 0x70);
        coordinator.Submit(intent, () => true).Should().BeTrue();
        await coordinator.Completion; await coordinator.SchedulerCompletion;
        coordinator.LastResult.Should().Be(expected); coordinator.Status.Should().Be("Unknown"); coordinator.Busy.Should().BeFalse();
        coordinator.Invalidate(); coordinator.LastResult.Should().Be(expected);
        time.Advance(TimeSpan.FromSeconds(3));
        coordinator.Submit(intent with { ReceivedAt = time.GetUtcNow() }, () => true).Should().BeFalse(); fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Coordinator_catches_unexpected_activator_throw_with_diagnostic_and_no_duplicate_call()
    {
        var time = new ManualTimeProvider(); var fake = new CodexLaunchTests.FakeActivator
        {
#pragma warning disable CA2201 // Deliberate COM-runtime failure simulation; no native activation.
            Next = () => throw new COMException("FAKE_SECRET_ACTIVATOR_TEXT", FailureCode)
#pragma warning restore CA2201
        };
        using var coordinator = new CodexLaunchCoordinator(fake, time);
        var intent = new CodexLaunchIntent("fake-throw", 2, 123, 0, 2, 1, time.GetUtcNow(), 10, 0x70);
        coordinator.Submit(intent, () => true).Should().BeTrue();
        await coordinator.Completion; await coordinator.SchedulerCompletion;
        coordinator.LastResult.Should().NotBeNull();
        var result = coordinator.LastResult!.Value;
        result.Stage.Should().Be("Coordinator.ActivatorCall"); result.HResult.Should().Be(FailureCode);
        result.ExceptionType.Should().Be(typeof(COMException).FullName); result.DispatchAttempted.Should().BeTrue(); fake.Calls.Should().Be(1);
        JsonSerializer.Serialize(result).Should().NotContain("FAKE_SECRET");
    }
}

using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class KeyboardRecoveryDiagnosticsTests
{
    [Fact]
    public void Recovery_failure_keeps_stage_and_inner_cause_even_when_transport_dump_is_large()
    {
        var cause = new InvalidDataException("selector not ready");
        var outer = new IOException(new string('x', 5000) + "dump-tail", cause);
        outer.Data["KeyboardStartupStage"] = "snapshot";
        var detail = KeyboardDisplayService.RecoveryFailureDetail(outer);
        detail.Should().Contain("stage=snapshot").And.Contain("InvalidDataException: selector not ready").And.NotContain("dump-tail");
        detail.Length.Should().BeLessThan(800);
    }

    [Fact]
    public void Recovery_failure_retains_primary_and_join_failure_from_aggregate()
    {
        var detail = KeyboardDisplayService.RecoveryFailureDetail(new AggregateException("retirement failed",
            new IOException("original route absent"), new TimeoutException("input join failed")));
        detail.Should().Contain("original route absent").And.Contain("input join failed");
    }
}

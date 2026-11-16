using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Workbench;

internal readonly record struct CaseHealthReport(
    bool HasRfMismatch,
    bool HasRfRetryExhausted,
    bool HasScreenError,
    bool HasFanLcdScreenError,
    bool HasKeyboardError,
    string ShortSummary,
    string DetailedNotice)
{
    public bool HasRfIssue => HasRfMismatch || HasRfRetryExhausted;
    public bool HasAnyIssue => HasRfIssue || HasScreenError || HasKeyboardError;
}

/// <summary>
/// Shared, strictly factual health analysis for tray, top-bar, topology, and device list.
/// Never claims proven physical LED on/off state from RF or USB transfer flags.
/// </summary>
internal static class CaseHealthSummary
{
    public static CaseHealthReport Analyze(
        IEnumerable<DeviceStatusDto>? devices,
        IReadOnlyList<ControllerDto>? controllers,
        IEnumerable<ScreenDeviceDto>? screens,
        KeyboardDisplayStatusDto? keyboard)
    {
        var hasMismatch = false;
        var hasRetryExhausted = false;
        var hasKeyboardError = false;
        string? keyboardErrorDetail = null;

        if (devices is not null)
        {
            foreach (var d in devices)
            {
                if (DisplayNames.IsDiscoveryPortId(d.Id)) continue;

                var rfState = d.LastUpload?.ReceiverDelivery?.State;
                if (rfState == "ReportedMismatch") hasMismatch = true;
                else if (rfState == "RetryExhausted") hasRetryExhausted = true;

                if (CaseDeviceMetadata.IsKeyboardDevice(d, controllers) && !string.IsNullOrWhiteSpace(d.LastError))
                {
                    hasKeyboardError = true;
                    keyboardErrorDetail ??= d.LastError;
                }
            }
        }

        if (keyboard is not null
            && (string.Equals(keyboard.State, "Error", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(keyboard.LastError)))
        {
            hasKeyboardError = true;
            keyboardErrorDetail ??= keyboard.LastError ?? keyboard.Detail;
        }

        var errorScreens = screens?
            .Where(s => s.LastError is not null || string.Equals(s.State, "Error", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        var hasScreenError = errorScreens.Length > 0;
        var hasFanLcdError = errorScreens.Any(s => string.Equals(s.Family, "TlLcd", StringComparison.OrdinalIgnoreCase));

        List<string> shortParts = [];
        List<string> detailParts = [];

        if (hasMismatch || hasRetryExhausted)
        {
            var rfText = (hasMismatch, hasRetryExhausted) switch
            {
                (true, true) => "RF 回读代数失配 / 重试耗尽，建议重新同步",
                (false, true) => "RF 回读重试耗尽，建议重新同步",
                _ => "RF 回读代数失配，建议重新同步",
            };
            shortParts.Add(rfText);
            detailParts.Add(rfText);
        }

        if (hasScreenError)
        {
            shortParts.Add("屏幕传输异常");
            var names = string.Join("、", errorScreens.Select(s => new ScreenChoice(s).Name));
            var firstErr = errorScreens.Select(s => s.LastError).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
            detailParts.Add(string.IsNullOrWhiteSpace(firstErr)
                ? $"屏幕传输异常（{names}）"
                : $"屏幕传输异常（{names}）：{firstErr}");
        }

        if (hasKeyboardError)
        {
            shortParts.Add("键盘通道异常");
            detailParts.Add(string.IsNullOrWhiteSpace(keyboardErrorDetail)
                ? "键盘通道异常（请检查连接与通信状态）"
                : $"键盘通道异常：{keyboardErrorDetail}");
        }

        return new(
            hasMismatch,
            hasRetryExhausted,
            hasScreenError,
            hasFanLcdError,
            hasKeyboardError,
            string.Join(" · ", shortParts),
            string.Join("；", detailParts));
    }
}

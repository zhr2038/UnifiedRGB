using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.Host.Services;

/// <summary>
/// Fan-out point for the IRgbService notifications. Every pipe session subscribes through RgbService; a failing subscriber
/// (e.g. a client that just disconnected) never affects the others or the publisher.
/// </summary>
public sealed class RgbNotifier
{
    public event EventHandler<IReadOnlyList<ControllerDto>>? ControllersChanged;

    public event EventHandler<StatusDto>? StatusChanged;

    public event EventHandler<IReadOnlyList<TelemetryDto>>? TelemetryTick;

    public void PublishControllers(IReadOnlyList<ControllerDto> controllers) => Raise(ControllersChanged, controllers);

    public void PublishStatus(StatusDto status) => Raise(StatusChanged, status);

    public void PublishTelemetry(IReadOnlyList<TelemetryDto> telemetry) => Raise(TelemetryTick, telemetry);

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)d)(this, args);
            }
#pragma warning disable CA1031 // one broken subscriber must not stop the others
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}

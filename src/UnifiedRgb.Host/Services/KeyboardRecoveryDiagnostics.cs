namespace UnifiedRgb.Host.Services;

public sealed partial class KeyboardDisplayService
{
    internal static string RecoveryFailureDetail(Exception error)
    {
        var parts = new List<string>();
        var queue = new Queue<Exception>(); queue.Enqueue(error);
        while (queue.TryDequeue(out var current) && parts.Count < 4)
        {
            var message = current.Message;
            if (message.Length > 320) message = message[..320];
            var stage = current.Data["KeyboardStartupStage"] as string;
            parts.Add((stage is null ? string.Empty : "stage=" + stage + "; ") + current.GetType().Name + ": " + message);
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) queue.Enqueue(inner);
            else if (current.InnerException is { } inner) queue.Enqueue(inner);
        }
        return string.Join(" -> ", parts);
    }
}

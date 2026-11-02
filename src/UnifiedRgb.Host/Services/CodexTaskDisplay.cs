using System.Globalization;
using System.Text;

namespace UnifiedRgb.Host.Services;

internal sealed record CodexRunningTask(string Id, string Title, DateTimeOffset? StartedAt);
internal sealed record CodexTaskSnapshot(string State, IReadOnlyList<CodexRunningTask> Tasks, DateTimeOffset ObservedAt);

internal enum CodexTaskViewKind { Quota, Task, Empty, Unavailable }
internal sealed record CodexTaskView(CodexTaskViewKind Kind, string Title, string Duration, int Index, int Total, TimeSpan SelectedFor);

/// <summary>Pure cached navigation. No task execution, session resume, device I/O or host actions.</summary>
internal sealed class CodexTaskDisplay(TimeProvider? time = null)
{
    internal const int MaximumTasks = 128;
    internal const int MaximumTitleRunes = 256;
    internal static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CodexRunningTask[] _tasks = [];
    private string _state = "Unavailable";
    private DateTimeOffset _observedAt;
    private bool _taskView;
    private string? _selected;
    private DateTimeOffset _selectedAt;

    public bool IsTaskView { get { lock (_gate) return _taskView; } }

    public void PublishSnapshot(CodexTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var valid = snapshot.State == "Ready" && snapshot.Tasks is { Count: <= MaximumTasks };
        var tasks = valid ? CopyTasks(snapshot.Tasks) : [];
        if (valid && tasks is null) valid = false;
        lock (_gate)
        {
            var previous = _tasks.FirstOrDefault(task => task.Id == _selected);
            _state = valid ? "Ready" : "Unavailable";
            _observedAt = snapshot.ObservedAt;
            // An unavailable read is not an empty list. Retain selection identity, but never render old tasks as current.
            if (!valid) return;
            _tasks = tasks!;
            if (!_taskView) return;
            var selected = _tasks.FirstOrDefault(task => task.Id == _selected) ?? _tasks.FirstOrDefault();
            _selected = selected?.Id;
            if (previous != selected) _selectedAt = _time.GetUtcNow();
        }
    }

    /// <summary>Positive is next/down, negative previous/up. Quota is always a member of the cycle.</summary>
    public void Browse(int direction)
    {
        if (direction == 0) return;
        lock (_gate)
        {
            var count = Fresh(_time.GetUtcNow()) ? _tasks.Length : 0;
            if (count == 0)
            {
                _taskView = !_taskView;
                _selectedAt = _time.GetUtcNow();
                return;
            }
            var current = _taskView ? Array.FindIndex(_tasks, task => task.Id == _selected) + 1 : 0;
            if (current == 0 && _taskView) current = 1;
            var next = (current + (direction > 0 ? 1 : -1) + count + 1) % (count + 1);
            _taskView = next != 0;
            _selected = _taskView ? _tasks[next - 1].Id : null;
            _selectedAt = _time.GetUtcNow();
        }
    }

    public void ReturnToQuota()
    {
        lock (_gate) { _taskView = false; _selected = null; _selectedAt = _time.GetUtcNow(); }
    }

    public byte[]? RenderTaskFrame(DateTimeOffset now)
    {
        var view = View(now);
        return view.Kind == CodexTaskViewKind.Quota ? null : KeyboardCodexTaskArtwork.Render(view);
    }

    internal CodexTaskView View(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_taskView) return new(CodexTaskViewKind.Quota, "", "", 0, 0, TimeSpan.Zero);
            var since = now >= _selectedAt ? now - _selectedAt : TimeSpan.Zero;
            if (!Fresh(now)) return new(CodexTaskViewKind.Unavailable, "任务状态暂不可用", "", 0, 0, since);
            if (_tasks.Length == 0) return new(CodexTaskViewKind.Empty, "没有任务", "", 0, 0, since);
            var index = Array.FindIndex(_tasks, task => task.Id == _selected);
            if (index < 0) index = 0;
            var selected = _tasks[index];
            return new(CodexTaskViewKind.Task, selected.Title, Elapsed(selected.StartedAt, now), index + 1, _tasks.Length, since);
        }
    }

    private bool Fresh(DateTimeOffset now) => _state == "Ready" && now - _observedAt <= MaximumSnapshotAge
        && _observedAt - now <= TimeSpan.FromSeconds(5);

    internal static string Elapsed(DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null || startedAt.Value == default || startedAt > now + TimeSpan.FromSeconds(5)) return "时长未知";
        var elapsed = now >= startedAt ? now - startedAt.Value : TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    private static CodexRunningTask[]? CopyTasks(IReadOnlyList<CodexRunningTask> source)
    {
        var tasks = new CodexRunningTask[source.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < source.Count; index++)
        {
            var task = source[index];
            if (task is null || string.IsNullOrWhiteSpace(task.Id) || task.Id.Length > 256 || !ids.Add(task.Id)) return null;
            tasks[index] = task with { Title = SanitizeTitle(task.Title) };
        }
        return tasks;
    }

    internal static string SanitizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未命名任务";
        var output = new StringBuilder();
        var count = 0;
        var space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // Preserve ZWJ and variation selectors for emoji shaping, but remove directional/control formatting.
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || category == UnicodeCategory.Format && rune.Value != 0x200D || Rune.IsWhiteSpace(rune))
            { space = output.Length != 0; continue; }
            if (space)
            {
                if (count + 1 >= MaximumTitleRunes) break;
                output.Append(' '); count++; space = false;
            }
            if (count == MaximumTitleRunes) break;
            count++;
            output.Append(rune.ToString());
        }
        return output.Length == 0 ? "未命名任务" : output.ToString();
    }
}

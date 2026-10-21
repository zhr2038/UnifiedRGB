namespace UnifiedRgb.Setup;

/// <summary>The appsettings file as inspected: its exact bytes' hash is the optimistic-concurrency token for saving.</summary>
/// <param name="Sha256">Upper-case SHA-256 of the exact bytes, or <see cref="AbsentSha256"/> when the file does not exist.</param>
/// <param name="Error">Why the file is refused (oversized, malformed, link...); a refused file is never overwritten.</param>
public sealed record SetupConfigSnapshot(string Path, bool Exists, string Sha256, byte[]? Content, string? Error)
{
    public const string AbsentSha256 = "ABSENT";
}

/// <summary>File operations used by <see cref="SetupConfigStore"/>; replaceable to test failure and rollback paths.</summary>
public interface ISetupFileSystem
{
    /// <summary>The file's bytes, or null when absent. Throws <see cref="SetupConfigException"/> beyond <paramref name="maxBytes"/>.</summary>
    byte[]? ReadBounded(string path, int maxBytes);

    bool IsReparsePoint(string path);

    /// <summary>Creates a NEW file (never overwrites) and flushes it to disk.</summary>
    void WriteNew(string path, byte[] content);

    /// <summary>Atomic swap of <paramref name="source"/> into <paramref name="destination"/>, keeping the old file as <paramref name="backup"/>.</summary>
    void Replace(string source, string destination, string backup);

    /// <summary>Moves <paramref name="source"/> to a destination that must not exist.</summary>
    void MoveNew(string source, string destination);

    void Delete(string path);
}

public sealed class PhysicalSetupFileSystem : ISetupFileSystem
{
    public byte[]? ReadBounded(string path, int maxBytes)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes)
        {
            throw new SetupConfigException($"'{path}' exceeds {maxBytes} bytes.");
        }

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public bool IsReparsePoint(string path) => File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public void WriteNew(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup, ignoreMetadataErrors: false);

    public void MoveNew(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// Reads and atomically publishes ONE appsettings file. Only <see cref="SavePlanAsync"/> writes, and only when the file still has
/// the inspected hash. The previous file is kept as a timestamped backup next to it (never deleted); any failure after the swap
/// restores it. Scenes, profiles, device settings, mounting, native maps and takeover files are never touched.
/// </summary>
public sealed class SetupConfigStore
{
    private readonly ISetupFileSystem _files;
    private readonly TimeProvider _clock;

    public SetupConfigStore(string appSettingsPath, ISetupFileSystem? files = null, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(appSettingsPath) || !System.IO.Path.IsPathFullyQualified(appSettingsPath))
        {
            throw new ArgumentException("The configuration path must be absolute.", nameof(appSettingsPath));
        }

        Path = System.IO.Path.GetFullPath(appSettingsPath);
        _files = files ?? new PhysicalSetupFileSystem();
        _clock = clock ?? TimeProvider.System;
    }

    public string Path { get; }

    public Task<SetupConfigSnapshot> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    public SetupConfigSnapshot Read()
    {
        byte[]? content;
        try
        {
            if (_files.IsReparsePoint(Path))
            {
                return new(Path, true, string.Empty, null, "The configuration file is a link/reparse point; refusing it.");
            }

            content = _files.ReadBounded(Path, SetupConfigDocument.MaxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SetupConfigException)
        {
            return new(Path, true, string.Empty, null, ex.Message);
        }

        if (content is null)
        {
            return new(Path, false, SetupConfigSnapshot.AbsentSha256, null, null);
        }

        var sha = SetupConfigDocument.Sha256(content);
        try
        {
            _ = SetupConfigDocument.Parse(content);
            return new(Path, true, sha, content, null);
        }
        catch (SetupConfigException ex)
        {
            return new(Path, true, sha, content, ex.Message);
        }
    }

    public Task<SetupSaveResult> SavePlanAsync(SetupPlan plan, string expectedSha256, CancellationToken ct) =>
        Task.Run(() => SavePlan(plan, expectedSha256, ct), ct);

    public SetupSaveResult SavePlan(SetupPlan plan, string expectedSha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (!plan.CanSave)
        {
            return new(SetupSaveStatus.Refused, null, null, "The plan has errors and cannot be saved.");
        }

        if (!string.Equals(plan.BaseSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(SetupSaveStatus.Refused, null, null, "The plan was built from a different configuration snapshot.");
        }

        var current = Read();
        if (!string.Equals(current.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase) || current.Exists != plan.BaseExists)
        {
            return new(SetupSaveStatus.Stale, null, null, "The configuration changed since inspection; inspect again before saving.");
        }

        if (current.Error is not null)
        {
            return new(SetupSaveStatus.Refused, null, null, current.Error);
        }

        if (!plan.HasChanges)
        {
            return new(SetupSaveStatus.NoChanges, null, current.Sha256, "Nothing to change.");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(plan.ProposedJson);
        if (!string.Equals(SetupConfigDocument.Sha256(bytes), plan.ProposedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(SetupSaveStatus.Refused, null, null, "The plan content does not match its hash.");
        }

        try
        {
            _ = SetupConfigDocument.Parse(bytes);
        }
        catch (SetupConfigException ex)
        {
            return new(SetupSaveStatus.Refused, null, null, $"The proposed configuration is invalid: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var name = System.IO.Path.GetFileName(Path);
        var stamp = $"{_clock.GetUtcNow():yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}"[..30];
        var temp = System.IO.Path.Combine(directory, $".{name}.{stamp}.tmp");
        var backup = plan.BaseExists ? System.IO.Path.Combine(directory, $"{name}.setup-backup-{stamp}") : null;

        try
        {
            _files.WriteNew(temp, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new(SetupSaveStatus.Failed, null, null, $"Could not stage the new configuration; nothing changed: {ex.Message}");
        }

        try
        {
            if (backup is null)
            {
                _files.MoveNew(temp, Path);
            }
            else
            {
                _files.Replace(temp, Path, backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return backup is null && ex is IOException
                ? new(SetupSaveStatus.Stale, null, null, "A configuration file appeared since inspection; nothing was overwritten.")
                : new(SetupSaveStatus.Failed, null, null, $"Could not publish the new configuration; nothing changed: {ex.Message}");
        }

        string? failure;
        try
        {
            if (backup is not null && _files.ReadBounded(backup, SetupConfigDocument.MaxBytes) is { } replaced
                && !string.Equals(SetupConfigDocument.Sha256(replaced), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Rollback(backup, SetupSaveStatus.Stale, "The configuration changed during publication");
            }

            var written = _files.ReadBounded(Path, SetupConfigDocument.MaxBytes);
            failure = written is not null && SetupConfigDocument.Sha256(written) == plan.ProposedSha256 ? null : "the written file does not read back as planned";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SetupConfigException)
        {
            failure = ex.Message;
        }

        if (failure is not null)
        {
            return backup is null
                ? RemoveNew(failure)
                : Rollback(backup, SetupSaveStatus.RolledBack, $"Publication check failed ({failure})");
        }

        return new(SetupSaveStatus.Saved, backup, plan.ProposedSha256, backup is null ? "Configuration created." : "Configuration saved; previous file kept as backup.");
    }

    private SetupSaveResult Rollback(string backup, SetupSaveStatus status, string reason)
    {
        var restore = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.restore");
        try
        {
            var original = _files.ReadBounded(backup, SetupConfigDocument.MaxBytes)
                ?? throw new IOException("The backup disappeared.");
            _files.WriteNew(restore, original);
            _files.Replace(restore, Path, backup + ".rejected");
            return new(status, backup, null, $"{reason}; the previous configuration was restored from the backup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SetupConfigException)
        {
            TryDelete(restore);
            return new(SetupSaveStatus.Failed, backup, null, $"{reason}; RESTORE FAILED ({ex.Message}). The previous configuration is preserved at '{backup}'.");
        }
    }

    private SetupSaveResult RemoveNew(string reason)
    {
        try
        {
            _files.Delete(Path);
            return new(SetupSaveStatus.RolledBack, null, null, $"Publication check failed ({reason}); the new file was removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(SetupSaveStatus.Failed, null, null, $"Publication check failed ({reason}); removing the new file also failed: {ex.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            _files.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover staging file is harmless: it is never read
        }
    }
}

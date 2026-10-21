using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Screens.Runtime;

namespace UnifiedRgb.Host.ScenePackages;

/// <summary>
/// Chunked package import. Bytes go only to a Host-owned staging file named by a random 256-bit token under
/// <c>&lt;data&gt;/scene-package-uploads</c>; the client never names a path. The file is opened exclusively with delete-on-close, so
/// it disappears when the session ends (finish, refusal, abort, expiry, disposal) and also when the process dies. Chunks are
/// strictly sequential, the whole stream is hashed as it arrives, and Finish refuses anything but the exact declared length and
/// SHA-256 before the ZIP is read. The archive is then validated and installed from the staging file through exactly the same
/// reader/probe/store path as the legacy import, so no second in-memory copy of the ZIP is made.
/// </summary>
public sealed partial class ScenePackageService
{
    public const string UploadDirectoryName = "scene-package-uploads";
    public const int MaxUploadChunkBytes = 1024 * 1024;
    public const int MaxUploadSessions = 2;
    private const int MaxChunkBase64Length = (MaxUploadChunkBytes + 2) / 3 * 4;

    private readonly object _uploadLock = new();
    private readonly Dictionary<string, UploadSession> _uploads = new(StringComparer.Ordinal);
    private Timer? _uploadTimer;
    private bool _uploadsSwept;

    /// <summary>A session without a chunk for this long expires (sliding).</summary>
    internal TimeSpan UploadIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A session expires this long after it was opened, however active.</summary>
    internal TimeSpan UploadMaxLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound of bytes staged by all open sessions together (each also at most the 64 MiB ZIP limit).</summary>
    internal long MaxUploadStagingBytes { get; set; } = MaxUploadSessions * ScenePackageFormat.MaxZipBytes;

    /// <summary>Clock of the session expiry (tests).</summary>
    internal Func<DateTimeOffset> UploadClock { get; set; } = () => DateTimeOffset.UtcNow;
    internal Func<long> UploadMonotonicMilliseconds { get; set; } = () => Environment.TickCount64;

    /// <summary>Open sessions (tests).</summary>
    internal int OpenUploads { get { lock (_uploadLock) return _uploads.Count; } }

    internal string UploadRoot => Path.Combine(Store.DataRoot, UploadDirectoryName);

    public Task<ScenePackageUploadSessionDto> BeginScenePackageUploadAsync(BeginScenePackageUploadDto upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var name = CheckedUploadName(upload.Name, nameof(upload));
        if (upload.Length <= 0 || upload.Length > ScenePackageFormat.MaxZipBytes)
        {
            throw new ArgumentException($"A package upload is 1..{ScenePackageFormat.MaxZipBytes} bytes.", nameof(upload));
        }

        if (!ScenePackageFormat.IsSha256Hex(upload.Sha256))
        {
            throw new ArgumentException("An upload declares the lowercase SHA-256 of the whole ZIP.", nameof(upload));
        }

        ct.ThrowIfCancellationRequested();
        ExpireUploads();
        lock (_uploadLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_uploads.Count >= MaxUploadSessions)
            {
                throw new InvalidOperationException($"{MaxUploadSessions} package uploads are already open; finish or abort one first.");
            }

            var staged = _uploads.Values.Sum(s => s.Length);
            if (staged + upload.Length > MaxUploadStagingBytes)
            {
                throw new InvalidOperationException($"Package uploads would stage {staged + upload.Length} bytes (at most {MaxUploadStagingBytes}).");
            }

            var root = UploadRoot;
            OwnedLibraryPaths.CheckAncestors(root);
            Directory.CreateDirectory(root);
            OwnedLibraryPaths.CheckAncestors(root); // refuses a staging directory reached through a link
            if (!_uploadsSwept)
            {
                SweepOrphanUploads(root);
                _uploadsSwept = true;
            }

            CheckFreeSpace(root, upload.Length);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var file = new FileStream(Path.Combine(root, token + ".part"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.DeleteOnClose);
            var now = UploadClock();
            var session = new UploadSession(token, name, upload.Length, upload.Sha256, file, now, UploadMonotonicMilliseconds());
            _uploads.Add(token, session);
            _uploadTimer ??= new Timer(static s => ((ScenePackageService)s!).ExpireFromTimer(), this, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            LogUploadOpened(token[..8], upload.Length);
            return Task.FromResult(new ScenePackageUploadSessionDto(token, MaxUploadChunkBytes, ExpiresAt(session)));
        }
    }

    public Task<ScenePackageUploadProgressDto> UploadScenePackageChunkAsync(ScenePackageUploadChunkDto chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ExpireUploads();
        var session = Find(chunk.Token);
        lock (session.Sync)
        {
            if (session.Ended)
            {
                throw UnknownUpload();
            }

            var buffer = ArrayPool<byte>.Shared.Rent(MaxUploadChunkBytes);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (chunk.Offset != session.Received)
                {
                    throw new ArgumentException($"The chunk offset is {chunk.Offset}, but {session.Received} bytes were received; the upload was ended.", nameof(chunk));
                }

                if (string.IsNullOrEmpty(chunk.Base64) || chunk.Base64.Length > MaxChunkBase64Length
                    || !Convert.TryFromBase64String(chunk.Base64, buffer.AsSpan(0, MaxUploadChunkBytes), out var written) || written == 0)
                {
                    throw new ArgumentException($"A chunk is 1..{MaxUploadChunkBytes} bytes of valid Base64; the upload was ended.", nameof(chunk));
                }

                if (session.Received + written > session.Length)
                {
                    throw new ArgumentException($"The upload exceeds its declared {session.Length} bytes; the upload was ended.", nameof(chunk));
                }

#pragma warning disable VSTHRD103 // One bounded (<=1 MiB) local write inside the session monitor, which orders it against expiry/abort/finish.
                session.File.Write(buffer, 0, written);
#pragma warning restore VSTHRD103
                session.Hash.AppendData(buffer, 0, written);
                session.Received += written;
                session.LastUsed = UploadClock();
                session.Touch(UploadMonotonicMilliseconds());
                return Task.FromResult(new ScenePackageUploadProgressDto(session.Received));
            }
            catch (Exception ex)
            {
                End(session, ex.Message); // a refused, failed or cancelled chunk leaves no resumable state
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public async Task<ScenePackageDetailsDto> FinishScenePackageUploadAsync(ScenePackageUploadTokenDto upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ExpireUploads();
        var session = Take(upload.Token); // retained in the quota until finalization ends; no later chunk can follow
        try
        {
            lock (session.Sync)
            {
                if (session.Ended)
                {
                    throw UnknownUpload();
                }

                session.Ended = true; // waits for an in-flight chunk, then refuses later ones
            }

            if (session.Received != session.Length)
            {
                throw new ArgumentException($"The upload received {session.Received} of {session.Length} declared bytes; nothing was installed.", nameof(upload));
            }

            var hash = Convert.ToHexString(session.Hash.GetHashAndReset()).ToLowerInvariant();
            if (hash != session.Sha256)
            {
                throw new ArgumentException("The uploaded bytes do not match the declared SHA-256; nothing was installed.", nameof(upload));
            }

            await session.File.FlushAsync(ct).ConfigureAwait(false);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var view = new ReadOnlyView(session.File);
                return await ImportLockedAsync(view, hash, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            LogUploadEnded(session.Token[..8], ex.Message);
            throw;
        }
        finally
        {
            End(session, "finalization ended"); // quota is released only after the staging file is closed
        }
    }

    public Task<bool> AbortScenePackageUploadAsync(ScenePackageUploadTokenDto upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        UploadSession? session;
        lock (_uploadLock)
        {
            if (upload.Token is null || !_uploads.TryGetValue(upload.Token, out session) || session.Finishing)
            {
                return Task.FromResult(false);
            }
            session.Finishing = true; // reserve closing bytes until delete-on-close has actually run
        }

        End(session, "aborted by the client");
        return Task.FromResult(true);
    }

    /// <summary>Ends every session past its expiry. Runs on each upload call and from a 30 s timer while sessions exist.</summary>
    internal void ExpireUploads()
    {
        var expired = new List<UploadSession>();
        lock (_uploadLock)
        {
            var now = UploadMonotonicMilliseconds();
            foreach (var s in _uploads.Values)
            {
                if (!s.Finishing && (now - s.LastUsedTick >= UploadIdleTimeout.TotalMilliseconds || now - s.OpenedTick >= UploadMaxLifetime.TotalMilliseconds)) expired.Add(s);
            }

            foreach (var s in expired) s.Finishing = true;
            if (_uploads.Count == 0 && _uploadTimer is not null)
            {
                _uploadTimer.Dispose();
                _uploadTimer = null;
            }
        }

        foreach (var s in expired) End(s, "expired");
    }

    private void ExpireFromTimer()
    {
        try
        {
            ExpireUploads();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            LogUploadEnded("timer", "expiry sweep failed: " + ex.Message); // retried on the next tick or upload call
        }
    }

    private void DisposeUploads()
    {
        UploadSession[] open;
        lock (_uploadLock)
        {
            open = _uploads.Values.ToArray();
            _uploads.Clear();
            _uploadTimer?.Dispose();
            _uploadTimer = null;
        }

        foreach (var s in open) End(s, "service disposed");
    }

    private async Task<ScenePackageDetailsDto> ImportLockedAsync(Stream zip, string? expectedZipSha256, CancellationToken ct)
    {
        var content = await Task.Run(() => ScenePackageReader.Read(zip), ct).ConfigureAwait(false);
        var media = new List<(ScenePackageMediaRecord, byte[])>();
        foreach (var s in content.Manifest.Screens)
        {
            media.Add(await ProbeAsync(s, content.Files[s.File], ct).ConfigureAwait(false));
        }

        var (installed, created) = await Task.Run(() => Store.Install(content, zip, expectedZipSha256, media), ct).ConfigureAwait(false);
        _source.Restore(installed.ContentId);
        await Task.Run(() => _timeline.Retry(installed.ContentId), ct).ConfigureAwait(false); // cached resolution failures only
        LogImported(installed.ContentId, installed.Manifest.Id, installed.Manifest.Revision, created);
        return DetailsOf(installed);
    }

    private static string CheckedUploadName(string? raw, string parameter)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxUploadNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException($"An upload name must be 1..{MaxUploadNameLength} printable characters.", parameter);
        }

        return name;
    }

    private UploadSession Find(string? token)
    {
        lock (_uploadLock)
        {
            return token is not null && _uploads.TryGetValue(token, out var s) && !s.Finishing ? s : throw UnknownUpload();
        }
    }

    private UploadSession Take(string? token)
    {
        lock (_uploadLock)
        {
            if (token is null || !_uploads.TryGetValue(token, out var s) || s.Finishing) throw UnknownUpload();
            s.Finishing = true;
            return s;
        }
    }

    private void End(UploadSession session, string reason)
    {
        lock (session.Sync) { session.Ended = true; session.Dispose(); }
        lock (_uploadLock)
        {
            _uploads.Remove(session.Token);
            if (_uploads.Count == 0) { _uploadTimer?.Dispose(); _uploadTimer = null; }
        }
        LogUploadEnded(session.Token[..8], reason);
    }

    private DateTimeOffset ExpiresAt(UploadSession s)
    {
        var idle = s.LastUsed + UploadIdleTimeout;
        var max = s.Opened + UploadMaxLifetime;
        return idle < max ? idle : max;
    }

    private static ArgumentException UnknownUpload() => new("No such package upload (unknown token, already finished, aborted or expired).");

    private static void CheckFreeSpace(string root, long length)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // not a drive letter root (for example UNC); the write itself still fails cleanly when the disk is full
        }

        if (free < length + (64L * 1024 * 1024))
        {
            throw new InvalidOperationException($"Not enough free disk space to stage a {length}-byte package upload.");
        }
    }

    /// <summary>Once per service: deletes staging files left by a process that ended without closing them (only own names, never open ones).</summary>
    private void SweepOrphanUploads(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.part"))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 69 || !ScenePackageFormat.IsSha256Hex(name[..64]) || _uploads.ContainsKey(name[..64]))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                {
                    File.Delete(path); // a session of another live Host holds its file exclusively, so this fails and it is kept
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogUploadEnded(name[..8], "orphan staging file kept: " + ex.Message);
            }
        }
    }

    [LoggerMessage(EventId = 4116, Level = LogLevel.Information, Message = "Scene package upload {Token}… opened for {Length} bytes")]
    private partial void LogUploadOpened(string token, long length);

    [LoggerMessage(EventId = 4117, Level = LogLevel.Information, Message = "Scene package upload {Token}… ended: {Reason}")]
    private partial void LogUploadEnded(string token, string reason);

    private sealed class UploadSession(string token, string name, long length, string sha256, FileStream file, DateTimeOffset opened, long openedTick) : IDisposable
    {
        private int _disposed;
        private long _lastUsedTick = openedTick;
        public long OpenedTick { get; } = openedTick;
        public long LastUsedTick => Interlocked.Read(ref _lastUsedTick);
        public void Touch(long now) => Interlocked.Exchange(ref _lastUsedTick, now);
        public bool Finishing { get; set; } // guarded by _uploadLock; finalizers remain reserved against count/disk limits
        public string Token { get; } = token;
        public string Name { get; } = name;
        public long Length { get; } = length;
        public string Sha256 { get; } = sha256;
        public FileStream File { get; } = file;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public DateTimeOffset Opened { get; } = opened;
        public DateTimeOffset LastUsed { get; set; } = opened;
        public long Received { get; set; }
        public bool Ended { get; set; }
        public object Sync { get; } = new();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            File.Dispose();
            Hash.Dispose();
        }
    }

    /// <summary>A read-only, non-owning view of the staging file for the reader and store (they never write; this makes it explicit).</summary>
    private sealed class ReadOnlyView(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

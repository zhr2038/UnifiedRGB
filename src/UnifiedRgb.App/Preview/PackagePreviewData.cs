using System.IO;
using System.Security.Cryptography;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.Preview;

public sealed record PackagePreviewTrack(ScenePackagePreviewTrackDto Info, byte[] Rgb);
public sealed record PackagePreviewData(ScenePackagePreviewDto Info, IReadOnlyList<PackagePreviewTrack> Tracks);

/// <summary>Bounded, hash-verified preview downloads. Never reads privileged paths or issues an output RPC.</summary>
public sealed class PackagePreviewCache(Func<IScenePackageService> service) : IDisposable
{
    private readonly SemaphoreSlim _download = new(1, 1);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb", "PreviewCache");
    private readonly object _lifetime = new();
    private int _users;
    private bool _disposed, _gateDisposed;

    public async Task<PackagePreviewData> ReadLightingAsync(ScenePackagePreviewDto info, CancellationToken ct)
    {
        Enter();
        try
        {
        if (info.Lighting.Count > 64 || info.Lighting.Sum(t => t.Bytes) > 32L * 1024 * 1024) throw new InvalidDataException("灯光预览超过内存预算。");
        var tracks = new List<PackagePreviewTrack>();
        foreach (var t in info.Lighting)
        {
            if (t.LedCount is < 1 or > 4096 || t.Frames is < 1 or > 4096 || t.Bytes != (long)t.LedCount * t.Frames * 3) throw new InvalidDataException("灯光预览尺寸无效。");
            tracks.Add(new(t, await ReadBytesAsync(info.ContentId, t.File, t.Sha256, t.Bytes, 32 * 1024 * 1024, ct).ConfigureAwait(false)));
        }
        return new(info, tracks);
        }
        finally { Exit(); }
    }

    public bool TryGetFile(string sha256, out string file)
    {
        lock (_files) return _files.TryGetValue(sha256, out file!);
    }

    public async Task<string> GetMediaAsync(string contentId, ScenePackagePreviewClipDto clip, CancellationToken ct)
    {
        Enter();
        try
        {
        if (TryGetFile(clip.Sha256, out var cached)) return cached;
        await _download.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetFile(clip.Sha256, out cached)) return cached;
            var suffix = Path.GetExtension(clip.File).ToLowerInvariant();
            if (suffix is not (".mp4" or ".png" or ".jpg" or ".jpeg" or ".gif")) throw new InvalidDataException("未知的屏幕预览格式。");
            if (clip.Sha256.Length != 64 || !clip.Sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("预览缺少有效的内容校验值。");
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, clip.Sha256.ToLowerInvariant() + suffix);
            if (File.Exists(path) && new FileInfo(path).Length <= 12 * 1024 * 1024)
            {
                using var input = File.OpenRead(path);
                if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).Equals(clip.Sha256, StringComparison.OrdinalIgnoreCase))
                { lock (_files) _files[clip.Sha256] = path; return path; }
            }
            var bytes = await ReadBytesAsync(contentId, clip.File, clip.Sha256, clip.Bytes, 12 * 1024 * 1024, ct).ConfigureAwait(false);
            PruneCache(bytes.Length, path);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            lock (_files) _files[clip.Sha256] = path;
            return path;
        }
        finally { _download.Release(); }
        }
        finally { Exit(); }
    }

    private void PruneCache(long incoming, string destination)
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles().Where(f => f.Name.Length > 64 && f.Name.AsSpan(0, 64).ToString().All(char.IsAsciiHexDigit) && f.FullName != destination).OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length) + incoming;
        foreach (var file in files)
        {
            if (total <= 256L * 1024 * 1024) break;
            try { var length = file.Length; file.Delete(); total -= length; lock (_files) _files.Remove(file.Name[..64]); }
            catch (IOException) { } // in-use media remains until a later prune
        }
        if (total > 256L * 1024 * 1024) throw new IOException("本地预览缓存已满，请关闭其他预览后重试。");
    }

    private void Enter() { lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); _users++; } }
    private void Exit() { lock (_lifetime) { _users--; if (_disposed && _users == 0 && !_gateDisposed) { _download.Dispose(); _gateDisposed = true; } } }
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return; _disposed = true;
            if (_users == 0) { _download.Dispose(); _gateDisposed = true; }
        }
    }

    private async Task<byte[]> ReadBytesAsync(string contentId, string file, string sha, long expected, int limit, CancellationToken ct)
    {
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) throw new InvalidDataException("预览缺少有效的内容校验值。");
        byte[]? bytes = null; long offset = 0;
        do
        {
            var chunk = await service().ReadScenePackagePreviewChunkAsync(new(contentId, file, offset), ct).ConfigureAwait(false);
            if (chunk.Offset != offset || chunk.TotalBytes is < 1 || chunk.TotalBytes > limit || chunk.Sha256 != sha || expected > 0 && chunk.TotalBytes != expected) throw new InvalidDataException("预览分块与内容清单不一致。");
            bytes ??= new byte[checked((int)chunk.TotalBytes)];
            if (bytes.Length != chunk.TotalBytes) throw new InvalidDataException("预览分块长度发生变化。");
            var part = Convert.FromBase64String(chunk.Base64);
            if (part.Length is < 1 or > 49152 || offset + part.Length > bytes.Length) throw new InvalidDataException("预览分块长度无效。");
            part.CopyTo(bytes, checked((int)offset)); offset += part.Length;
        } while (offset < bytes.Length);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("预览校验失败，未显示未验证的内容。");
        return bytes;
    }
}

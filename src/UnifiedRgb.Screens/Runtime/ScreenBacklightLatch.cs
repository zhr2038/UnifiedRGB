using System.Security.Cryptography;
using System.Text;

namespace UnifiedRgb.Screens.Runtime;

/// <summary>Small local recovery latch, independent of scene/package/media bytes. Loaded once; written only on off/wake transitions.</summary>
internal sealed class ScreenBacklightLatch
{
    private readonly string _path;
    public bool Off { get; private set; }
    public byte RestoreLevel { get; }

    public ScreenBacklightLatch(string directory, string screenId, byte restoreLevel)
    {
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(screenId))).ToLowerInvariant();
        _path = Path.Combine(directory, key + ".state");
        RestoreLevel = restoreLevel;
        if (!File.Exists(_path)) return;
        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length != 4 || bytes[0] != 1 || bytes[1] > 1 || bytes[3] != (byte)(bytes[0] ^ bytes[1] ^ bytes[2]))
            throw new InvalidDataException("Invalid local screen-backlight recovery latch; no automatic wake is allowed.");
        Off = bytes[1] != 0;
        if (Off) RestoreLevel = bytes[2]; // keep the target that was captured before off, even after a configuration change
    }

    public void Save(bool off)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte state = off ? (byte)1 : (byte)0;
        byte[] bytes = [1, state, RestoreLevel, (byte)(1 ^ state ^ RestoreLevel)];
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, _path, overwrite: true);
        Off = off;
    }
}

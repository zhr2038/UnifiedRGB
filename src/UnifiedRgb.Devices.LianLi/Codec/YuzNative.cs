using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace UnifiedRgb.Devices.LianLi.Codec;

/// <summary>
/// L-Connect's tinyuz codec, <c>yuz.dll</c> (ARCHITECTURE-D2 §3), loaded from an explicit path with
/// <see cref="NativeLibrary.Load(string)"/> and bound to its two exports through unmanaged function pointers — no
/// <c>[DllImport]</c>, so nothing is resolved from the search path. The file is only read and mapped: never copied, shipped,
/// hash-pinned or modified. Signatures as <c>Util.CompressYuz</c>/<c>DecYuz</c> P/Invoke them:
/// <c>uint TuzEnc(byte* in, int len, byte* out)</c>, <c>void TuzDec(byte* comp, int compLen, int origLen, byte* out)</c>.
/// </summary>
public sealed unsafe class YuzNative : IDisposable
{
    /// <summary>IMAGE_FILE_MACHINE_AMD64.</summary>
    public const ushort MachineAmd64 = 0x8664;

    private readonly delegate* unmanaged[Cdecl]<byte*, int, byte*, uint> _enc;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, int, byte*, void> _dec;
    private readonly object _gate = new();
    private nint _handle;

    private YuzNative(string path, string sha256, nint handle, nint enc, nint dec)
    {
        Path = path;
        Sha256 = sha256;
        _handle = handle;
        _enc = (delegate* unmanaged[Cdecl]<byte*, int, byte*, uint>)enc;
        _dec = (delegate* unmanaged[Cdecl]<byte*, int, int, byte*, void>)dec;
    }

    public string Path { get; }

    /// <summary>SHA-256 of the loaded file (logged at open, shown in Diagnostics; never compared against a pinned value).</summary>
    public string Sha256 { get; }

    /// <summary>Relative explicit paths belong to the application directory, never the caller's working directory.</summary>
    public static string? ResolveConfiguredPath(string? path) => string.IsNullOrWhiteSpace(path) ? path
        : System.IO.Path.GetFullPath(path, AppContext.BaseDirectory);

    /// <summary>
    /// Loads <paramref name="path"/>. Throws <see cref="DeviceBlockedException"/> ("yuz.dll missing", wrong architecture or
    /// missing export) — the adapter then shows Blocked.
    /// </summary>
    public static YuzNative Load(string? path)
    {
        path = ResolveConfiguredPath(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new DeviceBlockedException($"yuz.dll missing ({(string.IsNullOrWhiteSpace(path) ? "VendorYuz requires an explicit LianLi:YuzDllPath" : path)})");
        }

        var full = System.IO.Path.GetFullPath(path);
        byte[] image;
        try
        {
            image = File.ReadAllBytes(full);
        }
        catch (IOException ex)
        {
            throw new DeviceBlockedException($"yuz.dll unreadable ({full}): {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DeviceBlockedException($"yuz.dll unreadable ({full}): {ex.Message}", ex);
        }

        var machine = PeMachine(image);
        if (machine != MachineAmd64)
        {
            throw new DeviceBlockedException($"yuz.dll is not an x64 PE (machine 0x{machine:X4}): {full}");
        }

        var sha = Convert.ToHexString(SHA256.HashData(image));
        nint handle;
        try
        {
            handle = NativeLibrary.Load(full);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            throw new DeviceBlockedException($"yuz.dll could not be loaded ({full}): {ex.Message}", ex);
        }

        if (!NativeLibrary.TryGetExport(handle, "TuzEnc", out var enc) || !NativeLibrary.TryGetExport(handle, "TuzDec", out var dec))
        {
            NativeLibrary.Free(handle);
            throw new DeviceBlockedException($"yuz.dll lacks the TuzEnc/TuzDec exports: {full}");
        }

        return new YuzNative(full, sha, handle, enc, dec);
    }

    /// <summary>Machine field of a PE image's COFF header, or 0 when the bytes are not a PE file.</summary>
    public static ushort PeMachine(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return 0;
        }

        var pe = BitConverter.ToInt32(image[0x3C..]);
        if (pe < 0 || pe > image.Length - 6 || image[pe] != (byte)'P' || image[pe + 1] != (byte)'E' || image[pe + 2] != 0 || image[pe + 3] != 0)
        {
            return 0;
        }

        return BitConverter.ToUInt16(image[(pe + 4)..]);
    }

    /// <summary>
    /// <c>TuzEnc</c>. <c>CompressYuz</c> gives the native encoder an output buffer of exactly the input length; this one is
    /// larger (2 × len + 1 KiB) because the export takes no output capacity, and an output longer than the input is
    /// refused rather than trusted (SELF_AUDIT M9 dev. 6).
    /// </summary>
    public byte[] Encode(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            throw new ArgumentException("Nothing to compress.", nameof(input));
        }

        var outBuf = new byte[(2 * input.Length) + 1024];
        uint n;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            fixed (byte* src = input)
            fixed (byte* dst = outBuf)
            {
                n = _enc(src, input.Length, dst);
            }
        }

        if (n == 0 || n > (uint)input.Length)
        {
            throw new InvalidDataException($"TuzEnc returned {n} bytes for {input.Length} input bytes.");
        }

        return outBuf.AsSpan(0, (int)n).ToArray();
    }

    /// <summary><c>TuzDec</c> into a buffer of <paramref name="originalLength"/> bytes (as <c>DecYuz</c>).</summary>
    public byte[] Decode(ReadOnlySpan<byte> compressed, int originalLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(originalLength);
        if (compressed.IsEmpty)
        {
            throw new ArgumentException("Nothing to decompress.", nameof(compressed));
        }

        var outBuf = new byte[originalLength];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            fixed (byte* src = compressed)
            fixed (byte* dst = outBuf)
            {
                _dec(src, compressed.Length, originalLength, dst);
            }
        }

        return outBuf;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != 0)
            {
                NativeLibrary.Free(_handle);
                _handle = 0;
            }
        }
    }
}

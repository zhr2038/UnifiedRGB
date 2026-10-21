using System.Buffers.Binary;
using UnifiedRgb.Setup;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Setup.Tests;

/// <summary>A scriptable OS backend; counts every receiver query so tests can prove when none is sent.</summary>
internal sealed class FakeSetupSystem : ISetupSystem
{
    public List<string> InstanceIds { get; } = [];

    public List<UsbDeviceInfo> Interfaces { get; } = [];

    public List<HidCollectionInfo> Aura { get; } = [];

    public Func<UsbDeviceInfo, byte[]> Query { get; set; } = _ => throw new InvalidOperationException("no receiver reply scripted");

    public Dictionary<string, SetupFileFacts> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Untrusted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Func<string, string>? PawnIoProbe { get; set; } = _ => "API 2.0.0";

    public List<string> InspectedFiles { get; } = [];

    public int Queries { get; private set; }

    public string ProgramFilesDirectory { get; set; } = @"C:\Program Files";

    public IReadOnlyList<string> PresentDeviceInstanceIds() => InstanceIds;

    public IReadOnlyList<UsbDeviceInfo> DeviceInterfaces(Guid interfaceClass) => Interfaces;

    public IReadOnlyList<HidCollectionInfo> HidCollections(ushort vid, ushort pid) => Aura.Where(c => c.Vid == vid && c.Pid == pid).ToArray();

    public byte[] QueryRfReceiverList(UsbDeviceInfo receiver)
    {
        Queries++;
        return Query(receiver);
    }

    public SetupFileFacts InspectFile(string fullPath)
    {
        InspectedFiles.Add(fullPath);
        return Files.TryGetValue(fullPath, out var f) ? f : new(false, 0, null, null);
    }

    public bool IsAdminOnlyWritable(string fullPath, out string reason)
    {
        reason = Untrusted.Contains(fullPath) ? "user-writable" : string.Empty;
        return reason.Length == 0;
    }

    public string ProbePawnIoApi(string libraryPath, string sha256, string apiVersion) =>
        PawnIoProbe is { } probe ? probe(libraryPath) : throw new InvalidOperationException("API refused");

    public const string InstallDir = @"C:\Program Files\UnifiedRGB\Host";

    public static string CodecPath => Path.Combine(InstallDir, SetupEnvironment.DefaultCodecRelativePath);

    public static string ModulePath => Path.Combine(InstallDir, SetupEnvironment.DefaultPawnIoModuleRelativePath);

    public const string PawnIoLibPath = @"C:\Program Files\PawnIO\PawnIOLib.dll";

    public static UsbDeviceInfo Sender(string serial = "1") => Usb(0x0416, 0x8040, serial);

    public static UsbDeviceInfo Receiver(string serial = "2") => Usb(0x0416, 0x8041, serial);

    public static UsbDeviceInfo Usb(ushort vid, ushort pid, string serial) =>
        UsbDeviceInfo.FromPath($@"\\?\usb#vid_{vid:x4}&pid_{pid:x4}#{serial}#{{dee824ef-729b-4a0e-9c14-b7117d33a817}}")!;

    /// <summary>One receiver + one sender, codec and PawnIO resources all pinned and trusted.</summary>
    public static FakeSetupSystem WithLianLi(byte[] reply)
    {
        var s = new FakeSetupSystem();
        s.Interfaces.Add(Sender());
        s.Interfaces.Add(Receiver());
        s.Query = _ => reply;
        s.AddPinnedResources();
        return s;
    }

    public void AddPinnedResources()
    {
        Files[CodecPath] = new(true, 1000, new string('F', 64), "1.0"); // optional legacy DLL is ignored
        Files[ModulePath] = new(true, 1000, Devices.EneDram.EneDramDefaults.ModuleSha256, null);
        Files[PawnIoLibPath] = new(true, 1000, Devices.EneDram.EneDramDefaults.PawnIoLibSha256, "2.2.0");
    }

    public SetupReport Inspect() => new SetupInspector(this, new SetupEnvironment(InstallDir)).Inspect(CancellationToken.None);
}

/// <summary>Builds receiver replies in the documented 4-byte header + 42-byte record layout.</summary>
internal static class Reply
{
    public const ulong Master = 0x0A0B0C0D0E0F;

    public static byte[] Of(params byte[][] records)
    {
        var reply = new byte[4 + records.Length * 42];
        reply[0] = 16;
        reply[1] = (byte)records.Length;
        reply[2] = 0x80;
        reply[3] = 0x32;
        for (var i = 0; i < records.Length; i++)
        {
            records[i].CopyTo(reply, 4 + i * 42);
        }

        return reply;
    }

    public static byte[] Record(ulong mac, byte rx, byte devType = 0, int fans = 3, byte fanType = 28, ulong master = Master, byte channel = 8,
        byte[]? fanTypes = null)
    {
        var r = new byte[42];
        Write48(r, 0, mac);
        Write48(r, 6, master);
        r[12] = channel;
        r[13] = rx;
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(14), 123456);
        r[18] = devType;
        r[19] = (byte)fans;
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(20), 77);
        var types = fanTypes ?? Enumerable.Repeat(devType == 0 ? fanType : (byte)0, 4).ToArray();
        types.CopyTo(r, 24);
        r[41] = 28;
        return r;
    }

    /// <summary>Three TL x3 groups and one H2 block, like a typical case (synthetic MACs, NOT any real machine).</summary>
    public static byte[] TypicalCase() => Of(
        Record(0x111111000001, 2),
        Record(0x111111000002, 3),
        Record(0x222222000003, 6, devType: 10, fans: 0),
        Record(0x111111000004, 5, fanType: 52));

    private static void Write48(byte[] b, int o, ulong v)
    {
        for (var i = 0; i < 6; i++)
        {
            b[o + i] = (byte)(v >> (8 * (5 - i)));
        }
    }
}

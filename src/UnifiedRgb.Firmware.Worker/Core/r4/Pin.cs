using System.Text.Json;

namespace BootReadOnly;

public sealed record Pin
{
    public const int PhysicalSessionMaxAgeMinutes=15;
    public string HidPath{get;init;}="";public string HidInstanceId{get;init;}="";public string UsbAncestor{get;init;}="";
    public string PhysicalLocation{get;init;}="";public string ContainerId{get;init;}="";public string CapsSha256{get;init;}="";
    public string InventoryFile{get;init;}="";public string InventorySha256{get;init;}="";
    public Guid FreshPhysicalSessionId{get;init;}public DateTimeOffset BootObservedAtUtc{get;init;}
    public bool RootObservedFreshPhysicalBootEntry{get;init;}public bool RootConfirmedExclusiveNoPriorBootReports{get;init;}
    public bool RootConfirmedContinuousWatchedPhysicalSession{get;init;}
    public string ExpectedRawVersionHex{get;init;}="02000200";
    public static Pin Load(string path,DateTimeOffset now)
    {
        if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Manifest too large");
        var p=JsonSerializer.Deserialize<Pin>(File.ReadAllBytes(path))??throw new InvalidDataException("Invalid pin manifest");p.Validate(now);
        if(!Path.IsPathFullyQualified(p.InventoryFile)||new FileInfo(p.InventoryFile).Length>4*1024*1024)throw new InvalidDataException("Inventory must be explicit bounded local file");
        var raw=File.ReadAllBytes(p.InventoryFile);if(Wire.Sha(raw)!=p.InventorySha256)throw new InvalidDataException("Root inventory hash mismatch");
        using var document=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=32});var values=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateInventoryTime(document.RootElement,p.BootObservedAtUtc,now);
        void Gather(JsonElement e){if(e.ValueKind==JsonValueKind.String)values.Add(e.GetString()!);else if(e.ValueKind==JsonValueKind.Array)foreach(var c in e.EnumerateArray())Gather(c);else if(e.ValueKind==JsonValueKind.Object)foreach(var c in e.EnumerateObject())Gather(c.Value);}
        Gather(document.RootElement);
        foreach(var value in new[]{p.HidPath,p.HidInstanceId,p.UsbAncestor,p.PhysicalLocation,p.ContainerId,p.CapsSha256})
            if(!values.Contains(value))throw new InvalidDataException("Pin field absent from exact Root inventory");
        return p;
    }
    static void ValidateInventoryTime(JsonElement root,DateTimeOffset bootObserved,DateTimeOffset now)
    {
        if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count(x=>x.NameEquals("at"))!=1
            ||!root.TryGetProperty("at",out var stamp)||stamp.ValueKind!=JsonValueKind.String)
            throw new InvalidDataException("Root inventory requires one string UTC timestamp 'at'");
        string? text=stamp.GetString();
        if(text is null||text.Length is <20 or >40||
            !(text.EndsWith('Z')||text.EndsWith("+00:00",StringComparison.Ordinal))||
            !stamp.TryGetDateTimeOffset(out var captured)||captured.Offset!=TimeSpan.Zero)
            throw new InvalidDataException("Inventory 'at' must be a valid bounded explicit UTC timestamp");
        if(captured<bootObserved||captured>now||now-captured>TimeSpan.FromMinutes(PhysicalSessionMaxAgeMinutes))
            throw new InvalidDataException("Inventory timestamp predates physical boot, is future, or exceeds fifteen minutes");
    }
    public void Validate(DateTimeOffset now)
    {
        if(!RootObservedFreshPhysicalBootEntry||!RootConfirmedExclusiveNoPriorBootReports||!RootConfirmedContinuousWatchedPhysicalSession||FreshPhysicalSessionId==Guid.Empty)
            throw new InvalidDataException("Root fresh physical session attestation required");
        var age=now-BootObservedAtUtc;if(age<TimeSpan.Zero||age>TimeSpan.FromMinutes(PhysicalSessionMaxAgeMinutes))throw new InvalidDataException("Physical boot observation is stale/future");
        if(!HidPath.StartsWith("\\\\?\\hid#",StringComparison.OrdinalIgnoreCase)||!HidPath.Contains("vid_0b05&pid_1c26",StringComparison.OrdinalIgnoreCase)
            ||!HidInstanceId.StartsWith("HID\\VID_0B05&PID_1C26",StringComparison.OrdinalIgnoreCase)
            ||!UsbAncestor.StartsWith("USB\\VID_0B05&PID_1C26\\",StringComparison.OrdinalIgnoreCase)
            ||!PhysicalLocation.Contains("#USB(",StringComparison.Ordinal)||!Guid.TryParse(ContainerId,out _)
            ||CapsSha256.Length!=64||InventorySha256.Length!=64||ExpectedRawVersionHex!="02000200")
            throw new InvalidDataException("Incomplete/foreign physical identity/version pin");
        _=Convert.FromHexString(CapsSha256);_=Convert.FromHexString(InventorySha256);
    }
}

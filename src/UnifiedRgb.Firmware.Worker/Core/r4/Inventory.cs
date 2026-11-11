using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BootReadOnly;

public sealed record Collection(string Path,string InstanceId,string UsbAncestor,string[] LocationPaths,string ContainerId,
    ushort Vid,ushort Pid,ushort UsagePage,ushort Usage,ushort InputLength,ushort OutputLength,
    byte[] InputIds,byte[] OutputIds,byte[] FeatureIds,string CapsSha256,string? Error,
    string DeviceClassGuid="",string[]? HardwareIds=null,int? Access0ErrorCode=null)
{
    public bool Eligible=>Error==null&&Vid==0x0B05&&Pid==0x1C26&&UsagePage==0xFF00&&Usage==1&&InputLength==65&&OutputLength==65
        &&InputIds.SequenceEqual(new byte[]{0})&&OutputIds.SequenceEqual(new byte[]{0})&&!FeatureIds.Any(x=>x!=0);
    public bool PositivelyIdentifiedOsMouse=>Error!=null&&Access0ErrorCode==5&&Vid==0x0B05&&Pid==0x1C26
        &&Guid.TryParse(DeviceClassGuid,out var cls)&&cls==Guid.Parse("4d36e96f-e325-11ce-bfc1-08002be10318")
        &&Path.Contains("&mi_02#",StringComparison.OrdinalIgnoreCase)
        &&InstanceId.StartsWith("HID\\VID_0B05&PID_1C26&MI_02\\",StringComparison.OrdinalIgnoreCase)
        &&UsbAncestor.StartsWith("USB\\VID_0B05&PID_1C26\\",StringComparison.OrdinalIgnoreCase)
        &&LocationPaths.Length>0&&HardwareIds!=null
        &&HardwareIds.Contains("HID_DEVICE_SYSTEM_MOUSE",StringComparer.OrdinalIgnoreCase)
        &&HardwareIds.Contains("HID_DEVICE_UP:0001_U:0002",StringComparer.OrdinalIgnoreCase);
    public string? ExcludedReason=>PositivelyIdentifiedOsMouse?"Access0 denied5; positive Windows Mouse class and system-mouse/UP0001U0002 HardwareIds; same-physical binding required by selection":null;
}
public sealed record PhysicalIdentity(string InstanceId,string UsbAncestor,string[] LocationPaths,string ContainerId);
internal static class MetaNative
{
    [StructLayout(LayoutKind.Sequential)]internal struct InterfaceData{public uint Size;public Guid Class;public uint Flags;public UIntPtr Reserved;}
    [StructLayout(LayoutKind.Sequential)]internal struct DevInfo{public uint Size;public Guid Class;public uint DevInst;public UIntPtr Reserved;}
    [StructLayout(LayoutKind.Sequential)]internal struct Attributes{public uint Size;public ushort Vid,Pid,Version;}
    [StructLayout(LayoutKind.Sequential)]internal struct PropertyKey{public Guid Format;public uint Pid;public PropertyKey(string f,uint p){Format=Guid.Parse(f);Pid=p;}}
    internal static readonly PropertyKey Location=new("A45C254E-DF1C-4EFD-8020-67D146A850E0",37),Container=new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C",2),
        ClassGuid=new("A45C254E-DF1C-4EFD-8020-67D146A850E0",10),HardwareIds=new("A45C254E-DF1C-4EFD-8020-67D146A850E0",3);
    [DllImport("hid.dll")]internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.U1)]internal static extern bool HidD_GetAttributes(SafeFileHandle h,ref Attributes a);
    [DllImport("hid.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.U1)]internal static extern bool HidD_GetPreparsedData(SafeFileHandle h,out IntPtr data);
    [DllImport("hid.dll")][return:MarshalAs(UnmanagedType.U1)]internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]internal static extern int HidP_GetCaps(IntPtr data,[Out]ushort[] caps);
    [DllImport("hid.dll")]internal static extern int HidP_GetButtonCaps(int type,[Out]byte[] records,ref ushort count,IntPtr data);
    [DllImport("hid.dll")]internal static extern int HidP_GetValueCaps(int type,[Out]byte[] records,ref ushort count,IntPtr data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern IntPtr SetupDiGetClassDevsW(ref Guid g,string? e,IntPtr p,uint f);
    [DllImport("setupapi.dll",SetLastError=true)]internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr s,IntPtr d,ref Guid g,uint i,ref InterfaceData data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s,ref InterfaceData i,IntPtr b,uint n,out uint needed,ref DevInfo d);
    [DllImport("setupapi.dll")]internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]internal static extern uint CM_Get_Device_IDW(uint node,StringBuilder buffer,uint size,uint flags);
    [DllImport("cfgmgr32.dll")]internal static extern uint CM_Get_Parent(out uint parent,uint node,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]internal static extern uint CM_Locate_DevNodeW(out uint node,string instanceId,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)]internal static extern uint CM_Get_DevNode_PropertyW(uint node,ref PropertyKey key,out uint type,[Out]byte[] buffer,ref uint length,uint flags);
}

public static class Inventory
{
    public static (ushort Vid,ushort Pid,ushort Page,ushort Usage,ushort InLength,ushort OutLength,byte[] InIds,byte[] OutIds,byte[] FeatureIds,string Sha) Describe(SafeFileHandle h)
    {
        var a=new MetaNative.Attributes{Size=(uint)Marshal.SizeOf<MetaNative.Attributes>()};
        if(!MetaNative.HidD_GetAttributes(h,ref a)||!MetaNative.HidD_GetPreparsedData(h,out var pp))throw new Win32Exception(Marshal.GetLastPInvokeError(),"HID metadata unavailable");
        try
        {
            var caps=new ushort[32];if(MetaNative.HidP_GetCaps(pp,caps)!=0x110000)throw new InvalidDataException("HidP_GetCaps failed");
            var hash=new List<byte>();foreach(var v in caps)hash.AddRange(BitConverter.GetBytes(v));
            byte[] Ids(int type,int first)
            {
                var ids=new SortedSet<byte>();
                for(int k=0;k<2;k++)
                {
                    ushort count=caps[first+k];if(count>64)throw new InvalidDataException("Too many descriptor caps");if(count==0)continue;
                    ushort original=count;var bytes=new byte[count*72];int status=k==0?MetaNative.HidP_GetButtonCaps(type,bytes,ref count,pp):MetaNative.HidP_GetValueCaps(type,bytes,ref count,pp);
                    if(status!=0x110000||count!=original)throw new InvalidDataException("Capability count changed/failed");
                    hash.AddRange(bytes);foreach(byte id in ParseIds(bytes,count))ids.Add(id);
                }
                return ids.ToArray();
            }
            var input=Ids(0,23);var output=Ids(1,26);var features=Ids(2,29);
            return(a.Vid,a.Pid,caps[1],caps[0],caps[2],caps[3],input,output,features,Wire.Sha(hash.ToArray()));
        }
        finally{MetaNative.HidD_FreePreparsedData(pp);}
    }
    public static byte[] ParseIds(byte[] bytes,int count)
    {if(count<0||count>64||bytes.Length!=count*72)throw new InvalidDataException("Descriptor cap bytes/count mismatch");return Enumerable.Range(0,count).Select(i=>bytes[i*72+2]).Distinct().Order().ToArray();}
    static string Instance(uint node)
    {var b=new StringBuilder(1024);uint e=MetaNative.CM_Get_Device_IDW(node,b,1024,0);if(e!=0)throw new IOException($"CM_Get_Device_IDW:{e}");return b.ToString();}
    static byte[] Property(uint node,MetaNative.PropertyKey key,uint expectedType)
    {var b=new byte[8192];uint n=(uint)b.Length;uint e=MetaNative.CM_Get_DevNode_PropertyW(node,ref key,out uint type,b,ref n,0);if(e!=0||type!=expectedType||n>b.Length)throw new IOException($"CM_Get_DevNode_Property:{e}/type{type}");return b[..(int)n];}
    static (string Usb,string[] Locations,string Container) Physical(uint node)
    {
        for(int depth=0;depth<12;depth++)
        {
            string id=Instance(node);
            if(id.StartsWith("USB\\VID_0B05&PID_1C26\\",StringComparison.OrdinalIgnoreCase))
            {var locations=Encoding.Unicode.GetString(Property(node,MetaNative.Location,0x2012)).Split('\0',StringSplitOptions.RemoveEmptyEntries);var container=new Guid(Property(node,MetaNative.Container,13)).ToString("D");return(id,locations,container);}
            if(MetaNative.CM_Get_Parent(out uint parent,node,0)!=0)break;node=parent;
        }
        throw new InvalidDataException("Exact boot USB physical ancestor not found");
    }
    public static IReadOnlyList<Collection> Enumerate()
    {
        if(!OperatingSystem.IsWindows()||IntPtr.Size!=8)throw new PlatformNotSupportedException("Windows x64 only");
        MetaNative.HidD_GetHidGuid(out var guid);var set=MetaNative.SetupDiGetClassDevsW(ref guid,null,IntPtr.Zero,0x12);
        if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastPInvokeError());
        var list=new List<Collection>();
        try
        {
            for(uint i=0;i<1024;i++)
            {
                var data=new MetaNative.InterfaceData{Size=(uint)Marshal.SizeOf<MetaNative.InterfaceData>()};
                if(!MetaNative.SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref data))
                {int e=Marshal.GetLastPInvokeError();if(e==259)return list;throw new Win32Exception(e);}
                var dev=new MetaNative.DevInfo{Size=(uint)Marshal.SizeOf<MetaNative.DevInfo>()};
                _=MetaNative.SetupDiGetDeviceInterfaceDetailW(set,ref data,IntPtr.Zero,0,out uint need,ref dev);
                if(need<8||need>65536)throw new InvalidDataException("Unexpected interface detail size");
                var b=Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(b,8);
                    if(!MetaNative.SetupDiGetDeviceInterfaceDetailW(set,ref data,b,need,out _,ref dev))throw new Win32Exception(Marshal.GetLastPInvokeError());
                    string path=Marshal.PtrToStringUni(b+4)??throw new InvalidDataException();
                    if(!path.Contains("vid_0b05&pid_1c26",StringComparison.OrdinalIgnoreCase))continue;
                    string instance="",usb="",container="",classGuid="";string[] locations=[],hardwareIds=[];int? accessError=null;
                    try
                    {
                        // Physical/PnP evidence must survive an OS-owned collection's access0 refusal.
                        instance=Instance(dev.DevInst);var phy=Physical(dev.DevInst);usb=phy.Usb;locations=phy.Locations;container=phy.Container;
                        classGuid=new Guid(Property(dev.DevInst,MetaNative.ClassGuid,13)).ToString("D");
                        hardwareIds=Encoding.Unicode.GetString(Property(dev.DevInst,MetaNative.HardwareIds,0x2012)).Split('\0',StringSplitOptions.RemoveEmptyEntries);
                        using var h=Native.CreateFileW(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero);
                        if(h.IsInvalid){accessError=Marshal.GetLastPInvokeError();throw new Win32Exception(accessError.Value);}
                        var c=Describe(h);
                        list.Add(new(path,instance,usb,locations,container,c.Vid,c.Pid,c.Page,c.Usage,c.InLength,c.OutLength,c.InIds,c.OutIds,c.FeatureIds,c.Sha,null,classGuid,hardwareIds,null));
                    }
                    catch(Exception ex){list.Add(new(path,instance,usb,locations,container,0x0B05,0x1C26,0,0,0,0,[],[],[],"",ex.Message,classGuid,hardwareIds,accessError));}
                }
                finally{Marshal.FreeHGlobal(b);}
            }
            throw new IOException("Enumeration cap exceeded");
        }
        finally{MetaNative.SetupDiDestroyDeviceInfoList(set);}
    }
    public static Collection Select(IReadOnlyList<Collection> rows,Pin pin)
    {
        var eligible=rows.Where(x=>x.Eligible).ToArray();if(eligible.Length!=1)throw new InvalidDataException("Expected exactly one eligible boot collection");
        var c=eligible[0];
        foreach(var row in rows.Where(x=>x.Error!=null))
            if(!row.PositivelyIdentifiedOsMouse||!row.UsbAncestor.Equals(c.UsbAncestor,StringComparison.OrdinalIgnoreCase)
                ||!row.ContainerId.Equals(c.ContainerId,StringComparison.OrdinalIgnoreCase)
                ||!row.LocationPaths.Contains(pin.PhysicalLocation,StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Uninspectable collection lacks complete same-physical Windows Mouse exclusion evidence");
        if(!c.Path.Equals(pin.HidPath,StringComparison.OrdinalIgnoreCase)||!c.InstanceId.Equals(pin.HidInstanceId,StringComparison.OrdinalIgnoreCase)
          ||!c.UsbAncestor.Equals(pin.UsbAncestor,StringComparison.OrdinalIgnoreCase)||!c.ContainerId.Equals(pin.ContainerId,StringComparison.OrdinalIgnoreCase)
          ||!c.LocationPaths.Contains(pin.PhysicalLocation,StringComparer.OrdinalIgnoreCase)||c.CapsSha256!=pin.CapsSha256)
            throw new InvalidDataException("Pinned path/physical identity/capabilities mismatch");
        return c;
    }
    public static void CheckPostOpenPhysical(Collection selected,Pin pin,PhysicalIdentity fresh)
    {
        if(!fresh.InstanceId.Equals(selected.InstanceId,StringComparison.OrdinalIgnoreCase)
            ||!fresh.InstanceId.Equals(pin.HidInstanceId,StringComparison.OrdinalIgnoreCase)
            ||!fresh.UsbAncestor.Equals(selected.UsbAncestor,StringComparison.OrdinalIgnoreCase)
            ||!fresh.UsbAncestor.Equals(pin.UsbAncestor,StringComparison.OrdinalIgnoreCase)
            ||!fresh.ContainerId.Equals(selected.ContainerId,StringComparison.OrdinalIgnoreCase)
            ||!fresh.ContainerId.Equals(pin.ContainerId,StringComparison.OrdinalIgnoreCase)
            ||!fresh.LocationPaths.Contains(pin.PhysicalLocation,StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Post-open Configuration Manager physical identity mismatch");
    }
    internal static void RevalidatePhysicalWithoutHidOpen(Collection selected,Pin pin)
    {
        uint result=MetaNative.CM_Locate_DevNodeW(out uint node,selected.InstanceId,0);
        if(result!=0)throw new IOException($"Post-open CM_Locate_DevNodeW failed:{result}");
        string instance=Instance(node);var p=Physical(node);
        CheckPostOpenPhysical(selected,pin,new(instance,p.Usb,p.Locations,p.Container));
    }
    internal static NativeReportIo OpenExclusive(Collection c)=>OpenExclusive(c,Wire.Validate);
    internal static NativeReportIo OpenExclusive(Collection c,ReportValidator validateWrite)
    {
        ArgumentNullException.ThrowIfNull(validateWrite);
        if(!c.Eligible)throw new InvalidDataException("Not exact boot collection");
        // Dedicated tool-only binding; the production generic MI_00 rules are untouched.
        var h=Native.CreateFileW(c.Path,0xC0000000,0,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(h.IsInvalid){int e=Marshal.GetLastPInvokeError();h.Dispose();throw new Win32Exception(e,"Fully exclusive read/write handle refused");}
        try
        {var d=Describe(h);if(d.Vid!=c.Vid||d.Pid!=c.Pid||d.Sha!=c.CapsSha256)throw new InvalidDataException("Opened handle metadata drift");return new NativeReportIo(h,validateWrite);}
        catch{h.Dispose();throw;}
    }
}

using System.Buffers.Binary;
using BootReadOnly;

namespace AppOnlyWriter;

public static class BootWriteWire
{
    public const int Start=0x1C000,End=0x53000,Page=4096,Width=32,Length=End-Start;
    public static readonly byte[] Version=[2,0,2,0];
    public static byte[] Frame(byte command,ReadOnlySpan<byte> payload=default)
    {var r=new byte[65];r[1]=command;payload.CopyTo(r.AsSpan(2));Validate(r);return r;}
    public static byte[] Address(int address)
    {Span<byte> p=stackalloc byte[4];BinaryPrimitives.WriteInt32LittleEndian(p,address);return Frame(0x20,p);}
    public static byte[] Action(byte action)=>Frame(0x1F,[action]);
    public static byte[] Stage(ReadOnlySpan<byte> data)
    {if(data.Length!=32)throw new InvalidDataException("Exactly32 staged bytes");var p=new byte[35];p[0]=32;data.CopyTo(p.AsSpan(3));return Frame(0x22,p);}
    public static byte[] StagePageChunk(int offset,ReadOnlySpan<byte> data)
    {
        if(data.Length is<1 or>60||offset<0||offset+data.Length>Page||data.Length!=60&&offset+data.Length!=Page)throw new InvalidDataException("Official page staging requires 60-byte chunks or a bounded final chunk");
        var p=new byte[3+data.Length];p[0]=(byte)data.Length;BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1,2),(ushort)offset);data.CopyTo(p.AsSpan(3));return Frame(0x22,p);
    }
    public static byte[] Marker()=>Frame(0x10,"ASUSHIDFWU"u8);
    public static void Validate(ReadOnlySpan<byte> r)
    {
        if(r.Length!=65||r[0]!=0)throw new InvalidDataException("Boot report must be65/RID0");int used;
        switch(r[1])
        {
            case 0x82:case 0xAA:case 0x8F:used=2;break;
            case 0x10:if(!r.Slice(2,10).SequenceEqual("ASUSHIDFWU"u8))throw new InvalidDataException("Bad marker");used=12;break;
            case 0x20:
                int a=BinaryPrimitives.ReadInt32LittleEndian(r[2..6]);
                if(a<Start||a>End-Width||(a&31)!=0)throw new InvalidDataException("Outside aligned app32 range");used=6;break;
            case 0x21:
                int length=BinaryPrimitives.ReadUInt16LittleEndian(r[2..4]);if(length is not(32 or 4096))throw new InvalidDataException("Only length32 or official page4096");used=4;break;
            case 0x22:
                int count=r[2],offset=BinaryPrimitives.ReadUInt16LittleEndian(r[3..5]);if(count is<1 or>60||offset+count>Page||count!=60&&offset+count!=Page&&!(offset==0&&count==32))throw new InvalidDataException("Invalid 32/page staging range");used=5+count;break;
            case 0x1F:
                if(r[2] is not(1 or 5 or 0x51))throw new InvalidDataException("Forbidden1F action");used=3;break;
            default:throw new InvalidDataException("Forbidden boot command");
        }
        if(r[used..].ContainsAnyExcept((byte)0))throw new InvalidDataException("Nonzero reserved bytes");
    }
    public static byte[] Reply(byte command,ReadOnlySpan<byte> report)
    {
        int n=command switch{0x82=>4,0xAA=>32,0x8F=>2,_=>throw new InvalidDataException("Not a query")};
        if(report.Length!=65||report[0]!=0||report[1]!=(command&0x7F)||report[(2+n)..].ContainsAnyExcept((byte)0))throw new InvalidDataException("Reply framing/padding mismatch");
        if(command==0x8F&&report[3]!=0)throw new InvalidDataException($"Boot status error {report[3]}");
        return report.Slice(2,n).ToArray();
    }
}

public interface IBootProtocol:IDisposable
{
    byte[] QueryVersion(CancellationToken ct);
    byte[] QueryStatus(CancellationToken ct);
    byte[] QueryResult(CancellationToken ct);
    void SetAddress(int address,CancellationToken ct);
    void SetLength32(CancellationToken ct);
    void Marker(CancellationToken ct);
    void Stage(ReadOnlySpan<byte> data,CancellationToken ct);
    void Request(byte action,CancellationToken ct);
}
public interface IBootPageProtocol
{
    void SetLength4096(CancellationToken ct);
    void StagePageChunk(int offset,ReadOnlySpan<byte> data,CancellationToken ct);
}

public sealed class BootProtocolSession:IBootProtocol,IBootPageProtocol
{
    private readonly IReportIo _io;private readonly ITrace _trace;private bool _version,_closed,_traceFailed;private int _gate,_length;
    public BootProtocolSession(IReportIo io,ITrace trace){_io=io;_trace=trace;}
    internal void RequireTraceCapacity(int records,long bytes)
    {if(_trace is not TraceFile bounded)throw new InvalidOperationException("Writer transaction requires bounded file trace");bounded.RequireCapacity(records,bytes);}
    internal void FlushTraceBoundary(){Guard(default);TraceBarrier();}
    private void TraceAdd(string kind,object value){try{_trace.Add(kind,value);}catch{_traceFailed=true;throw;}}
    private void TraceBarrier(){try{_trace.DurableBarrier();}catch{_traceFailed=true;throw;}}
    private void Guard(CancellationToken ct){ct.ThrowIfCancellationRequested();if(_closed||_traceFailed)throw new InvalidOperationException("Session closed/trace failed");}
    private void Enter(){if(Interlocked.CompareExchange(ref _gate,1,0)!=0)throw new InvalidOperationException("One boot operation only");}
    private void Exit()=>Volatile.Write(ref _gate,0);
    private void Quiet(CancellationToken ct)
    {Guard(ct);var r=_io.Read(10,ct);TraceAdd("quiet",new{r.Kind,r.Bytes,r.NativeError,r.CancellationJoined,raw=Convert.ToHexString(r.Data)});if(r.Kind!=IoKind.TimedOut||r.Bytes!=0||!r.CancellationJoined||r.NativeError!=995)throw new InvalidDataException("Unsolicited boot reply");}
    private void Send(byte[] frame,CancellationToken ct)
    {
        Guard(ct);BootWriteWire.Validate(frame);if(frame[1]!=0x82&&!_version)throw new InvalidOperationException("Version first");
        if(frame[1]==0x21){int length=BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2,2));if(_length==length)throw new InvalidOperationException("Duplicate boot length/view command");_length=length;}
        TraceAdd("tx",new{raw=Convert.ToHexString(frame)});
        if(frame[1]==0x1F&&frame[2] is 1 or 0x51)TraceBarrier();
        var r=_io.Write(frame,1000,ct);TraceAdd("tx-result",new{r.Kind,r.Bytes,r.NativeError,r.CancellationJoined});
        if(r.Kind!=IoKind.Completed||r.Bytes!=65||!r.CancellationJoined)throw new IOException("Boot OUT not completed65; never resent");Guard(ct);
    }
    private byte[] Query(byte command,CancellationToken ct)
    {
        Enter();try{Quiet(ct);Send(BootWriteWire.Frame(command),ct);var r=_io.Read(1500,ct);TraceAdd("rx",new{r.Kind,r.Bytes,r.NativeError,r.CancellationJoined,raw=Convert.ToHexString(r.Data)});
            if(r.Kind!=IoKind.Completed||r.Bytes!=65||!r.CancellationJoined)throw new IOException("Boot query incomplete");
            var value=BootWriteWire.Reply(command,r.Data);Quiet(ct);if(command==0x82){if(!value.AsSpan().SequenceEqual(BootWriteWire.Version))throw new InvalidDataException("Boot version mismatch");_version=true;}return value;
        }finally{Exit();}
    }
    private void Command(byte[] frame,CancellationToken ct){Enter();try{Send(frame,ct);}finally{Exit();}}
    public byte[] QueryVersion(CancellationToken ct)=>Query(0x82,ct);
    public byte[] QueryStatus(CancellationToken ct)=>Query(0x8F,ct);
    public byte[] QueryResult(CancellationToken ct)=>Query(0xAA,ct);
    public void SetAddress(int address,CancellationToken ct)=>Command(BootWriteWire.Address(address),ct);
    public void SetLength32(CancellationToken ct)=>Command(BootWriteWire.Frame(0x21,[32,0]),ct);
    public void SetLength4096(CancellationToken ct)=>Command(BootWriteWire.Frame(0x21,[0,16]),ct);
    public void StagePageChunk(int offset,ReadOnlySpan<byte> data,CancellationToken ct)=>Command(BootWriteWire.StagePageChunk(offset,data),ct);
    public void Marker(CancellationToken ct)=>Command(BootWriteWire.Marker(),ct);
    public void Stage(ReadOnlySpan<byte> data,CancellationToken ct)=>Command(BootWriteWire.Stage(data),ct);
    public void Request(byte action,CancellationToken ct)=>Command(BootWriteWire.Action(action),ct);
    public void Dispose(){if(_closed)return;_closed=true;_io.Dispose();}
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace BootReadOnly;

public static class Wire
{
    public const int Start=0x1C000, End=0x53000, Width=60, Length=End-Start;
    public static readonly byte[] Version=[2,0,2,0];
    public static byte[] Frame(byte command, ReadOnlySpan<byte> payload=default)
    { var r=new byte[65];r[1]=command;payload.CopyTo(r.AsSpan(2));Validate(r);return r; }
    public static byte[] Address(int address)
    { Span<byte> p=stackalloc byte[4];BinaryPrimitives.WriteInt32LittleEndian(p,address);return Frame(0x20,p); }
    public static void Validate(ReadOnlySpan<byte> r)
    {
        if(r.Length!=65||r[0]!=0)throw new InvalidDataException("Report must be65bytes/RID0");
        int used;
        switch(r[1])
        {
            case 0x82:case 0xAA:used=2;break;
            case 0x20:
                int a=BinaryPrimitives.ReadInt32LittleEndian(r[2..6]);
                if(a<Start||a>End-Width||(a&3)!=0)throw new InvalidDataException("Read address outside fixed60-byte app range");
                used=6;break;
            case 0x21:
                if(r[2]!=60||r[3]!=0)throw new InvalidDataException("Only fixed length60");used=4;break;
            case 0x1F:
                if(r[2]!=5)throw new InvalidDataException("Only read action05");used=3;break;
            default:throw new InvalidDataException("Forbidden boot command");
        }
        if(r[used..].ContainsAnyExcept((byte)0))throw new InvalidDataException("Nonzero reserved request bytes");
    }
    public static byte[] Reply(byte command,ReadOnlySpan<byte> report)
    {
        int n=command==0x82?4:command==0xAA?60:throw new InvalidDataException("Not a query");
        if(report.Length!=65||report[0]!=0||report[1]!=(command&0x7F)||report[(2+n)..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Reply length/RID/tag/padding mismatch");
        return report.Slice(2,n).ToArray();
    }
    public static string Sha(ReadOnlySpan<byte> data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

public enum IoKind { Completed, TimedOut, Canceled }
public sealed record IoResult(IoKind Kind,int Bytes,byte[] Data,int NativeError,bool CancellationJoined);
public interface IReportIo:IDisposable
{ IoResult Write(byte[] report,int timeoutMs,CancellationToken ct);IoResult Read(int timeoutMs,CancellationToken ct); }
public interface ITrace { void Add(string kind,object value);void DurableBarrier(){} }
public sealed class NullTrace:ITrace { public void Add(string kind,object value){} }
public sealed class TraceFile: ITrace,IDisposable
{
    readonly FileStream file;readonly int maxRecords,maxRecordBytes;readonly long maxBytes;readonly bool bufferedWriter;readonly Action? beforeDurableFlush;
    long bytes;int records;bool failed,disposed;
    public int RecordsWritten=>records;public long BytesWritten=>bytes;
    public int DurableFlushes{get;private set;}public bool BufferedWriter=>bufferedWriter;
    public TraceFile(string directory)
        :this(directory,200000,64L*1024*1024,int.MaxValue){}
    internal TraceFile(string directory,int maxRecords,long maxBytes,int maxRecordBytes)
        :this(directory,maxRecords,maxBytes,maxRecordBytes,false){}
    internal TraceFile(string directory,int maxRecords,long maxBytes,int maxRecordBytes,bool bufferedWriter,Action? beforeDurableFlush=null)
    {
        if(maxRecords is<1 or>1000000||maxBytes is<1 or>256L*1024*1024||maxRecordBytes<1)throw new ArgumentOutOfRangeException(nameof(maxRecords));
        this.maxRecords=maxRecords;this.maxBytes=maxBytes;this.maxRecordBytes=maxRecordBytes;this.bufferedWriter=bufferedWriter;this.beforeDurableFlush=beforeDurableFlush;
        Directory.CreateDirectory(directory);file=new FileStream(Path.Combine(directory,"trace.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,bufferedWriter?65536:4096,bufferedWriter?FileOptions.None:FileOptions.WriteThrough);
    }
    internal void RequireCapacity(int additionalRecords,long additionalBytes)
    {if(additionalRecords<0||additionalBytes<0||additionalRecords>maxRecords-records||additionalBytes>maxBytes-bytes)throw new IOException("Insufficient bounded trace capacity before destructive intent");}
    public void Add(string kind,object value)
    {
        if(failed||disposed)throw new InvalidOperationException("Trace channel failed/closed");
        var line=JsonSerializer.SerializeToUtf8Bytes(new{at=DateTimeOffset.UtcNow,ordinal=records+1,kind,value});
        if(records>=maxRecords||line.Length+1>maxRecordBytes||bytes+line.Length+1>maxBytes)throw new IOException("Bounded trace budget exhausted");
        try{file.Write(line);file.WriteByte(10);if(!bufferedWriter)FlushCore();records++;bytes+=line.Length+1;}
        catch{failed=true;throw;}
    }
    private void FlushCore(){beforeDurableFlush?.Invoke();file.Flush(true);DurableFlushes++;}
    public void DurableBarrier()
    {if(failed||disposed)throw new InvalidOperationException("Trace channel failed/closed");try{FlushCore();}catch{failed=true;throw;}}
    public void Dispose()
    {if(disposed)return;disposed=true;try{if(bufferedWriter&&!failed)FlushCore();}catch{failed=true;throw;}finally{file.Dispose();}}
}

public sealed class ReadSession:IReportIo
{
    readonly IReportIo io;readonly ITrace trace;readonly Stopwatch elapsed=Stopwatch.StartNew();
    readonly int maxQueries,maxWrites,wallMs,quietMs;bool lengthSent,versionVerified,closed,poisoned;int serial,operationGate;
    public int Queries{get;private set;}public int Writes{get;private set;}
    public ReadSession(IReportIo io,ITrace trace,int maxQueries=80000,int maxWrites=80000,int wallMs=1800000,int quietMs=10)
    {this.io=io;this.trace=trace;this.maxQueries=maxQueries;this.maxWrites=maxWrites;this.wallMs=wallMs;this.quietMs=quietMs;}
    void Guard(CancellationToken ct)
    {ct.ThrowIfCancellationRequested();if(closed||poisoned)throw new InvalidOperationException("Session closed/poisoned");if(elapsed.ElapsedMilliseconds>wallMs)throw new TimeoutException("Session wall-clock budget");}
    void NoUnsolicited(CancellationToken ct)
    {
        Guard(ct);var r=io.Read(quietMs,ct);
        trace.Add("quiet-read",new{r.Kind,r.Bytes,r.NativeError,r.CancellationJoined,raw=Convert.ToHexString(r.Data)});
        if(r.Kind!=IoKind.TimedOut||!r.CancellationJoined||r.Bytes!=0||r.Data.Length!=0||r.NativeError!=995){poisoned=true;throw new InvalidDataException("Unsolicited/extra response or non-joined quiet read");}
        Guard(ct);
    }
    void Send(byte[] frame,CancellationToken ct)
    {
        Guard(ct);Wire.Validate(frame);if(++Writes>maxWrites)throw new TimeoutException("Write budget");
        if(frame[1]!=0x82&&!versionVerified)throw new InvalidDataException("Raw boot version must match before read setup");
        if(frame[1]==0x21){if(lengthSent)throw new InvalidDataException("Length21 may be sent only once");lengthSent=true;}
        if(frame[1] is 0x20 or 0x1F or 0xAA &&!lengthSent)throw new InvalidDataException("Read setup missing");
        trace.Add("tx-attempt",new{number=++serial,raw=Convert.ToHexString(frame)});
        var r=io.Write(frame,1000,ct);trace.Add("tx-result",new{r.Kind,r.Bytes,r.NativeError,r.CancellationJoined});
        if(r.Kind!=IoKind.Completed||r.Bytes!=65||!r.CancellationJoined){poisoned=true;throw new IOException("Write not explicitly completed65bytes; not retried");}
        Guard(ct);
    }
    public void WriteCommand(byte[] frame,CancellationToken ct)
    {
        if(Interlocked.CompareExchange(ref operationGate,1,0)!=0)throw new InvalidOperationException("Concurrent session operation refused");
        try{Wire.Validate(frame);if(frame[1] is 0x82 or 0xAA)throw new InvalidOperationException("Query must use Query");Send(frame,ct);}
        catch{poisoned=true;throw;}
        finally{Volatile.Write(ref operationGate,0);}
    }
    public byte[] Query(byte command,CancellationToken ct)
    {
        if(Interlocked.CompareExchange(ref operationGate,1,0)!=0)throw new InvalidOperationException("Concurrent query refused");
        try{
        if(command is not(0x82 or 0xAA))throw new InvalidDataException("Forbidden query");
        if(++Queries>maxQueries)throw new TimeoutException("Query budget");
        NoUnsolicited(ct);Send(Wire.Frame(command),ct);
        var r=io.Read(1500,ct);trace.Add("rx-result",new{r.Kind,r.Bytes,r.NativeError,r.CancellationJoined,raw=Convert.ToHexString(r.Data)});
        if(r.Kind!=IoKind.Completed||r.Bytes!=65||!r.CancellationJoined){poisoned=true;throw new IOException("Query did not complete one65byte report");}
        var payload=Wire.Reply(command,r.Data);NoUnsolicited(ct);
        if(command==0x82){if(!payload.AsSpan().SequenceEqual(Wire.Version)){poisoned=true;throw new InvalidDataException("Raw boot version mismatch");}versionVerified=true;}
        return payload;
        }catch{poisoned=true;throw;}
        finally{Volatile.Write(ref operationGate,0);}
    }
    // No implicit public raw transfer seam through this protocol session.
    IoResult IReportIo.Write(byte[] r,int t,CancellationToken c)=>throw new NotSupportedException();
    IoResult IReportIo.Read(int t,CancellationToken c)=>throw new NotSupportedException();
    public void Dispose(){if(closed)return;closed=true;io.Dispose();}
}

public sealed class BackupReader
{
    readonly ReadSession session;readonly ITrace trace;readonly int polls,backoffMs;
    readonly Dictionary<int,byte[]> anchors=[];bool initialized,smallPassed;readonly Guid id=Guid.NewGuid();
    public BackupReader(ReadSession session,ITrace trace,int polls=24,int backoffMs=25)
    {if(polls is <1 or >64||backoffMs is <0 or >100)throw new ArgumentOutOfRangeException();this.session=session;this.trace=trace;this.polls=polls;this.backoffMs=backoffMs;}
    void Wait(CancellationToken ct){if(backoffMs>0&&ct.WaitHandle.WaitOne(backoffMs))ct.ThrowIfCancellationRequested();}
    void Request(int address,CancellationToken ct)
    {session.WriteCommand(Wire.Address(address),ct);session.WriteCommand(Wire.Frame(0x1F,[5]),ct);}
    byte[]? Changed(byte[] baseline,CancellationToken ct)
    {for(int k=0;k<polls;k++){var r=session.Query(0xAA,ct);if(!r.AsSpan().SequenceEqual(baseline))return r;Wait(ct);}return null;}
    void Fence(int address,byte[] known,CancellationToken ct)
    {Request(address,ct);for(int k=0;k<polls;k++){if(session.Query(0xAA,ct).AsSpan().SequenceEqual(known))return;Wait(ct);}throw new TimeoutException("Anchor content fence not observed");}
    public void Initialize(CancellationToken ct)
    {
        if(initialized)throw new InvalidOperationException("Already initialized");initialized=true;
        if(!session.Query(0x82,ct).AsSpan().SequenceEqual(Wire.Version))throw new InvalidDataException("Raw boot version mismatch");
        session.WriteCommand(Wire.Frame(0x21,[60,0]),ct);var baseline=session.Query(0xAA,ct);
        Request(Wire.Start,ct);var a=Changed(baseline,ct)??throw new TimeoutException("AnchorA unchanged/unproved");
        Request(Wire.End-60,ct);var b=Changed(a,ct)??throw new TimeoutException("AnchorB not distinct/fresh");
        anchors.Add(Wire.Start,a);anchors.Add(Wire.End-60,b);
        trace.Add("anchors",new{session=id,a=Wire.Sha(a),b=Wire.Sha(b)});
    }
    public byte[] Target(int address,CancellationToken ct)
    {
        if(anchors.Count!=2)throw new InvalidOperationException("No proven distinct anchors");
        _=Wire.Address(address);
        foreach(var (anchor,known) in anchors)
        {Fence(anchor,known,ct);Request(address,ct);var value=Changed(known,ct);if(value!=null){trace.Add("fresh-window",new{session=id,address,anchor,sha256=Wire.Sha(value)});return value;}}
        throw new TimeoutException("No content transition against either anchor");
    }
    public void SmallTrial(CancellationToken ct)
    {
        if(!initialized)Initialize(ct);if(smallPassed)throw new InvalidOperationException("Small trial already complete");
        int[] points=[Wire.Start,Wire.End-60,Wire.Start+60,Wire.Start+0x1000,Wire.End-120];
        var first=points.Select(a=>Target(a,ct)).ToArray();
        for(int i=points.Length-1;i>=0;i--)
            if(!Target(points[i],ct).AsSpan().SequenceEqual(first[i]))throw new InvalidDataException("Small-trial repeated window disagreement");
        if(!first[0].AsSpan().SequenceEqual(anchors[Wire.Start])||!first[1].AsSpan().SequenceEqual(anchors[Wire.End-60]))throw new InvalidDataException("Anchor target disagreement");
        smallPassed=true;trace.Add("small-trial-pass",new{session=id,points,windows=10});
    }
    static IEnumerable<int> Addresses(bool reverse)
    {var a=new List<int>();for(int x=Wire.Start;x+60<=Wire.End;x+=60)a.Add(x);if(a[^1]!=Wire.End-60)a.Add(Wire.End-60);if(reverse)a.Reverse();return a;}
    public byte[] Pass(bool reverse,CancellationToken ct)
    {
        if(!smallPassed)throw new InvalidOperationException("Same-session small trial required");
        var bytes=new byte[Wire.Length];var seen=new bool[bytes.Length];int chunks=0;
        foreach(int address in Addresses(reverse))
        {var p=Target(address,ct);MergeWindow(bytes,seen,address,p);if(++chunks%100==0)trace.Add("pass-progress",new{session=id,reverse,chunks});}
        if(seen.Any(x=>!x))throw new InvalidDataException("Backup incomplete");
        trace.Add("pass-complete",new{session=id,reverse,chunks,sha256=Wire.Sha(bytes)});return bytes;
    }
    public static void MergeWindow(byte[] target,bool[] seen,int address,byte[] value)
    {
        _=Wire.Address(address);if(value.Length!=60||target.Length!=Wire.Length||seen.Length!=Wire.Length)throw new InvalidDataException();
        int o=address-Wire.Start;for(int i=0;i<60;i++){if(seen[o+i]&&target[o+i]!=value[i])throw new InvalidDataException("Overlap disagreement");target[o+i]=value[i];seen[o+i]=true;}
    }
    public static void CheckTwoPasses(byte[] first,byte[] second)
    {if(first.Length!=Wire.Length||!first.AsSpan().SequenceEqual(second))throw new InvalidDataException("Two-pass disagreement/incomplete");}
}

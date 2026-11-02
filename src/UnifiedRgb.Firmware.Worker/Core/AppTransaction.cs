using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace AppOnlyWriter;

public sealed record LedgerIdentity(string Device,string OriginalSha256,string TargetSha256,Guid RunId);
public sealed record LedgerRecord(long Ordinal,string Kind,DateTimeOffset At,int Page,int Address,string Operation,
    string? SeedSha256,string? ExpectedSha256,string? BeforeImageSha256,bool UnknownOutcome,int ProgramIntentsAfterErase,int EraseCycle=0);
public sealed record OperationLineage(int Schema,string Mode,LedgerIdentity Identity,string BeforeImagePath,string BeforeImageSha256,
    string OriginalBeforeImagePath,string OriginalBeforeImageSha256,string? ParentLedgerPath=null,string? ParentLedgerSha256=null,
    LedgerIdentity? ParentIdentity=null,string? ParentLineageSha256=null);
public sealed class DurableLedger
{
    private readonly string _path,_identityPath;private readonly List<LedgerRecord> _records=[];private int _resolvedCount=-1;
    public IReadOnlyList<LedgerRecord> Records=>_records;public LedgerIdentity Identity{get;}
    public string FilePath=>Path.GetFullPath(_path);
    public OperationLineage? Lineage
    {get{var p=_path+".lineage.json";if(!File.Exists(p))return null;if(new FileInfo(p).Length>16*1024)throw new InvalidDataException("Lineage bound exceeded");return JsonSerializer.Deserialize<OperationLineage>(File.ReadAllBytes(p))??throw new InvalidDataException("Bad lineage");}}
    public DurableLedger(string path,LedgerIdentity expected)
    {
        _path=path;_identityPath=path+".identity.json";Identity=expected;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if(File.Exists(path))
        {
            if(new FileInfo(path).Length>16*1024*1024||!File.Exists(_identityPath)||new FileInfo(_identityPath).Length>4096)throw new InvalidDataException("Ledger size/identity missing");
            var identity=JsonSerializer.Deserialize<LedgerIdentity>(File.ReadAllBytes(_identityPath))??throw new InvalidDataException("Bad ledger identity");
            if(identity!=expected)throw new InvalidDataException("Ledger belongs to another device/original/target/run lineage");
            foreach(var line in File.ReadLines(path))
            {if(string.IsNullOrWhiteSpace(line))continue;var r=JsonSerializer.Deserialize<LedgerRecord>(line)??throw new InvalidDataException("Bad ledger");if(r.Ordinal!=_records.Count+1)throw new InvalidDataException("Ledger ordinal");_records.Add(r);}
            AssertResolved();
        }
        else if(File.Exists(_identityPath))throw new InvalidDataException("Ledger identity without ledger");
    }
    public void AssertResolved()
    {
        if(_resolvedCount==_records.Count)return;LedgerRecord? pending=null;
        foreach(var r in _records)
        {
            if(r.Kind is "EraseIntent" or "ProgramIntent")
            {if(pending is not null||!r.UnknownOutcome)throw new InvalidDataException("Invalid destructive intent");pending=r;}
            else if(r.Kind=="Retired")
            {if(pending is null||r.Page!=pending.Page||r.Address!=pending.Address||r.EraseCycle!=pending.EraseCycle||r.Operation!=pending.Operation||r.SeedSha256!=pending.SeedSha256||r.ExpectedSha256!=pending.ExpectedSha256||r.UnknownOutcome)throw new InvalidDataException("Invalid destructive retirement");pending=null;}
        }
        if(pending is not null)throw new InvalidDataException("Unresolved destructive outcome is terminal; independent reviewed recovery required");_resolvedCount=_records.Count;
    }
    public void BindLineage(OperationLineage lineage)
    {
        if(lineage.Schema!=1||lineage.Identity!=Identity)throw new InvalidDataException("Lineage identity mismatch");
        var existing=Lineage;if(existing is not null){if(existing!=lineage)throw new InvalidDataException("Immutable lineage differs");return;}
        if(_records.Count!=0||File.Exists(_path))throw new InvalidDataException("Historical ledger without immutable lineage cannot resume");
        using var f=new FileStream(_path+".lineage.json",FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);JsonSerializer.Serialize(f,lineage);f.Flush(true);
    }
    public void VerifyStoredSnapshot()
    {
        if(!File.Exists(_path)){if(_records.Count!=0)throw new InvalidDataException("Ledger disappeared");return;}
        if(new FileInfo(_path).Length>16*1024*1024||!File.Exists(_identityPath)||JsonSerializer.Deserialize<LedgerIdentity>(File.ReadAllBytes(_identityPath))!=Identity)throw new InvalidDataException("Stored ledger identity changed");
        var stored=File.ReadLines(_path).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonSerializer.Deserialize<LedgerRecord>(x)??throw new InvalidDataException("Bad stored ledger")).ToArray();
        if(!stored.SequenceEqual(_records))throw new InvalidDataException("Stored ledger changed after load");
    }
    public void EnsureStarted()
    {
        if(File.Exists(_identityPath))return;
        using var f=new FileStream(_identityPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);JsonSerializer.Serialize(f,Identity);f.Flush(true);
        using var ledger=new FileStream(_path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);ledger.Flush(true);
    }
    public int NextEraseCycle(int page)=>CurrentEraseCycle(page)+1;
    internal void RequireCapacity(int additionalRecords)
    {
        if(additionalRecords<0)throw new ArgumentOutOfRangeException(nameof(additionalRecords));
        var length=File.Exists(_path)?new FileInfo(_path).Length:0;
        if(length>WriterBudget.LedgerBytes-(long)additionalRecords*WriterBudget.LedgerRecordBytes)throw new IOException("Insufficient bounded ledger capacity before destructive intent");
    }
    public int CurrentEraseCycle(int page)=>_records.LastOrDefault(x=>x.Page==page&&(x.Kind is "EraseIntent" or "ParentEraseEffectBound"))?.EraseCycle??0;
    public void Append(string kind,int page,int address,string operation,byte[]? seed,byte[]? expected,string? beforeSha,bool unknown,int eraseCycle=0)
    {
        EnsureStarted();var r=new LedgerRecord(_records.Count+1,kind,DateTimeOffset.UtcNow,page,address,operation,
            seed is null?null:Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant(),expected is null?null:Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),beforeSha,unknown,
            _records.Count(x=>x.Page==page&&x.Kind=="ProgramIntent"),eraseCycle);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(r);var length=File.Exists(_path)?new FileInfo(_path).Length:0;
        if(bytes.Length+1>WriterBudget.LedgerRecordBytes||length+bytes.Length+1>WriterBudget.LedgerBytes)throw new IOException("Bounded ledger budget exhausted");
        using var f=new FileStream(_path,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);f.Write(bytes);f.WriteByte(10);f.Flush(true);_records.Add(r);
    }
    public bool CanReuseErasedPage(int page)
    {
        var effect=_records.LastOrDefault(x=>x.Page==page&&(x.Kind is "EraseEffectVerified" or "ParentEraseEffectBound")&&x.EraseCycle>0);
        return effect is not null&&!_records.Any(x=>x.Page==page&&x.Ordinal>effect.Ordinal&&(x.Kind is "ProgramIntent" or "EraseIntent"));
    }
    public bool PageProgrammed(int page,int address,byte[] expected)
    {
        var cycle=CurrentEraseCycle(page);if(cycle==0)return false;var hash=Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        var lastErase=_records.LastOrDefault(x=>x.Page==page&&(x.Kind is "EraseIntent" or "ParentEraseEffectBound"))?.Ordinal??0;
        return _records.Any(x=>x.Page==page&&x.Address==address&&(x.Kind=="ProgramReadback"||x.Kind=="Retired"&&x.Operation is("program" or"program-page"))&&x.ExpectedSha256==hash&&!x.UnknownOutcome&&x.EraseCycle==cycle&&x.Ordinal>lastErase);
    }
}

public sealed class AppTransaction
{
    private readonly IBootProtocol _io;private readonly DurableLedger _ledger;private readonly string _beforePath;private readonly int _polls,_pageLimit;
    private readonly RestoreSource? _restoreSource;
    private readonly Action? _beforeDestructive;
    private bool _unknown,_prepared;private int _operations;
    public AppTransaction(IBootProtocol io,DurableLedger ledger,string beforeImagePath,int polls=200,int pageLimit=55,RestoreSource? restoreSource=null,Action? beforeDestructive=null)
    {if(polls is<1 or>10000||pageLimit is<1 or>55)throw new ArgumentOutOfRangeException(nameof(polls));_io=io;_ledger=ledger;_beforePath=beforeImagePath;_polls=polls;_pageLimit=pageLimit;_restoreSource=restoreSource?.Snapshot();_beforeDestructive=beforeDestructive;}
    private static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static bool FF(ReadOnlySpan<byte> b)=>!b.ContainsAnyExcept((byte)0xFF);
    private static byte[] Erased(){var b=new byte[32];Array.Fill(b,(byte)0xFF);return b;}
    private void Budget(){if(++_operations>2000000)throw new TimeoutException("Whole transaction command budget");}
    private void RequireIo(){if(_unknown)throw new InvalidOperationException("Unknown destructive outcome freezes ALL public I/O; independent reviewed recovery required");_ledger.AssertResolved();}
    private void Address(int a,CancellationToken ct,bool duringUnknown=false){if(_unknown&&!duringUnknown)throw new InvalidOperationException("Unknown destructive outcome freezes address");Budget();_io.SetAddress(a,ct);}
    private void Marker(CancellationToken ct){if(_unknown)throw new InvalidOperationException("Unknown destructive outcome forbids marker");Budget();_io.Marker(ct);}
    private byte[] ReadAt(int address,ReadOnlySpan<byte> expected,CancellationToken ct,bool duringUnknown=false)
    {
        Address(address,ct,duringUnknown);for(var i=0;i<_polls;i++){Budget();_io.Request(5,ct);Budget();var value=_io.QueryResult(ct);if(value.Length!=32)throw new InvalidDataException("AA32 required");if(value.AsSpan().SequenceEqual(expected))return value;}
        throw new TimeoutException($"Read/content fence inconclusive at {address:X}");
    }
    private void ObserveStatus(CancellationToken ct)
    {
        Budget();var status=_io.QueryStatus(ct);if(status.Length!=2||status[1]!=0)throw new InvalidDataException("Destructive status/error qualification failed");
    }
    private byte[] ReadDistinct(ReadOnlySpan<byte> seed,ReadOnlySpan<byte> expected,CancellationToken ct)
    {
        if(seed.SequenceEqual(expected))throw new InvalidDataException("Distinct transition required");
        ObserveStatus(ct);
        for(var i=0;i<_polls;i++){Budget();_io.Request(5,ct);Budget();var value=_io.QueryResult(ct);if(value.AsSpan().SequenceEqual(expected)){ObserveStatus(ct);return value;}if(!value.AsSpan().SequenceEqual(seed))throw new InvalidDataException("Unexpected read-result transition");}
        ObserveStatus(ct);throw new TimeoutException("Destructive outcome remains unknown; no retry/reset/parameter change");
    }
    public byte[] Read32(int address,CancellationToken ct){RequireIo();if(address<BootWriteWire.Start||address>BootWriteWire.End-32||(address&31)!=0)throw new InvalidDataException("Read outside app32");Address(address,ct);Budget();_io.Request(5,ct);Budget();var b=_io.QueryResult(ct);if(b.Length!=32)throw new InvalidDataException("AA32 required");return b;}
    public byte[] ReadApp(CancellationToken ct){RequireIo();var b=new byte[BootWriteWire.Length];for(var a=BootWriteWire.Start;a<BootWriteWire.End;a+=32)Read32(a,ct).CopyTo(b,a-BootWriteWire.Start);return b;}
    private void PrepareSession(byte[]? expectedAnchors,CancellationToken ct)
    {
        RequireIo();if(_prepared)return;Budget();if(!_io.QueryVersion(ct).AsSpan().SequenceEqual(BootWriteWire.Version))throw new InvalidDataException("Boot02000200 required");Budget();_=_io.QueryStatus(ct);Budget();_io.SetLength32(ct);
        var anchors=new[]{BootWriteWire.Start,BootWriteWire.End-32};var before=anchors.Select(a=>Read32(a,ct)).ToArray();
        if(expectedAnchors is not null)for(var i=0;i<anchors.Length;i++)if(!before[i].AsSpan().SequenceEqual(expectedAnchors.AsSpan(anchors[i]-BootWriteWire.Start,32)))throw new InvalidDataException("Fresh nonmutating32 preimage anchor mismatch");
        Marker(ct);var status=_io.QueryStatus(ct);if((status[0]&0x80)==0||status[1]!=0)throw new InvalidDataException("10/8F sessionbit7/error qualification failed");
        for(var i=0;i<anchors.Length;i++)if(!Read32(anchors[i],ct).AsSpan().SequenceEqual(before[i]))throw new InvalidDataException("Marker changed app content");_prepared=true;
    }
    public void Preflight(byte[] expectedOriginal,CancellationToken ct){RequireIo();ImageAdmission.ValidateOriginal(expectedOriginal);PrepareSession(expectedOriginal,ct);}
    public void SaveBeforeImage(byte[] before)
    {RequireIo();if(File.Exists(_beforePath)){if(!File.ReadAllBytes(_beforePath).AsSpan().SequenceEqual(before))throw new InvalidDataException("Existing before-image differs");return;}using var f=new FileStream(_beforePath,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);f.Write(before);f.Flush(true);}
    private void IssueOnce(byte action,int page,int address,byte[] seed,byte[] expected,byte[]? stage,string beforeSha,int cycle,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var reserve=WriterBudget.Destructive(stage is not null,_polls);
        if(_operations>WriterBudget.Commands-reserve.Commands)throw new TimeoutException("Insufficient command capacity before destructive intent");
        _ledger.RequireCapacity(reserve.LedgerRecords);
        if(_io is BootProtocolSession protocol)protocol.RequireTraceCapacity(reserve.TraceRecords,(long)reserve.TraceRecords*WriterBudget.TraceRecordBytes);
        Marker(ct);Address(address,ct);if(stage is not null){if(stage.Length!=32)throw new InvalidDataException("Stage32");Budget();_io.Stage(stage,ct);}
        _unknown=true;_ledger.Append(action==1?"EraseIntent":"ProgramIntent",page,address,action==1?"erase":"program",seed,expected,beforeSha,true,cycle);Budget();_io.Request(action,ct);
        var value=ReadDistinct(seed,expected,ct);_unknown=false;_ledger.Append("Retired",page,address,action==1?"erase":"program",seed,value,beforeSha,false,cycle);
    }
    private static void Delay(int milliseconds,CancellationToken ct){if(ct.WaitHandle.WaitOne(milliseconds))ct.ThrowIfCancellationRequested();}
    private byte[] QueryStatusChecked(CancellationToken ct)
    {Budget();var status=_io.QueryStatus(ct);if(status.Length!=2||status[1]!=0)throw new InvalidDataException("Page status/error qualification failed");return status;}
    private void WaitStatusCompleted(CancellationToken ct)
    {
        QueryStatusChecked(ct);for(var i=0;i<_polls;i++){var status=QueryStatusChecked(ct);if((status[0]&3)==0)return;}
        throw new TimeoutException("Page destructive outcome remains unknown; no retry/reset/parameter change");
    }
    private void ReservePage(bool program)
    {
        var reserve=WriterBudget.PageDestructive(program,_polls);if(_operations>WriterBudget.Commands-reserve.Commands)throw new TimeoutException("Insufficient page command capacity before destructive intent");
        _ledger.RequireCapacity(reserve.LedgerRecords);if(_io is BootProtocolSession protocol)protocol.RequireTraceCapacity(reserve.TraceRecords,(long)reserve.TraceRecords*WriterBudget.TraceRecordBytes);
    }
    private void IssuePageEraseOnce(int page,byte[] seed,string beforeSha,int cycle,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ReservePage(false);Marker(ct);Address(page,ct);Delay(2,ct);
        _unknown=true;_ledger.Append("EraseIntent",page,page,"erase",seed,Erased(),beforeSha,true,cycle);Budget();_io.Request(1,ct);Delay(50,ct);WaitStatusCompleted(ct);
        for(var a=page;a<page+BootWriteWire.Page;a+=32){var value=ReadAt(a,Erased(),ct,duringUnknown:true);if(!FF(value))throw new InvalidDataException("Page erase effect incomplete");}
        _unknown=false;_ledger.Append("Retired",page,page,"erase",seed,Erased(),beforeSha,false,cycle);
    }
    private void IssuePageProgramOnce(int page,byte[] targetPage,string beforeSha,int cycle,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(_io is not IBootPageProtocol pageIo)throw new InvalidOperationException("Official page protocol unavailable");ReservePage(true);
        Marker(ct);Address(page,ct);Delay(2,ct);Budget();pageIo.SetLength4096(ct);Delay(2,ct);
        for(var offset=0;offset<BootWriteWire.Page;offset+=60){var count=Math.Min(60,BootWriteWire.Page-offset);Budget();pageIo.StagePageChunk(offset,targetPage.AsSpan(offset,count),ct);}
        _unknown=true;_ledger.Append("ProgramIntent",page,page,"program-page",Erased(),targetPage,beforeSha,true,cycle);Budget();_io.Request(0x51,ct);WaitStatusCompleted(ct);
        Budget();_io.SetLength32(ct);for(var a=page;a<page+BootWriteWire.Page;a+=32)ReadAt(a,targetPage.AsSpan(a-page,32),ct,duringUnknown:true);
        _unknown=false;_ledger.Append("Retired",page,page,"program-page",Erased(),targetPage,beforeSha,false,cycle);_ledger.Append("ProgramReadback",page,page,"program-page",Erased(),targetPage,beforeSha,false,cycle);
        if(_io is BootProtocolSession protocol)protocol.FlushTraceBoundary();
    }
    public void Run(byte[] target,bool restore,byte[]? requiredFreshPreimage,CancellationToken ct,bool officialPages=false)
    {
        RequireIo();target=target.ToArray();requiredFreshPreimage=requiredFreshPreimage?.ToArray();
        if(target.Length!=BootWriteWire.Length||FF(target.AsSpan(0,32)))throw new InvalidDataException("Target size/vector");
        if(_ledger.Records.Any(x=>x.Kind=="ProgramIntent"&&x.Operation!=(officialPages?"program-page":"program")))throw new InvalidDataException("Cannot mix 32-byte and page ledger histories");
        var admission=LedgerState.Admit(_ledger,target,restore,requiredFreshPreimage,_beforePath,_restoreSource);
        PrepareSession(admission.ExpectedCurrent,ct);
        var before=ReadApp(ct);if(!before.AsSpan().SequenceEqual(admission.ExpectedCurrent))throw new InvalidDataException("Whole current app differs from proven ledger state; no writes permitted");
        var second=ReadApp(ct);if(!before.AsSpan().SequenceEqual(second))throw new InvalidDataException("Two-pass preimage disagreement");
        if(_ledger.Lineage is null){SaveBeforeImage(before);_ledger.BindLineage(admission.Lineage);}
        var beforeSha=admission.Lineage.BeforeImageSha256;_ledger.EnsureStarted();
        // Dedicated callers may durably bind original/source provenance here. Both complete
        // reads, immutable before-image and lineage are already verified; no intent exists yet.
        _beforeDestructive?.Invoke();
        for(var pageIndex=0;pageIndex<_pageLimit;pageIndex++)
        {
            var page=BootWriteWire.Start+pageIndex*BootWriteWire.Page;var current=before.AsSpan(page-BootWriteWire.Start,BootWriteWire.Page).ToArray();var targetPage=target.AsSpan(page-BootWriteWire.Start,BootWriteWire.Page).ToArray();var alreadyComplete=current.AsSpan().SequenceEqual(targetPage);
            if(restore&&alreadyComplete)continue; // A proven unchanged original page needs no destructive recovery.
            if(officialPages){if(!FF(targetPage)&&!_ledger.PageProgrammed(page,page,targetPage))alreadyComplete=false;}
            else for(var a=page;a<page+BootWriteWire.Page;a+=32){var data=target.AsSpan(a-BootWriteWire.Start,32).ToArray();if(!FF(data)&&!_ledger.PageProgrammed(page,a,data)){alreadyComplete=false;break;}}
            if(restore&&FF(current)&&_ledger.CurrentEraseCycle(page)==0&&admission.InheritedErases.TryGetValue(page,out var parentCycle))
                _ledger.Append("ParentEraseEffectBound",page,page,"parent-erase",current,current,beforeSha,false,parentCycle);
            if(alreadyComplete)continue;var erased=_ledger.CanReuseErasedPage(page);
            if(!erased)
            {
                var witness=current.AsSpan(0,32).ToArray();if(FF(witness))throw new InvalidDataException($"Page {page:X} has FF prefix; later nonFF witness cannot safely retire erase at a different address");
                var seed=ReadAt(page,witness,ct);var cycle=_ledger.NextEraseCycle(page);
                if(officialPages)IssuePageEraseOnce(page,seed,beforeSha,cycle,ct);else IssueOnce(1,page,page,seed,Erased(),null,beforeSha,cycle,ct);
                if(!officialPages)for(var a=page;a<page+BootWriteWire.Page;a+=32){var value=ReadAt(a,Erased(),ct);if(!FF(value))throw new InvalidDataException("Erase effect incomplete");}
                _ledger.Append("EraseEffectVerified",page,page,"erase",witness,Enumerable.Repeat((byte)0xFF,4096).ToArray(),beforeSha,false,cycle);
                if(_io is BootProtocolSession eraseProtocol)eraseProtocol.FlushTraceBoundary();
            }
            var activeCycle=_ledger.CurrentEraseCycle(page);if(activeCycle==0)throw new InvalidDataException("No actual erase cycle for programming");
            if(officialPages)
            {
                if(FF(targetPage)||_ledger.PageProgrammed(page,page,targetPage))continue;IssuePageProgramOnce(page,targetPage,beforeSha,activeCycle,ct);
            }
            else for(var a=page;a<page+BootWriteWire.Page;a+=32)
            {
                var data=target.AsSpan(a-BootWriteWire.Start,32).ToArray();if(FF(data)||_ledger.PageProgrammed(page,a,data))continue;var seed=ReadAt(a,Erased(),ct);
                IssueOnce(0x51,page,a,seed,data,data,beforeSha,activeCycle,ct);_ledger.Append("ProgramReadback",page,a,"program",seed,data,beforeSha,false,activeCycle);
                if(_io is BootProtocolSession programProtocol)programProtocol.FlushTraceBoundary();
            }
        }
        if(_pageLimit==55)
        {
            var forward=ReadApp(ct);if(!forward.AsSpan().SequenceEqual(target))throw new InvalidDataException("Forward target readback mismatch");var reverse=ReadApp(ct);if(!reverse.AsSpan().SequenceEqual(target))throw new InvalidDataException("Reverse target readback mismatch");
            uint sum=0;for(var i=0;i<forward.Length-4;i+=4)sum=unchecked(sum+BinaryPrimitives.ReadUInt32LittleEndian(forward.AsSpan(i,4)));if(sum!=BinaryPrimitives.ReadUInt32LittleEndian(forward.AsSpan(forward.Length-4)))throw new InvalidDataException("Target checkword mismatch");
            var completionKind=restore?"RestoreVerified":"InstallVerified";if(_ledger.Records.LastOrDefault()?.Kind!=completionKind)_ledger.Append(completionKind,0,BootWriteWire.Start,restore?"restore":"install",before,target,beforeSha,false);
            if(_io is BootProtocolSession finalProtocol)finalProtocol.FlushTraceBoundary();
        }
    }
}

using System.Security.Cryptography;

namespace AppOnlyWriter;

/// <summary>A separate restore operation must name its completed known install history and immutable owned original.</summary>
public sealed record RestoreSource(DurableLedger InstallLedger,string OriginalBeforeImagePath,byte[] Candidate)
{
    internal RestoreSource Snapshot()=>new(InstallLedger,Path.GetFullPath(OriginalBeforeImagePath),Candidate.ToArray());
}
internal sealed record LedgerAdmission(byte[] ExpectedCurrent,OperationLineage Lineage,IReadOnlyDictionary<int,int> InheritedErases);

internal static class LedgerState
{
    private static string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] ReadPinned(string path,string sha)
    {if(new FileInfo(path).Length!=BootWriteWire.Length)throw new InvalidDataException("Immutable before-image length differs");var bytes=File.ReadAllBytes(path);if(bytes.Length!=BootWriteWire.Length||Sha(bytes)!=sha)throw new InvalidDataException("Immutable before-image pin differs");return bytes;}
    internal static LedgerAdmission Admit(DurableLedger ledger,byte[] target,bool restore,byte[]? requested,string beforePath,RestoreSource? source)
    {
        ledger.VerifyStoredSnapshot();
        if(ledger.Identity.RunId==Guid.Empty||string.IsNullOrWhiteSpace(ledger.Identity.Device)||ledger.Identity.OriginalSha256!=ImageAdmission.OriginalSha||ledger.Identity.TargetSha256!=Sha(target))throw new InvalidDataException("Exact device/original/target/run pins required");
        if(!restore&&Sha(target)!=ImageAdmission.OfflineCandidateSha||restore&&source is not null&&Sha(source.Candidate)!=ImageAdmission.OfflineCandidateSha)throw new InvalidDataException("Exact offline fixture target pin required; NO LIVE");
        var line=ledger.Lineage;byte[] baseline;var inherited=new Dictionary<int,int>();
        if(restore)
        {
            ImageAdmission.ValidateOriginal(target);
            if(source is null)throw new InvalidDataException("Restore requires explicit parent install ledger and immutable original before-image");
            var parent=source.InstallLedger;parent.VerifyStoredSnapshot();parent.AssertResolved();var pl=parent.Lineage??throw new InvalidDataException("Parent install lineage absent");
            if(pl.Schema!=1||pl.Mode!="install"||pl.Identity!=parent.Identity||parent.Identity.Device!=ledger.Identity.Device||parent.Identity.OriginalSha256!=ledger.Identity.OriginalSha256||parent.Identity.TargetSha256!=Sha(source.Candidate)||parent.Identity.RunId==ledger.Identity.RunId||parent.Identity.TargetSha256==ledger.Identity.TargetSha256)throw new InvalidDataException("Parent install identity/device/run/target differs");
            if(Path.GetFullPath(pl.OriginalBeforeImagePath)!=source.OriginalBeforeImagePath||pl.OriginalBeforeImageSha256!=ImageAdmission.OriginalSha||pl.BeforeImageSha256!=ImageAdmission.OriginalSha||Path.GetFullPath(pl.BeforeImagePath)!=source.OriginalBeforeImagePath)throw new InvalidDataException("Parent original before-image lineage differs");
            var original=ReadPinned(source.OriginalBeforeImagePath,ImageAdmission.OriginalSha);ImageAdmission.ValidateOriginal(original);
            baseline=Replay(parent,original,source.Candidate,pl.BeforeImageSha256);
            for(var page=BootWriteWire.Start;page<BootWriteWire.End;page+=BootWriteWire.Page)
                if(parent.CanReuseErasedPage(page)&&!baseline.AsSpan(page-BootWriteWire.Start,BootWriteWire.Page).ContainsAnyExcept((byte)0xFF))inherited.Add(page,parent.CurrentEraseCycle(page));
            var proposed=new OperationLineage(1,"restore",ledger.Identity,Path.GetFullPath(beforePath),Sha(baseline),source.OriginalBeforeImagePath,ImageAdmission.OriginalSha,parent.FilePath,Sha(File.ReadAllBytes(parent.FilePath)),parent.Identity,Sha(File.ReadAllBytes(parent.FilePath+".lineage.json")));
            if(line is null){if(ledger.Records.Count!=0)throw new InvalidDataException("Restore ledger missing lineage");line=proposed;}
            else if(line.Schema!=1||line.Mode!="restore"||line.Identity!=ledger.Identity||line.ParentLedgerPath!=proposed.ParentLedgerPath||line.ParentLedgerSha256!=proposed.ParentLedgerSha256||line.ParentIdentity!=proposed.ParentIdentity||line.ParentLineageSha256!=proposed.ParentLineageSha256||line.OriginalBeforeImagePath!=proposed.OriginalBeforeImagePath||line.OriginalBeforeImageSha256!=proposed.OriginalBeforeImageSha256||line.BeforeImageSha256!=proposed.BeforeImageSha256)throw new InvalidDataException("Bound parent install history changed");
            if(ledger.Lineage is not null&&!ReadPinned(line.BeforeImagePath,line.BeforeImageSha256).AsSpan().SequenceEqual(baseline))throw new InvalidDataException("Restore baseline differs from parent proven state");
        }
        else
        {
            if(source is not null)throw new InvalidDataException("Install cannot use restore parent");
            if(line is null)
            {
                if(ledger.Records.Count!=0||requested is null)throw new InvalidDataException("Fresh install requires exact owned original; historical lineage required for resume");
                ImageAdmission.ValidateOriginal(requested);baseline=requested;
                line=new(1,"install",ledger.Identity,Path.GetFullPath(beforePath),ImageAdmission.OriginalSha,Path.GetFullPath(beforePath),ImageAdmission.OriginalSha);
            }
            else
            {
                if(line.Schema!=1||line.Mode!="install"||line.Identity!=ledger.Identity||line.BeforeImageSha256!=ImageAdmission.OriginalSha||line.OriginalBeforeImageSha256!=ImageAdmission.OriginalSha||line.BeforeImagePath!=line.OriginalBeforeImagePath||line.ParentLedgerPath is not null||line.ParentIdentity is not null||line.ParentLedgerSha256 is not null||line.ParentLineageSha256 is not null)throw new InvalidDataException("Install lineage differs");
                baseline=ReadPinned(line.OriginalBeforeImagePath,ImageAdmission.OriginalSha);ImageAdmission.ValidateOriginal(baseline);
            }
        }
        var expected=Replay(ledger,baseline,target,line.BeforeImageSha256,inherited);
        if(requested is not null&&!requested.AsSpan().SequenceEqual(expected))throw new InvalidDataException("Requested preimage differs from proven ledger state");
        return new(expected,line,inherited);
    }

    // Exact legal whole-image reconstruction. Unresolved or incomplete destructive evidence never licenses a retry.
    private static byte[] Replay(DurableLedger ledger,byte[] original,byte[] target,string beforeSha,IReadOnlyDictionary<int,int>? inherited=null)
    {
        ledger.AssertResolved();var image=original.ToArray();var cycles=new Dictionary<int,int>();var programs=new Dictionary<int,int>();var verified=new HashSet<int>();LedgerRecord? pending=null,retired=null;
        var ff32=Enumerable.Repeat((byte)0xFF,32).ToArray();var ffPage=Enumerable.Repeat((byte)0xFF,4096).ToArray();
        foreach(var r in ledger.Records)
        {
            // A program Retired event already records exact distinct 32-byte content. Its redundant
            // ProgramReadback append can be absent after a process/file failure without licensing a retry.
            if(retired?.Kind=="ProgramIntent"&&r.Kind!="ProgramReadback")retired=null;
            if(r.BeforeImageSha256!=beforeSha)throw new InvalidDataException("Event before-image lineage differs");
            if(r.Kind is "InstallVerified" or "RestoreVerified")
            {if(pending is not null||retired is not null||!image.AsSpan().SequenceEqual(target)||r.ExpectedSha256!=Sha(target)||r.UnknownOutcome)throw new InvalidDataException("Invalid completion proof");continue;}
            if(r.Page<BootWriteWire.Start||r.Page>=BootWriteWire.End||(r.Page-BootWriteWire.Start)%4096!=0||r.Address<r.Page||r.Address>r.Page+4096-32||(r.Address&31)!=0)throw new InvalidDataException("Event outside exact APP page/block");
            var cycle=cycles.GetValueOrDefault(r.Page);var count=programs.GetValueOrDefault(r.Page);
            if(r.ProgramIntentsAfterErase!=count)throw new InvalidDataException("Program event count differs");
            switch(r.Kind)
            {
                case "ParentEraseEffectBound":
                    if(pending is not null||retired is not null||cycle!=0||r.Address!=r.Page||r.Operation!="parent-erase"||r.UnknownOutcome||inherited is null||!inherited.TryGetValue(r.Page,out var parentCycle)||r.EraseCycle!=parentCycle||r.SeedSha256!=Sha(ffPage)||r.ExpectedSha256!=Sha(ffPage)||!image.AsSpan(r.Page-BootWriteWire.Start,4096).SequenceEqual(ffPage))throw new InvalidDataException("Parent erase cycle/effect is not bound to exact parent proof");
                    cycles[r.Page]=parentCycle;verified.Add(r.Page);break;
                case "EraseIntent":
                    if(pending is not null||retired is not null||r.Address!=r.Page||r.Operation!="erase"||!r.UnknownOutcome||r.EraseCycle!=cycle+1||r.SeedSha256!=Sha(image.AsSpan(r.Page-BootWriteWire.Start,32).ToArray())||r.ExpectedSha256!=Sha(ff32))throw new InvalidDataException("Illegal erase intent/cycle/seed");
                    cycles[r.Page]=r.EraseCycle;verified.Remove(r.Page);pending=r;break;
                case "ProgramIntent":
                    var pageOperation=r.Operation=="program-page";var width=pageOperation?4096:32;
                    if(pending is not null||retired is not null||!verified.Contains(r.Page)||r.Operation is not("program" or"program-page")||pageOperation&&r.Address!=r.Page||!r.UnknownOutcome||r.EraseCycle!=cycle||r.SeedSha256!=Sha(ff32)||r.ExpectedSha256!=Sha(target.AsSpan(r.Address-BootWriteWire.Start,width).ToArray())||image.AsSpan(r.Address-BootWriteWire.Start,width).ContainsAnyExcept((byte)0xFF))throw new InvalidDataException("Illegal program intent/cycle/target");
                    programs[r.Page]=count+1;pending=r;break;
                case "Retired":
                    if(pending is null||r.UnknownOutcome||r.Page!=pending.Page||r.Address!=pending.Address||r.EraseCycle!=pending.EraseCycle||r.Operation!=pending.Operation||r.SeedSha256!=pending.SeedSha256||r.ExpectedSha256!=pending.ExpectedSha256)throw new InvalidDataException("Destructive retirement does not match intent");
                    retired=pending;pending=null;if(retired.Kind=="ProgramIntent"){var retiredWidth=retired.Operation=="program-page"?4096:32;target.AsSpan(r.Address-BootWriteWire.Start,retiredWidth).CopyTo(image.AsSpan(r.Address-BootWriteWire.Start,retiredWidth));}break;
                case "EraseEffectVerified":
                    if(retired?.Kind!="EraseIntent"||r.Page!=retired.Page||r.Address!=r.Page||r.Operation!="erase"||r.EraseCycle!=cycle||r.UnknownOutcome||r.SeedSha256!=retired.SeedSha256||r.ExpectedSha256!=Sha(ffPage))throw new InvalidDataException("Erase page effect not proven");
                    ffPage.CopyTo(image,r.Page-BootWriteWire.Start);verified.Add(r.Page);retired=null;break;
                case "ProgramReadback":
                    if(retired?.Kind!="ProgramIntent"||r.Page!=retired.Page||r.Address!=retired.Address||r.Operation is not("program" or"program-page")||r.EraseCycle!=cycle||r.UnknownOutcome||r.SeedSha256!=retired.SeedSha256||r.ExpectedSha256!=retired.ExpectedSha256)throw new InvalidDataException("Program effect not proven");
                    var readbackWidth=r.Operation=="program-page"?4096:32;target.AsSpan(r.Address-BootWriteWire.Start,readbackWidth).CopyTo(image.AsSpan(r.Address-BootWriteWire.Start,readbackWidth));retired=null;break;
                default:throw new InvalidDataException("Unrecognized ledger event");
            }
        }
        if(pending is not null||retired?.Kind=="EraseIntent")throw new InvalidDataException("Incomplete destructive proof is terminal; independent reviewed recovery required");
        return image;
    }
}

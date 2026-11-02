namespace AppOnlyWriter;

public sealed class FakeBoot:IBootProtocol,IBootPageProtocol
{
    public byte[] Flash;public byte[] Version=[2,0,2,0];public int Address=BootWriteWire.Start;public byte Status,Error;
    public readonly List<string> Trace=[];public bool DropNextDestructiveOut,ShortNextOut,TimeoutNextResult,TimeoutNextDestructiveResult,FailNextQuery,SkipNextErase,DifferentDevice;
    public Action? BeforeSetAddress;public Action<int>? BeforeSetAddressValue;
    public int EraseRequests,ProgramRequests,DestructiveRetries,DestructiveDelayReads;private byte[] _result=new byte[32],_staging=new byte[32],_pageStaging=new byte[BootWriteWire.Page];private int _lastErase=-1,_lastProgram=-1,_delay,_length=32;private byte _pending;private bool _pageMode;
    public FakeBoot(byte[] initial){if(initial.Length!=BootWriteWire.Length)throw new InvalidDataException("Fake app size");Flash=initial.ToArray();}
    public byte[] QueryVersion(CancellationToken ct){ct.ThrowIfCancellationRequested();if(FailNextQuery){FailNextQuery=false;throw new IOException("fake query error");}Trace.Add("82");return DifferentDevice?[2,0,3,0]:Version;}
    public byte[] QueryStatus(CancellationToken ct){ct.ThrowIfCancellationRequested();Trace.Add("8F");return [(byte)(Status|(_pending!=0?1:0)),Error];}
    public byte[] QueryResult(CancellationToken ct){ct.ThrowIfCancellationRequested();if(_length!=32)throw new InvalidDataException("AA32 read view required");if(TimeoutNextResult){TimeoutNextResult=false;throw new TimeoutException("fake AA timeout");}Trace.Add("AA");return _result.ToArray();}
    public void SetAddress(int address,CancellationToken ct){BeforeSetAddress?.Invoke();BeforeSetAddressValue?.Invoke(address);ct.ThrowIfCancellationRequested();Address=address;Trace.Add($"20:{address:X}");}
    public void SetLength32(CancellationToken ct){ct.ThrowIfCancellationRequested();_pageMode=false;_length=32;Trace.Add("21:32");}
    public void SetLength4096(CancellationToken ct){ct.ThrowIfCancellationRequested();_pageMode=true;_length=4096;Array.Fill(_pageStaging,(byte)0xFF);Trace.Add("21:4096");}
    public void Marker(CancellationToken ct){ct.ThrowIfCancellationRequested();Status|=0x80;Trace.Add("10");}
    public void Stage(ReadOnlySpan<byte> data,CancellationToken ct){ct.ThrowIfCancellationRequested();data.CopyTo(_staging);Trace.Add("22");}
    public void StagePageChunk(int offset,ReadOnlySpan<byte> data,CancellationToken ct){ct.ThrowIfCancellationRequested();if(!_pageMode||offset<0||offset+data.Length>BootWriteWire.Page)throw new InvalidDataException("fake page staging state/range");data.CopyTo(_pageStaging.AsSpan(offset));Trace.Add($"22:{offset}/{data.Length}");}
    public void Request(byte action,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();Trace.Add($"1F:{action:X2}");
        if(action==5){if(_pending!=0&&--_delay<=0){var pending=_pending;_pending=0;Execute(pending);}_result=Flash.AsSpan(Address-BootWriteWire.Start,32).ToArray();return;}
        if(_pending!=0)throw new InvalidOperationException("fake changed destructive parameters while unknown");
        if(action==1&&_lastErase==Address||action==0x51&&_lastProgram==Address){DestructiveRetries++;throw new InvalidOperationException("fake destructive retry");}
        if(DropNextDestructiveOut){DropNextDestructiveOut=false;throw new IOException("unknown destructive OUT");}
        if(ShortNextOut){ShortNextOut=false;throw new IOException("short destructive OUT");}
        if(DestructiveDelayReads>0){_pending=action;_delay=DestructiveDelayReads;DestructiveDelayReads=0;return;}
        Execute(action);
    }
    private void Execute(byte action)
    {
        if(action==1)_lastErase=Address;else _lastProgram=Address;
        if(action==1){EraseRequests++;if(!SkipNextErase)Array.Fill(Flash,(byte)0xFF,Address-BootWriteWire.Start,BootWriteWire.Page);SkipNextErase=false;}
        else if(action==0x51&&_pageMode){ProgramRequests++;if(Flash.AsSpan(Address-BootWriteWire.Start,BootWriteWire.Page).ContainsAnyExcept((byte)0xFF))throw new InvalidDataException("page program before erase");_pageStaging.CopyTo(Flash,Address-BootWriteWire.Start);_pageMode=false;}
        else if(action==0x51){ProgramRequests++;for(var i=0;i<32;i+=4){if(!Flash.AsSpan(Address-BootWriteWire.Start+i,4).SequenceEqual(new byte[]{0xFF,0xFF,0xFF,0xFF}))throw new InvalidDataException("program before erase");_staging.AsSpan(i,4).CopyTo(Flash.AsSpan(Address-BootWriteWire.Start+i,4));}}
        else throw new InvalidDataException("action");
        if(TimeoutNextDestructiveResult){TimeoutNextDestructiveResult=false;TimeoutNextResult=true;}
    }
    public void Dispose()=>Trace.Add("DISPOSE");
}

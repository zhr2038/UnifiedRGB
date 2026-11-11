using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly:DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
namespace BootReadOnly;

public readonly record struct Completion(bool Done,int Bytes,int Error);
public interface IPendingIo:IDisposable
{
    int Start();Completion Poll();void Wait(int milliseconds);int Cancel();byte[] Data(int bytes);
    void RetainUntilExit();
}
public static class NativeCompletion
{
    public static IoResult Execute(IPendingIo op,int timeoutMs,CancellationToken ct)
    {
        if(timeoutMs is <1 or >3000)throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        ct.ThrowIfCancellationRequested();int start=op.Start();
        if(start is not(0 or 997))throw new Win32Exception(start,"Native I/O start failed");
        var sw=Stopwatch.StartNew();bool stopped=false;IoKind reason=IoKind.TimedOut;
        while(true)
        {
            var c=op.Poll();
            if(c.Done)
            {if(c.Error!=0)throw new Win32Exception(c.Error,"Native completed I/O error");return new(IoKind.Completed,c.Bytes,op.Data(c.Bytes),0,true);}
            if(c.Error!=996)throw new Win32Exception(c.Error,"GetOverlappedResult failed");
            if(ct.IsCancellationRequested||sw.ElapsedMilliseconds>=timeoutMs)
            {reason=ct.IsCancellationRequested?IoKind.Canceled:IoKind.TimedOut;stopped=true;break;}
            op.Wait((int)Math.Min(20,Math.Max(1,timeoutMs-sw.ElapsedMilliseconds)));
        }
        if(!stopped)throw new InvalidOperationException();
        int cancelError=op.Cancel();var join=Stopwatch.StartNew();
        while(join.ElapsedMilliseconds<1000)
        {
            var c=op.Poll();
            if(c.Done)
            {
                // Completion won the cancellation race: bytes are real, never discard an unsolicited packet.
                if(c.Error==0)return new(reason,c.Bytes,op.Data(c.Bytes),0,true);
                if(c.Error==995)return new(reason,0,[],995,true);
                throw new Win32Exception(c.Error,"Canceled I/O completed with an error");
            }
            if(c.Error!=996)throw new Win32Exception(c.Error,"Cancellation completion query failed");
            op.Wait(10);
        }
        // Windows forbids freeing an OVERLAPPED/buffer while cancellation is pending.
        // Intentionally retain these allocations until process exit and stop the session.
        op.RetainUntilExit();
        throw new IOException($"Cancellation not joined within1000ms (CancelIoEx error{cancelError}); native memory retained; session must stop");
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]internal struct Overlapped{public UIntPtr Internal,InternalHigh;public uint Offset,OffsetHigh;public IntPtr Event;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool ReadFile(SafeFileHandle h,IntPtr b,uint n,out uint got,IntPtr o);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool WriteFile(SafeFileHandle h,IntPtr b,uint n,out uint got,IntPtr o);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetOverlappedResult(SafeFileHandle h,IntPtr o,out uint got,bool wait);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool CancelIoEx(SafeFileHandle h,IntPtr o);
}

internal sealed class NativeOperation:IPendingIo
{
    static readonly List<NativeOperation> retained=[];
    readonly SafeFileHandle handle;readonly bool write;readonly EventWaitHandle signal=new(false,EventResetMode.ManualReset);
    IntPtr buffer,overlapped;bool settled,started,retainedFlag,disposed;
    public NativeOperation(SafeFileHandle handle,byte[]? report)
    {
        this.handle=handle;write=report!=null;buffer=Marshal.AllocHGlobal(65);Marshal.Copy(report??new byte[65],0,buffer,65);
        overlapped=Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
        Marshal.StructureToPtr(new Native.Overlapped{Event=signal.SafeWaitHandle.DangerousGetHandle()},overlapped,false);
    }
    public int Start()
    {
        if(started)throw new InvalidOperationException();started=true;
        bool ok=write?Native.WriteFile(handle,buffer,65,out _,overlapped):Native.ReadFile(handle,buffer,65,out _,overlapped);
        int e=ok?0:Marshal.GetLastPInvokeError();if(e is not(0 or 997))settled=true;return e;
    }
    public Completion Poll()
    {
        bool ok=Native.GetOverlappedResult(handle,overlapped,out uint bytes,false);
        int error=ok?0:Marshal.GetLastPInvokeError();bool done=ok||error==995;
        if(done)settled=true;
        if(ok&&bytes>65)throw new IOException("Native transfer count exceeds buffer65");
        return new(done,ok?(int)bytes:0,error);
    }
    public byte[] Data(int n)
    {if(!settled||n is <0 or >65)throw new InvalidOperationException();var data=new byte[n];Marshal.Copy(buffer,data,0,n);return data;}
    public void Wait(int ms)=>signal.WaitOne(ms);
    public int Cancel()=>Native.CancelIoEx(handle,overlapped)?0:Marshal.GetLastPInvokeError();
    public void RetainUntilExit(){if(retainedFlag)return;retainedFlag=true;lock(retained)retained.Add(this);}
    public void Dispose()
    {
        if(disposed||retainedFlag)return;disposed=true;
        if(started&&!settled){_=Cancel();RetainUntilExit();return;}
        Marshal.FreeHGlobal(buffer);Marshal.FreeHGlobal(overlapped);buffer=overlapped=IntPtr.Zero;signal.Dispose();
    }
}

internal delegate void ReportValidator(ReadOnlySpan<byte> report);

internal sealed class NativeReportIo:IReportIo
{
    readonly SafeFileHandle handle;readonly ReportValidator validateWrite;
    readonly Func<SafeFileHandle,byte[]?,IPendingIo> createOperation;int busy;bool poisoned,disposed;
    internal NativeReportIo(SafeFileHandle handle):this(handle,Wire.Validate){}
    internal NativeReportIo(SafeFileHandle handle,ReportValidator validateWrite)
        :this(handle,validateWrite,static(h,report)=>new NativeOperation(h,report)){}
    // Internal offline seam: validation and NativeCompletion still run before/around the fake operation.
    internal NativeReportIo(SafeFileHandle handle,Func<SafeFileHandle,byte[]?,IPendingIo> createOperation)
        :this(handle,Wire.Validate,createOperation){}
    internal NativeReportIo(SafeFileHandle handle,ReportValidator validateWrite,Func<SafeFileHandle,byte[]?,IPendingIo> createOperation)
    {
        ArgumentNullException.ThrowIfNull(handle);ArgumentNullException.ThrowIfNull(validateWrite);ArgumentNullException.ThrowIfNull(createOperation);
        this.handle=handle;this.validateWrite=validateWrite;this.createOperation=createOperation;
    }
    IoResult Transfer(byte[]? report,int timeout,CancellationToken ct)
    {
        if(disposed||poisoned)throw new InvalidOperationException("Native channel closed/poisoned");
        if(Interlocked.CompareExchange(ref busy,1,0)!=0)throw new InvalidOperationException("Only one native operation at a time");
        try
        {using var op=createOperation(handle,report);var r=NativeCompletion.Execute(op,timeout,ct);if(r.Kind==IoKind.Canceled)poisoned=true;return r;}
        catch{poisoned=true;throw;}
        finally{Volatile.Write(ref busy,0);}
    }
    public IoResult Write(byte[] report,int timeout,CancellationToken ct){validateWrite(report);return Transfer(report,timeout,ct);}
    public IoResult Read(int timeout,CancellationToken ct)=>Transfer(null,timeout,ct);
    public void Dispose(){if(disposed)return;disposed=true;handle.Dispose();}
}

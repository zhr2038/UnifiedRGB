namespace AppOnlyWriter;

internal static class WriterBudget
{
    internal const int TraceRecords=1000000,TraceRecordBytes=512,LedgerRecordBytes=768;
    internal const long TraceBytes=256L*1024*1024,LedgerBytes=16L*1024*1024;
    internal const int Commands=2000000,NormalFullTraceRecords=557975,NormalFullCommands=183607,NormalFullLedgerRecords=21286;
    // Reserve from BEFORE Marker through a replayable ledger boundary. Erase includes all128 whole-page fences.
    internal static (int Commands,int TraceRecords,int LedgerRecords) Destructive(bool program,int polls)
    {
        if(polls is<1 or>10000)throw new ArgumentOutOfRangeException(nameof(polls));
        return program?(6+2*polls,18+7*polls,3):(5+2*polls+128*(1+2*polls),16+7*polls+128*(2+7*polls),3);
    }
    internal static (int Commands,int TraceRecords,int LedgerRecords) PageDestructive(bool program,int polls)
    {
        if(polls is<1 or>10000)throw new ArgumentOutOfRangeException(nameof(polls));
        var completionCommands=polls+1;var completionTrace=5*(polls+1);var pageReadCommands=128*(1+2*polls)+(program?1:0);var pageReadTrace=128*(2+7*polls)+(program?2:0);
        return program?(73+completionCommands+pageReadCommands,146+completionTrace+pageReadTrace,3):(3+completionCommands+pageReadCommands,6+completionTrace+pageReadTrace,3);
    }
}

namespace UnifiedRgb.Setup;

/// <summary>Hardware classes a first-run setup can offer. Screen families are separate because each has its own output gate.</summary>
public enum SetupFamily
{
    LianLi,
    MsiGpu,
    AsusAura,
    EneDram,
    ScreenTlLcd,
    ScreenH2,
    ScreenTurzx,
}

/// <summary>What read-only inspection established for one family on THIS computer.</summary>
public enum SetupSupport
{
    /// <summary>No supported identity was seen.</summary>
    NotPresent,

    /// <summary>A supported identity was seen and its packaged prerequisites passed their pins.</summary>
    Supported,

    /// <summary>A supported identity was seen, but a required resource is missing, untrusted or does not match its pin.</summary>
    PrerequisiteMissing,

    /// <summary>Something related was seen but it is not a supported/validated identity (or is ambiguous).</summary>
    Unsupported,

    /// <summary>Inspection could not decide read-only (e.g. ENE DRAM presence needs SMBus probing, which setup never does).</summary>
    Unknown,
}

public enum SetupSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SetupFinding(SetupSeverity Severity, string Code, string Message);

/// <summary>Where the installed Host lives; packaged native resources are resolved relative to it, never from user paths.</summary>
/// <param name="InstallDirectory">Absolute Host base directory (the directory of UnifiedRgb.Host.exe).</param>
/// <param name="CodecRelativePath">Packaged Lian Li codec, relative to <paramref name="InstallDirectory"/>.</param>
/// <param name="PawnIoModuleRelativePath">Packaged official PawnIO SMBus module, relative to <paramref name="InstallDirectory"/>.</param>
public sealed record SetupEnvironment(
    string InstallDirectory,
    string CodecRelativePath = SetupEnvironment.DefaultCodecRelativePath,
    string PawnIoModuleRelativePath = SetupEnvironment.DefaultPawnIoModuleRelativePath)
{
    public const string DefaultCodecRelativePath = @"Resources\LianLi\yuz.dll";
    public const string DefaultPawnIoModuleRelativePath = @"Resources\PawnIO\SmbusPIIX4.bin";
}

public enum SetupResourceState
{
    Verified,
    Missing,
    HashMismatch,
    UntrustedLocation,
    InvalidPath,
    ApiMismatch,
    Unreadable,
}

/// <summary>A pinned file prerequisite. <c>Verified</c> means location trust + SHA-256 pin (+ API version where stated) only.</summary>
public sealed record SetupResourceReport(
    string Name,
    string? FullPath,
    SetupResourceState State,
    string? Sha256,
    string ExpectedSha256,
    string? Version,
    string Detail);

/// <summary>One proposed <c>LianLi:Devices</c> entry, derived only from this computer's receiver report.</summary>
public sealed record LianLiProposal(
    string Mac,
    string Family,
    int RecType,
    int DevType,
    int FanNum,
    int Leds,
    int Sender,
    int Channel,
    int Rx,
    string Name);

/// <summary>A receiver record that is NOT proposed, with the reason (never silently dropped).</summary>
public sealed record LianLiRejection(string Mac, string Code, string Message);

public enum LianLiPairingState
{
    Ready,
    NoReceiver,
    MultipleReceivers,
    UnverifiedReceiver,
    NoSender,
    MultipleSenders,
    QueryFailed,
    ReportInvalid,
    AmbiguousMaster,
    AmbiguousChannel,
    NoPairedDevices,
}

/// <summary>
/// Result of the query-only pairing analysis. <see cref="MasterMac"/> is the single master all proposed devices report; it is
/// NOT confirmed against the sender (that needs the sender's USB 17 query, which setup does not send).
/// </summary>
public sealed record LianLiPairingResult(
    LianLiPairingState State,
    string? MasterMac,
    int? Channel,
    IReadOnlyList<LianLiProposal> Proposals,
    IReadOnlyList<LianLiRejection> Rejections,
    string Detail);

public sealed record SetupFamilyReport(
    SetupFamily Family,
    SetupSupport Support,
    IReadOnlyList<string> Identities,
    bool CanEnable,
    bool CanAuthorizeOutput,
    string Detail);

public sealed record SetupReport(
    DateTimeOffset InspectedAt,
    SetupEnvironment Environment,
    IReadOnlyList<SetupFamilyReport> Families,
    LianLiPairingResult LianLi,
    IReadOnlyList<SetupResourceReport> Resources,
    IReadOnlyList<SetupFinding> Findings)
{
    public SetupFamilyReport Family(SetupFamily family) => Families.First(f => f.Family == family);

    public SetupResourceReport? Resource(string name) => Resources.FirstOrDefault(r => r.Name == name);
}

public enum SetupMode
{
    /// <summary>Virtual demo adapter only; every hardware class off. Works with no hardware or optional resources.</summary>
    Demo,

    /// <summary>Real hardware classes according to <see cref="SetupSelection.Families"/>.</summary>
    Hardware,
}

/// <summary><paramref name="Enable"/> = discovery/bring-up only; <paramref name="AuthorizeOutput"/> = explicit consent to real writes.</summary>
public sealed record SetupFamilySelection(SetupFamily Family, bool Enable, bool AuthorizeOutput);

/// <param name="AcceptedLianLiMacs">Null keeps the configured Lian Li device list unchanged; a list replaces it with exactly
/// those proposals of the report.</param>
public sealed record SetupSelection(SetupMode Mode, IReadOnlyList<SetupFamilySelection> Families, IReadOnlyList<string>? AcceptedLianLiMacs = null);

public enum SetupPlaybackState
{
    Off,

    /// <summary>Class enabled for discovery/bring-up; no output was authorized.</summary>
    DiscoveryOnly,

    /// <summary>Output authorized and permitted by the existing gates; physical playback is UNVERIFIED until optical confirmation.</summary>
    OutputUnverified,

    /// <summary>Output was authorized, but an existing verification gate still blocks the writer (setup never sets it).</summary>
    BlockedByVerificationGate,

    /// <summary>The existing configuration already carried this class's ControlVerified gate; setup preserved, not re-verified, it.</summary>
    PreviouslyVerified,
}

public sealed record SetupFamilyOutcome(SetupFamily Family, bool Enabled, bool OutputAuthorized, SetupPlaybackState Playback, string Detail);

/// <summary>One JSON value the plan changes, as JSON text (<c>null</c> = absent).</summary>
public sealed record SetupConfigChange(string Path, string? Before, string? After);

/// <summary>A runtime gate the plan changes; listed separately so the UI shows it explicitly.</summary>
public sealed record SetupGateChange(string Path, bool Before, bool After, string Reason);

public sealed record SetupPlan(
    bool CanSave,
    bool HasChanges,
    bool BaseExists,
    string BaseSha256,
    string ProposedJson,
    string ProposedSha256,
    IReadOnlyList<SetupConfigChange> Changes,
    IReadOnlyList<SetupGateChange> GateChanges,
    IReadOnlyList<SetupFamilyOutcome> Outcomes,
    IReadOnlyList<SetupFinding> Findings);

public enum SetupSaveStatus
{
    Saved,
    NoChanges,

    /// <summary>The file changed since inspection; nothing was written (or the replaced content was restored).</summary>
    Stale,

    /// <summary>The plan is not savable or does not belong to the expected file; nothing was written.</summary>
    Refused,

    /// <summary>Publication failed after the swap and the backup was restored.</summary>
    RolledBack,

    /// <summary>Publication failed; see detail (the original file is intact unless stated).</summary>
    Failed,
}

public sealed record SetupSaveResult(SetupSaveStatus Status, string? BackupPath, string? WrittenSha256, string Detail);

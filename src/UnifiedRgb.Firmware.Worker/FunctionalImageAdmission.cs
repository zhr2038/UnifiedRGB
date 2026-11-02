using UnifiedRgb.Firmware;

// Deliberately separate adapter. The old LogoAssetPageWriter ImageAdmission/RootApproval are NOT compiled here or edited.
namespace AppOnlyWriter;

public static class ImageAdmission
{
    private static ReleasePackage? _release;
    public static string OriginalSha => Context.Release.Rollback.Sha256;
    public static string OfflineCandidateSha => Context.Release.Candidate.Sha256;
    private static ReleasePackage Context => _release ?? throw new InvalidOperationException("No immutable functional-image admission");
    public static void AdmitOnce(ReleasePackage release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (Interlocked.CompareExchange(ref _release, release, null) is not null) throw new InvalidOperationException("One release per worker process; no context replacement");
    }
    public static void ValidateOriginal(byte[] original)
    {
        FirmwareFiles.CheckImage(original);
        if (FirmwareFiles.Sha(original) != OriginalSha) throw new InvalidDataException("Exact reviewed functional release rollback required");
    }
}

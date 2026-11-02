namespace UnifiedRgb.Firmware;

public sealed record RetainedBaselineReview(string Label, ReleaseFile Image, ReleaseFile Receipt);
public sealed record RetainedBaselineReceipt(int Schema, string Classification, string ImageSha256,
    string NormalModel, string NormalFirmwareHeader, string ContainerId, string PhysicalLocation,
    DateTimeOffset OwnerConfirmedAtUtc, string EvidenceNote);
public sealed record RetainedBaselineSelection(string ImagePath, string ImageSha256, string ReceiptPath,
    string ReceiptSha256, string Label);

/// <summary>Installed review + explicit owner choice. This is NOT an actual readback receipt, old
/// physical pin or operation approval. Current boot entry and R6 preerase reads remain mandatory.</summary>
public static class RetainedBaselinePolicy
{
    public const string Origin = "OwnerConfirmedExistingReceipt";
    public const string ActualReadOrigin = "ActualTwoPassRead";
    public static (RetainedBaselineSelection Selection, RetainedBaselineReceipt Receipt) LoadInstalled(
        FirmwareInstalledOptions options, CatalogEntry catalog, ReleasePackage package)
    {
        var review = catalog.RetainedBaseline ?? throw new InvalidDataException("本机未安装此版本的可信保留基线记录");
        FirmwareFiles.Relative(review.Image.Path); FirmwareFiles.Relative(review.Receipt.Path);
        if (review.Label.Length is < 1 or > 160 || review.Image.Sha256 != package.Release.Rollback.Sha256)
            throw new InvalidDataException("Retained baseline differs from reviewed release rollback");
        var imagePath = Path.Combine(options.PrivateDirectory, review.Image.Path);
        var receiptPath = Path.Combine(options.PrivateDirectory, review.Receipt.Path);
        var selection = new RetainedBaselineSelection(imagePath, review.Image.Sha256, receiptPath, review.Receipt.Sha256, review.Label);
        return (selection, Validate(selection, catalog, package));
    }
    public static RetainedBaselineReceipt Validate(RetainedBaselineSelection selection, CatalogEntry catalog, ReleasePackage package)
    {
        var reviewed = catalog.RetainedBaseline ?? throw new InvalidDataException("No independently installed retained baseline review");
        if (selection.ImageSha256 != reviewed.Image.Sha256 || selection.ReceiptSha256 != reviewed.Receipt.Sha256 ||
            selection.Label != reviewed.Label || selection.ImageSha256 != package.Release.Rollback.Sha256)
            throw new InvalidDataException("Retained baseline selected outside installed review");
        var image = FirmwareFiles.ReadBounded(selection.ImagePath, FirmwareFiles.ImageLength);
        FirmwareFiles.CheckImage(image);
        var raw = FirmwareFiles.ReadBounded(selection.ReceiptPath, 32768);
        if (FirmwareFiles.Sha(image) != selection.ImageSha256 || FirmwareFiles.Sha(raw) != selection.ReceiptSha256)
            throw new InvalidDataException("Retained reference/receipt hash mismatch");
        var receipt = FirmwareFiles.Parse<RetainedBaselineReceipt>(raw);
        if (receipt.Schema != 1 || receipt.Classification != "UNIFIEDRGB_OWNER_CONFIRMED_RETAINED_BASELINE_V1" ||
            receipt.ImageSha256 != selection.ImageSha256 || receipt.NormalModel != "1C24" || receipt.NormalFirmwareHeader != "001200000025000100" ||
            !Guid.TryParse(receipt.ContainerId, out var container) || container == Guid.Empty ||
            !receipt.PhysicalLocation.Contains("#USB(", StringComparison.Ordinal) || receipt.OwnerConfirmedAtUtc > DateTimeOffset.UtcNow ||
            receipt.EvidenceNote.Length is < 1 or > 4096)
            throw new InvalidDataException("Invalid owner-confirmed retained reference receipt (not a fresh read)");
        return receipt;
    }
}

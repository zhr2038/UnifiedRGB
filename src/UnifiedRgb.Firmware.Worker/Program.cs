using AppOnlyWriter;
using BootReadOnly;
using UnifiedRgb.Firmware;
using UnifiedRgb.Firmware.Worker;
using System.Text.Json;

if (args.Length != 2 || args[0] != "--firmware-job") return 64;
// No CLI image/runner/pin overrides. Only Host-owned private request files and installed catalog.
var options = InstalledWorkerConfiguration.Read(AppContext.BaseDirectory);
if (!options.HardwareEnabled || options.CatalogPath is null) return 77;
FirmwareFiles.NoReparse(args[1]);
if (new FileInfo(args[1]).Length > 32768) return 65;
var request = FirmwareFiles.Parse<FirmwareWorkerRequest>(File.ReadAllBytes(args[1]));
if (Path.GetFullPath(Path.GetDirectoryName(args[1])!) != Path.GetFullPath(request.Directory)) return 65;
var package = ReleasePackage.Load(request.BundlePath);
var review = new InstalledTrust(options).Review(package) ?? throw new InvalidDataException("No installed reviewed release entry");
if (Path.GetFullPath(new InstalledTrust(options).ValidateWorker(review.Worker)) != Path.GetFullPath(Environment.ProcessPath!)) return 65;
ImageAdmission.AdmitOnce(package);
var ordinal = 0;
using var trace = new TraceFile(request.Directory, WriterBudget.TraceRecords, WriterBudget.TraceBytes, WriterBudget.TraceRecordBytes, true);
IBootProtocol Open(Pin pin, Func<bool> shouldCancel)
{
    var selected = Inventory.Select(Inventory.Enumerate(), pin);
    var native = Inventory.OpenExclusive(selected, BootWriteWire.Validate);
    try { Inventory.RevalidatePhysicalWithoutHidOpen(selected, pin); return new BootProtocolSession(new SafePreEraseReportIo(native, shouldCancel), trace); }
    catch { native.Dispose(); throw; }
}
var result = FunctionalTransaction.Run(package, request, _ => throw new InvalidOperationException("Native cancellation factory required"), receipt =>
{
    var path = Path.Combine(request.Directory, $"progress-{ordinal++:D4}.json");
    FirmwareFiles.CreateNew(path + ".pending", JsonSerializer.SerializeToUtf8Bytes(receipt));
    File.Move(path + ".pending", path);
}, () => File.Exists(Path.Combine(request.Directory, "cancel-request")), review, Open);
FirmwareFiles.CreateNew(Path.Combine(request.Directory, "result.json"), JsonSerializer.SerializeToUtf8Bytes(result));
return result.Phase is UnifiedRgb.Contracts.FirmwarePhase.Failed or UnifiedRgb.Contracts.FirmwarePhase.NeedsReviewedRecovery ? 1 : 0;

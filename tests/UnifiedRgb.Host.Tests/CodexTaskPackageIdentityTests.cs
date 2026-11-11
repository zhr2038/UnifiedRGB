using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed class CodexTaskPackageIdentityTests
{
    private const string Legacy = @"C:\Users\example\AppData\Local\OpenAI\Codex\bin";
    private const string Package = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1029.9216.0_x64__2p2nqsd0c76g0";

    [Theory]
    [InlineData(@"C:\Users\example\AppData\Local\OpenAI\Codex\bin\codex.exe")]
    [InlineData(@"C:\Users\example\AppData\Local\OpenAI\Codex\bin\release\codex.exe")]
    public void Legacy_install_remains_eligible_without_package_identity(string image) =>
        CodexTaskOwnerSource.IsAllowedImage(image, Legacy, null, null).Should().BeTrue();

    [Theory]
    [InlineData(Package)]
    [InlineData(@"D:\WindowsApps\OpenAI.Codex_27.1.0.0_x64__2p2nqsd0c76g0")]
    public void Package_location_from_Windows_is_used_without_pinning_a_version_or_drive(string root) =>
        CodexTaskOwnerSource.IsAllowedImage(Path.Combine(root, "app", "resources", "codex.exe"),
            Legacy, CodexTaskOwnerSource.PackageFamily, root).Should().BeTrue();

    [Theory]
    [InlineData(@"C:\Users\example\AppData\Local\OpenAI\Codex\bin-other\codex.exe", null, null)]
    [InlineData(Package + @"\app\resources\codex.exe", null, Package)]
    [InlineData(Package + @"\app\resources\codex.exe", "Other.Codex_2p2nqsd0c76g0", Package)]
    [InlineData(Package + @"\app\resources-other\codex.exe", CodexTaskOwnerSource.PackageFamily, Package)]
    [InlineData(Package + @"\app\resources\helper.exe", CodexTaskOwnerSource.PackageFamily, Package)]
    [InlineData(@"C:\Temp\codex.exe", CodexTaskOwnerSource.PackageFamily, Package)]
    public void Unrelated_or_lookalike_process_paths_are_rejected(string image, string? family, string? root) =>
        CodexTaskOwnerSource.IsAllowedImage(image, Legacy, family, root).Should().BeFalse();
}

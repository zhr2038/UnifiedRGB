using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    [Fact]
    public void Concurrent_native_preference_snapshots_publish_whole_records_without_sharing_a_temporary_writer()
    {
        using var service = Make(Pinned(), new Factory());
        var type = typeof(KeyboardDisplayService);
        var recordType = type.GetNestedType("NativeDisplayPreference", BindingFlags.NonPublic)!;
        var save = type.GetMethod("SaveNativePreference", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        Parallel.For(0, 128, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                var record = Activator.CreateInstance(recordType, 1, Key, "001200000024000100", (byte)6,
                    (byte)(1 + i % 100), "fresh-native-while-disabled");
                save.Invoke(service, [record]);
            }
            catch (TargetInvocationException ex) { failures.Enqueue(ex.InnerException!); }
        });

        failures.Should().BeEmpty("the owner and lifecycle can observe native brightness concurrently");
        var path = Path.Combine(_directory, "peripherals", "keyboard-native-display.json");
        using var persisted = JsonDocument.Parse(File.ReadAllBytes(path));
        var record = persisted.RootElement;
        record.GetProperty("Schema").GetInt32().Should().Be(1);
        record.GetProperty("DeviceKey").GetString().Should().Be(Key);
        record.GetProperty("ProfileToken").GetByte().Should().Be(6);
        record.GetProperty("Brightness").GetByte().Should().BeInRange(1, 100);
        record.GetProperty("Origin").GetString().Should().Be("fresh-native-while-disabled");
        File.Exists(path + ".tmp").Should().BeFalse();
        File.Exists(Path.Combine(_directory, "peripherals", "keyboard-display-session.json")).Should().BeFalse();
    }
}

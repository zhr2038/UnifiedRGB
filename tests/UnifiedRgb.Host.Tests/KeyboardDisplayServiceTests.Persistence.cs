using System.Reflection;
using System.Text.Json;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

public sealed partial class KeyboardDisplayServiceTests
{
    // The subject publishes by atomic rename. A read-only observer must allow that rename and keep its old file snapshot,
    // rather than holding File.ReadAllBytes' delete-denying handle and turning a valid observation into a writer I/O fault.
    private static FileStream OpenPreferenceSnapshot(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static JsonDocument ReadPreferenceSnapshot(string path)
    {
        using var stream = OpenPreferenceSnapshot(path);
        return JsonDocument.Parse(stream);
    }

    [Fact]
    public void A_read_only_preference_observer_does_not_block_atomic_publishing()
    {
        using var service = Make(Pinned(), new Factory());
        var type = typeof(KeyboardDisplayService);
        var recordType = type.GetNestedType("NativeDisplayPreference", BindingFlags.NonPublic)!;
        var save = type.GetMethod("SaveNativePreference", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Save(byte brightness) => save.Invoke(service,
            [Activator.CreateInstance(recordType, 1, Key, "001200000024000100", (byte)6, brightness, "fresh-native-while-disabled")]);
        Save(10);
        var path = Path.Combine(_directory, "peripherals", "keyboard-native-display.json");
        using var observer = OpenPreferenceSnapshot(path);
        ((Action)(() => Save(20))).Should().NotThrow("a read-only test observer must not turn a native observation into an I/O fault");
        using var previous = JsonDocument.Parse(observer);
        previous.RootElement.GetProperty("Brightness").GetByte().Should().Be(10, "the held handle keeps a whole old snapshot");
        using var current = ReadPreferenceSnapshot(path);
        current.RootElement.GetProperty("Brightness").GetByte().Should().Be(20, "new readers see the whole atomic replacement");
    }

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

using System.Text.Json;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Host.Services;

namespace UnifiedRgb.Host.Tests;

/// <summary>WP-11 package B persistence (mounting.json) and the persisted loop-policy identity.</summary>
public sealed class MountingStoreTests : IDisposable
{
    private const string Id = "lianli:rf:AA-BB-CC-DD-EE-01";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-mounting", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, MountingStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Save_and_reload_keep_only_the_settings_and_leave_no_temp_files()
    {
        var store = new MountingStore(FilePath);
        store.Load().Should().BeEmpty("absent file = no overrides");
        store.Save(Id, new MountingDto(Id, 180, true, [new FanMountingDto(2, 0, true), new FanMountingDto(0, 180, false)], "TL", 3, 9, "x", true, "note"));

        var again = new MountingStore(FilePath).Load();
        var m = again.Should().ContainKey(Id).WhoseValue;
        (m.RotationDegrees, m.Reverse, m.Family, m.Revision, m.Description, m.Note).Should().Be((180, true, (string?)null, 0, (string?)null, (string?)null));
        m.Fans.Select(f => f.Fan).Should().Equal(0, 2);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();

        store.Save("lianli:rf:AA-BB-CC-DD-EE-02", new MountingDto("ignored", 90, false, []));
        store.Load().Keys.Should().BeEquivalentTo(new[] { Id, "lianli:rf:AA-BB-CC-DD-EE-02" }, "keyed by the given controller ID");
        store.Save(Id, null);
        store.Load().Keys.Should().Equal("lianli:rf:AA-BB-CC-DD-EE-02");
    }

    [Theory]
    [InlineData("")]
    [InlineData("virtual:gpu")]
    [InlineData("lianli:rf:\u0001")]
    public void Invalid_ids_are_rejected(string id)
    {
        var store = new MountingStore(FilePath);
        MountingStore.IsValidId(id).Should().BeFalse();
        var save = () => store.Save(id, new MountingDto(id, 0, false, []));
        save.Should().Throw<ArgumentException>();
        File.Exists(FilePath).Should().BeFalse();
        MountingStore.IsValidId(new string('a', 200)).Should().BeFalse();
    }

    [Fact]
    public void Corrupt_files_are_reported_and_foreign_entries_dropped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ torn");
        var corrupt = () => new MountingStore(FilePath).Load();
        corrupt.Should().Throw<InvalidDataException>();

        File.WriteAllText(FilePath, """{"schemaVersion":99,"devices":[]}""");
        corrupt.Should().Throw<InvalidDataException>();

        var json = JsonSerializer.Serialize(
            new MountingFileDto(1, [new MountingDto("virtual:gpu", 180, false, []), new MountingDto(Id, 0, true, [])]),
            ContractsJson.Configure(new JsonSerializerOptions()));
        File.WriteAllText(FilePath, json);
        new MountingStore(FilePath).Load().Keys.Should().Equal(Id);
    }

    [Fact]
    public void Chain_reversal_persists_in_schema_2_and_schema_1_files_load_without_it()
    {
        // WP-13: schema 2 adds ReverseChain (whole-chain fan order), separate from the fan-local mirror
        MountingFileDto.CurrentSchema.Should().Be(3, "WP-14 adds the ring fields (schema 3)");
        var store = new MountingStore(FilePath);
        store.Save(Id, new MountingDto(Id, 0, false, [], ReverseChain: true));
        File.ReadAllText(FilePath).Should().MatchRegex("\"schemaVersion\":\\s*3").And.MatchRegex("\"reverseChain\":\\s*true");
        var m = new MountingStore(FilePath).Load()[Id];
        (m.Reverse, m.ReverseChain).Should().Be((false, true));

        store.Save(Id, new MountingDto(Id, 0, true, []));
        (store.Load()[Id].Reverse, store.Load()[Id].ReverseChain).Should().Be((true, false), "the two switches are independent");

        // a file written by the previous version (schema 1, no reverseChain field) still loads, with the flag off
        File.WriteAllText(FilePath, $$"""{"schemaVersion":1,"devices":[{"controllerId":"{{Id}}","rotationDegrees":0,"reverse":true,"fans":[]}]}""");
        var old = new MountingStore(FilePath).Load()[Id];
        (old.Reverse, old.ReverseChain).Should().Be((true, false));
    }

    [Fact]
    public void Loop_policy_is_persisted_explicitly_and_old_profiles_load_as_lconnect()
    {
        var old = JsonSerializer.Deserialize<AssignmentDto>(
            """{"targets":[{"controllerId":"lianli:rf:AA-BB-CC-DD-EE-01","zoneId":0}],"effectId":"Rainbow","colors":[]}""",
            ContractsJson.Configure(new JsonSerializerOptions()))!;
        old.LoopPolicy.Should().BeNull();
        DtoMapper.FromDto(old).Policy.Should().Be(LoopPolicy.LConnect);

        var generic = DtoMapper.FromDto(old with { LoopPolicy = "Generic" });
        generic.Policy.Should().Be(LoopPolicy.Generic);
        DtoMapper.ToDto(new Scene([generic])).Assignments[0].LoopPolicy.Should().Be("Generic");

        foreach (var bad in new[] { "1", "Vendor", "", "LConnect,Generic" })
        {
            var act = () => DtoMapper.ParsePolicy(bad);
            act.Should().Throw<ArgumentException>(bad);
        }
    }
}

using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Transports.Hid;
using UnifiedRgb.Transports.WinUsb;

namespace UnifiedRgb.Devices.Tests;

/// <summary>
/// Pure metadata and fake-handle tests; no HID/PnP call. Rows copy Root's actual metadata-only snapshot
/// (evidence/ACTUAL-ROOT-METADATA.json, sourceSha256 95bb92f358378413c5491b8e33cf87278650b40ed50c8b1bd6e24d35967188ac) field for
/// field: VID, PID, MI, COL, usage page, usage, input/output/feature lengths, caps error and the descriptor-only report ids.
/// Only the PnP instance segment of each path is replaced by a neutral placeholder; nothing selects by path text.
/// </summary>
public sealed class NativeInputMetadataTests
{
    private const string HidClass = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static HidCollectionInfo Row(string pid, string mi, int index, int iface, ushort usagePage, ushort usage,
        int input, int output, int feature, string? error = null, string suffix = "") =>
        new($@"\\?\hid#vid_0b05&pid_{pid}&{mi}#root-sanitized&0&{index:D4}#{HidClass}{suffix}", 0x0B05,
            pid == "1c24" ? (ushort)0x1C24 : (ushort)0x1ACE, iface, usagePage, usage, input, output, feature, error);

    // Actual wired 0B05:1C24 rows, in snapshot order.
    private static readonly HidCollectionInfo WiredConsumer = Row("1c24", "mi_02&col01", 0, 2, 0x000C, 1, 4, 0, 0);
    private static readonly HidCollectionInfo WiredSystem = Row("1c24", "mi_02&col02", 1, 2, 0x0001, 0x80, 2, 0, 0);
    private static readonly HidCollectionInfo WiredVendor = Row("1c24", "mi_02&col03", 2, 2, 0xFFC0, 1, 21, 0, 0);
    private static readonly HidCollectionInfo WiredDenied = Row("1c24", "mi_02&col04", 3, 2, 0, 0, 0, 0, 0, "CreateFile error 5");
    private static readonly HidCollectionInfo WiredBootKeyboard = Row("1c24", "mi_00", 0, 0, 0x0001, 6, 9, 2, 0, suffix: @"\kbd");
    private static readonly HidCollectionInfo WiredMi03Keyboard = Row("1c24", "mi_03", 0, 3, 0x0001, 6, 16, 0, 0, suffix: @"\kbd");
    private static readonly HidCollectionInfo WiredControl = Row("1c24", "mi_01", 0, 1, 0xFF00, 1, 65, 65, 0);

    private static HidCollectionInfo[] Wired =>
        [WiredConsumer, WiredSystem, WiredVendor, WiredDenied, WiredBootKeyboard, WiredMi03Keyboard, WiredControl];

    // Actual RF receiver 0B05:1ACE rows, in snapshot order.
    private static readonly HidCollectionInfo ReceiverConsumer = Row("1ace", "mi_03&col01", 0, 3, 0x000C, 1, 4, 0, 0);
    private static readonly HidCollectionInfo ReceiverSystem = Row("1ace", "mi_03&col02", 1, 3, 0x0001, 0x80, 2, 0, 0);
    private static readonly HidCollectionInfo ReceiverFfc2 = Row("1ace", "mi_03&col03", 2, 3, 0xFFC2, 1, 21, 0, 0);
    private static readonly HidCollectionInfo ReceiverVendor = Row("1ace", "mi_03&col04", 3, 3, 0xFFC0, 1, 21, 0, 0);
    private static readonly HidCollectionInfo ReceiverBootKeyboard2 = Row("1ace", "mi_00&col02", 1, 0, 0x0001, 6, 20, 0, 0, suffix: @"\kbd");
    private static readonly HidCollectionInfo ReceiverFfc1 = Row("1ace", "mi_03&col05", 4, 3, 0xFFC1, 1, 21, 0, 0);
    private static readonly HidCollectionInfo ReceiverDenied = Row("1ace", "mi_01", 0, 1, 0, 0, 0, 0, 0, "CreateFile error 5");
    private static readonly HidCollectionInfo ReceiverControlFf02 = Row("1ace", "mi_02&col01", 0, 2, 0xFF02, 1, 64, 64, 0);
    private static readonly HidCollectionInfo ReceiverControlFf00 = Row("1ace", "mi_02&col02", 1, 2, 0xFF00, 1, 64, 64, 0);
    private static readonly HidCollectionInfo ReceiverControlFf01 = Row("1ace", "mi_02&col03", 2, 2, 0xFF01, 1, 64, 64, 0);
    private static readonly HidCollectionInfo ReceiverBootKeyboard1 = Row("1ace", "mi_00&col01", 0, 0, 0x0001, 6, 9, 2, 0, suffix: @"\kbd");

    private static HidCollectionInfo[] Receiver =>
        [ReceiverConsumer, ReceiverSystem, ReceiverFfc2, ReceiverVendor, ReceiverBootKeyboard2, ReceiverFfc1, ReceiverDenied,
            ReceiverControlFf02, ReceiverControlFf00, ReceiverControlFf01, ReceiverBootKeyboard1];

    // Root's descriptor-only read handles (the only two collections Root opened): "...input RIDs [3]" and "...input RIDs [4]".
    private static HidInputDescriptor WiredDescriptor => new(0x0B05, 0x1C24, 0xFFC0, 1, 21, 0, 0, [3]);
    private static HidInputDescriptor ReceiverDescriptor => new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, 0, 0, [4]);

    // The same shapes the Host discovery uses (wired RID3, RF RID4; FFC0/1, input 21, no output/feature).
    private static NativeInputShape WiredShape => new(0x0B05, 0x1C24, 0xFFC0, 1, 21, [3]);
    private static NativeInputShape ReceiverShape => new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, [4]);

    [Fact]
    public void Actual_descriptor_strings_are_reproduced_exactly()
    {
        WiredDescriptor.ToString().Should().Be("0B05:1C24 UP:FFC0 U:0001 in 21 out 0 feature 0 input RIDs [3]");
        ReceiverDescriptor.ToString().Should().Be("0B05:1ACE UP:FFC0 U:0001 in 21 out 0 feature 0 input RIDs [4]");
    }

    [Fact]
    public void Wired_shape_admits_exactly_the_actual_MI02_COL03_collection_and_derives_interface_2()
    {
        var shape = WiredShape;
        Wired.Where(c => shape.Reject(c) is null).Should().Equal(WiredVendor);
        var rule = shape.RuleFor(WiredVendor);
        rule.Interface.Should().Be(2);
        rule.ToString().Should().Be("0B05:1C24 MI_02 UP:FFC0 U:0001 in 21 out 0 feature 0 input RIDs [3]");
        rule.Reject(WiredVendor).Should().BeNull();
        rule.Reject(WiredDescriptor).Should().BeNull();
        shape.Reject(WiredMi03Keyboard).Should().Be("not a vendor-defined usage page; ordinary HID input is never opened");
        shape.Reject(WiredControl).Should().Be("usage FF00/0001 is not FFC0/0001");
        shape.Reject(WiredDenied).Should().Be("caps unavailable");
        shape.Reject(WiredBootKeyboard).Should().Be("MI_00 or an unnumbered interface is never opened for input");
    }

    [Fact]
    public void Historical_fixed_MI03_wired_rule_rejects_every_actual_wired_collection()
    {
        // The pre-fix PrepareInput rule: the source of "Exactly one same-container native vendor21 collection is required."
        var historical = new HidVendorInputRule(0x0B05, 0x1C24, 3, 0xFFC0, 1, 21, [3]);
        Wired.Where(c => historical.Reject(c) is null).Should().BeEmpty();
        historical.Reject(WiredVendor).Should().Be("interface MI_02 is not MI_03");
        historical.Reject(WiredMi03Keyboard).Should().Be("not a vendor-defined usage page; ordinary HID input is never opened",
            "the actual wired MI03 is an ordinary keyboard collection, never a native notice collection");
    }

    [Fact]
    public void Receiver_shape_admits_only_actual_FFC0_and_derives_the_existing_RF_contract_rule()
    {
        var shape = ReceiverShape;
        Receiver.Where(c => shape.Reject(c) is null).Should().Equal(ReceiverVendor);
        var rule = shape.RuleFor(ReceiverVendor);
        rule.Interface.Should().Be(3);
        rule.ToString().Should().Be(RogKeyboardInputContract.EventRule.ToString());
        rule.Reject(ReceiverDescriptor).Should().BeNull();
        shape.Reject(ReceiverFfc1).Should().Be("usage FFC1/0001 is not FFC0/0001");
        shape.Reject(ReceiverFfc2).Should().Be("usage FFC2/0001 is not FFC0/0001");
        foreach (var control in new[] { ReceiverControlFf00, ReceiverControlFf01, ReceiverControlFf02 }) shape.Reject(control).Should().NotBeNull();
        // Cross-transport: neither shape admits the other device's actual collection.
        WiredShape.Reject(ReceiverVendor).Should().Be("device 0B05:1ACE is not 0B05:1C24");
        shape.Reject(WiredVendor).Should().Be("device 0B05:1C24 is not 0B05:1ACE");
    }

    [Theory]
    [InlineData("mi00")] [InlineData("unnumbered")] [InlineData("path-interface")] [InlineData("caps-error")] [InlineData("output")]
    [InlineData("feature")] [InlineData("length-20")] [InlineData("length-64")] [InlineData("usage")] [InlineData("usage-page")]
    [InlineData("ordinary-page")] [InlineData("pid")]
    public void Shape_refuses_every_non_exact_variant_of_the_actual_collection(string mutation)
    {
        var c = mutation switch
        {
            "mi00" => WiredVendor with { Interface = 0, Path = WiredVendor.Path.Replace("mi_02", "mi_00", StringComparison.Ordinal) },
            "unnumbered" => WiredVendor with { Interface = null, Path = WiredVendor.Path.Replace("&mi_02&col03", string.Empty, StringComparison.Ordinal) },
            "path-interface" => WiredVendor with { Interface = 3 },
            "caps-error" => WiredVendor with { Error = "CreateFile error 5" },
            "output" => WiredVendor with { OutputReportLength = 21 },
            "feature" => WiredVendor with { FeatureReportLength = 1 },
            "length-20" => WiredVendor with { InputReportLength = 20 },
            "length-64" => WiredVendor with { InputReportLength = 64 },
            "usage" => WiredVendor with { Usage = 2 },
            "usage-page" => WiredVendor with { UsagePage = 0xFFC1 },
            "ordinary-page" => WiredVendor with { UsagePage = 0x0001 },
            "pid" => WiredVendor with { Pid = 0x1ACE },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        WiredShape.Reject(c).Should().NotBeNull();
        var derive = () => WiredShape.RuleFor(c);
        derive.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Shape_arguments_are_validated_like_an_exact_rule()
    {
        var ordinary = () => new NativeInputShape(0x0B05, 0x1C24, 0x0001, 6, 21, [3]);
        ordinary.Should().Throw<ArgumentOutOfRangeException>();
        var none = () => new NativeInputShape(0x0B05, 0x1C24, 0xFFC0, 1, 21, []);
        none.Should().Throw<ArgumentException>();
        var zero = () => new NativeInputShape(0x0B05, 0x1C24, 0xFFC0, 1, 21, [0]);
        zero.Should().Throw<ArgumentException>();
        var duplicate = () => new NativeInputShape(0x0B05, 0x1C24, 0xFFC0, 1, 21, [3, 3]);
        duplicate.Should().Throw<ArgumentException>();
        var tooLong = () => new NativeInputShape(0x0B05, 0x1C24, 0xFFC0, 1, 65, [3]);
        tooLong.Should().Throw<ArgumentOutOfRangeException>();
        var noDevice = () => new NativeInputShape(0, 0x1C24, 0xFFC0, 1, 21, [3]);
        noDevice.Should().Throw<ArgumentException>();
        WiredShape.InputReportIds.Should().Equal(3);
        WiredShape.ToString().Should().Be("0B05:1C24 UP:FFC0 U:0001 in 21 out 0 feature 0 input RIDs [3]");
    }

    [Theory]
    [InlineData("actual")] [InlineData("rid-4")] [InlineData("rids-3-4")] [InlineData("length-20")] [InlineData("output")]
    [InlineData("feature")] [InlineData("usage-page")] [InlineData("device")]
    public void Derived_rule_reverifies_the_opened_descriptor_and_closes_on_drift(string variant)
    {
        var devices = new Inputs(variant switch
        {
            "actual" => WiredDescriptor,
            "rid-4" => new(0x0B05, 0x1C24, 0xFFC0, 1, 21, 0, 0, [4]),
            "rids-3-4" => new(0x0B05, 0x1C24, 0xFFC0, 1, 21, 0, 0, [3, 4]),
            "length-20" => new(0x0B05, 0x1C24, 0xFFC0, 1, 20, 0, 0, [3]),
            "output" => new(0x0B05, 0x1C24, 0xFFC0, 1, 21, 21, 0, [3]),
            "feature" => new(0x0B05, 0x1C24, 0xFFC0, 1, 21, 0, 2, [3]),
            "usage-page" => new(0x0B05, 0x1C24, 0xFFC1, 1, 21, 0, 0, [3]),
            "device" => new(0x0B05, 0x1ACE, 0xFFC0, 1, 21, 0, 0, [3]),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        });
        var open = () => HidVendorInputTransport.Open(devices, WiredVendor, WiredShape.RuleFor(WiredVendor), TimeSpan.FromMilliseconds(25));
        if (variant == "actual")
        {
            using var input = open();
            input.Rule.Interface.Should().Be(2); input.Collection.Should().Be(WiredVendor); devices.Handle.Disposed.Should().BeFalse();
        }
        else
        {
            open.Should().Throw<InvalidDataException>();
            devices.Handle.Disposed.Should().BeTrue();
        }
        devices.Opens.Should().Be(1); devices.Handle.Reads.Should().Be(0);
    }

    [Fact]
    public void Shape_refusals_never_open_an_ordinary_or_peer_collection()
    {
        foreach (var c in Wired.Where(c => c != WiredVendor))
        {
            var devices = new Inputs(WiredDescriptor);
            var open = () => HidVendorInputTransport.Open(devices, c, new HidVendorInputRule(0x0B05, 0x1C24, 2, 0xFFC0, 1, 21, [3]), TimeSpan.FromMilliseconds(25));
            open.Should().Throw<InvalidOperationException>();
            devices.Opens.Should().Be(0);
        }
        foreach (var peer in new[] { ReceiverFfc1, ReceiverFfc2 })
        {
            var devices = new Inputs(ReceiverDescriptor);
            var open = () => HidVendorInputTransport.Open(devices, peer, RogKeyboardInputContract.EventRule, TimeSpan.FromMilliseconds(25));
            open.Should().Throw<InvalidOperationException>();
            devices.Opens.Should().Be(0);
        }
    }

    [Theory]
    [InlineData(21, 3, true)] [InlineData(21, 4, false)] [InlineData(20, 3, false)] [InlineData(0, 0, false)]
    public async Task Derived_wired_rule_reads_RID3_reports_and_closes_permanently_on_other_framing(int length, byte rid, bool admitted)
    {
        var devices = new Inputs(WiredDescriptor);
        var report = new byte[length]; if (length != 0) { report[0] = rid; report[1] = 0x72; }
        devices.Handle.Reports.Writer.TryWrite(report);
        using var input = HidVendorInputTransport.Open(devices, WiredVendor, WiredShape.RuleFor(WiredVendor), TimeSpan.FromMilliseconds(250));
        var output = new byte[21];
        var read = async () => await input.ReadAsync(output, default);
        if (admitted)
        {
            (await read()).Should().BeTrue(); output[0].Should().Be(3); output[1].Should().Be(0x72); input.IsClosed.Should().BeFalse();
        }
        else
        {
            if (length == 0) await read.Should().ThrowAsync<IOException>();
            else await read.Should().ThrowAsync<InvalidDataException>();
            input.IsClosed.Should().BeTrue(); devices.Handle.Disposed.Should().BeTrue(); output.Should().OnlyContain(x => x == 0);
        }
    }

    [Fact]
    public void Platform_input_channel_opens_the_actual_collection_read_only_and_refuses_ordinary_or_MI00_before_CreateFile()
    {
        uint desired = 0, share = 0, flags = 0; var called = 0;
        var devices = new WinHidInputReportDevices(new NoEnumeration(), (_, a, s, f) =>
        { called++; desired = a; share = s; flags = f; return new SafeFileHandle(IntPtr.Zero, false); });
        var invalid = () => devices.OpenReadOnly(WiredVendor);
        invalid.Should().Throw<System.ComponentModel.Win32Exception>();
        called.Should().Be(1); desired.Should().Be(0x80000000u, "GENERIC_READ only"); share.Should().Be(3); flags.Should().Be(0x40000000u);
        foreach (var refused in new[] { WiredMi03Keyboard, WiredBootKeyboard, WiredDenied, WiredConsumer })
        {
            var act = () => devices.OpenReadOnly(refused);
            act.Should().Throw<InvalidOperationException>();
        }
        called.Should().Be(1);
        typeof(IHidInputReportDevices).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(new[] { "Enumerate", "OpenReadOnly" });
        typeof(IHidInputReportHandle).GetMethods().Select(m => m.Name).Should()
            .NotContain(n => n.Contains("Write", StringComparison.Ordinal) || n.Contains("Feature", StringComparison.Ordinal) || n.StartsWith("Set", StringComparison.Ordinal));
        typeof(HidVendorInputTransport).GetMethods().Select(m => m.Name).Should()
            .NotContain(n => n.Contains("Write", StringComparison.Ordinal) || n.Contains("Send", StringComparison.Ordinal) || n.Contains("Feature", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejection_diagnostics_never_carry_path_or_instance_text()
    {
        foreach (var c in Wired.Concat(Receiver))
        {
            foreach (var why in new[] { WiredShape.Reject(c), ReceiverShape.Reject(c) })
            {
                if (why is null) continue;
                why.Should().NotContain("hid#").And.NotContain("root-sanitized").And.NotContain(HidClass).And.NotContain(@"\");
            }
            c.ToString().Should().NotContain("root-sanitized").And.NotContain("hid#");
        }
    }

    private sealed class NoEnumeration : IUsbEnumerator
    {
        public IReadOnlyList<UsbDeviceInfo> Enumerate(Guid interfaceClass) => throw new InvalidOperationException("No PnP enumeration in tests.");
    }

    private sealed class Inputs(HidInputDescriptor descriptor) : IHidInputReportDevices
    {
        public Handle Handle { get; } = new(descriptor);
        public int Opens { get; private set; }
        public IReadOnlyList<HidCollectionInfo> Enumerate(ushort vid, ushort pid) => throw new InvalidOperationException("Not used.");
        public IHidInputReportHandle OpenReadOnly(HidCollectionInfo collection) { Opens++; return Handle; }
    }

    private sealed class Handle(HidInputDescriptor descriptor) : IHidInputReportHandle
    {
        public Channel<byte[]> Reports { get; } = Channel.CreateUnbounded<byte[]>();
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public HidInputDescriptor Describe() => descriptor;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++;
            var data = await Reports.Reader.ReadAsync(ct);
            data.CopyTo(buffer);
            return data.Length;
        }
        public void Dispose() => Disposed = true;
    }
}

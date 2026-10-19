using System.Buffers.Binary;
using System.Security.Cryptography;
using UnifiedRgb.Screens.Protocol;

namespace UnifiedRgb.Screens.Tests;

/// <summary>
/// Frame headers against goldens computed by an independent DES implementation (OpenSSL 3.5.5 legacy provider, DES-CBC,
/// key = IV = "slv3tuzx"; script and outputs in .artifacts-screens-opus-20261018/golden). Plain header: cmd, 1A 6D at [2..3],
/// LE32 timestamp 0x11223344 at [4..8), BE32 length 0x00012345 at [8..12), zeros elsewhere.
/// </summary>
public sealed class ProtocolTests
{
    private const uint Ts = 0x11223344;
    private const int Len = 0x00012345;

    [Theory]
    [InlineData(ScreenFamily.TlLcd, 101, "05683667ead84393e82aaf9c418cf1a8954a9f4f65b47c36652f25878b0bc515", "03cf7a6abf5e82e9ed75ad2363dacbaf")]
    [InlineData(ScreenFamily.H2, 101, "e51c1a5813da8de23782bedfb01cabcc9344b559c1de890b52d1ac396b813fc0", "03cf7a6abf5e82e9ed75ad2363dacbaf")]
    [InlineData(ScreenFamily.Turzx, 102, "dbde92e10ddecdf01aab40269975008c5333ff233cd5f234ebefbb15bf99de3d", "7b22249a4a68c5f1236b0abc827c58f9")]
    public void Encrypted_header_matches_the_independent_golden(ScreenFamily family, byte cmd, string sha256, string first16)
    {
        var block = ScreenProtocol.EncryptHeader(family, ScreenProtocol.BuildPlainHeader(family, cmd, Ts, Len));

        block.Should().HaveCount(512);
        Convert.ToHexString(block[..16]).ToLowerInvariant().Should().Be(first16);
        Convert.ToHexString(SHA256.HashData(block)).ToLowerInvariant().Should().Be(sha256);
    }

    [Theory]
    [InlineData(ScreenFamily.TlLcd, 504)]
    [InlineData(ScreenFamily.H2, 500)]
    [InlineData(ScreenFamily.Turzx, 500)]
    public void Plain_header_layout(ScreenFamily family, int size)
    {
        var cmd = ScreenProtocol.AllowedCommands(family)[0];
        var plain = ScreenProtocol.BuildPlainHeader(family, cmd, Ts, Len);

        plain.Should().HaveCount(size);
        plain[0].Should().Be(cmd);
        plain[1].Should().Be(0);
        plain[2].Should().Be(0x1A);
        plain[3].Should().Be(0x6D);
        BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4)).Should().Be(Ts);
        BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(8)).Should().Be(Len);
        plain.Skip(12).Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void H2_and_Turzx_differ_only_in_padding_of_the_last_cipher_block()
    {
        var plain = ScreenProtocol.BuildPlainHeader(ScreenFamily.H2, 102, Ts, Len);
        var h2 = ScreenProtocol.EncryptHeader(ScreenFamily.H2, plain);
        var tz = ScreenProtocol.EncryptHeader(ScreenFamily.Turzx, plain);

        h2[..496].Should().Equal(tz[..496]);
        h2[496..504].Should().NotEqual(tz[496..504]); // PKCS7 04 04 04 04 vs zero padding
        h2[504..510].Should().OnlyContain(b => b == 0);
        h2[510..].Should().Equal(0xA1, 0x1A);
        tz[510..].Should().Equal(0xA1, 0x1A);
    }

    [Theory]
    [InlineData(ScreenFamily.TlLcd)]
    [InlineData(ScreenFamily.H2)]
    [InlineData(ScreenFamily.Turzx)]
    public void Decrypt_round_trips_and_checks_the_trailer(ScreenFamily family)
    {
        var plain = ScreenProtocol.BuildPlainHeader(family, ScreenProtocol.AllowedCommands(family)[0], Ts, 777);
        var block = ScreenProtocol.EncryptHeader(family, plain);

        ScreenProtocol.DecryptHeader(family, block).Should().Equal(plain);
        if (family != ScreenFamily.TlLcd)
        {
            var bad = (byte[])block.Clone();
            bad[511] = 0;
            FluentActions.Invoking(() => ScreenProtocol.DecryptHeader(family, bad)).Should().Throw<Exception>();
        }
    }

    [Fact]
    public void Tl_packet_is_the_fixed_102400_buffer_with_zero_padding()
    {
        var jpeg = Jpeg(1000);
        var packet = ScreenProtocol.BuildImagePacket(ScreenFamily.TlLcd, 101, jpeg, Ts);

        packet.Should().HaveCount(102400);
        packet.AsSpan(512, jpeg.Length).ToArray().Should().Equal(jpeg);
        packet.Skip(512 + jpeg.Length).Should().OnlyContain(b => b == 0);
        var header = ScreenProtocol.DecryptHeader(ScreenFamily.TlLcd, packet.AsSpan(0, 512));
        header[0].Should().Be(101);
        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)).Should().Be(1000);
    }

    [Theory]
    [InlineData(ScreenFamily.H2, 101)]
    [InlineData(ScreenFamily.Turzx, 102)]
    public void H2_and_Turzx_packets_are_header_plus_image(ScreenFamily family, byte cmd)
    {
        var image = cmd == 101 ? Jpeg(3000) : Png(3000);
        var packet = ScreenProtocol.BuildImagePacket(family, cmd, image, Ts);

        packet.Should().HaveCount(512 + 3000);
        packet[512..].Should().Equal(image);
        BinaryPrimitives.ReadInt32BigEndian(ScreenProtocol.DecryptHeader(family, packet.AsSpan(0, 512)).AsSpan(8)).Should().Be(3000);
    }

    [Theory]
    [InlineData(ScreenFamily.TlLcd, 101888, true)]
    [InlineData(ScreenFamily.TlLcd, 101889, false)]
    [InlineData(ScreenFamily.Turzx, 524288, true)]
    [InlineData(ScreenFamily.Turzx, 524289, false)]
    [InlineData(ScreenFamily.H2, 2 * 1024 * 1024, true)]
    [InlineData(ScreenFamily.H2, (2 * 1024 * 1024) + 1, false)]
    public void Image_length_bounds(ScreenFamily family, int length, bool ok)
    {
        var cmd = family == ScreenFamily.Turzx ? (byte)102 : (byte)101;
        var image = cmd == 101 ? Jpeg(length) : Png(length);
        var act = () => ScreenProtocol.BuildImagePacket(family, cmd, image, Ts);

        if (ok)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Empty_or_mismatched_images_are_refused()
    {
        FluentActions.Invoking(() => ScreenProtocol.BuildImagePacket(ScreenFamily.TlLcd, 101, [], Ts)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScreenProtocol.BuildImagePacket(ScreenFamily.TlLcd, 101, Png(100), Ts)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScreenProtocol.BuildImagePacket(ScreenFamily.Turzx, 102, Jpeg(100), Ts)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScreenProtocol.BuildImagePacket(ScreenFamily.H2, 102, Jpeg(100), Ts)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Only_image_push_commands_are_allowed()
    {
        ScreenProtocol.AllowedCommands(ScreenFamily.TlLcd).Should().Equal(101);
        ScreenProtocol.AllowedCommands(ScreenFamily.H2).Should().BeEquivalentTo(new byte[] { 101, 102 });
        ScreenProtocol.AllowedCommands(ScreenFamily.Turzx).Should().Equal(102);
        foreach (var family in Enum.GetValues<ScreenFamily>())
        {
            for (var cmd = 0; cmd < 256; cmd++)
            {
                if (ScreenProtocol.AllowedCommands(family).Contains((byte)cmd))
                {
                    continue;
                }

                // H.264, file/storage, firmware, reset, brightness/PWM, frame-rate and every other command: refused
                FluentActions.Invoking(() => ScreenProtocol.BuildImagePacket(family, (byte)cmd, Jpeg(64), Ts))
                    .Should().Throw<ArgumentException>($"{family} cmd {cmd}");
            }
        }
    }

    [Fact]
    public void Vendor_timestamp_counts_ms_from_local_midnight_of_yesterday_to_utc_now()
    {
        var local = new DateTime(2026, 10, 18, 16, 0, 0, DateTimeKind.Local);
        var utc = new DateTime(2026, 10, 18, 8, 0, 0, 400, DateTimeKind.Utc);
        var stamp = new ScreenTimestamp(ScreenFamily.TlLcd, () => utc, () => local);

        // epoch 2026-10-17 00:00 (captured once); ticks compared raw, as the vendor's DateTime subtraction does
        stamp.Next().Should().Be((uint)(((24 + 8) * 3600 * 1000) + 400));
        utc = utc.AddDays(60);
        stamp.Next().Should().Be(unchecked((uint)(((60L * 24) + 24 + 8) * 3600 * 1000) + 400));
    }

    [Fact]
    public void Turzx_timestamp_is_ms_since_local_midnight()
    {
        var local = new DateTime(2026, 10, 18, 1, 2, 3, 456, DateTimeKind.Local);
        var stamp = new ScreenTimestamp(ScreenFamily.Turzx, () => DateTime.UtcNow, () => local);

        stamp.Next().Should().Be((uint)((((1 * 60) + 2) * 60 * 1000) + 3456));
    }

    private static byte[] Jpeg(int length)
    {
        var b = new byte[length];
        b[0] = 0xFF;
        b[1] = 0xD8;
        b[2] = 0xFF;
        return b;
    }

    private static byte[] Png(int length)
    {
        var b = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        return b;
    }
}

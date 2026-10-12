using FluentAssertions;
using GBM.Core.Services;
using Xunit;
using static GBM.Core.Services.D3BatteryProtocol;

namespace GBM.Tests.Services;

/// <summary>
/// Tests for the Model D3 / O3 Wireless battery protocol. Frames are REAL
/// captures of Glorious CORE, including a live hot-swap-pack pull/reinsert that
/// proved the two-cell layout (pack in bytes[4..7], Guardian in bytes[8..11]).
/// </summary>
public class D3BatteryProtocolTests
{
    [Fact]
    public void BatteryCommand_IsReportId03_BB82()
    {
        var cmd = BuildCommand(new byte[] { 0xBB, 0x82 });
        cmd.Length.Should().Be(64);
        cmd[0].Should().Be(0x03);
        cmd[1].Should().Be(0xBB);
        cmd[2].Should().Be(0x82);
        for (int i = 3; i < cmd.Length; i++) cmd[i].Should().Be(0x00);
    }

    [Fact]
    public void PackPresent_HeadlineIsPack_BothCellsParsed()
    {
        // +0.0s capture frame: pack 0x51=81% @4202mV, guardian 0x53=83% @4133mV.
        var r = ParseBattery(Pad("03bb82080251106a0253102500"));

        r.Success.Should().BeTrue();
        r.Pack.Present.Should().BeTrue();
        r.Pack.Level.Should().Be(81);
        r.Pack.VoltageMillivolts.Should().Be(4202);
        r.Guardian.Present.Should().BeTrue();
        r.Guardian.Level.Should().Be(83);
        r.Guardian.VoltageMillivolts.Should().Be(4133);
        r.OnGuardianOnly.Should().BeFalse();
        r.Level.Should().Be(81);         // headline = pack
        r.Status.Should().Be("pack");
    }

    [Fact]
    public void PackPulled_HeadlineReadsNoPack_GuardianStillReported()
    {
        // +30.0s capture frame: pack absent (byte[4]=00), guardian 0x53=83%.
        // The headline (tray/main gauge) must read as NO battery — it must never
        // borrow the Guardian's charge, which would imply the hot-swap pack still
        // has power. The Guardian is still reported in its own field.
        var r = ParseBattery(Pad("03bb82080000000002531017"));

        r.Success.Should().BeFalse();     // tray: no hot-swap battery
        r.Level.Should().Be(0);           // not borrowing the Guardian's 83%
        r.Pack.Present.Should().BeFalse();
        r.Guardian.Present.Should().BeTrue();
        r.Guardian.Level.Should().Be(83); // still available for its own card
        r.OnGuardianOnly.Should().BeTrue();
        r.Status.Should().Be("no-pack");
    }

    [Theory]
    // Real pull sequence: guardian discharges 83 -> 82 -> 81 while pack is out.
    [InlineData("03bb82080000000002531017", 83)]
    [InlineData("03bb820800000000025210ff", 82)]
    [InlineData("03bb82080000000002511002", 81)]
    public void GuardianDischarge_WhilePackOut(string hex, int expectedGuardian)
    {
        var r = ParseBattery(Pad(hex));
        r.Pack.Present.Should().BeFalse();
        r.Guardian.Level.Should().Be(expectedGuardian);
    }

    [Fact]
    public void PackReinserted_HeadlineReturnsToPack()
    {
        // +55.0s capture frame: pack back, 0x50=80% @4202mV.
        var r = ParseBattery(Pad("03bb820802501 06a0251101 0".Replace(" ", "")));

        r.Pack.Present.Should().BeTrue();
        r.Pack.Level.Should().Be(80);
        r.OnGuardianOnly.Should().BeFalse();
        r.Level.Should().Be(80);
        r.Status.Should().Be("pack");
    }

    [Fact]
    public void ParseBattery_AllZeroBuffer_IsNoAnswer()
    {
        var buf = new byte[64];
        buf[0] = 0x03;
        var r = ParseBattery(buf);
        r.Success.Should().BeFalse();
        r.Status.Should().Be("no-answer");
    }

    [Fact]
    public void ParseBattery_HeartbeatReply_IsRejected()
    {
        // 03 AA E2 is the status heartbeat, not battery.
        ParseBattery(Pad("03aae20501220f590100")).Success.Should().BeFalse();
    }

    [Fact]
    public void ParseBattery_ShortBuffer_IsRejected()
    {
        var r = ParseBattery(new byte[6]);
        r.Success.Should().BeFalse();
        r.Status.Should().Be("short");
    }

    [Fact]
    public void ParseBattery_RejectsDockReply_NoCrossContamination()
    {
        // The firmware shares one response buffer, so a battery query can receive
        // the dock's AA-E2 reply. ParseBattery must reject it (the real fix reads
        // again until the header matches, but the parser is the backstop).
        ParseBattery(Pad("03aae205026411 0a01".Replace(" ", ""))).Success.Should().BeFalse();
    }

    [Fact]
    public void ParseDock_RejectsBatteryReply_NoCrossContamination()
    {
        // ...and a dock query can receive the battery's BB-82 reply.
        ParseDock(Pad("03bb8208025910c8")).Present.Should().BeFalse();
    }

    [Fact]
    public void Handles_OnlyD3Vendor()
    {
        Handles(0x3794).Should().BeTrue();
        Handles(0x258A).Should().BeFalse();
        Handles(0x093A).Should().BeFalse();
    }

    // --- Dock spare battery (AA E2), from the live dock pull/reinsert capture ---

    [Theory]
    [InlineData("03aae205015910f501", 89, 4341)]   // +3s  present
    [InlineData("03aae205015b110101", 91, 4353)]   // +13s present
    [InlineData("03aae205015a10fc01", 90, 4348)]   // +43s reinserted
    public void ParseDock_PresentFrames(string hex, int level, int mv)
    {
        var dock = ParseDock(Pad(hex));
        dock.Present.Should().BeTrue();
        dock.Level.Should().Be(level);
        dock.VoltageMillivolts.Should().Be(mv);
    }

    [Fact]
    public void ParseDock_PackPulled_IsNotPresent()
    {
        // +23s: dock pack removed — byte[4]=00, group zeroed.
        var dock = ParseDock(Pad("03aae2050000000001"));
        dock.Present.Should().BeFalse();
        dock.Level.Should().Be(0);
    }

    [Fact]
    public void ParseDock_RejectsBatteryFrame()
    {
        // A BB 82 reply must not be misread as a dock reply.
        ParseDock(Pad("03bb82080251106a02531025")).Present.Should().BeFalse();
    }

    [Fact]
    public void ParseBattery_RejectsDockFrame()
    {
        // An AA E2 reply must not be misread as a mouse battery reply.
        ParseBattery(Pad("03aae205015910f501")).Success.Should().BeFalse();
    }

    [Fact]
    public void Result_CarriesAllThreeCells()
    {
        var r = ParseBattery(Pad("03bb82080251106a0253102500"))
            with { Dock = ParseDock(Pad("03aae205015910f501")) };

        r.Pack.Level.Should().Be(81);
        r.Guardian.Level.Should().Be(83);
        r.Dock.Present.Should().BeTrue();
        r.Dock.Level.Should().Be(89);
    }

    private static byte[] Pad(string hex)
    {
        var src = Convert.FromHexString(hex);
        var b = new byte[64];
        Array.Copy(src, b, Math.Min(src.Length, 64));
        return b;
    }
}

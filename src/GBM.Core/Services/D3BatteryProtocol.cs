// =============================================================================
//  D3BatteryProtocol — Glorious Model D3 / O3 Wireless (VID 0x3794, PID 0xA300)
// -----------------------------------------------------------------------------
//  Battery read for the V3 wireless generation (Nordic nRF52840 receiver),
//  decoded from Glorious CORE captures (Wireshark/USBPcap), including a live
//  hot-swap-pack pull/reinsert that disambiguated the two cells.
//
//  COMMAND (host -> device, feature report, report id 0x03):
//        03 BB 82 00 00 ... (64 bytes)
//
//  REPLY (device -> host, 64 bytes) reports TWO cells, each a 4-byte group
//  [flag, percent, voltHi, voltLo]:
//
//        byte[0..2] = 03 BB 82     command echo
//        byte[3]    = 0x08         payload length
//        --- group A: HOT-SWAP PACK (the main user-facing battery) ---
//        byte[4]    = 0x02 present / 0x00 absent (pack removed)
//        byte[5]    = pack percentage
//        byte[6:7]  = pack voltage, mV, big-endian
//        --- group B: built-in GUARDIAN backup cell (always present) ---
//        byte[8]    = 0x02 marker
//        byte[9]    = guardian percentage
//        byte[10:11]= guardian voltage, mV, big-endian
//
//  Verified by the pull/reinsert capture: group A zeros out the instant the
//  pack is removed and returns on reinsert, while group B (Guardian) visibly
//  discharges while it is the sole power source, then recovers. Voltages ~4.2 V
//  (pack) / ~4.1 V (Guardian) confirm both percentage bytes are genuine.
//
//  The DOCK's spare battery is a THIRD cell, read with a SEPARATE command on the
//  same interface:
//        Dock   SET_REPORT:  03 AA E2 ...
//               GET_REPORT:  03 AA E2 05 <present> <pct> <vHi> <vLo> ...
//        byte[4] = 0x01 present / 0x00 no pack in dock
//        byte[5] = dock pack percentage
//        byte[6:7] = dock pack voltage, mV, big-endian
//  Verified by a live dock pull/reinsert that zeroed bytes[4..7] and restored
//  them (89-91% @ ~4.34 V). Earlier this command looked like an idle heartbeat
//  only because the dock pack sat at a steady charge.
//
//  Headline battery = the hot-swap PACK when present; if the pack is out, the
//  mouse is running on the Guardian, so we report the Guardian and flag it.
//
//  Read-only: only the battery/identity STATUS commands are ever sent.
// =============================================================================

using System;
using System.Threading;
using HidSharp;
using Microsoft.Extensions.Logging;

namespace GBM.Core.Services;

/// <summary>
/// Battery read for Glorious V3 Wireless mice (Model D3 / O3), VID 0x3794.
/// </summary>
public static class D3BatteryProtocol
{
    public const int VendorId = 0x3794;
    public const int ProductId = 0xA300;

    private const string FeatureInterfaceToken = "mi_00&col01";
    private const int FeatureReportLength = 64;
    private const int ReportId = 0x03;

    // The firmware uses a single shared response buffer, so a GetFeature issued
    // too soon after SetFeature can pick up the PREVIOUS command's reply (the
    // battery query returning the dock's answer and vice versa). We therefore
    // wait longer and VERIFY the reply header matches the command we sent,
    // retrying a few times until it does.
    // This firmware lags its shared response buffer by one command: the reply to
    // command N surfaces on the GetFeature issued AFTER command N+1. We exploit
    // that with a short, bounded exchange rather than a retry storm — hammering
    // the feature interface disrupts the mouse's own 2.4 GHz link, which showed up
    // as the mouse "losing connection" and the dock still missing. Keep this gentle.
    private const int RfRoundTripDelayMs = 50;
    private const int MatchRetries = 3;         // gentle: 3 reads max per query
    private const int MatchRetryDelayMs = 20;
    private const int RetryDelayMs = 250;

    private static readonly byte[] BatteryCommand = { 0xBB, 0x82 };
    private static readonly byte[] IdentityCommand = { 0xBB, 0x99 };
    // Dock spare-battery query. The reply (03 AA E2 ...) carries the dock pack as
    // [flag(0x01 present), percent, voltHi, voltLo] at bytes[4..7]. Verified by a
    // live dock pull/reinsert that zeroed the group and brought it back.
    private static readonly byte[] DockCommand = { 0xAA, 0xE2 };

    /// <summary>One cell's reading.</summary>
    public readonly record struct Cell(bool Present, int Level, int VoltageMillivolts);

    /// <summary>
    /// Full battery state. <see cref="Level"/>/<see cref="Charging"/> are the
    /// headline figures (the pack when present, else the Guardian), for the
    /// existing single-value UI. <see cref="Pack"/> and <see cref="Guardian"/>
    /// expose both cells for a richer display.
    /// </summary>
    public readonly record struct Result(
        bool Success, int Level, bool Charging, int VoltageMillivolts,
        Cell Pack, Cell Guardian, bool OnGuardianOnly, string Status)
    {
        /// <summary>
        /// The dock's spare battery, read separately via the AA E2 command.
        /// Present=false when no pack sits in the dock. This is a third physical
        /// cell, independent of Pack/Guardian.
        /// </summary>
        public Cell Dock { get; init; }

        /// <summary>
        /// True when the dock query did not get a matching reply this cycle (the
        /// firmware's shared buffer returned a sibling command's answer too many
        /// times). The UI should HOLD the last known dock value rather than blank
        /// it — a miss is not the same as an empty dock. When this is true,
        /// <see cref="Dock"/> is not meaningful.
        /// </summary>
        public bool DockReadMissed { get; init; }
    }

    public static bool Handles(int vendorId) => vendorId == VendorId;

    /// <summary>
    /// Reads the mouse battery (pack + Guardian), and optionally the dock cell.
    /// The dock is a slow-changing spare battery, so it should be polled only
    /// occasionally (<paramref name="includeDock"/> = true) rather than every
    /// tick — querying it constantly keeps the feature interface busy and can
    /// disrupt the mouse's own 2.4 GHz link. When includeDock is false the dock
    /// is reported as "missed" so the caller holds its last known value.
    /// </summary>
    public static Result Read(string devicePath, ILogger? logger = null, bool includeDock = true)
    {
        try
        {
            var device = FindByPath(devicePath);
            if (device == null)
            {
                logger?.LogDebug("[D3] Device not found at path: {Path}", devicePath);
                return Fail("not-found");
            }

            using var stream = device.Open();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;

            var r = QueryBattery(stream, logger);

            // Retry only when the frame genuinely didn't arrive (asleep / RF not
            // ready). "Pack absent" is a valid final answer — the mouse is on the
            // Guardian — so we must NOT retry that, or we'd stall every cycle.
            if (!r.Success && r.Status is "no-answer" or "short" or "error")
            {
                Thread.Sleep(RetryDelayMs);
                var retry = QueryBattery(stream, logger);
                if (retry.Success || retry.Status == "no-pack") r = retry;
            }

            if (!includeDock)
            {
                // Don't touch the dock this cycle — report it as missed so the UI
                // holds the last value. Keeps the interface free for the mouse.
                return r with { Dock = default, DockReadMissed = true };
            }

            // Query the dock's spare battery on the same open stream. A failure
            // here never fails the mouse reading.
            var (dock, dockMissed) = QueryDock(stream, logger);
            return r with { Dock = dock, DockReadMissed = dockMissed };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[D3] Read failed for {Path}", devicePath);
            return Fail("error");
        }
    }

    /// <summary>
    /// Sends a 2-byte command and reads back the reply whose header matches it.
    ///
    /// This firmware's shared response buffer lags by one command: the reply to
    /// command N appears on the GetFeature issued AFTER command N+1. So re-reading
    /// the same buffer doesn't help — we must RE-SEND the command so its reply
    /// becomes "the previous command's reply" on the next read. The loop therefore
    /// sends, reads, and on any mismatch sends again, until the header matches.
    /// </summary>
    private static byte[]? SendAndMatch(HidStream stream, byte cmdHi, byte cmdLo,
        string tag, ILogger? logger)
    {
        var cmd = BuildCommand(new[] { cmdHi, cmdLo });

        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("[D3] SetFeature {Tag}: {Bytes}", tag,
                BitConverter.ToString(cmd, 0, 4));

        for (int attempt = 0; attempt < MatchRetries; attempt++)
        {
            // Send (or re-send) the command, then read. Because the reply lags by
            // one command, sending the SAME command again makes the reply we want
            // appear on this read once the buffer catches up.
            stream.SetFeature(cmd);
            Thread.Sleep(RfRoundTripDelayMs);

            var resp = new byte[FeatureReportLength];
            resp[0] = ReportId;
            stream.GetFeature(resp);

            if (resp[0] == ReportId && resp[1] == cmdHi && resp[2] == cmdLo)
            {
                if (logger?.IsEnabled(LogLevel.Debug) == true)
                    logger.LogDebug("[D3] {Tag} reply: {Bytes}", tag,
                        BitConverter.ToString(resp, 0, 12));
                return resp;
            }

            if (logger?.IsEnabled(LogLevel.Debug) == true)
                logger.LogDebug("[D3] {Tag} mismatch (got {H1:X2} {H2:X2}, want {W1:X2} {W2:X2}), retry {N}",
                    tag, resp[1], resp[2], cmdHi, cmdLo, attempt + 1);

            Thread.Sleep(MatchRetryDelayMs);
        }

        logger?.LogDebug("[D3] {Tag}: no matching reply after {N} tries", tag, MatchRetries);
        return null;
    }

    private static Result QueryBattery(HidStream stream, ILogger? logger)
    {
        var resp = SendAndMatch(stream, 0xBB, 0x82, "battery", logger);
        if (resp == null)
            return Fail("no-answer");
        return ParseBattery(resp, logger);
    }

    /// <summary>
    /// Queries the dock. Returns the cell and whether the read MISSED (no matching
    /// reply this cycle) as distinct from a real empty dock. On a miss the caller
    /// should hold the last known value rather than show the dock as absent.
    /// </summary>
    private static (Cell Cell, bool Missed) QueryDock(HidStream stream, ILogger? logger)
    {
        try
        {
            var resp = SendAndMatch(stream, 0xAA, 0xE2, "dock", logger);
            if (resp == null)
                return (default, true);          // no matching reply → miss
            return (ParseDock(resp, logger), false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[D3] Dock query failed");
            return (default, true);              // exception → treat as a miss
        }
    }

    /// <summary>
    /// Parses a dock reply (03 AA E2 ...). The dock pack sits at bytes[4..7] as
    /// [present(0x01), percent, voltHi, voltLo]. Public and static for testing.
    /// </summary>
    public static Cell ParseDock(byte[] r, ILogger? logger = null)
    {
        if (r.Length < 8 || r[0] != ReportId || r[1] != 0xAA || r[2] != 0xE2)
        {
            logger?.LogDebug("[D3] Dock reply not recognised (header {B0:X2} {B1:X2} {B2:X2})",
                r.Length > 0 ? r[0] : 0, r.Length > 1 ? r[1] : 0, r.Length > 2 ? r[2] : 0);
            return default;
        }

        // Capture showed the dock present-flag at byte[4] = 0x01, percentage at
        // [5], voltage at [6:7]. Be tolerant of flag variation: treat the dock as
        // present when the flag is non-zero AND the percentage/voltage look real,
        // and as absent when the group is zeroed (pack pulled from the dock).
        byte flag = r[4];
        int pct = r[5];
        int mv = (r[6] << 8) | r[7];

        bool zeroed = flag == 0x00 && pct == 0x00 && mv == 0;
        if (zeroed)
        {
            logger?.LogDebug("[D3] Dock empty (no pack in dock).");
            return new Cell(false, 0, 0);
        }

        if (pct is < 1 or > 100)
        {
            // Flag set but percentage implausible — log the raw bytes so a changed
            // layout is easy to spot, and report absent rather than a bogus value.
            logger?.LogDebug("[D3] Dock bytes unexpected: {Bytes}",
                BitConverter.ToString(r, 0, 8));
            return default;
        }

        logger?.LogDebug("[D3] Dock battery {Level}% ({Mv}mV, flag=0x{Flag:X2})", pct, mv, flag);
        return new Cell(true, pct, mv);
    }

    public static byte[] BuildCommand(byte[] command)
    {
        var b = new byte[FeatureReportLength];
        b[0] = ReportId;
        for (int i = 0; i < command.Length && (1 + i) < b.Length; i++)
            b[1 + i] = command[i];
        return b;
    }

    /// <summary>
    /// Parses a battery reply (03 BB 82 ...) into both cells. Public and static
    /// for hardware-free unit testing against captured frames.
    /// </summary>
    public static Result ParseBattery(byte[] r, ILogger? logger = null)
    {
        if (r.Length < 12)
            return Fail("short");

        if (r[0] != ReportId || r[1] != 0xBB || r[2] != 0x82)
        {
            bool empty = true;
            for (int i = 1; i < r.Length; i++) if (r[i] != 0) { empty = false; break; }
            return Fail(empty ? "no-answer" : "unexpected");
        }

        // Group A: hot-swap pack (present only when byte[4]==0x02).
        bool packPresent = r[4] == 0x02;
        int packPct = r[5];
        int packMv = (r[6] << 8) | r[7];
        var pack = new Cell(packPresent, packPct, packMv);

        // Group B: Guardian backup cell (always reported).
        int guardPct = r[9];
        int guardMv = (r[10] << 8) | r[11];
        var guardian = new Cell(true, guardPct, guardMv);

        // Guardian sanity (it's always reported). An out-of-range value means a
        // malformed frame; treat the whole read as failed.
        if (guardPct is < 0 or > 100)
        {
            logger?.LogDebug("[D3] Guardian level out of range: {Level}", guardPct);
            return Fail("level-oor");
        }

        // The HEADLINE battery is the mouse hot-swap pack ONLY — the number shown
        // in the tray and the hero gauge. We never substitute the Guardian here:
        // if the pack is removed, the mouse is running on the built-in backup and
        // the headline must read as "no pack" rather than borrowing the Guardian's
        // charge (which would wrongly imply the hot-swap battery still has power).
        //
        // So when the pack is absent we report Success=false for the headline.
        // The Guardian and Dock cells are still returned in their own fields, so
        // the side readouts keep showing them — only the main number goes empty.
        bool onGuardianOnly = !packPresent;

        if (!packPresent)
        {
            logger?.LogDebug(
                "[D3] pack REMOVED — running on Guardian {GuardPct}%. Headline = no pack.",
                guardPct);

            return new Result(
                Success: false,          // headline/tray: no hot-swap battery
                Level: 0,
                Charging: false,
                VoltageMillivolts: 0,
                Pack: pack,              // Present=false
                Guardian: guardian,      // still reported for its own field
                OnGuardianOnly: true,
                Status: "no-pack");
        }

        // Pack present but byte garbled — fail rather than show a bogus number.
        if (packPct is < 0 or > 100)
        {
            logger?.LogDebug("[D3] Pack level out of range: {Level}", packPct);
            return new Result(false, 0, false, 0, pack, guardian, false, "pack-oor");
        }

        int packLevel = packPct;
        if (packLevel == 0 && packMv > 3000) packLevel = 1;

        logger?.LogDebug(
            "[D3] pack={PackPct}% ({PackMv}mV)  guardian={GuardPct}% ({GuardMv}mV)",
            packLevel, packMv, guardPct, guardMv);

        return new Result(
            Success: true,
            Level: packLevel,            // headline = pack ONLY
            Charging: false,             // dock interface is the charge authority
            VoltageMillivolts: packMv,
            Pack: pack,
            Guardian: guardian,
            OnGuardianOnly: onGuardianOnly,
            Status: "pack");
    }

    private static Result Fail(string status) =>
        new(false, 0, false, 0, default, default, false, status);

    public static string? ReadIdentity(string devicePath, ILogger? logger = null)
    {
        try
        {
            var device = FindByPath(devicePath);
            if (device == null) return null;

            using var stream = device.Open();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;

            var resp = SendAndMatch(stream, 0xBB, 0x99, "identity", logger);
            if (resp == null) return null;

            int len = resp[3];
            if (len <= 0 || 4 + len > resp.Length) len = Math.Min(32, resp.Length - 4);
            var sb = new System.Text.StringBuilder();
            for (int i = 4; i < 4 + len; i++)
            {
                byte c = resp[i];
                if (c == 0) break;
                if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
            }
            var id = sb.ToString();
            logger?.LogDebug("[D3] Identity: {Id}", id);
            return id.Length > 0 ? id : null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[D3] ReadIdentity failed");
            return null;
        }
    }

    private static HidDevice? FindByPath(string devicePath)
    {
        foreach (var d in DeviceList.Local.GetHidDevices(VendorId, ProductId))
            if (string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    public static string? FindFeatureInterfacePath()
    {
        HidDevice? fallback = null;
        foreach (var d in DeviceList.Local.GetHidDevices(VendorId, ProductId))
        {
            int feat;
            try { feat = d.GetMaxFeatureReportLength(); } catch { continue; }
            if (feat != FeatureReportLength) continue;

            if (d.DevicePath.Contains(FeatureInterfaceToken, StringComparison.OrdinalIgnoreCase))
                return d.DevicePath;
            fallback ??= d;
        }
        return fallback?.DevicePath;
    }
}

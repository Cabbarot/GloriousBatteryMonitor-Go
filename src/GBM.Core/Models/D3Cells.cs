namespace GBM.Core.Models;

/// <summary>
/// One battery cell of a multi-cell device (percentage + presence + voltage).
/// </summary>
public readonly record struct BatteryCell(bool Present, int Level, int VoltageMillivolts)
{
    public static BatteryCell Absent => new(false, 0, 0);
}

/// <summary>
/// The full three-cell picture for a Glorious V3 Wireless (D3 / O3):
/// the hot-swap pack in the mouse (the main battery), the built-in Guardian
/// backup, and the spare pack sitting in the dock. Any cell may be absent.
/// </summary>
public readonly record struct D3BatterySet(
    BatteryCell Pack,       // mouse hot-swap pack — the headline battery
    BatteryCell Guardian,   // built-in backup cell
    BatteryCell Dock)       // spare pack in the dock
{
    public static D3BatterySet Empty =>
        new(BatteryCell.Absent, BatteryCell.Absent, BatteryCell.Absent);

    /// <summary>True if we have at least the pack or Guardian reading.</summary>
    public bool HasMouseData => Pack.Present || Guardian.Present;

    /// <summary>
    /// True when the dock query missed this cycle (the shared-buffer race). The
    /// UI should hold the last known dock value rather than blank it, since a
    /// miss is not the same as an empty dock.
    /// </summary>
    public bool DockReadMissed { get; init; }
}

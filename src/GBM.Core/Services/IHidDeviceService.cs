using GBM.Core.Models;

namespace GBM.Core.Services;

public interface IHidDeviceService
{
    List<DeviceInfo> EnumerateDevices();
    (bool Success, int BatteryLevel, bool IsCharging) ReadBattery(DeviceProfile profile);
    DeviceProfile? ProbeDevice(DeviceInfo device);
    bool IsWiredDevicePresent(string modelName);
    bool IsDevicePresent(DeviceProfile profile);
    string GetHidDiagnostics();
    byte[]? CaptureRawReport(DeviceProfile profile);

    /// <summary>
    /// Reads the full three-cell battery set from a connected Glorious V3
    /// Wireless (D3 / O3): the mouse hot-swap pack, the built-in Guardian
    /// backup, and the dock's spare pack. Returns D3BatterySet.Empty if no D3 is
    /// present or it does not answer. Independent of the main battery pipeline.
    /// </summary>
    D3BatterySet ReadD3Cells();
}

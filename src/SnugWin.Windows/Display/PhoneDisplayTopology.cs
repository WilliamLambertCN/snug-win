using System.Text.Json;
using Microsoft.Win32;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Display;

internal class PhoneDisplayTopology
{
    public virtual CherryInventory Inspect() => CherryMonitorIdentity.Inspect();
    public virtual IReadOnlyList<MonitorSnapshot> Active() => DisplayConfigService.GetActiveMonitors();

    public virtual IReadOnlyList<uint> LegacyProfileIds()
    {
        // Legacy restores this list before opening the pipe; a configured ID
        // can exist even if PnP has not finished exposing its monitor yet.
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VirtualDisplayDriver");
        if (key?.GetValue("data") is not string json) return [];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(m => m.GetProperty("id").GetUInt32()).ToArray();
    }
    public virtual string? Attach(CherryMonitor target, int width, int height) =>
        DisplayConfigService.TryAttachVirtualDisplay(target, width, height);

    public virtual bool SetResolution(MonitorSnapshot current, int width, int height, double hz) =>
        SamePhoneSource(current) && DisplayConfigService.SetResolution(current.DeviceName, width, height, hz);

    public virtual bool SetScale(MonitorSnapshot current, int percent) =>
        SamePhoneSource(current) && DisplayConfigService.SetDpiScalePercent(current.AdapterId, current.SourceId, percent);

    private bool SamePhoneSource(MonitorSnapshot snapshot)
    {
        if (snapshot.Cherry is not { } target) return true; // a separate, explicit OtherDisplays restore
        var now = Online(target, snapshot.DeviceName);
        return now?.AdapterId == snapshot.AdapterId && now.SourceId == snapshot.SourceId;
    }

    public MonitorSnapshot? Online(CherryMonitor target, string? expectedName = null)
    {
        var matches = Active().Where(m => m.Cherry == target).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var monitor = matches[0];
        if (!monitor.HasDedicatedTarget || monitor.IsPrimary) return null;
        return expectedName is null || monitor.DeviceName.Equals(expectedName, StringComparison.OrdinalIgnoreCase) ? monitor : null;
    }

    internal static bool IsRestorable(MonitorSnapshot m) =>
        m.HasDedicatedTarget && m.Cherry is null && m.AdapterHardwareId.Length != 0
        && !m.AdapterHardwareId.StartsWith(@"Root\", StringComparison.OrdinalIgnoreCase)
        && !m.AdapterHardwareId.Equals("usbmmidd", StringComparison.OrdinalIgnoreCase)
        && !m.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    public virtual void RestoreCursorIfStranded()
    {
        if (!Win32.GetCursorPos(out var pt)) return;
        var monitors = Active();
        if (monitors.Any(m => m.Bounds.Contains(new PointD(pt.X, pt.Y)))) return;
        if (monitors.FirstOrDefault(m => m.IsPrimary) is { } primary)
            Win32.SetCursorPos((int)Math.Round(primary.Bounds.MidX), (int)Math.Round(primary.Bounds.MidY));
    }
}

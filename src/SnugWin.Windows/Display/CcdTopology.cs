using System.Runtime.InteropServices;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Display;

internal sealed record CcdSource(Win32.LUID Adapter, uint Id, string FriendlyName, string TargetPath);

internal sealed unsafe record CcdTopology(Win32.DISPLAYCONFIG_PATH_INFO[] Paths, Win32.DISPLAYCONFIG_MODE_INFO[] Modes)
{
    public static CcdTopology? Query(uint flags)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.GetDisplayConfigBufferSizes(flags, out uint pc, out uint mc) != 0) break;
            var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new Win32.DISPLAYCONFIG_MODE_INFO[mc];
            int error = Win32.QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (error == 122) continue; // topology changed, resize both buffers
            if (error != 0) break;
            return new(paths[..(int)pc], modes[..(int)mc]);
        }
        return null;
    }

    public Dictionary<string, CcdSource> ActiveSources()
    {
        var result = new Dictionary<string, CcdSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Paths.Where(IsActive))
        {
            string source = SourceName(path.sourceInfo);
            var (friendly, target) = TargetName(path.targetInfo);
            // A cloned source is not a dedicated screen, even when one target is Cherry.
            if (result.ContainsKey(source)) target = string.Empty;
            result[source] = new(path.sourceInfo.adapterId, path.sourceInfo.id, friendly, target);
        }
        return result;
    }

    public string? Attach(CherryMonitor monitor, int width, int height)
    {
        var active = Paths.Where(IsActive).ToList();
        if (active.Count == 0 || active.Any(p => !HasSourceMode(p))) return null;
        var desired = FindDetachedPath(monitor, active);
        if (desired is not { } path) return null;
        string name = SourceName(path.sourceInfo);
        if (name.Length == 0) return null;

        var rightmost = active.Select(p => Modes[p.sourceInfo.modeInfoIdx].info.sourceMode)
            .MaxBy(s => (long)s.position.X + s.width);
        var position = new Win32.POINT { X = checked(rightmost.position.X + (int)rightmost.width), Y = rightmost.position.Y };
        var mode = new Win32.DISPLAYCONFIG_MODE_INFO
        {
            infoType = 1, // DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
            adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id,
            info = new() { sourceMode = new() { width = (uint)width, height = (uint)height, pixelFormat = 4, position = position } }
        };
        path.sourceInfo.modeInfoIdx = (uint)Modes.Length;
        path.targetInfo.modeInfoIdx = uint.MaxValue;
        path.flags |= Win32.DISPLAYCONFIG_PATH_ACTIVE;
        active.Add(path);
        // Keep every existing active path, mode and priority; do not allow Windows
        // to change other displays to make this request succeed.
        int error = Win32.SetDisplayConfig((uint)active.Count, active.ToArray(), (uint)Modes.Length + 1,
            [.. Modes, mode], Win32.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Win32.SDC_APPLY);
        if (error != 0) throw new PhoneScreenException(PhoneScreenErrorKind.CreateFailed, $"挂载 Cherry 显示器 #{monitor.DriverId} 失败（{error}）");
        return name;
    }

    private Win32.DISPLAYCONFIG_PATH_INFO? FindDetachedPath(CherryMonitor monitor, List<Win32.DISPLAYCONFIG_PATH_INFO> active)
    {
        // Inactive GDI sources can each enumerate *all* of an adapter's monitors.
        // Only an explicit CCD target path says which connector would be enabled.
        var inspected = new HashSet<(Win32.LUID, uint)>();
        foreach (var path in Paths)
        {
            if (IsActive(path) || path.targetInfo.targetAvailable == 0) continue;
            if (!path.sourceInfo.adapterId.Equals(path.targetInfo.adapterId)) continue;
            if (active.Any(p => SameSource(p.sourceInfo, path.sourceInfo) || SameTarget(p.targetInfo, path.targetInfo))) continue;
            if (!inspected.Add((path.targetInfo.adapterId, path.targetInfo.id))) continue;
            if (CherryMonitorIdentity.FromInterface(TargetName(path.targetInfo).Path) == monitor)
                return path;
        }
        return null;
    }

    private bool HasSourceMode(Win32.DISPLAYCONFIG_PATH_INFO path) =>
        path.sourceInfo.modeInfoIdx < Modes.Length && Modes[path.sourceInfo.modeInfoIdx].infoType == 1;

    private static bool IsActive(Win32.DISPLAYCONFIG_PATH_INFO p) => (p.flags & Win32.DISPLAYCONFIG_PATH_ACTIVE) != 0;

    private static bool SameSource(Win32.DISPLAYCONFIG_PATH_SOURCE_INFO a, Win32.DISPLAYCONFIG_PATH_SOURCE_INFO b) =>
        a.id == b.id && a.adapterId.Equals(b.adapterId);

    private static bool SameTarget(Win32.DISPLAYCONFIG_PATH_TARGET_INFO a, Win32.DISPLAYCONFIG_PATH_TARGET_INFO b) =>
        a.id == b.id && a.adapterId.Equals(b.adapterId);

    private static string SourceName(Win32.DISPLAYCONFIG_PATH_SOURCE_INFO source)
    {
        var packet = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new() { type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = source.adapterId, id = source.id }
        };
        return Win32.DisplayConfigGetDeviceInfo(ref packet) == 0 ? new string(packet.viewGdiDeviceName) : string.Empty;
    }

    private static (string Friendly, string Path) TargetName(Win32.DISPLAYCONFIG_PATH_TARGET_INFO target)
    {
        var packet = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new() { type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = target.adapterId, id = target.id }
        };
        return Win32.DisplayConfigGetDeviceInfo(ref packet) == 0
            ? (new string(packet.monitorFriendlyDeviceName), new string(packet.monitorDevicePath)) : (string.Empty, string.Empty);
    }
}

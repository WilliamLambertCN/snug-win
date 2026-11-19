using System.Collections.Concurrent;
using System.Diagnostics;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Display;

// A null state means the pipe has been negotiated as write-only legacy Add.
// On v2 Notify is a full replacement, including disabled monitors.
internal interface IVirtualDisplayState
{
    Task<IReadOnlyList<VddMonitor>?> ReadStateAsync(CancellationToken ct);
}

public sealed class PhoneDisplayService : IPhoneDisplays, IAsyncDisposable
{
    private sealed record Screen(CherryMonitor Target, ScreenFit Fit, string Name);

    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(8);
    private readonly IVirtualDisplayIpc _ipc;
    private readonly DisplayOwnership _ownership;
    private readonly Func<bool> _isPipeAvailable;
    private readonly PhoneDisplayTopology _topology;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly HashSet<uint> _unconfirmed = [];
    private readonly ConcurrentDictionary<uint, Screen> _ready = new();
    private readonly ConcurrentDictionary<uint, CherryMonitor> _retiring = new();

    public PhoneDisplayService(VirtualDisplayIpcClient? ipc = null, SnugPaths? paths = null)
        : this(ipc ?? new VirtualDisplayIpcClient(), paths, () => VirtualDisplayIpcClient.IsDriverPipeAvailable(), new()) { }

    internal PhoneDisplayService(IVirtualDisplayIpc ipc, SnugPaths? paths, Func<bool> isPipeAvailable,
        PhoneDisplayTopology topology, TimeSpan? timeout = null)
    {
        _ipc = ipc;
        _ownership = new(paths?.OwnedDisplaysFile);
        _isPipeAvailable = isPipeAvailable;
        _topology = topology;
        _timeout = timeout ?? OnlineTimeout;
    }

    public async Task CleanupOrphansAsync(CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isPipeAvailable()) return;
            foreach (var receipt in _ownership.All())
            {
                if (!_ready.ContainsKey(receipt.DriverId))
                    await RemoveOwnedAsync(receipt, ct).ConfigureAwait(false);
            }
        }
        finally { _changes.Release(); }
    }

    public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default) => CreateAsync(fit, null, ct);

    public async Task<uint> CreateAsync(ScreenFit fit, string? displayName, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isPipeAvailable()) throw Failed("未检测到 virtual-display-rs 驱动管道，未更改显示配置");
            var state = await DriverStateAsync(ct).ConfigureAwait(false);
            var before = await ReadBaselineAsync(ct).ConfigureAwait(false);
            var occupied = before.OccupiedIds.Concat(_ready.Keys).Concat(_retiring.Keys).Concat(_unconfirmed).ToHashSet();
            occupied.UnionWith(state is null ? _topology.LegacyProfileIds() : state.Select(m => m.Id));
            uint id = PhoneScreenSpec.CandidateIds.FirstOrDefault(i => !occupied.Contains(i), uint.MaxValue);
            if (id == uint.MaxValue) throw Failed("没有可安全使用的 Cherry 显示器编号");
            string name = string.IsNullOrWhiteSpace(displayName) ? PhoneScreenSpec.Name : displayName;
            var monitor = new VddMonitor(id, name, true, BuildModes(fit));
            ct.ThrowIfCancellationRequested();
            await SubmitAsync(monitor, state, replace: false, ct).ConfigureAwait(false);
            await CompleteArrivalAsync(monitor, fit, before, ct).ConfigureAwait(false);
            return id;
        }
        finally { _changes.Release(); }
    }

    private async Task<CherryInventory> ReadBaselineAsync(CancellationToken ct)
    {
        var known = new HashSet<Guid>();
        var occupied = new HashSet<uint>();
        CherryInventory? Sample()
        {
            var scan = _topology.Inspect();
            known.UnionWith(scan.KnownContainers);
            occupied.UnionWith(scan.OccupiedIds);
            if (scan.HasUnknownId) return null;
            scan.KnownContainers.UnionWith(known);
            scan.OccupiedIds.UnionWith(occupied);
            return scan;
        }
        return await PollAsync(Sample, _timeout, ct).ConfigureAwait(false)
            ?? throw Failed("仍有 Cherry 显示器的编号未知，未更改驱动配置");
    }

    private async Task<IReadOnlyList<VddMonitor>?> DriverStateAsync(CancellationToken ct)
    {
        if (_ipc is IVirtualDisplayState reader)
        {
            var state = await reader.ReadStateAsync(ct).ConfigureAwait(false);
            if (state is not null || _ipc.IsLegacyProtocol) return state;
        }
        else if (_ipc.IsLegacyProtocol) return null;
        throw Failed("无法确认驱动现有配置，为保护已有屏幕，未发送 Notify");
    }

    private async Task SubmitAsync(VddMonitor monitor, IReadOnlyList<VddMonitor>? state, bool replace, CancellationToken ct)
    {
        if (state is not null)
            state = await RefreshBeforeNotifyAsync(state, monitor.Id, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Once sent, finish the bounded write (including legacy Remove + Add).
        // Observe caller cancellation in the arrival/rollback path, not mid-write.
        using var write = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _unconfirmed.Add(monitor.Id); // the write-only driver may silently ignore an occupied slot
        if (replace && state is null)
            await _ipc.ReplaceLegacyAsync([monitor.Id], [monitor], write.Token).ConfigureAwait(false);
        else
            await _ipc.NotifyAsync(state is null ? [monitor] : [.. state.Where(m => m.Id != monitor.Id), monitor], write.Token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VddMonitor>> RefreshBeforeNotifyAsync(IReadOnlyList<VddMonitor> baseline, uint id, CancellationToken ct)
    {
        var latest = await DriverStateAsync(ct).ConfigureAwait(false) ?? throw Failed("驱动协议发生变化");
        var before = baseline.Where(m => m.Id == id).ToArray();
        var current = latest.Where(m => m.Id == id).ToArray();
        // Comparing the wire representation includes nested mode and refresh-rate lists.
        if (VirtualDisplayIpcClient.SerializeNotify(before) != VirtualDisplayIpcClient.SerializeNotify(current))
            throw Failed($"Cherry 显示器 #{id} 在建屏期间已被其他程序更改");
        return latest;
    }

    private async Task CompleteArrivalAsync(VddMonitor monitor, ScreenFit fit, CherryInventory before, CancellationToken ct)
    {
        CherryMonitor? proof = null;
        try
        {
            proof = await FindArrivalAsync(monitor, before, _timeout, ct).ConfigureAwait(false)
                ?? throw new PhoneScreenException(PhoneScreenErrorKind.NeverOnline);
            _unconfirmed.Remove(proof.DriverId);
            _ownership.Record(proof);
            await WaitForDesktopAsync(proof, fit, ct).ConfigureAwait(false);
            await ApplyWidthAsync(proof, fit, fit.BaseWidth, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ready[monitor.Id] = new(proof, fit, monitor.Name!);
            Log.Info($"Cherry 手机屏 #{monitor.Id} 已按 EDID 确认并切到 {fit}");
        }
        catch
        {
            await RollbackArrivalAsync(proof, monitor, before).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<CherryMonitor?> FindArrivalAsync(VddMonitor monitor, CherryInventory before, TimeSpan timeout, CancellationToken ct)
    {
        CherryMonitor? FreshTarget()
        {
            var current = _topology.Inspect().Unique(monitor.Id);
            return current is not null && !before.KnownContainers.Contains(current.ContainerId) ? current : null;
        }
        var target = await PollAsync(FreshTarget, timeout, ct).ConfigureAwait(false);
        if (target is null || _ipc.IsLegacyProtocol) return target;
        var state = await DriverStateAsync(ct).ConfigureAwait(false);
        if (state is null) return null;
        var actual = state.Where(m => m.Id == monitor.Id).ToArray();
        return VirtualDisplayIpcClient.SerializeNotify(actual) == VirtualDisplayIpcClient.SerializeNotify([monitor]) ? target : null;
    }

    private async Task RollbackArrivalAsync(CherryMonitor? proof, VddMonitor monitor, CherryInventory before)
    {
        try
        {
            // Cancellation can win just after the write, before PnP delivers the arrival.
            var grace = TimeSpan.FromMilliseconds(Math.Min(1000, _timeout.TotalMilliseconds));
            proof ??= await FindArrivalAsync(monitor, before, grace, CancellationToken.None).ConfigureAwait(false);
            if (proof is null) return; // a successful pipe write alone is not ownership
            _unconfirmed.Remove(proof.DriverId);
            _ownership.Record(proof);
            await RemoveOwnedAsync(proof, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn($"回滚未完成：{ex.Message}"); }
    }

    private async Task WaitForDesktopAsync(CherryMonitor target, ScreenFit fit, CancellationToken ct)
    {
        var (w, h) = fit.PixelSize(fit.BaseWidth);
        string? attachedName = null;
        var sinceAttempt = Stopwatch.StartNew();
        bool attempted = false;
        MonitorSnapshot? Observe()
        {
            var online = _topology.Online(target, attachedName);
            if (online is not null || attachedName is not null) return online;
            if (attempted && sinceAttempt.ElapsedMilliseconds < 500) return null;
            attempted = true;
            sinceAttempt.Restart();
            attachedName = _topology.Attach(target, w, h);
            return attachedName is null ? null : _topology.Online(target, attachedName);
        }
        if (await PollAsync(Observe, _timeout, ct).ConfigureAwait(false) is null)
            throw new PhoneScreenException(PhoneScreenErrorKind.NeverOnline);
    }

    public async Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ready.TryGetValue(displayId, out var old)) return false;
            var state = await DriverStateAsync(ct).ConfigureAwait(false);
            var before = await ReadBaselineAsync(ct).ConfigureAwait(false);
            if (before.Unique(displayId) != old.Target || _topology.Online(old.Target) is null) return false;
            if (state is not null && !state.Any(m => m.Id == displayId && m.Enabled)) return false;
            if (SameModes(old.Fit, fit))
            {
                await ApplyWidthAsync(old.Target, fit, fit.BaseWidth, ct).ConfigureAwait(false);
                _ready[displayId] = old with { Fit = fit };
            }
            else await ReplaceScreenAsync(old, fit, state, before, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Cherry 手机屏 #{displayId} 换比例失败：{ex.Message}");
            return false;
        }
        finally { _changes.Release(); }
    }

    private async Task ReplaceScreenAsync(Screen old, ScreenFit fit, IReadOnlyList<VddMonitor>? state, CherryInventory before, CancellationToken ct)
    {
        uint id = old.Target.DriverId;
        var monitor = new VddMonitor(id, old.Name, true, BuildModes(fit));
        _ready.TryRemove(id, out _);
        try
        {
            await SubmitAsync(monitor, state, replace: true, ct).ConfigureAwait(false);
            await CompleteArrivalAsync(monitor, fit, before, ct).ConfigureAwait(false);
            _ownership.Release(old.Target);
        }
        catch
        {
            if (_topology.Online(old.Target) is { } previous && WidthIn(old.Fit, previous) is not null)
                _ready[id] = old;
            else
                await RemoveOwnedAsync(old.Target, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready.TryGetValue(displayId, out var screen))
            {
                _retiring[displayId] = screen.Target;
                _ready.TryRemove(displayId, out _);
            }
            if (!_retiring.TryGetValue(displayId, out var target)) return false;
            if (!await RemoveOwnedAsync(target, ct).ConfigureAwait(false)) return false;
            _retiring.TryRemove(displayId, out _);
            _topology.RestoreCursorIfStranded();
            return true;
        }
        finally { _changes.Release(); }
    }

    private async Task<bool> RemoveOwnedAsync(CherryMonitor target, CancellationToken ct)
    {
        try
        {
            // Connect before rechecking the receipt, not after a potentially slow pipe negotiation.
            var state = await DriverStateAsync(ct).ConfigureAwait(false);
            var inventory = _topology.Inspect();
            if (inventory.Unique(target.DriverId) == target)
            {
                if (state is not null && !state.Any(m => m.Id == target.DriverId && m.Enabled)) return false;
                await _ipc.RemoveAsync([target.DriverId], ct).ConfigureAwait(false);
                CherryMonitor? Gone() => MayStillExist(_topology.Inspect(), target) ? null : target;
                if (await PollAsync(Gone, _timeout, ct).ConfigureAwait(false) is null) return false;
            }
            else if (MayStillExist(inventory, target)) return false;
            _ownership.Release(target);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"未确认 Cherry 手机屏 #{target.DriverId} 已移除：{ex.Message}");
            return false;
        }
    }

    public ScreenFit? FitOf(uint displayId) => Confirmed(displayId)?.Fit;

    public bool IsOnline(uint displayId)
    {
        var target = _ready.GetValueOrDefault(displayId)?.Target ?? _retiring.GetValueOrDefault(displayId);
        return target is not null && MayStillExist(_topology.Inspect(), target);
    }

    private bool MayStillExist(CherryInventory inventory, CherryMonitor target) =>
        inventory.MayContain(target) || _topology.Active().Any(m => m.Cherry == target);

    public async Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_ready.TryGetValue(displayId, out var screen)) throw Failed($"手机屏 #{displayId} 未就绪");
            await ApplyWidthAsync(screen.Target, screen.Fit, width, ct).ConfigureAwait(false);
        }
        finally { _changes.Release(); }
    }

    private async Task ApplyWidthAsync(CherryMonitor target, ScreenFit fit, int width, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (w, h) = fit.PixelSize(width);
        var current = _topology.Online(target) ?? throw Failed($"未找到独立的 Cherry 手机屏 #{target.DriverId}");
        bool Sized(MonitorSnapshot? m) => m?.Mode.Width == w && m.Mode.Height == h;
        MonitorSnapshot? ReadSize() => _topology.Online(target) is { } m && Sized(m) ? m : null;
        if (!Sized(current))
        {
            if (!_topology.SetResolution(current, w, h, 60)) throw Failed($"Cherry 手机屏 #{target.DriverId} 切换 {w}x{h} 失败");
            current = await PollAsync(ReadSize, _timeout, ct).ConfigureAwait(false) ?? throw Failed($"未确认手机屏已切换 {w}x{h}");
        }
        await ApplyScaleAsync(current, fit.TargetDpiPercent(width), ReadSize, ct).ConfigureAwait(false);
    }

    private async Task ApplyScaleAsync(MonitorSnapshot current, int requested, Func<MonitorSnapshot?> readSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int dpi = current.ResolveDpiScalePercent(requested) ?? throw Failed("无法确认手机屏支持的缩放档位");
        if (dpi != requested) Log.Info($"Cherry 手机屏 #{current.Cherry!.DriverId} 请求 {requested}% 缩放，最接近的支持档位为 {dpi}%");
        if (current.Mode.DpiScalePercent == dpi) return;
        if (!_topology.SetScale(current, dpi)) throw Failed($"Cherry 手机屏 #{current.Cherry!.DriverId} 设置 {dpi}% 缩放失败");
        MonitorSnapshot? ReadDpi() => readSize() is { } m && m.Mode.DpiScalePercent == dpi ? m : null;
        if (await PollAsync(ReadDpi, _timeout, ct).ConfigureAwait(false) is null) throw Failed($"未确认手机屏缩放为 {dpi}%");
    }

    private (MonitorSnapshot Monitor, ScreenFit Fit, int Width)? Confirmed(uint id)
    {
        if (!_ready.TryGetValue(id, out var screen) || _topology.Online(screen.Target) is not { } mon) return null;
        return WidthIn(screen.Fit, mon) is { } width ? (mon, screen.Fit, width) : null;
    }

    private static int? WidthIn(ScreenFit fit, MonitorSnapshot mon)
    {
        foreach (int width in fit.Widths)
            if (fit.PixelSize(width) == (mon.Mode.Width, mon.Mode.Height)) return width;
        return null;
    }

    public int? WidthOf(uint displayId) => Confirmed(displayId)?.Width;
    public RectD? BoundsOf(uint displayId) => Confirmed(displayId)?.Monitor.Bounds;

    public RectD? WindowAreaOf(uint displayId) => Confirmed(displayId) is { } screen ? WindowArea(screen) : null;

    public RectD? HomeBarAreaOf(uint displayId)
    {
        if (Confirmed(displayId) is not { } screen || screen.Fit.Kind == DeviceKind.Computer) return null;
        var work = screen.Monitor.WorkArea;
        var area = WindowArea(screen);
        return new RectD(work.X, area.MaxY, work.Width, Math.Max(28, work.MaxY - area.MaxY));
    }

    private static RectD WindowArea((MonitorSnapshot Monitor, ScreenFit Fit, int Width) screen)
    {
        var work = screen.Monitor.WorkArea;
        if (screen.Fit.Kind == DeviceKind.Computer) return work;
        double scale = Math.Max(1.0, screen.Monitor.Mode.Width / (double)screen.Width);
        double bar = Math.Round(ScreenFit.HomeBarHeight(screen.Fit.Size(screen.Width)) * scale);
        return new(work.X, work.Y, work.Width, Math.Max(100, work.Height - bar));
    }

    public RectD? MainArea()
    {
        var others = _topology.Active().Where(m => m.Cherry is null).ToList();
        return (others.FirstOrDefault(m => m.IsPrimary) ?? others.FirstOrDefault())?.WorkArea;
    }

    public IReadOnlyList<OtherDisplay> OtherDisplays() => _topology.Active().Where(PhoneDisplayTopology.IsRestorable)
        .Select(m => new OtherDisplay(m.StableId, m.DeviceName, m.FriendlyName, m.Mode)).ToList();

    public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MonitorSnapshot? Other() => _topology.Active().FirstOrDefault(m => m.StableId == displayId && PhoneDisplayTopology.IsRestorable(m));
        if (Other() is not { } target) return Task.CompletedTask;
        if (target.Mode.Width != mode.Width || target.Mode.Height != mode.Height || Math.Abs(target.Mode.RefreshRate - mode.RefreshRate) >= 1.0)
            _topology.SetResolution(target, mode.Width, mode.Height, mode.RefreshRate);
        if (Other() is { } after && after.Mode.DpiScalePercent != mode.DpiScalePercent)
            _topology.SetScale(after, mode.DpiScalePercent);
        return Task.CompletedTask;
    }

    private static List<VddMode> BuildModes(ScreenFit fit) => fit.RegistrationOrder.Select(w =>
    {
        var (pw, ph) = fit.PixelSize(w);
        return new VddMode((uint)pw, (uint)ph, [60]);
    }).ToList();

    private static bool SameModes(ScreenFit first, ScreenFit second) =>
        first.RegistrationOrder.Select(first.PixelSize).SequenceEqual(second.RegistrationOrder.Select(second.PixelSize));

    private static PhoneScreenException Failed(string detail) => new(PhoneScreenErrorKind.CreateFailed, detail);

    private static async Task<T?> PollAsync<T>(Func<T?> read, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (read() is { } result) return result;
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(remaining.TotalMilliseconds < 100 ? remaining : TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _ipc.DisposeAsync();
}

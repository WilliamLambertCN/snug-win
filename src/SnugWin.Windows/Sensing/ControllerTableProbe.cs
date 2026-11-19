using System;
using System.Threading.Tasks;
using SnugWin.Core.Session;

namespace SnugWin.Windows.Sensing;

// Polled by the serialized sensor loop. A stalled UIA provider keeps its one slot, even on PID changes.
internal sealed class ControllerTableProbe(Func<int, ControllerTable> read, Func<DateTimeOffset>? utcNow = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private Task<(ControllerTable Table, DateTimeOffset ExpiresAt)>? _pending;
    private (ControllerTable Table, DateTimeOffset ExpiresAt)? _latest;
    private int? _readPid;
    private DateTimeOffset _nextReadAt;

    public ControllerTable Poll(int? clientPid, DateTimeOffset now)
    {
        if (_readPid != clientPid)
        {
            _readPid = null;
            _latest = default;
            _nextReadAt = default;
        }

        if (_pending is { IsCompleted: true } completed)
        {
            if (_readPid.HasValue)
                _latest = completed.Result;
            _pending = null;
        }

        if (_pending is null && clientPid is { } pid && now >= _nextReadAt)
        {
            _readPid = pid;
            _nextReadAt = now + RefreshInterval;
            _pending = Task.Run(() => (ReadOrUnreadable(pid), _utcNow() + MaxAge));
        }
        return _latest is { } last && now < last.ExpiresAt ? last.Table : ControllerTable.Unreadable;
    }

    private ControllerTable ReadOrUnreadable(int pid)
    {
        try { return read(pid); }
        catch (Exception) { return ControllerTable.Unreadable; }
    }
}

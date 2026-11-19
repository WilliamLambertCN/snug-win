using System;

namespace SnugWin.Core.Session;

/// <summary>
/// Tracks GameViewerServer streamer log byte growth to distinguish active remote streaming
/// from idle/post-disconnect control sockets or single isolated heartbeat flushes.
/// </summary>
public sealed class StreamLogActivityTracker
{
    public static readonly TimeSpan AdvancePairMaxGap = TimeSpan.FromSeconds(3.0);
    public static readonly TimeSpan ActiveHoldWindow = TimeSpan.FromSeconds(2.0);

    private int? _serverPid;
    private long? _lastBytes;
    private DateTimeOffset _lastReadAt;
    private DateTimeOffset? _lastAdvance;
    private bool _continuous;

    public void Reset()
    {
        _serverPid = null;
        _lastBytes = null;
        _lastReadAt = default;
        _lastAdvance = null;
        _continuous = false;
    }

    public bool? Observe(int? serverPid, long? totalBytes, bool logFilesExist, DateTimeOffset now, bool wasConnected = false)
    {
        if (serverPid is null || !logFilesExist || totalBytes is not { } bytes)
        {
            Reset();
            return serverPid is null ? false : null;
        }

        if (serverPid != _serverPid || bytes < _lastBytes || now < _lastReadAt)
            Reset();
        _serverPid = serverPid;
        return ObserveBytes(bytes, now, wasConnected);
    }

    private bool? ObserveBytes(long bytes, DateTimeOffset now, bool wasConnected)
    {
        var previous = _lastBytes;
        var sampleGap = now - _lastReadAt;
        _lastBytes = bytes;
        _lastReadAt = now;
        if (previous is null || bytes == previous)
            return _continuous && now - _lastAdvance < ActiveHoldWindow;

        if (sampleGap > AdvancePairMaxGap)
        {
            // Growth somewhere in a missed interval can sustain a connection, but cannot prove a new one.
            _continuous = wasConnected;
            _lastAdvance = wasConnected ? now : null;
            return wasConnected ? true : null;
        }

        _continuous = _lastAdvance is { } last && now - last <= AdvancePairMaxGap;
        _lastAdvance = now;
        return _continuous;
    }
}

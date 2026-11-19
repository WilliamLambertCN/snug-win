using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Session;
using SnugWin.Windows.Sensing;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class SessionSamplingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void DelayedGrowthKeepsAnExistingSessionUntilTheLogStops()
    {
        var session = new RemoteSession();
        var log = new StreamLogActivityTracker();
        Assert.IsType<SessionTransition.Began>(Assert.Single(Sample(session, log, 0, 100, ControllerTable.FromNames("Phone"))));
        Assert.Empty(Sample(session, log, 12, 200));
        Assert.Empty(Sample(session, log, 14, 250));
        Assert.Empty(Sample(session, log, 26, 300));
        Assert.True(session.IsConnected);

        Assert.Empty(Sample(session, log, 27.5, 300));
        Assert.IsType<SessionTransition.Interrupted>(Assert.Single(Sample(session, log, 30, 300)));
        Assert.IsType<SessionTransition.Ended>(Assert.Single(Sample(session, log, 46, 300)));
        Assert.False(session.InEpisode);
    }

    [Fact]
    public void SparseWritesDoNotStartStreamingOrPairWithOneFreshHeartbeat()
    {
        var log = new StreamLogActivityTracker();
        Assert.False(log.Observe(42, 100, true, Start));
        Assert.Null(log.Observe(42, 200, true, Start.AddSeconds(12)));
        Assert.Null(log.Observe(42, 300, true, Start.AddSeconds(24)));
        Assert.False(log.Observe(42, 400, true, Start.AddSeconds(24.5)));
        Assert.True(log.Observe(42, 500, true, Start.AddSeconds(25)));
        Assert.False(log.Observe(42, 500, true, Start.AddSeconds(37), wasConnected: true));
    }

    [Theory]
    [InlineData(77, 1200, false)] // new PID, larger total
    [InlineData(42, 10, false)]   // truncate in place
    [InlineData(42, 1200, true)]  // files disappear during rotation
    public void ChangedLogBaselineRequiresTwoNewAdvances(int pid, long bytes, bool missing)
    {
        var log = new StreamLogActivityTracker();
        log.Observe(42, 100, true, Start);
        log.Observe(42, 200, true, Start.AddSeconds(0.5));
        Assert.True(log.Observe(42, 300, true, Start.AddSeconds(1)));
        if (missing) Assert.Null(log.Observe(pid, null, false, Start.AddSeconds(1.5), wasConnected: true));
        Assert.False(log.Observe(pid, bytes, true, Start.AddSeconds(2), wasConnected: true));
        Assert.False(log.Observe(pid, bytes + 1, true, Start.AddSeconds(2.5)));
        Assert.True(log.Observe(pid, bytes + 2, true, Start.AddSeconds(3)));
    }

    [Fact]
    public async Task StalledControllerReadDoesNotBlockLogSamplingOrQueueMoreReads()
    {
        int reads = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ControllerTable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ControllerTableProbe(_ =>
        {
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            return release.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        });
        try
        {
            var session = new RemoteSession();
            var log = new StreamLogActivityTracker();
            using var file = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.Write,
                FileShare.Read, bufferSize: 1, FileOptions.DeleteOnClose);
            Assert.Empty(Sample(session, log, 0, file.Length, probe.Poll(42, Start)));
            await entered.Task.WaitAsync(TestTimeout);
            for (int tick = 1; tick <= 24; tick++)
            {
                double seconds = tick * 0.5;
                var controllers = probe.Poll(tick < 12 ? 42 : 77, Start.AddSeconds(seconds));
                Assert.Same(ControllerTable.Unreadable, controllers);
                file.WriteByte(1);
                Sample(session, log, seconds, file.Length, controllers);
                Assert.Equal(tick >= 2, session.IsConnected);
            }
            var noControllers = probe.Poll(77, Start.AddSeconds(16));
            Assert.IsType<SessionTransition.Interrupted>(Assert.Single(Sample(session, log, 16, file.Length, noControllers)));
            Assert.IsType<SessionTransition.Ended>(Assert.Single(Sample(session, log, 32, file.Length, noControllers)));
            Assert.Equal(1, Volatile.Read(ref reads));
        }
        finally
        {
            release.TrySetResult(ControllerTable.Unreadable);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void SlowControllerReadsPublishFreshNamesWithoutInterruptingTheSession(int duration)
    {
        var session = new RemoteSession();
        var log = new StreamLogActivityTracker();
        TaskCompletionSource<ControllerTable>[] reads = [new(), new(), new()];
        int index = -1, completedAt = 0;
        var probe = new ControllerTableProbe(
            _ => reads[Interlocked.Increment(ref index)].Task.WaitAsync(TestTimeout).GetAwaiter().GetResult(),
            () => Start.AddSeconds(Volatile.Read(ref completedAt)));
        try
        {
            Assert.Same(ControllerTable.Unreadable, probe.Poll(42, Start));
            for (int i = 0; i < 2; i++)
            {
                int finish = (i + 1) * duration;
                var table = ControllerTable.FromNames("Phone");
                Volatile.Write(ref completedAt, finish);
                reads[i].SetResult(table);
                Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(probe.Poll(42, Start.AddSeconds(finish)), table), TestTimeout));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref index) == i + 1, TestTimeout));
                Sample(session, log, finish, 100, table);
                Assert.True(session.IsConnected);
                for (double t = finish + 0.5; t < finish + duration; t += 0.5)
                    Assert.Empty(Sample(session, log, t, 100, probe.Poll(42, Start.AddSeconds(t))));
            }

            int expired = 2 * duration + 5;
            var stale = probe.Poll(42, Start.AddSeconds(expired));
            Assert.Same(ControllerTable.Unreadable, stale);
            Assert.IsType<SessionTransition.Interrupted>(Assert.Single(Sample(session, log, expired, 100, stale)));
        }
        finally
        {
            foreach (var read in reads) read.TrySetResult(ControllerTable.Unreadable);
        }
    }

    [Fact]
    public void LatePollingDoesNotRenewAnAlreadyExpiredResult()
    {
        int reads = 0;
        var probe = new ControllerTableProbe(_ =>
        {
            Interlocked.Increment(ref reads);
            return ControllerTable.FromNames("Phone");
        }, () => Start.AddSeconds(2));
        Assert.Same(ControllerTable.Unreadable, probe.Poll(42, Start));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            Assert.Same(ControllerTable.Unreadable, probe.Poll(42, Start.AddSeconds(12)));
            return Volatile.Read(ref reads) == 2;
        }, TestTimeout));
    }

    [Fact]
    public void CachedControllerNamesExpireAndDoNotCarryOverToAnotherProcess()
    {
        var table = ControllerTable.FromNames("Phone");
        var release = new TaskCompletionSource<ControllerTable>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var completedAt = Start;
        var changed = Start.AddSeconds(0.5);
        var probe = new ControllerTableProbe(_ => Interlocked.Increment(ref reads) <= 2
            ? table : release.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult(), () => completedAt);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => probe.Poll(42, Start) == table, TestTimeout));
            completedAt = changed;
            Assert.Same(ControllerTable.Unreadable, probe.Poll(77, changed));
            Assert.True(SpinWait.SpinUntil(() => probe.Poll(77, changed) == table, TestTimeout));
            Assert.Same(table, probe.Poll(77, changed.AddSeconds(2)));
            Assert.Same(ControllerTable.Unreadable, probe.Poll(77, changed.AddSeconds(12)));
        }
        finally
        {
            release.TrySetResult(ControllerTable.Unreadable);
        }
    }

    private static IReadOnlyList<SessionTransition> Sample(RemoteSession session, StreamLogActivityTracker log,
        double seconds, long bytes, ControllerTable? controllers = null)
    {
        var now = Start.AddSeconds(seconds);
        var active = log.Observe(42, bytes, true, now, session.IsConnected);
        return session.Update(new UUSignals(false, controllers ?? ControllerTable.Unreadable, active), now);
    }
}

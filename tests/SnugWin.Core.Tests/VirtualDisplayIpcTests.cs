using System.IO;
using System.IO.Pipes;
using System.Text;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class VirtualDisplayIpcTests
{
    private const string Eot = "\u0004";
    private const string Changed = """{"Changed":[]}""" + Eot;
    private const string EmptyState = """{"State":[]}""" + Eot;
    private const string SavedState = """{"State":[{"id":7,"name":"已有屏","enabled":false,"modes":[{"width":800,"height":600,"refresh_rates":[60,75]}]}]}""";

    [Fact]
    public async Task InboundPipe_FirstQueryNegotiatesLegacyWithoutSendingState()
    {
        await using var pipes = new PipeFixture(PipeDirection.In);
        IVirtualDisplayState reader = pipes.Client;
        var query = reader.ReadStateAsync(pipes.Token);
        await pipes.Server.WaitForConnectionAsync(pipes.Token);
        Assert.Null(await query);
        Assert.True(pipes.Client.IsLegacyProtocol);

        VddMonitor[] monitors = [new(3, null, true, [new(800, 600, [60])])];
        var add = pipes.Client.NotifyAsync(monitors, pipes.Token);
        await pipes.ExpectLegacyMessageAsync(VirtualDisplayIpcClient.SerializeLegacyAdd(monitors));
        await add;
        var replace = pipes.Client.ReplaceLegacyAsync([3], monitors, pipes.Token);
        await pipes.ExpectLegacyMessageAsync("""{"Remove":[3]}""");
        await pipes.ExpectLegacyMessageAsync(VirtualDisplayIpcClient.SerializeLegacyAdd(monitors));
        await replace;
    }

    [Fact]
    public async Task DuplexPipe_IgnoresChangedAndKeepsPartialNextFrameAcrossQueries()
    {
        await using var pipes = new PipeFixture();
        var first = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(Changed + SavedState + Eot + "{\"Cha");
        var monitor = Assert.Single((await first)!);
        Assert.False(pipes.Client.IsLegacyProtocol);
        Assert.Equal(7u, monitor.Id);
        Assert.False(monitor.Enabled);
        Assert.Equal("已有屏", monitor.Name);
        var mode = Assert.Single(monitor.Modes);
        Assert.Equal((800u, 600u), (mode.Width, mode.Height));
        Assert.Equal(new uint[] { 60, 75 }, mode.RefreshRates);

        var second = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync("nged\":[]}" + Eot + EmptyState);
        Assert.Empty((await second)!);
    }

    [Fact]
    public async Task DuplexPipe_RejectsLegacyReplaceWithoutWritingAnyFrame()
    {
        await using var pipes = new PipeFixture();
        var replace = pipes.Client.ReplaceLegacyAsync([3], [new(3, null, true, [])], pipes.Token);
        await pipes.Server.WaitForConnectionAsync(pipes.Token);
        await Assert.ThrowsAsync<NotSupportedException>(() => replace);

        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(SavedState + Eot);
        Assert.Equal(7u, Assert.Single((await query)!).Id);
    }

    [Fact]
    public async Task DuplexPipe_DrainsChangedDuringLargeNotifyAndWhileIdle()
    {
        await using var pipes = new PipeFixture();
        string backlog = string.Concat(Enumerable.Repeat(Changed, 5000));
        VddMonitor[] monitors = [new(3, new string('x', 64 * 1024), true, [])];
        var notify = pipes.Client.NotifyAsync(monitors, pipes.Token);
        await pipes.Server.WaitForConnectionAsync(pipes.Token);
        // The driver cannot read Notify until its own write (larger than the 4 KiB buffer) finishes.
        await pipes.WriteAsync(backlog);
        await pipes.ExpectBytesAsync(VirtualDisplayIpcClient.SerializeNotify(monitors) + Eot);
        await notify;

        await pipes.WriteAsync(EmptyState + backlog);
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(Changed + SavedState + Eot);
        Assert.Equal(7u, Assert.Single((await query)!).Id);
    }

    [Fact]
    public async Task Query_DoesNotClaimAStateFrameThatStartedWhileIdle()
    {
        await using var pipes = new PipeFixture();
        var first = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(EmptyState);
        Assert.Empty((await first)!);
        await pipes.WriteAsync("{\"State\":" + new string(' ', 64 * 1024));

        var second = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync("[]}" + Eot + SavedState + Eot);
        Assert.Equal(7u, Assert.Single((await second)!).Id);
    }

    [Fact]
    public async Task BadFrameWhileIdle_ClosesPipeBeforeNextCall()
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(EmptyState);
        Assert.Empty((await query)!);
        await pipes.WriteAsync("bad" + Eot);
        await pipes.AssertReconnectAsync();
    }

    [Fact]
    public async Task DisposeWithPendingQuery_StopsReaderAndReleasesCaller()
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync("{\"Chan");
        await pipes.Client.DisposeAsync().AsTask().WaitAsync(pipes.Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => query);
        Assert.Equal(0, await pipes.Server.ReadAsync(new byte[1], pipes.Token));
    }

    [Fact]
    public async Task FragmentedState_ReadsAcrossBufferAndUtf8Boundaries()
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        string name = new('屏', 3000);
        byte[] frame = Encoding.UTF8.GetBytes(SavedState.Replace("已有屏", name) + Eot);
        int split = Array.IndexOf(frame, Encoding.UTF8.GetBytes("屏")[0]) + 1;
        await pipes.Server.WriteAsync(frame.AsMemory(0, split), pipes.Token);
        await pipes.Server.WriteAsync(frame.AsMemory(split), pipes.Token);
        Assert.Equal(name, Assert.Single((await query)!).Name);
    }

    [Fact]
    public async Task MissingEot_TimesOutInsteadOfReturningChangedOrEmptyState()
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(Changed + """{"State":[]}""");
        await Assert.ThrowsAsync<TimeoutException>(() => query);
        await pipes.AssertReconnectAsync();
    }

    [Fact]
    public async Task CancellationAfterRequest_DiscardsPartialReplyAndReconnects()
    {
        await using var pipes = new PipeFixture();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(pipes.Token);
        var query = pipes.Client.ReadStateAsync(cancel.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(Changed + "{\"Sta");
        cancel.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Equal(cancel.Token, ex.CancellationToken);
        await pipes.AssertReconnectAsync();
    }

    [Fact]
    public async Task CancellationWaitingForGate_DoesNotInterruptActiveQueryOrQueuedWrite()
    {
        await using var pipes = new PipeFixture();
        var active = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(pipes.Token);
        var waiting = pipes.Client.ReadStateAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var write = pipes.Client.NotifyAsync([], pipes.Token);
        Assert.False(write.IsCompleted);
        await pipes.WriteAsync(EmptyState);
        Assert.Empty((await active)!);
        await pipes.ExpectBytesAsync("""{"Notify":[]}""" + Eot);
        await write;
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("""{"State":null}""")]
    [InlineData("""{"State":{}}""")]
    [InlineData("""{"State":[null]}""")]
    [InlineData("""{"State":[{"enabled":false,"modes":[]}]}""")]
    [InlineData("""{"State":[{"id":0,"enabled":true,"modes":null}]}""")]
    [InlineData("""{"State":[{"id":0,"enabled":true,"modes":[null]}]}""")]
    [InlineData("""{"State":[{"id":0,"enabled":true,"modes":[{"width":800,"height":600}]}]}""")]
    [InlineData("""{"State":[{"id":0,"enabled":true,"modes":[],"unknown":1}]}""")]
    [InlineData("""{"State":[],"Changed":[]}""")]
    public async Task InvalidFrame_FailsClosedEvenWhenFollowedByAValidFrame(string invalid)
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync(invalid + Eot + EmptyState);
        await Assert.ThrowsAsync<InvalidDataException>(() => query);
        Assert.False(VirtualDisplayIpcClient.TryParseStateReply(invalid, out _));
        await pipes.AssertReconnectAsync();
    }

    [Fact]
    public void ParseHelper_AcceptsChangedForExistingCallersAndRejectsInvalidText()
    {
        string changed = SavedState.Replace("\"State\"", "\"Changed\"");
        Assert.True(VirtualDisplayIpcClient.TryParseStateReply(changed, out var monitors));
        Assert.Equal(7u, Assert.Single(monitors).Id);
        Assert.False(VirtualDisplayIpcClient.TryParseStateReply(SavedState.Replace("已有屏", "\ud800"), out _));
    }

    [Fact]
    public async Task UnterminatedOversizedFrame_IsRejectedWithoutWaitingForEot()
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        byte[] body = new byte[1024 * 1024 + 1];
        Array.Fill(body, (byte)' ');
        await pipes.Server.WriteAsync(body, pipes.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => query);
        await pipes.AssertReconnectAsync();
    }

    [Fact]
    public async Task DisconnectMidFrame_IsNotAnEmptyState()
    {
        await using var pipes = new PipeFixture();
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.ExpectStateRequestAsync();
        await pipes.WriteAsync("{\"State\":");
        pipes.Server.Dispose();
        await Assert.ThrowsAnyAsync<IOException>(() => query);
        await pipes.AssertReconnectAsync();
    }

    [Fact]
    public async Task FailedLegacyWrite_IsNotReplayedAndNextCallNegotiatesAgain()
    {
        await using var pipes = new PipeFixture(PipeDirection.In);
        var query = pipes.Client.ReadStateAsync(pipes.Token);
        await pipes.Server.WaitForConnectionAsync(pipes.Token);
        Assert.Null(await query);
        pipes.Server.Dispose();
        await Assert.ThrowsAnyAsync<IOException>(() => pipes.Client.RemoveAsync([3], pipes.Token));
        await pipes.AssertReconnectAsync();
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        private readonly string _name = $"snugwin-ipc-test-{Guid.NewGuid():N}";
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        public NamedPipeServerStream Server { get; private set; }
        public VirtualDisplayIpcClient Client { get; }
        public CancellationToken Token => _deadline.Token;

        public PipeFixture(PipeDirection direction = PipeDirection.InOut)
        {
            Server = Listen(direction);
            Client = new(_name);
        }

        private NamedPipeServerStream Listen(PipeDirection direction) =>
            new(_name, direction, 1, direction == PipeDirection.In ? PipeTransmissionMode.Message : PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096);

        public async Task ExpectStateRequestAsync()
        {
            if (!Server.IsConnected) await Server.WaitForConnectionAsync(Token);
            await ExpectBytesAsync("\"State\"" + Eot);
        }

        public async Task ExpectBytesAsync(string expected)
        {
            byte[] actual = new byte[Encoding.UTF8.GetByteCount(expected)];
            await Server.ReadExactlyAsync(actual, Token);
            Assert.Equal(expected, Encoding.UTF8.GetString(actual));
        }

        public async Task ExpectLegacyMessageAsync(string expected)
        {
            byte[] buffer = new byte[4096];
            int count = await Server.ReadAsync(buffer, Token);
            Assert.True(Server.IsMessageComplete);
            Assert.Equal(expected, Encoding.UTF8.GetString(buffer, 0, count));
        }

        public ValueTask WriteAsync(string wireText) => Server.WriteAsync(Encoding.UTF8.GetBytes(wireText), Token);

        public async Task AssertReconnectAsync()
        {
            if (Server.IsConnected) Assert.Equal(0, await Server.ReadAsync(new byte[1], Token));
            Server.Dispose();
            Server = Listen(PipeDirection.InOut);
            var next = Client.ReadStateAsync(Token);
            await ExpectStateRequestAsync();
            await WriteAsync(SavedState + Eot);
            Assert.Equal(7u, Assert.Single((await next)!).Id);
        }

        public async ValueTask DisposeAsync()
        {
            Server.Dispose();
            await Client.DisposeAsync();
            _deadline.Dispose();
        }
    }
}

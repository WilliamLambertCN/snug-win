using System.Buffers.Binary;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;
using static SnugWin.Core.Tests.DisplayOwnershipFixture;

namespace SnugWin.Core.Tests;

public sealed class DisplayOwnershipSafetyTests
{
    private static readonly ScreenFit Fit = new(954.0 / 540);

    [Fact]
    public async Task Create_BindsNewId1NotFirstReappearingSourceOrDetachedId0()
    {
        var rig = new DisplayOwnershipFixture();
        var old = rig.Plug(0);
        rig.AttachAs = target =>
        {
            rig.Desktop.Add(Monitor(@"\\.\DISPLAY1", primary: true));
            rig.Desktop.Add(Monitor(@"\\.\DISPLAY68", adapter: @"Root\GameViewerIddDriver"));
            rig.Desktop.Add(Monitor(@"\\.\DISPLAY78", old));
            rig.Desktop.Add(Monitor(@"\\.\DISPLAY79", target));
            return @"\\.\DISPLAY79";
        };
        var svc = rig.Service();
        Assert.Equal(1u, await svc.CreateAsync(Fit));
        Assert.Equal([1u], rig.Adds);
        Assert.Equal(1u, Assert.Single(rig.Attachments).DriverId);
        Assert.Equal([@"\\.\DISPLAY79", @"\\.\DISPLAY79"], rig.Writes);
        Assert.NotNull(svc.BoundsOf(1));
        Assert.False(await svc.RetireAsync(0));
        Assert.True(await svc.RetireAsync(1));
        Assert.Equal([1u], rig.Removes);
        Assert.Equal(old, Assert.Single(rig.Present));
    }

    [Fact]
    public async Task ReuseOfGdiNameOrDriverId_IsNotOwnership()
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(Fit);
        string oldName = Assert.Single(rig.Desktop).DeviceName;
        rig.Unplug(id);
        rig.Desktop.Add(Monitor(oldName, primary: true));
        var user = rig.Plug(id);
        rig.Desktop.Add(Monitor("another name", user));
        int writes = rig.Writes.Count;
        Assert.False(svc.IsOnline(id));
        Assert.Null(svc.BoundsOf(id));
        Assert.Null(svc.WindowAreaOf(id));
        Assert.Null(svc.HomeBarAreaOf(id));
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.SetWidthAsync(id, Fit.BaseWidth));
        Assert.False(await svc.ReshapeAsync(id, new ScreenFit(2)));
        Assert.Equal(writes, rig.Writes.Count);
        Assert.True(await svc.RetireAsync(id));
        Assert.Empty(rig.Removes);
    }

    [Theory]
    [InlineData(@"Root\GameViewerIddDriver")]
    [InlineData(@"Root\OrayIddDriver")]
    [InlineData("usbmmidd")]
    [InlineData(@"Root\VirtualDisplayDriver")]
    public async Task UnknownArrivals_AreNeitherPhoneScreensNorModeRestoreTargets(string adapter)
    {
        var rig = new DisplayOwnershipFixture();
        rig.Arrive = _ =>
        {
            rig.Desktop.Add(Monitor("returning primary", primary: true));
            rig.Desktop.Add(Monitor("other adapter", adapter: adapter));
        };
        var svc = rig.Service();
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Equal(PhoneScreenErrorKind.NeverOnline, ex.Kind);
        Assert.Single(svc.OtherDisplays());
        Assert.Empty(rig.Writes);
        Assert.Empty(rig.Attachments);
        Assert.Empty(rig.Removes);
        Assert.Equal([0u], rig.Adds); // no eight speculative adds after an unconfirmed write
    }

    [Fact]
    public async Task ReappearingOldContainer_IsNotANewArrival()
    {
        var rig = new DisplayOwnershipFixture();
        var old = rig.Plug(0);
        rig.Unplug(0);
        rig.Arrive = _ => { rig.Present.Add(old); rig.Desktop.Add(Monitor("old alias", old)); };
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Empty(rig.Attachments);
        Assert.Empty(rig.Removes);
        Assert.Empty(rig.Writes);
        Assert.Null(svc.FitOf(0));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("wrong attached name")]
    [InlineData("primary")]
    [InlineData("cloned")]
    public async Task AttachmentFailure_RollsBackOnlyNewlyProvenArrival(string failure)
    {
        var rig = new DisplayOwnershipFixture();
        var user = rig.Plug(0);
        rig.AttachAs = m =>
        {
            if (failure != "timeout") rig.Desktop.Add(Monitor("target", m, primary: failure == "primary")
                with { HasDedicatedTarget = failure != "cloned" });
            return failure == "wrong attached name" ? "different source" : "target";
        };
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Equal([1u], rig.Removes);
        Assert.Equal(user, Assert.Single(rig.Present));
        Assert.Empty(rig.Writes);
        Assert.Null(svc.BoundsOf(1));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ModeOrDpiNotApplied_FailsCreationAndRollsBack(bool accepted, bool applied, bool scaled)
    {
        var rig = new DisplayOwnershipFixture { AcceptResolution = accepted, ApplyResolution = applied, AcceptScale = scaled };
        var svc = rig.Service();
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, ex.Kind);
        Assert.Equal([0u], rig.Removes);
        Assert.Null(svc.FitOf(0));
        Assert.Null(svc.BoundsOf(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAfterWrite_RemovesOnlyAProvenNewContainer(bool arrived)
    {
        using var cancel = new CancellationTokenSource();
        var rig = new DisplayOwnershipFixture();
        rig.Plug(0);
        rig.Arrive = id => { if (arrived) rig.Plug(id); cancel.Cancel(); };
        var svc = rig.Service();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.CreateAsync(Fit, cancel.Token));
        Assert.Equal(arrived ? new uint[] { 1 } : [], rig.Removes);
        Assert.Null(svc.FitOf(1));
        Assert.Null(svc.BoundsOf(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenConfiguredIdsSurviveCreateAndReshape(bool legacy)
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = legacy };
        var user = new VddMonitor(0, "user", false, [new(1920, 1080, [60])]);
        rig.Config.Add(user);
        if (legacy) rig.Profile.Add(0);
        var svc = rig.Service();
        Assert.Equal(1u, await svc.CreateAsync(Fit, "phone"));
        var first = Assert.Single(rig.Present);
        var secondFit = new ScreenFit(2);
        Assert.True(await svc.ReshapeAsync(1, secondFit));
        Assert.NotEqual(first.ContainerId, Assert.Single(rig.Present).ContainerId);
        Assert.Equal(secondFit, svc.FitOf(1));
        Assert.Contains(user, rig.Config);
        Assert.Equal("phone", rig.Config.Single(m => m.Id == 1).Name);
        Assert.Empty(rig.Removes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchFailure_IsReportedAndReplacementIsNotPublished(bool legacy)
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = legacy };
        var svc = rig.Service();
        uint id = await svc.CreateAsync(Fit);
        rig.AcceptResolution = false;
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.SetWidthAsync(id, Fit.Widths[^1]));
        Assert.False(await svc.ReshapeAsync(id, new ScreenFit(2)));
        Assert.Null(svc.FitOf(id));
        Assert.Null(svc.HomeBarAreaOf(id));
        Assert.Equal([id], rig.Removes);
    }

    [Fact]
    public async Task IgnoredLegacyAdd_DoesNotKeepRetryingOrRemoveAnInvisibleOccupiedId()
    {
        var rig = new DisplayOwnershipFixture();
        var invisible = new VddMonitor(0, "preexisting", true, [new(1920, 1080, [60])]);
        rig.Config.Add(invisible); // driver has the ID but Windows has not exposed any PnP target
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Equal(1u, await svc.CreateAsync(Fit));
        Assert.Contains(invisible, rig.Config);
        Assert.Equal([0u, 1u], rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public async Task V2RefreshBeforeNotify_PreservesForeignChangesDuringDiscovery(uint foreignId)
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = false };
        var external = new VddMonitor(foreignId, "external", false, []);
        rig.BeforeStateRead = n => { if (n == 2) rig.Config.Add(external); };
        var svc = rig.Service();
        if (foreignId == 0)
        {
            await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
            Assert.Empty(rig.Adds);
        }
        else Assert.Equal(0u, await svc.CreateAsync(Fit));
        Assert.Contains(external, rig.Config);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task ConcurrentDesks_GetDistinctValidatedArrivals()
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint[] ids = await Task.WhenAll(svc.CreateAsync(Fit, "a"), svc.CreateAsync(new ScreenFit(2), "b"));
        Assert.Equal([0u, 1u], ids);
        Assert.NotEqual(rig.Present[0].ContainerId, rig.Present[1].ContainerId);
        Assert.All(ids, id => Assert.NotNull(svc.BoundsOf(id)));
    }

    [Fact]
    public async Task FullSlotsAndMissingPipe_DoNotSendAnyMutation()
    {
        var rig = new DisplayOwnershipFixture();
        foreach (uint id in PhoneScreenSpec.CandidateIds) rig.Plug(id);
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        rig.PipeUp = false;
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Empty(rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanCleanup_RetainsReceiptWhilePipeDown_RejectsReusedId(bool replaced)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new SnugPaths(dir);
            var rig = new DisplayOwnershipFixture();
            uint id = await rig.Service(paths).CreateAsync(Fit);
            var restarted = rig.Service(paths);
            rig.PipeUp = false;
            await restarted.CleanupOrphansAsync();
            Assert.Empty(rig.Removes);
            Assert.True(File.Exists(paths.OwnedDisplaysFile));
            if (replaced) { rig.Unplug(id); rig.Plug(id); }
            rig.PipeUp = true;
            await restarted.CleanupOrphansAsync();
            Assert.Equal(replaced ? [] : new[] { id }, rig.Removes);
            Assert.False(File.Exists(paths.OwnedDisplaysFile));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OrphanCleanup_DoesNotTrustLegacyIdOnlyFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new SnugPaths(dir);
            new OwnedDisplayStore(paths.OwnedDisplaysFile).RecordOwned(0);
            var rig = new DisplayOwnershipFixture();
            rig.Plug(0);
            await rig.Service(paths).CleanupOrphansAsync();
            Assert.Empty(rig.Removes);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void CherryEdid_UsesLittleEndianSerialNotUidOrProductCode()
    {
        byte[] data = new byte[128];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0, 13, 25, 0, 0 }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x01020304);
        data[127] = unchecked((byte)-data.Sum(v => v));
        Assert.Equal(0x01020304u, CherryMonitorIdentity.DriverIdFromEdid(data));
        data[127]++;
        Assert.Null(CherryMonitorIdentity.DriverIdFromEdid(data));
        data[8]++; data[127] -= 2;
        Assert.Null(CherryMonitorIdentity.DriverIdFromEdid(data));
        Assert.Null(CherryMonitorIdentity.DriverIdFromEdid(data[..16]));
    }
}

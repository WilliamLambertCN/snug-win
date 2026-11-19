using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class DisplayArrivalAndRetirementTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartiallyIdentifiedArrival_IsPolledUntilBothFieldsAreAvailable(bool edidFirst)
    {
        var rig = new DisplayOwnershipFixture();
        var container = Guid.NewGuid();
        int samples = 0;
        rig.Arrive = id => rig.DuringInspection = scan =>
        {
            if (++samples == 1) scan.ObservePresent(edidFirst ? id : null, edidFirst ? Guid.Empty : container);
            else scan.ObservePresent(id, container);
        };
        var svc = rig.Service();
        Assert.Equal(0u, await svc.CreateAsync(ScreenFit.Standard));
        Assert.Equal(container, Assert.Single(rig.Attachments).ContainerId);
        Assert.True(samples >= 2);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task PreexistingUnknownId_TimesOutWithoutSendingAnyDriverMutation()
    {
        var rig = new DisplayOwnershipFixture();
        var container = Guid.NewGuid();
        int samples = 0;
        rig.DuringInspection = scan => { samples++; scan.ObservePresent(null, container); };
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, ex.Kind);
        Assert.True(samples >= 2);
        Assert.Empty(rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task KnownIdWithoutContainer_IsReservedAndDoesNotBlockOtherSlots()
    {
        var rig = new DisplayOwnershipFixture { DuringInspection = scan => scan.ObservePresent(0, Guid.Empty) };
        Assert.Equal(1u, await rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal([1u], rig.Adds);
        Assert.Empty(rig.Removes);
    }

    [Fact]
    public async Task PreexistingContainerSeenWhileWaiting_CannotLaterBeClaimed()
    {
        var rig = new DisplayOwnershipFixture();
        var old = new CherryMonitor(0, Guid.NewGuid());
        rig.DuringInspection = scan => { scan.ObservePresent(null, old.ContainerId); rig.DuringInspection = null; };
        rig.Arrive = _ => rig.Present.Add(old);
        await Assert.ThrowsAsync<PhoneScreenException>(() => rig.Service().CreateAsync(ScreenFit.Standard));
        Assert.Equal([0u], rig.Adds);
        Assert.Empty(rig.Removes);
        Assert.Empty(rig.Attachments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryAfterPipeFailure_RechecksIdentityAndDoesNotTreatPrimaryAsOffline(bool replaced)
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        rig.Desktop[0] = rig.Desktop[0] with { IsPrimary = true };
        Assert.True(svc.IsOnline(id));
        Assert.Null(svc.BoundsOf(id));
        rig.PipeUp = false;
        Assert.False(await svc.RetireAsync(id));
        Assert.True(svc.IsOnline(id));
        Assert.Empty(rig.Removes);
        if (replaced) { rig.Unplug(id); rig.Plug(id); }
        rig.PipeUp = true;
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal(replaced ? [] : new[] { id }, rig.Removes);
        Assert.False(svc.IsOnline(id));
    }

    [Fact]
    public async Task Retire_WhileIdentityIsPending_NeitherRemovesNorLosesReceipt()
    {
        var rig = new DisplayOwnershipFixture();
        var svc = rig.Service();
        uint id = await svc.CreateAsync(ScreenFit.Standard);
        var owned = Assert.Single(rig.Present);
        rig.Unplug(id);
        rig.DuringInspection = scan => scan.ObservePresent(null, owned.ContainerId);
        Assert.False(await svc.RetireAsync(id));
        Assert.True(svc.IsOnline(id));
        Assert.Empty(rig.Removes);
        rig.DuringInspection = null;
        rig.Present.Add(owned);
        Assert.True(await svc.RetireAsync(id));
        Assert.Equal([id], rig.Removes);
    }

    [Fact]
    public async Task V2Arrival_WithDifferentModeTable_IsNotClaimedOrRolledBack()
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = false };
        rig.Arrive = id =>
        {
            rig.Plug(id);
            rig.Config[0] = rig.Config[0] with { Modes = [new(1920, 1080, [60])] };
        };
        var svc = rig.Service();
        await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));
        Assert.Null(svc.FitOf(0));
        Assert.Empty(rig.Attachments);
        Assert.Empty(rig.Writes);
        Assert.Empty(rig.Removes);
    }
}

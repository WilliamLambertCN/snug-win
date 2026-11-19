using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class DisplayDpiScaleTests
{
    private static readonly ScreenFit Fit = ScreenFit.FromPhone(new PhoneScreenSpecData(1440, 3200));

    [Theory]
    [InlineData(3, 100, 175)]
    [InlineData(3, 175, 175)]
    [InlineData(4, 100, 200)]
    public async Task Create_UsesClosestSupportedDpiForTheAppliedResolution(int maxRelative, int initial, int expected)
    {
        var rig = WithDpi(maxRelative, initial);
        var svc = rig.Service();
        uint id = await svc.CreateAsync(Fit);
        var screen = Assert.Single(rig.Desktop);
        Assert.Equal(200, Fit.TargetDpiPercent(Fit.BaseWidth));
        Assert.Equal(Fit.PixelSize(Fit.BaseWidth), (screen.Mode.Width, screen.Mode.Height));
        Assert.Equal(expected, screen.ResolveDpiScalePercent(200));
        Assert.Equal(expected, screen.Mode.DpiScalePercent);
        Assert.Equal(initial == expected ? [] : new[] { expected }, rig.ScaleRequests);
        Assert.Equal(Fit, svc.FitOf(id));
        Assert.Empty(rig.Removes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnsupportedRequestedDpi_DoesNotHideSetterFailureOrIncorrectReadback(bool accept, bool apply)
    {
        var rig = WithDpi(3, 100);
        rig.AcceptScale = accept;
        rig.ApplyScale = apply;
        var svc = rig.Service();
        var error = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(Fit));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, error.Kind);
        Assert.Equal([175], rig.ScaleRequests);
        Assert.Equal([0u], rig.Removes);
        Assert.Null(svc.FitOf(0));
    }

    [Fact]
    public void RelativeRange_ReportsOnlyKnownStepsAndRejectsInvalidCapabilities()
    {
        var range = DpiScaleRange.FromRelative(-6, 2)!;
        Assert.Equal(200, range.PercentAt(-2));
        Assert.Equal(-2, range.RelativeFor(200));
        Assert.Equal(225, range.NearestPercent(215));
        Assert.Null(range.PercentAt(3));
        Assert.Null(range.RelativeFor(400));
        Assert.Null(DpiScaleRange.FromRelative(0, -1));
        Assert.Null(DpiScaleRange.FromRelative(int.MinValue, int.MaxValue));
    }

    private static DisplayOwnershipFixture WithDpi(int maxRelative, int current)
    {
        var rig = new DisplayOwnershipFixture();
        rig.AttachAs = target =>
        {
            var monitor = DisplayOwnershipFixture.Monitor("phone", target);
            rig.Desktop.Add(monitor with { DpiRange = DpiScaleRange.FromRelative(0, maxRelative),
                Mode = monitor.Mode with { DpiScalePercent = current } });
            return monitor.DeviceName;
        };
        return rig;
    }
}

using System.Text.Json;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class FixedNativePixelTests
{
    private static ScreenConfig NativePhone(int width = 1440, int height = 3200) => new()
    {
        Device = new PhoneChoice(null, new PhoneScreenSpecData(width, height)),
        FixedNativePixels = true
    };

    [Fact]
    public void SavedOptIn_SurvivesCatalogResolutionAndRoundTripForOnlyThatScreen()
    {
        var dir = Directory.CreateTempSubdirectory("snug-native-pixels-");
        try
        {
            var store = new JsonFileStore<AppConfig>(Path.Combine(dir.FullName, "config.json"));
            File.WriteAllText(store.FilePath, """
                { "Screens": [
                    { "id":"native", "fixedNativePixels":true, "device":{"model":"小米 15 Ultra","screen":{"width":1440,"height":3200}} },
                    { "id":"adaptive", "device":{"model":"小米 15 Ultra","screen":{"width":1440,"height":3200}} }
                ] }
                """);
            var catalog = PhoneCatalog.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "device-models.json"));
            var cfg = store.LoadOrQuarantine()!.Sanitize().ResolveCatalog(catalog);
            store.Save(cfg);
            var saved = store.LoadOrQuarantine()!.Sanitize();
            var screens = saved.EffectiveScreens();
            Assert.True(screens[0].FixedNativePixels);
            Assert.False(screens[1].FixedNativePixels);
            Assert.DoesNotContain("fixedNativePixels", JsonSerializer.Serialize(screens[1]));
            var wishes = ScreenPlan.Wishes(saved, remote: true);
            Assert.Equal((1440, 3200), wishes[0].Fit.PixelSize(wishes[0].Fit.BaseWidth));
            Assert.Equal((1080, 1908), wishes[1].Fit.PixelSize(wishes[1].Fit.BaseWidth));
            Assert.Equal(wishes[0].Fit, ScreenPlan.FitOf(screens[0]));
            Assert.Equal(wishes[0].Fit, ScreenPlan.Wishes(saved, remote: false, preview: true)[0].Fit);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void LegacyTopLevelPhone_KeepsToolbarInsetsAndAdaptiveWidths()
    {
        var cfg = AppConfig.FromJsonOrDefault("""{"Phone":{"screen":{"width":1440,"height":3200}}}""");
        var screen = Assert.Single(cfg.EffectiveScreens());
        var fit = ScreenPlan.FitOf(screen);
        Assert.False(screen.FixedNativePixels);
        Assert.Equal((1080, 1908), fit.PixelSize(fit.BaseWidth));
        Assert.True(fit.Widths.Count > 1);
    }

    [Fact]
    public void FixedLayout_UsesNativeAspectInPreviewAndNeverWidensForContent()
    {
        var fit = ScreenPlan.FitOf(NativePhone());
        Assert.Equal(720, fit.BaseWidth);
        Assert.Equal(720, Assert.Single(fit.RegistrationOrder));
        Assert.Equal(720, Assert.Single(fit.Widths));
        Assert.Equal(new SizeD(720, 1600), fit.Size(540));
        Assert.Equal((1440, 3200), fit.PixelSize(1080));
        Assert.Equal(200, fit.TargetDpiPercent(540));
        Assert.Equal(720, fit.WidthToFit(new SizeD(10, 20), new SizeD(720, 1500), 540));
        Assert.Equal(720, fit.WidthToFit(new SizeD(5000, 7000), new SizeD(720, 1500), 720));
        var (w, h, scale) = ScreenFit.ComputePreviewLayout(new SizeD(1280, 680), fit);
        Assert.Equal((720d, 1600d), (w, h));
        Assert.InRange(h * scale, 1, 680 * 0.84 - 48 + 0.01);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayService_RegistersJustTheNativeModeAndKeepsItOnWidthChanges(bool legacy)
    {
        var rig = new DisplayOwnershipFixture { IsLegacyProtocol = legacy };
        await using var svc = rig.Service();
        var wish = Assert.Single(ScreenPlan.Wishes(new AppConfig { Screens = [NativePhone()] }, remote: true));
        uint id = await svc.CreateAsync(wish.Fit, wish.Name);
        var mode = Assert.Single(Assert.Single(rig.Config).Modes);
        Assert.Equal((1440u, 3200u, 60u), (mode.Width, mode.Height, Assert.Single(mode.RefreshRates)));
        var monitor = Assert.Single(rig.Desktop);
        Assert.Equal((1440, 3200, 200), (monitor.Mode.Width, monitor.Mode.Height, monitor.Mode.DpiScalePercent));
        Assert.Equal(720, svc.WidthOf(id));
        int writes = rig.Writes.Count;
        await svc.SetWidthAsync(id, 540);
        await svc.SetWidthAsync(id, 1080);
        Assert.Equal(writes, rig.Writes.Count);
        Assert.Equal(monitor, Assert.Single(rig.Desktop));
        Assert.Equal(wish.Fit, svc.FitOf(id));
    }

    [Fact]
    public async Task DriverReportingAnotherResolution_RollsBackInsteadOfAcceptingItAsNative()
    {
        var rig = new DisplayOwnershipFixture { ApplyResolution = false };
        var primary = DisplayOwnershipFixture.Monitor("primary", primary: true);
        rig.Desktop.Add(primary);
        await using var svc = rig.Service();
        var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenPlan.FitOf(NativePhone())));
        Assert.Equal(PhoneScreenErrorKind.CreateFailed, ex.Kind);
        Assert.Equal([0u], rig.Removes);
        Assert.Same(primary, Assert.Single(rig.Desktop));
        Assert.Empty(rig.ScaleRequests);
        Assert.Null(svc.FitOf(0));
    }

    [Fact]
    public async Task EqualIndependentFitsDoNotRecreateButAnAdaptiveTableWithTheSameBaseDoes()
    {
        var first = ScreenPlan.FitOf(NativePhone(1080, 2160));
        var same = ScreenPlan.FitOf(NativePhone(1080, 2160));
        var adaptive = new ScreenFit(2.0);
        Assert.Equal((first.Ratio, first.BaseWidth), (adaptive.Ratio, adaptive.BaseWidth));
        Assert.Equal(first.PixelSize(first.BaseWidth), adaptive.PixelSize(adaptive.BaseWidth));
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, adaptive);
        Assert.NotEqual(first, ScreenPlan.FitOf(NativePhone(1440, 2880)));
        var rig = new DisplayOwnershipFixture();
        await using var svc = rig.Service();
        uint id = await svc.CreateAsync(first);
        Assert.True(await svc.ReshapeAsync(id, same));
        Assert.Single(rig.Adds);
        Assert.True(await svc.ReshapeAsync(id, adaptive));
        Assert.Equal(2, rig.Adds.Count);
        Assert.Equal(adaptive.RegistrationOrder.Count, Assert.Single(rig.Config).Modes.Count);
    }

    [Theory]
    [InlineData(9, 20)]
    [InlineData(20000, 20000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(480, 4320)]
    [InlineData(1081, 2400)]
    [InlineData(1080, 2399)]
    public void RatioOnlyOrUnsupportedDimensions_KeepBoundedAdaptiveModes(int width, int height)
    {
        var screen = NativePhone(width, height);
        var fit = ScreenPlan.FitOf(screen);
        Assert.Equal(ScreenFit.FromPhone(screen.Device.Screen), fit);
        Assert.True(fit.Widths.Count > 1);
        Assert.All(fit.RegistrationOrder.Select(fit.PixelSize), px =>
        {
            Assert.InRange(px.PixelWidth, 1, (int)ScreenFit.MaxPixels.Width);
            Assert.InRange(px.PixelHeight, 1, (int)ScreenFit.MaxPixels.Height);
        });
    }

    [Theory]
    [InlineData(480, 800)]
    [InlineData(2160, 4320)]
    public void SupportedBounds_RemainExact(int width, int height)
    {
        var fit = ScreenPlan.FitOf(NativePhone(width, height));
        Assert.Equal((width, height), fit.PixelSize(Assert.Single(fit.RegistrationOrder)));
    }

    [Theory]
    [InlineData(DeviceKind.Phone, ScreenOrientation.Landscape)]
    [InlineData(DeviceKind.Tablet, ScreenOrientation.Portrait)]
    [InlineData(DeviceKind.Tablet, ScreenOrientation.Landscape)]
    [InlineData(DeviceKind.Computer, ScreenOrientation.Landscape)]
    public void OtherDeviceKindsAndOrientations_IgnoreTheOptIn(DeviceKind kind, ScreenOrientation orientation)
    {
        var screen = NativePhone();
        screen = screen with { Device = screen.Device with { Kind = kind }, Orientation = orientation };
        var expected = ScreenFit.FromDevice(screen.Device.Screen, kind, orientation);
        var actual = ScreenPlan.FitOf(screen);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.RegistrationOrder.Select(expected.PixelSize), actual.RegistrationOrder.Select(actual.PixelSize));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{\"value\":true}")]
    public void MalformedOptIn_DisablesOnlyThatOption(string token)
    {
        var cfg = AppConfig.FromJsonOrDefault("""{"ZoomSteps":4,"Screens":[{"id":"keep","fixedNativePixels":TOKEN,"device":{"screen":{"width":1440,"height":3200}}}]}""".Replace("TOKEN", token));
        var screen = Assert.Single(cfg.EffectiveScreens());
        Assert.Equal("keep", screen.Id);
        Assert.Equal(4, cfg.ZoomSteps);
        Assert.False(screen.FixedNativePixels);
        Assert.Equal((1080, 1908), ScreenPlan.FitOf(screen).PixelSize(540));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"screen\":null}")]
    [InlineData("{\"screen\":{\"width\":0,\"height\":3200}}")]
    [InlineData("{\"screen\":{\"width\":3200,\"height\":1440}}")]
    public void InvalidDevice_IsNotPlannedAndDoesNotDiscardOtherScreens(string device)
    {
        var cfg = AppConfig.FromJsonOrDefault("""{"Screens":[{"id":"bad","fixedNativePixels":true,"device":DEVICE},{"id":"keep"}]}""".Replace("DEVICE", device));
        Assert.Equal("keep", Assert.Single(ScreenPlan.Wishes(cfg, remote: true)).Id);
    }
}

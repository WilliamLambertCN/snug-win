using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Config;

namespace SnugWin.Core.PhoneScreen;

public enum ScreenBehavior
{
    TouchScreen,
    Computer
}

public sealed record ScreenWish(
    string Id,
    string Name,
    PhoneChoice Device,
    ScreenOrientation Orientation,
    ScreenFit Fit,
    ScreenBehavior Behavior)
{
    public bool IsTouch => Behavior == ScreenBehavior.TouchScreen;
}

public static class ScreenPlan
{
    public static ScreenBehavior BehaviorOf(DeviceKind kind) =>
        kind.IsTouch() ? ScreenBehavior.TouchScreen : ScreenBehavior.Computer;

    public static ScreenFit FitOf(ScreenConfig screen) =>
        screen is { FixedNativePixels: true, Device.Kind: DeviceKind.Phone, Orientation: ScreenOrientation.Portrait }
            ? ScreenFit.FromNativePhone(screen.Device.Screen)
            : ScreenFit.FromDevice(screen.Device.Screen, screen.Device.Kind, screen.Orientation);

    public static IReadOnlyList<ScreenWish> Wishes(AppConfig config, bool remote, bool preview = false)
    {
        if (!remote && !preview) return [];

        return config.EffectiveScreens()
            .Where(s => s.Enabled)
            .Take(ScreenConfig.Limit)
            .Select(s => new ScreenWish(
                s.Id,
                s.Name,
                s.Device,
                s.Orientation,
                FitOf(s),
                BehaviorOf(s.Device.Kind)))
            .ToList();
    }
}

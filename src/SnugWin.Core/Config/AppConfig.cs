using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppThemeMode
{
    Light = 0,
    Dark = 1,
    Auto = 2
}

public sealed record AppConfig
{
    public const int MinZoomSteps = 0;
    public const int MaxZoomSteps = 4;

    public bool PhoneScreen { get; init; } = true;
    public bool Gestures { get; init; } = true;
    public bool GestureScroll { get; init; } = true;
    public bool GestureLongPressRightClick { get; init; } = true;
    public bool CopyAfterSelection { get; init; } = true;
    public bool GesturesInjectedOnly { get; init; } = true;
    public bool Screenshot { get; init; } = false;
    public bool AutoCheckUpdates { get; init; } = true;
    public int ZoomSteps { get; init; } = 2;
    public AppThemeMode Theme { get; init; } = AppThemeMode.Light;
    public PhoneChoice Phone { get; init; } = PhoneChoice.Standard;
    public IReadOnlyList<ScreenConfig>? Screens { get; init; }
    public bool Welcomed { get; init; } = false;
    public string? LastVersion { get; init; }

    public IReadOnlyList<ScreenConfig> EffectiveScreens()
    {
        if (Screens is not null)
            return Screens;
        return [ScreenConfig.LegacyPhone(Phone, PhoneScreen)];
    }

    public ScreenConfig? PrimaryTouchScreen()
    {
        var list = EffectiveScreens();
        return list.FirstOrDefault(s => s.Orientation == ScreenOrientation.Portrait && s.Device.Kind == DeviceKind.Phone)
            ?? list.FirstOrDefault(s => s.Device.Kind.IsTouch())
            ?? list.FirstOrDefault();
    }

    public AppConfig Sanitize()
    {
        var phone = Phone is { Screen.IsValid: true } ? Phone : PhoneChoice.Standard;
        var theme = Enum.IsDefined(Theme) ? Theme : AppThemeMode.Light;
        var sanitizedScreens = SanitizeScreens(phone);
        var legacyPhone = sanitizedScreens.FirstOrDefault(s =>
            s.Orientation == ScreenOrientation.Portrait && s.Device.Kind == DeviceKind.Phone);

        return this with
        {
            ZoomSteps = ZoomSteps.Clamp(MinZoomSteps, MaxZoomSteps),
            Theme = theme,
            Phone = legacyPhone?.Device ?? phone,
            PhoneScreen = legacyPhone?.Enabled ?? sanitizedScreens.Any(s => s.Enabled),
            Screens = sanitizedScreens
        };
    }

    private IReadOnlyList<ScreenConfig> SanitizeScreens(PhoneChoice phone)
    {
        if (Screens is null) return [ScreenConfig.LegacyPhone(phone, PhoneScreen)];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ScreenConfig>();
        foreach (var raw in Screens)
        {
            if (raw is not { Device.Screen.IsValid: true }) continue;
            var clean = raw.Sanitize();
            if (!seen.Add(clean.Id)) continue;
            list.Add(clean);
            if (list.Count >= ScreenConfig.Limit) break;
        }
        return list;
    }

    public AppConfig WithScreenAdded(ScreenOrientation orientation, PhoneChoice device, string? name = null)
    {
        var list = EffectiveScreens().ToList();
        if (list.Count >= ScreenConfig.Limit) return this;
        var cleanOrientation = device.Kind == DeviceKind.Computer ? ScreenOrientation.Landscape : orientation;
        var screen = new ScreenConfig
        {
            Id = Guid.NewGuid().ToString("D"),
            Orientation = cleanOrientation,
            Device = device,
            Enabled = true,
            Name = string.IsNullOrWhiteSpace(name) ? ScreenConfig.SuggestedName(device, cleanOrientation) : name.Trim()
        }.Sanitize();
        list.Add(screen);
        return (this with { Screens = list }).Sanitize();
    }

    public AppConfig WithScreenRemoved(string id)
    {
        var list = EffectiveScreens().Where(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        return (this with { Screens = list }).Sanitize();
    }

    public AppConfig WithScreenUpdated(string id, Func<ScreenConfig, ScreenConfig> update)
    {
        var list = EffectiveScreens().Select(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) ? update(s) : s).ToList();
        return (this with { Screens = list }).Sanitize();
    }

    public AppConfig ResolveCatalog(PhoneCatalog catalog)
    {
        var phone = catalog.Resolve(Phone);
        var screens = EffectiveScreens()
            .Select(s => s with { Device = catalog.Resolve(s.Device) })
            .ToList();
        return (this with { Phone = phone, Screens = screens }).Sanitize();
    }

    public static AppConfig FromJsonOrDefault(string json)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return (cfg ?? new AppConfig()).Sanitize();
        }
        catch
        {
            return new AppConfig();
        }
    }
}

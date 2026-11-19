using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Touch;

namespace SnugWin.Core.PhoneScreen;

public sealed record ScreenFit : IEquatable<ScreenFit>
{
    public const int DesignWidth = 540;
    public const int MinLongSide = 800;
    public const int MinShortSide = 525;
    public const int MinWidth = 540;
    public const int MinHeight = 800;
    public const int MaxWidth = 1080;
    public const int Step = 10;
    public const double MinRatio = 1.0;
    public const double MaxRatio = 2.0;
    public const double MinLandscapeRatio = 0.3;
    public const double MaxLandscapeRatio = 1.0;

    public static readonly SizeD MaxPixels = new(MaxWidth * 2, (int)(MaxWidth * MaxRatio) * 2);
    public static readonly SizeD LandscapeMaxPixels = new(6144, 3840);

    private const double UuInset = 91.0;
    private const double UuLandscapeInsets = 82.0 + 72.0;
    private const double AssumedPoints = 400.0;
    private const double AssumedTabletPoints = 800.0;
    private const double TabletScale = 1.24;
    private const double BaseHomeBarHeight = 28.0;

    private readonly (int Width, int Height)? _nativePixels;

    public double Ratio { get; }
    public int BaseWidth { get; }
    public IReadOnlyList<int> Widths { get; }
    public ScreenOrientation Orientation { get; }
    public DeviceKind Kind { get; }

    public IReadOnlyList<int> RegistrationOrder => Widths.Reverse().ToList();

    public static readonly ScreenFit Standard = FromPhone(PhoneChoice.Standard.Screen);

    public ScreenFit(double ratio)
    {
        Kind = DeviceKind.Phone;
        Orientation = ScreenOrientation.Portrait;
        Ratio = ratio.Clamp(MinRatio, MaxRatio);
        var widths = new List<int>();
        int? baseWidth = null;
        for (int w = MinWidth; w <= MaxWidth; w += Step)
        {
            if (baseWidth is null && Math.Round(w * Ratio, MidpointRounding.AwayFromZero) >= MinHeight)
                baseWidth = w;
            if (baseWidth is not null)
                widths.Add(w);
        }
        BaseWidth = baseWidth ?? MaxWidth;
        if (widths.Count == 0) widths.Add(MaxWidth);
        Widths = widths;
    }

    private ScreenFit(double ratio, ScreenOrientation orientation, DeviceKind kind, int? wantedWidth)
    {
        Kind = kind;
        Orientation = orientation;
        Ratio = orientation == ScreenOrientation.Portrait
            ? ratio.Clamp(MinRatio, MaxRatio)
            : ratio.Clamp(MinLandscapeRatio, MaxLandscapeRatio);

        var maxPixels = orientation == ScreenOrientation.Portrait ? MaxPixels : LandscapeMaxPixels;
        int limit = (int)Math.Min(maxPixels.Width / 2.0, Math.Floor(maxPixels.Height / 2.0 / Ratio));

        if (kind == DeviceKind.Computer)
        {
            int w = Math.Max(MinLongSide, wantedWidth ?? 1920);
            BaseWidth = w;
            Widths = [w];
            return;
        }

        int minW = orientation == ScreenOrientation.Portrait ? MinShortSide : MinLongSide;
        int minH = orientation == ScreenOrientation.Portrait ? MinLongSide : MinShortSide;
        int threshold = Math.Max(minW, (int)Math.Ceiling((minH - 0.5) / Ratio));
        int clampedBase = Math.Min(Math.Max(wantedWidth ?? threshold, threshold), Math.Max(threshold, limit));
        BaseWidth = clampedBase;

        var list = new List<int>();
        int maxRange = Math.Min(clampedBase * 2, Math.Max(clampedBase, limit));
        for (int w = clampedBase; w <= maxRange; w += Step)
            list.Add(w);
        if (list.Count == 0) list.Add(clampedBase);
        Widths = list;
    }

    private ScreenFit(int pixelWidth, int pixelHeight)
    {
        Kind = DeviceKind.Phone;
        Orientation = ScreenOrientation.Portrait;
        _nativePixels = (pixelWidth, pixelHeight);
        Ratio = (double)pixelHeight / pixelWidth;
        BaseWidth = pixelWidth / 2;
        Widths = [BaseWidth];
    }

    internal static ScreenFit FromNativePhone(PhoneScreenSpecData? phone)
    {
        if (phone is not { IsValid: true }) return Standard;
        bool usable = phone.Width >= 480 && phone.Width <= MaxPixels.Width
            && phone.Height >= 800 && phone.Height <= MaxPixels.Height
            && phone.Height <= 3 * phone.Width && phone.Width % 2 == 0 && phone.Height % 2 == 0;
        return usable ? new ScreenFit(phone.Width, phone.Height) : FromPhone(phone);
    }

    public static ScreenFit FromPhone(PhoneScreenSpecData phone) =>
        FromDevice(phone, DeviceKind.Phone, ScreenOrientation.Portrait);

    public static ScreenFit FromDevice(
        PhoneScreenSpecData device,
        DeviceKind kind,
        ScreenOrientation orientation)
    {
        double s = device.Width;
        double l = device.Height;

        return (kind, orientation) switch
        {
            (DeviceKind.Phone, ScreenOrientation.Portrait) =>
                new ScreenFit(l / s - 2.0 * UuInset / (device.Points ?? AssumedPoints)),

            (DeviceKind.Phone, ScreenOrientation.Landscape) =>
                CreatePhoneLandscape(s, l, device.Points),

            (DeviceKind.Tablet, _) =>
                CreateTablet(s, l, device.Points, orientation),

            (DeviceKind.Computer, ScreenOrientation.Landscape) =>
                new ScreenFit((s - (device.Notch ?? 0)) / l, ScreenOrientation.Landscape, DeviceKind.Computer, device.Height),

            (DeviceKind.Computer, ScreenOrientation.Portrait) =>
                new ScreenFit(l / s, ScreenOrientation.Portrait, DeviceKind.Computer, device.Width),

            _ => Standard
        };
    }

    private static ScreenFit CreatePhoneLandscape(double shortSide, double longSide, int? pointsSpec)
    {
        double points = pointsSpec ?? AssumedPoints;
        double ratio = points / (longSide * points / shortSide - UuLandscapeInsets);
        return new ScreenFit(ratio, ScreenOrientation.Landscape, DeviceKind.Phone, null);
    }

    private static ScreenFit CreateTablet(double shortSide, double longSide, int? pointsSpec, ScreenOrientation orientation)
    {
        double points = pointsSpec ?? AssumedTabletPoints;
        double across = orientation == ScreenOrientation.Portrait ? points : longSide * points / shortSide;
        double ratio = orientation == ScreenOrientation.Portrait ? longSide / shortSide : shortSide / longSide;
        int wantedWidth = (int)Math.Round(across / TabletScale, MidpointRounding.AwayFromZero);
        return new ScreenFit(ratio, orientation, DeviceKind.Tablet, wantedWidth);
    }

    public SizeD Size(int width) => _nativePixels is { } pixels
        ? new(pixels.Width / 2, pixels.Height / 2)
        : new(width, Math.Round(width * Ratio, MidpointRounding.AwayFromZero));

    /// <summary>Physical pixels to register: the exact fixed mode, or the adaptive mode at the requested width.</summary>
    public (int PixelWidth, int PixelHeight) PixelSize(int logicalWidth)
    {
        if (_nativePixels is { } pixels) return pixels;
        var logical = Size(logicalWidth);
        int multiplier = (Kind == DeviceKind.Computer && (logical.Width * 2 > 3840 || logical.Height * 2 > 2400)) ? 1 : 2;
        int pw = (int)logical.Width * multiplier;
        int ph = (int)logical.Height * multiplier;
        if (pw % 2 != 0) pw++;
        if (ph % 2 != 0) ph++;
        return (pw, ph);
    }

    public int TargetDpiPercent(int logicalWidth)
    {
        var logical = Size(logicalWidth);
        return (Kind == DeviceKind.Computer && (logical.Width * 2 > 3840 || logical.Height * 2 > 2400)) ? 100 : 200;
    }

    public int WidthToFit(SizeD window, SizeD area, int currentWidth)
    {
        if (_nativePixels.HasValue) return BaseWidth;
        const double tolerance = 2.0;
        if (window.Width <= area.Width + tolerance && window.Height <= area.Height + tolerance)
            return currentWidth;

        double needed = Math.Max(
            currentWidth * window.Width / area.Width,
            currentWidth * window.Height / area.Height);
        int target = (int)Math.Ceiling(needed);
        foreach (int w in Widths)
        {
            if (w >= target) return w;
        }
        return Widths[^1];
    }

    public static double Scale(double screenWidth) => screenWidth / DesignWidth;

    public static double Scale(SizeD screen) => Math.Min(screen.Width, screen.Height) / DesignWidth;

    public static double HomeBarHeight(double screenWidth) =>
        Math.Round(BaseHomeBarHeight * Scale(screenWidth), MidpointRounding.AwayFromZero);

    public static double HomeBarHeight(SizeD screen) =>
        Math.Round(BaseHomeBarHeight * Scale(screen), MidpointRounding.AwayFromZero);

    public static (double DesignWidth, double DesignHeight, double Scale) ComputePreviewLayout(SizeD workAreaDip, ScreenFit fit)
    {
        var logical = fit.Size(fit.BaseWidth);
        double maxW = Math.Max(300.0, workAreaDip.Width * 0.72);
        double maxH = Math.Max(420.0, workAreaDip.Height * 0.84 - 48.0);
        double scale = Math.Min(0.84, Math.Min(maxW / logical.Width, maxH / logical.Height));
        if (fit._nativePixels is null) scale = Math.Max(0.35, scale);
        return (logical.Width, logical.Height, scale);
    }

    public bool Equals(ScreenFit? other) =>
        other is not null
        && Ratio.Equals(other.Ratio)
        && BaseWidth == other.BaseWidth
        && Orientation == other.Orientation
        && Kind == other.Kind
        && _nativePixels == other._nativePixels;

    public override int GetHashCode() => HashCode.Combine(Ratio, BaseWidth, Orientation, Kind, _nativePixels);

    public override string ToString()
    {
        var logical = Size(BaseWidth);
        return $"{(int)logical.Width}x{(int)logical.Height}";
    }
}

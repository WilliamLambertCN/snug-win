using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Config;

public sealed record ScreenConfig
{
    public const int Limit = 4;
    public const string LegacyPhoneId = "00000000-0000-0000-0000-000000000001";

    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    [JsonPropertyName("orientation")]
    public ScreenOrientation Orientation { get; init; } = ScreenOrientation.Portrait;

    [JsonPropertyName("device")]
    public PhoneChoice Device { get; init; } = PhoneChoice.Standard;

    [JsonPropertyName("fixedNativePixels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(NativePixelsOptInConverter))]
    public bool FixedNativePixels { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("name")]
    public string Name { get; init; } = PhoneScreenSpec.Name;

    public static ScreenConfig LegacyPhone(PhoneChoice phone, bool enabled = true) => new()
    {
        Id = LegacyPhoneId,
        Orientation = ScreenOrientation.Portrait,
        Device = phone with { Kind = DeviceKind.Phone },
        Enabled = enabled,
        Name = PhoneScreenSpec.Name
    };

    public static string SuggestedName(PhoneChoice device, ScreenOrientation orientation)
    {
        string baseName = device.Model ?? $"{device.Kind.DisplayName()}";
        if (orientation == device.Kind.UsualOrientation())
            return baseName;
        return $"{baseName} {orientation.DisplayName()}";
    }

    public ScreenConfig Sanitize()
    {
        var id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("D") : Id.Trim();
        var kind = Enum.IsDefined(Device.Kind) ? Device.Kind : DeviceKind.Phone;
        var screen = Device.Screen.IsValid ? Device.Screen : PhoneChoice.Standard.Screen;
        var orientation = kind == DeviceKind.Computer
            ? ScreenOrientation.Landscape
            : (Enum.IsDefined(Orientation) ? Orientation : kind.UsualOrientation());
        var name = string.IsNullOrWhiteSpace(Name)
            ? SuggestedName(Device with { Kind = kind, Screen = screen }, orientation)
            : Name.Trim();
        return this with
        {
            Id = id,
            Orientation = orientation,
            Device = Device with { Kind = kind, Screen = screen },
            Name = name
        };
    }
}

// A malformed opt-in must not quarantine the user's other screen settings.
internal sealed class NativePixelsOptInConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        bool enabled = reader.TokenType == JsonTokenType.True;
        reader.Skip();
        return enabled;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

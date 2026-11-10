using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupermarketErp.Api.Infrastructure;

public static class JavaInstantFormat
{
    public static string Format(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var result = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return result + (fraction.Length == 0 ? string.Empty : "." + fraction) + "Z";
    }
}

public sealed class JavaInstantJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JavaInstantFormat.Format(value));
}

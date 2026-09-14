using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace InvoiceReviewAssistant.Api.Contracts;

public static class InvoiceJsonDefaults
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new LowercaseGuidJsonConverter());
        options.Converters.Add(new ContractDateOnlyJsonConverter());
        options.Converters.Add(new UtcMillisecondsDateTimeOffsetJsonConverter());
        options.Converters.Add(new MoneyJsonConverter());
    }

    public static JsonSerializerOptions Create() { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); Configure(options); return options; }
}

public sealed class LowercaseGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || value.Length != 36 || value != value.ToLowerInvariant() || !Guid.TryParseExact(value, "D", out var result))
        {
            throw new JsonException("UUIDs must use lowercase RFC 4122 D format.");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D").ToLowerInvariant());
}

public sealed class ContractDateOnlyJsonConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new JsonException("Dates must use yyyy-MM-dd format.");
        }

        return date;
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

public sealed class UtcMillisecondsDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || value.Length != 24 || !value.EndsWith('Z') || !DateTimeOffset.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new JsonException("Timestamps must use UTC millisecond format.");
        }

        return timestamp;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

public sealed class MoneyJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Money must be a two-decimal string.");
        }

        var value = reader.GetString();
        if (value is null || !IsCanonicalMoney(value) || !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var money))
        {
            throw new JsonException("Money must be an invariant two-decimal value.");
        }

        return money == decimal.Zero ? decimal.Zero : money;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        if (decimal.Round(value, 2, MidpointRounding.ToEven) != value || Math.Abs(value) > 99999999999999999999999999.99m)
        {
            throw new JsonException("Money is outside the HTTP v1 range.");
        }

        writer.WriteStringValue(value == decimal.Zero ? "0.00" : value.ToString("0.00", CultureInfo.InvariantCulture));
    }

    private static bool IsCanonicalMoney(string value)
    {
        if (value.Length is < 4 or > 30) return false;
        var start = value[0] == '-' ? 1 : 0;
        if (start == value.Length || value.Length - start < 4 || value[^3] != '.') return false;
        if (value[start] == '0' && value.Length - start > 4) return false;
        if (value[start] is < '0' or > '9') return false;
        for (var index = start; index < value.Length; index++)
        {
            if (index == value.Length - 3) continue;
            if (value[index] is < '0' or > '9') return false;
        }
        return true;
    }
}

public sealed class AuditEventDtoJsonConverter : JsonConverter<AuditEventDto>
{
    public override AuditEventDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException("Audit history is response-only.");

    public override void Write(Utf8JsonWriter writer, AuditEventDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("id"); JsonSerializer.Serialize(writer, value.Id.ToString(CultureInfo.InvariantCulture), options);
        writer.WritePropertyName("invoiceId"); JsonSerializer.Serialize(writer, value.InvoiceId, options);
        writer.WritePropertyName("type"); JsonSerializer.Serialize(writer, value.Type, options);
        writer.WritePropertyName("actor"); JsonSerializer.Serialize(writer, value.Actor, options);
        writer.WritePropertyName("occurredAt"); JsonSerializer.Serialize(writer, value.OccurredAt, options);
        writer.WriteNumber("draftVersion", value.DraftVersion);
        writer.WritePropertyName("details"); JsonSerializer.Serialize(writer, value.Details, value.Details.GetType(), options);
        writer.WriteEndObject();
    }
}

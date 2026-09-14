using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Infrastructure.Persistence;

internal static class CanonicalJson
{
    private const string SchemaVersion = "1.0";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string SerializeValue(CanonicalFieldValue value) => JsonSerializer.Serialize(
        new PersistedValue(SchemaVersion, value.Kind.ToString(), value.Value), Options);

    public static CanonicalFieldValue DeserializeValue(string json)
    {
        var value = JsonSerializer.Deserialize<PersistedValue>(json, Options)
            ?? throw new InvalidOperationException("Persisted canonical value is missing.");
        if (value.SchemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException("Persisted canonical value has an unsupported schema version.");
        }

        return Enum.Parse<CanonicalValueKind>(value.Kind, ignoreCase: false) switch
        {
            CanonicalValueKind.Null => CanonicalFieldValue.Null(),
            CanonicalValueKind.Text => CanonicalFieldValue.Text(value.Value),
            CanonicalValueKind.Date => CanonicalFieldValue.Date(value.Value is null ? null : DateOnly.ParseExact(value.Value, "yyyy-MM-dd", null)),
            CanonicalValueKind.Money => CanonicalFieldValue.Money(value.Value is null ? null : decimal.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException("Persisted canonical value has an unsupported kind.")
        };
    }

    public static string SerializeData(object? value) => JsonSerializer.Serialize(new PersistedData(SchemaVersion, value), Options);

    public static string SerializeFields(IReadOnlyList<InvoiceFieldKey> fields) =>
        JsonSerializer.Serialize(new PersistedFields(SchemaVersion, fields.Select(field => field.ToString()).ToArray()), Options);

    public static IReadOnlyList<InvoiceFieldKey> DeserializeFields(string json)
    {
        var fields = JsonSerializer.Deserialize<PersistedFields>(json, Options)
            ?? throw new InvalidOperationException("Persisted related fields are missing.");
        if (fields.SchemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException("Persisted related fields have an unsupported schema version.");
        }

        return fields.Fields.Select(value => Enum.Parse<InvoiceFieldKey>(value, ignoreCase: false)).ToArray();
    }

    private sealed record PersistedValue(string SchemaVersion, string Kind, string? Value);
    private sealed record PersistedData(string SchemaVersion, object? Value);
    private sealed record PersistedFields(string SchemaVersion, string[] Fields);
}

using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InvoiceReviewAssistant.Infrastructure.Persistence;

internal static class SqliteConverters
{
    public static readonly ValueConverter<decimal, string> Decimal = new(
        value => value.ToString("0.00", CultureInfo.InvariantCulture),
        value => decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));

    public static readonly ValueConverter<decimal?, string?> NullableDecimal = new(
        value => value == null ? null : value.Value.ToString("0.00", CultureInfo.InvariantCulture),
        value => value == null ? null : decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));

    public static readonly ValueConverter<DateOnly, string> DateOnly = new(
        value => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value => global::System.DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static readonly ValueConverter<DateOnly?, string?> NullableDateOnly = new(
        value => value == null ? null : value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value => value == null ? null : global::System.DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static readonly ValueConverter<DateTime, DateTime> UtcDateTime = new(
        value => ToUtc(value),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, DateTime?> NullableUtcDateTime = new(
        value => value == null ? null : ToUtc(value.Value),
        value => value == null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    public static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : value.ToUniversalTime();
}

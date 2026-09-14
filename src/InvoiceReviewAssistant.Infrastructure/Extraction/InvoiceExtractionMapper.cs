using InvoiceReviewAssistant.Core.Invoices;
using System.Globalization;
using System.Text.Json;

namespace InvoiceReviewAssistant.Infrastructure.Extraction;

public enum InvoiceExtractionValidationCode
{
    MalformedJson,
    SchemaViolation,
    UnsupportedSchemaVersion,
    NullConfidenceMismatch,
    BlankText,
    InvalidDate,
    InvalidMoney,
    InvalidCurrency,
}

/// <summary>
/// A safe validation failure that never contains provider output or extracted values.
/// </summary>
public sealed class InvoiceExtractionValidationException : InvalidOperationException
{
    internal InvoiceExtractionValidationException(InvoiceExtractionValidationCode code, string? field)
        : base(CreateMessage(code, field))
    {
        Code = code;
        Field = field;
    }

    public InvoiceExtractionValidationCode Code { get; }

    public string? Field { get; }

    private static string CreateMessage(InvoiceExtractionValidationCode code, string? field) => field is null
        ? $"The extraction response failed validation ({code})."
        : $"The extraction response field '{field}' failed validation ({code}).";
}

/// <summary>
/// Validates provider structured output locally before atomically mapping all fields to a
/// provider-neutral proposal. Document text provenance remains outside this proposal.
/// </summary>
public sealed class InvoiceExtractionMapper
{
    private static readonly DateOnly MinimumDate = new(1, 1, 1);
    private readonly InvoiceExtractionSchema _schema;

    public InvoiceExtractionMapper(InvoiceExtractionSchema schema)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public InvoiceExtractionProposal Map(string structuredOutput, ExtractionSchemaVersion schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(structuredOutput);
        EnsureSelectorVersion(schemaVersion);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(structuredOutput);
        }
        catch (JsonException)
        {
            throw Failure(InvoiceExtractionValidationCode.MalformedJson);
        }

        using (document)
        {
            if (!_schema.IsValid(document.RootElement))
            {
                throw Failure(InvoiceExtractionValidationCode.SchemaViolation);
            }

            return MapValidated(document.RootElement);
        }
    }

    private static InvoiceExtractionProposal MapValidated(JsonElement root)
    {
        var payloadVersion = root.GetProperty("schemaVersion").GetString();
        if (!string.Equals(payloadVersion, InvoiceExtractionSchema.PayloadVersion, StringComparison.Ordinal))
        {
            throw Failure(InvoiceExtractionValidationCode.UnsupportedSchemaVersion, "schemaVersion");
        }

        var supplier = root.GetProperty("supplier");
        var reference = root.GetProperty("reference");
        var datesAndTerms = root.GetProperty("datesAndTerms");
        var amounts = root.GetProperty("amounts");

        var supplierName = ReadText(supplier, "name", "supplier.name");
        var registrationId = ReadText(supplier, "registrationId", "supplier.registrationId");
        var invoiceNumber = ReadText(reference, "invoiceNumber", "reference.invoiceNumber");
        var purchaseOrderNumber = ReadText(reference, "purchaseOrderNumber", "reference.purchaseOrderNumber");
        var invoiceDate = ReadDate(datesAndTerms, "invoiceDate", "datesAndTerms.invoiceDate");
        var dueDate = ReadDate(datesAndTerms, "dueDate", "datesAndTerms.dueDate");
        var paymentTerms = ReadText(datesAndTerms, "paymentTerms", "datesAndTerms.paymentTerms");
        var currency = ReadCurrency(amounts, "currency", "amounts.currency");
        var subtotal = ReadMoney(amounts, "subtotal", "amounts.subtotal");
        var taxAmount = ReadMoney(amounts, "taxAmount", "amounts.taxAmount");
        var total = ReadMoney(amounts, "total", "amounts.total");

        var fields = new InvoiceFields(
            supplierName.NullableValue,
            registrationId.NullableValue,
            invoiceNumber.NullableValue,
            purchaseOrderNumber.NullableValue,
            invoiceDate.HasValue ? invoiceDate.Value : null,
            dueDate.HasValue ? dueDate.Value : null,
            paymentTerms.NullableValue,
            PaymentTerms.DeriveDays(paymentTerms.NullableValue),
            currency.NullableValue,
            subtotal.HasValue ? subtotal.Value : null,
            taxAmount.HasValue ? taxAmount.Value : null,
            total.HasValue ? total.Value : null);

        InvoiceFieldMetadata[] metadata =
        [
            Metadata(InvoiceFieldKey.SupplierName, fields, supplierName.Confidence),
            Metadata(InvoiceFieldKey.SupplierRegistrationId, fields, registrationId.Confidence),
            Metadata(InvoiceFieldKey.InvoiceNumber, fields, invoiceNumber.Confidence),
            Metadata(InvoiceFieldKey.PurchaseOrderNumber, fields, purchaseOrderNumber.Confidence),
            Metadata(InvoiceFieldKey.InvoiceDate, fields, invoiceDate.Confidence),
            Metadata(InvoiceFieldKey.DueDate, fields, dueDate.Confidence),
            Metadata(InvoiceFieldKey.PaymentTerms, fields, paymentTerms.Confidence),
            Metadata(InvoiceFieldKey.Currency, fields, currency.Confidence),
            Metadata(InvoiceFieldKey.Subtotal, fields, subtotal.Confidence),
            Metadata(InvoiceFieldKey.TaxAmount, fields, taxAmount.Confidence),
            Metadata(InvoiceFieldKey.Total, fields, total.Confidence),
        ];

        return new InvoiceExtractionProposal(fields, metadata);
    }

    private static ExtractedValue<string> ReadText(JsonElement parent, string propertyName, string field)
    {
        var extracted = ReadField(parent, propertyName, field, element => element.GetString()!);
        if (extracted.HasValue && string.IsNullOrWhiteSpace(extracted.Value))
        {
            throw Failure(InvoiceExtractionValidationCode.BlankText, field);
        }

        return extracted;
    }

    private static ExtractedValue<DateOnly> ReadDate(JsonElement parent, string propertyName, string field) =>
        ReadField(parent, propertyName, field, element =>
        {
            var text = element.GetString();
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) || value < MinimumDate)
            {
                throw Failure(InvoiceExtractionValidationCode.InvalidDate, field);
            }

            return value;
        });

    private static ExtractedValue<decimal> ReadMoney(JsonElement parent, string propertyName, string field) =>
        ReadField(parent, propertyName, field, element =>
        {
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            var text = element.GetString()!;
            if (!decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out var value) ||
                !string.Equals(value.ToString("0.00", CultureInfo.InvariantCulture), text, StringComparison.Ordinal))
            {
                throw Failure(InvoiceExtractionValidationCode.InvalidMoney, field);
            }

            return value;
        });

    private static ExtractedValue<string> ReadCurrency(JsonElement parent, string propertyName, string field) =>
        ReadField(parent, propertyName, field, element =>
        {
            var value = element.GetString()!;
            if (value.Length != 3 || value.Any(character => character is < 'A' or > 'Z'))
            {
                throw Failure(InvoiceExtractionValidationCode.InvalidCurrency, field);
            }

            return value;
        });

    private static ExtractedValue<T> ReadField<T>(
        JsonElement parent,
        string propertyName,
        string field,
        Func<JsonElement, T> convert)
        where T : notnull
    {
        var element = parent.GetProperty(propertyName);
        var valueElement = element.GetProperty("value");
        var confidenceElement = element.GetProperty("confidence");
        double? confidence = confidenceElement.ValueKind == JsonValueKind.Null ? null : confidenceElement.GetDouble();

        if (valueElement.ValueKind == JsonValueKind.Null)
        {
            if (confidence is not null)
            {
                throw Failure(InvoiceExtractionValidationCode.NullConfidenceMismatch, field);
            }

            return new ExtractedValue<T>(false, default!, null);
        }

        return new ExtractedValue<T>(true, convert(valueElement), confidence);
    }

    private static InvoiceFieldMetadata Metadata(InvoiceFieldKey field, InvoiceFields fields, double? confidence) =>
        new(field, fields.GetCanonicalValue(field), FieldSource.AiInference, FieldSource.AiInference, confidence, null);

    private static void EnsureSelectorVersion(ExtractionSchemaVersion schemaVersion)
    {
        if (!string.Equals(schemaVersion.Value, InvoiceExtractionSchema.SelectorVersion, StringComparison.Ordinal))
        {
            throw Failure(InvoiceExtractionValidationCode.UnsupportedSchemaVersion, "schemaVersion");
        }
    }

    private static InvoiceExtractionValidationException Failure(InvoiceExtractionValidationCode code, string? field = null) => new(code, field);

    private readonly record struct ExtractedValue<T>(bool HasValue, T Value, double? Confidence)
        where T : notnull
    {
        public T? NullableValue => HasValue ? Value : default;
    }
}

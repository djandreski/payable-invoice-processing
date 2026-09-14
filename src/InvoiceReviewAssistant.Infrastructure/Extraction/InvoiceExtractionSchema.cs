using Json.Schema;
using System.Text;
using System.Text.Json;

namespace InvoiceReviewAssistant.Infrastructure.Extraction;

/// <summary>
/// The locally parsed form of the exact schema sent to extraction providers.
/// </summary>
public sealed class InvoiceExtractionSchema
{
    public const string FormatName = "invoice_extraction_v1";
    public const string SelectorVersion = "v1";
    public const string PayloadVersion = "1.0";
    public static readonly string CanonicalRelativePath = Path.Combine("Schemas", $"{FormatName}.schema.json");

    private readonly JsonSchema _schema;

    internal InvoiceExtractionSchema(string json, JsonSchema schema)
    {
        Json = json;
        _schema = schema;
    }

    /// <summary>
    /// Gets the canonical schema text for a provider's strict Structured Outputs request.
    /// </summary>
    public string Json { get; }

    internal bool IsValid(JsonElement instance)
    {
        var results = _schema.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Flag,
            RequireFormatValidation = true,
        });

        return results.IsValid;
    }
}

/// <summary>
/// Loads the checked-in extraction schema. Streams passed to this loader remain owned by
/// the caller; file streams opened by the loader are always disposed by the loader.
/// </summary>
public sealed class InvoiceExtractionSchemaLoader
{
    public async Task<InvoiceExtractionSchema> LoadCanonicalAsync(
        string baseDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var schemaPath = Path.Combine(baseDirectory, InvoiceExtractionSchema.CanonicalRelativePath);
        return await LoadFromFileAsync(schemaPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceExtractionSchema> LoadFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await LoadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceExtractionSchema> LoadAsync(
        Stream schemaStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemaStream);
        if (!schemaStream.CanRead)
        {
            throw new ArgumentException("The schema stream must be readable.", nameof(schemaStream));
        }

        using var reader = new StreamReader(
            schemaStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new InvoiceExtractionSchema(json, JsonSchema.FromText(json));
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException)
        {
            throw new InvalidOperationException("The canonical extraction schema is invalid.", exception);
        }
    }
}

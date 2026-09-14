namespace InvoiceReviewAssistant.Infrastructure.Configuration;

/// <summary>
/// Configuration for application-managed document storage.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InvoiceReviewAssistant");

    public TimeSpan StagingMaximumAge { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Limits applied before a document enters managed storage.
/// </summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    public long MaximumBytes { get; init; } = 20L * 1024 * 1024;

    public int MaximumPageCount { get; init; } = 25;
}

/// <summary>
/// Thresholds used to decide whether native PDF text is usable for extraction.
/// </summary>
public sealed class NativeTextOptions
{
    public const string SectionName = "NativeText";

    public int MinimumMeaningfulCharacters { get; init; } = 100;

    public decimal MinimumMeaningfulCharacterRatio { get; init; } = 0.60m;

    public int MinimumMeaningfulCharactersPerCoveredPage { get; init; } = 20;

    public decimal MinimumCoveredPageRatio { get; init; } = 0.50m;
}

/// <summary>
/// Settings for rendering PDF pages before OCR.
/// </summary>
public sealed class PdfRenderingOptions
{
    public const string SectionName = "PdfRendering";

    public int Dpi { get; init; } = 300;

    public bool SerializeRendering { get; init; } = true;
}

/// <summary>
/// Settings for the replaceable local OCR adapter.
/// </summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    public string ExecutablePath { get; init; } = "tesseract";

    public string Language { get; init; } = "eng";

    public TimeSpan PageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan DocumentTimeout { get; init; } = TimeSpan.FromSeconds(300);
}

/// <summary>
/// Selects the extraction adapter and its bounded operation profile.
/// </summary>
public sealed class ExtractionOptions
{
    public const string SectionName = "Extraction";

    public string Provider { get; init; } = "OpenAI";

    public string SchemaVersion { get; init; } = "v1";

    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public ExtractionProfile Profile { get; init; } = ExtractionProfile.Real;
}

/// <summary>
/// Determines whether startup requires credentials for a real extraction provider.
/// </summary>
public enum ExtractionProfile
{
    Real,
    Deterministic,
    ContractGeneration,
}

/// <summary>
/// Settings specific to the OpenAI Responses adapter. ApiKey is intentionally omitted
/// from committed configuration and is supplied through user secrets or the environment.
/// </summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public string Model { get; init; } = "gpt-5.6-terra";

    public string ReasoningEffort { get; init; } = "low";

    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaximumRetries { get; init; } = 1;

    public bool StoreResponses { get; init; } = false;

    public string? ApiKey { get; init; }
}

/// <summary>
/// The accepted two-decimal currencies and their reconciliation tolerances.
/// </summary>
public sealed class CurrenciesOptions
{
    public const string SectionName = "Currencies";

    public Dictionary<string, decimal> Tolerances { get; init; } =
        new(StringComparer.Ordinal)
        {
            ["USD"] = 0.01m,
            ["EUR"] = 0.01m,
            ["GBP"] = 0.01m,
            ["MKD"] = 0.01m,
        };
}

/// <summary>
/// Presentation thresholds for provider-supplied extraction confidence.
/// </summary>
public sealed class ConfidenceOptions
{
    public const string SectionName = "Confidence";

    public decimal HighThreshold { get; init; } = 0.90m;

    public decimal MediumThreshold { get; init; } = 0.70m;
}

/// <summary>
/// The single development origin allowed to call the API from Vite.
/// </summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string DevelopmentOrigin { get; init; } = "http://localhost:5173";
}

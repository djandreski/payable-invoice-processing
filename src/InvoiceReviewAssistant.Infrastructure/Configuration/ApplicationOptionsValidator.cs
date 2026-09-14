using System.Net;
using System.Text.RegularExpressions;

namespace InvoiceReviewAssistant.Infrastructure.Configuration;

/// <summary>
/// Produces configuration-area-specific errors suitable for safe startup diagnostics.
/// It intentionally never includes configured values in errors because values can be sensitive.
/// </summary>
public static partial class ApplicationOptionsValidator
{
    public static OptionsValidationResult Validate(
        StorageOptions storage,
        UploadOptions upload,
        NativeTextOptions nativeText,
        PdfRenderingOptions pdfRendering,
        OcrOptions ocr,
        ExtractionOptions extraction,
        OpenAiOptions openAi,
        CurrenciesOptions currencies,
        ConfidenceOptions confidence,
        CorsOptions cors)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(nativeText);
        ArgumentNullException.ThrowIfNull(pdfRendering);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(openAi);
        ArgumentNullException.ThrowIfNull(currencies);
        ArgumentNullException.ThrowIfNull(confidence);
        ArgumentNullException.ThrowIfNull(cors);

        var errors = new List<OptionsValidationError>();
        ValidateStorage(storage, errors);
        ValidateUpload(upload, errors);
        ValidateNativeText(nativeText, errors);
        ValidatePdfRendering(pdfRendering, errors);
        ValidateOcr(ocr, errors);
        ValidateExtraction(extraction, openAi, errors);
        ValidateCurrencies(currencies, errors);
        ValidateConfidence(confidence, errors);
        ValidateCors(cors, errors);

        return new OptionsValidationResult(errors);
    }

    private static void ValidateStorage(StorageOptions options, ICollection<OptionsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath))
        {
            errors.Add(new(StorageOptions.SectionName, "RootPath must be an absolute application-managed path."));
        }

        if (options.StagingMaximumAge <= TimeSpan.Zero)
        {
            errors.Add(new(StorageOptions.SectionName, "StagingMaximumAge must be greater than zero."));
        }
    }

    private static void ValidateUpload(UploadOptions options, ICollection<OptionsValidationError> errors)
    {
        if (options.MaximumBytes <= 0)
        {
            errors.Add(new(UploadOptions.SectionName, "MaximumBytes must be greater than zero."));
        }

        if (options.MaximumPageCount <= 0)
        {
            errors.Add(new(UploadOptions.SectionName, "MaximumPageCount must be greater than zero."));
        }
    }

    private static void ValidateNativeText(NativeTextOptions options, ICollection<OptionsValidationError> errors)
    {
        if (options.MinimumMeaningfulCharacters <= 0 || options.MinimumMeaningfulCharactersPerCoveredPage <= 0)
        {
            errors.Add(new(NativeTextOptions.SectionName, "Meaningful-character thresholds must be greater than zero."));
        }

        if (!IsUnitInterval(options.MinimumMeaningfulCharacterRatio) || !IsUnitInterval(options.MinimumCoveredPageRatio))
        {
            errors.Add(new(NativeTextOptions.SectionName, "Text and page coverage ratios must be between zero and one."));
        }
    }

    private static void ValidatePdfRendering(PdfRenderingOptions options, ICollection<OptionsValidationError> errors)
    {
        if (options.Dpi is < 72 or > 600)
        {
            errors.Add(new(PdfRenderingOptions.SectionName, "Dpi must be between 72 and 600."));
        }
    }

    private static void ValidateOcr(OcrOptions options, ICollection<OptionsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            errors.Add(new(OcrOptions.SectionName, "ExecutablePath is required."));
        }

        if (string.IsNullOrWhiteSpace(options.Language) || !LanguageCodePattern().IsMatch(options.Language))
        {
            errors.Add(new(OcrOptions.SectionName, "Language must be a three-letter Tesseract language code."));
        }

        if (options.PageTimeout <= TimeSpan.Zero || options.DocumentTimeout <= TimeSpan.Zero)
        {
            errors.Add(new(OcrOptions.SectionName, "PageTimeout and DocumentTimeout must be greater than zero."));
        }
        else if (options.DocumentTimeout <= options.PageTimeout)
        {
            errors.Add(new(OcrOptions.SectionName, "DocumentTimeout must be greater than PageTimeout."));
        }
    }

    private static void ValidateExtraction(
        ExtractionOptions extraction,
        OpenAiOptions openAi,
        ICollection<OptionsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(extraction.Provider))
        {
            errors.Add(new(ExtractionOptions.SectionName, "Provider is required."));
        }

        if (string.IsNullOrWhiteSpace(extraction.SchemaVersion))
        {
            errors.Add(new(ExtractionOptions.SectionName, "SchemaVersion is required."));
        }

        if (extraction.OverallTimeout <= TimeSpan.Zero)
        {
            errors.Add(new(ExtractionOptions.SectionName, "OverallTimeout must be greater than zero."));
        }

        if (!Enum.IsDefined(extraction.Profile))
        {
            errors.Add(new(ExtractionOptions.SectionName, "Profile must be a supported startup profile."));
        }

        if (!string.Equals(extraction.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new(ExtractionOptions.SectionName, "Provider must name a supported extraction adapter."));
        }

        if (string.IsNullOrWhiteSpace(openAi.Model))
        {
            errors.Add(new(OpenAiOptions.SectionName, "Model is required."));
        }

        if (!string.Equals(openAi.ReasoningEffort, "low", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new(OpenAiOptions.SectionName, "ReasoningEffort must be low for the accepted provider profile."));
        }

        if (openAi.NetworkTimeout <= TimeSpan.Zero || extraction.OverallTimeout <= openAi.NetworkTimeout)
        {
            errors.Add(new(OpenAiOptions.SectionName, "NetworkTimeout must be positive and shorter than Extraction:OverallTimeout."));
        }

        if (openAi.MaximumRetries is < 0 or > 1)
        {
            errors.Add(new(OpenAiOptions.SectionName, "MaximumRetries must be zero or one."));
        }

        if (openAi.StoreResponses)
        {
            errors.Add(new(OpenAiOptions.SectionName, "StoreResponses must be false."));
        }

        if (extraction.Profile == ExtractionProfile.Real && string.IsNullOrWhiteSpace(openAi.ApiKey))
        {
            errors.Add(new(OpenAiOptions.SectionName, "ApiKey is required for the real-provider startup profile."));
        }
    }

    private static void ValidateCurrencies(CurrenciesOptions options, ICollection<OptionsValidationError> errors)
    {
        if (options.Tolerances is null || options.Tolerances.Count == 0)
        {
            errors.Add(new(CurrenciesOptions.SectionName, "At least one enabled currency tolerance is required."));
            return;
        }

        foreach (var (code, tolerance) in options.Tolerances)
        {
            if (string.IsNullOrWhiteSpace(code) || !CurrencyCodePattern().IsMatch(code))
            {
                errors.Add(new(CurrenciesOptions.SectionName, "Currency codes must be uppercase ISO 4217 codes."));
            }

            if (tolerance <= 0 || decimal.Round(tolerance, 2) != tolerance)
            {
                errors.Add(new(CurrenciesOptions.SectionName, "Currency tolerances must be positive two-decimal values."));
            }
        }
    }

    private static void ValidateConfidence(ConfidenceOptions options, ICollection<OptionsValidationError> errors)
    {
        if (!IsUnitInterval(options.HighThreshold) || !IsUnitInterval(options.MediumThreshold) ||
            options.MediumThreshold >= options.HighThreshold)
        {
            errors.Add(new(ConfidenceOptions.SectionName, "MediumThreshold and HighThreshold must be ordered values between zero and one."));
        }
    }

    private static void ValidateCors(CorsOptions options, ICollection<OptionsValidationError> errors)
    {
        if (!Uri.TryCreate(options.DevelopmentOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            !string.Equals(origin.OriginalString, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) ||
            !IsLoopbackHost(origin.Host))
        {
            errors.Add(new(CorsOptions.SectionName, "DevelopmentOrigin must be one exact loopback HTTP(S) origin without a path, query, or fragment."));
        }
    }

    private static bool IsUnitInterval(decimal value) => value is >= 0m and <= 1m;

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    [GeneratedRegex("^[a-z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyCodePattern();
}

public sealed record OptionsValidationError(string Section, string Message);

public sealed class OptionsValidationResult
{
    public OptionsValidationResult(IReadOnlyCollection<OptionsValidationError> errors)
    {
        Errors = errors;
    }

    public IReadOnlyCollection<OptionsValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;
}

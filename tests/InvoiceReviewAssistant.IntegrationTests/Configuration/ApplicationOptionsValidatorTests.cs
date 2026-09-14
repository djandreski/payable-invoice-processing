using InvoiceReviewAssistant.Infrastructure.Configuration;
using System.Text.Json;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Configuration;

public sealed class ApplicationOptionsValidatorTests
{
    [Fact]
    public void Defaults_encode_the_accepted_backend_policy()
    {
        var storage = new StorageOptions();
        var upload = new UploadOptions();
        var nativeText = new NativeTextOptions();
        var rendering = new PdfRenderingOptions();
        var ocr = new OcrOptions();
        var extraction = new ExtractionOptions();
        var openAi = new OpenAiOptions();
        var currencies = new CurrenciesOptions();
        var confidence = new ConfidenceOptions();
        var cors = new CorsOptions();

        Assert.True(Path.IsPathFullyQualified(storage.RootPath));
        Assert.Equal(TimeSpan.FromHours(24), storage.StagingMaximumAge);
        Assert.Equal(20L * 1024 * 1024, upload.MaximumBytes);
        Assert.Equal(25, upload.MaximumPageCount);
        Assert.Equal(100, nativeText.MinimumMeaningfulCharacters);
        Assert.Equal(0.60m, nativeText.MinimumMeaningfulCharacterRatio);
        Assert.Equal(20, nativeText.MinimumMeaningfulCharactersPerCoveredPage);
        Assert.Equal(0.50m, nativeText.MinimumCoveredPageRatio);
        Assert.Equal(300, rendering.Dpi);
        Assert.True(rendering.SerializeRendering);
        Assert.Equal("tesseract", ocr.ExecutablePath);
        Assert.Equal("eng", ocr.Language);
        Assert.Equal(TimeSpan.FromSeconds(30), ocr.PageTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), ocr.DocumentTimeout);
        Assert.Equal("OpenAI", extraction.Provider);
        Assert.Equal("v1", extraction.SchemaVersion);
        Assert.Equal(TimeSpan.FromSeconds(90), extraction.OverallTimeout);
        Assert.Equal(ExtractionProfile.Real, extraction.Profile);
        Assert.Equal("gpt-5.6-terra", openAi.Model);
        Assert.Equal("low", openAi.ReasoningEffort);
        Assert.Equal(TimeSpan.FromSeconds(60), openAi.NetworkTimeout);
        Assert.Equal(1, openAi.MaximumRetries);
        Assert.False(openAi.StoreResponses);
        Assert.Equal(new Dictionary<string, decimal>
        {
            ["USD"] = 0.01m,
            ["EUR"] = 0.01m,
            ["GBP"] = 0.01m,
            ["MKD"] = 0.01m,
        }, currencies.Tolerances);
        Assert.Equal(0.90m, confidence.HighThreshold);
        Assert.Equal(0.70m, confidence.MediumThreshold);
        Assert.Equal("http://localhost:5173", cors.DevelopmentOrigin);
    }

    [Theory]
    [InlineData(ExtractionProfile.Deterministic)]
    [InlineData(ExtractionProfile.ContractGeneration)]
    public void Deterministic_startup_profiles_do_not_require_an_api_key(ExtractionProfile profile)
    {
        var result = ApplicationOptionsValidatorTestSupport.Validate(extraction: new ExtractionOptions { Profile = profile });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Real_provider_profile_rejects_a_missing_key_without_echoing_it()
    {
        var result = ApplicationOptionsValidatorTestSupport.Validate();

        var error = Assert.Single(result.Errors);
        Assert.Equal(OpenAiOptions.SectionName, error.Section);
        Assert.Contains("ApiKey is required", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_reports_representative_invalid_combinations_by_configuration_area()
    {
        var result = ApplicationOptionsValidator.Validate(
            new StorageOptions { RootPath = "relative-storage", StagingMaximumAge = TimeSpan.Zero },
            new UploadOptions { MaximumBytes = 0, MaximumPageCount = 0 },
            new NativeTextOptions { MinimumMeaningfulCharacterRatio = 1.1m, MinimumCoveredPageRatio = -0.1m },
            new PdfRenderingOptions { Dpi = 601 },
            new OcrOptions { ExecutablePath = "", Language = "English", PageTimeout = TimeSpan.FromSeconds(30), DocumentTimeout = TimeSpan.FromSeconds(30) },
            new ExtractionOptions { OverallTimeout = TimeSpan.FromSeconds(60) },
            new OpenAiOptions { ApiKey = "secret-value", NetworkTimeout = TimeSpan.FromSeconds(60), StoreResponses = true, MaximumRetries = 2 },
            new CurrenciesOptions { Tolerances = new Dictionary<string, decimal> { ["usd"] = 0.001m } },
            new ConfidenceOptions { HighThreshold = 0.70m, MediumThreshold = 0.70m },
            new CorsOptions { DevelopmentOrigin = "https://example.test/path" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Section == StorageOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == UploadOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == NativeTextOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == PdfRenderingOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == OcrOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == OpenAiOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == CurrenciesOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == ConfidenceOptions.SectionName);
        Assert.Contains(result.Errors, error => error.Section == CorsOptions.SectionName);
        Assert.DoesNotContain("secret-value", string.Join(Environment.NewLine, result.Errors.Select(error => error.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void Committed_configuration_is_valid_json_and_does_not_contain_an_openai_key()
    {
        var configurationPath = FindRepositoryFile(Path.Combine("src", "InvoiceReviewAssistant.Api", "appsettings.json"));
        var json = File.ReadAllText(configurationPath);

        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty(OpenAiOptions.SectionName, out var openAi));
        Assert.False(openAi.TryGetProperty(nameof(OpenAiOptions.ApiKey), out _));
        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InvoiceReviewAssistant.sln")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }

        throw new InvalidOperationException("Could not locate the repository root for configuration artifact validation.");
    }
}

file static class ApplicationOptionsValidatorTestSupport
{
    public static OptionsValidationResult Validate(
        StorageOptions? storage = null,
        UploadOptions? upload = null,
        NativeTextOptions? nativeText = null,
        PdfRenderingOptions? pdfRendering = null,
        OcrOptions? ocr = null,
        ExtractionOptions? extraction = null,
        OpenAiOptions? openAi = null,
        CurrenciesOptions? currencies = null,
        ConfidenceOptions? confidence = null,
        CorsOptions? cors = null) =>
        ApplicationOptionsValidator.Validate(
            storage ?? new StorageOptions(),
            upload ?? new UploadOptions(),
            nativeText ?? new NativeTextOptions(),
            pdfRendering ?? new PdfRenderingOptions(),
            ocr ?? new OcrOptions(),
            extraction ?? new ExtractionOptions(),
            openAi ?? new OpenAiOptions(),
            currencies ?? new CurrenciesOptions(),
            confidence ?? new ConfidenceOptions(),
            cors ?? new CorsOptions());
}

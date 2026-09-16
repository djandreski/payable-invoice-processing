#pragma warning disable OPENAI001

using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Extraction;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.ProviderSmoke;

public sealed class InstalledTesseractSmokeTests
{
    [ProviderSmokeFact("INVOICE_REVIEW_RUN_TESSERACT_SMOKE")]
    public async Task Installed_tesseract_5_exposes_configured_english_language_data()
    {
        var executable = Environment.GetEnvironmentVariable("OCR_EXECUTABLE_PATH") ?? "tesseract";
        var service = new TesseractPreflightService(
            new OcrOptions
            {
                ExecutablePath = executable,
                Language = "eng",
                PageTimeout = TimeSpan.FromSeconds(30),
            },
            NullLogger<TesseractPreflightService>.Instance);

        await service.CheckAsync(CancellationToken.None);
    }
}

public sealed class ConfiguredOpenAiSmokeTests
{
    [ProviderSmokeFact("INVOICE_REVIEW_RUN_OPENAI_SMOKE")]
    public async Task Configured_responses_provider_returns_a_valid_strict_invoice_proposal()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENAI_API_KEY must be set for this opt-in smoke test.");
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.6-terra";
        var options = new OpenAiOptions
        {
            ApiKey = apiKey!,
            Model = model,
            ReasoningEffort = "low",
            NetworkTimeout = TimeSpan.FromSeconds(60),
            MaximumRetries = 1,
            StoreResponses = false,
        };
        var schemaPath = FindWorkspaceFile(Path.Combine(
            "src", "InvoiceReviewAssistant.Infrastructure", "Schemas", "invoice_extraction_v1.schema.json"));
        var schema = await new InvoiceExtractionSchemaLoader().LoadFromFileAsync(
            schemaPath,
            CancellationToken.None);
        var provider = new OpenAiInvoiceExtractionProvider(
            OpenAiResponsesClientFactory.Create(options),
            schema,
            options,
            new ExtractionOptions { OverallTimeout = TimeSpan.FromSeconds(90) },
            NullLogger<OpenAiInvoiceExtractionProvider>.Instance);

        var proposal = await provider.ExtractAsync(
            new NormalizedDocumentText(
                "Synthetic Supply Company. Registration REG-SMOKE-01. Invoice SMOKE-0001 dated 2026-09-01, due 2026-10-01, Net 30. PO SMOKE-PO-01. USD subtotal 100.00, tax 20.00, total 120.00.",
                DocumentTextSource.NativeText),
            new ExtractionSchemaVersion(InvoiceExtractionSchema.SelectorVersion),
            CancellationToken.None);

        Assert.Equal("SMOKE-0001", proposal.Fields.InvoiceNumber);
        Assert.Equal("USD", proposal.Fields.Currency);
        Assert.Equal(120m, proposal.Fields.Total);
    }

    private static string FindWorkspaceFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("The checked-in extraction schema could not be located.");
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class ProviderSmokeFactAttribute : FactAttribute
{
    public ProviderSmokeFactAttribute(string optInVariable)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(optInVariable), "1", StringComparison.Ordinal))
        {
            Skip = $"Opt in by setting {optInVariable}=1.";
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hardening;

public sealed class IngestionFailureCatalogTests
{
    private const string SensitiveFilename = "PRIVATE-INVOICE-MARKER.pdf";
    private const string SensitiveText = "PRIVATE-NORMALIZED-INVOICE-TEXT-MARKER";
    private const string SensitiveProviderOutput = "PRIVATE-PROVIDER-OUTPUT-MARKER";
    private const string SensitiveCredential = "sk-private-credential-marker";

    public static TheoryData<ProcessingFailureCase> ProcessingFailures => new()
    {
        new(ProcessingStage.PdfExtraction, ProcessingFailureCode.PdfExtractionFailed, FailureInjectionPoint.NativeText),
        new(ProcessingStage.Ocr, ProcessingFailureCode.PdfRenderFailed, FailureInjectionPoint.Ocr),
        new(ProcessingStage.Ocr, ProcessingFailureCode.OcrUnavailable, FailureInjectionPoint.Ocr),
        new(ProcessingStage.Ocr, ProcessingFailureCode.OcrPageTimeout, FailureInjectionPoint.Ocr),
        new(ProcessingStage.Ocr, ProcessingFailureCode.OcrDocumentTimeout, FailureInjectionPoint.Ocr),
        new(ProcessingStage.Ocr, ProcessingFailureCode.OcrFailed, FailureInjectionPoint.Ocr),
        new(ProcessingStage.AiExtraction, ProcessingFailureCode.AiTimeout, FailureInjectionPoint.Extraction),
        new(ProcessingStage.AiExtraction, ProcessingFailureCode.AiUnavailable, FailureInjectionPoint.Extraction),
        new(ProcessingStage.AiExtraction, ProcessingFailureCode.AiRefused, FailureInjectionPoint.Extraction),
        new(ProcessingStage.AiExtraction, ProcessingFailureCode.AiResponseIncomplete, FailureInjectionPoint.Extraction),
        new(ProcessingStage.Parsing, ProcessingFailureCode.AiResponseInvalid, FailureInjectionPoint.Extraction),
        new(ProcessingStage.AiExtraction, ProcessingFailureCode.ProcessingFailed, FailureInjectionPoint.UnclassifiedExtraction),
    };

    public static TheoryData<string, HttpStatusCode> UploadRejections => new()
    {
        { "REQUEST_VALIDATION_FAILED", HttpStatusCode.BadRequest },
        { "PDF_FILE_REQUIRED", HttpStatusCode.BadRequest },
        { "PDF_EMPTY", HttpStatusCode.BadRequest },
        { "PDF_TYPE_INVALID", HttpStatusCode.BadRequest },
        { "PDF_SIGNATURE_INVALID", HttpStatusCode.BadRequest },
        { "PDF_INVALID", HttpStatusCode.BadRequest },
        { "PDF_ENCRYPTED", HttpStatusCode.BadRequest },
        { "PDF_PAGE_LIMIT_EXCEEDED", HttpStatusCode.BadRequest },
        { "PDF_SIZE_LIMIT_EXCEEDED", HttpStatusCode.RequestEntityTooLarge },
    };

    public static TheoryData<ServerFailureCase> UploadServerFailures => new()
    {
        new("PERSISTENCE_FAILED", HttpStatusCode.InternalServerError, ServerFailureKind.Persistence),
        new("STORAGE_FAILED", HttpStatusCode.InternalServerError, ServerFailureKind.Storage),
        new("UNEXPECTED_ERROR", HttpStatusCode.InternalServerError, ServerFailureKind.Unexpected),
    };

    [Theory]
    [MemberData(nameof(ProcessingFailures))]
    public async Task Every_post_acceptance_failure_code_is_safe_atomic_and_durable(ProcessingFailureCase failureCase)
    {
        var fault = new ProcessingFault(failureCase);
        await using var factory = new HardeningApplicationFactory(services =>
        {
            services.RemoveAll<INativeDocumentTextPath>();
            services.RemoveAll<IWholeDocumentOcr>();
            services.RemoveAll<IInvoiceExtractionProvider>();
            services.AddSingleton<INativeDocumentTextPath>(fault);
            services.AddSingleton<IWholeDocumentOcr>(fault);
            services.AddSingleton<IInvoiceExtractionProvider>(fault);
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, CreatePdf(), SensitiveFilename);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("processingFailed", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fields").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentValidation").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("documentTextSource").ValueKind);
        var failure = root.GetProperty("processingFailure");
        Assert.Equal(ToCamelCase(failureCase.Stage.ToString()), failure.GetProperty("stage").GetString());
        Assert.Equal(ToStableCode(failureCase.Code), failure.GetProperty("code").GetString());
        Assert.Equal(failureCase.SafeMessage, failure.GetProperty("message").GetString());
        Assert.DoesNotContain(SensitiveText, body.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveProviderOutput, body.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveCredential, body.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.RootPath, body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ProcessingFailed), invoice.Status);
        Assert.Equal(failureCase.Stage.ToString(), invoice.ProcessingFailureStage);
        Assert.Equal(failureCase.Code.ToString(), invoice.ProcessingFailureCode);
        Assert.Equal(failureCase.SafeMessage, invoice.ProcessingFailureMessage);
        Assert.Null(invoice.SupplierName);
        Assert.Null(invoice.InvoiceNumber);
        Assert.Null(invoice.CurrentValidationRunId);
        Assert.Equal(0, await context.InvoiceFieldMetadata.CountAsync());
        Assert.Equal(0, await context.ValidationRuns.CountAsync());
        Assert.Equal(0, await context.ValidationResults.CountAsync());
        Assert.Equal(
            [nameof(AuditEventType.InvoiceUploaded), nameof(AuditEventType.ExtractionFailed)],
            await context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).Select(row => row.EventType).ToArrayAsync());

        Assert.Single(Directory.EnumerateFiles(factory.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.QuarantineDirectory));
        factory.AssertAllGeneratedFilesAreContained();

        var durableText = string.Join('\n',
            await context.Invoices.AsNoTracking().Select(row =>
                (row.SupplierName ?? string.Empty) + (row.ProcessingFailureMessage ?? string.Empty)).ToArrayAsync(),
            await context.AuditEvents.AsNoTracking().Select(row => row.DataJson).ToArrayAsync());
        Assert.DoesNotContain(SensitiveText, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveProviderOutput, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveCredential, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.RootPath, durableText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(UploadRejections))]
    public async Task Every_upload_rejection_problem_code_creates_no_resource(
        string code,
        HttpStatusCode expectedStatus)
    {
        await using var factory = new HardeningApplicationFactory(services =>
        {
            services.RemoveAll<IInvoiceUploadAcceptance>();
            services.AddSingleton<IInvoiceUploadAcceptance>(new RejectingAcceptance(code, (int)expectedStatus));
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, CreatePdf(), SensitiveFilename);

        await AssertProblemAsync(response, expectedStatus, code);
        await AssertNoResourceOrFileAsync(factory);
    }

    [Theory]
    [MemberData(nameof(UploadServerFailures))]
    public async Task Upload_server_failures_use_only_safe_problem_details_and_create_no_resource(
        ServerFailureCase failureCase)
    {
        await using var factory = new HardeningApplicationFactory(services =>
        {
            services.RemoveAll<IInvoiceUploadAcceptance>();
            services.AddSingleton<IInvoiceUploadAcceptance>(new ThrowingAcceptance(failureCase.Kind));
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, CreatePdf(), SensitiveFilename);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SensitiveFilename, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveProviderOutput, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveCredential, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.RootPath, raw, StringComparison.OrdinalIgnoreCase);
        await AssertProblemAsync(response, failureCase.Status, failureCase.Code);
        await AssertNoResourceOrFileAsync(factory);
    }

    [Fact]
    public async Task Controlled_failure_logs_and_persistence_exclude_all_sensitive_inputs()
    {
        var fault = new ProcessingFault(new ProcessingFailureCase(
            ProcessingStage.AiExtraction,
            ProcessingFailureCode.AiUnavailable,
            FailureInjectionPoint.Extraction));
        await using var factory = new HardeningApplicationFactory(services =>
        {
            services.RemoveAll<INativeDocumentTextPath>();
            services.RemoveAll<IInvoiceExtractionProvider>();
            services.AddSingleton<INativeDocumentTextPath>(fault);
            services.AddSingleton<IInvoiceExtractionProvider>(fault);
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, CreatePdf(), SensitiveFilename);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var logs = string.Join(Environment.NewLine, factory.Logs.Entries);
        Assert.DoesNotContain(SensitiveFilename, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveText, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveProviderOutput, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveCredential, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.RootPath, logs, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var persisted = string.Join(Environment.NewLine,
            await context.Invoices.AsNoTracking().Select(row =>
                (row.SupplierName ?? string.Empty) + (row.InvoiceNumber ?? string.Empty) +
                (row.ProcessingFailureMessage ?? string.Empty)).ToArrayAsync(),
            await context.AuditEvents.AsNoTracking().Select(row => row.DataJson).ToArrayAsync());
        Assert.DoesNotContain(SensitiveText, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveProviderOutput, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveCredential, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.RootPath, persisted, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string filename)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(file, "file", filename);
        return await client.PostAsync("/api/invoices", multipart);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(
            $"urn:invoice-review-assistant:problem:{expectedCode.ToLowerInvariant().Replace('_', '-')}",
            root.GetProperty("type").GetString());
        Assert.Equal("/api/invoices", root.GetProperty("instance").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentVersion").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
    }

    private static async Task AssertNoResourceOrFileAsync(HardeningApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(0, await context.Invoices.CountAsync());
        Assert.Equal(0, await context.InvoiceDocuments.CountAsync());
        Assert.Equal(0, await context.AuditEvents.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(factory.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.QuarantineDirectory));
        factory.AssertAllGeneratedFilesAreContained();
    }

    internal static byte[] CreatePdf(string text = "SYNTHETIC SAFE PDF CONTENT")
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    private static string ToCamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static string ToStableCode(ProcessingFailureCode code)
    {
        var name = code.ToString();
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
            {
                result.Append('_');
            }

            result.Append(char.ToUpperInvariant(name[index]));
        }

        return result.ToString();
    }

    public sealed record ProcessingFailureCase(
        ProcessingStage Stage,
        ProcessingFailureCode Code,
        FailureInjectionPoint InjectionPoint)
    {
        public string SafeMessage => InjectionPoint == FailureInjectionPoint.UnclassifiedExtraction
            ? "Invoice processing failed. Review the source document and application configuration."
            : $"Safe classified failure: {ToStableCode(Code)}.";

        public override string ToString() => ToStableCode(Code);
    }

    public sealed record ServerFailureCase(string Code, HttpStatusCode Status, ServerFailureKind Kind)
    {
        public override string ToString() => Code;
    }

    public enum FailureInjectionPoint
    {
        NativeText,
        Ocr,
        Extraction,
        UnclassifiedExtraction,
    }

    public enum ServerFailureKind
    {
        Persistence,
        Storage,
        Unexpected,
    }

    private sealed class ProcessingFault(ProcessingFailureCase failureCase) :
        INativeDocumentTextPath,
        IWholeDocumentOcr,
        IInvoiceExtractionProvider
    {
        public Task<NativeDocumentTextResult> ExtractAsync(Stream pdf, CancellationToken cancellationToken)
        {
            if (failureCase.InjectionPoint == FailureInjectionPoint.NativeText)
            {
                throw ClassifiedFailure();
            }

            return Task.FromResult<NativeDocumentTextResult>(
                failureCase.InjectionPoint == FailureInjectionPoint.Ocr
                    ? new NativeDocumentTextResult.RequiresWholeDocumentOcr()
                    : new NativeDocumentTextResult.Usable(
                        new NormalizedDocumentText(SensitiveText, DocumentTextSource.NativeText)));
        }

        public Task<NormalizedDocumentText> ExtractAsync(
            Stream pdf,
            int pageCount,
            CancellationToken cancellationToken)
        {
            if (failureCase.InjectionPoint == FailureInjectionPoint.Ocr)
            {
                throw ClassifiedFailure();
            }

            return Task.FromResult(new NormalizedDocumentText(SensitiveText, DocumentTextSource.Ocr));
        }

        public Task<InvoiceExtractionProposal> ExtractAsync(
            NormalizedDocumentText document,
            ExtractionSchemaVersion schemaVersion,
            CancellationToken cancellationToken)
        {
            if (failureCase.InjectionPoint == FailureInjectionPoint.UnclassifiedExtraction)
            {
                throw new InvalidOperationException(
                    $"{SensitiveProviderOutput} {SensitiveCredential} C:\\private\\invoice.pdf");
            }

            throw ClassifiedFailure();
        }

        private ProcessingProviderException ClassifiedFailure() => new(
            failureCase.Stage,
            failureCase.Code,
            failureCase.SafeMessage,
            new InvalidOperationException(
                $"{SensitiveProviderOutput} {SensitiveCredential} C:\\private\\invoice.pdf"));
    }

    private sealed class RejectingAcceptance(string code, int status) : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) =>
            Task.FromResult<InvoiceUploadAcceptanceResult>(new InvoiceUploadAcceptanceResult.Rejected(
                new InvoiceUploadRejection(code, status, "The synthetic upload was rejected.")));
    }

    private sealed class ThrowingAcceptance(ServerFailureKind kind) : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) => kind switch
            {
                ServerFailureKind.Persistence => throw new DbUpdateException(
                    $"{SensitiveProviderOutput} {SensitiveCredential} C:\\private\\invoice.pdf"),
                ServerFailureKind.Storage => throw new IOException(
                    $"{SensitiveProviderOutput} {SensitiveCredential} C:\\private\\invoice.pdf"),
                _ => throw new InvalidOperationException(
                    $"{SensitiveProviderOutput} {SensitiveCredential} C:\\private\\invoice.pdf"),
            };
    }

    internal sealed class HardeningApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly Action<IServiceCollection>? _configureServices;

        public HardeningApplicationFactory(Action<IServiceCollection>? configureServices = null)
        {
            RootPath = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "invoice-review-hardening-tests",
                Guid.NewGuid().ToString("N")));
            _configureServices = configureServices;
        }

        public string RootPath { get; }

        public string DocumentsDirectory => Path.Combine(RootPath, "documents");

        public string StagingDirectory => Path.Combine(RootPath, "staging");

        public string QuarantineDirectory => Path.Combine(RootPath, "quarantine");

        public CapturingLogProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                    [$"{ExtractionOptions.SectionName}:Profile"] = ExtractionProfile.Deterministic.ToString(),
                }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            if (_configureServices is not null)
            {
                builder.ConfigureServices(_configureServices);
            }
        }

        public void AssertAllGeneratedFilesAreContained()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            var prefix = Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar;
            Assert.All(Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories), path =>
                Assert.StartsWith(prefix, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(RootPath))
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(RootPath, "invoices.db")}");
                SqliteConnection.ClearPool(connection);
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    internal sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyCollection<string> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue($"{category}|{logLevel}|{formatter(state, exception)}|{exception}");
            }
        }
    }
}

using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Controllers;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Approval;
using InvoiceReviewAssistant.Infrastructure.Decisions;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Drafts;
using InvoiceReviewAssistant.Infrastructure.Extraction;
using InvoiceReviewAssistant.Infrastructure.Ingestion;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using InvoiceReviewAssistant.Infrastructure.Pdf;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Queue;
using InvoiceReviewAssistant.Infrastructure.Queries;
using InvoiceReviewAssistant.Infrastructure.Reconciliation;
using InvoiceReviewAssistant.Infrastructure.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
#pragma warning disable OPENAI001
using OpenAI.Responses;
#pragma warning restore OPENAI001

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(serviceProvider => Bind<StorageOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<UploadOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<NativeTextOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<PdfRenderingOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<OcrOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => BindExtractionOptions(
    serviceProvider.GetRequiredService<IConfiguration>(),
    serviceProvider.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton(serviceProvider => Bind<OpenAiOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<CurrenciesOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<ConfidenceOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(serviceProvider => Bind<CorsOptions>(serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(serviceProvider =>
{
    var currencies = serviceProvider.GetRequiredService<CurrenciesOptions>();
    return new CurrencyPolicy(currencies.Tolerances.Select(item => new CurrencyTolerance(item.Key, item.Value)));
});
builder.Services.AddSingleton(_ => new InvoiceValidator());
builder.Services.AddDbContext<InvoiceDbContext>((serviceProvider, options) =>
{
    var storage = serviceProvider.GetRequiredService<StorageOptions>();
    options.UseSqlite($"Data Source={Path.Combine(storage.RootPath, "invoices.db")}");
});
builder.Services.AddScoped<EfInvoiceRepository>();
builder.Services.AddScoped<IInvoiceRepository>(serviceProvider => serviceProvider.GetRequiredService<EfInvoiceRepository>());
builder.Services.AddScoped<IInvoiceUnitOfWork, EfInvoiceUnitOfWork>();
builder.Services.AddSingleton<LocalDocumentStore>();
builder.Services.AddSingleton<IDocumentStore>(serviceProvider => serviceProvider.GetRequiredService<LocalDocumentStore>());
builder.Services.AddSingleton<ILocalDocumentStoreMaintenance>(serviceProvider => serviceProvider.GetRequiredService<LocalDocumentStore>());
builder.Services.AddPdfAcceptanceInspection();
builder.Services.AddNativePdfTextPath();
builder.Services.AddPdfRenderingAndOcr();
builder.Services.AddStartupReconciliation();
builder.Services.AddInvoiceIngestion();
builder.Services.AddInvoiceDraftSaving();
builder.Services.AddExplicitInvoiceValidation();
builder.Services.AddInvoiceApproval();
builder.Services.AddInvoiceRejection();
builder.Services.AddInvoiceQueue();
builder.Services.AddInvoiceReadApis();
builder.Services.AddScoped<EfInvoiceDocumentLookup>();
builder.Services.AddSingleton<InvoiceExtractionSchema>(serviceProvider =>
    new InvoiceExtractionSchemaLoader()
        .LoadFromFileAsync(Path.Combine(AppContext.BaseDirectory, InvoiceExtractionSchema.CanonicalRelativePath))
        .GetAwaiter()
        .GetResult());
builder.Services.AddSingleton<IInvoiceExtractionProvider>(serviceProvider => serviceProvider.GetRequiredService<ExtractionOptions>().Profile switch
{
    ExtractionProfile.Real => CreateRealExtractionProvider(serviceProvider),
    ExtractionProfile.Deterministic or ExtractionProfile.ContractGeneration =>
        new DeterministicInvoiceExtractionProvider(CreateDeterministicProposal()),
    _ => throw new InvalidOperationException("The configured extraction profile is unsupported."),
});
builder.Services.AddControllers().AddJsonOptions(options => InvoiceJsonDefaults.Configure(options.JsonSerializerOptions));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = actionContext =>
    {
        var isUpload = actionContext.HttpContext.Request.Path.Equals("/api/invoices", StringComparison.Ordinal);
        return InvoiceEndpointResults.Problem(
            actionContext.HttpContext,
            StatusCodes.Status400BadRequest,
            "REQUEST_VALIDATION_FAILED",
            "The request is invalid",
            "The request could not be read or contains invalid values.",
            isUpload
                ? new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["file"] = ["The multipart upload could not be read."],
                }
                : null);
    };
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddCors();

var app = builder.Build();
var storage = app.Services.GetRequiredService<StorageOptions>();
var upload = app.Services.GetRequiredService<UploadOptions>();
var nativeText = app.Services.GetRequiredService<NativeTextOptions>();
var rendering = app.Services.GetRequiredService<PdfRenderingOptions>();
var ocr = app.Services.GetRequiredService<OcrOptions>();
var extraction = app.Services.GetRequiredService<ExtractionOptions>();
var openAi = app.Services.GetRequiredService<OpenAiOptions>();
var currencies = app.Services.GetRequiredService<CurrenciesOptions>();
var confidence = app.Services.GetRequiredService<ConfidenceOptions>();
var cors = app.Services.GetRequiredService<CorsOptions>();
var optionResult = ApplicationOptionsValidator.Validate(storage, upload, nativeText, rendering, ocr, extraction, openAi, currencies, confidence, cors);
if (!optionResult.IsValid)
{
    throw new InvalidOperationException(string.Join(" ", optionResult.Errors.Select(error => $"{error.Section}: {error.Message}")));
}

Directory.CreateDirectory(storage.RootPath);
var contractGeneration = extraction.Profile == ExtractionProfile.ContractGeneration;
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var problem = ServerFailure(
        context,
        context.Features.Get<IExceptionHandlerFeature>()?.Error);
    context.Response.StatusCode = problem.Status!.Value;
    context.Response.ContentType = "application/problem+json";
    context.Response.Headers[CorrelationIdMiddleware.HeaderName] = problem.CorrelationId;
    await context.Response.WriteAsJsonAsync(
        problem,
        InvoiceJsonDefaults.Create(),
        contentType: "application/problem+json",
        cancellationToken: context.RequestAborted);
}));
app.UseCors(policy => policy.WithOrigins(cors.DevelopmentOrigin).AllowAnyHeader().AllowAnyMethod());
app.MapOpenApi();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (!contractGeneration)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<InvoiceDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<IStartupStorageReconciler>().ReconcileAsync(CancellationToken.None);
}

app.Run();

static T Bind<T>(IConfiguration configuration) where T : class, new() =>
    configuration.GetSection(typeof(T).GetField("SectionName")!.GetRawConstantValue()!.ToString()!).Get<T>() ?? new T();

static ExtractionOptions BindExtractionOptions(IConfiguration configuration, IHostEnvironment environment)
{
    var configured = Bind<ExtractionOptions>(configuration);
    var profile = string.Equals(configuration["INVOICE_REVIEW_CONTRACT_GENERATION"], "true", StringComparison.OrdinalIgnoreCase)
        ? ExtractionProfile.ContractGeneration
        : environment.IsDevelopment()
            ? ExtractionProfile.Deterministic
            : configured.Profile;
    return new ExtractionOptions
    {
        Provider = configured.Provider,
        SchemaVersion = configured.SchemaVersion,
        OverallTimeout = configured.OverallTimeout,
        Profile = profile,
    };
}

static IInvoiceExtractionProvider CreateRealExtractionProvider(IServiceProvider serviceProvider)
{
    var openAi = serviceProvider.GetRequiredService<OpenAiOptions>();
#pragma warning disable OPENAI001
    var client = OpenAiResponsesClientFactory.Create(openAi);
#pragma warning restore OPENAI001
    return ActivatorUtilities.CreateInstance<OpenAiInvoiceExtractionProvider>(serviceProvider, client);
}

static InvoiceProblemDetails ServerFailure(HttpContext context, Exception? exception) => exception switch
{
    DbUpdateException or System.Data.Common.DbException => InvoiceProblems.Create(
        context,
        StatusCodes.Status500InternalServerError,
        "PERSISTENCE_FAILED",
        "Invoice data could not be saved",
        "The request could not produce a trustworthy persisted result."),
    IOException or UnauthorizedAccessException => InvoiceProblems.Create(
        context,
        StatusCodes.Status500InternalServerError,
        "STORAGE_FAILED",
        "Document storage failed",
        "The source document could not be stored safely."),
    _ => InvoiceProblems.Create(
        context,
        StatusCodes.Status500InternalServerError,
        "UNEXPECTED_ERROR",
        "An unexpected error occurred",
        "The request could not be completed. Try again."),
};

static InvoiceExtractionProposal CreateDeterministicProposal()
{
    var fields = new InvoiceFields(
        "Synthetic Supply Company",
        "REG-001",
        "INV-0001",
        "PO-0001",
        new DateOnly(2026, 9, 1),
        new DateOnly(2026, 10, 1),
        "Net 30",
        30,
        "USD",
        100m,
        20m,
        120m);
    var metadata = Enum.GetValues<InvoiceReviewAssistant.Core.Invoices.InvoiceFieldKey>()
        .Where(field => field != InvoiceReviewAssistant.Core.Invoices.InvoiceFieldKey.ReviewNotes)
        .Select(field => new InvoiceFieldMetadata(
            field,
            fields.GetCanonicalValue(field),
            InvoiceReviewAssistant.Core.Invoices.FieldSource.AiInference,
            InvoiceReviewAssistant.Core.Invoices.FieldSource.AiInference,
            0.95d,
            null))
        .ToArray();
    return new InvoiceExtractionProposal(fields, metadata);
}

public partial class Program;

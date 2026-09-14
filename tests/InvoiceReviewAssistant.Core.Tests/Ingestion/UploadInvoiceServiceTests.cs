using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using Xunit;

namespace InvoiceReviewAssistant.Core.Tests.Ingestion;

public sealed class UploadInvoiceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyPolicy CurrencyPolicy = new(
        [new CurrencyTolerance("USD", 0.01m), new CurrencyTolerance("EUR", 0.01m), new CurrencyTolerance("GBP", 0.01m), new CurrencyTolerance("MKD", 0.01m)]);

    [Fact]
    public async Task Success_uses_no_acceptance_transaction_during_ocr_or_ai_and_enters_review_required()
    {
        var persistence = new RecordingPersistence();
        var native = new DelegateNativePath((_, token) =>
        {
            Assert.False(persistence.TransactionOpen);
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult<NativeDocumentTextResult>(new NativeDocumentTextResult.RequiresWholeDocumentOcr());
        });
        var ocr = new DelegateOcr((_, pages, token) =>
        {
            Assert.False(persistence.TransactionOpen);
            Assert.Equal(2, pages);
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(new NormalizedDocumentText("normalized OCR text", DocumentTextSource.Ocr));
        });
        var provider = new DelegateExtractionProvider((document, _, token) =>
        {
            Assert.False(persistence.TransactionOpen);
            Assert.Equal(DocumentTextSource.Ocr, document.Source);
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(ValidProposal());
        });
        var service = CreateService(persistence, native, ocr, provider);

        var result = await service.UploadAsync(UploadFiles(), CancellationToken.None);

        var created = Assert.IsType<UploadInvoiceResult.Created>(result);
        Assert.Equal(InvoiceStatus.ReviewRequired, created.Invoice.Status);
        Assert.Equal(DocumentTextSource.Ocr, created.Invoice.DocumentTextSource);
        Assert.NotNull(created.Invoice.CurrentValidation);
        Assert.False(created.Invoice.CurrentValidation!.HasErrors);
        Assert.Equal(11, created.Invoice.FieldMetadata.Count);
        Assert.True(persistence.Accepted);
        Assert.True(persistence.Completed);
        Assert.False(persistence.Failed);
        Assert.Equal(
            [AuditEventType.ExtractionCompleted, AuditEventType.ValidationCompleted],
            persistence.CompletionAudits.Select(item => item.Type));
    }

    [Fact]
    public async Task Accepted_request_ignores_caller_disconnect_and_reaches_a_terminal_outcome()
    {
        using var request = new CancellationTokenSource();
        var persistence = new RecordingPersistence { AfterAccept = request.Cancel };
        var provider = new DelegateExtractionProvider((_, _, token) =>
        {
            Assert.True(request.IsCancellationRequested);
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(ValidProposal());
        });
        var service = CreateService(persistence, provider: provider);

        var result = await service.UploadAsync(UploadFiles(), request.Token);

        var created = Assert.IsType<UploadInvoiceResult.Created>(result);
        Assert.Equal(InvoiceStatus.ReviewRequired, created.Invoice.Status);
        Assert.True(persistence.Completed);
    }

    [Fact]
    public async Task Classified_provider_failure_persists_only_safe_failure_data()
    {
        var persistence = new RecordingPersistence();
        var provider = new DelegateExtractionProvider((_, _, _) => throw new ProcessingProviderException(
            ProcessingStage.AiExtraction,
            ProcessingFailureCode.AiUnavailable,
            "The AI provider is temporarily unavailable.",
            new InvalidOperationException("secret provider body C:\\sensitive\\invoice.pdf")));
        var service = CreateService(persistence, provider: provider);

        var result = await service.UploadAsync(UploadFiles(), CancellationToken.None);

        var created = Assert.IsType<UploadInvoiceResult.Created>(result);
        Assert.Equal(InvoiceStatus.ProcessingFailed, created.Invoice.Status);
        Assert.Null(created.Invoice.Draft);
        Assert.Empty(created.Invoice.FieldMetadata);
        Assert.Equal(ProcessingStage.AiExtraction, created.Invoice.ProcessingFailure!.Stage);
        Assert.Equal(ProcessingFailureCode.AiUnavailable, created.Invoice.ProcessingFailure.Code);
        Assert.Equal("The AI provider is temporarily unavailable.", created.Invoice.ProcessingFailure.Message);
        Assert.DoesNotContain("secret", created.Invoice.ProcessingFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(persistence.Failed);
        Assert.False(persistence.Completed);
        Assert.Equal(AuditEventType.ExtractionFailed, persistence.FailureAudit!.Type);
    }

    [Fact]
    public async Task Incomplete_proposal_becomes_parsing_failure_without_partial_values()
    {
        var persistence = new RecordingPersistence();
        var valid = ValidProposal();
        var provider = new DelegateExtractionProvider((_, _, _) => Task.FromResult(
            valid with { FieldMetadata = valid.FieldMetadata.Take(10).ToArray() }));
        var service = CreateService(persistence, provider: provider);

        var result = await service.UploadAsync(UploadFiles(), CancellationToken.None);

        var created = Assert.IsType<UploadInvoiceResult.Created>(result);
        Assert.Equal(InvoiceStatus.ProcessingFailed, created.Invoice.Status);
        Assert.Equal(ProcessingStage.Parsing, created.Invoice.ProcessingFailure!.Stage);
        Assert.Equal(ProcessingFailureCode.AiResponseInvalid, created.Invoice.ProcessingFailure.Code);
        Assert.Null(created.Invoice.Draft);
        Assert.Empty(created.Invoice.FieldMetadata);
        Assert.False(persistence.Completed);
        Assert.True(persistence.Failed);
    }

    [Fact]
    public async Task Failed_completion_is_cleaned_up_as_persistence_failure_without_proposal_leakage()
    {
        var persistence = new RecordingPersistence { ThrowDuringCompletion = true };
        var service = CreateService(persistence);

        var result = await service.UploadAsync(UploadFiles(), CancellationToken.None);

        var created = Assert.IsType<UploadInvoiceResult.Created>(result);
        Assert.Equal(InvoiceStatus.ProcessingFailed, created.Invoice.Status);
        Assert.Equal(ProcessingStage.Persistence, created.Invoice.ProcessingFailure!.Stage);
        Assert.Equal(ProcessingFailureCode.ProcessingFailed, created.Invoice.ProcessingFailure.Code);
        Assert.Null(created.Invoice.Draft);
        Assert.Empty(created.Invoice.FieldMetadata);
        Assert.True(persistence.CompletionAttempted);
        Assert.True(persistence.Failed);
        Assert.Null(persistence.FailedInvoice!.Draft);
    }

    [Fact]
    public async Task Pre_acceptance_rejection_creates_no_resource_or_audit()
    {
        var persistence = new RecordingPersistence();
        var rejection = new InvoiceUploadRejection("PDF_INVALID", 400, "The PDF is invalid.");
        var service = CreateService(
            persistence,
            acceptance: new RejectedAcceptance(rejection));

        var result = await service.UploadAsync(UploadFiles(), CancellationToken.None);

        var rejected = Assert.IsType<UploadInvoiceResult.Rejected>(result);
        Assert.Equal(rejection, rejected.Failure);
        Assert.False(persistence.Accepted);
        Assert.False(persistence.Completed);
        Assert.False(persistence.Failed);
    }

    private static UploadInvoiceService CreateService(
        RecordingPersistence persistence,
        INativeDocumentTextPath? native = null,
        IWholeDocumentOcr? ocr = null,
        IInvoiceExtractionProvider? provider = null,
        IInvoiceUploadAcceptance? acceptance = null) =>
        new(
            acceptance ?? new AcceptedAcceptance(),
            persistence,
            new MemoryDocumentStore(),
            new FixedStorageKeyFactory(),
            native ?? new DelegateNativePath((_, _) => Task.FromResult<NativeDocumentTextResult>(
                new NativeDocumentTextResult.Usable(new NormalizedDocumentText("normalized native text", DocumentTextSource.NativeText)))),
            ocr ?? new DelegateOcr((_, _, _) => throw new InvalidOperationException("OCR was not expected.")),
            provider ?? new DelegateExtractionProvider((_, _, _) => Task.FromResult(ValidProposal())),
            new EmptyInvoiceRepository(),
            new InvoiceValidator(),
            CurrencyPolicy,
            new IngestionExecutionPolicy(new ExtractionSchemaVersion("v1"), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5)),
            new FrozenTimeProvider(Now));

    private static IReadOnlyList<InvoiceUploadFile> UploadFiles() =>
        [new("file", "synthetic.pdf", "application/pdf", new MemoryStream("pdf"u8.ToArray()))];

    private static InvoiceExtractionProposal ValidProposal()
    {
        var fields = new InvoiceFields(
            "Synthetic Supply",
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
        var keys = Enum.GetValues<InvoiceFieldKey>().Where(key => key != InvoiceFieldKey.ReviewNotes);
        return new InvoiceExtractionProposal(
            fields,
            keys.Select(key => new InvoiceFieldMetadata(
                key,
                fields.GetCanonicalValue(key),
                FieldSource.AiInference,
                FieldSource.AiInference,
                0.95,
                null)).ToArray());
    }

    private sealed class AcceptedAcceptance : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) => Task.FromResult<InvoiceUploadAcceptanceResult>(
                new InvoiceUploadAcceptanceResult.Accepted(new AcceptedInvoiceUpload(
                    new StagedDocument(new DocumentStorageKey("00000000000000000000000000000000.upload"), 3, new string('a', 64)),
                    "synthetic.pdf",
                    2)));
    }

    private sealed class RejectedAcceptance(InvoiceUploadRejection rejection) : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) => Task.FromResult<InvoiceUploadAcceptanceResult>(
                new InvoiceUploadAcceptanceResult.Rejected(rejection));
    }

    private sealed class RecordingPersistence : IIngestionPersistence
    {
        public bool TransactionOpen { get; private set; }
        public bool Accepted { get; private set; }
        public bool CompletionAttempted { get; private set; }
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }
        public bool ThrowDuringCompletion { get; init; }
        public Action? AfterAccept { get; init; }
        public IReadOnlyList<AuditEvent> CompletionAudits { get; private set; } = [];
        public AuditEvent? FailureAudit { get; private set; }
        public Invoice? FailedInvoice { get; private set; }

        public Task AcceptAsync(Invoice invoice, StagedDocument stagedDocument, AuditEvent uploadAudit, CancellationToken cancellationToken)
        {
            TransactionOpen = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(AuditEventType.InvoiceUploaded, uploadAudit.Type);
                Accepted = true;
            }
            finally
            {
                TransactionOpen = false;
            }

            AfterAccept?.Invoke();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(Invoice invoice, ValidationRun validationRun, IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken)
        {
            Assert.False(TransactionOpen);
            CompletionAttempted = true;
            CompletionAudits = auditEvents;
            if (ThrowDuringCompletion)
            {
                throw new InvalidOperationException("Injected persistence failure.");
            }

            Completed = true;
            return Task.CompletedTask;
        }

        public Task FailAsync(Invoice invoice, AuditEvent failureAudit, CancellationToken cancellationToken)
        {
            Assert.False(TransactionOpen);
            cancellationToken.ThrowIfCancellationRequested();
            Failed = true;
            FailedInvoice = invoice;
            FailureAudit = failureAudit;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryDocumentStore : IDocumentStore
    {
        public Task<Stream> OpenReadAsync(DocumentStorageKey key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream("pdf"u8.ToArray()));
        public Task<StagedDocument> StageAsync(Stream source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredDocument> CommitAsync(StagedDocument staged, DocumentStorageKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DocumentIntegrityResult> CheckIntegrityAsync(StoredDocumentDescriptor document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(DocumentStorageKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedStorageKeyFactory : IDocumentStorageKeyFactory
    {
        public DocumentStorageKey CreateDocumentKey() => new("11111111111111111111111111111111.pdf");
    }

    private sealed class DelegateNativePath(Func<Stream, CancellationToken, Task<NativeDocumentTextResult>> handler) : INativeDocumentTextPath
    {
        public Task<NativeDocumentTextResult> ExtractAsync(Stream pdf, CancellationToken cancellationToken) => handler(pdf, cancellationToken);
    }

    private sealed class DelegateOcr(Func<Stream, int, CancellationToken, Task<NormalizedDocumentText>> handler) : IWholeDocumentOcr
    {
        public Task<NormalizedDocumentText> ExtractAsync(Stream pdf, int pageCount, CancellationToken cancellationToken) => handler(pdf, pageCount, cancellationToken);
    }

    private sealed class DelegateExtractionProvider(Func<NormalizedDocumentText, ExtractionSchemaVersion, CancellationToken, Task<InvoiceExtractionProposal>> handler) : IInvoiceExtractionProvider
    {
        public Task<InvoiceExtractionProposal> ExtractAsync(NormalizedDocumentText document, ExtractionSchemaVersion schemaVersion, CancellationToken cancellationToken) => handler(document, schemaVersion, cancellationToken);
    }

    private sealed class EmptyInvoiceRepository : IInvoiceRepository
    {
        public Task<Invoice?> GetAsync(InvoiceId id, CancellationToken cancellationToken) => Task.FromResult<Invoice?>(null);
        public Task AddAsync(Invoice invoice, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateKey key, InvoiceId excludeId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DuplicateCandidate>>([]);
        public Task<InvoicePage> SearchAsync(InvoiceSearch criteria, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

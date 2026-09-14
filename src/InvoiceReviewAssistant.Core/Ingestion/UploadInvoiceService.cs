using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Core.Ingestion;

/// <summary>
/// Coordinates synchronous upload processing while deliberately ending the request-token
/// lifetime at durable acceptance. Every accepted invoice reaches a terminal processing
/// outcome using an independent bounded token.
/// </summary>
public sealed class UploadInvoiceService(
    IInvoiceUploadAcceptance acceptanceService,
    IIngestionPersistence persistence,
    IDocumentStore documentStore,
    IDocumentStorageKeyFactory storageKeyFactory,
    INativeDocumentTextPath nativeTextPath,
    IWholeDocumentOcr ocrProcessor,
    IInvoiceExtractionProvider extractionProvider,
    IInvoiceRepository invoiceRepository,
    InvoiceValidator validator,
    CurrencyPolicy currencyPolicy,
    IngestionExecutionPolicy executionPolicy,
    TimeProvider timeProvider)
{
    public async Task<UploadInvoiceResult> UploadAsync(
        IReadOnlyList<InvoiceUploadFile> files,
        CancellationToken requestCancellationToken)
    {
        var acceptance = await acceptanceService.AcceptAsync(files, requestCancellationToken);
        if (acceptance is InvoiceUploadAcceptanceResult.Rejected rejected)
        {
            return new UploadInvoiceResult.Rejected(rejected.Failure);
        }

        var accepted = ((InvoiceUploadAcceptanceResult.Accepted)acceptance).Upload;
        var invoiceId = InvoiceId.New();
        var acceptedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var document = new InvoiceDocument(
            storageKeyFactory.CreateDocumentKey(),
            accepted.OriginalFilename,
            accepted.StagedDocument.ByteLength,
            accepted.StagedDocument.Sha256,
            accepted.PageCount,
            DocumentIntegrityStatus.Available);
        var processingInvoice = Invoice.CreateProcessing(invoiceId, document, acceptedAtUtc);
        var uploadAudit = new AuditEvent(
            null,
            invoiceId,
            AuditEventType.InvoiceUploaded,
            AuditActor.Reviewer,
            acceptedAtUtc,
            processingInvoice.DraftVersion,
            new InvoiceUploadedAuditDetails(document));

        // Before this returns there is no accepted resource, so request cancellation is
        // authoritative and compensation removes staged/final files on any failure.
        await persistence.AcceptAsync(
            processingInvoice,
            accepted.StagedDocument,
            uploadAudit,
            requestCancellationToken);

        using var completion = new CancellationTokenSource(executionPolicy.CompletionTimeout);
        return await ProcessAcceptedAsync(processingInvoice, accepted.PageCount, completion.Token);
    }

    private async Task<UploadInvoiceResult> ProcessAcceptedAsync(
        Invoice processingInvoice,
        int pageCount,
        CancellationToken completionToken)
    {
        var stage = ProcessingStage.PdfExtraction;
        try
        {
            NormalizedDocumentText normalizedText;
            await using (var pdf = await documentStore.OpenReadAsync(
                processingInvoice.Document.StorageKey,
                completionToken))
            {
                var native = await nativeTextPath.ExtractAsync(pdf, completionToken);
                if (native is NativeDocumentTextResult.Usable usable)
                {
                    normalizedText = usable.Document;
                }
                else
                {
                    stage = ProcessingStage.Ocr;
                    await using var ocrPdf = await documentStore.OpenReadAsync(
                        processingInvoice.Document.StorageKey,
                        completionToken);
                    normalizedText = await ocrProcessor.ExtractAsync(ocrPdf, pageCount, completionToken);
                }
            }

            stage = ProcessingStage.AiExtraction;
            var proposal = await extractionProvider.ExtractAsync(
                normalizedText,
                executionPolicy.SchemaVersion,
                completionToken);
            EnsureCompleteProposal(proposal);
            stage = ProcessingStage.Persistence;

            var completedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
            var draft = new InvoiceDraft(proposal.Fields, null);
            var snapshot = new InvoiceSnapshot(
                processingInvoice.Id,
                InvoiceStatus.Processing,
                draft,
                proposal.FieldMetadata.ToDictionary(item => item.Field),
                processingInvoice.DraftVersion,
                null,
                null,
                processingInvoice.CreatedAtUtc,
                processingInvoice.UpdatedAtUtc,
                null,
                null);
            var duplicateKey = string.IsNullOrWhiteSpace(proposal.Fields.SupplierName) ||
                string.IsNullOrWhiteSpace(proposal.Fields.InvoiceNumber)
                ? null
                : DuplicateKey.Create(proposal.Fields.SupplierName, proposal.Fields.InvoiceNumber);
            var duplicates = duplicateKey is null
                ? []
                : await invoiceRepository.FindDuplicatesAsync(
                    duplicateKey,
                    processingInvoice.Id,
                    completionToken);
            var evaluation = validator.Validate(snapshot, currencyPolicy, duplicates, timeProvider);
            var validationRun = new ValidationRun(
                ValidationRunId.New(),
                processingInvoice.DraftVersion,
                completedAtUtc,
                evaluation.Results);

            processingInvoice.CompleteExtraction(
                draft,
                proposal.FieldMetadata,
                normalizedText.Source,
                validationRun,
                completedAtUtc);
            var extractedFieldCount = proposal.FieldMetadata.Count(item => item.OriginalValue.Kind != CanonicalValueKind.Null);
            AuditEvent[] audits =
            [
                new(
                    null,
                    processingInvoice.Id,
                    AuditEventType.ExtractionCompleted,
                    AuditActor.System,
                    completedAtUtc,
                    processingInvoice.DraftVersion,
                    new ExtractionCompletedAuditDetails(normalizedText.Source, extractedFieldCount)),
                new(
                    null,
                    processingInvoice.Id,
                    AuditEventType.ValidationCompleted,
                    AuditActor.System,
                    completedAtUtc,
                    processingInvoice.DraftVersion,
                    new ValidationCompletedAuditDetails(
                        ValidationTrigger.Initial,
                        validationRun.Id,
                        validationRun.WarningCount,
                        validationRun.ErrorCount,
                        InvoiceStatus.ReviewRequired)),
            ];

            await persistence.CompleteAsync(processingInvoice, validationRun, audits, completionToken);
            return new UploadInvoiceResult.Created(processingInvoice);
        }
        catch (ProcessingProviderException exception)
        {
            return await PersistFailureAsync(
                processingInvoice,
                exception.Stage,
                exception.Code,
                exception.Message);
        }
        catch (OperationCanceledException)
        {
            var (code, message) = TimeoutFailure(stage);
            return await PersistFailureAsync(processingInvoice, stage, code, message);
        }
        catch
        {
            return await PersistFailureAsync(
                processingInvoice,
                stage,
                ProcessingFailureCode.ProcessingFailed,
                "Invoice processing failed. Review the source document and application configuration.");
        }
    }

    private async Task<UploadInvoiceResult> PersistFailureAsync(
        Invoice acceptedInvoice,
        ProcessingStage stage,
        ProcessingFailureCode code,
        string safeMessage)
    {
        var failedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        // Completion may already have mutated the in-memory aggregate before a database
        // failure. Recreate only its durable Processing state so no proposal can leak into
        // the controlled failure result or cleanup save.
        var failedInvoice = Invoice.CreateProcessing(
            acceptedInvoice.Id,
            acceptedInvoice.Document,
            acceptedInvoice.CreatedAtUtc);
        var failure = new ProcessingFailure(stage, code, safeMessage, failedAtUtc);
        failedInvoice.FailProcessing(failure, failedAtUtc);
        var audit = new AuditEvent(
            null,
            failedInvoice.Id,
            AuditEventType.ExtractionFailed,
            AuditActor.System,
            failedAtUtc,
            failedInvoice.DraftVersion,
            new ExtractionFailedAuditDetails(failure));

        using var cleanup = new CancellationTokenSource(executionPolicy.FailureSaveTimeout);
        await persistence.FailAsync(failedInvoice, audit, cleanup.Token);
        return new UploadInvoiceResult.Created(failedInvoice);
    }

    private static void EnsureCompleteProposal(InvoiceExtractionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.FieldMetadata.Count != 11 ||
            proposal.FieldMetadata.Select(item => item.Field).Distinct().Count() != 11 ||
            proposal.FieldMetadata.Any(item => item.Field == InvoiceFieldKey.ReviewNotes))
        {
            throw new ProcessingProviderException(
                ProcessingStage.Parsing,
                ProcessingFailureCode.AiResponseInvalid,
                "The AI extraction response could not be validated.");
        }
    }

    private static (ProcessingFailureCode Code, string Message) TimeoutFailure(ProcessingStage stage) => stage switch
    {
        ProcessingStage.PdfExtraction => (
            ProcessingFailureCode.PdfExtractionFailed,
            "PDF text extraction did not complete."),
        ProcessingStage.Ocr => (
            ProcessingFailureCode.OcrDocumentTimeout,
            "Text recognition exceeded the configured document deadline."),
        ProcessingStage.AiExtraction => (
            ProcessingFailureCode.AiTimeout,
            "AI extraction exceeded its configured deadline."),
        _ => (
            ProcessingFailureCode.ProcessingFailed,
            "Invoice processing did not complete."),
    };
}

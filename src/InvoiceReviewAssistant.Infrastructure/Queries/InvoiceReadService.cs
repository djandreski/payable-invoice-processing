using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Queries;

/// <summary>
/// Builds the public read model from immutable/current persistence rows. It deliberately
/// does not rehydrate history by replaying the aggregate, because generated sequence IDs
/// and historical validation data belong to their persisted rows.
/// </summary>
public sealed class InvoiceReadService(InvoiceDbContext context)
{
    private const string PersistedSchemaVersion = "1.0";

    public async Task<InvoiceDetailReadModel?> GetDetailAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var row = await context.Invoices.AsNoTracking()
            .Include(invoice => invoice.Document)
            .Include(invoice => invoice.FieldMetadata)
            .SingleOrDefaultAsync(invoice => invoice.Id == invoiceId.Value, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var corrections = await LoadCorrectionsAsync(invoiceId, cancellationToken);
        var validation = row.CurrentValidationRunId is { } runId
            ? await LoadValidationAsync(runId, cancellationToken)
            : null;
        return ToDetail(row, corrections, validation);
    }

    public async Task<InvoiceAuditReadPage?> GetHistoryAsync(
        InvoiceId invoiceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!await context.Invoices.AsNoTracking().AnyAsync(invoice => invoice.Id == invoiceId.Value, cancellationToken))
        {
            return null;
        }

        var query = context.AuditEvents.AsNoTracking()
            .Where(audit => audit.InvoiceId == invoiceId.Value);
        var totalItems = await query.CountAsync(cancellationToken);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        var rows = page > totalPages
            ? []
            : await query
                .OrderBy(audit => audit.OccurredAtUtc)
                .ThenBy(audit => audit.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
        var corrections = await LoadCorrectionsForEventsAsync(rows.Select(row => row.Id), cancellationToken);

        return new InvoiceAuditReadPage(
            rows.Select(row => ToAudit(row, corrections.GetValueOrDefault(row.Id) ?? [])).ToArray(),
            page,
            pageSize,
            totalItems);
    }

    public async Task<InvoiceExportReadResult> GetExportAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var detail = await GetDetailAsync(invoiceId, cancellationToken);
        if (detail is null)
        {
            return new InvoiceExportReadResult.InvoiceNotFound();
        }

        if (detail.Status is not InvoiceStatus.Approved and not InvoiceStatus.Rejected)
        {
            return new InvoiceExportReadResult.StateConflict(detail.DraftVersion.Value);
        }

        var rows = await context.AuditEvents.AsNoTracking()
            .Where(audit => audit.InvoiceId == invoiceId.Value)
            .OrderBy(audit => audit.OccurredAtUtc)
            .ThenBy(audit => audit.Id)
            .ToListAsync(cancellationToken);
        var corrections = await LoadCorrectionsForEventsAsync(rows.Select(row => row.Id), cancellationToken);
        var history = rows.Select(row => ToAudit(row, corrections.GetValueOrDefault(row.Id) ?? [])).ToArray();
        return new InvoiceExportReadResult.Exported(detail, history);
    }

    private async Task<IReadOnlyList<FieldCorrection>> LoadCorrectionsAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var rows = await context.FieldCorrections.AsNoTracking()
            .Where(correction => correction.InvoiceId == invoiceId.Value)
            .OrderBy(correction => correction.OccurredAtUtc)
            .ThenBy(correction => correction.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToCorrection).ToArray();
    }

    private async Task<Dictionary<long, IReadOnlyList<FieldCorrection>>> LoadCorrectionsForEventsAsync(
        IEnumerable<long> eventIds,
        CancellationToken cancellationToken)
    {
        var ids = eventIds.ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await context.FieldCorrections.AsNoTracking()
            .Where(correction => ids.Contains(correction.AuditEventId))
            .OrderBy(correction => correction.OccurredAtUtc)
            .ThenBy(correction => correction.Id)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(correction => correction.AuditEventId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<FieldCorrection>)group.Select(ToCorrection).ToArray());
    }

    private async Task<InvoiceValidationReadRun> LoadValidationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await context.ValidationRuns.AsNoTracking()
            .Include(validation => validation.Results)
            .SingleAsync(validation => validation.Id == runId, cancellationToken);
        var results = run.Results.OrderBy(result => result.Id)
            .Select(result => new InvoiceValidationReadResult(
                Enum.Parse<ValidationCode>(result.RuleCode, ignoreCase: false),
                Enum.Parse<ValidationSeverity>(result.Severity, ignoreCase: false),
                result.Message,
                CanonicalJson.DeserializeFields(result.RelatedFieldsJson),
                DeserializeValidationData(result.RuleCode, result.DataJson)))
            .ToArray();
        return new InvoiceValidationReadRun(
            new ValidationRunId(run.Id),
            new DraftVersion(run.DraftVersion),
            AsUtc(run.ValidatedAtUtc),
            results);
    }

    private static InvoiceDetailReadModel ToDetail(
        InvoiceEntity row,
        IReadOnlyList<FieldCorrection> corrections,
        InvoiceValidationReadRun? validation)
    {
        var document = row.Document ?? throw new InvalidOperationException("Every persisted invoice requires document metadata.");
        var metadata = row.FieldMetadata
            .Select(item => new InvoiceFieldMetadata(
                Enum.Parse<InvoiceFieldKey>(item.FieldKey, ignoreCase: false),
                CanonicalJson.DeserializeValue(item.OriginalValueJson),
                Enum.Parse<FieldSource>(item.OriginalSource, ignoreCase: false),
                Enum.Parse<FieldSource>(item.CurrentSource, ignoreCase: false),
                item.Confidence,
                item.LastCorrectedAtUtc is { } correctedAt ? AsUtc(correctedAt) : null))
            .ToDictionary(item => item.Field);
        var status = Enum.Parse<InvoiceStatus>(row.Status, ignoreCase: false);
        var draft = status == InvoiceStatus.Processing || metadata.Count != 11
            ? null
            : new InvoiceDraft(
                new InvoiceFields(
                    row.SupplierName,
                    row.SupplierRegistrationId,
                    row.InvoiceNumber,
                    row.PurchaseOrderNumber,
                    row.InvoiceDate,
                    row.DueDate,
                    row.PaymentTerms,
                    row.NormalizedPaymentTermsDays,
                    row.Currency,
                    row.Subtotal,
                    row.TaxAmount,
                    row.Total),
                row.ReviewNotes);
        var failure = row.ProcessingFailureStage is null
            ? null
            : new ProcessingFailure(
                Enum.Parse<ProcessingStage>(row.ProcessingFailureStage, ignoreCase: false),
                Enum.Parse<ProcessingFailureCode>(row.ProcessingFailureCode!, ignoreCase: false),
                row.ProcessingFailureMessage!,
                AsUtc(row.ProcessingFailedAtUtc!.Value));
        var decision = row.DecisionKind is null
            ? null
            : new InvoiceDecision(
                Enum.Parse<DecisionKind>(row.DecisionKind, ignoreCase: false),
                AsUtc(row.DecidedAtUtc!.Value),
                row.RejectionReason);

        return new InvoiceDetailReadModel(
            new InvoiceId(row.Id),
            status,
            new DraftVersion(row.DraftVersion),
            row.LastValidatedVersion is { } version ? new DraftVersion(version) : null,
            AsUtc(row.CreatedAtUtc),
            AsUtc(row.UpdatedAtUtc),
            new InvoiceReadDocument(
                document.OriginalFilename,
                document.ByteLength,
                document.Sha256,
                document.PageCount,
                Enum.Parse<DocumentIntegrityStatus>(document.IntegrityStatus, ignoreCase: false)),
            row.DocumentTextSource is null
                ? null
                : Enum.Parse<DocumentTextSource>(row.DocumentTextSource, ignoreCase: false),
            draft,
            metadata,
            validation,
            corrections,
            failure,
            decision);
    }

    private static FieldCorrection ToCorrection(FieldCorrectionEntity row) => new(
        new SequenceId(row.Id),
        new SequenceId(row.AuditEventId),
        Enum.Parse<InvoiceFieldKey>(row.FieldKey, ignoreCase: false),
        CanonicalJson.DeserializeValue(row.PreviousValueJson),
        CanonicalJson.DeserializeValue(row.NewValueJson),
        new DraftVersion(row.DraftVersion),
        AsUtc(row.OccurredAtUtc));

    private static InvoiceAuditReadEvent ToAudit(
        AuditEventEntity row,
        IReadOnlyList<FieldCorrection> linkedCorrections)
    {
        var type = Enum.Parse<AuditEventType>(row.EventType, ignoreCase: false);
        return new InvoiceAuditReadEvent(
            new SequenceId(row.Id),
            new InvoiceId(row.InvoiceId),
            type,
            Enum.Parse<AuditActor>(row.Actor, ignoreCase: false),
            AsUtc(row.OccurredAtUtc),
            new DraftVersion(row.DraftVersion),
            DeserializeAuditDetails(type, row.DataJson, linkedCorrections));
    }

    private static InvoiceAuditReadDetails DeserializeAuditDetails(
        AuditEventType type,
        string json,
        IReadOnlyList<FieldCorrection> linkedCorrections)
    {
        var value = ReadPersistedValue(json);
        return type switch
        {
            AuditEventType.InvoiceUploaded => Uploaded(value),
            AuditEventType.ExtractionCompleted => new ExtractionCompletedReadDetails(
                (DocumentTextSource)value.GetProperty("documentTextSource").GetInt32(),
                value.GetProperty("extractedFieldCount").GetInt32()),
            AuditEventType.ExtractionFailed => new ExtractionFailedReadDetails(ReadFailure(value.GetProperty("failure"))),
            // Generated IDs do not exist when DataJson is created. The relationship is the
            // authoritative source for draft-save changes and is also how no-ops are known.
            AuditEventType.DraftSaved => new DraftSavedReadDetails(linkedCorrections.Count == 0, linkedCorrections),
            AuditEventType.ValidationCompleted => new ValidationCompletedReadDetails(
                (ValidationTrigger)value.GetProperty("trigger").GetInt32(),
                new ValidationRunId(value.GetProperty("validationRunId").GetProperty("value").GetGuid()),
                value.GetProperty("warningCount").GetInt32(),
                value.GetProperty("errorCount").GetInt32(),
                (InvoiceStatus)value.GetProperty("resultingStatus").GetInt32()),
            AuditEventType.InvoiceApproved => new InvoiceApprovedReadDetails(value.GetProperty("decidedAtUtc").GetDateTimeOffset()),
            AuditEventType.InvoiceRejected => new InvoiceRejectedReadDetails(
                value.GetProperty("decidedAtUtc").GetDateTimeOffset(),
                value.GetProperty("rejectionReason").GetString()!),
            AuditEventType.DocumentIntegrityChanged => new DocumentIntegrityChangedReadDetails(
                (DocumentIntegrityStatus)value.GetProperty("previousStatus").GetInt32(),
                (DocumentIntegrityStatus)value.GetProperty("currentStatus").GetInt32()),
            _ => throw new InvalidOperationException("The persisted audit event type is unsupported."),
        };
    }

    private static InvoiceUploadedReadDetails Uploaded(JsonElement value)
    {
        var document = value.GetProperty("document");
        return new InvoiceUploadedReadDetails(new InvoiceReadDocument(
            document.GetProperty("originalFilename").GetString()!,
            document.GetProperty("byteLength").GetInt64(),
            document.GetProperty("sha256").GetString()!,
            document.GetProperty("pageCount").GetInt32(),
            (DocumentIntegrityStatus)document.GetProperty("integrityStatus").GetInt32()));
    }

    private static ProcessingFailure ReadFailure(JsonElement value) => new(
        (ProcessingStage)value.GetProperty("stage").GetInt32(),
        (ProcessingFailureCode)value.GetProperty("code").GetInt32(),
        value.GetProperty("message").GetString()!,
        value.GetProperty("failedAtUtc").GetDateTimeOffset());

    private static object? DeserializeValidationData(string ruleCode, string? json)
    {
        if (json is null)
        {
            return null;
        }

        var value = ReadPersistedValue(json);
        var code = Enum.Parse<ValidationCode>(ruleCode, ignoreCase: false);
        return code switch
        {
            ValidationCode.RequiredFieldMissing => new RequiredFieldMissingValidationData(
                (InvoiceFieldKey)value.GetProperty("missingField").GetInt32()),
            ValidationCode.AmountReconciliationFailed => new AmountReconciliationFailedValidationData(
                value.GetProperty("currency").GetString()!,
                value.GetProperty("subtotal").GetDecimal(),
                value.GetProperty("taxAmount").GetDecimal(),
                value.GetProperty("expectedTotal").GetDecimal(),
                value.GetProperty("actualTotal").GetDecimal(),
                value.GetProperty("difference").GetDecimal(),
                value.GetProperty("tolerance").GetDecimal()),
            ValidationCode.NegativeAmountUnexpected => new NegativeAmountUnexpectedValidationData(
                (InvoiceFieldKey)value.GetProperty("field").GetInt32(),
                value.GetProperty("amount").GetDecimal()),
            ValidationCode.DueDateBeforeInvoiceDate => new DueDateBeforeInvoiceDateValidationData(
                value.GetProperty("invoiceDate").GetDateOnly(),
                value.GetProperty("dueDate").GetDateOnly()),
            ValidationCode.PaymentTermsMismatch => new PaymentTermsMismatchValidationData(
                value.GetProperty("invoiceDate").GetDateOnly(),
                value.GetProperty("dueDate").GetDateOnly(),
                value.GetProperty("normalizedPaymentTermsDays").GetInt32(),
                value.GetProperty("calculatedDueDate").GetDateOnly()),
            ValidationCode.PossibleDuplicateInvoice => new PossibleDuplicateInvoiceValidationData(
                value.GetProperty("matches").EnumerateArray()
                    .Select(match => new DuplicateInvoiceMatch(
                        new InvoiceId(match.GetProperty("invoiceId").GetProperty("value").GetGuid()),
                        (InvoiceStatus)match.GetProperty("status").GetInt32()))
                    .ToArray()),
            ValidationCode.CurrencyInvalid => new CurrencyInvalidValidationData(
                value.GetProperty("value").ValueKind == JsonValueKind.Null ? null : value.GetProperty("value").GetString(),
                value.GetProperty("allowedCurrencies").EnumerateArray().Select(item => item.GetString()!).ToArray()),
            ValidationCode.LowExtractionConfidence => new LowExtractionConfidenceValidationData(
                (InvoiceFieldKey)value.GetProperty("field").GetInt32(),
                value.GetProperty("confidence").ValueKind == JsonValueKind.Null ? null : value.GetProperty("confidence").GetDouble(),
                (ConfidenceBand)value.GetProperty("confidenceBand").GetInt32()),
            ValidationCode.InvoiceDateInFuture => new InvoiceDateInFutureValidationData(
                value.GetProperty("invoiceDate").GetDateOnly(),
                value.GetProperty("currentLocalDate").GetDateOnly()),
            _ => throw new InvalidOperationException("The persisted validation code is unsupported."),
        };
    }

    private static JsonElement ReadPersistedValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("schemaVersion").GetString(), PersistedSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted data has an unsupported schema version.");
        }

        return root.GetProperty("value").Clone();
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal static class InvoiceReadJsonExtensions
{
    public static DateOnly GetDateOnly(this JsonElement value) =>
        DateOnly.ParseExact(value.GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

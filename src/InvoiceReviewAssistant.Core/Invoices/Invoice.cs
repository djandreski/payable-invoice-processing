using System.Collections.ObjectModel;

namespace InvoiceReviewAssistant.Core.Invoices;

public sealed class Invoice
{
    private static readonly InvoiceFieldKey[] EditableFields =
    [
        InvoiceFieldKey.SupplierName,
        InvoiceFieldKey.SupplierRegistrationId,
        InvoiceFieldKey.InvoiceNumber,
        InvoiceFieldKey.PurchaseOrderNumber,
        InvoiceFieldKey.InvoiceDate,
        InvoiceFieldKey.DueDate,
        InvoiceFieldKey.PaymentTerms,
        InvoiceFieldKey.Currency,
        InvoiceFieldKey.Subtotal,
        InvoiceFieldKey.TaxAmount,
        InvoiceFieldKey.Total,
        InvoiceFieldKey.ReviewNotes
    ];

    private readonly Dictionary<InvoiceFieldKey, InvoiceFieldMetadata> _fieldMetadata;

    private Invoice(
        InvoiceId id,
        InvoiceDocument document,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Document = document;
        CreatedAtUtc = EnsureUtc(createdAtUtc);
        UpdatedAtUtc = CreatedAtUtc;
        Status = InvoiceStatus.Processing;
        DraftVersion = new DraftVersion(1);
        _fieldMetadata = new Dictionary<InvoiceFieldKey, InvoiceFieldMetadata>();
    }

    public InvoiceId Id { get; }

    public InvoiceDocument Document { get; }

    public InvoiceStatus Status { get; private set; }

    public InvoiceDraft? Draft { get; private set; }

    public DocumentTextSource? DocumentTextSource { get; private set; }

    public DraftVersion DraftVersion { get; private set; }

    public DraftVersion? LastValidatedVersion { get; private set; }

    public ValidationRunId? CurrentValidationRunId { get; private set; }

    public ValidationRun? CurrentValidation { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public ProcessingFailure? ProcessingFailure { get; private set; }

    public InvoiceDecision? Decision { get; private set; }

    public IReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata> FieldMetadata => new ReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata>(_fieldMetadata);

    public static Invoice CreateProcessing(InvoiceId id, InvoiceDocument document, DateTimeOffset createdAtUtc) =>
        new(id, document, createdAtUtc);

    public void CompleteExtraction(
        InvoiceDraft draft,
        IEnumerable<InvoiceFieldMetadata> fieldMetadata,
        DocumentTextSource documentTextSource,
        ValidationRun initialValidation,
        DateTimeOffset occurredAtUtc)
    {
        RequireStatus(InvoiceStatus.Processing);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(fieldMetadata);
        ArgumentNullException.ThrowIfNull(initialValidation);
        if (initialValidation.DraftVersion != DraftVersion)
        {
            throw new DomainRuleViolation("The initial validation must belong to the current draft version.");
        }

        var metadata = fieldMetadata.ToDictionary(item => item.Field);
        EnsureExtractableMetadata(metadata);
        Draft = draft;
        _fieldMetadata.Clear();
        foreach (var item in metadata)
        {
            _fieldMetadata.Add(item.Key, item.Value);
        }

        DocumentTextSource = documentTextSource;
        CurrentValidation = initialValidation;
        CurrentValidationRunId = initialValidation.Id;
        LastValidatedVersion = DraftVersion;
        Status = InvoiceStatus.ReviewRequired;
        UpdatedAtUtc = EnsureUtc(occurredAtUtc);
    }

    public void FailProcessing(ProcessingFailure failure, DateTimeOffset occurredAtUtc)
    {
        RequireStatus(InvoiceStatus.Processing);
        ArgumentNullException.ThrowIfNull(failure);
        ProcessingFailure = failure;
        Status = InvoiceStatus.ProcessingFailed;
        UpdatedAtUtc = EnsureUtc(occurredAtUtc);
    }

    public DraftChange SaveDraft(InvoiceDraft proposedDraft, DraftVersion expectedVersion, DateTimeOffset occurredAtUtc)
    {
        RequireEditable();
        RequireExpectedVersion(expectedVersion);
        ArgumentNullException.ThrowIfNull(proposedDraft);

        var timestamp = EnsureUtc(occurredAtUtc);
        var current = Draft ?? throw new DomainRuleViolation("An editable invoice must have a draft.");
        var changes = new List<FieldCorrection>();

        foreach (var field in EditableFields)
        {
            var previous = current.GetCanonicalValue(field);
            var next = proposedDraft.GetCanonicalValue(field);
            if (previous == next)
            {
                continue;
            }

            changes.Add(new FieldCorrection(null, null, field, previous, next, DraftVersion.Next(), timestamp));
        }

        if (changes.Count == 0)
        {
            return DraftChange.NoOp(DraftVersion);
        }

        Draft = proposedDraft;
        DraftVersion = DraftVersion.Next();
        Status = InvoiceStatus.ReviewRequired;
        LastValidatedVersion = null;
        CurrentValidationRunId = null;
        CurrentValidation = null;
        UpdatedAtUtc = timestamp;

        foreach (var change in changes)
        {
            if (_fieldMetadata.TryGetValue(change.Field, out var metadata))
            {
                _fieldMetadata[change.Field] = metadata.MarkCorrected(timestamp);
            }
        }

        return new DraftChange(false, DraftVersion, changes);
    }

    public void ApplyValidation(ValidationRun validation, ValidationTrigger trigger, DraftVersion expectedVersion, DateTimeOffset occurredAtUtc)
    {
        RequireEditable();
        RequireExpectedVersion(expectedVersion);
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.DraftVersion != DraftVersion)
        {
            throw new DomainRuleViolation("Validation must belong to the current draft version.");
        }

        if (trigger == ValidationTrigger.Initial)
        {
            throw new DomainRuleViolation("Initial validation is applied only while completing extraction.");
        }

        CurrentValidation = validation;
        CurrentValidationRunId = validation.Id;
        LastValidatedVersion = DraftVersion;
        Status = validation.HasErrors ? InvoiceStatus.ReviewRequired : InvoiceStatus.ReadyForApproval;
        UpdatedAtUtc = EnsureUtc(occurredAtUtc);
    }

    public void Approve(DraftVersion expectedVersion, DateTimeOffset decidedAtUtc)
    {
        RequireStatus(InvoiceStatus.ReadyForApproval);
        RequireExpectedVersion(expectedVersion);
        if (LastValidatedVersion != DraftVersion || CurrentValidation?.HasErrors != false)
        {
            throw new DomainRuleViolation("Approval requires the current draft to have a validation without errors.");
        }

        var timestamp = EnsureUtc(decidedAtUtc);
        Decision = new InvoiceDecision(DecisionKind.Approved, timestamp, null);
        Status = InvoiceStatus.Approved;
        UpdatedAtUtc = timestamp;
    }

    public void Reject(DraftVersion expectedVersion, string reason, DateTimeOffset decidedAtUtc)
    {
        if (Status is not InvoiceStatus.ReviewRequired and not InvoiceStatus.ReadyForApproval)
        {
            throw new DomainRuleViolation("Only reviewable invoices can be rejected.");
        }

        RequireExpectedVersion(expectedVersion);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleViolation("A non-blank rejection reason is required.");
        }

        var timestamp = EnsureUtc(decidedAtUtc);
        Decision = new InvoiceDecision(DecisionKind.Rejected, timestamp, reason);
        Status = InvoiceStatus.Rejected;
        UpdatedAtUtc = timestamp;
    }

    public DuplicateKey? GetDuplicateKey()
    {
        var fields = Draft?.Fields;
        return string.IsNullOrWhiteSpace(fields?.SupplierName) || string.IsNullOrWhiteSpace(fields.InvoiceNumber)
            ? null
            : DuplicateKey.Create(fields.SupplierName, fields.InvoiceNumber);
    }

    public InvoiceSnapshot CreateSnapshot() => new(
        Id,
        Status,
        Draft,
        new ReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata>(new Dictionary<InvoiceFieldKey, InvoiceFieldMetadata>(_fieldMetadata)),
        DraftVersion,
        LastValidatedVersion,
        CurrentValidation,
        CreatedAtUtc,
        UpdatedAtUtc,
        ProcessingFailure,
        Decision);

    private void RequireEditable()
    {
        if (Status is InvoiceStatus.Approved or InvoiceStatus.Rejected or InvoiceStatus.ProcessingFailed or InvoiceStatus.Processing)
        {
            throw new DomainRuleViolation($"An invoice in {Status} cannot be edited or validated.");
        }
    }

    private void RequireStatus(InvoiceStatus status)
    {
        if (Status != status)
        {
            throw new DomainRuleViolation($"The invoice must be {status} but is {Status}.");
        }
    }

    private void RequireExpectedVersion(DraftVersion expectedVersion)
    {
        if (DraftVersion != expectedVersion)
        {
            throw new DomainRuleViolation("The supplied draft version is not current.");
        }
    }

    private static void EnsureExtractableMetadata(IReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata> metadata)
    {
        foreach (var field in EditableFields.Where(field => field != InvoiceFieldKey.ReviewNotes))
        {
            if (!metadata.ContainsKey(field))
            {
                throw new DomainRuleViolation($"Missing metadata for {field}.");
            }
        }

        if (metadata.ContainsKey(InvoiceFieldKey.ReviewNotes))
        {
            throw new DomainRuleViolation("Review notes are not an extractable field.");
        }
    }

    private static DateTimeOffset EnsureUtc(DateTimeOffset timestamp) => timestamp.Offset == TimeSpan.Zero
        ? timestamp
        : timestamp.ToUniversalTime();
}

public sealed record DraftChange(bool IsNoOp, DraftVersion CurrentVersion, IReadOnlyList<FieldCorrection> Changes)
{
    public static DraftChange NoOp(DraftVersion version) => new(true, version, Array.Empty<FieldCorrection>());
}

public sealed record InvoiceSnapshot(
    InvoiceId Id,
    InvoiceStatus Status,
    InvoiceDraft? Draft,
    IReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata> FieldMetadata,
    DraftVersion DraftVersion,
    DraftVersion? LastValidatedVersion,
    ValidationRun? CurrentValidation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ProcessingFailure? ProcessingFailure,
    InvoiceDecision? Decision);

public sealed class DomainRuleViolation : InvalidOperationException
{
    public DomainRuleViolation(string message)
        : base(message)
    {
    }
}

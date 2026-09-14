namespace InvoiceReviewAssistant.Core.Invoices;

public enum InvoiceStatus
{
    Processing,
    ReviewRequired,
    ReadyForApproval,
    Approved,
    Rejected,
    ProcessingFailed
}

public enum InvoiceFieldKey
{
    SupplierName,
    SupplierRegistrationId,
    InvoiceNumber,
    PurchaseOrderNumber,
    InvoiceDate,
    DueDate,
    PaymentTerms,
    Currency,
    Subtotal,
    TaxAmount,
    Total,
    ReviewNotes
}

public enum FieldSource
{
    NativeText,
    Ocr,
    AiInference,
    Reviewer
}

public enum DocumentTextSource
{
    NativeText,
    Ocr
}

public enum ConfidenceBand
{
    High,
    Medium,
    Low,
    Unknown
}

public enum DecisionKind
{
    Approved,
    Rejected
}

public enum DocumentIntegrityStatus
{
    Available,
    Missing,
    Corrupt
}

public enum ValidationSeverity
{
    Warning,
    Error
}

public enum ValidationCode
{
    RequiredFieldMissing,
    AmountReconciliationFailed,
    NegativeAmountUnexpected,
    DueDateBeforeInvoiceDate,
    PaymentTermsMismatch,
    PossibleDuplicateInvoice,
    CurrencyInvalid,
    LowExtractionConfidence,
    InvoiceDateInFuture
}

public enum ValidationTrigger
{
    Initial,
    Explicit,
    Approval
}

public enum ProcessingStage
{
    Upload,
    PdfExtraction,
    Ocr,
    AiExtraction,
    Parsing,
    Persistence,
    StartupRecovery
}

public enum ProcessingFailureCode
{
    PdfExtractionFailed,
    PdfRenderFailed,
    OcrUnavailable,
    OcrPageTimeout,
    OcrDocumentTimeout,
    OcrFailed,
    AiTimeout,
    AiUnavailable,
    AiRefused,
    AiResponseIncomplete,
    AiResponseInvalid,
    ProcessInterrupted,
    ProcessingFailed
}

public enum AuditEventType
{
    InvoiceUploaded,
    ExtractionCompleted,
    ExtractionFailed,
    DraftSaved,
    ValidationCompleted,
    InvoiceApproved,
    InvoiceRejected,
    DocumentIntegrityChanged
}

public enum AuditActor
{
    System,
    Reviewer
}

public enum InvoiceSort
{
    UpdatedAtDescending,
    UpdatedAtAscending,
    CreatedAtDescending,
    CreatedAtAscending
}

public enum CanonicalValueKind
{
    Null,
    Text,
    Date,
    Money
}

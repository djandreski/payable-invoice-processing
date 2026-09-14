using System.Text.Json.Serialization;

namespace InvoiceReviewAssistant.Api.Contracts;

public enum InvoiceStatus { Processing, ReviewRequired, ReadyForApproval, Approved, Rejected, ProcessingFailed }
public enum InvoiceFieldKey { SupplierName, SupplierRegistrationId, InvoiceNumber, PurchaseOrderNumber, InvoiceDate, DueDate, PaymentTerms, Currency, Subtotal, TaxAmount, Total, ReviewNotes }
public enum FieldSource { NativeText, Ocr, AiInference, Reviewer }
public enum DocumentTextSource { NativeText, Ocr }
public enum ConfidenceBand { High, Medium, Low, Unknown }
public enum DecisionKind { Approved, Rejected }
public enum DocumentIntegrityStatus { Available, Missing, Corrupt }
public enum ValidationSeverity { Warning, Error }
public enum ValidationTrigger { Initial, Explicit, Approval }
public enum ProcessingStage { Upload, PdfExtraction, Ocr, AiExtraction, Parsing, Persistence, StartupRecovery }
public enum AuditActor { System, Reviewer }
public enum InvoiceSort { UpdatedAtDesc, UpdatedAtAsc, CreatedAtDesc, CreatedAtAsc }
public enum AuditEventType { InvoiceUploaded, ExtractionCompleted, ExtractionFailed, DraftSaved, ValidationCompleted, InvoiceApproved, InvoiceRejected, DocumentIntegrityChanged }

public enum ValidationCode
{
    [JsonStringEnumMemberName("REQUIRED_FIELD_MISSING")] RequiredFieldMissing,
    [JsonStringEnumMemberName("AMOUNT_RECONCILIATION_FAILED")] AmountReconciliationFailed,
    [JsonStringEnumMemberName("NEGATIVE_AMOUNT_UNEXPECTED")] NegativeAmountUnexpected,
    [JsonStringEnumMemberName("DUE_DATE_BEFORE_INVOICE_DATE")] DueDateBeforeInvoiceDate,
    [JsonStringEnumMemberName("PAYMENT_TERMS_MISMATCH")] PaymentTermsMismatch,
    [JsonStringEnumMemberName("POSSIBLE_DUPLICATE_INVOICE")] PossibleDuplicateInvoice,
    [JsonStringEnumMemberName("CURRENCY_INVALID")] CurrencyInvalid,
    [JsonStringEnumMemberName("LOW_EXTRACTION_CONFIDENCE")] LowExtractionConfidence,
    [JsonStringEnumMemberName("INVOICE_DATE_IN_FUTURE")] InvoiceDateInFuture
}

public enum ProcessingFailureCode
{
    [JsonStringEnumMemberName("PDF_EXTRACTION_FAILED")] PdfExtractionFailed,
    [JsonStringEnumMemberName("PDF_RENDER_FAILED")] PdfRenderFailed,
    [JsonStringEnumMemberName("OCR_UNAVAILABLE")] OcrUnavailable,
    [JsonStringEnumMemberName("OCR_PAGE_TIMEOUT")] OcrPageTimeout,
    [JsonStringEnumMemberName("OCR_DOCUMENT_TIMEOUT")] OcrDocumentTimeout,
    [JsonStringEnumMemberName("OCR_FAILED")] OcrFailed,
    [JsonStringEnumMemberName("AI_TIMEOUT")] AiTimeout,
    [JsonStringEnumMemberName("AI_UNAVAILABLE")] AiUnavailable,
    [JsonStringEnumMemberName("AI_REFUSED")] AiRefused,
    [JsonStringEnumMemberName("AI_RESPONSE_INCOMPLETE")] AiResponseIncomplete,
    [JsonStringEnumMemberName("AI_RESPONSE_INVALID")] AiResponseInvalid,
    [JsonStringEnumMemberName("PROCESS_INTERRUPTED")] ProcessInterrupted,
    [JsonStringEnumMemberName("PROCESSING_FAILED")] ProcessingFailed
}

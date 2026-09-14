using System.ComponentModel.DataAnnotations;

namespace InvoiceReviewAssistant.Api.Contracts;

public sealed class SaveInvoiceDraftRequest { [Range(1, int.MaxValue)] public required int ExpectedVersion { get; init; } public required InvoiceDraftInputDto Draft { get; init; } }
public sealed class InvoiceDraftInputDto { public required SupplierDraftDto Supplier { get; init; } public required ReferenceDraftDto Reference { get; init; } public required DatesAndTermsDraftDto DatesAndTerms { get; init; } public required AmountDraftDto Amounts { get; init; } public required string? ReviewNotes { get; init; } }
public sealed class SupplierDraftDto { public required string? Name { get; init; } public required string? RegistrationId { get; init; } }
public sealed class ReferenceDraftDto { public required string? InvoiceNumber { get; init; } public required string? PurchaseOrderNumber { get; init; } }
public sealed class DatesAndTermsDraftDto { public required DateOnly? InvoiceDate { get; init; } public required DateOnly? DueDate { get; init; } public required string? PaymentTerms { get; init; } }
public sealed class AmountDraftDto { public required string? Currency { get; init; } public required decimal? Subtotal { get; init; } public required decimal? TaxAmount { get; init; } public required decimal? Total { get; init; } }
public sealed class ValidateInvoiceRequest { [Range(1, int.MaxValue)] public required int ExpectedVersion { get; init; } }
public sealed class ApproveInvoiceRequest { [Range(1, int.MaxValue)] public required int ExpectedVersion { get; init; } }
public sealed class RejectInvoiceRequest { [Range(1, int.MaxValue)] public required int ExpectedVersion { get; init; } public required string? Reason { get; init; } }

public sealed class InvoiceQueueQueryDto
{
    public string? Search { get; init; }
    public IReadOnlyList<InvoiceStatus> Status { get; init; } = Array.Empty<InvoiceStatus>();
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 25;
    public InvoiceSort Sort { get; init; } = InvoiceSort.UpdatedAtDesc;
}

public sealed class AuditHistoryQueryDto { [Range(1, int.MaxValue)] public int Page { get; init; } = 1; [Range(1, 100)] public int PageSize { get; init; } = 50; }

using Domain = InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Api.Approval;

internal static class ApprovalHttpMapping
{
    public static IReadOnlyDictionary<string, string[]> ToProblemFields(
        this IReadOnlyDictionary<Domain.InvoiceFieldKey, IReadOnlyList<string>> messages) =>
        messages.OrderBy(item => item.Key).ToDictionary(
            item => ToDraftPath(item.Key),
            item => item.Value.ToArray(),
            StringComparer.Ordinal);

    private static string ToDraftPath(Domain.InvoiceFieldKey field) => field switch
    {
        Domain.InvoiceFieldKey.SupplierName => "draft.supplier.name",
        Domain.InvoiceFieldKey.SupplierRegistrationId => "draft.supplier.registrationId",
        Domain.InvoiceFieldKey.InvoiceNumber => "draft.reference.invoiceNumber",
        Domain.InvoiceFieldKey.PurchaseOrderNumber => "draft.reference.purchaseOrderNumber",
        Domain.InvoiceFieldKey.InvoiceDate => "draft.datesAndTerms.invoiceDate",
        Domain.InvoiceFieldKey.DueDate => "draft.datesAndTerms.dueDate",
        Domain.InvoiceFieldKey.PaymentTerms => "draft.datesAndTerms.paymentTerms",
        Domain.InvoiceFieldKey.Currency => "draft.amounts.currency",
        Domain.InvoiceFieldKey.Subtotal => "draft.amounts.subtotal",
        Domain.InvoiceFieldKey.TaxAmount => "draft.amounts.taxAmount",
        Domain.InvoiceFieldKey.Total => "draft.amounts.total",
        Domain.InvoiceFieldKey.ReviewNotes => "draft.reviewNotes",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}

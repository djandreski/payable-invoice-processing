using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Infrastructure.Queue;

namespace InvoiceReviewAssistant.Api.Queue;

internal static class InvoiceQueueHttpMapping
{
    public static InvoiceQueuePageDto ToDto(this InvoiceQueuePage page) => new()
    {
        Items = page.Items.Select(item => new InvoiceQueueItemDto
        {
            Id = item.InvoiceId.Value,
            Status = (Contracts.InvoiceStatus)(int)item.Status,
            SupplierName = item.SupplierName,
            InvoiceNumber = item.InvoiceNumber,
            InvoiceDate = item.InvoiceDate,
            Total = item.Total,
            Currency = item.Currency,
            DraftVersion = item.DraftVersion.Value,
            WarningCount = item.WarningCount,
            ErrorCount = item.ErrorCount,
            ExceptionCount = item.WarningCount + item.ErrorCount,
            ProcessingFailure = item.ProcessingFailure?.ToDto(),
            CreatedAt = item.CreatedAtUtc,
            UpdatedAt = item.UpdatedAtUtc,
        }).ToArray(),
        Summary = new InvoiceQueueSummaryDto
        {
            TotalInvoiceCount = page.Summary.TotalInvoiceCount,
            ProcessingCount = page.Summary.ProcessingCount,
            ReviewRequiredCount = page.Summary.ReviewRequiredCount,
            ReadyForApprovalCount = page.Summary.ReadyForApprovalCount,
            ApprovedCount = page.Summary.ApprovedCount,
            RejectedCount = page.Summary.RejectedCount,
            ProcessingFailedCount = page.Summary.ProcessingFailedCount,
            PendingReviewCount = page.Summary.PendingReviewCount,
            WarningInvoiceCount = page.Summary.WarningInvoiceCount,
            ErrorInvoiceCount = page.Summary.ErrorInvoiceCount,
        },
        Page = page.Page,
        PageSize = page.PageSize,
        TotalItems = page.TotalItems,
        TotalPages = page.TotalPages,
        HasPreviousPage = page.HasPreviousPage,
        HasNextPage = page.HasNextPage,
    };
}

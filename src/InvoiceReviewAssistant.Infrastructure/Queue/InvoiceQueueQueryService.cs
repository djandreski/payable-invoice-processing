using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Queue;

public sealed record InvoiceQueueRow(
    InvoiceId InvoiceId,
    InvoiceStatus Status,
    string? SupplierName,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    decimal? Total,
    string? Currency,
    DraftVersion DraftVersion,
    int WarningCount,
    int ErrorCount,
    ProcessingFailure? ProcessingFailure,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record InvoiceQueueSummary(
    int TotalInvoiceCount,
    int ProcessingCount,
    int ReviewRequiredCount,
    int ReadyForApprovalCount,
    int ApprovedCount,
    int RejectedCount,
    int ProcessingFailedCount,
    int WarningInvoiceCount,
    int ErrorInvoiceCount)
{
    public int PendingReviewCount => ReviewRequiredCount + ReadyForApprovalCount;
}

public sealed record InvoiceQueuePage(
    IReadOnlyList<InvoiceQueueRow> Items,
    InvoiceQueueSummary Summary,
    int Page,
    int PageSize,
    int TotalItems)
{
    public int TotalPages => TotalItems == 0
        ? 0
        : (int)(((long)TotalItems + PageSize - 1) / PageSize);

    public bool HasPreviousPage => Page > 1 && TotalPages > 0;

    public bool HasNextPage => Page < TotalPages;
}

/// <summary>Executes the read-only queue projection without materializing invoice aggregates.</summary>
public sealed class InvoiceQueueQueryService(InvoiceDbContext context)
{
    public async Task<InvoiceQueuePage> QueryAsync(
        InvoiceSearch criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentOutOfRangeException.ThrowIfLessThan(criteria.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(criteria.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(criteria.PageSize, 100);

        var summary = await LoadSummaryAsync(cancellationToken);
        var filtered = ApplyFilters(context.Invoices.AsNoTracking(), criteria);
        var totalItems = await filtered.CountAsync(cancellationToken);
        var offset = ((long)criteria.Page - 1) * criteria.PageSize;

        if (offset >= totalItems)
        {
            return new InvoiceQueuePage([], summary, criteria.Page, criteria.PageSize, totalItems);
        }

        var ordered = ApplyOrdering(filtered, criteria.Sort);
        var rows = await ordered
            .Skip((int)offset)
            .Take(criteria.PageSize)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.Status,
                invoice.SupplierName,
                invoice.InvoiceNumber,
                invoice.InvoiceDate,
                invoice.Total,
                invoice.Currency,
                invoice.DraftVersion,
                WarningCount = invoice.CurrentValidationRunId == null
                    ? 0
                    : context.ValidationResults.Count(result =>
                        result.ValidationRunId == invoice.CurrentValidationRunId &&
                        result.Severity == nameof(ValidationSeverity.Warning)),
                ErrorCount = invoice.CurrentValidationRunId == null
                    ? 0
                    : context.ValidationResults.Count(result =>
                        result.ValidationRunId == invoice.CurrentValidationRunId &&
                        result.Severity == nameof(ValidationSeverity.Error)),
                invoice.ProcessingFailureStage,
                invoice.ProcessingFailureCode,
                invoice.ProcessingFailureMessage,
                invoice.ProcessingFailedAtUtc,
                invoice.CreatedAtUtc,
                invoice.UpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => new InvoiceQueueRow(
            new InvoiceId(row.Id),
            Enum.Parse<InvoiceStatus>(row.Status, ignoreCase: false),
            row.SupplierName,
            row.InvoiceNumber,
            row.InvoiceDate,
            row.Total,
            row.Currency,
            new DraftVersion(row.DraftVersion),
            row.WarningCount,
            row.ErrorCount,
            CreateProcessingFailure(
                row.ProcessingFailureStage,
                row.ProcessingFailureCode,
                row.ProcessingFailureMessage,
                row.ProcessingFailedAtUtc),
            AsDateTimeOffset(row.CreatedAtUtc),
            AsDateTimeOffset(row.UpdatedAtUtc)))
            .ToArray();

        return new InvoiceQueuePage(items, summary, criteria.Page, criteria.PageSize, totalItems);
    }

    private async Task<InvoiceQueueSummary> LoadSummaryAsync(CancellationToken cancellationToken)
    {
        var statusCounts = await context.Invoices.AsNoTracking()
            .GroupBy(invoice => invoice.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, StringComparer.Ordinal, cancellationToken);

        var exceptionCounts = await context.ValidationResults.AsNoTracking()
            .Where(result => context.Invoices.Any(invoice =>
                invoice.CurrentValidationRunId == result.ValidationRunId))
            .GroupBy(result => result.Severity)
            .Select(group => new
            {
                Severity = group.Key,
                Count = group.Select(result => result.ValidationRunId).Distinct().Count(),
            })
            .ToDictionaryAsync(row => row.Severity, row => row.Count, StringComparer.Ordinal, cancellationToken);

        var processing = Count(statusCounts, InvoiceStatus.Processing);
        var reviewRequired = Count(statusCounts, InvoiceStatus.ReviewRequired);
        var readyForApproval = Count(statusCounts, InvoiceStatus.ReadyForApproval);
        var approved = Count(statusCounts, InvoiceStatus.Approved);
        var rejected = Count(statusCounts, InvoiceStatus.Rejected);
        var processingFailed = Count(statusCounts, InvoiceStatus.ProcessingFailed);

        return new InvoiceQueueSummary(
            statusCounts.Values.Sum(),
            processing,
            reviewRequired,
            readyForApproval,
            approved,
            rejected,
            processingFailed,
            exceptionCounts.GetValueOrDefault(nameof(ValidationSeverity.Warning)),
            exceptionCounts.GetValueOrDefault(nameof(ValidationSeverity.Error)));
    }

    private static IQueryable<Persistence.Entities.InvoiceEntity> ApplyFilters(
        IQueryable<Persistence.Entities.InvoiceEntity> query,
        InvoiceSearch criteria)
    {
        if (criteria.Statuses.Count > 0)
        {
            var statuses = criteria.Statuses
                .Distinct()
                .Select(status => status.ToString())
                .ToArray();
            query = query.Where(invoice => statuses.Contains(invoice.Status));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var pattern = $"%{EscapeLikePattern(criteria.Search.Trim())}%";
            query = query.Where(invoice =>
                (invoice.SupplierName != null && EF.Functions.Like(invoice.SupplierName, pattern, "\\")) ||
                (invoice.SupplierRegistrationId != null && EF.Functions.Like(invoice.SupplierRegistrationId, pattern, "\\")) ||
                (invoice.InvoiceNumber != null && EF.Functions.Like(invoice.InvoiceNumber, pattern, "\\")) ||
                (invoice.PurchaseOrderNumber != null && EF.Functions.Like(invoice.PurchaseOrderNumber, pattern, "\\")));
        }

        return query;
    }

    private static IOrderedQueryable<Persistence.Entities.InvoiceEntity> ApplyOrdering(
        IQueryable<Persistence.Entities.InvoiceEntity> query,
        InvoiceSort sort) => sort switch
        {
            InvoiceSort.UpdatedAtDescending => query
                .OrderByDescending(invoice => invoice.UpdatedAtUtc)
                .ThenByDescending(invoice => invoice.Id),
            InvoiceSort.UpdatedAtAscending => query
                .OrderBy(invoice => invoice.UpdatedAtUtc)
                .ThenBy(invoice => invoice.Id),
            InvoiceSort.CreatedAtDescending => query
                .OrderByDescending(invoice => invoice.CreatedAtUtc)
                .ThenByDescending(invoice => invoice.Id),
            InvoiceSort.CreatedAtAscending => query
                .OrderBy(invoice => invoice.CreatedAtUtc)
                .ThenBy(invoice => invoice.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };

    private static ProcessingFailure? CreateProcessingFailure(
        string? stage,
        string? code,
        string? message,
        DateTime? failedAtUtc)
    {
        if (stage is null && code is null && message is null && failedAtUtc is null)
        {
            return null;
        }

        if (stage is null || code is null || message is null || failedAtUtc is null)
        {
            throw new InvalidOperationException("A persisted processing failure must be complete.");
        }

        return new ProcessingFailure(
            Enum.Parse<ProcessingStage>(stage, ignoreCase: false),
            Enum.Parse<ProcessingFailureCode>(code, ignoreCase: false),
            message,
            AsDateTimeOffset(failedAtUtc.Value));
    }

    private static int Count(
        IReadOnlyDictionary<string, int> counts,
        InvoiceStatus status) => counts.GetValueOrDefault(status.ToString());

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static DateTimeOffset AsDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

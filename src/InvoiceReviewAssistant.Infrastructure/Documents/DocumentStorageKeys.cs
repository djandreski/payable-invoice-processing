using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Infrastructure.Documents;

/// <summary>
/// Creates opaque application-managed keys. A key is a leaf name, never a path supplied by a user.
/// </summary>
public static class DocumentStorageKeys
{
    public static DocumentStorageKey CreateDocumentKey() =>
        new($"{Guid.NewGuid():N}.pdf");

    internal static DocumentStorageKey CreateStagingKey() =>
        new($"{Guid.NewGuid():N}.upload");

    internal static DocumentStorageKey CreateQuarantineKey() =>
        new($"{Guid.NewGuid():N}.quarantine");
}

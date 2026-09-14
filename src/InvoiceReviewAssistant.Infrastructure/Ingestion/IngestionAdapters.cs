using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using InvoiceReviewAssistant.Infrastructure.Pdf;

namespace InvoiceReviewAssistant.Infrastructure.Ingestion;

public sealed class PdfUploadAcceptanceAdapter(PdfUploadAcceptanceService inner) : IInvoiceUploadAcceptance
{
    public async Task<InvoiceUploadAcceptanceResult> AcceptAsync(
        IReadOnlyList<InvoiceUploadFile> files,
        CancellationToken cancellationToken)
    {
        var candidates = files.Select(file => new UploadFileCandidate(
            file.FieldName,
            file.DisplayFilename,
            file.ContentType,
            file.Content)).ToArray();
        var result = await inner.AcceptAsync(candidates, cancellationToken);
        return result switch
        {
            PdfUploadAcceptanceResult.Accepted accepted => new InvoiceUploadAcceptanceResult.Accepted(
                new AcceptedInvoiceUpload(
                    accepted.Upload.StagedDocument,
                    accepted.Upload.OriginalFilename,
                    accepted.Upload.PageCount)),
            PdfUploadAcceptanceResult.Rejected rejected => new InvoiceUploadAcceptanceResult.Rejected(
                new InvoiceUploadRejection(
                    rejected.Failure.Code,
                    rejected.Failure.HttpStatus,
                    rejected.Failure.SafeMessage,
                    rejected.Failure.FieldPath)),
            _ => throw new InvalidOperationException("The upload acceptance result is unsupported."),
        };
    }
}

public sealed class NativeDocumentTextPathAdapter(NativePdfTextPathService inner) : INativeDocumentTextPath
{
    public async Task<NativeDocumentTextResult> ExtractAsync(
        Stream pdf,
        CancellationToken cancellationToken)
    {
        var result = await inner.ExtractAndClassifyAsync(pdf, cancellationToken);
        return result switch
        {
            NativePdfTextPathResult.Usable usable => new NativeDocumentTextResult.Usable(usable.Document),
            NativePdfTextPathResult.RequiresWholeDocumentOcr => new NativeDocumentTextResult.RequiresWholeDocumentOcr(),
            _ => throw new InvalidOperationException("The native-text result is unsupported."),
        };
    }
}

public sealed class WholeDocumentOcrAdapter(IPdfOcrDocumentProcessor inner) : IWholeDocumentOcr
{
    public Task<NormalizedDocumentText> ExtractAsync(
        Stream pdf,
        int pageCount,
        CancellationToken cancellationToken) =>
        inner.ExtractAsync(pdf, pageCount, cancellationToken);
}

public sealed class DocumentStorageKeyFactory : IDocumentStorageKeyFactory
{
    public DocumentStorageKey CreateDocumentKey() => DocumentStorageKeys.CreateDocumentKey();
}

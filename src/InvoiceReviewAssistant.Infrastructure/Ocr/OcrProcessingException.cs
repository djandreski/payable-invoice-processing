using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

/// <summary>
/// A rendering or OCR failure whose classification and message are safe to persist.
/// Provider output, executable paths, and temporary paths are intentionally omitted.
/// </summary>
public sealed class OcrProcessingException : ProcessingProviderException
{
    internal OcrProcessingException(
        ProcessingFailureCode code,
        string safeMessage)
        : base(ProcessingStage.Ocr, code, safeMessage)
    {
    }
}

public interface IOcrPreflightService
{
    Task CheckAsync(CancellationToken cancellationToken);
}

public interface IPdfOcrDocumentProcessor
{
    Task<NormalizedDocumentText> ExtractAsync(
        Stream pdf,
        int pageCount,
        CancellationToken cancellationToken);
}

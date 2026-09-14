using InvoiceReviewAssistant.Core.Invoices;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

/// <summary>
/// Performs the structural PDF checks that require a parser. The input stream remains
/// owned by the caller.
/// </summary>
public sealed class PdfPigDocumentInspector : IPdfDocumentInspector
{
    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    public async Task<PdfInspection> InspectAsync(Stream pdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead)
        {
            throw new ArgumentException("The PDF stream must be readable.", nameof(pdf));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (pdf.CanSeek)
        {
            var initialPosition = pdf.Position;
            try
            {
                return InspectSeekable(pdf, cancellationToken);
            }
            finally
            {
                pdf.Position = initialPosition;
            }
        }

        await using var buffered = new MemoryStream();
        await pdf.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return InspectSeekable(buffered, cancellationToken);
    }

    private static PdfInspection InspectSeekable(Stream pdf, CancellationToken cancellationToken)
    {
        var signature = new byte[PdfSignature.Length];
        var read = 0;
        while (read < signature.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pdf.Read(signature, read, signature.Length - read);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        var hasSignature = read == signature.Length && signature.AsSpan().SequenceEqual(PdfSignature);
        if (!hasSignature)
        {
            return new PdfInspection(false, false, 0);
        }

        pdf.Position = 0;
        try
        {
            using var document = PdfDocument.Open(pdf, ParsingOptions.LenientParsingOff);
            if (document.IsEncrypted)
            {
                return new PdfInspection(true, true, document.NumberOfPages);
            }

            var pageCount = document.NumberOfPages;
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = document.GetPage(pageNumber);
            }

            return new PdfInspection(true, false, pageCount);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new PdfInspection(true, true, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PdfInspectionException("The PDF did not pass structural inspection.", exception);
        }
    }
}

/// <summary>
/// Classifies parser failures without exposing parser diagnostics to an HTTP caller.
/// </summary>
public sealed class PdfInspectionException : Exception
{
    public PdfInspectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

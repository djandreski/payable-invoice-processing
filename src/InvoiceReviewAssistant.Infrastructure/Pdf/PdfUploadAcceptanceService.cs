using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

public sealed record UploadFileCandidate(
    string FieldName,
    string DisplayFilename,
    string ContentType,
    Stream Content);

public sealed record AcceptedPdfUpload(
    StagedDocument StagedDocument,
    string OriginalFilename,
    string MediaType,
    int PageCount);

public enum UploadAcceptanceFailureKind
{
    RequestValidationFailed,
    PdfFileRequired,
    PdfEmpty,
    PdfTypeInvalid,
    PdfSignatureInvalid,
    PdfInvalid,
    PdfEncrypted,
    PdfPageLimitExceeded,
    PdfSizeLimitExceeded,
}

public sealed record UploadAcceptanceFailure(
    UploadAcceptanceFailureKind Kind,
    string Code,
    int HttpStatus,
    string SafeMessage,
    string FieldPath = "file");

public abstract record PdfUploadAcceptanceResult
{
    private PdfUploadAcceptanceResult()
    {
    }

    public sealed record Accepted(AcceptedPdfUpload Upload) : PdfUploadAcceptanceResult;

    public sealed record Rejected(UploadAcceptanceFailure Failure) : PdfUploadAcceptanceResult;
}

/// <summary>
/// Owns the complete pre-acceptance policy. It never creates an invoice or performs
/// provider work. The caller retains ownership of every upload stream.
/// </summary>
public sealed class PdfUploadAcceptanceService
{
    private const string RequiredFieldName = "file";
    private const string PdfMediaType = "application/pdf";

    private readonly IDocumentStore _documentStore;
    private readonly ILocalDocumentStoreMaintenance _stagingMaintenance;
    private readonly IPdfDocumentInspector _pdfInspector;
    private readonly UploadOptions _options;

    public PdfUploadAcceptanceService(
        IDocumentStore documentStore,
        ILocalDocumentStoreMaintenance stagingMaintenance,
        IPdfDocumentInspector pdfInspector,
        UploadOptions options)
    {
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(stagingMaintenance);
        ArgumentNullException.ThrowIfNull(pdfInspector);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaximumBytes <= 0 || options.MaximumPageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Upload limits must be greater than zero.");
        }

        _documentStore = documentStore;
        _stagingMaintenance = stagingMaintenance;
        _pdfInspector = pdfInspector;
        _options = options;
    }

    public async Task<PdfUploadAcceptanceResult> AcceptAsync(
        IReadOnlyList<UploadFileCandidate> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();

        if (files.Count == 0 || (files.Count == 1 && !IsRequiredFile(files[0])))
        {
            return Reject(
                UploadAcceptanceFailureKind.PdfFileRequired,
                "PDF_FILE_REQUIRED",
                400,
                "Choose one PDF file to upload.");
        }

        if (files.Count != 1 || !IsRequiredFile(files[0]))
        {
            return Reject(
                UploadAcceptanceFailureKind.RequestValidationFailed,
                "REQUEST_VALIDATION_FAILED",
                400,
                "Upload exactly one multipart file named 'file'.");
        }

        var file = files[0];
        ArgumentNullException.ThrowIfNull(file.Content);
        if (!file.Content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(files));
        }

        StagedDocument? staged = null;
        var accepted = false;
        await using var capturedUpload = new BoundedCaptureReadStream(file.Content, _options.MaximumBytes);

        try
        {
            try
            {
                staged = await _documentStore.StageAsync(capturedUpload, cancellationToken);
            }
            catch (UploadSizeLimitExceededException)
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfSizeLimitExceeded,
                    "PDF_SIZE_LIMIT_EXCEEDED",
                    413,
                    "The PDF exceeds the configured upload size limit.");
            }

            if (staged.ByteLength == 0)
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfEmpty,
                    "PDF_EMPTY",
                    400,
                    "The uploaded PDF is empty.");
            }

            if (!string.Equals(Path.GetExtension(file.DisplayFilename), ".pdf", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(file.ContentType.Trim(), PdfMediaType, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfTypeInvalid,
                    "PDF_TYPE_INVALID",
                    400,
                    "Upload a file with a .pdf extension and application/pdf media type.");
            }

            capturedUpload.RewindCapture();
            PdfInspection inspection;
            try
            {
                inspection = await _pdfInspector.InspectAsync(capturedUpload.CapturedContent, cancellationToken);
            }
            catch (PdfInspectionException)
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfInvalid,
                    "PDF_INVALID",
                    400,
                    "The PDF is malformed or structurally invalid.");
            }

            if (!inspection.HasPdfSignature)
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfSignatureInvalid,
                    "PDF_SIGNATURE_INVALID",
                    400,
                    "The file does not have a valid PDF signature.");
            }

            if (inspection.IsEncrypted)
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfEncrypted,
                    "PDF_ENCRYPTED",
                    400,
                    "Upload an unprotected copy of the PDF.");
            }

            if (inspection.PageCount > _options.MaximumPageCount)
            {
                return Reject(
                    UploadAcceptanceFailureKind.PdfPageLimitExceeded,
                    "PDF_PAGE_LIMIT_EXCEEDED",
                    400,
                    "The PDF exceeds the configured page limit.");
            }

            accepted = true;
            return new PdfUploadAcceptanceResult.Accepted(
                new AcceptedPdfUpload(staged, file.DisplayFilename, PdfMediaType, inspection.PageCount));
        }
        finally
        {
            if (staged is not null && !accepted)
            {
                await _stagingMaintenance.DeleteStagedAsync(staged.Key, CancellationToken.None);
            }
        }
    }

    private static bool IsRequiredFile(UploadFileCandidate file) =>
        file is not null && string.Equals(file.FieldName, RequiredFieldName, StringComparison.Ordinal);

    private static PdfUploadAcceptanceResult Reject(
        UploadAcceptanceFailureKind kind,
        string code,
        int status,
        string safeMessage) =>
        new PdfUploadAcceptanceResult.Rejected(new UploadAcceptanceFailure(kind, code, status, safeMessage));

    private sealed class BoundedCaptureReadStream : Stream
    {
        private readonly Stream _source;
        private readonly long _maximumBytes;
        private readonly MemoryStream _capture = new();
        private long _bytesRead;

        public BoundedCaptureReadStream(Stream source, long maximumBytes)
        {
            _source = source;
            _maximumBytes = maximumBytes;
        }

        public Stream CapturedContent => _capture;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void RewindCapture() => _capture.Position = 0;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            var permitted = PermittedReadLength(count);
            var read = _source.Read(buffer, offset, permitted);
            CaptureOrThrow(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var permitted = PermittedReadLength(buffer.Length);
            var read = await _source.ReadAsync(buffer[..permitted], cancellationToken);
            CaptureOrThrow(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int PermittedReadLength(int requested)
        {
            var remainingWithProbe = checked(_maximumBytes - _bytesRead + 1);
            return (int)Math.Min(requested, remainingWithProbe);
        }

        private void CaptureOrThrow(ReadOnlySpan<byte> bytes)
        {
            if (_bytesRead + bytes.Length > _maximumBytes)
            {
                throw new UploadSizeLimitExceededException();
            }

            _capture.Write(bytes);
            _bytesRead += bytes.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _capture.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UploadSizeLimitExceededException : IOException;
}

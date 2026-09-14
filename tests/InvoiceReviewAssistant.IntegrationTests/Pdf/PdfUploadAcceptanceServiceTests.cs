using System.Security.Cryptography;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Pdf;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Pdf;

public sealed class PdfUploadAcceptanceServiceTests
{
    private const long DefaultMaximumBytes = 20L * 1024 * 1024;

    [Fact]
    public async Task No_file_returns_required_without_staging()
    {
        using var root = new TemporaryStorageRoot();
        var inspector = new StubPdfInspector();
        var service = root.CreateService(inspector);

        var result = await service.AcceptAsync([], CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfFileRequired, "PDF_FILE_REQUIRED", 400);
        Assert.Equal(0, inspector.CallCount);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task A_single_file_under_the_wrong_part_name_is_treated_as_missing_file()
    {
        using var root = new TemporaryStorageRoot();
        var content = new TrackingMemoryStream(CreatePdf(1));
        var service = root.CreateService(new StubPdfInspector());

        var result = await service.AcceptAsync(
            [new UploadFileCandidate("document", "invoice.pdf", "application/pdf", content)],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfFileRequired, "PDF_FILE_REQUIRED", 400);
        Assert.Equal(0, content.BytesRead);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Multiple_files_return_request_validation_without_reading_either_stream()
    {
        using var root = new TemporaryStorageRoot();
        var first = new TrackingMemoryStream(CreatePdf(1));
        var second = new TrackingMemoryStream(CreatePdf(1));
        var service = root.CreateService(new StubPdfInspector());

        var result = await service.AcceptAsync(
            [
                new UploadFileCandidate("file", "first.pdf", "application/pdf", first),
                new UploadFileCandidate("file", "second.pdf", "application/pdf", second),
            ],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.RequestValidationFailed, "REQUEST_VALIDATION_FAILED", 400);
        Assert.Equal(0, first.BytesRead);
        Assert.Equal(0, second.BytesRead);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Empty_file_is_rejected_and_its_staged_file_is_removed()
    {
        using var root = new TemporaryStorageRoot();
        var inspector = new StubPdfInspector();

        var result = await root.CreateService(inspector).AcceptAsync(
            [File(new MemoryStream())],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfEmpty, "PDF_EMPTY", 400);
        Assert.Equal(0, inspector.CallCount);
        Assert.Empty(root.StagedFiles);
    }

    [Theory]
    [InlineData("invoice.txt", "application/pdf")]
    [InlineData("invoice.pdf", "application/octet-stream")]
    [InlineData("invoice", "application/pdf")]
    public async Task Wrong_extension_or_declared_type_is_rejected_before_inspection(
        string displayFilename,
        string contentType)
    {
        using var root = new TemporaryStorageRoot();
        var inspector = new StubPdfInspector();

        var result = await root.CreateService(inspector).AcceptAsync(
            [new UploadFileCandidate("file", displayFilename, contentType, new MemoryStream(CreatePdf(1)))],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfTypeInvalid, "PDF_TYPE_INVALID", 400);
        Assert.Equal(0, inspector.CallCount);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Wrong_signature_is_rejected_and_cleaned_up()
    {
        using var root = new TemporaryStorageRoot();
        var bytes = CreatePdf(1);
        "NOT-A"u8.CopyTo(bytes);

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [File(new MemoryStream(bytes))],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfSignatureInvalid, "PDF_SIGNATURE_INVALID", 400);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Malformed_pdf_is_rejected_and_cleaned_up()
    {
        using var root = new TemporaryStorageRoot();

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [File(new MemoryStream("%PDF-1.7\nsynthetic malformed content"u8.ToArray()))],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfInvalid, "PDF_INVALID", 400);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Password_protected_pdf_is_uniformly_rejected_and_cleaned_up()
    {
        using var root = new TemporaryStorageRoot();
        var encrypted = Convert.FromBase64String(EncryptedPdfBase64);

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [File(new MemoryStream(encrypted))],
            CancellationToken.None);

        var failure = AssertRejected(result, UploadAcceptanceFailureKind.PdfEncrypted, "PDF_ENCRYPTED", 400);
        Assert.Contains("unprotected copy", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Exactly_twenty_mib_is_accepted_and_hashing_uses_the_single_staging_read()
    {
        using var root = new TemporaryStorageRoot();
        var bytes = CreateSizedPdfLikePayload(DefaultMaximumBytes);
        using var source = new TrackingMemoryStream(bytes, publiclySeekable: false);
        var service = root.CreateService(new StubPdfInspector(new PdfInspection(true, false, 1)));

        var result = await service.AcceptAsync([File(source)], CancellationToken.None);

        var accepted = Assert.IsType<PdfUploadAcceptanceResult.Accepted>(result).Upload;
        Assert.Equal(DefaultMaximumBytes, accepted.StagedDocument.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), accepted.StagedDocument.Sha256);
        Assert.Matches("^[0-9a-f]{32}\\.upload$", accepted.StagedDocument.Key.Value);
        Assert.Equal(bytes.Length, source.BytesRead);
        Assert.Single(root.StagedFiles);
    }

    [Fact]
    public async Task One_byte_over_twenty_mib_is_rejected_without_inspection_or_a_leftover()
    {
        using var root = new TemporaryStorageRoot();
        var inspector = new StubPdfInspector();
        using var source = new TrackingMemoryStream(CreateSizedPdfLikePayload(DefaultMaximumBytes + 1), publiclySeekable: false);

        var result = await root.CreateService(inspector).AcceptAsync([File(source)], CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfSizeLimitExceeded, "PDF_SIZE_LIMIT_EXCEEDED", 413);
        Assert.Equal(0, inspector.CallCount);
        Assert.Equal(DefaultMaximumBytes + 1, source.BytesRead);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Competing_failures_follow_the_authoritative_pre_acceptance_order()
    {
        using var root = new TemporaryStorageRoot();
        var strictLimits = new UploadOptions { MaximumBytes = 8, MaximumPageCount = 1 };

        var oversized = await root.CreateService(new StubPdfInspector(), strictLimits).AcceptAsync(
            [new UploadFileCandidate("file", "wrong.txt", "application/octet-stream", new MemoryStream(new byte[9]))],
            CancellationToken.None);
        AssertRejected(oversized, UploadAcceptanceFailureKind.PdfSizeLimitExceeded, "PDF_SIZE_LIMIT_EXCEEDED", 413);

        var empty = await root.CreateService(new StubPdfInspector(), strictLimits).AcceptAsync(
            [new UploadFileCandidate("file", "wrong.txt", "application/octet-stream", new MemoryStream())],
            CancellationToken.None);
        AssertRejected(empty, UploadAcceptanceFailureKind.PdfEmpty, "PDF_EMPTY", 400);

        var signature = await root.CreateService(
            new StubPdfInspector(new PdfInspection(false, true, 2)),
            strictLimits).AcceptAsync(
                [File(new MemoryStream("12345678"u8.ToArray()))],
                CancellationToken.None);
        AssertRejected(signature, UploadAcceptanceFailureKind.PdfSignatureInvalid, "PDF_SIGNATURE_INVALID", 400);

        var encrypted = await root.CreateService(
            new StubPdfInspector(new PdfInspection(true, true, 2)),
            strictLimits).AcceptAsync(
                [File(new MemoryStream("12345678"u8.ToArray()))],
                CancellationToken.None);
        AssertRejected(encrypted, UploadAcceptanceFailureKind.PdfEncrypted, "PDF_ENCRYPTED", 400);

        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Exactly_twenty_five_pages_is_accepted_by_the_real_inspector()
    {
        using var root = new TemporaryStorageRoot();
        var bytes = CreatePdf(25);

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [File(new MemoryStream(bytes))],
            CancellationToken.None);

        var accepted = Assert.IsType<PdfUploadAcceptanceResult.Accepted>(result).Upload;
        Assert.Equal(25, accepted.PageCount);
        Assert.Equal(bytes.Length, accepted.StagedDocument.ByteLength);
        Assert.Single(root.StagedFiles);
    }

    [Fact]
    public async Task Twenty_six_pages_is_rejected_and_cleaned_up()
    {
        using var root = new TemporaryStorageRoot();

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [File(new MemoryStream(CreatePdf(26)))],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfPageLimitExceeded, "PDF_PAGE_LIMIT_EXCEEDED", 400);
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Accepted_descriptor_preserves_display_metadata_without_using_it_as_a_storage_key()
    {
        using var root = new TemporaryStorageRoot();
        const string untrustedDisplayName = "../../synthetic-invoice.PDF";
        var bytes = CreatePdf(1);

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [new UploadFileCandidate("file", untrustedDisplayName, "APPLICATION/PDF", new MemoryStream(bytes))],
            CancellationToken.None);

        var accepted = Assert.IsType<PdfUploadAcceptanceResult.Accepted>(result).Upload;
        Assert.Equal(untrustedDisplayName, accepted.OriginalFilename);
        Assert.Equal("application/pdf", accepted.MediaType);
        Assert.Equal(1, accepted.PageCount);
        Assert.DoesNotContain("synthetic", accepted.StagedDocument.Key.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), accepted.StagedDocument.Sha256);
    }

    [Fact]
    public async Task Cancellation_after_staging_propagates_and_removes_the_staged_file()
    {
        using var root = new TemporaryStorageRoot();
        using var cancellation = new CancellationTokenSource();
        var inspector = new CancelingPdfInspector(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            root.CreateService(inspector).AcceptAsync(
                [File(new MemoryStream(CreatePdf(1)))],
                cancellation.Token));

        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task Rejection_does_not_create_invoice_document_or_audit_rows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var databaseOptions = new DbContextOptionsBuilder<InvoiceDbContext>().UseSqlite(connection).Options;
        await using var database = new InvoiceDbContext(databaseOptions);
        await database.Database.EnsureCreatedAsync();
        using var root = new TemporaryStorageRoot();

        var result = await root.CreateService(new PdfPigDocumentInspector()).AcceptAsync(
            [File(new MemoryStream("%PDF-1.7\nmalformed"u8.ToArray()))],
            CancellationToken.None);

        AssertRejected(result, UploadAcceptanceFailureKind.PdfInvalid, "PDF_INVALID", 400);
        Assert.Equal(0, await database.Invoices.CountAsync());
        Assert.Equal(0, await database.InvoiceDocuments.CountAsync());
        Assert.Equal(0, await database.AuditEvents.CountAsync());
        Assert.Empty(root.StagedFiles);
    }

    [Fact]
    public async Task PdfPig_inspector_restores_a_seekable_stream_position()
    {
        var pdf = CreatePdf(1);
        await using var stream = new MemoryStream([0x00, .. pdf]);
        stream.Position = 1;

        var inspection = await new PdfPigDocumentInspector().InspectAsync(stream, CancellationToken.None);

        Assert.True(inspection.HasPdfSignature);
        Assert.False(inspection.IsEncrypted);
        Assert.Equal(1, inspection.PageCount);
        Assert.Equal(1, stream.Position);
    }

    private static UploadFileCandidate File(Stream content) =>
        new("file", "synthetic-invoice.pdf", "application/pdf", content);

    private static UploadAcceptanceFailure AssertRejected(
        PdfUploadAcceptanceResult result,
        UploadAcceptanceFailureKind expectedKind,
        string expectedCode,
        int expectedStatus)
    {
        var rejected = Assert.IsType<PdfUploadAcceptanceResult.Rejected>(result);
        Assert.Equal(expectedKind, rejected.Failure.Kind);
        Assert.Equal(expectedCode, rejected.Failure.Code);
        Assert.Equal(expectedStatus, rejected.Failure.HttpStatus);
        Assert.Equal("file", rejected.Failure.FieldPath);
        Assert.DoesNotContain(Path.GetTempPath(), rejected.Failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
        return rejected.Failure;
    }

    private static byte[] CreatePdf(int pageCount)
    {
        using var builder = new PdfDocumentBuilder();
        for (var page = 0; page < pageCount; page++)
        {
            builder.AddPage(PageSize.A4);
        }

        return builder.Build();
    }

    private static byte[] CreateSizedPdfLikePayload(long byteLength)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)byteLength));
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    private sealed class TemporaryStorageRoot : IDisposable
    {
        public TemporaryStorageRoot()
        {
            RootPath = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "invoice-review-pdf-acceptance-tests",
                Guid.NewGuid().ToString("N")));
            Store = new LocalDocumentStore(new StorageOptions { RootPath = RootPath });
        }

        public string RootPath { get; }

        public LocalDocumentStore Store { get; }

        public IReadOnlyList<string> StagedFiles => Directory.Exists(Path.Combine(RootPath, "staging"))
            ? Directory.GetFiles(Path.Combine(RootPath, "staging"))
            : [];

        public PdfUploadAcceptanceService CreateService(
            IPdfDocumentInspector inspector,
            UploadOptions? options = null) =>
            new(Store, Store, inspector, options ?? new UploadOptions());

        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invoice-review-pdf-acceptance-tests"));
            var normalizedRoot = Path.GetFullPath(RootPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!normalizedRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, comparison))
            {
                throw new InvalidOperationException("The generated test root escaped its expected parent.");
            }

            if (Directory.Exists(normalizedRoot))
            {
                Directory.Delete(normalizedRoot, recursive: true);
            }
        }
    }

    private sealed class StubPdfInspector(PdfInspection? result = null) : IPdfDocumentInspector
    {
        private readonly PdfInspection _result = result ?? new PdfInspection(true, false, 1);

        public int CallCount { get; private set; }

        public Task<PdfInspection> InspectAsync(Stream pdf, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class CancelingPdfInspector(CancellationTokenSource cancellation) : IPdfDocumentInspector
    {
        public Task<PdfInspection> InspectAsync(Stream pdf, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<PdfInspection>(cancellationToken);
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        private readonly bool _publiclySeekable;

        public TrackingMemoryStream(byte[] bytes, bool publiclySeekable = true)
            : base(bytes)
        {
            _publiclySeekable = publiclySeekable;
        }

        public long BytesRead { get; private set; }

        public override bool CanSeek => _publiclySeekable;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (read.IsCompletedSuccessfully)
            {
                BytesRead += read.Result;
                return read;
            }

            return TrackAsync(read);
        }

        private async ValueTask<int> TrackAsync(ValueTask<int> pending)
        {
            var read = await pending;
            BytesRead += read;
            return read;
        }
    }

    private const string EncryptedPdfBase64 =
        "JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPDM3NGU0MWZhZTE+Cj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9UeXBlIC9QYWdlcwovQ291bnQgMQovS2lkcyBbIDQgMCBSIF0KPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL0NhdGFsb2cKL1BhZ2VzIDIgMCBSCj4+CmVuZG9iago0IDAgb2JqCjw8Ci9UeXBlIC9QYWdlCi9SZXNvdXJjZXMgPDwKPj4KL01lZGlhQm94IFsgMC4wIDAuMCA2MTIgNzkyIF0KL1BhcmVudCAyIDAgUgo+PgplbmRvYmoKNSAwIG9iago8PAovViAyCi9SIDMKL0xlbmd0aCAxMjgKL1AgNDI5NDk2NzI5MgovRmlsdGVyIC9TdGFuZGFyZAovTyA8Y2FlMTJhMTM3MDY0MzdiMmExMzNhMjAyMWMyYzdmMWYxYjA2OTJkODcwNjZlZmRiZWY3YjFiMDBlNmM2MDc1OD4KL1UgPGZjMDAzY2M4ZGJhMzBlNjk2NjkxZmQ3MDY4MTE2ZDg0MjhiZjRlNWU0ZTc1OGE0MTY0MDA0ZTU2ZmZmYTAxMDg+Cj4+CmVuZG9iagp4cmVmCjAgNgowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA1OSAwMDAwMCBuIAowMDAwMDAwMTE4IDAwMDAwIG4gCjAwMDAwMDAxNjcgMDAwMDAgbiAKMDAwMDAwMDI2MSAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDYKL1Jvb3QgMyAwIFIKL0luZm8gMSAwIFIKL0lEIFsgPDM1NjEzMTMyNjIzNzY0MzczODM1NjEzNjY0MzUzNTM3MzUzNjM5NjI2MjM3MzA2NDMyMzQzMjMyNjEzNzMwMzk+IDwzNTYxMzEzMjYyMzc2NDM3MzgzNTYxMzY2NDM1MzUzNzM1MzYzOTYyNjIzNzMwNjQzMjM0MzIzMjYxMzczMDM5PiBdCi9FbmNyeXB0IDUgMCBSCj4+CnN0YXJ0eHJlZgo0NzYKJSVFT0YK";
}

using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Ocr;

public sealed class PdfToImagePageRendererTests
{
    [Fact]
    public async Task Real_adapter_renders_a_synthetic_pdf_page_as_png()
    {
        using var root = new OcrTestRoot();
        var renderer = new PdfToImagePageRenderer(
            new StorageOptions { RootPath = root.Path },
            NullLogger<PdfToImagePageRenderer>.Instance);
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        await using var pdf = new MemoryStream(builder.Build());

        var page = await renderer.RenderPageAsync(pdf, 1, 300, CancellationToken.None);
        var bytes = await ReadAllAsync(page.Image);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        await page.Image.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Renders_requested_page_at_requested_dpi_to_managed_deleting_png()
    {
        using var root = new OcrTestRoot();
        var conversion = new CapturingConversion();
        var renderer = CreateRenderer(root, conversion);
        await using var pdf = new MemoryStream("synthetic pdf bytes"u8.ToArray());

        var page = await renderer.RenderPageAsync(pdf, pageNumber: 3, dpi: 300, CancellationToken.None);
        var temporary = Assert.IsAssignableFrom<ITemporaryPageImage>(page.Image);

        Assert.Equal(3, page.PageNumber);
        Assert.Equal(2, conversion.ZeroBasedPageNumber);
        Assert.Equal(300, conversion.Dpi);
        Assert.True(File.Exists(temporary.Path));
        Assert.StartsWith(System.IO.Path.GetFullPath(root.Path), System.IO.Path.GetFullPath(temporary.Path), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, await ReadAllAsync(page.Image));

        await page.Image.DisposeAsync();
        Assert.False(File.Exists(temporary.Path));
    }

    [Fact]
    public async Task Serializes_pdfium_calls_across_renderer_instances()
    {
        using var root = new OcrTestRoot();
        var conversion = new CoordinatedConversion();
        var firstRenderer = CreateRenderer(root, conversion);
        var secondRenderer = CreateRenderer(root, conversion);
        await using var firstPdf = new MemoryStream("first"u8.ToArray());
        await using var secondPdf = new MemoryStream("second"u8.ToArray());

        var firstTask = firstRenderer.RenderPageAsync(firstPdf, 1, 300, CancellationToken.None);
        await conversion.FirstEntered.Task;
        var secondTask = secondRenderer.RenderPageAsync(secondPdf, 2, 300, CancellationToken.None);
        await Task.Yield();

        Assert.Equal(1, conversion.CallCount);
        conversion.ReleaseFirst.TrySetResult();
        var pages = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, conversion.MaximumConcurrency);
        Assert.Equal(new[] { 0, 1 }, conversion.Pages);
        foreach (var page in pages)
        {
            await page.Image.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rendering_failure_is_safe_and_removes_partial_page_file()
    {
        using var root = new OcrTestRoot();
        const string sensitiveDetail = "SENSITIVE_RENDERER_PATH_MARKER";
        var renderer = CreateRenderer(root, new ThrowingConversion(new IOException(sensitiveDetail)));
        await using var pdf = new MemoryStream("synthetic"u8.ToArray());

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() =>
            renderer.RenderPageAsync(pdf, 1, 300, CancellationToken.None));

        Assert.Equal(ProcessingStage.Ocr, exception.Stage);
        Assert.Equal(ProcessingFailureCode.PdfRenderFailed, exception.Code);
        Assert.DoesNotContain(sensitiveDetail, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, exception.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_pdfium_removes_page_file()
    {
        using var root = new OcrTestRoot();
        var conversion = new CoordinatedConversion();
        var firstRenderer = CreateRenderer(root, conversion);
        var waitingRenderer = CreateRenderer(root, conversion);
        await using var firstPdf = new MemoryStream("first"u8.ToArray());
        await using var waitingPdf = new MemoryStream("waiting"u8.ToArray());

        var firstTask = firstRenderer.RenderPageAsync(firstPdf, 1, 300, CancellationToken.None);
        await conversion.FirstEntered.Task;
        using var cancellation = new CancellationTokenSource();
        var waitingTask = waitingRenderer.RenderPageAsync(waitingPdf, 2, 300, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask);
        conversion.ReleaseFirst.TrySetResult();
        var firstPage = await firstTask;
        await firstPage.Image.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.png", SearchOption.AllDirectories));
    }

    private static PdfToImagePageRenderer CreateRenderer(OcrTestRoot root, IPdfPageConversion conversion) =>
        new(
            new OcrTemporaryFileStore(new StorageOptions { RootPath = root.Path }),
            conversion,
            NullLogger<PdfToImagePageRenderer>.Instance);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private sealed class CapturingConversion : IPdfPageConversion
    {
        public int ZeroBasedPageNumber { get; private set; }

        public int Dpi { get; private set; }

        public void SavePng(Stream image, Stream pdf, int zeroBasedPageNumber, int dpi)
        {
            ZeroBasedPageNumber = zeroBasedPageNumber;
            Dpi = dpi;
            image.Write(new byte[] { 137, 80, 78, 71 });
        }
    }

    private sealed class ThrowingConversion(Exception exception) : IPdfPageConversion
    {
        public void SavePng(Stream image, Stream pdf, int zeroBasedPageNumber, int dpi)
        {
            image.WriteByte(137);
            throw exception;
        }
    }

    private sealed class CoordinatedConversion : IPdfPageConversion
    {
        private int _active;
        private int _callCount;
        private int _maximumConcurrency;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public List<int> Pages { get; } = [];

        public void SavePng(Stream image, Stream pdf, int zeroBasedPageNumber, int dpi)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            var call = Interlocked.Increment(ref _callCount);
            lock (Pages)
            {
                Pages.Add(zeroBasedPageNumber);
            }

            if (call == 1)
            {
                FirstEntered.TrySetResult();
                ReleaseFirst.Task.GetAwaiter().GetResult();
            }

            image.WriteByte(137);
            Interlocked.Decrement(ref _active);
        }

        private void UpdateMaximum(int active)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maximumConcurrency);
                if (active <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maximumConcurrency, active, current) != current);
        }
    }
}

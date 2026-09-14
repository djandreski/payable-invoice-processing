using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Ocr;

public sealed class PdfOcrDocumentProcessorTests
{
    [Fact]
    public async Task Renders_and_recognizes_pages_sequentially_disposes_each_page_and_normalizes_output()
    {
        var renderer = new TrackingRenderer();
        var engine = new TrackingOcrEngine(renderer);
        var processor = CreateProcessor(renderer, engine);
        await using var pdf = new MemoryStream("synthetic PDF"u8.ToArray());

        var document = await processor.ExtractAsync(pdf, pageCount: 3, CancellationToken.None);

        Assert.Equal(DocumentTextSource.Ocr, document.Source);
        Assert.Equal("Page 1 text\n\nPage 2 text\n\nPage 3 text", document.Text);
        Assert.Equal(new[] { 1, 2, 3 }, renderer.RenderedPages);
        Assert.Equal(new[] { 1, 2, 3 }, engine.RecognizedPages);
        Assert.All(renderer.Streams, stream => Assert.True(stream.IsDisposed));
        Assert.Equal(new[] { 300, 300, 300 }, renderer.Dpis);
        Assert.All(engine.Requests, request =>
        {
            Assert.Equal("eng", request.Language);
            Assert.Equal(TimeSpan.FromSeconds(30), request.PageTimeout);
        });
    }

    [Fact]
    public async Task Document_deadline_is_classified_and_current_page_is_disposed()
    {
        var renderer = new TrackingRenderer();
        var engine = new NeverCompletingOcrEngine();
        var processor = CreateProcessor(
            renderer,
            engine,
            new OcrOptions
            {
                PageTimeout = TimeSpan.FromSeconds(30),
                DocumentTimeout = TimeSpan.FromMilliseconds(50),
            });
        await using var pdf = new MemoryStream("synthetic PDF"u8.ToArray());

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() =>
            processor.ExtractAsync(pdf, 1, CancellationToken.None));

        Assert.Equal(ProcessingFailureCode.OcrDocumentTimeout, exception.Code);
        Assert.True(Assert.Single(renderer.Streams).IsDisposed);
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation()
    {
        var renderer = new TrackingRenderer();
        var engine = new NeverCompletingOcrEngine();
        var processor = CreateProcessor(renderer, engine);
        await using var pdf = new MemoryStream("synthetic PDF"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ExtractAsync(pdf, 1, cancellation.Token));
        Assert.Empty(renderer.Streams);
    }

    [Fact]
    public async Task Page_timeout_classification_is_not_replaced_by_document_timeout()
    {
        var renderer = new TrackingRenderer();
        var engine = new FailingOcrEngine(new OcrProcessingException(
            ProcessingFailureCode.OcrPageTimeout,
            "Safe page timeout."));
        var processor = CreateProcessor(renderer, engine);
        await using var pdf = new MemoryStream("synthetic PDF"u8.ToArray());

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() =>
            processor.ExtractAsync(pdf, 1, CancellationToken.None));

        Assert.Equal(ProcessingFailureCode.OcrPageTimeout, exception.Code);
        Assert.True(Assert.Single(renderer.Streams).IsDisposed);
    }

    private static PdfOcrDocumentProcessor CreateProcessor(
        IPdfPageRenderer renderer,
        IOcrEngine engine,
        OcrOptions? ocrOptions = null) =>
        new(
            renderer,
            engine,
            new PdfRenderingOptions { Dpi = 300, SerializeRendering = true },
            ocrOptions ?? new OcrOptions());

    private sealed class TrackingRenderer : IPdfPageRenderer
    {
        public List<int> RenderedPages { get; } = [];

        public List<int> Dpis { get; } = [];

        public List<TrackingStream> Streams { get; } = [];

        public Task<RenderedPage> RenderPageAsync(
            Stream pdf,
            int pageNumber,
            int dpi,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Streams.Count > 0)
            {
                Assert.True(Streams[^1].IsDisposed);
            }

            var stream = new TrackingStream();
            Streams.Add(stream);
            RenderedPages.Add(pageNumber);
            Dpis.Add(dpi);
            return Task.FromResult(new RenderedPage(pageNumber, stream));
        }
    }

    private sealed class TrackingOcrEngine(TrackingRenderer renderer) : IOcrEngine
    {
        public List<int> RecognizedPages { get; } = [];

        public List<OcrRequest> Requests { get; } = [];

        public Task<OcrPageText> RecognizeAsync(
            RenderedPage page,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(renderer.Streams[^1], page.Image);
            Assert.False(renderer.Streams[^1].IsDisposed);
            RecognizedPages.Add(page.PageNumber);
            Requests.Add(request);
            return Task.FromResult(new OcrPageText(
                page.PageNumber,
                $"\r\nPage\u00a0{page.PageNumber} text\n\n\n"));
        }
    }

    private sealed class NeverCompletingOcrEngine : IOcrEngine
    {
        public async Task<OcrPageText> RecognizeAsync(
            RenderedPage page,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FailingOcrEngine(Exception exception) : IOcrEngine
    {
        public Task<OcrPageText> RecognizeAsync(
            RenderedPage page,
            OcrRequest request,
            CancellationToken cancellationToken) => Task.FromException<OcrPageText>(exception);
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return base.DisposeAsync();
        }
    }
}

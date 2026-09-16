using System.Text.Json;
using System.Net.Http.Json;
using InvoiceReviewAssistant.EndToEndTests.Fixtures;
using InvoiceReviewAssistant.EndToEndTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace InvoiceReviewAssistant.EndToEndTests;

/// <summary>
/// Browser proofs for reviewer recovery paths. The assertions intentionally pair the
/// accessible UI state with direct requests so client restrictions cannot mask a
/// missing server-side lifecycle check.
/// </summary>
public sealed class FailureAndDecisionInvoiceJourneyTests
{
    [Fact]
    public async Task Persisted_processing_failure_is_reviewer_visible_and_remains_immutable_through_the_direct_api()
    {
        using var application = new ProcessingFailureApplication();
        var root = Path.Combine(Path.GetTempPath(), "invoice-review-e2e-failure-upload", Guid.NewGuid().ToString("N"));
        var browserHarness = await LaunchBrowserAsync();
        using var playwright = browserHarness.Playwright;
        await using var browser = browserHarness.Browser;
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        try
        {
            var baseUri = application.Start();
            var fixture = await AcceptanceFixtureCorpus.MaterializeAsync(
                AcceptanceFixtureCorpus.Get("processing-failure"), root, CancellationToken.None);
            using var client = application.CreateClient();
            await using var source = File.OpenRead(fixture);
            using var body = new MultipartFormDataContent();
            using var file = new StreamContent(source);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            body.Add(file, "file", "processing-failure.pdf");
            using var upload = await client.PostAsync("/api/invoices", body);
            Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
            using var invoice = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
            Assert.Equal("processingFailed", invoice.RootElement.GetProperty("status").GetString());
            Assert.Equal("AI_UNAVAILABLE", invoice.RootElement.GetProperty("processingFailure").GetProperty("code").GetString());
            var invoiceId = invoice.RootElement.GetProperty("id").GetString();

            await page.GotoAsync(new Uri(baseUri, $"invoices/{invoiceId}").ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("AI_UNAVAILABLE");
            await Assertions.Expect(page.GetByText("Fields are unavailable")).ToBeVisibleAsync();
            Assert.Equal(0, await page.GetByRole(AriaRole.Form, new() { Name = "Invoice draft" }).CountAsync());

            using var retry = await client.PostAsJsonAsync($"/api/invoices/{invoiceId}/validate", new { expectedVersion = 1 });
            await AssertHttpProblemAsync(retry, 409, "INVOICE_STATE_CONFLICT");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_upload_keeps_the_reviewer_in_an_accessible_recovery_dialog_with_a_stable_code()
    {
        await using var application = new HappyPathApplication();
        await application.StartAsync(CancellationToken.None);
        var browserHarness = await LaunchBrowserAsync();
        using var playwright = browserHarness.Playwright;
        await using var browser = browserHarness.Browser;
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        try
        {
            var fixture = await MaterializeAsync(application, "empty");
            await page.GotoAsync(application.ApiUri.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "Upload invoice" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Upload invoice PDF" });
            await dialog.Locator("input[type=file]").SetInputFilesAsync(fixture);
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Upload invoice", Exact = true }).ClickAsync();

            var alert = dialog.GetByRole(AriaRole.Alert);
            await Assertions.Expect(alert).ToContainTextAsync("PDF_EMPTY");
            await Assertions.Expect(dialog.GetByLabel("Invoice PDF")).ToBeFocusedAsync();
            Assert.DoesNotContain("/invoices/", page.Url, StringComparison.OrdinalIgnoreCase);
            application.Succeeded = true;
        }
        catch (Exception exception)
        {
            await ThrowWithArtifactsAsync(application, page, exception);
        }
    }

    [Fact]
    public async Task Blocking_validation_rejection_terminal_api_enforcement_and_restart_are_visible_to_the_reviewer()
    {
        await using var application = new HappyPathApplication();
        await application.StartAsync(CancellationToken.None);
        var browserHarness = await LaunchBrowserAsync();
        using var playwright = browserHarness.Playwright;
        await using var browser = browserHarness.Browser;
        await using var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        var page = await context.NewPageAsync();
        try
        {
            var invoiceId = await UploadAndOpenAsync(page, application, "text-usd");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Invoice number" }).FillAsync($"P5-04-{invoiceId[..8]}");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Total", Exact = true }).FillAsync("119.98");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Draft version: 2", new() { Exact = false })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Revalidate" }).ClickAsync();
            await Assertions.Expect(page.GetByText("AMOUNT_RECONCILIATION_FAILED", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true })).ToBeDisabledAsync();

            var blocked = await context.APIRequest.PostAsync(new Uri(application.ApiUri, $"api/invoices/{invoiceId}/approve").ToString(), new()
            {
                DataObject = new { expectedVersion = 2 },
            });
            await AssertProblemAsync(blocked, 409, "INVOICE_STATE_CONFLICT");

            await page.GetByRole(AriaRole.Button, new() { Name = "Reject", Exact = true }).ClickAsync();
            var rejection = page.GetByRole(AriaRole.Dialog, new() { Name = "Reject invoice?" });
            await rejection.GetByLabel("Rejection reason").FillAsync("Amounts do not reconcile with the source document.");
            await rejection.GetByRole(AriaRole.Button, new() { Name = "Confirm rejection" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Rejected invoice" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Amounts do not reconcile with the source document.")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("invoiceRejected", new() { Exact = true })).ToBeVisibleAsync();

            var document = await context.APIRequest.GetAsync(new Uri(application.ApiUri, $"api/invoices/{invoiceId}/document").ToString());
            Assert.True(document.Ok);
            Assert.Equal("application/pdf", document.Headers["content-type"]?.Split(';')[0]);
            var export = await context.APIRequest.GetAsync(new Uri(application.ApiUri, $"api/invoices/{invoiceId}/export").ToString());
            Assert.True(export.Ok);
            using (var exportJson = JsonDocument.Parse(await export.TextAsync()))
            {
                Assert.Equal("1.0", exportJson.RootElement.GetProperty("schemaVersion").GetString());
                Assert.Equal("rejected", exportJson.RootElement.GetProperty("invoice").GetProperty("status").GetString());
            }

            var immutable = await context.APIRequest.PostAsync(new Uri(application.ApiUri, $"api/invoices/{invoiceId}/reject").ToString(), new()
            {
                DataObject = new { expectedVersion = 2, reason = "A second decision must be rejected." },
            });
            await AssertProblemAsync(immutable, 409, "INVOICE_STATE_CONFLICT");

            await application.RestartAsync(CancellationToken.None);
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Rejected invoice" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("invoiceRejected", new() { Exact = true })).ToBeVisibleAsync();
            Assert.Equal(0, await page.GetByRole(AriaRole.Form, new() { Name = "Invoice draft" }).CountAsync());

            var afterRestart = await context.APIRequest.PostAsync(new Uri(application.ApiUri, $"api/invoices/{invoiceId}/validate").ToString(), new()
            {
                DataObject = new { expectedVersion = 2 },
            });
            await AssertProblemAsync(afterRestart, 409, "INVOICE_STATE_CONFLICT");
            application.Succeeded = true;
        }
        catch (Exception exception)
        {
            await ThrowWithArtifactsAsync(application, page, exception);
        }
    }

    [Fact]
    public async Task Stale_save_preserves_unsaved_values_and_offers_an_explicit_recovery_action()
    {
        await using var application = new HappyPathApplication();
        await application.StartAsync(CancellationToken.None);
        var browserHarness = await LaunchBrowserAsync();
        using var playwright = browserHarness.Playwright;
        await using var browser = browserHarness.Browser;
        await using var context = await browser.NewContextAsync();
        var reviewer = await context.NewPageAsync();
        IPage? concurrentReviewer = null;
        try
        {
            var invoiceId = await UploadAndOpenAsync(reviewer, application, "text-usd");
            concurrentReviewer = await context.NewPageAsync();
            await concurrentReviewer.GotoAsync(new Uri(application.ApiUri, $"invoices/{invoiceId}").ToString());
            await Assertions.Expect(concurrentReviewer.GetByRole(AriaRole.Textbox, new() { Name = "Invoice number" })).ToBeVisibleAsync();

            await reviewer.GetByRole(AriaRole.Textbox, new() { Name = "Supplier name" }).FillAsync("Reviewer A unsaved supplier");
            await concurrentReviewer.GetByRole(AriaRole.Textbox, new() { Name = "Invoice number" }).FillAsync("CONCURRENT-CHANGE-02");
            await concurrentReviewer.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
            await Assertions.Expect(concurrentReviewer.GetByText("Draft version: 2", new() { Exact = false })).ToBeVisibleAsync();

            await reviewer.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
            await Assertions.Expect(reviewer.GetByRole(AriaRole.Alert)).ToContainTextAsync("Your edits have been kept");
            await Assertions.Expect(reviewer.GetByText("Current server version: 2")).ToBeVisibleAsync();
            Assert.Equal("Reviewer A unsaved supplier", await reviewer.GetByRole(AriaRole.Textbox, new() { Name = "Supplier name" }).InputValueAsync());
            await Assertions.Expect(reviewer.GetByRole(AriaRole.Button, new() { Name = "Refetch current record" })).ToBeVisibleAsync();
            application.Succeeded = true;
        }
        catch (Exception exception)
        {
            await ThrowWithArtifactsAsync(application, reviewer, exception);
        }
        finally
        {
            if (concurrentReviewer is not null) await concurrentReviewer.CloseAsync();
        }
    }

    private static async Task<(IPlaywright Playwright, IBrowser Browser)> LaunchBrowserAsync()
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ExecutablePath = HappyPathApplication.ResolveChromiumExecutable(playwright.Chromium.ExecutablePath),
            });
            return (playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    private static async Task<string> UploadAndOpenAsync(IPage page, HappyPathApplication application, string fixtureName)
    {
        var fixture = await MaterializeAsync(application, fixtureName);
        await page.GotoAsync(application.ApiUri.ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Upload invoice" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Upload invoice PDF" });
        await dialog.Locator("input[type=file]").SetInputFilesAsync(fixture);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Upload invoice", Exact = true }).ClickAsync();
        await page.WaitForURLAsync("**/invoices/**");
        return page.Url.Split("/invoices/", StringSplitOptions.None)[1].Split('?', '#')[0];
    }

    private static Task<string> MaterializeAsync(HappyPathApplication application, string fixtureName) =>
        AcceptanceFixtureCorpus.MaterializeAsync(AcceptanceFixtureCorpus.Get(fixtureName), Path.Combine(application.DataRoot, "uploads"), CancellationToken.None);

    private static async Task AssertProblemAsync(IAPIResponse response, int expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, response.Status);
        using var problem = JsonDocument.Parse(await response.TextAsync());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertHttpProblemAsync(HttpResponseMessage response, int expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task ThrowWithArtifactsAsync(HappyPathApplication application, IPage? page, Exception exception)
    {
        await application.CaptureFailureArtifactsAsync(page);
        throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}{application.FailureDiagnostics()}");
    }
}

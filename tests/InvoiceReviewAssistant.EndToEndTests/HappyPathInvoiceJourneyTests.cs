using InvoiceReviewAssistant.EndToEndTests.Fixtures;
using InvoiceReviewAssistant.EndToEndTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace InvoiceReviewAssistant.EndToEndTests;

public sealed class HappyPathInvoiceJourneyTests
{
    [Fact]
    public async Task Reviewer_can_correct_revalidate_approve_and_inspect_a_complete_invoice_record()
    {
        await using var application = new HappyPathApplication();
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        try
        {
            await application.StartAsync(CancellationToken.None);
            var fixturePath = await AcceptanceFixtureCorpus.MaterializeAsync(
                AcceptanceFixtureCorpus.Get("text-usd"),
                Path.Combine(application.DataRoot, "uploads"),
                CancellationToken.None);

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ExecutablePath = HappyPathApplication.ResolveChromiumExecutable(playwright.Chromium.ExecutablePath),
            });
            context = await browser.NewContextAsync(new() { AcceptDownloads = true });
            await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
            page = await context.NewPageAsync();

            await page.GotoAsync(application.ApiUri.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "Upload invoice" }).ClickAsync();
            var uploadDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Upload invoice PDF" });
            await uploadDialog.Locator("input[type=file]").SetInputFilesAsync(fixturePath);
            await uploadDialog.GetByRole(AriaRole.Button, new() { Name = "Upload invoice", Exact = true }).ClickAsync();
            await page.WaitForURLAsync("**/invoices/**");

            await page.GetByRole(AriaRole.Textbox, new() { Name = "Supplier name" }).FillAsync("Synthetic Supply Company Reviewed");
            // Keep this isolated scenario out of the deterministic provider's
            // duplicate pair while still exercising an auditable correction.
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Invoice number" }).FillAsync("INV-0001-REVIEWED");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
            await Assertions.Expect(page.GetByText("All changes are saved.")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Draft version: 2", new() { Exact = false })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Corrected from: Synthetic Supply Company")).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Revalidate" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Status: readyForApproval", new() { Exact = false })).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
            var approvalDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Approve invoice?" });
            await approvalDialog.GetByRole(AriaRole.Button, new() { Name = "Confirm approval" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Approved invoice" })).ToBeVisibleAsync();

            await Assertions.Expect(page.GetByText("Draft version: 2", new() { Exact = false })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("for draft 2:", new() { Exact = false })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Synthetic Supply Company Reviewed", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Original extracted value: Synthetic Supply Company")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("invoiceApproved", new() { Exact = true })).ToBeVisibleAsync();

            var documentLink = page.GetByRole(AriaRole.Link, new() { Name = "Open source PDF: text-usd.pdf" });
            await Assertions.Expect(documentLink).ToBeVisibleAsync();
            var documentUrl = await documentLink.GetAttributeAsync("href");
            Assert.Equal("/api/invoices/" + documentUrl!.Split('/')[3] + "/document", documentUrl);
            var document = await context.APIRequest.GetAsync(new Uri(application.ApiUri, documentUrl).ToString());
            Assert.True(document.Ok, $"Source document request returned {document.Status}.");
            Assert.Equal("application/pdf", document.Headers["content-type"]?.Split(';')[0]);

            Assert.Equal(0, await page.GetByRole(AriaRole.Form, new() { Name = "Invoice draft" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Revalidate" }).CountAsync());
            Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).CountAsync());

            await context.Tracing.StopAsync(new() { Path = Path.Combine(application.DataRoot, "successful-trace.zip") });
            application.Succeeded = true;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(application.ArtifactRoot);
            if (context is not null)
            {
                await context.Tracing.StopAsync(new() { Path = Path.Combine(application.ArtifactRoot, "trace.zip") });
            }
            await application.CaptureFailureArtifactsAsync(page);
            throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}{application.FailureDiagnostics()}");
        }
        finally
        {
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }
}

using System.Text.Json;
using InvoiceReviewAssistant.EndToEndTests.Fixtures;
using InvoiceReviewAssistant.EndToEndTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace InvoiceReviewAssistant.EndToEndTests;

public sealed class AccessibilityAndResponsiveJourneyTests
{
    private static readonly (int Width, int Height)[] Viewports =
    [
        (1280, 900),
        (768, 900),
        (320, 800),
    ];

    [Fact]
    public async Task Queue_upload_and_review_pass_keyboard_responsive_and_browser_accessibility_checks()
    {
        await using var application = new HappyPathApplication();
        await application.StartAsync(CancellationToken.None);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = HappyPathApplication.ResolveChromiumExecutable(playwright.Chromium.ExecutablePath),
        });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        try
        {
            await page.AddInitScriptAsync(
                script: await File.ReadAllTextAsync(
                    FindWorkspaceFile("src/invoice-review-client/node_modules/axe-core/axe.min.js")));
            await page.GotoAsync(application.ApiUri.ToString());

            foreach (var viewport in Viewports)
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                await AssertNoPageOverflowAsync(page, viewport.Width);
                await AssertNoSeriousAxeViolationsAsync(page, $"queue at {viewport.Width}px");
                await CaptureIfRequestedAsync(page, $"queue-{viewport.Width}.png");
            }

            var uploadButton = page.GetByRole(AriaRole.Button, new() { Name = "Upload invoice" });
            await uploadButton.FocusAsync();
            Assert.True(await HasVisibleFocusIndicatorAsync(page));
            await page.Keyboard.PressAsync("Enter");
            var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Upload invoice PDF" });
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            Assert.True(await dialog.EvaluateAsync<bool>("dialog => dialog.contains(document.activeElement)"));
            await AssertNoSeriousAxeViolationsAsync(page, "upload dialog");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(uploadButton).ToBeFocusedAsync();

            var fixture = await AcceptanceFixtureCorpus.MaterializeAsync(
                AcceptanceFixtureCorpus.Get("text-usd"),
                Path.Combine(application.DataRoot, "uploads"),
                CancellationToken.None);
            await uploadButton.ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Upload invoice PDF" });
            await dialog.Locator("input[type=file]").SetInputFilesAsync(fixture);
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Upload invoice", Exact = true }).ClickAsync();
            await page.WaitForURLAsync("**/invoices/**");
            await Assertions.Expect(page.GetByRole(AriaRole.Form, new() { Name = "Invoice draft" })).ToBeVisibleAsync();

            foreach (var viewport in Viewports)
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                await AssertNoPageOverflowAsync(page, viewport.Width);
                await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Source invoice" })).ToBeVisibleAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Form, new() { Name = "Invoice draft" })).ToBeVisibleAsync();
                await AssertNoSeriousAxeViolationsAsync(page, $"review at {viewport.Width}px");
                await CaptureIfRequestedAsync(page, $"review-{viewport.Width}.png");
            }

            application.Succeeded = true;
        }
        catch (Exception exception)
        {
            await application.CaptureFailureArtifactsAsync(page);
            throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}{application.FailureDiagnostics()}");
        }
    }

    private static Task<bool> HasVisibleFocusIndicatorAsync(IPage page) => page.EvaluateAsync<bool>("""
        () => {
          const style = getComputedStyle(document.activeElement);
          return style.outlineStyle !== 'none' || style.boxShadow !== 'none';
        }
        """);

    private static async Task AssertNoPageOverflowAsync(IPage page, int viewportWidth)
    {
        var dimensions = await page.EvaluateAsync<int[]>("() => [document.documentElement.clientWidth, document.documentElement.scrollWidth]");
        Assert.Equal(viewportWidth, dimensions[0]);
        Assert.True(dimensions[1] <= dimensions[0], $"Page width {dimensions[1]} overflowed the {viewportWidth}px viewport.");
    }

    private static async Task AssertNoSeriousAxeViolationsAsync(IPage page, string state)
    {
        var result = await page.EvaluateAsync<string>("""
            async () => JSON.stringify((await axe.run(document, {
              runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] }
            })).violations.filter(item => item.impact === 'serious' || item.impact === 'critical'))
            """);
        using var violations = JsonDocument.Parse(result);
        Assert.True(
            violations.RootElement.GetArrayLength() == 0,
            $"Browser accessibility violations for {state}: {result}");
    }

    private static async Task CaptureIfRequestedAsync(IPage page, string fileName)
    {
        var root = Environment.GetEnvironmentVariable("INVOICE_REVIEW_ACCESSIBILITY_CAPTURE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, fileName), FullPage = true });
    }

    private static string FindWorkspaceFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("A required workspace accessibility asset could not be located.");
    }
}

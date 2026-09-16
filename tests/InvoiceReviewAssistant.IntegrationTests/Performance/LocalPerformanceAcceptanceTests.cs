using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using InvoiceReviewAssistant.IntegrationTests.Hardening;
using InvoiceReviewAssistant.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace InvoiceReviewAssistant.IntegrationTests.Performance;

[Collection(PerformanceAcceptanceCollection.Name)]
public sealed class LocalPerformanceAcceptanceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Goal = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Queue_and_local_mutations_meet_the_warm_one_thousand_record_goal()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        await SeedBackgroundInvoicesAsync(factory, 1_000);

        using (var warmup = await client.GetAsync("/api/invoices?page=1&pageSize=25"))
        {
            warmup.EnsureSuccessStatusCode();
        }
        await WarmLocalMutationsAsync(client);

        var queue = await MeasureAsync("queue-first-page", () => client.GetAsync("/api/invoices?page=1&pageSize=25"));
        var approvalId = await UploadAsync(client, "performance-approval.pdf");
        var save = await MeasureAsync("draft-save", () => client.PutAsJsonAsync(
            $"/api/invoices/{approvalId}/draft", DraftRequest(1, $"PERF-{approvalId[..8]}")));
        var validate = await MeasureAsync("validation", () => client.PostAsJsonAsync(
            $"/api/invoices/{approvalId}/validate", new { expectedVersion = 2 }));
        var approve = await MeasureAsync("approval", () => client.PostAsJsonAsync(
            $"/api/invoices/{approvalId}/approve", new { expectedVersion = 2 }));

        var rejectionId = await UploadAsync(client, "performance-rejection.pdf");
        var reject = await MeasureAsync("rejection", () => client.PostAsJsonAsync(
            $"/api/invoices/{rejectionId}/reject", new { expectedVersion = 1, reason = "Synthetic performance verification." }));

        AssertSuccess(queue.Response);
        AssertSuccess(save.Response);
        AssertSuccess(validate.Response);
        AssertSuccess(approve.Response);
        AssertSuccess(reject.Response);
        AssertUnderGoal(queue, save, validate, approve, reject);
    }

    private async Task<TimedResponse> MeasureAsync(string operation, Func<Task<HttpResponseMessage>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await action();
        stopwatch.Stop();
        output.WriteLine("{0}: {1:F1} ms", operation, stopwatch.Elapsed.TotalMilliseconds);
        return new TimedResponse(operation, stopwatch.Elapsed, response);
    }

    private static void AssertUnderGoal(params TimedResponse[] results)
    {
        foreach (var result in results)
        {
            Assert.True(result.Elapsed < Goal,
                $"{result.Operation} took {result.Elapsed.TotalMilliseconds:F1} ms; goal is under {Goal.TotalMilliseconds:F0} ms.");
        }
    }

    private static void AssertSuccess(HttpResponseMessage response)
    {
        try
        {
            Assert.True(response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices,
                $"Unexpected response {(int)response.StatusCode}: {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async Task<string> UploadAsync(HttpClient client, string filename)
    {
        using var body = new MultipartFormDataContent();
        using var file = new ByteArrayContent(IngestionFailureCatalogTests.CreatePdf(new string('A', 180)));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        body.Add(file, "file", filename);
        using var response = await client.PostAsync("/api/invoices", body);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task WarmLocalMutationsAsync(HttpClient client)
    {
        var approvalId = await UploadAsync(client, "performance-warm-approval.pdf");
        using (var save = await client.PutAsJsonAsync(
                   $"/api/invoices/{approvalId}/draft",
                   DraftRequest(1, $"WARM-{approvalId[..8]}")))
        {
            save.EnsureSuccessStatusCode();
        }
        using (var validate = await client.PostAsJsonAsync(
                   $"/api/invoices/{approvalId}/validate",
                   new { expectedVersion = 2 }))
        {
            validate.EnsureSuccessStatusCode();
        }
        using (var approve = await client.PostAsJsonAsync(
                   $"/api/invoices/{approvalId}/approve",
                   new { expectedVersion = 2 }))
        {
            approve.EnsureSuccessStatusCode();
        }

        var rejectionId = await UploadAsync(client, "performance-warm-rejection.pdf");
        using var reject = await client.PostAsJsonAsync(
            $"/api/invoices/{rejectionId}/reject",
            new { expectedVersion = 1, reason = "Synthetic warm-up record." });
        reject.EnsureSuccessStatusCode();
    }

    private static object DraftRequest(int expectedVersion, string invoiceNumber) => new
    {
        expectedVersion,
        draft = new
        {
            supplier = new { name = "Synthetic Supply Company", registrationId = "REG-001" },
            reference = new { invoiceNumber, purchaseOrderNumber = "PO-0001" },
            datesAndTerms = new { invoiceDate = "2026-09-01", dueDate = "2026-10-01", paymentTerms = "Net 30" },
            amounts = new { currency = "USD", subtotal = "100.00", taxAmount = "20.00", total = "120.00" },
            reviewNotes = (string?)null,
        },
    };

    private static async Task SeedBackgroundInvoicesAsync(TestApplicationFactory factory, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 1; index <= count; index++)
        {
            context.Invoices.Add(new InvoiceEntity
            {
                Id = Guid.Parse($"00000000-0000-0000-0001-{index:000000000000}"),
                Status = InvoiceStatus.ProcessingFailed.ToString(),
                DraftVersion = 1,
                CreatedAtUtc = epoch.AddSeconds(index),
                UpdatedAtUtc = epoch.AddSeconds(index),
                ProcessingFailureStage = ProcessingStage.AiExtraction.ToString(),
                ProcessingFailureCode = ProcessingFailureCode.AiUnavailable.ToString(),
                ProcessingFailureMessage = "Synthetic deterministic background record.",
                ProcessingFailedAtUtc = epoch.AddSeconds(index),
            });
        }

        await context.SaveChangesAsync();
    }

    private sealed record TimedResponse(string Operation, TimeSpan Elapsed, HttpResponseMessage Response);
}

[CollectionDefinition("Performance acceptance", DisableParallelization = true)]
public sealed class PerformanceAcceptanceCollection
{
    public const string Name = "Performance acceptance";
}

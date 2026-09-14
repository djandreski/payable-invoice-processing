using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hardening;

public sealed class WorkflowIntegrationHardeningTests
{
    [Fact]
    public async Task Concurrent_draft_saves_allow_one_winner_and_return_a_version_conflict_without_lost_updates()
    {
        await using var factory = new WorkflowApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await WorkflowTestSupport.UploadAsync(client);

        var firstTask = WorkflowTestSupport.SaveSupplierAsync(client, invoiceId, 1, "First contender");
        var secondTask = WorkflowTestSupport.SaveSupplierAsync(client, invoiceId, 1, "Second contender");
        var responses = await Task.WhenAll(firstTask, secondTask);
        using var first = responses[0];
        using var second = responses[1];

        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await WorkflowTestSupport.AssertProblemAsync(
            loser,
            HttpStatusCode.Conflict,
            "INVOICE_VERSION_CONFLICT",
            2);

        using var winnerBody = await WorkflowTestSupport.ReadJsonAsync(winner);
        var persistedWinner = winnerBody.RootElement
            .GetProperty("fields")
            .GetProperty("supplier")
            .GetProperty("name")
            .GetProperty("value")
            .GetString();

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(persistedWinner, invoice.SupplierName);
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Equal(1, await context.FieldCorrections.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(1, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.DraftSaved)));
    }

    [Fact]
    public async Task Draft_save_during_validation_returns_validation_stale_and_persists_only_the_winning_draft()
    {
        var barrier = new CoordinatedValidationBarrier();
        await using var factory = new WorkflowApplicationFactory(barrier);
        using var client = factory.CreateClient();
        var invoiceId = await WorkflowTestSupport.UploadAsync(client);

        var validationTask = client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/validate",
            new { expectedVersion = 1 });
        await barrier.WaitUntilEnteredAsync();

        using var save = await WorkflowTestSupport.SaveSupplierAsync(
            client,
            invoiceId,
            1,
            "Saved while validation was evaluating");
        save.EnsureSuccessStatusCode();
        barrier.Release();

        using var validation = await validationTask;
        await WorkflowTestSupport.AssertProblemAsync(
            validation,
            HttpStatusCode.Conflict,
            "VALIDATION_STALE",
            2);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal("Saved while validation was evaluating", invoice.SupplierName);
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Null(invoice.LastValidatedVersion);
        Assert.Null(invoice.CurrentValidationRunId);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
        Assert.Equal(1, await context.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(1, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.ValidationCompleted)));
        Assert.Equal(1, await context.FieldCorrections.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(1, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.DraftSaved)));
    }

    [Fact]
    public async Task A_winning_save_makes_validate_approve_and_reject_stale_before_state_checks()
    {
        await using var factory = new WorkflowApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await WorkflowTestSupport.UploadAsync(client);
        using (var save = await WorkflowTestSupport.SaveSupplierAsync(client, invoiceId, 1, "Current winner"))
        {
            save.EnsureSuccessStatusCode();
        }

        var requests = new[]
        {
            client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/validate", new { expectedVersion = 1 }),
            client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/approve", new { expectedVersion = 1 }),
            client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/reject", new { expectedVersion = 1, reason = "stale" }),
        };

        var responses = await Task.WhenAll(requests);
        try
        {
            foreach (var response in responses)
            {
                await WorkflowTestSupport.AssertProblemAsync(
                    response,
                    HttpStatusCode.Conflict,
                    "INVOICE_VERSION_CONFLICT",
                    2);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal("Current winner", invoice.SupplierName);
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
        Assert.Null(invoice.DecisionKind);
        Assert.Equal(1, await context.FieldCorrections.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(1, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.DraftSaved)));
        Assert.Equal(0, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId &&
            (row.EventType == nameof(AuditEventType.InvoiceApproved) ||
             row.EventType == nameof(AuditEventType.InvoiceRejected))));
    }

    [Fact]
    public async Task Structurally_valid_mutations_check_not_found_version_state_then_operation_conditions()
    {
        await using var factory = new WorkflowApplicationFactory();
        using var client = factory.CreateClient();
        var missingId = Guid.CreateVersion7();
        using (var missing = await client.PostAsJsonAsync(
            $"/api/invoices/{missingId:D}/reject",
            new { expectedVersion = 1, reason = " " }))
        {
            await WorkflowTestSupport.AssertProblemAsync(
                missing,
                HttpStatusCode.NotFound,
                "INVOICE_NOT_FOUND",
                null);
        }

        var invoiceId = await WorkflowTestSupport.UploadAsync(client);
        await using (var stateScope = factory.Services.CreateAsyncScope())
        {
            var context = stateScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var invoice = await context.Invoices.SingleAsync(row => row.Id == invoiceId);
            invoice.Status = nameof(InvoiceStatus.Processing);
            await context.SaveChangesAsync();
        }

        using (var stale = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/reject",
            new { expectedVersion = 2, reason = " " }))
        {
            await WorkflowTestSupport.AssertProblemAsync(
                stale,
                HttpStatusCode.Conflict,
                "INVOICE_VERSION_CONFLICT",
                1);
        }

        using (var state = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/reject",
            new { expectedVersion = 1, reason = " " }))
        {
            await WorkflowTestSupport.AssertProblemAsync(
                state,
                HttpStatusCode.Conflict,
                "INVOICE_STATE_CONFLICT",
                1);
        }

        await using (var conditionScope = factory.Services.CreateAsyncScope())
        {
            var context = conditionScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var invoice = await context.Invoices.SingleAsync(row => row.Id == invoiceId);
            invoice.Status = nameof(InvoiceStatus.ReviewRequired);
            await context.SaveChangesAsync();
        }

        using var reason = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/reject",
            new { expectedVersion = 1, reason = " " });
        await WorkflowTestSupport.AssertProblemAsync(
            reason,
            HttpStatusCode.BadRequest,
            "REJECTION_REASON_REQUIRED",
            null);
    }

    [Fact]
    public async Task Validation_audit_failure_rolls_back_run_results_status_pointer_and_history()
    {
        await using var factory = new WorkflowApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await WorkflowTestSupport.UploadAsync(client);
        Guid? originalRunId;
        int originalRunCount;
        int originalResultCount;
        int originalAuditCount;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var invoice = await setup.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
            originalRunId = invoice.CurrentValidationRunId;
            originalRunCount = await setup.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId);
            originalResultCount = await setup.ValidationResults.CountAsync(row => row.ValidationRun.InvoiceId == invoiceId);
            originalAuditCount = await setup.AuditEvents.CountAsync(row => row.InvoiceId == invoiceId);
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_explicit_validation_audit BEFORE INSERT ON AuditEvents " +
                "WHEN NEW.EventType = 'ValidationCompleted' BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/validate",
            new { expectedVersion = 1 });

        await WorkflowTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "PERSISTENCE_FAILED",
            null);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var persisted = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), persisted.Status);
        Assert.Equal(1, persisted.DraftVersion);
        Assert.Equal(originalRunId, persisted.CurrentValidationRunId);
        Assert.Equal(originalRunCount, await context.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(originalResultCount, await context.ValidationResults.CountAsync(row => row.ValidationRun.InvoiceId == invoiceId));
        Assert.Equal(originalAuditCount, await context.AuditEvents.CountAsync(row => row.InvoiceId == invoiceId));
    }

    [Fact]
    public async Task Duplicate_created_after_validation_persists_one_blocking_approval_validation_without_a_decision()
    {
        await using var factory = new WorkflowApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await WorkflowTestSupport.UploadAsync(client, "approval-target.pdf");
        using (var validation = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/validate",
            new { expectedVersion = 1 }))
        {
            validation.EnsureSuccessStatusCode();
        }

        Guid? priorRunId;
        int priorRunCount;
        int priorValidationAuditCount;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var before = beforeScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var invoice = await before.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
            priorRunId = invoice.CurrentValidationRunId;
            priorRunCount = await before.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId);
            priorValidationAuditCount = await before.AuditEvents.CountAsync(row =>
                row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.ValidationCompleted));
        }

        var duplicateId = await WorkflowTestSupport.UploadAsync(client, "approval-duplicate.pdf");
        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/approve",
            new { expectedVersion = 1 });

        await WorkflowTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "APPROVAL_BLOCKED",
            1);
        using (var body = await WorkflowTestSupport.ReadJsonAsync(response))
        {
            var fields = body.RootElement.GetProperty("fields");
            Assert.Equal(
                new[] { "draft.reference.invoiceNumber", "draft.supplier.name" },
                fields.EnumerateObject().Select(field => field.Name).Order().ToArray());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var persisted = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), persisted.Status);
        Assert.Equal(1, persisted.DraftVersion);
        Assert.Null(persisted.DecisionKind);
        Assert.NotNull(persisted.CurrentValidationRunId);
        Assert.NotEqual(priorRunId, persisted.CurrentValidationRunId);
        Assert.Equal(priorRunCount + 1, await context.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(priorValidationAuditCount + 1, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.ValidationCompleted)));
        Assert.Equal(0, await context.AuditEvents.CountAsync(row =>
            row.InvoiceId == invoiceId && row.EventType == nameof(AuditEventType.InvoiceApproved)));
        var duplicateResult = await context.ValidationResults.AsNoTracking().SingleAsync(row =>
            row.ValidationRunId == persisted.CurrentValidationRunId &&
            row.RuleCode == nameof(ValidationCode.PossibleDuplicateInvoice));
        Assert.Contains(duplicateId.ToString("D"), duplicateResult.DataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completed_workflow_has_deterministic_audit_order_and_correction_links_and_is_terminally_immutable()
    {
        await using var factory = new WorkflowApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await WorkflowTestSupport.UploadAsync(client);
        using (var save = await WorkflowTestSupport.SaveSupplierAsync(client, invoiceId, 1, "Audited supplier"))
        {
            save.EnsureSuccessStatusCode();
        }
        using (var validation = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/validate",
            new { expectedVersion = 2 }))
        {
            validation.EnsureSuccessStatusCode();
        }
        using (var approval = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/approve",
            new { expectedVersion = 2 }))
        {
            approval.EnsureSuccessStatusCode();
        }

        int auditCountBeforeTerminalAttempts;
        int correctionCountBeforeTerminalAttempts;
        int runCountBeforeTerminalAttempts;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var before = beforeScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            auditCountBeforeTerminalAttempts = await before.AuditEvents.CountAsync(row => row.InvoiceId == invoiceId);
            correctionCountBeforeTerminalAttempts = await before.FieldCorrections.CountAsync(row => row.InvoiceId == invoiceId);
            runCountBeforeTerminalAttempts = await before.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId);
        }

        var terminalAttempts = new[]
        {
            WorkflowTestSupport.SaveSupplierAsync(client, invoiceId, 2, "Forbidden edit"),
            client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/validate", new { expectedVersion = 2 }),
            client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/approve", new { expectedVersion = 2 }),
            client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/reject", new { expectedVersion = 2, reason = "forbidden" }),
        };
        var terminalResponses = await Task.WhenAll(terminalAttempts);
        try
        {
            foreach (var response in terminalResponses)
            {
                await WorkflowTestSupport.AssertProblemAsync(
                    response,
                    HttpStatusCode.Conflict,
                    "INVOICE_STATE_CONFLICT",
                    2);
            }
        }
        finally
        {
            foreach (var response in terminalResponses)
            {
                response.Dispose();
            }
        }

        using var historyResponse = await client.GetAsync($"/api/invoices/{invoiceId:D}/history?page=1&pageSize=100");
        historyResponse.EnsureSuccessStatusCode();
        using var history = await WorkflowTestSupport.ReadJsonAsync(historyResponse);
        var events = history.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var ids = events.Select(item => long.Parse(item.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());

        var draftSaved = Assert.Single(events, item => item.GetProperty("type").GetString() == "draftSaved");
        var draftEventId = draftSaved.GetProperty("id").GetString();
        var correction = Assert.Single(draftSaved.GetProperty("details").GetProperty("changes").EnumerateArray());
        Assert.Equal(draftEventId, correction.GetProperty("auditEventId").GetString());
        Assert.Equal("supplierName", correction.GetProperty("field").GetString());
        Assert.Equal(2, correction.GetProperty("draftVersion").GetInt32());

        var approvalValidationIndex = Array.FindLastIndex(
            events,
            item => item.GetProperty("type").GetString() == "validationCompleted" &&
                item.GetProperty("details").GetProperty("trigger").GetString() == "approval");
        var approvedIndex = Array.FindIndex(events, item => item.GetProperty("type").GetString() == "invoiceApproved");
        Assert.True(approvalValidationIndex >= 0);
        Assert.Equal(approvalValidationIndex + 1, approvedIndex);
        Assert.True(long.Parse(events[approvalValidationIndex].GetProperty("id").GetString()!) <
            long.Parse(events[approvedIndex].GetProperty("id").GetString()!));

        await using var afterScope = factory.Services.CreateAsyncScope();
        var context = afterScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.Approved), invoice.Status);
        Assert.Equal(nameof(DecisionKind.Approved), invoice.DecisionKind);
        Assert.Equal("Audited supplier", invoice.SupplierName);
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Equal(auditCountBeforeTerminalAttempts, await context.AuditEvents.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(correctionCountBeforeTerminalAttempts, await context.FieldCorrections.CountAsync(row => row.InvoiceId == invoiceId));
        Assert.Equal(runCountBeforeTerminalAttempts, await context.ValidationRuns.CountAsync(row => row.InvoiceId == invoiceId));
    }

    [Fact]
    public async Task Integrated_journeys_emit_every_material_audit_event_type()
    {
        var observedTypes = new HashSet<string>(StringComparer.Ordinal);
        await using (var factory = new WorkflowApplicationFactory())
        {
            using var client = factory.CreateClient();
            var approvedId = await WorkflowTestSupport.UploadAsync(client, "approved-audit.pdf");
            using (var save = await WorkflowTestSupport.SaveSupplierAsync(client, approvedId, 1, "Audit supplier"))
            {
                save.EnsureSuccessStatusCode();
            }
            using (var validation = await client.PostAsJsonAsync(
                $"/api/invoices/{approvedId:D}/validate",
                new { expectedVersion = 2 }))
            {
                validation.EnsureSuccessStatusCode();
            }
            using (var approval = await client.PostAsJsonAsync(
                $"/api/invoices/{approvedId:D}/approve",
                new { expectedVersion = 2 }))
            {
                approval.EnsureSuccessStatusCode();
            }

            var approvedDocument = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(factory.RootPath, "documents"),
                "*.pdf",
                SearchOption.TopDirectoryOnly));
            File.Delete(approvedDocument);
            using (var unavailable = await client.GetAsync($"/api/invoices/{approvedId:D}/document"))
            {
                await WorkflowTestSupport.AssertProblemAsync(
                    unavailable,
                    HttpStatusCode.InternalServerError,
                    "DOCUMENT_UNAVAILABLE",
                    null);
            }

            var rejectedId = await WorkflowTestSupport.UploadAsync(client, "rejected-audit.pdf");
            using (var rejection = await client.PostAsJsonAsync(
                $"/api/invoices/{rejectedId:D}/reject",
                new { expectedVersion = 1, reason = "Not payable" }))
            {
                rejection.EnsureSuccessStatusCode();
            }

            await AddHistoryTypesAsync(client, approvedId, observedTypes);
            await AddHistoryTypesAsync(client, rejectedId, observedTypes);
        }

        await using (var failureFactory = new WorkflowApplicationFactory(failExtraction: true))
        {
            using var failureClient = failureFactory.CreateClient();
            var failedId = await WorkflowTestSupport.UploadAsync(failureClient, "failed-audit.pdf");
            await AddHistoryTypesAsync(failureClient, failedId, observedTypes);
        }

        Assert.Equal(
            new[]
            {
                "documentIntegrityChanged",
                "draftSaved",
                "extractionCompleted",
                "extractionFailed",
                "invoiceApproved",
                "invoiceRejected",
                "invoiceUploaded",
                "validationCompleted",
            },
            observedTypes.Order().ToArray());
    }

    private static async Task AddHistoryTypesAsync(
        HttpClient client,
        Guid invoiceId,
        ISet<string> destination)
    {
        using var response = await client.GetAsync($"/api/invoices/{invoiceId:D}/history?page=1&pageSize=100");
        response.EnsureSuccessStatusCode();
        using var body = await WorkflowTestSupport.ReadJsonAsync(response);
        foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
        {
            destination.Add(item.GetProperty("type").GetString()!);
        }
    }
}

internal static class WorkflowTestSupport
{
    public static async Task<Guid> UploadAsync(HttpClient client, string filename = "workflow.pdf")
    {
        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(IngestionFailureCatalogTests.CreatePdf(new string('A', 150)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        multipart.Add(content, "file", filename);
        using var response = await client.PostAsync("/api/invoices", multipart);
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> SaveSupplierAsync(
        HttpClient client,
        Guid invoiceId,
        int expectedVersion,
        string supplierName) => client.PutAsJsonAsync(
        $"/api/invoices/{invoiceId:D}/draft",
        new
        {
            expectedVersion,
            draft = new
            {
                supplier = new { name = supplierName, registrationId = "REG-001" },
                reference = new { invoiceNumber = "INV-0001", purchaseOrderNumber = "PO-0001" },
                datesAndTerms = new { invoiceDate = "2026-09-01", dueDate = "2026-10-01", paymentTerms = "Net 30" },
                amounts = new { currency = "USD", subtotal = "100.00", taxAmount = "20.00", total = "120.00" },
                reviewNotes = (string?)null,
            },
        });

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

    public static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        int? expectedCurrentVersion)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(
            $"urn:invoice-review-assistant:problem:{expectedCode.ToLowerInvariant().Replace('_', '-')}",
            root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        if (expectedCurrentVersion is { } version)
        {
            Assert.Equal(version, root.GetProperty("currentVersion").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("currentVersion").ValueKind);
        }
    }
}

internal sealed class WorkflowApplicationFactory(
    CoordinatedValidationBarrier? validationBarrier = null,
    bool failExtraction = false) : WebApplicationFactory<Program>
{
    public string RootPath { get; } = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "invoice-workflow-hardening-tests",
        Guid.NewGuid().ToString("N")));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                [$"{ExtractionOptions.SectionName}:Profile"] = ExtractionProfile.Deterministic.ToString(),
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new IncrementingTimeProvider());
            if (validationBarrier is not null)
            {
                services.RemoveAll<IExplicitValidationCommitBarrier>();
                services.AddSingleton<IExplicitValidationCommitBarrier>(validationBarrier);
            }

            if (failExtraction)
            {
                services.RemoveAll<IInvoiceExtractionProvider>();
                services.AddSingleton<IInvoiceExtractionProvider, AlwaysFailingExtractionProvider>();
            }
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(RootPath))
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(RootPath, "invoices.db"),
            }.ToString();
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

internal sealed class AlwaysFailingExtractionProvider : IInvoiceExtractionProvider
{
    public Task<InvoiceExtractionProposal> ExtractAsync(
        NormalizedDocumentText document,
        ExtractionSchemaVersion schemaVersion,
        CancellationToken cancellationToken) =>
        throw new ProcessingProviderException(
            ProcessingStage.AiExtraction,
            ProcessingFailureCode.AiUnavailable,
            "The deterministic extraction provider was unavailable.");
}

internal sealed class CoordinatedValidationBarrier : IExplicitValidationCommitBarrier
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release() => _release.TrySetResult();

    public async Task BeforeCommitCheckAsync(
        InvoiceId invoiceId,
        DraftVersion snapshotVersion,
        CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class IncrementingTimeProvider : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero).Ticks;

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

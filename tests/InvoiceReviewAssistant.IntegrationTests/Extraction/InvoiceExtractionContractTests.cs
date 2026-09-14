using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Extraction;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Extraction;

public sealed class InvoiceExtractionContractTests
{
    private static readonly ExtractionSchemaVersion V1 = new(InvoiceExtractionSchema.SelectorVersion);

    [Fact]
    public async Task Checked_in_schema_is_exactly_the_canonical_contract_schema()
    {
        var repositoryRoot = FindRepositoryRoot();
        var schemaText = await File.ReadAllTextAsync(SchemaPath(repositoryRoot));
        var contractsText = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "docs", "CONTRACTS.md"));
        var documentedSchema = ExtractDocumentedSchema(contractsText);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(documentedSchema), JsonNode.Parse(schemaText)));
    }

    [Fact]
    public async Task Complete_response_maps_all_fields_confidence_provenance_and_derived_terms()
    {
        var mapper = await CreateMapperAsync();

        var proposal = mapper.Map(CompleteJson, V1);

        Assert.Equal("Example Office Goods", proposal.Fields.SupplierName);
        Assert.Equal("REG-0017", proposal.Fields.SupplierRegistrationId);
        Assert.Equal("INV-000042", proposal.Fields.InvoiceNumber);
        Assert.Equal("PO-0009", proposal.Fields.PurchaseOrderNumber);
        Assert.Equal(new DateOnly(2026, 9, 1), proposal.Fields.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 10, 1), proposal.Fields.DueDate);
        Assert.Equal("Net 30", proposal.Fields.PaymentTerms);
        Assert.Equal(30, proposal.Fields.NormalizedPaymentTermsDays);
        Assert.Equal("USD", proposal.Fields.Currency);
        Assert.Equal(100.00m, proposal.Fields.Subtotal);
        Assert.Equal(18.00m, proposal.Fields.TaxAmount);
        Assert.Equal(118.00m, proposal.Fields.Total);

        Assert.Equal(11, proposal.FieldMetadata.Count);
        Assert.Equal(
            Enum.GetValues<InvoiceFieldKey>().Where(field => field != InvoiceFieldKey.ReviewNotes),
            proposal.FieldMetadata.Select(metadata => metadata.Field));
        Assert.All(proposal.FieldMetadata, metadata =>
        {
            Assert.Equal(FieldSource.AiInference, metadata.OriginalSource);
            Assert.Equal(FieldSource.AiInference, metadata.CurrentSource);
            Assert.Null(metadata.LastCorrectedAtUtc);
            Assert.Equal(proposal.Fields.GetCanonicalValue(metadata.Field), metadata.OriginalValue);
        });
        Assert.Equal(0.99d, proposal.FieldMetadata.Single(item => item.Field == InvoiceFieldKey.SupplierName).Confidence);
        Assert.Null(proposal.FieldMetadata.Single(item => item.Field == InvoiceFieldKey.PurchaseOrderNumber).Confidence);
    }

    [Fact]
    public async Task Complete_all_null_response_maps_no_fabricated_values()
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        foreach (var field in FieldPaths)
        {
            SetField(root, field, null, null);
        }

        var proposal = mapper.Map(root.ToJsonString(), V1);

        Assert.Equal(
            new InvoiceFields(null, null, null, null, null, null, null, null, null, null, null, null),
            proposal.Fields);
        Assert.Equal(11, proposal.FieldMetadata.Count);
        Assert.All(proposal.FieldMetadata, metadata =>
        {
            Assert.Equal(CanonicalValueKind.Null, metadata.OriginalValue.Kind);
            Assert.Null(metadata.Confidence);
            Assert.Equal(FieldSource.AiInference, metadata.OriginalSource);
        });
    }

    [Theory]
    [MemberData(nameof(AllFieldPaths))]
    public async Task Null_values_require_null_confidence(string field)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, field, null, 0.75d);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.NullConfidenceMismatch, exception.Code);
        Assert.Equal(field, exception.Field);
        Assert.DoesNotContain("Example Office Goods", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("supplier.name")]
    [InlineData("supplier.registrationId")]
    [InlineData("reference.invoiceNumber")]
    [InlineData("reference.purchaseOrderNumber")]
    [InlineData("datesAndTerms.paymentTerms")]
    public async Task Non_null_text_must_not_be_blank(string field)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, field, " \t\r\n", null);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.BlankText, exception.Code);
        Assert.Equal(field, exception.Field);
    }

    [Theory]
    [InlineData("2026-02-29")]
    [InlineData("2026-9-01")]
    [InlineData("2026-09-01T00:00:00Z")]
    public async Task Date_format_validation_is_required(string value)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "datesAndTerms.invoiceDate", value, 0.90d);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.SchemaViolation, exception.Code);
    }

    [Fact]
    public async Task Schema_valid_but_unmappable_date_is_rejected_semantically()
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "datesAndTerms.invoiceDate", "0000-01-01", 0.90d);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Contains(
            exception.Code,
            new[] { InvoiceExtractionValidationCode.SchemaViolation, InvoiceExtractionValidationCode.InvalidDate });
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("01.00")]
    [InlineData("+1.00")]
    [InlineData("1.000")]
    [InlineData("1e2")]
    public async Task Malformed_money_is_rejected_by_the_schema(string value)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "amounts.total", value, 0.95d);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.SchemaViolation, exception.Code);
    }

    [Fact]
    public async Task Schema_valid_money_outside_decimal_range_is_rejected_without_partial_mapping()
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "amounts.total", "999999999999999999999999999.99", 0.95d);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.InvalidMoney, exception.Code);
        Assert.Equal("amounts.total", exception.Field);
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData(" USD")]
    [InlineData("U1D")]
    public async Task Invalid_currency_candidates_are_rejected(string value)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "amounts.currency", value, 0.95d);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.SchemaViolation, exception.Code);
    }

    [Fact]
    public async Task Three_letter_currency_is_preserved_as_a_candidate_for_later_deterministic_validation()
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "amounts.currency", "XYZ", 0.95d);

        var proposal = mapper.Map(root.ToJsonString(), V1);

        Assert.Equal("XYZ", proposal.Fields.Currency);
    }

    [Theory]
    [InlineData(-0.001d)]
    [InlineData(1.001d)]
    public async Task Confidence_outside_the_closed_unit_interval_is_rejected(double confidence)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "supplier.name", "Synthetic Supplier", confidence);

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));

        Assert.Equal(InvoiceExtractionValidationCode.SchemaViolation, exception.Code);
    }

    [Fact]
    public async Task Non_null_value_may_have_unknown_confidence()
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "supplier.name", "Synthetic Supplier", null);

        var proposal = mapper.Map(root.ToJsonString(), V1);

        Assert.Null(proposal.FieldMetadata.Single(item => item.Field == InvoiceFieldKey.SupplierName).Confidence);
    }

    [Theory]
    [InlineData("due on receipt", 0)]
    [InlineData("NET30", 30)]
    [InlineData("45 calendar days", 45)]
    [InlineData("end of next month", null)]
    public async Task Payment_term_days_are_derived_conservatively(string paymentTerms, int? expectedDays)
    {
        var mapper = await CreateMapperAsync();
        var root = ParseComplete();
        SetField(root, "datesAndTerms.paymentTerms", paymentTerms, 0.88d);

        var proposal = mapper.Map(root.ToJsonString(), V1);

        Assert.Equal(expectedDays, proposal.Fields.NormalizedPaymentTermsDays);
    }

    [Fact]
    public async Task Unknown_properties_are_rejected_at_root_and_nested_object_boundaries()
    {
        var mapper = await CreateMapperAsync();
        var rootExtra = ParseComplete();
        rootExtra["reviewNotes"] = "must not be extracted";
        var nestedExtra = ParseComplete();
        nestedExtra["supplier"]!.AsObject()["status"] = "approved";
        var fieldExtra = ParseComplete();
        fieldExtra["supplier"]!["name"]!.AsObject()["source"] = "nativeText";

        AssertSchemaViolation(mapper, rootExtra);
        AssertSchemaViolation(mapper, nestedExtra);
        AssertSchemaViolation(mapper, fieldExtra);
    }

    [Fact]
    public async Task Missing_properties_wrong_types_and_wrong_payload_version_are_rejected()
    {
        var mapper = await CreateMapperAsync();
        var missing = ParseComplete();
        missing["amounts"]!.AsObject().Remove("total");
        var wrongType = ParseComplete();
        wrongType["amounts"]!["total"]!["value"] = 118.00m;
        var wrongVersion = ParseComplete();
        wrongVersion["schemaVersion"] = "2.0";

        AssertSchemaViolation(mapper, missing);
        AssertSchemaViolation(mapper, wrongType);
        AssertSchemaViolation(mapper, wrongVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not-json")]
    public async Task Malformed_json_is_rejected_without_exposing_it(string json)
    {
        var mapper = await CreateMapperAsync();

        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(json, V1));

        Assert.Equal(InvoiceExtractionValidationCode.MalformedJson, exception.Code);
        if (json.Length > 0)
        {
            Assert.DoesNotContain(json, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unsupported_schema_selector_is_rejected_before_mapping()
    {
        var mapper = await CreateMapperAsync();

        var exception = Assert.Throws<InvoiceExtractionValidationException>(
            () => mapper.Map(CompleteJson, new ExtractionSchemaVersion("v2")));

        Assert.Equal(InvoiceExtractionValidationCode.UnsupportedSchemaVersion, exception.Code);
    }

    [Fact]
    public async Task Schema_loader_leaves_caller_owned_stream_open()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(SchemaPath(FindRepositoryRoot()))));

        _ = await new InvoiceExtractionSchemaLoader().LoadAsync(stream);

        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Deterministic_provider_returns_the_provider_neutral_proposal_independent_of_text_source()
    {
        var proposal = (await CreateMapperAsync()).Map(CompleteJson, V1);
        IInvoiceExtractionProvider provider = new DeterministicInvoiceExtractionProvider(proposal);

        var native = await provider.ExtractAsync(
            new NormalizedDocumentText("synthetic native text", DocumentTextSource.NativeText),
            V1,
            CancellationToken.None);
        var ocr = await provider.ExtractAsync(
            new NormalizedDocumentText("different synthetic OCR text", DocumentTextSource.Ocr),
            V1,
            CancellationToken.None);

        Assert.Equal(native.Fields, ocr.Fields);
        Assert.Equal(native.FieldMetadata, ocr.FieldMetadata);
        Assert.NotSame(native, ocr);
        Assert.DoesNotContain(
            typeof(InvoiceExtractionProposal).GetProperties(),
            property => property.PropertyType == typeof(DocumentTextSource) || property.PropertyType == typeof(DocumentTextSource?));
    }

    [Fact]
    public async Task Deterministic_provider_honors_cancellation_and_schema_selection()
    {
        var provider = new DeterministicInvoiceExtractionProvider((await CreateMapperAsync()).Map(CompleteJson, V1));
        var input = new NormalizedDocumentText("synthetic", DocumentTextSource.NativeText);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ExtractAsync(input, V1, cancellation.Token));
        var exception = await Assert.ThrowsAsync<InvoiceExtractionValidationException>(
            () => provider.ExtractAsync(input, new ExtractionSchemaVersion("v2"), CancellationToken.None));
        Assert.Equal(InvoiceExtractionValidationCode.UnsupportedSchemaVersion, exception.Code);
    }

    public static TheoryData<string> AllFieldPaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in FieldPaths)
            {
                data.Add(path);
            }

            return data;
        }
    }

    private static readonly string[] FieldPaths =
    [
        "supplier.name",
        "supplier.registrationId",
        "reference.invoiceNumber",
        "reference.purchaseOrderNumber",
        "datesAndTerms.invoiceDate",
        "datesAndTerms.dueDate",
        "datesAndTerms.paymentTerms",
        "amounts.currency",
        "amounts.subtotal",
        "amounts.taxAmount",
        "amounts.total",
    ];

    private static async Task<InvoiceExtractionMapper> CreateMapperAsync()
    {
        var schema = await new InvoiceExtractionSchemaLoader().LoadFromFileAsync(
            SchemaPath(FindRepositoryRoot()),
            CancellationToken.None);
        return new InvoiceExtractionMapper(schema);
    }

    private static void AssertSchemaViolation(InvoiceExtractionMapper mapper, JsonObject root)
    {
        var exception = Assert.Throws<InvoiceExtractionValidationException>(() => mapper.Map(root.ToJsonString(), V1));
        Assert.Equal(InvoiceExtractionValidationCode.SchemaViolation, exception.Code);
    }

    private static JsonObject ParseComplete() => JsonNode.Parse(CompleteJson)!.AsObject();

    private static void SetField(JsonObject root, string fieldPath, object? value, double? confidence)
    {
        var segments = fieldPath.Split('.');
        var field = root[segments[0]]![segments[1]]!.AsObject();
        field["value"] = value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
        field["confidence"] = confidence;
    }

    private static string ExtractDocumentedSchema(string contracts)
    {
        var section = contracts.IndexOf("### 14.2 Canonical invoice_extraction_v1 schema", StringComparison.Ordinal);
        Assert.True(section >= 0);
        var openingFence = contracts.IndexOf("~~~json", section, StringComparison.Ordinal);
        Assert.True(openingFence >= 0);
        var jsonStart = contracts.IndexOf('\n', openingFence) + 1;
        var closingFence = contracts.IndexOf("~~~", jsonStart, StringComparison.Ordinal);
        Assert.True(closingFence > jsonStart);
        return contracts[jsonStart..closingFence];
    }

    private static string SchemaPath(string repositoryRoot) => Path.Combine(
        repositoryRoot,
        "src",
        "InvoiceReviewAssistant.Infrastructure",
        "Extraction",
        "Schemas",
        "invoice_extraction_v1.schema.json");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InvoiceReviewAssistant.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root for extraction contract validation.");
    }

    private const string CompleteJson = """
        {
          "schemaVersion": "1.0",
          "supplier": {
            "name": { "value": "Example Office Goods", "confidence": 0.99 },
            "registrationId": { "value": "REG-0017", "confidence": 0.92 }
          },
          "reference": {
            "invoiceNumber": { "value": "INV-000042", "confidence": 0.97 },
            "purchaseOrderNumber": { "value": "PO-0009", "confidence": null }
          },
          "datesAndTerms": {
            "invoiceDate": { "value": "2026-09-01", "confidence": 0.95 },
            "dueDate": { "value": "2026-10-01", "confidence": 0.93 },
            "paymentTerms": { "value": "Net 30", "confidence": 0.91 }
          },
          "amounts": {
            "currency": { "value": "USD", "confidence": 0.98 },
            "subtotal": { "value": "100.00", "confidence": 0.96 },
            "taxAmount": { "value": "18.00", "confidence": 0.94 },
            "total": { "value": "118.00", "confidence": 0.99 }
          }
        }
        """;
}

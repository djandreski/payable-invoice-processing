using System.Text.Json;
using InvoiceReviewAssistant.Api.Contracts;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Contracts;

public sealed class HttpContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = InvoiceJsonDefaults.Create();

    [Fact]
    public void Contract_primitives_use_lowercase_uuids_two_decimal_money_and_string_enums()
    {
        var id = Guid.Parse("A9F5B2C1-0E30-4A41-8CDD-212DE0F7E4B0");
        var json = JsonSerializer.Serialize(new ContractPrimitiveFixture(id, 12m, ValidationCode.RequiredFieldMissing), Options);

        Assert.Contains("a9f5b2c1-0e30-4a41-8cdd-212de0f7e4b0", json, StringComparison.Ordinal);
        Assert.Contains("\"12.00\"", json, StringComparison.Ordinal);
        Assert.Contains("REQUIRED_FIELD_MISSING", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_members_and_noncanonical_money_are_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ValidateInvoiceRequest>("{\"expectedVersion\":1,\"unexpected\":true}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MoneyFixture>("{\"amount\":12}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MoneyFixture>("{\"amount\":\"12.0\"}", Options));
    }

    private sealed record ContractPrimitiveFixture(Guid Id, decimal Amount, ValidationCode Code);
    private sealed class MoneyFixture { public decimal Amount { get; init; } }
}

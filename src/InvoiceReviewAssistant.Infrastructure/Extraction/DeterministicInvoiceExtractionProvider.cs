using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Infrastructure.Extraction;

/// <summary>
/// A content-free deterministic provider for default automated journeys. The fixture owns
/// the proposal definition; each call receives an isolated immutable-shape copy.
/// </summary>
public sealed class DeterministicInvoiceExtractionProvider : IInvoiceExtractionProvider
{
    private readonly InvoiceExtractionProposal _proposal;

    public DeterministicInvoiceExtractionProvider(InvoiceExtractionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _proposal = Copy(proposal);
    }

    public Task<InvoiceExtractionProposal> ExtractAsync(
        NormalizedDocumentText document,
        ExtractionSchemaVersion schemaVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(schemaVersion.Value, InvoiceExtractionSchema.SelectorVersion, StringComparison.Ordinal))
        {
            throw new InvoiceExtractionValidationException(
                InvoiceExtractionValidationCode.UnsupportedSchemaVersion,
                "schemaVersion");
        }

        return Task.FromResult(Copy(_proposal));
    }

    private static InvoiceExtractionProposal Copy(InvoiceExtractionProposal proposal) => new(
        proposal.Fields with { },
        proposal.FieldMetadata.Select(metadata => metadata with { }).ToArray());
}

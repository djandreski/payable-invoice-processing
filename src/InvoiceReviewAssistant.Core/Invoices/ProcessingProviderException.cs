namespace InvoiceReviewAssistant.Core.Invoices;

/// <summary>
/// Provider-neutral classified failure raised by document-processing adapters.
/// The stage, code, and message are safe for application services to persist.
/// </summary>
public class ProcessingProviderException : InvalidOperationException
{
    public ProcessingProviderException(
        ProcessingStage stage,
        ProcessingFailureCode code,
        string safeMessage,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        if (string.IsNullOrWhiteSpace(safeMessage))
        {
            throw new ArgumentException("A reviewer-safe processing message is required.", nameof(safeMessage));
        }

        Stage = stage;
        Code = code;
    }

    public ProcessingStage Stage { get; }

    public ProcessingFailureCode Code { get; }
}

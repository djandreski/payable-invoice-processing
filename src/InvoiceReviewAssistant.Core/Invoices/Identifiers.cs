namespace InvoiceReviewAssistant.Core.Invoices;

public readonly record struct InvoiceId
{
    public InvoiceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invoice identifiers cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static InvoiceId New() => new(Guid.NewGuid());
}

public readonly record struct DraftVersion
{
    public DraftVersion(int value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Draft versions start at one.");
        }

        Value = value;
    }

    public int Value { get; }

    public DraftVersion Next() => new(checked(Value + 1));
}

public readonly record struct SequenceId
{
    public SequenceId(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Sequence identifiers cannot be negative.");
        }

        Value = value;
    }

    public long Value { get; }
}

public readonly record struct ValidationRunId
{
    public ValidationRunId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Validation run identifiers cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ValidationRunId New() => new(Guid.NewGuid());
}

public readonly record struct DocumentStorageKey
{
    public DocumentStorageKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A storage key is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

public readonly record struct ExtractionSchemaVersion
{
    public ExtractionSchemaVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An extraction schema version is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

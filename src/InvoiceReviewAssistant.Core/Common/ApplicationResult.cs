namespace InvoiceReviewAssistant.Core.Common;

public sealed record ApplicationError(string Code, string Message);

public readonly record struct ApplicationResult
{
    private ApplicationResult(ApplicationError? error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public ApplicationError? Error { get; }

    public static ApplicationResult Success() => new(null);

    public static ApplicationResult Failure(ApplicationError error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

public readonly record struct ApplicationResult<T>
{
    private ApplicationResult(T? value, ApplicationError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public ApplicationError? Error { get; }

    public static ApplicationResult<T> Success(T value) => new(value, null);

    public static ApplicationResult<T> Failure(ApplicationError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}

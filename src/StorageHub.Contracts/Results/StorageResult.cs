using System.Diagnostics.CodeAnalysis;

namespace StorageHub.Contracts.Results;

/// <summary>Represents success or one structured expected failure.</summary>
public sealed class StorageResult
{
    private static readonly StorageResult Successful = new(null);

    private StorageResult(StorageFailure? error)
    {
        Error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public StorageFailure? Error { get; }

    public static StorageResult Success() => Successful;

    public static StorageResult Fail(StorageFailure error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Represents a successful value or one structured expected failure.</summary>
public sealed class StorageResult<T>
{
    private readonly T? _value;

    private StorageResult(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    private StorageResult(StorageFailure error)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public StorageFailure? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result does not contain a value.");

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Result factories are intentionally discoverable on the closed result type.")]
    public static StorageResult<T> Success(T value) => new(value);

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Result factories are intentionally discoverable on the closed result type.")]
    public static StorageResult<T> Fail(StorageFailure error) => new(error);
}

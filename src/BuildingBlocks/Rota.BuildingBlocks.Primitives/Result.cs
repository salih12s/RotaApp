using System.Diagnostics.CodeAnalysis;

namespace Rota.BuildingBlocks.Primitives;

/// <summary>
/// Outcome of an operation that can fail for an expected business reason.
/// Expected failures are returned, not thrown; exceptions are reserved for bugs and outages.
/// </summary>
public class Result
{
    private static readonly Result _success = new(null);

    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public static Result Success() => _success;

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value)
        : base(null) => _value = value;

    internal Result(Error error)
        : base(error) { }

    /// <summary>The value of a successful result. Reading it on a failure is a bug and throws.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result failed with '{Error.Code}'; check IsSuccess before reading Value.");

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => Failure<T>(error);
}

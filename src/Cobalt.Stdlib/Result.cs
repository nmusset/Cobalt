namespace Cobalt.Stdlib;

/// <summary>
/// Represents either a successful value Ok(T) or an error Err(E).
/// This is the Cobalt runtime type for Result&lt;T, E&gt;. No exceptions needed for expected failures.
/// </summary>
public abstract class Result<T, E>
{
    private Result() { }

    /// <summary>Creates an Ok variant holding <paramref name="value"/>.</summary>
    public static Result<T, E> Ok(T value) => new OkCase(value);

    /// <summary>Creates an Err variant holding <paramref name="error"/>.</summary>
    public static Result<T, E> Err(E error) => new ErrCase(error);

    /// <summary>Returns true when this is Ok.</summary>
    public bool IsOk => this is OkCase;

    /// <summary>Returns true when this is Err.</summary>
    public bool IsErr => this is ErrCase;

    /// <summary>
    /// Returns the contained Ok value.
    /// Throws <see cref="InvalidOperationException"/> if this is Err.
    /// </summary>
    public T Unwrap() =>
        this is OkCase ok ? ok.Value : throw new InvalidOperationException($"Called Unwrap on Err: {((ErrCase)this).Error}");

    /// <summary>Returns the contained Ok value, or <paramref name="defaultValue"/> if Err.</summary>
    public T UnwrapOr(T defaultValue) =>
        this is OkCase ok ? ok.Value : defaultValue;

    /// <summary>
    /// Returns the contained Err value.
    /// Throws <see cref="InvalidOperationException"/> if this is Ok.
    /// </summary>
    public E UnwrapErr() =>
        this is ErrCase err ? err.Error : throw new InvalidOperationException("Called UnwrapErr on Ok");

    /// <summary>Maps the Ok value using <paramref name="mapper"/>, propagating Err unchanged.</summary>
    public Result<U, E> Map<U>(Func<T, U> mapper) =>
        this is OkCase ok ? Result<U, E>.Ok(mapper(ok.Value)) : Result<U, E>.Err(((ErrCase)this).Error);

    /// <summary>Maps the Err value using <paramref name="mapper"/>, propagating Ok unchanged.</summary>
    public Result<T, F> MapErr<F>(Func<E, F> mapper) =>
        this is ErrCase err ? Result<T, F>.Err(mapper(err.Error)) : Result<T, F>.Ok(((OkCase)this).Value);

    /// <summary>Chains Ok through <paramref name="binder"/>, propagating Err unchanged.</summary>
    public Result<U, E> AndThen<U>(Func<T, Result<U, E>> binder) =>
        this is OkCase ok ? binder(ok.Value) : Result<U, E>.Err(((ErrCase)this).Error);

    public override string ToString() =>
        this is OkCase ok ? $"Ok({ok.Value})" : $"Err({((ErrCase)this).Error})";

    // ──────────────────────────────────────────────
    // Variant classes
    // ──────────────────────────────────────────────

    public sealed class OkCase : Result<T, E>
    {
        public T Value { get; }
        internal OkCase(T value) => Value = value;
    }

    public sealed class ErrCase : Result<T, E>
    {
        public E Error { get; }
        internal ErrCase(E error) => Error = error;
    }
}

/// <summary>
/// Static factory for Result — enables unqualified Ok(x) / Err(e) usage.
/// </summary>
public static class Result
{
    public static Result<T, E> Ok<T, E>(T value) => Result<T, E>.Ok(value);
    public static Result<T, E> Err<T, E>(E error) => Result<T, E>.Err(error);
}

namespace Cobalt.Stdlib;

/// <summary>
/// Represents an optional value — either Some(value) or None.
/// This is the Cobalt runtime type for Option&lt;T&gt;. Cobalt source uses it as a first-class type.
/// .NET nullable strings and references crossing the interop boundary are automatically wrapped.
/// </summary>
public abstract class Option<T>
{
    private Option() { }

    /// <summary>Creates a Some variant holding <paramref name="value"/>.</summary>
    public static Option<T> Some(T value) => new SomeCase(value);

    /// <summary>The None singleton for this type parameter.</summary>
    public static Option<T> None { get; } = new NoneCase();

    /// <summary>Returns true when this is Some.</summary>
    public bool IsSome => this is SomeCase;

    /// <summary>Returns true when this is None.</summary>
    public bool IsNone => this is NoneCase;

    /// <summary>
    /// Returns the contained value.
    /// Throws <see cref="InvalidOperationException"/> if this is None.
    /// </summary>
    public T Unwrap() =>
        this is SomeCase s ? s.Value : throw new InvalidOperationException("Called Unwrap on None");

    /// <summary>Returns the contained value, or <paramref name="defaultValue"/> if None.</summary>
    public T UnwrapOr(T defaultValue) =>
        this is SomeCase s ? s.Value : defaultValue;

    /// <summary>Maps the contained value using <paramref name="mapper"/>, or returns None.</summary>
    public Option<U> Map<U>(Func<T, U> mapper) =>
        this is SomeCase s ? Option<U>.Some(mapper(s.Value)) : Option<U>.None;

    /// <summary>Returns the result of <paramref name="binder"/> if Some, otherwise None.</summary>
    public Option<U> AndThen<U>(Func<T, Option<U>> binder) =>
        this is SomeCase s ? binder(s.Value) : Option<U>.None;

    public override string ToString() =>
        this is SomeCase s ? $"Some({s.Value})" : "None";

    // ──────────────────────────────────────────────
    // Variant classes
    // ──────────────────────────────────────────────

    public sealed class SomeCase : Option<T>
    {
        public T Value { get; }
        internal SomeCase(T value) => Value = value;
    }

    public sealed class NoneCase : Option<T>
    {
        internal NoneCase() { }
    }
}

/// <summary>
/// Static factory for Option — enables unqualified Some(x) and None usage in Cobalt code
/// that the compiler rewrites to these calls.
/// </summary>
public static class Option
{
    public static Option<T> Some<T>(T value) => Option<T>.Some(value);

    /// <summary>Wraps a nullable reference into Option&lt;T&gt; — used by the .NET interop boundary.</summary>
    public static Option<T> FromNullable<T>(T? value) where T : class =>
        value is not null ? Option<T>.Some(value) : Option<T>.None;

    /// <summary>Wraps a nullable value type into Option&lt;T&gt;.</summary>
    public static Option<T> FromNullable<T>(T? value) where T : struct =>
        value.HasValue ? Option<T>.Some(value.Value) : Option<T>.None;
}

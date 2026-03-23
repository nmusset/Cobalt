namespace Cobalt.Annotations;

/// <summary>
/// Indicates that the value is exclusively borrowed for mutation.
/// <para>
/// The caller retains ownership but must not access the value (read or write)
/// while the callee holds the exclusive borrow. The callee may mutate the value
/// but must not store it beyond the call's scope or transfer ownership.
/// </para>
/// <para>
/// Only one <see cref="MutBorrowedAttribute"/> reference may exist at a time
/// (exclusive borrowing — aliasing XOR mutability).
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Parameter,
    Inherited = true,
    AllowMultiple = false)]
public sealed class MutBorrowedAttribute : Attribute;

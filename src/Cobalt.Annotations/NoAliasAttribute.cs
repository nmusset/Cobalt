namespace Cobalt.Annotations;

/// <summary>
/// Indicates that the annotated parameter or field has no other aliases.
/// <para>
/// On a parameter: the caller guarantees that no other live reference points to
/// the same object. The callee may assume exclusive access for the duration of
/// the call.
/// </para>
/// <para>
/// On a field: the containing type guarantees that no other reference to this
/// object is held elsewhere. This enables the analyzer to reason about mutation
/// without worrying about concurrent modification through an alias.
/// </para>
/// <para>
/// This is weaker than <see cref="MutBorrowedAttribute"/> (which is enforced by
/// the borrow checker); <see cref="NoAliasAttribute"/> is a caller-side promise
/// that the analyzer can use for diagnostics but cannot fully verify.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.Property,
    Inherited = true,
    AllowMultiple = false)]
public sealed class NoAliasAttribute : Attribute;

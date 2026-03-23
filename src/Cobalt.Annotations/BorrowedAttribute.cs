namespace Cobalt.Annotations;

/// <summary>
/// Indicates that the value is borrowed as a shared (read-only) reference.
/// <para>
/// The caller retains ownership. The callee may read the value but must not
/// store it beyond the call's scope, mutate it, or transfer ownership.
/// </para>
/// <para>
/// Multiple <see cref="BorrowedAttribute"/> parameters may reference the same
/// value simultaneously (shared borrowing).
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Parameter,
    Inherited = true,
    AllowMultiple = false)]
public sealed class BorrowedAttribute : Attribute;

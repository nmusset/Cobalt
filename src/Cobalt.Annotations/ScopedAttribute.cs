namespace Cobalt.Annotations;

/// <summary>
/// Indicates that the reference is confined to the current scope and must not escape.
/// <para>
/// On a parameter: the callee must not store the reference in a field, return it,
/// or pass it to another method that could store it. The reference is only valid
/// for the duration of the call.
/// </para>
/// <para>
/// On a field: the field's value must not be exposed beyond the containing type's
/// methods (it is an implementation detail, not a shared reference).
/// </para>
/// <para>
/// This is the attribute-based equivalent of C#'s <c>scoped</c> keyword, extended
/// to reference types.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.Property,
    Inherited = true,
    AllowMultiple = false)]
public sealed class ScopedAttribute : Attribute;

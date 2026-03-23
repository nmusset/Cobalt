namespace Cobalt.Annotations;

/// <summary>
/// Indicates that ownership of the value is transferred.
/// <para>
/// On a parameter: the caller gives up ownership — the callee is now responsible
/// for the value's lifetime (including disposal if applicable). The caller must not
/// use the value after passing it.
/// </para>
/// <para>
/// On a return value: the caller receives ownership and is responsible for the
/// value's lifetime.
/// </para>
/// <para>
/// On a field or property: the containing type owns the value and is responsible
/// for its lifetime.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Parameter | AttributeTargets.ReturnValue |
    AttributeTargets.Field | AttributeTargets.Property,
    Inherited = true,
    AllowMultiple = false)]
public sealed class OwnedAttribute : Attribute;

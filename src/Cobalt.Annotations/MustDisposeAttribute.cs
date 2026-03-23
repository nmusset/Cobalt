namespace Cobalt.Annotations;

/// <summary>
/// Indicates that instances of the annotated type must be disposed by their owner.
/// <para>
/// On a type: all instances must be disposed. The analyzer will warn if an instance
/// is created but not disposed on any control flow path.
/// </para>
/// <para>
/// On a return value: the caller receives a value that must be disposed. Forgetting
/// to dispose it is a diagnostic.
/// </para>
/// <para>
/// This is stricter than <see cref="System.IDisposable"/> alone — IDisposable makes
/// disposal possible; <see cref="MustDisposeAttribute"/> makes it required.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.ReturnValue,
    Inherited = true,
    AllowMultiple = false)]
public sealed class MustDisposeAttribute : Attribute;

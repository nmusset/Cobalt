namespace Cobalt.Annotations;

/// <summary>
/// Indicates that instances of the annotated type are safe to share across threads.
/// <para>
/// This is the .NET equivalent of Rust's <c>Sync</c> marker trait. A type is
/// <c>[Sync]</c> if shared references to it can be safely accessed from multiple
/// threads concurrently.
/// </para>
/// <para>
/// This attribute is advisory — the analyzer uses it for diagnostics but the
/// runtime does not enforce it.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct,
    Inherited = true,
    AllowMultiple = false)]
public sealed class SyncAttribute : Attribute;

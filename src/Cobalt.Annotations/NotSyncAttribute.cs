namespace Cobalt.Annotations;

/// <summary>
/// Indicates that instances of the annotated type are NOT safe to share across threads.
/// <para>
/// This is the .NET equivalent of Rust's <c>!Sync</c>. A type is <c>[NotSync]</c>
/// if concurrent access from multiple threads can cause data races, corruption,
/// or undefined behavior.
/// </para>
/// <para>
/// The analyzer will warn when a <c>[NotSync]</c> value is captured by a lambda
/// passed to <c>Task.Run</c>, <c>Parallel.ForEach</c>, or similar concurrency APIs.
/// </para>
/// <para>
/// Examples of types that should be <c>[NotSync]</c>: non-thread-safe caches,
/// mutable state holders without internal synchronization, types wrapping
/// single-threaded native resources.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct,
    Inherited = true,
    AllowMultiple = false)]
public sealed class NotSyncAttribute : Attribute;

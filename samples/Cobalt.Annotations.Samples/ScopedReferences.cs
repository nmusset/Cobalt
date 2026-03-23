using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates [Scoped] for references that must not escape their scope.
/// </summary>
public static class ScopedReferences
{
    // --- Scoped parameter: must not be stored or returned ---

    /// <summary>
    /// Processes the buffer but must not store or return a reference to it.
    /// The caller guarantees the buffer is only valid for this call.
    /// </summary>
    public static int ProcessBuffer([Scoped] byte[] buffer)
    {
        // Can read and use the buffer within this method.
        var sum = 0;
        foreach (var b in buffer)
        {
            sum += b;
        }

        return sum;
        // Cannot store 'buffer' in a field or return it.
    }

    // --- Scoped field: an implementation detail that must not leak ---

    public sealed class ObjectPool<T> where T : class, new()
    {
        [Scoped]
        private readonly Queue<T> _pool = new();

        /// <summary>
        /// The internal pool is scoped — callers should not get a direct
        /// reference to it. Items are rented and returned individually.
        /// </summary>
        public T Rent()
        {
            return _pool.Count > 0 ? _pool.Dequeue() : new T();
        }

        public void Return([Owned] T item)
        {
            _pool.Enqueue(item);
        }
    }

    // --- Scoped callback pattern: reference valid only during callback ---

    /// <summary>
    /// The callback receives a scoped reference to the data. It must not
    /// store the reference beyond the callback's execution.
    /// </summary>
    public static void WithTemporaryBuffer(int size, Action<byte[]> callback)
    {
        var buffer = new byte[size];
        callback(buffer);
        // buffer may be reused or reclaimed after callback returns.
    }

    // --- Bug: escaping a scoped reference ---

#pragma warning disable CS0169 // Intentionally unused — bug example is commented out
    private static byte[]? _leaked;
#pragma warning restore CS0169

    public static void ScopedEscapeExample()
    {
        WithTemporaryBuffer(1024, buffer =>
        {
            // BUG: storing a scoped reference — the analyzer should flag this.
            // _leaked = buffer;
        });
    }
}

using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates [NoAlias] for parameters and fields with no other references.
/// </summary>
public static class NoAliasPatterns
{
    // --- NoAlias parameter: caller promises exclusive access ---

    /// <summary>
    /// Sorts the array in place. The caller promises that no other reference
    /// to this array exists, so the sort is safe from concurrent modification.
    /// </summary>
    public static void SortExclusive([NoAlias] int[] data)
    {
        Array.Sort(data);
    }

    /// <summary>
    /// Swaps the contents of two arrays. Both must be unaliased to avoid
    /// the case where both parameters point to the same array.
    /// </summary>
    public static void SwapContents([NoAlias] int[] a, [NoAlias] int[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Arrays must have the same length.");

        for (var i = 0; i < a.Length; i++)
        {
            (a[i], b[i]) = (b[i], a[i]);
        }
    }

    // --- NoAlias field: the type owns the sole reference ---

    public sealed class ExclusiveBuffer
    {
        [NoAlias]
        private byte[] _buffer;

        public ExclusiveBuffer(int size)
        {
            _buffer = new byte[size];
        }

        /// <summary>
        /// Safe to mutate freely — no other reference to _buffer exists.
        /// </summary>
        public void Fill(byte value)
        {
            Array.Fill(_buffer, value);
        }

        /// <summary>
        /// Resizing is safe because we hold the only reference.
        /// </summary>
        public void Resize(int newSize)
        {
            Array.Resize(ref _buffer, newSize);
        }
    }

    // --- Bug: aliasing a NoAlias parameter ---

    public static void AliasingExample()
    {
        var data = new int[] { 3, 1, 2 };

        // BUG: passing the same array as both [NoAlias] parameters.
        // The analyzer should flag this.
        // SwapContents(data, data);
    }
}

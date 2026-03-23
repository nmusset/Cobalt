using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates [Borrowed] and [MutBorrowed] for shared and exclusive borrowing.
/// </summary>
public static class BorrowingPatterns
{
    // --- Shared borrowing: multiple readers allowed ---

    /// <summary>
    /// Borrows the list for reading. The caller retains ownership.
    /// Multiple shared borrows can coexist.
    /// </summary>
    public static int SumValues([Borrowed] List<int> values)
    {
        // Can read, but should not store the reference or modify the list.
        return values.Sum();
    }

    /// <summary>
    /// Another shared borrow — can exist simultaneously with other shared borrows.
    /// </summary>
    public static bool ContainsNegative([Borrowed] List<int> values)
    {
        return values.Any(v => v < 0);
    }

    // --- Exclusive borrowing: single writer, no readers ---

    /// <summary>
    /// Exclusively borrows the list for mutation. No other references
    /// (shared or exclusive) may exist simultaneously.
    /// </summary>
    public static void AppendDefaults([MutBorrowed] List<int> values, int count)
    {
        for (var i = 0; i < count; i++)
        {
            values.Add(0);
        }
    }

    /// <summary>
    /// Exclusively borrows for in-place transformation.
    /// </summary>
    public static void DoubleAll([MutBorrowed] List<int> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            values[i] *= 2;
        }
    }

    // --- Valid usage: shared borrows don't conflict with each other ---

    public static void SharedBorrowsCoexist()
    {
        var data = new List<int> { 1, 2, 3, -4, 5 };

        // Both are shared borrows — this is fine.
        var sum = SumValues(data);
        var hasNeg = ContainsNegative(data);
    }

    // --- Invalid usage: the analyzer should catch this ---

    public static void ConflictingBorrows()
    {
        var data = new List<int> { 1, 2, 3 };

        // BUG: Can't have a shared borrow and exclusive borrow simultaneously.
        // In real code this could cause a modification during enumeration.
        // Not yet detectable — requires [Borrowed]/[MutBorrowed] conflict analysis (future).
        // var sum = SumValues(data);
        // AppendDefaults(data, 2);
    }
}

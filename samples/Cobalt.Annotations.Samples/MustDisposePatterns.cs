using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates [MustDispose] for types that require disposal.
/// </summary>
public static class MustDisposePatterns
{
    // --- A type that MUST be disposed ---

    /// <summary>
    /// A database connection that must always be disposed.
    /// Forgetting to dispose is a diagnostic, unlike plain IDisposable
    /// which only makes disposal possible but not required.
    /// </summary>
    [MustDispose]
    public sealed class DatabaseConnection : IDisposable
    {
        public string ConnectionString { get; }

        public DatabaseConnection(string connectionString)
        {
            ConnectionString = connectionString;
        }

        public string Query(string sql) => $"Result of: {sql}";

        public void Dispose()
        {
            // Close connection, release resources.
        }
    }

    // --- Factory returning a MustDispose type ---

    /// <summary>
    /// The return value must be disposed by the caller because
    /// DatabaseConnection is marked [MustDispose].
    /// </summary>
    public static DatabaseConnection Connect(string connectionString)
    {
        return new DatabaseConnection(connectionString);
    }

    // --- Correct usage: disposed via using ---

    public static void CorrectUsage()
    {
        using var conn = Connect("Server=localhost;Database=test");
        _ = conn.Query("SELECT 1");
        // conn is disposed at end of scope — correct.
    }

    // --- Correct usage: ownership transferred ---

    public static void TransferOwnership()
    {
        var conn = Connect("Server=localhost;Database=test");
        TakeOwnership(conn);
        // conn was moved — disposal is now TakeOwnership's responsibility.
    }

    private static void TakeOwnership([Owned] DatabaseConnection conn)
    {
        using (conn)
        {
            _ = conn.Query("SELECT 1");
        }
    }

    // --- Bug: not disposed, not transferred ---

    public static void ForgotToDispose()
    {
        var conn = Connect("Server=localhost;Database=test");
        _ = conn.Query("SELECT 1");
        // BUG: conn is never disposed and ownership was never transferred.
        // The analyzer should flag this.
    }

    // --- [MustDispose] on return value for non-annotated types ---

    /// <summary>
    /// Even though Stream is not marked [MustDispose] at the type level,
    /// this specific factory method's return value must be disposed.
    /// </summary>
    [return: MustDispose]
    public static Stream OpenTempFile()
    {
        return File.Create(Path.GetTempFileName());
    }
}

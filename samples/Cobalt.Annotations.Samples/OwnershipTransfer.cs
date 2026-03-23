using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates [Owned] for transferring ownership of values.
/// The analyzer (Phase A.2/A.3) will enforce these contracts.
/// </summary>
public static class OwnershipTransfer
{
    // --- Owned parameters: caller gives up access after the call ---

    /// <summary>
    /// Takes ownership of the stream. The caller must not use it after this call.
    /// </summary>
    public static void ConsumeStream([Owned] Stream stream)
    {
        using (stream)
        {
            // Process the stream...
            var buffer = new byte[1024];
            _ = stream.Read(buffer, 0, buffer.Length);
        }
        // stream is disposed here — the callee is responsible.
    }

    /// <summary>
    /// Takes ownership of the connection. Will be stored and disposed later.
    /// </summary>
    public static void RegisterConnection([Owned] IDisposable connection)
    {
        // In a real scenario, this would store the connection.
        // The callee now owns it.
        connection.Dispose();
    }

    // --- Owned return values: caller receives ownership ---

    /// <summary>
    /// Creates and returns an owned stream. Caller is responsible for disposal.
    /// </summary>
    [return: Owned]
    public static MemoryStream CreateBuffer()
    {
        var stream = new MemoryStream();
        stream.Write([1, 2, 3, 4]);
        return stream; // Ownership transfers to the caller.
    }

    // --- Owned fields: the containing type owns the value ---

    public sealed class ResourceHolder : IDisposable
    {
        [Owned]
        private readonly Stream _stream;

        public ResourceHolder([Owned] Stream stream)
        {
            _stream = stream; // Ownership transferred from parameter to field.
        }

        public void Dispose()
        {
            _stream.Dispose(); // Owner is responsible for cleanup.
        }
    }

    // --- Use-after-move: the analyzer catches this (CB0002) ---

    public static void UseAfterMoveExample()
    {
        var stream = CreateBuffer();

        // Ownership transferred to ConsumeStream.
        ConsumeStream(stream);

        // BUG: stream has been moved — CB0002 fires here.
        stream.Read(new byte[1], 0, 1);
    }
}

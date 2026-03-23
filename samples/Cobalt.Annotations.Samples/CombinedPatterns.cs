using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates combining multiple annotations in realistic scenarios.
/// </summary>
public static class CombinedPatterns
{
    // --- Factory + ownership + must-dispose ---

    [MustDispose]
    public sealed class FileProcessor : IDisposable
    {
        [Owned]
        private readonly Stream _input;

        [Owned]
        private readonly Stream _output;

        /// <summary>
        /// Takes ownership of both streams. The processor is responsible for
        /// disposing them.
        /// </summary>
        public FileProcessor([Owned] Stream input, [Owned] Stream output)
        {
            _input = input;
            _output = output;
        }

        /// <summary>
        /// Borrows a configuration for the duration of the call.
        /// Does not store or take ownership of it.
        /// </summary>
        public void Process([Borrowed] ProcessorConfig config)
        {
            var buffer = new byte[config.BufferSize];
            int bytesRead;
            while ((bytesRead = _input.Read(buffer, 0, buffer.Length)) > 0)
            {
                _output.Write(buffer, 0, bytesRead);
            }
        }

        public void Dispose()
        {
            _input.Dispose();
            _output.Dispose();
        }
    }

    public sealed class ProcessorConfig
    {
        public int BufferSize { get; init; } = 4096;
    }

    /// <summary>
    /// Correct usage: ownership flows clearly through the call chain.
    /// </summary>
    public static void CorrectPipeline()
    {
        var input = File.OpenRead("input.txt");   // Caller owns input.
        var output = File.Create("output.txt");    // Caller owns output.

        // Ownership of both streams transferred to FileProcessor.
        using var processor = new FileProcessor(input, output);

        // input and output are moved — caller must not use them.

        var config = new ProcessorConfig { BufferSize = 8192 };
        processor.Process(config); // config is borrowed — caller retains it.
    }

    // --- Scoped + borrowed: temporary access patterns ---

    public sealed class ConnectionPool
    {
        private readonly List<IDisposable> _connections = [];

        /// <summary>
        /// Adds a connection. Pool takes ownership.
        /// </summary>
        public void Add([Owned] IDisposable connection)
        {
            _connections.Add(connection);
        }

        /// <summary>
        /// Provides scoped, read-only access to the pool's connections.
        /// The caller must not store the returned list or modify it.
        /// </summary>
        public void ForEach(Action<IDisposable> action)
        {
            foreach (var conn in _connections)
            {
                action(conn);
            }
        }
    }

    // --- Ownership transfer chain ---

    /// <summary>
    /// Creates a resource, passes ownership through a chain, and ensures
    /// disposal at the end. Each step transfers ownership explicitly.
    /// </summary>
    public static void OwnershipChain()
    {
        var stream = new MemoryStream();   // We own it.
        var wrapped = WrapStream(stream);  // Ownership transferred to wrapper.
        UseAndDispose(wrapped);            // Ownership transferred to consumer.
        // Both stream and wrapped are moved — do not use.
    }

    [return: Owned]
    private static BufferedStream WrapStream([Owned] Stream inner)
    {
        return new BufferedStream(inner, 4096);
    }

    private static void UseAndDispose([Owned] IDisposable resource)
    {
        using (resource)
        {
            // Use the resource...
        }
    }
}

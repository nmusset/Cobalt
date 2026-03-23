using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates [Sync] / [NotSync] thread-safety annotations (Phase A.4).
/// The analyzer warns when [NotSync] values are captured by lambdas passed
/// to concurrency APIs like Task.Run or Parallel.ForEach.
/// </summary>
public static class ThreadSafetyPatterns
{
    // --- A type that is NOT thread-safe ---

    [NotSync]
    public sealed class RequestCache
    {
        private readonly Dictionary<string, string> _cache = [];

        public void Add(string key, string value) => _cache[key] = value;
        public string? Get(string key) => _cache.GetValueOrDefault(key);
    }

    // --- A type that IS thread-safe ---

    [Sync]
    public sealed class AtomicCounter
    {
        private int _value;

        public void Increment() => Interlocked.Increment(ref _value);
        public int Value => Volatile.Read(ref _value);
    }

    // --- BUG: [NotSync] value captured by Task.Run — CB0007 fires ---

    public static void UnsafeConcurrentAccess()
    {
        var cache = new RequestCache();
        cache.Add("init", "value");

        // CB0007: cache is [NotSync] but captured by Task.Run lambda.
        // Dictionary is not thread-safe — concurrent Add/Get can corrupt state.
        Task.Run(() =>
        {
            cache.Add("key1", "from-background");
        });

        // Main thread also accesses cache — data race.
        _ = cache.Get("key1");
    }

    // --- BUG: [NotSync] value captured by Parallel.ForEach — CB0007 fires ---

    public static void UnsafeParallelAccumulation()
    {
        var cache = new RequestCache();
        var items = new[] { "a", "b", "c", "d" };

        // CB0007: cache is [NotSync] — Parallel.ForEach runs iterations
        // on multiple threads, causing concurrent mutations.
        Parallel.ForEach(items, item =>
        {
            cache.Add(item, $"value-{item}");
        });
    }

    // --- Correct: [Sync] value captured by Task.Run — no warning ---

    public static void SafeConcurrentAccess()
    {
        var counter = new AtomicCounter();

        // No CB0007 — AtomicCounter is [Sync], safe to share.
        Task.Run(() =>
        {
            counter.Increment();
        });

        Task.Run(() =>
        {
            counter.Increment();
        });

        Thread.Sleep(100);
        _ = counter.Value;
    }

    // --- Correct: [NotSync] used on single thread — no warning ---

    public static void SingleThreadedUsage()
    {
        var cache = new RequestCache();

        // No warning — no concurrency API involved.
        cache.Add("a", "1");
        cache.Add("b", "2");
        _ = cache.Get("a");
    }
}

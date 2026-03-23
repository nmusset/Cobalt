using Cobalt.Annotations;

namespace Cobalt.Annotations.Samples;

/// <summary>
/// Demonstrates move-semantic discipline (Phase A.3).
/// The analyzer treats assignment of owned values as moves and warns
/// about aliasing, use-after-move, and implicit sharing.
/// </summary>
public static class MoveSemantics
{
    [MustDispose]
    public sealed class Handle : IDisposable
    {
        public string Name { get; }
        public Handle(string name) => Name = name;
        public void Use() { }
        public void Dispose() { }
    }

    // --- CB0004: Aliasing an owned value ---

    public static void AliasingExample()
    {
        var a = new Handle("resource");

        // CB0004: assigning an owned value to another variable creates an alias.
        // The original (a) is now considered moved.
#pragma warning disable CB0004
        var b = a;
#pragma warning restore CB0004

        // a is now moved — using it would trigger CB0002.
        // a.Use(); // BUG: use after move

        b.Dispose(); // b is the new owner.
    }

    // --- Correct pattern: transfer via [Owned] parameter ---

    public static void ExplicitTransfer()
    {
        var h = new Handle("resource");
        Consume(h);
        // h is moved — do not use.
    }

    private static void Consume([Owned] Handle h)
    {
        h.Use();
        h.Dispose();
    }

    // --- CB0005: Passing owned value to unannotated parameter ---

    public static void ImplicitShareExample()
    {
        var h = new Handle("resource");

        // CB0005 (Info): h is passed to a parameter without ownership annotation.
        // The callee might store or alias h without our knowledge.
#pragma warning disable CB0005
        LogHandle(h);
#pragma warning restore CB0005

        h.Dispose();
    }

    private static void LogHandle(object obj)
    {
        // Could store obj, creating an alias — the analyzer can't tell.
    }

    // --- Correct pattern: use [Borrowed] for read-only access ---

    public static void BorrowedAccess()
    {
        var h = new Handle("resource");

        // No CB0005 — the parameter is annotated [Borrowed],
        // so the analyzer knows h won't be stored or transferred.
        InspectHandle(h);

        h.Dispose();
    }

    private static void InspectHandle([Borrowed] Handle h)
    {
        _ = h.Name;
    }
}

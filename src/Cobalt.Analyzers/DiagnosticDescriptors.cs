using Microsoft.CodeAnalysis;

namespace Cobalt.Analyzers;

internal static class DiagnosticDescriptors
{
    private const string Category = "Cobalt.Ownership";

    /// <summary>
    /// CB0001: An owned disposable value is not disposed or transferred by end of scope.
    /// </summary>
    public static readonly DiagnosticDescriptor OwnedNotDisposed = new(
        id: "CB0001",
        title: "Owned disposable value is not disposed",
        messageFormat: "Owned value '{0}' is not disposed or transferred on all control flow paths",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Values marked [Owned] that implement IDisposable, or instances of [MustDispose] types, " +
                     "must be disposed via 'using' or Dispose(), or transferred to another owner via an [Owned] parameter.");

    /// <summary>
    /// CB0002: A value is used after its ownership was transferred (use-after-move).
    /// </summary>
    public static readonly DiagnosticDescriptor UseAfterMove = new(
        id: "CB0002",
        title: "Use of value after ownership transfer",
        messageFormat: "Value '{0}' is used after its ownership was transferred",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Once a value is passed to a parameter marked [Owned], ownership is transferred " +
                     "and the original variable must not be used again.");

    /// <summary>
    /// CB0003: A value is used after being disposed.
    /// </summary>
    public static readonly DiagnosticDescriptor UseAfterDispose = new(
        id: "CB0003",
        title: "Use of value after disposal",
        messageFormat: "Value '{0}' is used after being disposed",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A value that has been disposed must not be used again. " +
                     "Use a 'using' declaration or statement to ensure correct scoping.");

    /// <summary>
    /// CB0004: An owned value is assigned to another local, creating an alias. The source is considered moved.
    /// </summary>
    public static readonly DiagnosticDescriptor OwnedValueAliased = new(
        id: "CB0004",
        title: "Owned value aliased by assignment",
        messageFormat: "Owned value '{0}' is aliased by assignment to '{1}'; the original is now moved",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Assigning an owned value to another variable creates a second reference. " +
                     "The original variable is considered moved and must not be used again. " +
                     "Use .Clone() if both copies are needed.");

    /// <summary>
    /// CB0005: An owned value is passed to a parameter without a Cobalt ownership annotation.
    /// </summary>
    public static readonly DiagnosticDescriptor OwnedValueImplicitlyShared = new(
        id: "CB0005",
        title: "Owned value passed without ownership annotation",
        messageFormat: "Owned value '{0}' is passed to unannotated parameter '{1}' — ownership semantics are unclear",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Passing an owned value to a parameter that lacks [Owned], [Borrowed], or [MutBorrowed] " +
                     "means the callee might store or alias the value without the caller's knowledge. " +
                     "Consider adding an ownership annotation to the target parameter.");

    /// <summary>
    /// CB0006: A using-declared variable is passed to an [Owned] parameter, creating ambiguous ownership.
    /// </summary>
    public static readonly DiagnosticDescriptor UsingVariableOwnershipTransfer = new(
        id: "CB0006",
        title: "Using-declared variable has ownership transferred",
        messageFormat: "Variable '{0}' is declared with 'using' but ownership is transferred — ownership intent is ambiguous",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A variable declared with 'using' will be disposed at end of scope, " +
                     "but passing it to an [Owned] parameter transfers disposal responsibility to the callee. " +
                     "Both the 'using' and the callee will dispose the value. While IDisposable.Dispose() " +
                     "should be idempotent, the contradictory ownership intent is a code smell. " +
                     "Consider removing 'using' if the callee is responsible for disposal.");
}

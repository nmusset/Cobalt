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
}

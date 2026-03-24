namespace Cobalt.Compiler.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed record Diagnostic(
    string Id,
    string Message,
    DiagnosticSeverity Severity,
    SourceSpan Span)
{
    public override string ToString() =>
        $"{Span.Start}: {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

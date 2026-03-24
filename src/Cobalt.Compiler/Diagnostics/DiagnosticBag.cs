namespace Cobalt.Compiler.Diagnostics;

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> All => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Report(string id, string message, DiagnosticSeverity severity, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(id, message, severity, span));
    }

    public void Error(string id, string message, SourceSpan span) =>
        Report(id, message, DiagnosticSeverity.Error, span);

    public void Warning(string id, string message, SourceSpan span) =>
        Report(id, message, DiagnosticSeverity.Warning, span);

    public void AddRange(DiagnosticBag other) => _diagnostics.AddRange(other._diagnostics);
}

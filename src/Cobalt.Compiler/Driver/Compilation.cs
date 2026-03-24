namespace Cobalt.Compiler.Driver;

using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Dump;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;

/// <summary>
/// Orchestrates the compiler pipeline: lex → parse → type-check → borrow-check.
/// Each stage's output is available for inspection (dump modes).
/// </summary>
public sealed class Compilation
{
    public string SourceText { get; }
    public string FileName { get; }
    public DiagnosticBag Diagnostics { get; } = new();

    // Pipeline results — populated lazily by Run*() methods
    public List<Token>? Tokens { get; private set; }
    public CompilationUnit? Ast { get; private set; }
    public Scope? GlobalScope { get; private set; }

    public Compilation(string sourceText, string fileName)
    {
        SourceText = sourceText;
        FileName = fileName;
    }

    /// <summary>Run the full pipeline up to and including borrow checking.</summary>
    public void RunAll()
    {
        RunLex();
        if (Diagnostics.HasErrors) return;
        RunParse();
        if (Diagnostics.HasErrors) return;
        RunTypeCheck();
        RunBorrowCheck();
    }

    public void RunLex()
    {
        var lexer = new Lexer(SourceText, FileName);
        Tokens = lexer.Lex();
        Diagnostics.AddRange(lexer.Diagnostics);
    }

    public void RunParse()
    {
        if (Tokens == null) RunLex();
        var parser = new Parser(Tokens!, Diagnostics);
        Ast = parser.ParseCompilationUnit();
    }

    public void RunTypeCheck()
    {
        if (Ast == null) RunParse();
        var checker = new TypeChecker(Diagnostics);
        GlobalScope = checker.Check(Ast!);
    }

    public void RunBorrowCheck()
    {
        if (GlobalScope == null) RunTypeCheck();
        var borrowChecker = new BorrowChecker(Diagnostics, GlobalScope!);
        borrowChecker.Check(Ast!);
    }

    // ──────────────────────────────────────────────
    // Dump helpers
    // ──────────────────────────────────────────────

    public string DumpAst()
    {
        if (Ast == null) RunParse();
        return new AstPrinter().Print(Ast!);
    }

    public string DumpSymbols()
    {
        if (GlobalScope == null) RunTypeCheck();
        return new SemanticDumper().Dump(GlobalScope!);
    }

    public string DumpDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var d in Diagnostics.All)
        {
            var severity = d.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info"
            };
            sb.AppendLine($"{FileName}({d.Span.Start.Line},{d.Span.Start.Column}): {severity} {d.Id}: {d.Message}");
        }
        return sb.ToString();
    }
}

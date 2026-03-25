namespace Cobalt.Compiler.Driver;

using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Dump;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;

/// <summary>
/// Orchestrates the compiler pipeline: lex → parse → type-check → borrow-check → emit.
/// Supports single-file (for dump modes) and multi-file compilation.
/// </summary>
public sealed class Compilation
{
    public DiagnosticBag Diagnostics { get; } = new();

    // Pipeline results — populated during compilation
    public CompilationUnit? Ast { get; private set; }
    public Scope? GlobalScope { get; private set; }
    public AssemblyDefinition? Assembly { get; private set; }

    // ──────────────────────────────────────────────
    // Multi-file compilation (full pipeline)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compile one or more .co source files into a .NET assembly.
    /// Returns true on success (assembly written to outputPath).
    /// </summary>
    public bool Compile(string[] sourceFiles, string outputPath, string? assemblyName = null)
    {
        assemblyName ??= Path.GetFileNameWithoutExtension(outputPath);

        // 1. Lex and parse each file
        var units = new List<CompilationUnit>();
        foreach (var file in sourceFiles)
        {
            var source = File.ReadAllText(file);
            var lexer = new Lexer(source, file);
            var tokens = lexer.Lex();
            Diagnostics.AddRange(lexer.Diagnostics);
            if (Diagnostics.HasErrors) return false;

            var parser = new Parser(tokens, Diagnostics);
            var unit = parser.ParseCompilationUnit();
            units.Add(unit);
            if (Diagnostics.HasErrors) return false;
        }

        // 2. Merge all compilation units into one
        Ast = MergeUnits(units);

        // 3. Type check
        var typeChecker = new TypeChecker(Diagnostics);
        GlobalScope = typeChecker.Check(Ast);

        // 4. Borrow check
        var borrowChecker = new BorrowChecker(Diagnostics, GlobalScope);
        borrowChecker.Check(Ast);

        // 5. Emit IL (proceed even with warnings, stop only on errors)
        if (Diagnostics.HasErrors) return false;

        var emitter = new ILEmitter(assemblyName, new Version(1, 0, 0, 0), GlobalScope);
        Assembly = emitter.Emit(Ast);

        // 6. Write assembly to disk
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        Assembly.Write(outputPath);

        return true;
    }

    // ──────────────────────────────────────────────
    // Single-file analysis (for dump modes)
    // ──────────────────────────────────────────────

    /// <summary>Run full analysis pipeline on a single source string.</summary>
    public void Analyze(string sourceText, string fileName)
    {
        var lexer = new Lexer(sourceText, fileName);
        var tokens = lexer.Lex();
        Diagnostics.AddRange(lexer.Diagnostics);
        if (Diagnostics.HasErrors) return;

        var parser = new Parser(tokens, Diagnostics);
        Ast = parser.ParseCompilationUnit();
        if (Diagnostics.HasErrors) return;

        var typeChecker = new TypeChecker(Diagnostics);
        GlobalScope = typeChecker.Check(Ast);

        var borrowChecker = new BorrowChecker(Diagnostics, GlobalScope);
        borrowChecker.Check(Ast);
    }

    // ──────────────────────────────────────────────
    // Dump helpers
    // ──────────────────────────────────────────────

    public string DumpAst()
    {
        if (Ast == null) return "";
        return new AstPrinter().Print(Ast);
    }

    public string DumpSymbols()
    {
        if (GlobalScope == null) return "";
        return new SemanticDumper().Dump(GlobalScope);
    }

    public string FormatDiagnostics()
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
            sb.AppendLine($"{d.Span.Start}: {severity} {d.Id}: {d.Message}");
        }
        return sb.ToString();
    }

    // ──────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────

    private static CompilationUnit MergeUnits(List<CompilationUnit> units)
    {
        if (units.Count == 1) return units[0];

        // Take namespace from first file that has one
        var ns = units.FirstOrDefault(u => u.Namespace != null)?.Namespace;

        // Merge all use directives (deduplicate by name)
        var uses = units
            .SelectMany(u => u.Uses)
            .DistinctBy(u => u.Name)
            .ToList();

        // Merge all members from all files
        var members = units
            .SelectMany(u => u.Members)
            .ToList();

        var span = units[0].Span;
        return new CompilationUnit(ns, uses, members, span);
    }
}

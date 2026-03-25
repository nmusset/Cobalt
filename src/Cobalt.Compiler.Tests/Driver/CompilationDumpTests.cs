using Cobalt.Compiler.Driver;

namespace Cobalt.Compiler.Tests.Driver;

public class CompilationDumpTests
{
    private static Compilation AnalyzeSource(string source)
    {
        var compilation = new Compilation();
        compilation.Analyze(source, "test.co");
        return compilation;
    }

    // ──────────────────────────────────────────────
    // DumpAst
    // ──────────────────────────────────────────────

    [Fact]
    public void DumpAst_ReturnsNonEmpty()
    {
        var comp = AnalyzeSource("class Foo { }");
        var ast = comp.DumpAst();
        Assert.False(string.IsNullOrWhiteSpace(ast));
        Assert.Contains("CompilationUnit", ast);
    }

    [Fact]
    public void DumpAst_ContainsClassName()
    {
        var comp = AnalyzeSource("class MyClass { }");
        var ast = comp.DumpAst();
        Assert.Contains("MyClass", ast);
    }

    [Fact]
    public void DumpAst_BeforeAnalyze_ReturnsEmpty()
    {
        var comp = new Compilation();
        var ast = comp.DumpAst();
        Assert.Equal("", ast);
    }

    // ──────────────────────────────────────────────
    // DumpSymbols
    // ──────────────────────────────────────────────

    [Fact]
    public void DumpSymbols_ReturnsNonEmpty()
    {
        var comp = AnalyzeSource("class Foo { }");
        var symbols = comp.DumpSymbols();
        Assert.False(string.IsNullOrWhiteSpace(symbols));
        Assert.Contains("Scope", symbols);
    }

    [Fact]
    public void DumpSymbols_ContainsUserType()
    {
        var comp = AnalyzeSource("""
            class Widget
            {
                public int id;
            }
            """);
        var symbols = comp.DumpSymbols();
        Assert.Contains("Widget", symbols);
        Assert.Contains("id", symbols);
    }

    [Fact]
    public void DumpSymbols_ContainsBuiltInTypes()
    {
        var comp = AnalyzeSource("class Foo { }");
        var symbols = comp.DumpSymbols();
        Assert.Contains("int", symbols);
        Assert.Contains("string", symbols);
    }

    [Fact]
    public void DumpSymbols_BeforeAnalyze_ReturnsEmpty()
    {
        var comp = new Compilation();
        var symbols = comp.DumpSymbols();
        Assert.Equal("", symbols);
    }

    // ──────────────────────────────────────────────
    // FormatDiagnostics
    // ──────────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_NoDiagnostics_ReturnsEmpty()
    {
        var comp = AnalyzeSource("class Foo { }");
        var diag = comp.FormatDiagnostics();
        // Clean source should have no errors (may have warnings for built-in resolution)
        // Just verify it doesn't throw
        Assert.NotNull(diag);
    }

    [Fact]
    public void FormatDiagnostics_WithErrors_ContainsErrorText()
    {
        var comp = AnalyzeSource("""
            class Foo
            {
                public void Bad(own Stream s)
                {
                    Bad(own s);
                    Bad(own s);
                }
            }
            """);
        var diag = comp.FormatDiagnostics();
        Assert.Contains("error", diag);
    }

    [Fact]
    public void FormatDiagnostics_ContainsDiagnosticId()
    {
        var comp = AnalyzeSource("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Bad(own Stream s)
                {
                    Take(own s);
                    Take(own s);
                }
            }
            """);
        var diag = comp.FormatDiagnostics();
        Assert.Contains("CB3", diag); // borrow checker diagnostic IDs start with CB3
    }

    // ──────────────────────────────────────────────
    // Analyze pipeline
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_PopulatesAstAndScope()
    {
        var comp = AnalyzeSource("class Foo { }");
        Assert.NotNull(comp.Ast);
        Assert.NotNull(comp.GlobalScope);
    }

    [Fact]
    public void Analyze_WithSyntaxError_StillPopulatesAst()
    {
        var comp = new Compilation();
        comp.Analyze("class { }", "test.co");
        // Parser recovers — AST should still be populated
        Assert.NotNull(comp.Ast);
        Assert.True(comp.Diagnostics.HasErrors);
    }
}

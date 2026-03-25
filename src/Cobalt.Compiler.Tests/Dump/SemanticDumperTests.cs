using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Dump;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;

namespace Cobalt.Compiler.Tests.Dump;

public class SemanticDumperTests
{
    private static string DumpSymbols(string source)
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        var unit = parser.ParseCompilationUnit();
        var checker = new TypeChecker(diagnostics);
        var scope = checker.Check(unit);
        return new SemanticDumper().Dump(scope);
    }

    [Fact]
    public void Dump_Class_ShowsClassName()
    {
        var output = DumpSymbols("public class Greeter { }");
        Assert.Contains("Greeter", output);
    }

    [Fact]
    public void Dump_ClassWithFields_ShowsFields()
    {
        var output = DumpSymbols("""
            class Foo
            {
                public int x;
                public string name;
            }
            """);
        Assert.Contains("field", output);
        Assert.Contains("x", output);
        Assert.Contains("name", output);
    }

    [Fact]
    public void Dump_ClassWithMethods_ShowsMethods()
    {
        var output = DumpSymbols("""
            class Foo
            {
                public int Add(int a, int b) { return a; }
            }
            """);
        Assert.Contains("method", output);
        Assert.Contains("Add", output);
    }

    [Fact]
    public void Dump_Trait_ShowsTrait()
    {
        var output = DumpSymbols("""
            trait Printable
            {
                public string AsString();
            }
            """);
        Assert.Contains("trait", output);
        Assert.Contains("Printable", output);
    }

    [Fact]
    public void Dump_Union_ShowsVariants()
    {
        var output = DumpSymbols("""
            union Shape
            {
                Circle(int Radius),
                Rectangle(int Width, int Height),
            }
            """);
        Assert.Contains("union", output);
        Assert.Contains("Shape", output);
        Assert.Contains("Circle", output);
        Assert.Contains("Rectangle", output);
    }

    [Fact]
    public void Dump_OwnershipOnField_ShowsOwn()
    {
        var output = DumpSymbols("""
            class Foo
            {
                own string name;
            }
            """);
        Assert.Contains("own", output);
    }

    [Fact]
    public void Dump_OwnershipOnParameter_ShowsRef()
    {
        var output = DumpSymbols("""
            class Foo
            {
                public void Read(ref string s) { return; }
            }
            """);
        Assert.Contains("ref", output);
    }

    [Fact]
    public void Dump_StaticMethod_ShowsStatic()
    {
        var output = DumpSymbols("""
            class Foo
            {
                public static int Add(int a, int b) { return a; }
            }
            """);
        Assert.Contains("static", output);
    }

    [Fact]
    public void Dump_BuiltInTypes_ShowsBuiltIns()
    {
        var output = DumpSymbols("class Foo { }");
        // Built-in types should always appear
        Assert.Contains("int", output);
        Assert.Contains("string", output);
        Assert.Contains("bool", output);
    }

    [Fact]
    public void Dump_ScopeHeader_ShowsScope()
    {
        var output = DumpSymbols("class Foo { }");
        Assert.StartsWith("Scope", output.TrimStart());
    }

    [Fact]
    public void Dump_ClassInheritance_ShowsBaseType()
    {
        var output = DumpSymbols("""
            class Base { }
            class Derived : Base { }
            """);
        Assert.Contains("Derived", output);
        Assert.Contains("Base", output);
    }
}

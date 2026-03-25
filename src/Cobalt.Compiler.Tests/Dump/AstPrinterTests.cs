using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Dump;
using Cobalt.Compiler.Syntax;

namespace Cobalt.Compiler.Tests.Dump;

public class AstPrinterTests
{
    private static string PrintAst(string source)
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        var unit = parser.ParseCompilationUnit();
        return new AstPrinter().Print(unit);
    }

    [Fact]
    public void Print_Namespace_ShowsNamespace()
    {
        var output = PrintAst("namespace Foo.Bar;");
        Assert.Contains("namespace Foo.Bar", output);
    }

    [Fact]
    public void Print_UseDirective_ShowsUse()
    {
        var output = PrintAst("use System;");
        Assert.Contains("use System", output);
    }

    [Fact]
    public void Print_Class_ShowsClassDeclaration()
    {
        var output = PrintAst("public class Foo { }");
        Assert.Contains("class", output);
        Assert.Contains("Foo", output);
    }

    [Fact]
    public void Print_ClassWithBase_ShowsInheritance()
    {
        var output = PrintAst("public class Foo : Bar { }");
        Assert.Contains("Foo", output);
        Assert.Contains("Bar", output);
    }

    [Fact]
    public void Print_Field_ShowsFieldDeclaration()
    {
        var output = PrintAst("""
            class Foo
            {
                public int x;
            }
            """);
        Assert.Contains("field", output);
        Assert.Contains("int", output);
        Assert.Contains("x", output);
    }

    [Fact]
    public void Print_OwnField_ShowsOwnership()
    {
        var output = PrintAst("""
            class Foo
            {
                own string name;
            }
            """);
        Assert.Contains("own", output);
        Assert.Contains("name", output);
    }

    [Fact]
    public void Print_Method_ShowsMethodSignature()
    {
        var output = PrintAst("""
            class Foo
            {
                public int Add(int a, int b) { return a; }
            }
            """);
        Assert.Contains("method", output);
        Assert.Contains("Add", output);
    }

    [Fact]
    public void Print_Trait_ShowsTraitDeclaration()
    {
        var output = PrintAst("""
            trait Printable
            {
                public string AsString();
            }
            """);
        Assert.Contains("trait", output);
        Assert.Contains("Printable", output);
    }

    [Fact]
    public void Print_Union_ShowsVariants()
    {
        var output = PrintAst("""
            union Shape
            {
                Circle(int Radius),
                Rectangle(int Width, int Height),
            }
            """);
        Assert.Contains("union", output);
        Assert.Contains("Circle", output);
        Assert.Contains("Rectangle", output);
    }

    [Fact]
    public void Print_IfStatement_ShowsIfThenElse()
    {
        var output = PrintAst("""
            class Foo
            {
                public void Run(int x)
                {
                    if (x > 0) { return; }
                }
            }
            """);
        Assert.Contains("if", output);
        Assert.Contains("then", output);
    }

    [Fact]
    public void Print_WhileLoop_ShowsWhile()
    {
        var output = PrintAst("""
            class Foo
            {
                public void Run(int x)
                {
                    while (x > 0) { x = x - 1; }
                }
            }
            """);
        Assert.Contains("while", output);
    }

    [Fact]
    public void Print_ForLoop_ShowsFor()
    {
        var output = PrintAst("""
            class Foo
            {
                public void Run()
                {
                    for (var i = 0; i < 10; i++) { }
                }
            }
            """);
        Assert.Contains("for", output);
    }

    [Fact]
    public void Print_MatchStatement_ShowsMatchArms()
    {
        var output = PrintAst("""
            class Foo
            {
                public void Run(int x)
                {
                    match (x)
                    {
                        var n => {
                            return;
                        },
                    };
                }
            }
            """);
        Assert.Contains("match", output);
    }

    [Fact]
    public void Print_InterpolatedString_ShowsParts()
    {
        var output = PrintAst("""
            class Foo
            {
                public string Greet(string name)
                {
                    return $"Hello, {name}!";
                }
            }
            """);
        Assert.Contains("interpolated-string", output);
    }

    [Fact]
    public void Print_Constructor_ShowsConstructor()
    {
        var output = PrintAst("""
            class Foo
            {
                public int x;
                public Foo(int x) { this.x = x; }
            }
            """);
        Assert.Contains("constructor", output);
    }

    [Fact]
    public void Print_ImplBlock_ShowsImpl()
    {
        var output = PrintAst("""
            trait T { public void Run(); }
            class C { }
            impl T for C
            {
                public void Run() { return; }
            }
            """);
        Assert.Contains("impl", output);
    }

    [Fact]
    public void Print_EmptyCompilationUnit_ReturnsCompilationUnit()
    {
        var output = PrintAst("");
        Assert.Contains("CompilationUnit", output);
    }
}

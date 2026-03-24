using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;

namespace Cobalt.Compiler.Tests.Semantics;

public class TypeCheckerTests
{
    private static (Scope scope, DiagnosticBag diagnostics) Check(string source)
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        var unit = parser.ParseCompilationUnit();
        var checker = new TypeChecker(diagnostics);
        var scope = checker.Check(unit);
        return (scope, diagnostics);
    }

    // ──────────────────────────────────────────────
    // Type declarations
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ClassDeclaration_RegistersType()
    {
        var (scope, diagnostics) = Check("class Foo { }");

        var symbol = scope.Lookup("Foo");
        Assert.NotNull(symbol);
        var typeSymbol = Assert.IsType<TypeSymbol>(symbol);
        Assert.Equal("Foo", typeSymbol.Name);
        Assert.Equal(TypeKind.Class, typeSymbol.Kind);
    }

    [Fact]
    public void Check_ClassWithFields_RegistersFields()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                int _x;
                string _name;
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("Foo"));
        Assert.Equal(2, typeSymbol.Fields.Count);
        Assert.Equal("_x", typeSymbol.Fields[0].Name);
        Assert.Equal("int", typeSymbol.Fields[0].Type.Name);
        Assert.Equal("_name", typeSymbol.Fields[1].Name);
        Assert.Equal("string", typeSymbol.Fields[1].Type.Name);
    }

    [Fact]
    public void Check_ClassWithMethods_RegistersMethods()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public void DoStuff(int x)
                {
                }
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("Foo"));
        Assert.Single(typeSymbol.Methods);
        Assert.Equal("DoStuff", typeSymbol.Methods[0].Name);
        Assert.Equal(AccessModifier.Public, typeSymbol.Methods[0].Access);
        Assert.Single(typeSymbol.Methods[0].Parameters);
        Assert.Equal("x", typeSymbol.Methods[0].Parameters[0].Name);
    }

    [Fact]
    public void Check_TraitDeclaration_RegistersType()
    {
        var (scope, diagnostics) = Check("""
            trait ITransform
            {
                void Apply();
            }
            """);

        var symbol = scope.Lookup("ITransform");
        Assert.NotNull(symbol);
        var typeSymbol = Assert.IsType<TypeSymbol>(symbol);
        Assert.Equal("ITransform", typeSymbol.Name);
        Assert.Equal(TypeKind.Trait, typeSymbol.Kind);
    }

    [Fact]
    public void Check_UnionDeclaration_RegistersTypeAndVariants()
    {
        var (scope, diagnostics) = Check("""
            union ProcessResult
            {
                Success(int LinesProcessed),
                Error(string Message),
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("ProcessResult"));
        Assert.Equal(TypeKind.Union, typeSymbol.Kind);
        Assert.NotNull(typeSymbol.Variants);
        Assert.Equal(2, typeSymbol.Variants!.Count);
        Assert.Equal("Success", typeSymbol.Variants[0].Name);
        Assert.Equal("Error", typeSymbol.Variants[1].Name);

        // Variants are also registered in scope for pattern matching
        var successVariant = scope.Lookup("Success");
        Assert.NotNull(successVariant);
        Assert.IsType<UnionVariantSymbol>(successVariant);
    }

    // ──────────────────────────────────────────────
    // Variable type inference
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_VariableDeclaration_InfersType()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public void Run()
                {
                    var x = 42;
                }
            }
            """);

        // The variable is in a child scope (method body), so we verify
        // no errors were reported (type inference succeeded).
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_VariableDeclaration_StringLiteral()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public void Run()
                {
                    var s = "hello";
                }
            }
            """);

        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_BoolLiteral_InfersType()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public void Run()
                {
                    var b = true;
                }
            }
            """);

        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Error diagnostics
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_UndefinedVariable_ReportsDiagnostic()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public void Run()
                {
                    var x = undeclared;
                }
            }
            """);

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All,
            d => d.Id == SemanticDiagnosticIds.UndefinedName);
    }

    [Fact]
    public void Check_DuplicateDeclaration_ReportsDiagnostic()
    {
        var (scope, diagnostics) = Check("""
            class Foo { }
            class Foo { }
            """);

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All,
            d => d.Id == SemanticDiagnosticIds.DuplicateDefinition);
    }

    // ──────────────────────────────────────────────
    // Method and parameter resolution
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_MethodReturnType_Resolved()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public int GetValue()
                {
                    return 42;
                }
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("Foo"));
        Assert.Single(typeSymbol.Methods);
        Assert.Equal("int", typeSymbol.Methods[0].ReturnType.Name);
        Assert.Same(BuiltInTypes.Int, typeSymbol.Methods[0].ReturnType);
    }

    [Fact]
    public void Check_OwnParameter_Resolved()
    {
        var (scope, diagnostics) = Check("""
            class Foo
            {
                public void Process(own Stream input)
                {
                }
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("Foo"));
        var method = typeSymbol.Methods[0];
        Assert.Single(method.Parameters);
        Assert.Equal("input", method.Parameters[0].Name);
        Assert.Equal(OwnershipModifier.Own, method.Parameters[0].Ownership);
        Assert.Equal("Stream", method.Parameters[0].Type.Name);
    }

    [Fact]
    public void Check_BaseType_Resolved()
    {
        var (scope, diagnostics) = Check("""
            trait ISerializable
            {
                void Serialize();
            }
            class Foo : ISerializable
            {
                public void Serialize()
                {
                }
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("Foo"));
        Assert.Single(typeSymbol.BaseTypes);
        Assert.Equal("ISerializable", typeSymbol.BaseTypes[0].Name);
    }

    // ──────────────────────────────────────────────
    // Complete valid program
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_NoErrors_SimpleProgram()
    {
        var (scope, diagnostics) = Check("""
            namespace Cobalt.Samples;
            use System;

            class Greeter
            {
                string _name;

                public void Greet()
                {
                    var msg = "hello";
                }
            }
            """);

        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
        Assert.NotNull(scope.Lookup("Greeter"));
    }

    // ──────────────────────────────────────────────
    // Ownership on fields
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_FieldOwnership_Preserved()
    {
        var (scope, diagnostics) = Check("""
            class FileProcessor
            {
                own Stream _input;
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("FileProcessor"));
        Assert.Single(typeSymbol.Fields);
        Assert.Equal("_input", typeSymbol.Fields[0].Name);
        Assert.Equal(OwnershipModifier.Own, typeSymbol.Fields[0].Ownership);
        Assert.Equal("Stream", typeSymbol.Fields[0].Type.Name);
    }

    // ──────────────────────────────────────────────
    // Property declarations
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_PropertyDeclaration_Registered()
    {
        var (scope, diagnostics) = Check("""
            trait ITransform
            {
                string Name { get; }
            }
            """);

        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("ITransform"));
        Assert.Single(typeSymbol.Properties);
        Assert.Equal("Name", typeSymbol.Properties[0].Name);
        Assert.True(typeSymbol.Properties[0].HasGetter);
        Assert.Equal("string", typeSymbol.Properties[0].Type.Name);
    }

    // ──────────────────────────────────────────────
    // Impl blocks
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ImplBlock_AddsMethods()
    {
        var (scope, diagnostics) = Check("""
            trait IGreetable
            {
                void Greet();
            }

            class Foo { }

            impl IGreetable for Foo
            {
                public void Greet()
                {
                }
            }
            """);

        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
        var typeSymbol = Assert.IsType<TypeSymbol>(scope.Lookup("Foo"));
        Assert.Single(typeSymbol.Methods);
        Assert.Equal("Greet", typeSymbol.Methods[0].Name);
    }

    // ──────────────────────────────────────────────
    // Free-standing functions
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_FreeFunctionDeclaration()
    {
        var (scope, diagnostics) = Check("""
            public int Add(int a, int b)
            {
                return a + b;
            }
            """);

        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
        var symbol = scope.Lookup("Add");
        Assert.NotNull(symbol);
        var method = Assert.IsType<MethodSymbol>(symbol);
        Assert.Equal("Add", method.Name);
        Assert.Equal("int", method.ReturnType.Name);
        Assert.Equal(2, method.Parameters.Count);
    }
}

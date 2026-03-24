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
        var (scope, _) = Check("class Foo { }");

        var symbol = scope.Lookup("Foo");
        Assert.NotNull(symbol);
        var typeSymbol = Assert.IsType<TypeSymbol>(symbol);
        Assert.Equal("Foo", typeSymbol.Name);
        Assert.Equal(TypeKind.Class, typeSymbol.Kind);
    }

    [Fact]
    public void Check_ClassWithFields_RegistersFields()
    {
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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
        var (scope, _) = Check("""
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

    // ──────────────────────────────────────────────
    // Return statement checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ReturnValueFromVoidMethod_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    return 42;
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void Check_ReturnVoidFromNonVoidMethod_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int Get()
                {
                    return;
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.MissingReturnValue);
    }

    [Fact]
    public void Check_ReturnCorrectType_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int Get()
                {
                    return 42;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Variable declaration checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_DuplicateLocalVariable_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = 1;
                    var x = 2;
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.DuplicateDefinition);
    }

    [Fact]
    public void Check_UsingVarDeclaration_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    using var s = new Stream();
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // If/while condition checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_IfConditionNotBool_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    if (42)
                    {
                    }
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void Check_IfConditionBool_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    if (true)
                    {
                    }
                    else
                    {
                    }
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_WhileConditionNotBool_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    while (42)
                    {
                    }
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
    }

    // ──────────────────────────────────────────────
    // For loop checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ForLoop_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    for (var i = 0; i < 10; i++)
                    {
                    }
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // ForEach checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ForEach_DeclaresLoopVariable()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var items = new List();
                    foreach (var item in items)
                    {
                    }
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Expression type checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_BinaryComparison_ReturnsBool()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                bool Test()
                {
                    return 1 < 2;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_LogicalOperator_RequiresBool()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = 1 && 2;
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void Check_LogicalOperator_BoolOk()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                bool Test()
                {
                    return true && false;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_StringConcatenation_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                string Greet()
                {
                    return "hello" + " world";
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_UnaryBang_ReturnsBool()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                bool Negate()
                {
                    return !true;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_UnaryMinus_ReturnsOperandType()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int Negate()
                {
                    return -42;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Method invocation checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_WrongArgumentCount_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Take(int x) { }
                void Do()
                {
                    Take(1, 2);
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.WrongArgumentCount);
    }

    [Fact]
    public void Check_CorrectArgumentCount_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int Add(int a, int b)
                {
                    return a;
                }
                void Do()
                {
                    var x = Add(1, 2);
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_UndefinedMethodCall_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    Nonexistent();
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.UndefinedName);
    }

    [Fact]
    public void Check_NotCallable_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = 42;
                    x();
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.NotCallable);
    }

    // ──────────────────────────────────────────────
    // Member access checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_AccessFieldOnClass_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int _value;
                int Get()
                {
                    return this._value;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_AccessPropertyOnClass_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                string Name { get; }
                string Get()
                {
                    return this.Name;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_UndefinedMemberOnCobaltType_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = this.Nonexistent;
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.UndefinedMember);
    }

    [Fact]
    public void Check_MemberOnDotNetType_Permissive()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var s = new Stream();
                    s.Read();
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_StaticMethodCall_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    Console.WriteLine("hello");
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Object creation checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ObjectCreation_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var s = new Stream();
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_ObjectCreationWithInitializer_NoError()
    {
        var (_, diagnostics) = Check("""
            class Bar
            {
                int _x;
            }
            class Foo
            {
                void Do()
                {
                    var b = new Bar
                    {
                        _x = 42,
                    };
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_ObjectCreationUndefinedType_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = new NonexistentType();
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.UndefinedType);
    }

    // ──────────────────────────────────────────────
    // This expression checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ThisInMethod_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                Foo GetSelf()
                {
                    return this;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Assignment checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_Assignment_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = 1;
                    x = 2;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_AssignmentTypeMismatch_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var x = 1;
                    x = "hello";
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.TypeMismatch);
    }

    // ──────────────────────────────────────────────
    // Pattern checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_MatchWithVariantPattern_NoError()
    {
        var (_, diagnostics) = Check("""
            union Outcome
            {
                Ok(int Value),
                Err(string Message),
            }
            class Foo
            {
                void Do()
                {
                    var r = Ok(1);
                    match (r)
                    {
                        Ok(var v) => Console.WriteLine("ok"),
                        Err(var m) => Console.WriteLine("err"),
                    };
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_SwitchExpression_NoError()
    {
        var (_, diagnostics) = Check("""
            union Shape
            {
                Circle(int Radius),
                Rect(int Width),
            }
            class Foo
            {
                int Area(int x)
                {
                    return x switch
                    {
                        Circle(var r) => r,
                        _ => 0,
                    };
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    [Fact]
    public void Check_IsPattern_ReturnsBool()
    {
        var (_, diagnostics) = Check("""
            union Maybe
            {
                Some(int Value),
                None,
            }
            class Foo
            {
                void Do()
                {
                    var x = Some(42);
                    if (x is Some(var v))
                    {
                    }
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Interpolated string checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_InterpolatedString_ReturnsString()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                string Greet()
                {
                    var name = "world";
                    return $"hello {name}";
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Index expression checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_IndexExpression_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Do()
                {
                    var items = new List();
                    var x = items[0];
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Own/RefMut expression checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_OwnExpression_PassesThrough()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                void Take(own Stream s) { }
                void Do()
                {
                    var s = new Stream();
                    Take(own s);
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Field initializer checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_FieldInitializer_Checked()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int _x = 42;
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Expression-bodied property checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ExpressionBodiedProperty_Checked()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                public string Name => "hello";
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Constructor body checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ConstructorBody_Checked()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int _x;
                public Foo(int x)
                {
                    var y = x;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Trait body checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_TraitWithMethodBody_Checked()
    {
        var (_, diagnostics) = Check("""
            trait IGreetable
            {
                string Greet();
                string DefaultGreet()
                {
                    return "hello";
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Impl block body checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ImplBlockBody_Checked()
    {
        var (_, diagnostics) = Check("""
            trait IGreetable
            {
                string Greet();
            }
            class Foo { }
            impl IGreetable for Foo
            {
                public string Greet()
                {
                    return "hi";
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Top-level method body checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_TopLevelFunctionBody_Checked()
    {
        var (_, diagnostics) = Check("""
            int Square(int x)
            {
                return x * x;
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Undefined type checking
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_UndefinedType_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                UnknownType _x;
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.UndefinedType);
    }

    // ──────────────────────────────────────────────
    // Numeric promotion
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_NumericPromotion_NoError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                double Widen(int x)
                {
                    return x;
                }
            }
            """);
        Assert.False(diagnostics.HasErrors,
            string.Join("; ", diagnostics.All.Select(d => d.Message)));
    }

    // ──────────────────────────────────────────────
    // Type mismatch (non-numeric, non-.NET)
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_TypeMismatch_ReportsError()
    {
        var (_, diagnostics) = Check("""
            class Foo
            {
                int Get()
                {
                    return true;
                }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.TypeMismatch);
    }

    // ──────────────────────────────────────────────
    // Impl block with undefined target type
    // ──────────────────────────────────────────────

    [Fact]
    public void Check_ImplBlockUndefinedTarget_ReportsError()
    {
        var (_, diagnostics) = Check("""
            trait IFoo
            {
                void Do();
            }
            impl IFoo for Nonexistent
            {
                public void Do() { }
            }
            """);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == SemanticDiagnosticIds.UndefinedType);
    }
}

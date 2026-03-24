using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Syntax;

namespace Cobalt.Compiler.Tests.Syntax;

public class ParserTests
{
    private static CompilationUnit Parse(string source)
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        return parser.ParseCompilationUnit();
    }

    private static (CompilationUnit Unit, DiagnosticBag Diagnostics) ParseWithDiagnostics(string source)
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        var unit = parser.ParseCompilationUnit();
        return (unit, diagnostics);
    }

    // ──────────────────────────────────────────────
    // Namespace and Use
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_NamespaceAndUse()
    {
        var unit = Parse("""
            namespace Cobalt.Samples;
            use System;
            use System.IO;
            """);
        Assert.NotNull(unit.Namespace);
        Assert.Equal("Cobalt.Samples", unit.Namespace!.Name);
        Assert.Equal(2, unit.Uses.Count);
        Assert.Equal("System", unit.Uses[0].Name);
        Assert.Equal("System.IO", unit.Uses[1].Name);
    }

    // ──────────────────────────────────────────────
    // Class with fields
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_ClassWithFields()
    {
        var unit = Parse("""
            class Foo
            {
                int _x;
                string _name;
            }
            """);
        Assert.Single(unit.Members);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        Assert.Equal("Foo", cls.Name);
        Assert.Equal(2, cls.Members.Count);
        var f1 = Assert.IsType<FieldDeclaration>(cls.Members[0]);
        Assert.Equal("_x", f1.Name);
        Assert.Equal("int", f1.Type.Name);
        var f2 = Assert.IsType<FieldDeclaration>(cls.Members[1]);
        Assert.Equal("_name", f2.Name);
        Assert.Equal("string", f2.Type.Name);
    }

    [Fact]
    public void Parse_ClassWithOwnedFields()
    {
        var unit = Parse("""
            class FileProcessor
            {
                own Stream _input;
                own Stream _output;
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        Assert.Equal(2, cls.Members.Count);
        var f1 = Assert.IsType<FieldDeclaration>(cls.Members[0]);
        Assert.Equal("_input", f1.Name);
        Assert.Equal(OwnershipModifier.Own, f1.Ownership);
        Assert.Equal("Stream", f1.Type.Name);
    }

    // ──────────────────────────────────────────────
    // Methods and parameters
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_MethodWithParameters()
    {
        var unit = Parse("""
            class Foo
            {
                public void DoStuff(int x, string name)
                {
                }
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var method = Assert.IsType<MethodDeclaration>(cls.Members[0]);
        Assert.Equal("DoStuff", method.Name);
        Assert.Equal(AccessModifier.Public, method.Access);
        Assert.Equal("void", method.ReturnType.Name);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Equal("x", method.Parameters[0].Name);
        Assert.Equal("int", method.Parameters[0].Type.Name);
    }

    [Fact]
    public void Parse_OwnRefMutParameters()
    {
        var unit = Parse("""
            class Foo
            {
                public void Process(own Stream input, ref mut List<string> lines)
                {
                }
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var method = Assert.IsType<MethodDeclaration>(cls.Members[0]);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Equal(OwnershipModifier.Own, method.Parameters[0].Ownership);
        Assert.Equal("Stream", method.Parameters[0].Type.Name);
        Assert.Equal(OwnershipModifier.RefMut, method.Parameters[1].Ownership);
        Assert.Equal("List", method.Parameters[1].Type.Name);
    }

    // ──────────────────────────────────────────────
    // Trait
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_TraitDeclaration()
    {
        var unit = Parse("""
            trait ITransform
            {
                string Name { get; }
                void Apply(ref mut List<string> lines);
            }
            """);
        var trait = Assert.IsType<TraitDeclaration>(unit.Members[0]);
        Assert.Equal("ITransform", trait.Name);
        Assert.Equal(2, trait.Members.Count);
        var prop = Assert.IsType<PropertyDeclaration>(trait.Members[0]);
        Assert.Equal("Name", prop.Name);
        Assert.True(prop.HasGetter);
        var method = Assert.IsType<MethodDeclaration>(trait.Members[1]);
        Assert.Equal("Apply", method.Name);
    }

    // ──────────────────────────────────────────────
    // Impl block
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_ImplBlock()
    {
        var unit = Parse("""
            impl ITransform for UpperCaseTransform
            {
                public void Apply(ref mut List<string> lines)
                {
                }
            }
            """);
        var impl = Assert.IsType<ImplBlock>(unit.Members[0]);
        Assert.Equal("ITransform", impl.TraitName);
        Assert.Equal("UpperCaseTransform", impl.TargetTypeName);
        Assert.Single(impl.Members);
    }

    // ──────────────────────────────────────────────
    // Union
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_UnionDeclaration()
    {
        var unit = Parse("""
            union ProcessResult
            {
                Success(int LinesProcessed),
                Error(string Message),
                Skipped(string Reason),
            }
            """);
        var u = Assert.IsType<UnionDeclaration>(unit.Members[0]);
        Assert.Equal("ProcessResult", u.Name);
        Assert.Equal(3, u.Variants.Count);
        Assert.Equal("Success", u.Variants[0].Name);
        Assert.Single(u.Variants[0].Fields);
        Assert.Equal("LinesProcessed", u.Variants[0].Fields[0].Name);
        Assert.Equal("int", u.Variants[0].Fields[0].Type.Name);
        Assert.Equal("Error", u.Variants[1].Name);
        Assert.Equal("Skipped", u.Variants[2].Name);
    }

    // ──────────────────────────────────────────────
    // Variable declarations
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_VariableDeclaration()
    {
        var unit = Parse("var x = 42;");
        var stmt = Assert.IsType<VariableDeclaration>(unit.Members[0]);
        Assert.Equal("x", stmt.Name);
        Assert.True(stmt.IsVar);
        var lit = Assert.IsType<LiteralExpression>(stmt.Initializer);
        Assert.Equal(42L, lit.Value);
    }

    [Fact]
    public void Parse_UsingVarDeclaration()
    {
        var unit = Parse("using var p = Create();");
        var stmt = Assert.IsType<UsingVarDeclaration>(unit.Members[0]);
        Assert.Equal("p", stmt.Name);
        Assert.NotNull(stmt.Initializer);
    }

    // ──────────────────────────────────────────────
    // Control flow
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_IfElseStatement()
    {
        var unit = Parse("""
            if (x > 0)
            {
                return 1;
            }
            else
            {
                return 0;
            }
            """);
        var ifStmt = Assert.IsType<IfStatement>(unit.Members[0]);
        Assert.NotNull(ifStmt.Condition);
        Assert.NotNull(ifStmt.ThenBody);
        Assert.NotNull(ifStmt.ElseBody);
    }

    [Fact]
    public void Parse_WhileLoop()
    {
        var unit = Parse("""
            while (x > 0)
            {
                x = x - 1;
            }
            """);
        var whileStmt = Assert.IsType<WhileStatement>(unit.Members[0]);
        Assert.NotNull(whileStmt.Condition);
        Assert.NotNull(whileStmt.Body);
    }

    [Fact]
    public void Parse_ForLoop()
    {
        var unit = Parse("""
            for (var i = 0; i < 10; i++)
            {
                x = x + 1;
            }
            """);
        var forStmt = Assert.IsType<ForStatement>(unit.Members[0]);
        Assert.NotNull(forStmt.Initializer);
        Assert.NotNull(forStmt.Condition);
        Assert.NotNull(forStmt.Increment);
    }

    [Fact]
    public void Parse_ForEachWithOwnershipModifier()
    {
        var unit = Parse("""
            foreach (ref item in collection)
            {
                item.Process();
            }
            """);
        var forEach = Assert.IsType<ForEachStatement>(unit.Members[0]);
        Assert.Equal(OwnershipModifier.Ref, forEach.Ownership);
        Assert.Equal("item", forEach.VariableName);
    }

    // ──────────────────────────────────────────────
    // Match and Switch
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_MatchStatement()
    {
        var unit = Parse("""
            match (result)
            {
                Success(var n) => DoSomething(n),
                Error(var msg) => HandleError(msg),
            };
            """);
        var matchStmt = Assert.IsType<MatchStatement>(unit.Members[0]);
        Assert.Equal(2, matchStmt.Arms.Count);
        var firstArm = matchStmt.Arms[0];
        var pattern = Assert.IsType<VariantPattern>(firstArm.Pattern);
        Assert.Equal("Success", pattern.VariantName);
    }

    [Fact]
    public void Parse_SwitchExpression()
    {
        var unit = Parse("""
            class Foo
            {
                string Describe(int x)
                {
                    return x switch
                    {
                        Positive(var n) => n,
                        _ => 0,
                    };
                }
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var method = Assert.IsType<MethodDeclaration>(cls.Members[0]);
        Assert.NotNull(method.Body);
        var ret = Assert.IsType<ReturnStatement>(method.Body!.Statements[0]);
        Assert.IsType<SwitchExpression>(ret.Expression);
    }

    // ──────────────────────────────────────────────
    // Expressions
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_BinaryExpressions()
    {
        var unit = Parse("var x = 1 + 2 * 3;");
        var decl = Assert.IsType<VariableDeclaration>(unit.Members[0]);
        // 1 + (2 * 3) — multiplication binds tighter
        var add = Assert.IsType<BinaryExpression>(decl.Initializer);
        Assert.Equal(TokenKind.Plus, add.Operator);
        var mul = Assert.IsType<BinaryExpression>(add.Right);
        Assert.Equal(TokenKind.Star, mul.Operator);
    }

    [Fact]
    public void Parse_OwnExpression()
    {
        var unit = Parse("foo(own x);");
        var exprStmt = Assert.IsType<ExpressionStatement>(unit.Members[0]);
        var call = Assert.IsType<InvocationExpression>(exprStmt.Expression);
        Assert.Single(call.Arguments);
        Assert.Equal(OwnershipModifier.Own, call.Arguments[0].Ownership);
    }

    [Fact]
    public void Parse_ObjectCreation()
    {
        var unit = Parse("var x = new StreamReader(input);");
        var decl = Assert.IsType<VariableDeclaration>(unit.Members[0]);
        var creation = Assert.IsType<ObjectCreationExpression>(decl.Initializer);
        Assert.Equal("StreamReader", creation.Type.Name);
        Assert.Single(creation.Arguments);
    }

    [Fact]
    public void Parse_ObjectCreationWithInitializer()
    {
        var unit = Parse("""
            var x = new FileProcessor
            {
                _input = own input,
                _output = own output,
            };
            """);
        var decl = Assert.IsType<VariableDeclaration>(unit.Members[0]);
        var creation = Assert.IsType<ObjectCreationExpression>(decl.Initializer);
        Assert.Equal("FileProcessor", creation.Type.Name);
        Assert.NotNull(creation.InitializerClauses);
        Assert.Equal(2, creation.InitializerClauses!.Count);
        Assert.Equal("_input", creation.InitializerClauses[0].FieldName);
        Assert.Equal(OwnershipModifier.Own, creation.InitializerClauses[0].Ownership);
    }

    // ──────────────────────────────────────────────
    // Properties
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_PropertyWithGetter()
    {
        var unit = Parse("""
            class Foo
            {
                string Name { get; }
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var prop = Assert.IsType<PropertyDeclaration>(cls.Members[0]);
        Assert.Equal("Name", prop.Name);
        Assert.True(prop.HasGetter);
        Assert.False(prop.HasSetter);
    }

    [Fact]
    public void Parse_ExpressionBodiedProperty()
    {
        var unit = Parse("""
            class Foo
            {
                public string Name => "uppercase";
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var prop = Assert.IsType<PropertyDeclaration>(cls.Members[0]);
        Assert.Equal("Name", prop.Name);
        Assert.NotNull(prop.ExpressionBody);
        var lit = Assert.IsType<LiteralExpression>(prop.ExpressionBody);
        Assert.Equal("uppercase", lit.Value);
    }

    // ──────────────────────────────────────────────
    // Generic type arguments with ownership
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_OwnInGenericTypeArgument()
    {
        var unit = Parse("""
            class Foo
            {
                own List<own ITransform> _transforms;
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var field = Assert.IsType<FieldDeclaration>(cls.Members[0]);
        Assert.Equal("List", field.Type.Name);
        Assert.Equal(OwnershipModifier.Own, field.Ownership);
        Assert.Single(field.Type.TypeArguments);
        Assert.Equal("ITransform", field.Type.TypeArguments[0].Name);
        Assert.Equal(OwnershipModifier.Own, field.Type.TypeArguments[0].Ownership);
    }

    // ──────────────────────────────────────────────
    // Field with initializer
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_FieldWithInitializer()
    {
        var unit = Parse("""
            class Foo
            {
                own List<own ITransform> _transforms = new();
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var field = Assert.IsType<FieldDeclaration>(cls.Members[0]);
        Assert.Equal("_transforms", field.Name);
        Assert.NotNull(field.Initializer);
        Assert.IsType<ObjectCreationExpression>(field.Initializer);
    }

    // ──────────────────────────────────────────────
    // Interpolated strings
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_InterpolatedString()
    {
        var unit = Parse("""var x = $"hello {name} world";""");
        var decl = Assert.IsType<VariableDeclaration>(unit.Members[0]);
        var interp = Assert.IsType<InterpolatedStringExpression>(decl.Initializer);
        Assert.True(interp.Parts.Count >= 2);
    }

    // ──────────────────────────────────────────────
    // Is pattern expression
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_IsPatternExpression()
    {
        var unit = Parse("""
            class Foo
            {
                void Check()
                {
                    if (x is Some(var val))
                    {
                    }
                }
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var method = Assert.IsType<MethodDeclaration>(cls.Members[0]);
        var ifStmt = Assert.IsType<IfStatement>(method.Body!.Statements[0]);
        var isPat = Assert.IsType<IsPatternExpression>(ifStmt.Condition);
        Assert.IsType<VariantPattern>(isPat.Pattern);
    }

    // ──────────────────────────────────────────────
    // Return statement
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_ReturnStatement()
    {
        var unit = Parse("""
            class Foo
            {
                int Get()
                {
                    return 42;
                }
            }
            """);
        var cls = Assert.IsType<ClassDeclaration>(unit.Members[0]);
        var method = Assert.IsType<MethodDeclaration>(cls.Members[0]);
        var ret = Assert.IsType<ReturnStatement>(method.Body!.Statements[0]);
        var lit = Assert.IsType<LiteralExpression>(ret.Expression);
        Assert.Equal(42L, lit.Value);
    }

    // ──────────────────────────────────────────────
    // Top-level statements
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_TopLevelStatements()
    {
        var unit = Parse("""
            namespace Test;
            var x = 1;
            var y = 2;
            """);
        Assert.NotNull(unit.Namespace);
        Assert.Equal(2, unit.Members.Count);
        Assert.IsType<VariableDeclaration>(unit.Members[0]);
        Assert.IsType<VariableDeclaration>(unit.Members[1]);
    }

    // ──────────────────────────────────────────────
    // Free-standing function
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_FreeStandingFunction()
    {
        var unit = Parse("""
            string Greet(string name)
            {
                return name;
            }
            """);
        var method = Assert.IsType<MethodDeclaration>(unit.Members[0]);
        Assert.Equal("Greet", method.Name);
        Assert.Equal("string", method.ReturnType.Name);
        Assert.Single(method.Parameters);
        Assert.NotNull(method.Body);
    }

    // ──────────────────────────────────────────────
    // Full sample file
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_FullSample_TransformsCo()
    {
        var samplePath = Path.Combine(FindRepoRoot(), "samples", "cobalt-syntax", "transforms.co");
        var source = File.ReadAllText(samplePath);
        var (unit, diagnostics) = ParseWithDiagnostics(source);

        Assert.False(diagnostics.HasErrors,
            $"Parser reported errors:\n{string.Join("\n", diagnostics.All)}");
        Assert.NotNull(unit.Namespace);
        Assert.Equal("Cobalt.Samples", unit.Namespace!.Name);
        Assert.NotEmpty(unit.Members);
    }

    // ──────────────────────────────────────────────
    // Error recovery
    // ──────────────────────────────────────────────

    [Fact]
    public void Parse_ErrorRecovery_MissingSemicolon()
    {
        var (unit, diagnostics) = ParseWithDiagnostics("var x = 1\nvar y = 2;");
        Assert.True(diagnostics.HasErrors);
        // Should still parse something
        Assert.NotEmpty(unit.Members);
    }

    // ──────────────────────────────────────────────
    // Helper
    // ──────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "samples")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}

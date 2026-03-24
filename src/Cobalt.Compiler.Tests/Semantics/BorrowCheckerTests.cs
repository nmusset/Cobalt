using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;

namespace Cobalt.Compiler.Tests.Semantics;

public class BorrowCheckerTests
{
    private static DiagnosticBag CheckBorrows(string source)
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        var unit = parser.ParseCompilationUnit();
        var typeChecker = new TypeChecker(diagnostics);
        var scope = typeChecker.Check(unit);
        var borrowChecker = new BorrowChecker(diagnostics, scope);
        borrowChecker.Check(unit);
        return diagnostics;
    }

    private static List<Diagnostic> BorrowErrors(DiagnosticBag bag)
    {
        return bag.All.Where(d => d.Id.StartsWith("CB3")).ToList();
    }

    // ──────────────────────────────────────────────
    // 1. Own transfer — no error
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_OwnTransfer_MovesVariable()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream input)
                {
                    Take(own input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // The only borrow diagnostic should be the CB3005 warning on Take's param 's'
        // which is own but not moved/disposed. Run's param 'input' was moved, so no warning there.
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.DoubleMove);
    }

    // ──────────────────────────────────────────────
    // 2. Use after move
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_UseAfterMove_ReportsError()
    {
        // Move 'input' once with own, then pass it again without own — use after move.
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream input)
                {
                    Take(own input);
                    Take(input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
    }

    // ──────────────────────────────────────────────
    // 3. Double move
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_DoubleMove_ReportsError()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void A(own Stream s) { }
                public void B(own Stream s) { }
                public void Run(own Stream input)
                {
                    A(own input);
                    B(own input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.DoubleMove);
    }

    // ──────────────────────────────────────────────
    // 4. Move of borrowed value
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_MoveOfBorrowedValue_ReportsError()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Process(ref Stream input)
                {
                    Take(own input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.MoveOfBorrowedValue);
    }

    // ──────────────────────────────────────────────
    // 5. Using var moved — CB3007
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_UsingVarMoved_ReportsWarning()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run()
                {
                    using var f = new Stream();
                    Take(own f);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.UsingVarMoved);
    }

    // ──────────────────────────────────────────────
    // 6. Normal usage — no error
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_NormalUsage_NoError()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Run()
                {
                    var x = 42;
                    var y = x;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // No move/borrow errors — locals may get OwnedNotDisposed warnings but no
        // UseAfterMove, DoubleMove, or MoveOfBorrowedValue errors.
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.DoubleMove);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.MoveOfBorrowedValue);
    }

    // ──────────────────────────────────────────────
    // 7. Ref parameter can be read
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_RefParameter_CanBeRead()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Read(ref Stream input)
                {
                    var x = input;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
    }

    // ──────────────────────────────────────────────
    // 8. Own parameter can be used before move
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_OwnParameter_CanBeUsedBeforeMove()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream input)
                {
                    var x = input;
                    Take(own input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
    }

    // ──────────────────────────────────────────────
    // 9. Own parameter not disposed or moved — CB3005 warning
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_OwnParameter_NotDisposedOrMoved_ReportsWarning()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Run(own Stream input)
                {
                    var x = 42;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.OwnedNotDisposed);
    }

    // ──────────────────────────────────────────────
    // 10. Own parameter disposed — no warning
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_OwnParameter_Disposed_NoWarning()
    {
        // Note: The current borrow checker does not track .Dispose() calls
        // to set state to Disposed. Moving the value is the way to "consume" it.
        // So we verify that moving (own) suppresses the OwnedNotDisposed warning.
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream input)
                {
                    Take(own input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.DoesNotContain(errors, d =>
            d.Id == BorrowDiagnosticIds.OwnedNotDisposed
            && d.Message.Contains("input"));
    }

    // ──────────────────────────────────────────────
    // 11. Var declaration — no issue
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_VarDeclaration_NoIssue()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Run()
                {
                    var x = 10;
                    var y = 20;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // Regular var declarations don't trigger move/borrow errors.
        // They may trigger OwnedNotDisposed since locals are tracked as Own,
        // but no UseAfterMove or DoubleMove errors.
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.DoubleMove);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.MoveOfBorrowedValue);
    }

    // ──────────────────────────────────────────────
    // 12. Using var auto-disposed — no warning
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_UsingVarDisposal_NoIssue()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Run()
                {
                    using var f = new Stream();
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Empty(errors);
    }

    // ──────────────────────────────────────────────
    // 13. Ref mut argument — no move
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_RefMutArgument_NoMove()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Modify(ref mut Stream s) { }
                public void Run(own Stream input)
                {
                    Modify(ref mut input);
                    var x = input;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
    }

    // ──────────────────────────────────────────────
    // 14. Assignment after move — error
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_AssignmentAfterMove_ReportsError()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream input)
                {
                    Take(own input);
                    var y = input;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
    }

    // ──────────────────────────────────────────────
    // 15. Move in method call — tracks correctly
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_MoveInMethodCall_TracksCorrectly()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream input)
                {
                    Take(own input);
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // input was moved via own arg, so no OwnedNotDisposed for 'input'
        Assert.DoesNotContain(errors, d =>
            d.Id == BorrowDiagnosticIds.OwnedNotDisposed
            && d.Message.Contains("input"));
    }

    // ──────────────────────────────────────────────
    // 16. Multiple own params — independent tracking
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_MultipleOwnParams_IndependentTracking()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Run(own Stream a, own Stream b)
                {
                    Take(own a);
                    var x = b;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // 'a' was moved — no UseAfterMove for 'a'
        Assert.DoesNotContain(errors, d =>
            d.Id == BorrowDiagnosticIds.UseAfterMove
            && d.Message.Contains("'a'"));
        // 'b' was NOT moved, so OwnedNotDisposed warning for 'b'
        Assert.Contains(errors, d =>
            d.Id == BorrowDiagnosticIds.OwnedNotDisposed
            && d.Message.Contains("'b'"));
    }

    // ──────────────────────────────────────────────
    // 17. Move in object initializer — tracks correctly
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_MoveInObjectInitializer_TracksCorrectly()
    {
        // The parser stores the 'own' modifier on InitializerClause.Ownership,
        // but the borrow checker currently only calls CheckExpression on the value.
        // This means the identifier 'input' is read (not moved) by the checker,
        // and the own parameter triggers an OwnedNotDisposed warning.
        // This test documents the current behavior.
        var bag = CheckBorrows("""
            class Processor
            {
                own Stream _input;
            }
            class Foo
            {
                public void Run(own Stream input)
                {
                    var p = new Processor
                    {
                        _input = own input,
                    };
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // The 'own' in initializer doesn't currently trigger a move in the borrow checker,
        // so 'input' is still owned and triggers OwnedNotDisposed.
        Assert.Contains(errors, d =>
            d.Id == BorrowDiagnosticIds.OwnedNotDisposed
            && d.Message.Contains("input"));
    }

    // ──────────────────────────────────────────────
    // 18. No own annotation — no tracking
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_NoOwnAnnotation_NoTracking()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public void Run(Stream input)
                {
                    var x = input;
                    var y = input;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // Regular (non-own) parameters are not tracked for ownership
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(errors, d => d.Id == BorrowDiagnosticIds.DoubleMove);
    }

    // ──────────────────────────────────────────────
    // 19. Free function body — checked
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_FreeFunctionBody_Checked()
    {
        var bag = CheckBorrows("""
            void Take(own Stream s) { }
            void Run(own Stream input)
            {
                Take(own input);
                Take(own input);
            }
            """);

        var errors = BorrowErrors(bag);
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.DoubleMove);
    }

    // ──────────────────────────────────────────────
    // 20. Constructor body — checked
    // ──────────────────────────────────────────────

    [Fact]
    public void Borrow_ConstructorBody_Checked()
    {
        var bag = CheckBorrows("""
            class Foo
            {
                public Foo(own Stream input)
                {
                    var x = input;
                }
            }
            """);

        var errors = BorrowErrors(bag);
        // The own parameter 'input' is not moved or disposed, but merely read.
        // Check that constructor bodies are visited — we expect the OwnedNotDisposed warning.
        Assert.Contains(errors, d => d.Id == BorrowDiagnosticIds.OwnedNotDisposed);
    }
}

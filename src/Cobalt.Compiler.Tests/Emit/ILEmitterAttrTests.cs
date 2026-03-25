using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;

namespace Cobalt.Compiler.Tests.Emit;

public class ILEmitterAttrTests
{
    // ──────────────────────────────────────────────
    // Helper: full pipeline  parse → type-check → IL emit
    // ──────────────────────────────────────────────

    private static AssemblyDefinition Emit(string source, string asmName = "Test")
    {
        var lexer = new Lexer(source, "test.co");
        var tokens = lexer.Lex();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(lexer.Diagnostics);
        var parser = new Parser(tokens, diagnostics);
        var unit = parser.ParseCompilationUnit();
        var checker = new TypeChecker(diagnostics);
        var scope = checker.Check(unit);
        var emitter = new ILEmitter(asmName, new Version(1, 0, 0, 0), scope);
        return emitter.Emit(unit);
    }

    private static TypeDefinition GetType(AssemblyDefinition asm, string name) =>
        asm.MainModule.Types.First(t => t.Name == name);

    private static MethodDefinition GetMethod(TypeDefinition type, string name) =>
        type.Methods.First(m => m.Name == name);

    // ──────────────────────────────────────────────
    // Parameter ownership attributes
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_OwnParameter_HasOwnedAttribute()
    {
        var asm = Emit("""
            public class Transfer
            {
                public void Take(own Stream s) { return; }
            }
            """);
        var method = GetMethod(GetType(asm, "Transfer"), "Take");
        var param = method.Parameters[0];
        Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");
    }

    [Fact]
    public void Emit_RefParameter_HasBorrowedAttribute()
    {
        var asm = Emit("""
            public class Borrow
            {
                public void Read(ref Stream s) { return; }
            }
            """);
        var method = GetMethod(GetType(asm, "Borrow"), "Read");
        var param = method.Parameters[0];
        Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "BorrowedAttribute");
    }

    [Fact]
    public void Emit_RefMutParameter_HasMutBorrowedAttribute()
    {
        var asm = Emit("""
            public class Mutate
            {
                public void Write(ref mut Stream s) { return; }
            }
            """);
        var method = GetMethod(GetType(asm, "Mutate"), "Write");
        var param = method.Parameters[0];
        Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "MutBorrowedAttribute");
    }

    // ──────────────────────────────────────────────
    // Field ownership attributes
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_OwnField_HasOwnedAttribute()
    {
        var asm = Emit("""
            public class Owner
            {
                own Stream resource;
            }
            """);
        var type = GetType(asm, "Owner");
        var field = type.Fields.First(f => f.Name == "resource");
        Assert.Contains(field.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");
    }

    // ──────────────────────────────────────────────
    // No attribute when ownership is None
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_PlainParameter_NoOwnershipAttribute()
    {
        var asm = Emit("""
            public class Plain
            {
                public void Do(int x) { return; }
            }
            """);
        var method = GetMethod(GetType(asm, "Plain"), "Do");
        var param = method.Parameters[0];
        Assert.DoesNotContain(param.CustomAttributes, a =>
            a.AttributeType.Name is "OwnedAttribute" or "BorrowedAttribute" or "MutBorrowedAttribute");
    }
}

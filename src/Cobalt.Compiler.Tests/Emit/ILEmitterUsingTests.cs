using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cobalt.Compiler.Tests.Emit;

public class ILEmitterUsingTests
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

    private static IList<Instruction> Instructions(MethodDefinition method) =>
        method.Body.Instructions;

    // ──────────────────────────────────────────────
    // Tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_UsingVar_EmitsTryFinally()
    {
        var asm = Emit("""
            public class UsingTest
            {
                public void Run(Stream s)
                {
                    using var resource = s;
                    var x = 1;
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "UsingTest"), "Run");
        Assert.True(method.Body.ExceptionHandlers.Count > 0);
        Assert.Equal(ExceptionHandlerType.Finally, method.Body.ExceptionHandlers[0].HandlerType);
    }

    [Fact]
    public void Emit_UsingVar_CallsDispose()
    {
        var asm = Emit("""
            public class DisposeTest
            {
                public void Run(Stream s)
                {
                    using var resource = s;
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "DisposeTest"), "Run");
        Assert.True(Instructions(method).Any(i =>
            i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr && mr.Name == "Dispose"));
    }
}

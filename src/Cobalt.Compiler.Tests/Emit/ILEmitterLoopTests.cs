using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cobalt.Compiler.Tests.Emit;

public class ILEmitterLoopTests
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

    private static bool HasOpCode(MethodDefinition method, OpCode opCode) =>
        method.Body.Instructions.Any(i => i.OpCode == opCode);

    // ──────────────────────────────────────────────
    // Tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_BreakInWhile_EmitsBranch()
    {
        var asm = Emit("""
            public class Test
            {
                public void Run(bool flag)
                {
                    while (flag) { break; }
                    return;
                }
            }
            """);

        var type = GetType(asm, "Test");
        var method = GetMethod(type, "Run");
        var instructions = Instructions(method);

        // The break should produce a Br instruction targeting the end label
        var brInstructions = instructions.Where(i => i.OpCode == OpCodes.Br).ToList();
        Assert.True(brInstructions.Count >= 1, "Expected at least one Br instruction for break");
    }

    [Fact]
    public void Emit_ContinueInFor_EmitsBranch()
    {
        var asm = Emit("""
            public class Test
            {
                public void Run()
                {
                    for (var i = 0; i < 10; i = i + 1) { continue; }
                    return;
                }
            }
            """);

        var type = GetType(asm, "Test");
        var method = GetMethod(type, "Run");
        var instructions = Instructions(method);

        // continue should branch to increment label, plus there's the loop-back Br
        var brInstructions = instructions.Where(i => i.OpCode == OpCodes.Br).ToList();
        Assert.True(brInstructions.Count >= 2, "Expected at least 2 Br instructions (continue + loop back)");
    }

    [Fact]
    public void Emit_ForEach_EmitsMoveNextLoop()
    {
        var asm = Emit("""
            public class ForEachTest
            {
                public void Run(List items)
                {
                    foreach (var item in items)
                    {
                        var x = item;
                    }
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "ForEachTest"), "Run");
        Assert.True(HasOpCode(method, OpCodes.Callvirt));
        Assert.True(HasOpCode(method, OpCodes.Brtrue));
    }
}

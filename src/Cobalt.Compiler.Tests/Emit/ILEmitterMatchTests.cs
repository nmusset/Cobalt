using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cobalt.Compiler.Tests.Emit;

public class ILEmitterMatchTests
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

    private static bool HasOpCode(MethodDefinition method, OpCode opCode) =>
        method.Body.Instructions.Any(i => i.OpCode == opCode);

    private static IList<Instruction> Instructions(MethodDefinition method) =>
        method.Body.Instructions;

    // ══════════════════════════════════════════════
    // Match statement tests
    // ══════════════════════════════════════════════

    [Fact]
    public void Emit_MatchOnUnion_EmitsIsinst()
    {
        var asm = Emit("""
            union Shape
            {
                Circle(int Radius),
                Rectangle(int Width, int Height),
            }
            public class Matcher
            {
                public int Area(Shape s)
                {
                    match (s)
                    {
                        Circle(var r) => return r,
                        Rectangle(var w, var h) => return w,
                    };
                    return 0;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Matcher"), "Area");
        Assert.True(HasOpCode(method, OpCodes.Isinst));
        Assert.True(HasOpCode(method, OpCodes.Castclass));
        Assert.True(HasOpCode(method, OpCodes.Throw));  // safety-net
    }

    [Fact]
    public void Emit_MatchVarCatchAll_BindsLocal()
    {
        var asm = Emit("""
            union Res
            {
                Ok(int Value),
                Err(string Message),
            }
            public class Handler
            {
                public void Handle(Res r)
                {
                    match (r)
                    {
                        var x => return,
                    };
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Handler"), "Handle");
        Assert.False(HasOpCode(method, OpCodes.Isinst));  // catch-all, no type test
    }

    [Fact]
    public void Emit_MatchWithBlockBody_Works()
    {
        var asm = Emit("""
            union Status
            {
                Active(int Id),
                Inactive(),
            }
            public class Check
            {
                public int GetId(Status s)
                {
                    match (s)
                    {
                        Active(var id) => {
                            return id;
                        },
                        Inactive() => {
                            return 0;
                        },
                    };
                    return 0;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Check"), "GetId");
        Assert.True(HasOpCode(method, OpCodes.Isinst));
        Assert.True(HasOpCode(method, OpCodes.Ldfld));  // field extraction
    }
}

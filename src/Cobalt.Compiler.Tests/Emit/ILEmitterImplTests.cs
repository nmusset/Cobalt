using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cobalt.Compiler.Tests.Emit;

public class ILEmitterImplTests
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

    // ══════════════════════════════════════════════
    // ImplBlock — interface addition + method bodies
    // ══════════════════════════════════════════════

    [Fact]
    public void Emit_ImplBlock_AddsInterface()
    {
        var asm = Emit("""
            trait Greetable
            {
                public string Greet();
            }
            class Person
            {
                public string name;
            }
            impl Greetable for Person
            {
                public string Greet()
                {
                    return "hello";
                }
            }
            """);
        var person = GetType(asm, "Person");
        Assert.Contains(person.Interfaces, i => i.InterfaceType.Name == "Greetable");
        var greet = GetMethod(person, "Greet");
        Assert.NotNull(greet);
    }

    [Fact]
    public void Emit_ImplBlock_MethodHasBody()
    {
        var asm = Emit("""
            trait Countable
            {
                public int Count();
            }
            class Bag
            {
                public int size;
            }
            impl Countable for Bag
            {
                public int Count()
                {
                    return 42;
                }
            }
            """);
        var bag = GetType(asm, "Bag");
        var count = GetMethod(bag, "Count");
        Assert.True(HasOpCode(count, OpCodes.Ldc_I4));
        Assert.True(HasOpCode(count, OpCodes.Ret));
    }
}

using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Emit;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cobalt.Compiler.Tests.Emit;

public class ILEmitterTests
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

    private static FieldDefinition GetField(TypeDefinition type, string name) =>
        type.Fields.First(f => f.Name == name);

    private static IList<Instruction> Instructions(MethodDefinition method) =>
        method.Body.Instructions;

    private static bool HasOpCode(MethodDefinition method, OpCode opCode) =>
        method.Body.Instructions.Any(i => i.OpCode == opCode);

    // ══════════════════════════════════════════════
    // Pass 1 — Type declarations
    // ══════════════════════════════════════════════

    // ──────────────────────────────────────────────
    // 1. Public class declaration
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_PublicClass_CreatesTypeDefinition()
    {
        var asm = Emit("public class Foo { }");
        var type = GetType(asm, "Foo");
        Assert.True(type.IsPublic);
        Assert.True(type.IsClass);
        Assert.Equal("System.Object", type.BaseType.FullName);
    }

    // ──────────────────────────────────────────────
    // 2. Sealed class
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_SealedClass_HasSealedAttribute()
    {
        var asm = Emit("public sealed class Bar { }");
        var type = GetType(asm, "Bar");
        Assert.True(type.IsSealed);
    }

    // ──────────────────────────────────────────────
    // 3. Abstract class
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AbstractClass_HasAbstractAttribute()
    {
        var asm = Emit("public abstract class Base { }");
        var type = GetType(asm, "Base");
        Assert.True(type.IsAbstract);
    }

    // ──────────────────────────────────────────────
    // 4. Trait → interface
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Trait_CreatesInterface()
    {
        var asm = Emit("""
            trait Drawable
            {
                public void Draw();
            }
            """);
        var type = GetType(asm, "Drawable");
        Assert.True(type.IsInterface);
        Assert.True(type.IsAbstract);
    }

    // ──────────────────────────────────────────────
    // 5. Union → abstract base + nested variants
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Union_CreatesAbstractBaseWithNestedVariants()
    {
        var asm = Emit("""
            public union Shape
            {
                Circle(int radius),
                Rect(int w, int h),
            }
            """);
        var baseType = GetType(asm, "Shape");
        Assert.True(baseType.IsAbstract);
        Assert.True(baseType.IsClass);

        // Private constructor on base
        var baseCtor = baseType.Methods.FirstOrDefault(m => m.Name == ".ctor");
        Assert.NotNull(baseCtor);
        Assert.True(baseCtor.IsPrivate);

        // Nested variant types
        var circle = baseType.NestedTypes.FirstOrDefault(t => t.Name == "Circle");
        Assert.NotNull(circle);
        Assert.True(circle.IsSealed);
        Assert.Equal(baseType, circle.BaseType);

        var rect = baseType.NestedTypes.FirstOrDefault(t => t.Name == "Rect");
        Assert.NotNull(rect);
        Assert.True(rect.IsSealed);
    }

    // ──────────────────────────────────────────────
    // 6. Union variant fields
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_UnionVariant_HasFieldsAndConstructor()
    {
        var asm = Emit("""
            public union Color
            {
                Rgb(int r, int g, int b),
                Named(string name),
            }
            """);
        var baseType = GetType(asm, "Color");
        var rgb = baseType.NestedTypes.First(t => t.Name == "Rgb");

        // Fields
        Assert.Equal(3, rgb.Fields.Count);
        Assert.Contains(rgb.Fields, f => f.Name == "r");
        Assert.Contains(rgb.Fields, f => f.Name == "g");
        Assert.Contains(rgb.Fields, f => f.Name == "b");

        // Constructor takes 3 params
        var ctor = rgb.Methods.First(m => m.Name == ".ctor");
        Assert.Equal(3, ctor.Parameters.Count);

        // Named variant
        var named = baseType.NestedTypes.First(t => t.Name == "Named");
        Assert.Single(named.Fields);
        Assert.Equal("name", named.Fields[0].Name);
    }

    // ──────────────────────────────────────────────
    // 7. Namespace propagation
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Namespace_SetsTypeNamespace()
    {
        var asm = Emit("""
            namespace MyApp;
            public class Widget { }
            """);
        var type = GetType(asm, "Widget");
        Assert.Equal("MyApp", type.Namespace);
    }

    // ──────────────────────────────────────────────
    // 8. Top-level method creates synthetic class
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_TopLevelMethod_CreatesSyntheticClass()
    {
        var asm = Emit("""
            public void Main()
            {
                return;
            }
            """);
        var synth = asm.MainModule.Types.FirstOrDefault(t => t.Name == "<TopLevel>");
        Assert.NotNull(synth);
        Assert.True(synth.IsSealed);
        var main = GetMethod(synth, "Main");
        Assert.NotNull(main);
    }

    // ══════════════════════════════════════════════
    // Pass 2 — Member signatures
    // ══════════════════════════════════════════════

    // ──────────────────────────────────────────────
    // 9. Field emission
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Field_CreatesFieldDefinition()
    {
        var asm = Emit("""
            public class Pair
            {
                public int x;
                private string y;
            }
            """);
        var type = GetType(asm, "Pair");
        var x = GetField(type, "x");
        Assert.True(x.IsPublic);
        Assert.Equal("System.Int32", x.FieldType.FullName);

        var y = GetField(type, "y");
        Assert.True(y.IsPrivate);
        Assert.Equal("System.String", y.FieldType.FullName);
    }

    // ──────────────────────────────────────────────
    // 10. Property with getter and setter
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Property_CreatesGetterAndSetter()
    {
        var asm = Emit("""
            public class Box
            {
                public int Value { get; set; }
            }
            """);
        var type = GetType(asm, "Box");
        var prop = type.Properties.First(p => p.Name == "Value");
        Assert.NotNull(prop.GetMethod);
        Assert.NotNull(prop.SetMethod);
        Assert.Equal("System.Int32", prop.PropertyType.FullName);
    }

    // ──────────────────────────────────────────────
    // 11. Property with getter only
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_GetterOnlyProperty_HasNoSetter()
    {
        var asm = Emit("""
            public class ReadOnly
            {
                public string Name { get; }
            }
            """);
        var type = GetType(asm, "ReadOnly");
        var prop = type.Properties.First(p => p.Name == "Name");
        Assert.NotNull(prop.GetMethod);
        Assert.Null(prop.SetMethod);
    }

    // ──────────────────────────────────────────────
    // 12. Method with parameters
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_MethodWithParams_CreatesCorrectSignature()
    {
        var asm = Emit("""
            public class Calc
            {
                public int Add(int a, int b)
                {
                    return 0;
                }
            }
            """);
        var type = GetType(asm, "Calc");
        var method = GetMethod(type, "Add");
        Assert.Equal("System.Int32", method.ReturnType.FullName);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Equal("a", method.Parameters[0].Name);
        Assert.Equal("b", method.Parameters[1].Name);
    }

    // ──────────────────────────────────────────────
    // 13. Static method
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_StaticMethod_HasStaticAttribute()
    {
        var asm = Emit("""
            public class Utils
            {
                public static int Zero()
                {
                    return 0;
                }
            }
            """);
        var type = GetType(asm, "Utils");
        var method = GetMethod(type, "Zero");
        Assert.True(method.IsStatic);
    }

    // ──────────────────────────────────────────────
    // 14. Abstract method has no body
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AbstractMethod_HasNoBody()
    {
        var asm = Emit("""
            public abstract class Animal
            {
                public abstract void Speak();
            }
            """);
        var type = GetType(asm, "Animal");
        var method = GetMethod(type, "Speak");
        Assert.True(method.IsAbstract);
        Assert.True(method.IsVirtual);
        // Abstract methods should not have a body with instructions
        Assert.Null(method.Body);
    }

    // ──────────────────────────────────────────────
    // 15. Constructor with parameters
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Constructor_CreatesCtorWithParams()
    {
        var asm = Emit("""
            public class Point
            {
                public int x;
                public int y;

                public Point(int x, int y)
                {
                    this.x = x;
                }
            }
            """);
        var type = GetType(asm, "Point");
        var ctor = type.Methods.First(m => m.Name == ".ctor" && m.Parameters.Count == 2);
        Assert.Equal("x", ctor.Parameters[0].Name);
        Assert.Equal("y", ctor.Parameters[1].Name);
    }

    // ──────────────────────────────────────────────
    // 16. Default constructor added when none declared
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ClassWithoutCtor_GetsDefaultCtor()
    {
        var asm = Emit("public class Empty { }");
        var type = GetType(asm, "Empty");
        var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor");
        Assert.NotNull(ctor);
        Assert.Empty(ctor.Parameters);
    }

    // ──────────────────────────────────────────────
    // 17. Trait method → abstract virtual
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_TraitMethod_IsAbstractVirtual()
    {
        var asm = Emit("""
            trait Printable
            {
                public string Format();
            }
            """);
        var type = GetType(asm, "Printable");
        var method = GetMethod(type, "Format");
        Assert.True(method.IsAbstract);
        Assert.True(method.IsVirtual);
        Assert.Equal("System.String", method.ReturnType.FullName);
    }

    // ──────────────────────────────────────────────
    // 18. Class implementing trait
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ClassWithTrait_HasInterfaceImplementation()
    {
        var asm = Emit("""
            trait Greetable
            {
                public string Greet();
            }

            public class Person : Greetable
            {
                public string Greet()
                {
                    return "hello";
                }
            }
            """);
        var person = GetType(asm, "Person");
        Assert.Contains(person.Interfaces, i => i.InterfaceType.Name == "Greetable");
    }

    // ══════════════════════════════════════════════
    // Pass 3 — Statement emission
    // ══════════════════════════════════════════════

    // ──────────────────────────────────────────────
    // 19. Variable declaration with initializer
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_VarDecl_CreatesLocalAndStoresValue()
    {
        var asm = Emit("""
            public class Vars
            {
                public void Test()
                {
                    var x = 42;
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Vars"), "Test");
        Assert.True(method.Body.Variables.Count >= 1);
        Assert.True(HasOpCode(method, OpCodes.Ldc_I4));
        Assert.True(HasOpCode(method, OpCodes.Stloc));
    }

    // ──────────────────────────────────────────────
    // 20. Return statement with expression
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ReturnExpr_EmitsRetOpCode()
    {
        var asm = Emit("""
            public class Ret
            {
                public int Get()
                {
                    return 7;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Ret"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldc_I4));
        Assert.True(HasOpCode(method, OpCodes.Ret));
    }

    // ──────────────────────────────────────────────
    // 21. If statement (then only)
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_IfStatement_EmitsBrfalse()
    {
        var asm = Emit("""
            public class Ifs
            {
                public void Check(bool cond)
                {
                    if (cond)
                    {
                        var x = 1;
                    }
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Ifs"), "Check");
        Assert.True(HasOpCode(method, OpCodes.Brfalse));
    }

    // ──────────────────────────────────────────────
    // 22. If-else statement
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_IfElse_EmitsBranchAndJump()
    {
        var asm = Emit("""
            public class IfElse
            {
                public int Pick(bool flag)
                {
                    if (flag)
                    {
                        return 1;
                    }
                    else
                    {
                        return 2;
                    }
                }
            }
            """);
        var method = GetMethod(GetType(asm, "IfElse"), "Pick");
        Assert.True(HasOpCode(method, OpCodes.Brfalse));
        // Else branch uses unconditional jump to skip over
        Assert.True(HasOpCode(method, OpCodes.Br));
    }

    // ──────────────────────────────────────────────
    // 23. While loop
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_WhileLoop_EmitsConditionAndBranch()
    {
        var asm = Emit("""
            public class Loops
            {
                public void Loop(bool go)
                {
                    while (go)
                    {
                        var x = 1;
                    }
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Loops"), "Loop");
        // While has: brfalse (exit), br (back to condition)
        Assert.True(HasOpCode(method, OpCodes.Brfalse));
        Assert.True(HasOpCode(method, OpCodes.Br));
    }

    // ──────────────────────────────────────────────
    // 24. For loop
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ForLoop_EmitsInitCondIncrBody()
    {
        var asm = Emit("""
            public class ForTest
            {
                public void Count()
                {
                    for (var i = 0; i < 10; i++)
                    {
                        var x = i;
                    }
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "ForTest"), "Count");
        // Init: Ldc_I4 0; Cond: Clt → Brfalse; Incr: something; Br back
        Assert.True(HasOpCode(method, OpCodes.Brfalse));
        Assert.True(HasOpCode(method, OpCodes.Br));
        Assert.True(HasOpCode(method, OpCodes.Clt));
    }

    // ──────────────────────────────────────────────
    // 25. Using var declaration
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_UsingVar_CreatesLocalVariable()
    {
        var asm = Emit("""
            public class UsingTest
            {
                public void Test()
                {
                    using var s = "hello";
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "UsingTest"), "Test");
        Assert.True(method.Body.Variables.Count >= 1);
        Assert.True(HasOpCode(method, OpCodes.Stloc));
    }

    // ──────────────────────────────────────────────
    // 26. Expression statement pops non-void result
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ExpressionStatement_PopsNonVoidResult()
    {
        var asm = Emit("""
            public class ExprStmt
            {
                public int GetVal() { return 1; }
                public void Run()
                {
                    GetVal();
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "ExprStmt"), "Run");
        Assert.True(HasOpCode(method, OpCodes.Pop));
    }

    // ──────────────────────────────────────────────
    // 27. Nested block statement
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_NestedBlock_EmitsInnerStatements()
    {
        var asm = Emit("""
            public class Blocks
            {
                public int Test()
                {
                    {
                        var x = 5;
                    }
                    return 0;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Blocks"), "Test");
        Assert.True(method.Body.Variables.Count >= 1);
    }

    // ──────────────────────────────────────────────
    // 28. ForEach emits placeholder (pop + nop)
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ForEach_EmitsIEnumeratorPattern()
    {
        var asm = Emit("""
            public class Each
            {
                public void Iter(string items)
                {
                    foreach (var item in items)
                    {
                        var x = 1;
                    }
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Each"), "Iter");
        // ForEach emits IEnumerator pattern: Callvirt (GetEnumerator, get_Current, MoveNext) and Brtrue
        Assert.True(HasOpCode(method, OpCodes.Callvirt));
        Assert.True(HasOpCode(method, OpCodes.Brtrue));
    }

    // ══════════════════════════════════════════════
    // Pass 3 — Expression emission
    // ══════════════════════════════════════════════

    // ──────────────────────────────────────────────
    // 29. Integer literal
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_IntLiteral_EmitsLdcI4()
    {
        var asm = Emit("""
            public class Lit
            {
                public int Get() { return 42; }
            }
            """);
        var method = GetMethod(GetType(asm, "Lit"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldc_I4));
    }

    // ──────────────────────────────────────────────
    // 30. String literal
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_StringLiteral_EmitsLdstr()
    {
        var asm = Emit("""
            public class StrLit
            {
                public string Get() { return "hello"; }
            }
            """);
        var method = GetMethod(GetType(asm, "StrLit"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldstr));
        var ldstr = Instructions(method).First(i => i.OpCode == OpCodes.Ldstr);
        Assert.Equal("hello", ldstr.Operand);
    }

    // ──────────────────────────────────────────────
    // 31. Bool literal
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_BoolLiteral_EmitsLdcI4()
    {
        var asm = Emit("""
            public class BoolLit
            {
                public bool GetTrue() { return true; }
                public bool GetFalse() { return false; }
            }
            """);
        var type = GetType(asm, "BoolLit");
        var getTrue = GetMethod(type, "GetTrue");
        Assert.True(HasOpCode(getTrue, OpCodes.Ldc_I4_1));

        var getFalse = GetMethod(type, "GetFalse");
        Assert.True(HasOpCode(getFalse, OpCodes.Ldc_I4_0));
    }

    // ──────────────────────────────────────────────
    // 32. Null literal
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_NullLiteral_EmitsLdnull()
    {
        var asm = Emit("""
            public class NullLit
            {
                public object Get() { return null; }
            }
            """);
        var method = GetMethod(GetType(asm, "NullLit"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldnull));
    }

    // ──────────────────────────────────────────────
    // 33. Identifier — load local variable
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Identifier_LoadsLocal()
    {
        var asm = Emit("""
            public class Ident
            {
                public int Test()
                {
                    var x = 10;
                    return x;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Ident"), "Test");
        Assert.True(HasOpCode(method, OpCodes.Ldloc));
    }

    // ──────────────────────────────────────────────
    // 34. Identifier — load parameter
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Identifier_LoadsParam()
    {
        var asm = Emit("""
            public class Params
            {
                public int Echo(int val)
                {
                    return val;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Params"), "Echo");
        Assert.True(HasOpCode(method, OpCodes.Ldarg));
    }

    // ──────────────────────────────────────────────
    // 35. Identifier — load field via this
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Identifier_LoadsField()
    {
        var asm = Emit("""
            public class FieldLoad
            {
                public int value;
                public int Get()
                {
                    return value;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "FieldLoad"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldarg_0)); // load this
        Assert.True(HasOpCode(method, OpCodes.Ldfld));
    }

    // ──────────────────────────────────────────────
    // 36. Binary arithmetic operators
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_BinaryAdd_EmitsAddOpCode()
    {
        var asm = Emit("""
            public class Math
            {
                public int Add(int a, int b) { return a + b; }
                public int Sub(int a, int b) { return a - b; }
                public int Mul(int a, int b) { return a * b; }
                public int Div(int a, int b) { return a / b; }
            }
            """);
        var type = GetType(asm, "Math");
        Assert.True(HasOpCode(GetMethod(type, "Add"), OpCodes.Add));
        Assert.True(HasOpCode(GetMethod(type, "Sub"), OpCodes.Sub));
        Assert.True(HasOpCode(GetMethod(type, "Mul"), OpCodes.Mul));
        Assert.True(HasOpCode(GetMethod(type, "Div"), OpCodes.Div));
    }

    // ──────────────────────────────────────────────
    // 37. Binary comparison operators
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_BinaryComparisons_EmitCorrectOpCodes()
    {
        var asm = Emit("""
            public class Cmp
            {
                public bool Eq(int a, int b) { return a == b; }
                public bool Lt(int a, int b) { return a < b; }
                public bool Gt(int a, int b) { return a > b; }
            }
            """);
        var type = GetType(asm, "Cmp");
        Assert.True(HasOpCode(GetMethod(type, "Eq"), OpCodes.Ceq));
        Assert.True(HasOpCode(GetMethod(type, "Lt"), OpCodes.Clt));
        Assert.True(HasOpCode(GetMethod(type, "Gt"), OpCodes.Cgt));
    }

    // ──────────────────────────────────────────────
    // 38. Unary negation
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_UnaryNeg_EmitsNegOpCode()
    {
        var asm = Emit("""
            public class Neg
            {
                public int Negate(int x) { return -x; }
            }
            """);
        var method = GetMethod(GetType(asm, "Neg"), "Negate");
        Assert.True(HasOpCode(method, OpCodes.Neg));
    }

    // ──────────────────────────────────────────────
    // 39. Unary logical not
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_UnaryNot_EmitsCeqWithZero()
    {
        var asm = Emit("""
            public class Not
            {
                public bool Invert(bool x) { return !x; }
            }
            """);
        var method = GetMethod(GetType(asm, "Not"), "Invert");
        Assert.True(HasOpCode(method, OpCodes.Ldc_I4_0));
        Assert.True(HasOpCode(method, OpCodes.Ceq));
    }

    // ──────────────────────────────────────────────
    // 40. Member access — field
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_MemberAccess_EmitsLdfld()
    {
        var asm = Emit("""
            public class Outer
            {
                public int value;
                public int GetFrom(Outer other)
                {
                    return other.value;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Outer"), "GetFrom");
        Assert.True(HasOpCode(method, OpCodes.Ldfld));
    }

    // ──────────────────────────────────────────────
    // 41. Method invocation — same class
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_InvocationSameClass_EmitsCallOrCallvirt()
    {
        var asm = Emit("""
            public class Caller
            {
                public int Helper() { return 1; }
                public int Run()
                {
                    return Helper();
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Caller"), "Run");
        var hasCall = HasOpCode(method, OpCodes.Call) || HasOpCode(method, OpCodes.Callvirt);
        Assert.True(hasCall);
    }

    // ──────────────────────────────────────────────
    // 42. Object creation — new with constructor
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ObjectCreation_EmitsNewobj()
    {
        var asm = Emit("""
            public class Item { }
            public class Factory
            {
                public Item Make()
                {
                    return new Item();
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Factory"), "Make");
        Assert.True(HasOpCode(method, OpCodes.Newobj));
    }

    // ──────────────────────────────────────────────
    // 43. Assignment to local variable
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AssignmentLocal_EmitsStloc()
    {
        var asm = Emit("""
            public class Assign
            {
                public int Test()
                {
                    var x = 1;
                    x = 2;
                    return x;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Assign"), "Test");
        // Should have Stloc for both the initial and the reassignment
        var stlocs = Instructions(method).Where(i => i.OpCode == OpCodes.Stloc).ToList();
        Assert.True(stlocs.Count >= 2);
    }

    // ──────────────────────────────────────────────
    // 44. Assignment to field
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AssignmentField_EmitsStfld()
    {
        var asm = Emit("""
            public class FieldAssign
            {
                public int val;
                public void Set()
                {
                    val = 99;
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "FieldAssign"), "Set");
        Assert.True(HasOpCode(method, OpCodes.Stfld));
    }

    // ──────────────────────────────────────────────
    // 45. This expression
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ThisExpr_EmitsLdarg0()
    {
        var asm = Emit("""
            public class Self
            {
                public Self GetSelf()
                {
                    return this;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Self"), "GetSelf");
        Assert.True(HasOpCode(method, OpCodes.Ldarg_0));
    }

    // ──────────────────────────────────────────────
    // 46. Own expression is transparent
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_OwnExpr_IsTransparent()
    {
        var asm = Emit("""
            public class OwnTest
            {
                public void Take(own string s) { }
                public void Give(own string input)
                {
                    Take(own input);
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "OwnTest"), "Give");
        // own is compile-time only — should just load the inner identifier
        Assert.True(HasOpCode(method, OpCodes.Ldarg));
    }

    // ──────────────────────────────────────────────
    // 47. Interpolated string — two parts
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_InterpolatedString_EmitsLdstrAndConcat()
    {
        var asm = Emit("""
            public class Interp
            {
                public string Greet(string name)
                {
                    return $"Hello {name}";
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Interp"), "Greet");
        Assert.True(HasOpCode(method, OpCodes.Ldstr));
        Assert.True(HasOpCode(method, OpCodes.Call));
    }

    // ──────────────────────────────────────────────
    // 48. LessEquals / GreaterEquals comparison
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_LessEqualsGreaterEquals_EmitCorrectOpCodes()
    {
        var asm = Emit("""
            public class CmpEq
            {
                public bool Le(int a, int b) { return a <= b; }
                public bool Ge(int a, int b) { return a >= b; }
            }
            """);
        var type = GetType(asm, "CmpEq");
        // a <= b → cgt + ldc.i4.0 + ceq (negate gt)
        Assert.True(HasOpCode(GetMethod(type, "Le"), OpCodes.Cgt));
        Assert.True(HasOpCode(GetMethod(type, "Le"), OpCodes.Ceq));
        // a >= b → clt + ldc.i4.0 + ceq (negate lt)
        Assert.True(HasOpCode(GetMethod(type, "Ge"), OpCodes.Clt));
        Assert.True(HasOpCode(GetMethod(type, "Ge"), OpCodes.Ceq));
    }

    // ──────────────────────────────────────────────
    // 49. Is-pattern expression emits true placeholder
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_IsPattern_EmitsPlaceholder()
    {
        var asm = Emit("""
            public class IsTest
            {
                public bool Check(object o)
                {
                    return o is string s;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "IsTest"), "Check");
        // Placeholder: Ldc_I4_1 (true)
        Assert.True(HasOpCode(method, OpCodes.Ldc_I4_1));
    }

    // ──────────────────────────────────────────────
    // 50. Method body ends with Ret
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_MethodBody_AlwaysEndsWithRet()
    {
        var asm = Emit("""
            public class RetTest
            {
                public void NoExplicitReturn()
                {
                    var x = 1;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "RetTest"), "NoExplicitReturn");
        var lastInstr = Instructions(method).Last();
        Assert.Equal(OpCodes.Ret, lastInstr.OpCode);
    }

    // ──────────────────────────────────────────────
    // 51. Assembly name propagation
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AssemblyName_PropagatedCorrectly()
    {
        var asm = Emit("public class X { }", asmName: "MyLib");
        Assert.Equal("MyLib", asm.Name.Name);
        Assert.Equal(new Version(1, 0, 0, 0), asm.Name.Version);
    }

    // ──────────────────────────────────────────────
    // 52. Type resolution — built-in types
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_BuiltInTypes_ResolvedCorrectly()
    {
        var asm = Emit("""
            public class TypeRes
            {
                public int intField;
                public long longField;
                public bool boolField;
                public string strField;
                public double doubleField;
                public char charField;
            }
            """);
        var type = GetType(asm, "TypeRes");
        Assert.Equal("System.Int32", GetField(type, "intField").FieldType.FullName);
        Assert.Equal("System.Int64", GetField(type, "longField").FieldType.FullName);
        Assert.Equal("System.Boolean", GetField(type, "boolField").FieldType.FullName);
        Assert.Equal("System.String", GetField(type, "strField").FieldType.FullName);
        Assert.Equal("System.Double", GetField(type, "doubleField").FieldType.FullName);
        Assert.Equal("System.Char", GetField(type, "charField").FieldType.FullName);
    }

    // ──────────────────────────────────────────────
    // 53. Type resolution — user-defined type in field
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_UserDefinedType_ResolvedInField()
    {
        var asm = Emit("""
            public class Inner { }
            public class Wrapper
            {
                public Inner child;
            }
            """);
        var wrapper = GetType(asm, "Wrapper");
        var field = GetField(wrapper, "child");
        Assert.Equal("Inner", field.FieldType.Name);
    }

    // ──────────────────────────────────────────────
    // 54. Virtual method
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_VirtualMethod_HasVirtualAttribute()
    {
        var asm = Emit("""
            public class Base
            {
                public virtual int Compute() { return 0; }
            }
            """);
        var method = GetMethod(GetType(asm, "Base"), "Compute");
        Assert.True(method.IsVirtual);
    }

    // ──────────────────────────────────────────────
    // 55. Override method
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_OverrideMethod_HasVirtualAttribute()
    {
        var asm = Emit("""
            public abstract class Base
            {
                public abstract int Compute();
            }
            public class Derived : Base
            {
                public override int Compute() { return 1; }
            }
            """);
        var method = GetMethod(GetType(asm, "Derived"), "Compute");
        Assert.True(method.IsVirtual);
    }

    // ──────────────────────────────────────────────
    // 56. Constructor body calls base .ctor
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ConstructorBody_CallsBaseCtor()
    {
        var asm = Emit("""
            public class Obj
            {
                public Obj()
                {
                    var x = 1;
                }
            }
            """);
        var ctor = GetType(asm, "Obj").Methods.First(m => m.Name == ".ctor");
        // First instructions: ldarg.0, call .ctor
        var instrs = Instructions(ctor);
        Assert.Equal(OpCodes.Ldarg_0, instrs[0].OpCode);
        Assert.Equal(OpCodes.Call, instrs[1].OpCode);
        var calledMethod = (MethodReference)instrs[1].Operand;
        Assert.Equal(".ctor", calledMethod.Name);
    }

    // ──────────────────────────────────────────────
    // 57. Property expression body
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_PropertyExpressionBody_EmitsGetter()
    {
        var asm = Emit("""
            public class Prop
            {
                public string Name => "hello";
            }
            """);
        var type = GetType(asm, "Prop");
        var prop = type.Properties.First(p => p.Name == "Name");
        Assert.NotNull(prop.GetMethod);
        Assert.True(HasOpCode(prop.GetMethod, OpCodes.Ldstr));
        Assert.True(HasOpCode(prop.GetMethod, OpCodes.Ret));
    }

    // ──────────────────────────────────────────────
    // 58. Trait method with parameters
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_TraitMethodWithParams_HasCorrectSignature()
    {
        var asm = Emit("""
            trait Converter
            {
                public string Convert(int value);
            }
            """);
        var type = GetType(asm, "Converter");
        var method = GetMethod(type, "Convert");
        Assert.True(method.IsAbstract);
        Assert.Equal(1, method.Parameters.Count);
        Assert.Equal("value", method.Parameters[0].Name);
    }

    // ──────────────────────────────────────────────
    // 59. BangEquals operator
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_BangEquals_EmitsCeqThenNot()
    {
        var asm = Emit("""
            public class Ne
            {
                public bool NotEq(int a, int b) { return a != b; }
            }
            """);
        var method = GetMethod(GetType(asm, "Ne"), "NotEq");
        // != is: Ceq, Ldc_I4_0, Ceq
        var instrs = Instructions(method);
        var ceqIndices = instrs
            .Select((instr, idx) => (instr, idx))
            .Where(x => x.instr.OpCode == OpCodes.Ceq)
            .Select(x => x.idx)
            .ToList();
        Assert.True(ceqIndices.Count >= 2);
    }

    // ──────────────────────────────────────────────
    // 60. Object creation with initializer clauses
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ObjectCreationWithInitializer_SetsFields()
    {
        var asm = Emit("""
            public class Pt
            {
                public int x;
                public int y;
            }
            public class Builder
            {
                public Pt Make()
                {
                    return new Pt { x = 1, y = 2 };
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Builder"), "Make");
        Assert.True(HasOpCode(method, OpCodes.Newobj));
        Assert.True(HasOpCode(method, OpCodes.Stfld));
    }

    // ──────────────────────────────────────────────
    // 61. Void method without explicit return gets Ret
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_VoidMethodNoReturn_ImplicitRet()
    {
        var asm = Emit("""
            public class Impl
            {
                public void Noop() { }
            }
            """);
        var method = GetMethod(GetType(asm, "Impl"), "Noop");
        Assert.Equal(OpCodes.Ret, Instructions(method).Last().OpCode);
    }

    // ──────────────────────────────────────────────
    // 62. Trait property with getter
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_TraitProperty_IsAbstract()
    {
        var asm = Emit("""
            trait HasName
            {
                public string Name { get; }
            }
            """);
        var type = GetType(asm, "HasName");
        var prop = type.Properties.First(p => p.Name == "Name");
        Assert.NotNull(prop.GetMethod);
        Assert.True(prop.GetMethod.IsAbstract);
    }

    // ──────────────────────────────────────────────
    // 63. Static method identifier call
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_StaticMethodCall_DoesNotPushThis()
    {
        var asm = Emit("""
            public class Statics
            {
                public static int Value() { return 42; }
                public static int Wrapper()
                {
                    return Value();
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Statics"), "Wrapper");
        // Static call should use Call not Callvirt, and not push this first
        Assert.True(HasOpCode(method, OpCodes.Call));
    }

    // ──────────────────────────────────────────────
    // 64. Multiple classes in one compilation unit
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_MultipleClasses_AllDeclared()
    {
        var asm = Emit("""
            public class A { }
            public class B { }
            public class C { }
            """);
        var names = asm.MainModule.Types.Select(t => t.Name).ToList();
        Assert.Contains("A", names);
        Assert.Contains("B", names);
        Assert.Contains("C", names);
    }

    // ──────────────────────────────────────────────
    // 65. Float literal
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_FloatLiteral_EmitsLdcR8()
    {
        var asm = Emit("""
            public class FloatLit
            {
                public double Get() { return 3.14; }
            }
            """);
        var method = GetMethod(GetType(asm, "FloatLit"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldc_R8));
    }

    // ──────────────────────────────────────────────
    // 66. Char literal
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_CharLiteral_EmitsLdcI4()
    {
        var asm = Emit("""
            public class CharLit
            {
                public char Get() { return 'A'; }
            }
            """);
        var method = GetMethod(GetType(asm, "CharLit"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldc_I4));
    }

    // ──────────────────────────────────────────────
    // 67. Assignment to parameter
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AssignmentParam_EmitsStarg()
    {
        var asm = Emit("""
            public class ParamAssign
            {
                public int Mutate(int x)
                {
                    x = 5;
                    return x;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "ParamAssign"), "Mutate");
        Assert.True(HasOpCode(method, OpCodes.Starg));
    }

    // ──────────────────────────────────────────────
    // 68. Match statement emits Nop placeholder
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_MatchStatement_EmitsNop()
    {
        var asm = Emit("""
            public class MatchTest
            {
                public void Check(int x)
                {
                    match (x)
                    {
                        var n => {
                            var y = 1;
                        },
                    };
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "MatchTest"), "Check");
        Assert.True(HasOpCode(method, OpCodes.Nop));
    }

    // ──────────────────────────────────────────────
    // 69. Index expression emits Ldelem_Ref
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_IndexExpr_EmitsLdelemRef()
    {
        var asm = Emit("""
            public class Indexing
            {
                public object Get(object target, int idx)
                {
                    return target[idx];
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Indexing"), "Get");
        Assert.True(HasOpCode(method, OpCodes.Ldelem_Ref));
    }

    // ──────────────────────────────────────────────
    // 70. Percent (modulo) operator
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ModuloOperator_EmitsRem()
    {
        var asm = Emit("""
            public class Mod
            {
                public int Remainder(int a, int b) { return a % b; }
            }
            """);
        var method = GetMethod(GetType(asm, "Mod"), "Remainder");
        Assert.True(HasOpCode(method, OpCodes.Rem));
    }

    // ══════════════════════════════════════════════
    // Emitter correctness fixes
    // ══════════════════════════════════════════════

    // ──────────────────────────────────────────────
    // 71. Short-circuit && emits Brfalse
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_LogicalAnd_ShortCircuits()
    {
        var asm = Emit("""
            public class Logic
            {
                public bool Both(bool a, bool b) { return a && b; }
            }
            """);
        var method = GetMethod(GetType(asm, "Logic"), "Both");
        Assert.True(HasOpCode(method, OpCodes.Brfalse));
    }

    // ──────────────────────────────────────────────
    // 72. Short-circuit || emits Brtrue
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_LogicalOr_ShortCircuits()
    {
        var asm = Emit("""
            public class Logic
            {
                public bool Either(bool a, bool b) { return a || b; }
            }
            """);
        var method = GetMethod(GetType(asm, "Logic"), "Either");
        Assert.True(HasOpCode(method, OpCodes.Brtrue));
    }

    // ──────────────────────────────────────────────
    // 73. Interpolated string with 3+ parts uses chained Concat
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_InterpolatedString3Parts_ChainsConcat()
    {
        var asm = Emit("""
            public class Interp3
            {
                public string Build(string a, string b)
                {
                    return $"{a} and {b}";
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Interp3"), "Build");
        // 3 parts: insertion, text, insertion → 2 chained Concat calls
        var callCount = Instructions(method).Count(i => i.OpCode == OpCodes.Call);
        Assert.True(callCount >= 2);
    }

    // ──────────────────────────────────────────────
    // 74. Assignment to member access emits Stfld
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_AssignmentMemberAccess_EmitsStfld()
    {
        var asm = Emit("""
            public class Pt
            {
                public int x;
                public int y;
            }
            public class Setter
            {
                public void Set(Pt p)
                {
                    p.x = 5;
                    return;
                }
            }
            """);
        var method = GetMethod(GetType(asm, "Setter"), "Set");
        Assert.True(HasOpCode(method, OpCodes.Stfld));
    }

    // ──────────────────────────────────────────────
    // 75. Trait method with params (parser fix enables this)
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_TraitWithImplClass_InterfaceAdded()
    {
        var asm = Emit("""
            trait Runnable
            {
                public void Run();
            }
            public class Job : Runnable
            {
                public void Run()
                {
                    return;
                }
            }
            """);
        var job = GetType(asm, "Job");
        Assert.Contains(job.Interfaces, i => i.InterfaceType.Name == "Runnable");
        var run = GetMethod(job, "Run");
        Assert.Equal(OpCodes.Ret, Instructions(run).Last().OpCode);
    }
}

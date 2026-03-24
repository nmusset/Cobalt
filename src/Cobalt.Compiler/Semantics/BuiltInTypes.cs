namespace Cobalt.Compiler.Semantics;

using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Syntax;

public static class BuiltInTypes
{
    private static readonly SourceSpan BuiltInSpan = new(new SourceLocation("built-in", 0, 0),
        new SourceLocation("built-in", 0, 0));

    public static TypeSymbol Void { get; } = MakeBuiltIn("void", TypeKind.Void);
    public static TypeSymbol Int { get; } = MakeBuiltIn("int", TypeKind.BuiltIn);
    public static TypeSymbol Long { get; } = MakeBuiltIn("long", TypeKind.BuiltIn);
    public static TypeSymbol Float { get; } = MakeBuiltIn("float", TypeKind.BuiltIn);
    public static TypeSymbol Double { get; } = MakeBuiltIn("double", TypeKind.BuiltIn);
    public static TypeSymbol Bool { get; } = MakeBuiltIn("bool", TypeKind.BuiltIn);
    public static TypeSymbol String { get; } = MakeBuiltIn("string", TypeKind.BuiltIn);
    public static TypeSymbol Char { get; } = MakeBuiltIn("char", TypeKind.BuiltIn);
    public static TypeSymbol Object { get; } = MakeBuiltIn("object", TypeKind.BuiltIn);
    public static TypeSymbol Error { get; } = MakeBuiltIn("<error>", TypeKind.Error);

    // Well-known .NET types used in samples
    public static TypeSymbol Stream { get; } = MakeDotNet("Stream");
    public static TypeSymbol StreamReader { get; } = MakeDotNet("StreamReader");
    public static TypeSymbol StreamWriter { get; } = MakeDotNet("StreamWriter");
    public static TypeSymbol Console { get; } = MakeDotNet("Console");
    public static TypeSymbol File { get; } = MakeDotNet("File");
    public static TypeSymbol IDisposable { get; } = MakeDotNet("IDisposable");

    // Generic well-known types (represented without type args, resolved during binding)
    public static TypeSymbol List { get; } = MakeDotNet("List");

    // Cobalt built-ins
    public static TypeSymbol Option { get; } = MakeBuiltIn("Option", TypeKind.Union);
    public static TypeSymbol Result { get; } = MakeBuiltIn("Result", TypeKind.Union);

    private static TypeSymbol MakeBuiltIn(string name, TypeKind kind) =>
        new(name, kind, AccessModifier.Public, BuiltInSpan) { IsBuiltIn = true };

    private static TypeSymbol MakeDotNet(string name) =>
        new(name, TypeKind.Class, AccessModifier.Public, BuiltInSpan) { IsBuiltIn = true, IsDotNetType = true };

    public static void RegisterAll(Scope scope)
    {
        // Built-in types
        scope.TryDeclare(Void);
        scope.TryDeclare(Int);
        scope.TryDeclare(Long);
        scope.TryDeclare(Float);
        scope.TryDeclare(Double);
        scope.TryDeclare(Bool);
        scope.TryDeclare(String);
        scope.TryDeclare(Char);
        scope.TryDeclare(Object);

        // .NET types
        scope.TryDeclare(Stream);
        scope.TryDeclare(StreamReader);
        scope.TryDeclare(StreamWriter);
        scope.TryDeclare(Console);
        scope.TryDeclare(File);
        scope.TryDeclare(IDisposable);
        scope.TryDeclare(List);

        // Cobalt types
        scope.TryDeclare(Option);
        scope.TryDeclare(Result);

        // Register well-known static methods
        RegisterConsoleMethods(scope);
        RegisterFileMethods(scope);
    }

    private static void RegisterConsoleMethods(Scope scope)
    {
        var writeLine = new MethodSymbol("WriteLine", AccessModifier.Public, true, false, false, false, BuiltInSpan)
        {
            ReturnType = Void,
            ContainingType = Console,
            Parameters = [new ParameterSymbol("value", OwnershipModifier.None, BuiltInSpan) { Type = String }],
        };
        Console.Methods = [writeLine];
    }

    private static void RegisterFileMethods(Scope scope)
    {
        var openRead = new MethodSymbol("OpenRead", AccessModifier.Public, true, false, false, false, BuiltInSpan)
        {
            ReturnType = Stream,
            ReturnOwnership = OwnershipModifier.Own,
            ContainingType = File,
            Parameters = [new ParameterSymbol("path", OwnershipModifier.None, BuiltInSpan) { Type = String }],
        };
        var create = new MethodSymbol("Create", AccessModifier.Public, true, false, false, false, BuiltInSpan)
        {
            ReturnType = Stream,
            ReturnOwnership = OwnershipModifier.Own,
            ContainingType = File,
            Parameters = [new ParameterSymbol("path", OwnershipModifier.None, BuiltInSpan) { Type = String }],
        };
        File.Methods = [openRead, create];
    }
}

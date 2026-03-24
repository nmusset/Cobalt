namespace Cobalt.Compiler.Dump;

using System.Text;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;

/// <summary>
/// Produces a text summary of the semantic scope — types, methods, fields, and their
/// resolved symbols. Used for debugging and --dump-semantic output.
/// </summary>
public class SemanticDumper
{
    private readonly StringBuilder _sb = new();
    private int _depth;

    public string Dump(Scope scope)
    {
        _sb.Clear();
        _depth = 0;
        DumpScope(scope);
        return _sb.ToString();
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private void Line(string text) =>
        _sb.AppendLine(new string(' ', _depth * 2) + text);

    private void WithIndent(Action body)
    {
        _depth++;
        body();
        _depth--;
    }

    private static string OwnerStr(OwnershipModifier m) => m switch
    {
        OwnershipModifier.Own => "own ",
        OwnershipModifier.Ref => "ref ",
        OwnershipModifier.RefMut => "ref mut ",
        _ => "",
    };

    // ──────────────────────────────────────────────
    // Scope
    // ──────────────────────────────────────────────

    private void DumpScope(Scope scope)
    {
        Line("Scope");
        WithIndent(() =>
        {
            foreach (var sym in scope.GetAllSymbols().OrderBy(s => s.Name))
                DumpSymbol(sym);
        });
    }

    private void DumpSymbol(Symbol sym)
    {
        switch (sym)
        {
            case TypeSymbol type:
                DumpType(type);
                break;
            case MethodSymbol method:
                DumpMethod(method, containingType: null);
                break;
            case NamespaceSymbol ns:
                Line($"namespace {ns.Name}");
                break;
        }
    }

    // ──────────────────────────────────────────────
    // Types
    // ──────────────────────────────────────────────

    private void DumpType(TypeSymbol type)
    {
        var kind = type.Kind.ToString().ToLower();
        var extras = new List<string>();
        if (type.IsBuiltIn) extras.Add("built-in");
        if (type.IsDotNetType) extras.Add(".NET");
        if (type.IsSealed) extras.Add("sealed");
        if (type.IsAbstract) extras.Add("abstract");
        var extrasStr = extras.Count > 0 ? $" [{string.Join(", ", extras)}]" : "";

        var bases = type.BaseTypes.Count > 0
            ? $" : {string.Join(", ", type.BaseTypes.Select(b => b.Name))}"
            : "";

        Line($"{kind} {type.Name}{bases}{extrasStr}");

        WithIndent(() =>
        {
            if (type.TypeParameters.Count > 0)
                Line($"type-params: {string.Join(", ", type.TypeParameters.Select(t => t.Name))}");

            foreach (var field in type.Fields)
                DumpField(field);

            foreach (var prop in type.Properties)
                DumpProperty(prop);

            foreach (var method in type.Methods)
                DumpMethod(method, type);

            if (type.Variants != null)
                foreach (var variant in type.Variants)
                    DumpVariant(variant);
        });
    }

    // ──────────────────────────────────────────────
    // Members
    // ──────────────────────────────────────────────

    private void DumpField(FieldSymbol field)
    {
        var own = OwnerStr(field.Ownership);
        var typeName = field.Type?.Name ?? "?";
        Line($"field {own}{typeName} {field.Name}");
    }

    private void DumpProperty(PropertySymbol prop)
    {
        var typeName = prop.Type?.Name ?? "?";
        var accessors = (prop.HasGetter ? "get " : "") + (prop.HasSetter ? "set " : "");
        Line($"property {typeName} {prop.Name} {{ {accessors}}}");
    }

    private void DumpMethod(MethodSymbol method, TypeSymbol? containingType)
    {
        var statics = method.IsStatic ? "static " : "";
        var returnOwn = OwnerStr(method.ReturnOwnership);
        var returnType = method.ReturnType?.Name ?? "?";
        var paramStr = string.Join(", ", method.Parameters.Select(p =>
        {
            var own = OwnerStr(p.Ownership);
            var typeName = p.Type?.Name ?? "?";
            return $"{own}{typeName} {p.Name}";
        }));
        Line($"method {statics}{returnOwn}{returnType} {method.Name}({paramStr})");
    }

    private void DumpVariant(UnionVariantSymbol variant)
    {
        var fields = variant.Fields.Count > 0
            ? $"({string.Join(", ", variant.Fields.Select(f => $"{f.Type?.Name ?? "?"} {f.Name}"))})"
            : "";
        Line($"variant {variant.Name}{fields}");
    }
}

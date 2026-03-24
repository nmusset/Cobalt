namespace Cobalt.Compiler.Semantics;

using Cobalt.Compiler.Syntax;
using Cobalt.Compiler.Diagnostics;

// Base symbol
public abstract class Symbol
{
    public string Name { get; }
    public SourceSpan Span { get; }
    protected Symbol(string name, SourceSpan span) { Name = name; Span = span; }
}

// Type symbol — represents a type (class, trait, union, built-in)
public class TypeSymbol : Symbol
{
    public TypeKind Kind { get; }
    public AccessModifier Access { get; }
    public bool IsSealed { get; init; }
    public bool IsAbstract { get; init; }
    public IReadOnlyList<TypeSymbol> BaseTypes { get; set; } = [];
    public IReadOnlyList<MethodSymbol> Methods { get; set; } = [];
    public IReadOnlyList<FieldSymbol> Fields { get; set; } = [];
    public IReadOnlyList<PropertySymbol> Properties { get; set; } = [];
    public IReadOnlyList<UnionVariantSymbol>? Variants { get; set; } // null if not a union
    public IReadOnlyList<TypeSymbol> TypeParameters { get; set; } = []; // for generic types
    public bool IsBuiltIn { get; init; }
    public bool IsDotNetType { get; init; } // implicit trust boundary

    public TypeSymbol(string name, TypeKind kind, AccessModifier access, SourceSpan span)
        : base(name, span)
    {
        Kind = kind;
        Access = access;
    }
}

public enum TypeKind
{
    Class,
    Trait,
    Union,
    BuiltIn,     // int, string, bool, etc.
    Void,
    TypeParameter,
    Error,       // error recovery
}

// Method symbol
public class MethodSymbol : Symbol
{
    public TypeSymbol ReturnType { get; set; } = null!;
    public OwnershipModifier ReturnOwnership { get; set; }
    public AccessModifier Access { get; }
    public bool IsStatic { get; }
    public bool IsAbstract { get; }
    public bool IsVirtual { get; }
    public bool IsOverride { get; }
    public TypeSymbol? ContainingType { get; set; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; set; } = [];

    public MethodSymbol(string name, AccessModifier access, bool isStatic,
        bool isAbstract, bool isVirtual, bool isOverride, SourceSpan span)
        : base(name, span)
    {
        Access = access;
        IsStatic = isStatic;
        IsAbstract = isAbstract;
        IsVirtual = isVirtual;
        IsOverride = isOverride;
    }
}

// Field symbol
public class FieldSymbol : Symbol
{
    public TypeSymbol Type { get; set; } = null!;
    public OwnershipModifier Ownership { get; }
    public AccessModifier Access { get; }
    public TypeSymbol? ContainingType { get; set; }

    public FieldSymbol(string name, OwnershipModifier ownership, AccessModifier access, SourceSpan span)
        : base(name, span) { Ownership = ownership; Access = access; }
}

// Property symbol
public class PropertySymbol : Symbol
{
    public TypeSymbol Type { get; set; } = null!;
    public AccessModifier Access { get; }
    public bool HasGetter { get; }
    public bool HasSetter { get; }
    public TypeSymbol? ContainingType { get; set; }

    public PropertySymbol(string name, AccessModifier access, bool hasGetter, bool hasSetter, SourceSpan span)
        : base(name, span) { Access = access; HasGetter = hasGetter; HasSetter = hasSetter; }
}

// Parameter symbol
public class ParameterSymbol : Symbol
{
    public TypeSymbol Type { get; set; } = null!;
    public OwnershipModifier Ownership { get; }

    public ParameterSymbol(string name, OwnershipModifier ownership, SourceSpan span)
        : base(name, span) { Ownership = ownership; }
}

// Local variable symbol
public class LocalSymbol : Symbol
{
    public TypeSymbol Type { get; set; } = null!;
    public bool IsUsingVar { get; }
    public OwnershipModifier Ownership { get; set; } // inferred: Own for ref types, None for value types

    public LocalSymbol(string name, bool isUsingVar, SourceSpan span)
        : base(name, span) { IsUsingVar = isUsingVar; }
}

// Union variant symbol
public class UnionVariantSymbol : Symbol
{
    public TypeSymbol ContainingUnion { get; }
    public IReadOnlyList<FieldSymbol> Fields { get; set; } = [];

    public UnionVariantSymbol(string name, TypeSymbol containingUnion, SourceSpan span)
        : base(name, span) { ContainingUnion = containingUnion; }
}

// Namespace symbol
public class NamespaceSymbol : Symbol
{
    public NamespaceSymbol(string name, SourceSpan span) : base(name, span) { }
}

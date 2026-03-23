using Microsoft.CodeAnalysis;

namespace Cobalt.Analyzers;

internal static class SymbolExtensions
{
    /// <summary>
    /// Checks whether the symbol has an attribute with the given fully-qualified metadata name.
    /// </summary>
    public static bool HasAttribute(this ISymbol symbol, INamedTypeSymbol? attributeType)
    {
        if (attributeType is null) return false;

        foreach (var attr in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the symbol's return value has an attribute with the given type.
    /// Applicable to methods and properties.
    /// </summary>
    public static bool HasReturnAttribute(this IMethodSymbol method, INamedTypeSymbol? attributeType)
    {
        if (attributeType is null) return false;

        foreach (var attr in method.GetReturnTypeAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the type implements <see cref="System.IDisposable"/>.
    /// </summary>
    public static bool ImplementsIDisposable(this ITypeSymbol type, INamedTypeSymbol? idisposable)
    {
        if (idisposable is null) return false;

        // Check if the type IS IDisposable itself.
        if (SymbolEqualityComparer.Default.Equals(type, idisposable))
            return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, idisposable))
                return true;
        }

        return false;
    }
}

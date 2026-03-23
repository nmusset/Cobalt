namespace Cobalt.Analyzers;

/// <summary>
/// Fully-qualified metadata names for Cobalt annotation attributes.
/// The analyzer discovers attributes by name rather than by project reference,
/// since analyzers target netstandard2.0 and cannot reference net10.0 libraries.
/// </summary>
internal static class AttributeNames
{
    public const string Owned = "Cobalt.Annotations.OwnedAttribute";
    public const string Borrowed = "Cobalt.Annotations.BorrowedAttribute";
    public const string MutBorrowed = "Cobalt.Annotations.MutBorrowedAttribute";
    public const string MustDispose = "Cobalt.Annotations.MustDisposeAttribute";
    public const string Scoped = "Cobalt.Annotations.ScopedAttribute";
    public const string NoAlias = "Cobalt.Annotations.NoAliasAttribute";
}

namespace Cobalt.Compiler.Diagnostics;

public readonly record struct SourceLocation(string FilePath, int Line, int Column)
{
    public override string ToString() => $"{FilePath}({Line},{Column})";
}

public readonly record struct SourceSpan(SourceLocation Start, SourceLocation End);

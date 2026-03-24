namespace Cobalt.Compiler.Semantics;

public class Scope
{
    private readonly Dictionary<string, Symbol> _symbols = new();
    public Scope? Parent { get; }

    public Scope(Scope? parent = null) { Parent = parent; }

    public bool TryDeclare(Symbol symbol)
    {
        return _symbols.TryAdd(symbol.Name, symbol);
    }

    public Symbol? Lookup(string name)
    {
        if (_symbols.TryGetValue(name, out var symbol))
            return symbol;
        return Parent?.Lookup(name);
    }

    public Symbol? LookupLocal(string name)
    {
        _symbols.TryGetValue(name, out var symbol);
        return symbol;
    }

    public IEnumerable<Symbol> GetAllSymbols() => _symbols.Values;

    public Scope CreateChild() => new(this);
}

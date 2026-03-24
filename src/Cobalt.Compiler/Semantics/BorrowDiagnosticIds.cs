namespace Cobalt.Compiler.Semantics;

public static class BorrowDiagnosticIds
{
    public const string UseAfterMove = "CB3001";
    public const string MoveOfBorrowedValue = "CB3002";
    public const string MutableBorrowWhileBorrowed = "CB3003";
    public const string UseAfterMutableBorrow = "CB3004";
    public const string OwnedNotDisposed = "CB3005";
    public const string DoubleMove = "CB3006";
    public const string UsingVarMoved = "CB3007";
}

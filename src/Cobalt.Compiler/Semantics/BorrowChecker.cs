namespace Cobalt.Compiler.Semantics;

using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Syntax;

// ──────────────────────────────────────────────
// Ownership state tracking
// ──────────────────────────────────────────────

public enum OwnershipState
{
    Owned,       // Value is live and owned by current scope
    Moved,       // Ownership transferred to another method/variable
    Borrowed,    // Currently shared-borrowed (ref)
    MutBorrowed, // Currently exclusively borrowed (ref mut)
    Disposed,    // .Dispose() was called
}

public class VariableState
{
    public string Name { get; }
    public OwnershipModifier DeclaredOwnership { get; }
    public OwnershipState State { get; set; }
    public bool IsUsingVar { get; }
    public SourceSpan DeclaredAt { get; }

    public VariableState(string name, OwnershipModifier ownership, bool isUsingVar, SourceSpan span)
    {
        Name = name;
        DeclaredOwnership = ownership;
        IsUsingVar = isUsingVar;
        State = OwnershipState.Owned;
        DeclaredAt = span;
    }
}

// ──────────────────────────────────────────────
// Borrow checker
// ──────────────────────────────────────────────

public class BorrowChecker
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Scope _globalScope;

    public BorrowChecker(DiagnosticBag diagnostics, Scope globalScope)
    {
        _diagnostics = diagnostics;
        _globalScope = globalScope;
    }

    public void Check(CompilationUnit unit)
    {
        foreach (var member in unit.Members)
        {
            switch (member)
            {
                case ClassDeclaration classDecl:
                    CheckClassBody(classDecl);
                    break;
                case TraitDeclaration traitDecl:
                    CheckTraitBody(traitDecl);
                    break;
                case MethodDeclaration methodDecl:
                    CheckTopLevelMethod(methodDecl);
                    break;
                case ImplBlock implBlock:
                    CheckImplBlock(implBlock);
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────
    // Type / block traversal
    // ──────────────────────────────────────────────

    private void CheckClassBody(ClassDeclaration classDecl)
    {
        var typeSymbol = _globalScope.Lookup(classDecl.Name) as TypeSymbol;
        if (typeSymbol == null) return;

        var methodIndex = 0;
        foreach (var member in classDecl.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                if (methodIndex < typeSymbol.Methods.Count)
                {
                    CheckMethodBody(methodDecl, typeSymbol.Methods[methodIndex]);
                    methodIndex++;
                }
            }
            else if (member is ConstructorDeclaration ctorDecl && ctorDecl.Body != null)
            {
                var tracking = new Dictionary<string, VariableState>();
                foreach (var param in ctorDecl.Parameters)
                {
                    RegisterParameter(tracking, param);
                }
                CheckBlock(ctorDecl.Body, tracking);
                CheckEndOfScope(tracking);
            }
        }
    }

    private void CheckTraitBody(TraitDeclaration traitDecl)
    {
        var typeSymbol = _globalScope.Lookup(traitDecl.Name) as TypeSymbol;
        if (typeSymbol == null) return;

        var methodIndex = 0;
        foreach (var member in traitDecl.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                if (methodIndex < typeSymbol.Methods.Count)
                {
                    CheckMethodBody(methodDecl, typeSymbol.Methods[methodIndex]);
                    methodIndex++;
                }
            }
        }
    }

    private void CheckImplBlock(ImplBlock implBlock)
    {
        var targetType = _globalScope.Lookup(implBlock.TargetTypeName) as TypeSymbol;
        if (targetType == null) return;

        foreach (var member in implBlock.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                var methodSymbol = FindMethodOnType(targetType, methodDecl.Name);
                if (methodSymbol != null)
                {
                    CheckMethodBody(methodDecl, methodSymbol);
                }
            }
        }
    }

    private void CheckTopLevelMethod(MethodDeclaration methodDecl)
    {
        var methodSymbol = _globalScope.Lookup(methodDecl.Name) as MethodSymbol;
        if (methodSymbol == null) return;
        CheckMethodBody(methodDecl, methodSymbol);
    }

    private static MethodSymbol? FindMethodOnType(TypeSymbol type, string name)
    {
        foreach (var m in type.Methods)
        {
            if (m.Name == name) return m;
        }
        return null;
    }

    // ──────────────────────────────────────────────
    // Method body checking
    // ──────────────────────────────────────────────

    private void CheckMethodBody(MethodDeclaration methodDecl, MethodSymbol methodSymbol)
    {
        if (methodDecl.Body == null) return; // abstract / trait method without body

        var tracking = new Dictionary<string, VariableState>();

        // Register parameters
        foreach (var param in methodSymbol.Parameters)
        {
            var state = OwnershipToState(param.Ownership);
            tracking[param.Name] = new VariableState(
                param.Name, param.Ownership, false, param.Span)
            {
                State = state,
            };
        }

        CheckBlock(methodDecl.Body, tracking);
        CheckEndOfScope(tracking);
    }

    private void RegisterParameter(Dictionary<string, VariableState> tracking, ParameterSyntax param)
    {
        var state = OwnershipToState(param.Ownership);
        tracking[param.Name] = new VariableState(
            param.Name, param.Ownership, false, param.Span)
        {
            State = state,
        };
    }

    private static OwnershipState OwnershipToState(OwnershipModifier ownership) => ownership switch
    {
        OwnershipModifier.Own => OwnershipState.Owned,
        OwnershipModifier.Ref => OwnershipState.Borrowed,
        OwnershipModifier.RefMut => OwnershipState.MutBorrowed,
        _ => OwnershipState.Owned,
    };

    // ──────────────────────────────────────────────
    // Statement checking
    // ──────────────────────────────────────────────

    private void CheckBlock(BlockStatement block, Dictionary<string, VariableState> tracking)
    {
        foreach (var stmt in block.Statements)
        {
            CheckStatement(stmt, tracking);
        }
    }

    private void CheckStatement(StatementNode stmt, Dictionary<string, VariableState> tracking)
    {
        switch (stmt)
        {
            case BlockStatement block:
                CheckBlock(block, tracking);
                break;
            case VariableDeclaration varDecl:
                CheckVariableDeclaration(varDecl, tracking);
                break;
            case UsingVarDeclaration usingDecl:
                CheckUsingVarDeclaration(usingDecl, tracking);
                break;
            case ReturnStatement returnStmt:
                if (returnStmt.Expression != null)
                    CheckExpression(returnStmt.Expression, tracking);
                break;
            case IfStatement ifStmt:
                CheckExpression(ifStmt.Condition, tracking);
                CheckStatement(ifStmt.ThenBody, tracking);
                if (ifStmt.ElseBody != null)
                    CheckStatement(ifStmt.ElseBody, tracking);
                break;
            case WhileStatement whileStmt:
                CheckExpression(whileStmt.Condition, tracking);
                CheckStatement(whileStmt.Body, tracking);
                break;
            case ForStatement forStmt:
                if (forStmt.Initializer != null)
                    CheckStatement(forStmt.Initializer, tracking);
                if (forStmt.Condition != null)
                    CheckExpression(forStmt.Condition, tracking);
                if (forStmt.Increment != null)
                    CheckExpression(forStmt.Increment, tracking);
                CheckStatement(forStmt.Body, tracking);
                break;
            case ForEachStatement forEachStmt:
                CheckExpression(forEachStmt.Iterable, tracking);
                CheckStatement(forEachStmt.Body, tracking);
                break;
            case MatchStatement matchStmt:
                CheckExpression(matchStmt.Subject, tracking);
                foreach (var arm in matchStmt.Arms)
                {
                    if (arm.Body is StatementNode stmtBody)
                        CheckStatement(stmtBody, tracking);
                    else if (arm.Body is ExpressionNode exprBody)
                        CheckExpression(exprBody, tracking);
                }
                break;
            case ExpressionStatement exprStmt:
                CheckExpression(exprStmt.Expression, tracking);
                break;
            case BreakStatement:
            case ContinueStatement:
                break;
        }
    }

    private void CheckVariableDeclaration(VariableDeclaration varDecl, Dictionary<string, VariableState> tracking)
    {
        if (varDecl.Initializer != null)
        {
            CheckExpression(varDecl.Initializer, tracking);
        }

        // Track the new local as Owned
        tracking[varDecl.Name] = new VariableState(
            varDecl.Name, OwnershipModifier.Own, false, varDecl.Span);
    }

    private void CheckUsingVarDeclaration(UsingVarDeclaration usingDecl, Dictionary<string, VariableState> tracking)
    {
        if (usingDecl.Initializer != null)
        {
            CheckExpression(usingDecl.Initializer, tracking);
        }

        // Track the new using local as Owned + IsUsingVar
        tracking[usingDecl.Name] = new VariableState(
            usingDecl.Name, OwnershipModifier.Own, true, usingDecl.Span);
    }

    // ──────────────────────────────────────────────
    // Expression checking
    // ──────────────────────────────────────────────

    private void CheckExpression(ExpressionNode expr, Dictionary<string, VariableState> tracking)
    {
        switch (expr)
        {
            case OwnExpression ownExpr:
                CheckOwnExpression(ownExpr, tracking);
                break;
            case RefMutExpression refMutExpr:
                CheckRefMutExpression(refMutExpr, tracking);
                break;
            case IdentifierExpression ident:
                CheckIdentifierUse(ident, tracking);
                break;
            case BinaryExpression binExpr:
                CheckExpression(binExpr.Left, tracking);
                CheckExpression(binExpr.Right, tracking);
                break;
            case UnaryExpression unaryExpr:
                CheckExpression(unaryExpr.Operand, tracking);
                break;
            case InvocationExpression invocation:
                CheckExpression(invocation.Target, tracking);
                foreach (var arg in invocation.Arguments)
                {
                    CheckArgument(arg, tracking);
                }
                break;
            case MemberAccessExpression memberAccess:
                CheckExpression(memberAccess.Target, tracking);
                break;
            case ObjectCreationExpression objCreation:
                foreach (var arg in objCreation.Arguments)
                {
                    CheckArgument(arg, tracking);
                }
                if (objCreation.InitializerClauses != null)
                {
                    foreach (var clause in objCreation.InitializerClauses)
                    {
                        CheckExpression(clause.Value, tracking);
                    }
                }
                break;
            case AssignmentExpression assignment:
                CheckExpression(assignment.Value, tracking);
                // Assignment target is not a "use" in the borrow-checking sense
                break;
            case IsPatternExpression isPattern:
                CheckExpression(isPattern.Expression, tracking);
                break;
            case SwitchExpression switchExpr:
                CheckExpression(switchExpr.Subject, tracking);
                foreach (var arm in switchExpr.Arms)
                {
                    CheckExpression(arm.Expression, tracking);
                }
                break;
            case InterpolatedStringExpression interpStr:
                foreach (var part in interpStr.Parts)
                {
                    if (part is InterpolatedStringInsertion insertion)
                    {
                        CheckExpression(insertion.Expression, tracking);
                    }
                }
                break;
            case IndexExpression indexExpr:
                CheckExpression(indexExpr.Target, tracking);
                CheckExpression(indexExpr.Index, tracking);
                break;
            case CastExpression castExpr:
                CheckExpression(castExpr.Expression, tracking);
                break;
            case LiteralExpression:
            case ThisExpression:
                // No ownership tracking needed
                break;
        }
    }

    private void CheckArgument(ArgumentSyntax arg, Dictionary<string, VariableState> tracking)
    {
        // If the argument is passed with `own`, treat it as an own-expression
        if (arg.Ownership == OwnershipModifier.Own && arg.Expression is IdentifierExpression ident)
        {
            CheckOwnTransfer(ident.Name, ident.Span, tracking);
        }
        else
        {
            CheckExpression(arg.Expression, tracking);
        }
    }

    // ──────────────────────────────────────────────
    // Ownership transfer (own x)
    // ──────────────────────────────────────────────

    private void CheckOwnExpression(OwnExpression ownExpr, Dictionary<string, VariableState> tracking)
    {
        if (ownExpr.Inner is IdentifierExpression ident)
        {
            CheckOwnTransfer(ident.Name, ownExpr.Span, tracking);
        }
        else
        {
            // own applied to a non-identifier expression — just walk the inner expression
            CheckExpression(ownExpr.Inner, tracking);
        }
    }

    private void CheckOwnTransfer(string name, SourceSpan span, Dictionary<string, VariableState> tracking)
    {
        if (!tracking.TryGetValue(name, out var varState))
            return; // Unknown variable; type checker already reported

        switch (varState.State)
        {
            case OwnershipState.Moved:
                _diagnostics.Error(BorrowDiagnosticIds.DoubleMove,
                    $"Cannot move '{name}' because it has already been moved",
                    span);
                break;

            case OwnershipState.Borrowed:
            case OwnershipState.MutBorrowed:
                _diagnostics.Error(BorrowDiagnosticIds.MoveOfBorrowedValue,
                    $"Cannot move '{name}' because it is currently borrowed",
                    span);
                break;

            case OwnershipState.Owned:
                if (varState.IsUsingVar)
                {
                    _diagnostics.Error(BorrowDiagnosticIds.UsingVarMoved,
                        $"Cannot move '{name}' because it is declared with 'using' and will be auto-disposed",
                        span);
                }
                varState.State = OwnershipState.Moved;
                break;

            case OwnershipState.Disposed:
                _diagnostics.Error(BorrowDiagnosticIds.UseAfterMove,
                    $"Cannot move '{name}' because it has already been disposed",
                    span);
                break;
        }
    }

    // ──────────────────────────────────────────────
    // Mutable borrow (ref mut x)
    // ──────────────────────────────────────────────

    private void CheckRefMutExpression(RefMutExpression refMutExpr, Dictionary<string, VariableState> tracking)
    {
        if (refMutExpr.Inner is IdentifierExpression ident)
        {
            if (tracking.TryGetValue(ident.Name, out var varState))
            {
                switch (varState.State)
                {
                    case OwnershipState.Moved:
                        _diagnostics.Error(BorrowDiagnosticIds.UseAfterMove,
                            $"Cannot borrow '{ident.Name}' as mutable because it has been moved",
                            refMutExpr.Span);
                        break;

                    case OwnershipState.Borrowed:
                        _diagnostics.Error(BorrowDiagnosticIds.MutableBorrowWhileBorrowed,
                            $"Cannot borrow '{ident.Name}' as mutable because it is already borrowed",
                            refMutExpr.Span);
                        break;

                    case OwnershipState.MutBorrowed:
                        _diagnostics.Error(BorrowDiagnosticIds.MutableBorrowWhileBorrowed,
                            $"Cannot borrow '{ident.Name}' as mutable because it is already mutably borrowed",
                            refMutExpr.Span);
                        break;

                    case OwnershipState.Owned:
                        varState.State = OwnershipState.MutBorrowed;
                        break;
                }
            }
        }
        else
        {
            CheckExpression(refMutExpr.Inner, tracking);
        }
    }

    // ──────────────────────────────────────────────
    // Identifier use — detect use-after-move
    // ──────────────────────────────────────────────

    private void CheckIdentifierUse(IdentifierExpression ident, Dictionary<string, VariableState> tracking)
    {
        if (!tracking.TryGetValue(ident.Name, out var varState))
            return;

        if (varState.State == OwnershipState.Moved)
        {
            _diagnostics.Error(BorrowDiagnosticIds.UseAfterMove,
                $"Cannot use '{ident.Name}' because it has been moved",
                ident.Span);
        }
    }

    // ──────────────────────────────────────────────
    // End-of-scope checks
    // ──────────────────────────────────────────────

    private void CheckEndOfScope(Dictionary<string, VariableState> tracking)
    {
        foreach (var (_, varState) in tracking)
        {
            // using vars are auto-disposed — no warning needed
            if (varState.IsUsingVar)
                continue;

            // Already moved or disposed — fine
            if (varState.State == OwnershipState.Moved || varState.State == OwnershipState.Disposed)
                continue;

            // Borrowed / MutBorrowed parameters don't need disposal — they don't own the value
            if (varState.DeclaredOwnership == OwnershipModifier.Ref ||
                varState.DeclaredOwnership == OwnershipModifier.RefMut)
                continue;

            // For MVP: only warn on parameters explicitly marked `own`
            if (varState.State == OwnershipState.Owned &&
                varState.DeclaredOwnership == OwnershipModifier.Own)
            {
                _diagnostics.Warning(BorrowDiagnosticIds.OwnedNotDisposed,
                    $"Owned value '{varState.Name}' is not moved or disposed before end of scope",
                    varState.DeclaredAt);
            }
        }
    }
}

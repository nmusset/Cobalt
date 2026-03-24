namespace Cobalt.Compiler.Semantics;

using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Syntax;

public class TypeChecker
{
    private readonly DiagnosticBag _diagnostics;
    private Scope _globalScope = null!;
    private TypeSymbol? _currentType;
    private MethodSymbol? _currentMethod;

    public TypeChecker(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public Scope Check(CompilationUnit unit)
    {
        _globalScope = new Scope();
        BuiltInTypes.RegisterAll(_globalScope);

        // Pass 1: Register all type declarations
        DeclarationPass(unit, _globalScope);

        // Pass 2: Check method bodies, resolve expressions, report diagnostics
        BodyPass(unit, _globalScope);

        return _globalScope;
    }

    // ──────────────────────────────────────────────
    // Pass 1: Declaration pass
    // ──────────────────────────────────────────────

    private void DeclarationPass(CompilationUnit unit, Scope scope)
    {
        foreach (var member in unit.Members)
        {
            switch (member)
            {
                case ClassDeclaration classDecl:
                    DeclareClass(classDecl, scope);
                    break;
                case TraitDeclaration traitDecl:
                    DeclareTrait(traitDecl, scope);
                    break;
                case UnionDeclaration unionDecl:
                    DeclareUnion(unionDecl, scope);
                    break;
                case MethodDeclaration methodDecl:
                    DeclareMethod(methodDecl, null, scope);
                    break;
                case ImplBlock implBlock:
                    // Defer impl block member registration to after all types are declared
                    break;
            }
        }

        // Second sub-pass: resolve base types and impl blocks now that all types are known
        foreach (var member in unit.Members)
        {
            switch (member)
            {
                case ClassDeclaration classDecl:
                    ResolveClassBaseTypes(classDecl, scope);
                    break;
                case ImplBlock implBlock:
                    DeclareImplBlock(implBlock, scope);
                    break;
            }
        }
    }

    private void DeclareClass(ClassDeclaration classDecl, Scope scope)
    {
        var typeSymbol = new TypeSymbol(classDecl.Name, TypeKind.Class, classDecl.Access, classDecl.Span)
        {
            IsSealed = classDecl.IsSealed,
            IsAbstract = classDecl.IsAbstract,
        };

        if (!scope.TryDeclare(typeSymbol))
        {
            _diagnostics.Error(SemanticDiagnosticIds.DuplicateDefinition,
                $"Type '{classDecl.Name}' is already defined", classDecl.Span);
            return;
        }

        DeclareTypeMembers(classDecl.Members, typeSymbol, scope);
    }

    private void DeclareTrait(TraitDeclaration traitDecl, Scope scope)
    {
        var typeSymbol = new TypeSymbol(traitDecl.Name, TypeKind.Trait, AccessModifier.Public, traitDecl.Span);

        if (!scope.TryDeclare(typeSymbol))
        {
            _diagnostics.Error(SemanticDiagnosticIds.DuplicateDefinition,
                $"Type '{traitDecl.Name}' is already defined", traitDecl.Span);
            return;
        }

        DeclareTypeMembers(traitDecl.Members, typeSymbol, scope);
    }

    private void DeclareUnion(UnionDeclaration unionDecl, Scope scope)
    {
        var typeSymbol = new TypeSymbol(unionDecl.Name, TypeKind.Union, unionDecl.Access, unionDecl.Span);

        if (!scope.TryDeclare(typeSymbol))
        {
            _diagnostics.Error(SemanticDiagnosticIds.DuplicateDefinition,
                $"Type '{unionDecl.Name}' is already defined", unionDecl.Span);
            return;
        }

        var variants = new List<UnionVariantSymbol>();
        foreach (var variant in unionDecl.Variants)
        {
            var variantSymbol = new UnionVariantSymbol(variant.Name, typeSymbol, variant.Span);
            var fields = new List<FieldSymbol>();
            foreach (var field in variant.Fields)
            {
                var fieldType = ResolveType(field.Type, scope);
                var fieldSymbol = new FieldSymbol(field.Name, field.Ownership, field.Access, field.Span)
                {
                    Type = fieldType,
                    ContainingType = typeSymbol,
                };
                fields.Add(fieldSymbol);
            }
            variantSymbol.Fields = fields;
            variants.Add(variantSymbol);

            // Also declare the variant name in the global scope for pattern matching
            scope.TryDeclare(variantSymbol);
        }
        typeSymbol.Variants = variants;
    }

    private void DeclareTypeMembers(IReadOnlyList<SyntaxNode> members, TypeSymbol typeSymbol, Scope scope)
    {
        var methods = new List<MethodSymbol>();
        var fields = new List<FieldSymbol>();
        var properties = new List<PropertySymbol>();

        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDeclaration methodDecl:
                {
                    var methodSymbol = DeclareMethod(methodDecl, typeSymbol, scope);
                    if (methodSymbol != null)
                        methods.Add(methodSymbol);
                    break;
                }
                case FieldDeclaration fieldDecl:
                {
                    var fieldType = ResolveType(fieldDecl.Type, scope);
                    var fieldSymbol = new FieldSymbol(fieldDecl.Name, fieldDecl.Ownership, fieldDecl.Access, fieldDecl.Span)
                    {
                        Type = fieldType,
                        ContainingType = typeSymbol,
                    };
                    fields.Add(fieldSymbol);
                    break;
                }
                case PropertyDeclaration propDecl:
                {
                    var propType = ResolveType(propDecl.Type, scope);
                    var propSymbol = new PropertySymbol(propDecl.Name, propDecl.Access,
                        propDecl.HasGetter, propDecl.HasSetter, propDecl.Span)
                    {
                        Type = propType,
                        ContainingType = typeSymbol,
                    };
                    properties.Add(propSymbol);
                    break;
                }
                case ConstructorDeclaration:
                    // Constructors handled implicitly for MVP
                    break;
            }
        }

        typeSymbol.Methods = methods;
        typeSymbol.Fields = fields;
        typeSymbol.Properties = properties;
    }

    private MethodSymbol? DeclareMethod(MethodDeclaration methodDecl, TypeSymbol? containingType, Scope scope)
    {
        var returnType = ResolveType(methodDecl.ReturnType, scope);
        var methodSymbol = new MethodSymbol(methodDecl.Name, methodDecl.Access,
            methodDecl.IsStatic, methodDecl.IsAbstract, methodDecl.IsVirtual, methodDecl.IsOverride,
            methodDecl.Span)
        {
            ReturnType = returnType,
            ReturnOwnership = methodDecl.ReturnOwnership,
            ContainingType = containingType,
        };

        var parameters = new List<ParameterSymbol>();
        foreach (var param in methodDecl.Parameters)
        {
            var paramType = ResolveType(param.Type, scope);
            var paramSymbol = new ParameterSymbol(param.Name, param.Ownership, param.Span)
            {
                Type = paramType,
            };
            parameters.Add(paramSymbol);
        }
        methodSymbol.Parameters = parameters;

        // Top-level functions go into scope; type methods are attached to their type
        if (containingType == null)
        {
            if (!scope.TryDeclare(methodSymbol))
            {
                _diagnostics.Error(SemanticDiagnosticIds.DuplicateDefinition,
                    $"Function '{methodDecl.Name}' is already defined", methodDecl.Span);
                return null;
            }
        }

        return methodSymbol;
    }

    private void ResolveClassBaseTypes(ClassDeclaration classDecl, Scope scope)
    {
        if (scope.Lookup(classDecl.Name) is not TypeSymbol typeSymbol)
            return;

        var baseTypes = new List<TypeSymbol>();
        foreach (var baseType in classDecl.BaseTypes)
        {
            var resolved = ResolveType(baseType, scope);
            if (resolved.Kind != TypeKind.Error)
                baseTypes.Add(resolved);
        }
        typeSymbol.BaseTypes = baseTypes;
    }

    private void DeclareImplBlock(ImplBlock implBlock, Scope scope)
    {
        var targetType = scope.Lookup(implBlock.TargetTypeName) as TypeSymbol;
        if (targetType == null)
        {
            _diagnostics.Error(SemanticDiagnosticIds.UndefinedType,
                $"Type '{implBlock.TargetTypeName}' is not defined", implBlock.Span);
            return;
        }

        // Add methods from the impl block to the target type
        var existingMethods = new List<MethodSymbol>(targetType.Methods);

        foreach (var member in implBlock.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                var methodSymbol = DeclareMethod(methodDecl, targetType, scope);
                if (methodSymbol != null)
                    existingMethods.Add(methodSymbol);
            }
        }

        targetType.Methods = existingMethods;

        // If the impl block names a trait, register it as a base type
        if (!string.IsNullOrEmpty(implBlock.TraitName))
        {
            var traitSymbol = scope.Lookup(implBlock.TraitName) as TypeSymbol;
            if (traitSymbol == null)
            {
                _diagnostics.Error(SemanticDiagnosticIds.UndefinedType,
                    $"Trait '{implBlock.TraitName}' is not defined", implBlock.Span);
            }
            else
            {
                var bases = new List<TypeSymbol>(targetType.BaseTypes) { traitSymbol };
                targetType.BaseTypes = bases;
            }
        }
    }

    // ──────────────────────────────────────────────
    // Type resolution
    // ──────────────────────────────────────────────

    private TypeSymbol ResolveType(TypeSyntax typeSyntax, Scope scope)
    {
        var symbol = scope.Lookup(typeSyntax.Name);
        if (symbol is TypeSymbol typeSymbol)
        {
            // If generic, resolve type arguments (but still return the base type for MVP)
            if (typeSyntax.TypeArguments.Count > 0)
            {
                foreach (var typeArg in typeSyntax.TypeArguments)
                {
                    ResolveType(typeArg, scope);
                }
            }
            return typeSymbol;
        }

        _diagnostics.Error(SemanticDiagnosticIds.UndefinedType,
            $"Type '{typeSyntax.Name}' is not defined", typeSyntax.Span);
        return BuiltInTypes.Error;
    }

    // ──────────────────────────────────────────────
    // Pass 2: Body pass
    // ──────────────────────────────────────────────

    private void BodyPass(CompilationUnit unit, Scope scope)
    {
        foreach (var member in unit.Members)
        {
            switch (member)
            {
                case ClassDeclaration classDecl:
                    CheckClassBody(classDecl, scope);
                    break;
                case TraitDeclaration traitDecl:
                    CheckTraitBody(traitDecl, scope);
                    break;
                case MethodDeclaration methodDecl:
                    CheckTopLevelMethodBody(methodDecl, scope);
                    break;
                case ImplBlock implBlock:
                    CheckImplBlockBody(implBlock, scope);
                    break;
            }
        }
    }

    private void CheckClassBody(ClassDeclaration classDecl, Scope scope)
    {
        var typeSymbol = scope.Lookup(classDecl.Name) as TypeSymbol;
        if (typeSymbol == null) return;

        var previousType = _currentType;
        _currentType = typeSymbol;

        var methodIndex = 0;
        foreach (var member in classDecl.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                if (methodIndex < typeSymbol.Methods.Count)
                {
                    var methodSymbol = typeSymbol.Methods[methodIndex];
                    CheckMethodBody(methodDecl, methodSymbol, scope);
                    methodIndex++;
                }
            }
            else if (member is FieldDeclaration fieldDecl && fieldDecl.Initializer != null)
            {
                CheckExpression(fieldDecl.Initializer, scope);
            }
            else if (member is PropertyDeclaration propDecl && propDecl.ExpressionBody != null)
            {
                CheckExpression(propDecl.ExpressionBody, scope);
            }
            else if (member is ConstructorDeclaration ctorDecl && ctorDecl.Body != null)
            {
                var ctorScope = scope.CreateChild();
                // Declare 'this'
                var thisLocal = new LocalSymbol("this", false, ctorDecl.Span) { Type = typeSymbol };
                ctorScope.TryDeclare(thisLocal);
                foreach (var param in ctorDecl.Parameters)
                {
                    var paramType = ResolveType(param.Type, scope);
                    var paramSymbol = new ParameterSymbol(param.Name, param.Ownership, param.Span) { Type = paramType };
                    ctorScope.TryDeclare(paramSymbol);
                }
                CheckStatement(ctorDecl.Body, ctorScope);
            }
        }

        _currentType = previousType;
    }

    private void CheckTraitBody(TraitDeclaration traitDecl, Scope scope)
    {
        var typeSymbol = scope.Lookup(traitDecl.Name) as TypeSymbol;
        if (typeSymbol == null) return;

        var previousType = _currentType;
        _currentType = typeSymbol;

        var methodIndex = 0;
        foreach (var member in traitDecl.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                if (methodIndex < typeSymbol.Methods.Count)
                {
                    var methodSymbol = typeSymbol.Methods[methodIndex];
                    CheckMethodBody(methodDecl, methodSymbol, scope);
                    methodIndex++;
                }
            }
        }

        _currentType = previousType;
    }

    private void CheckImplBlockBody(ImplBlock implBlock, Scope scope)
    {
        var targetType = scope.Lookup(implBlock.TargetTypeName) as TypeSymbol;
        if (targetType == null) return;

        var previousType = _currentType;
        _currentType = targetType;

        // Find methods from the impl block in the target type's methods list
        foreach (var member in implBlock.Members)
        {
            if (member is MethodDeclaration methodDecl)
            {
                var methodSymbol = FindMethodOnType(targetType, methodDecl.Name);
                if (methodSymbol != null)
                {
                    CheckMethodBody(methodDecl, methodSymbol, scope);
                }
            }
        }

        _currentType = previousType;
    }

    private void CheckTopLevelMethodBody(MethodDeclaration methodDecl, Scope scope)
    {
        var methodSymbol = scope.Lookup(methodDecl.Name) as MethodSymbol;
        if (methodSymbol == null) return;
        CheckMethodBody(methodDecl, methodSymbol, scope);
    }

    private void CheckMethodBody(MethodDeclaration methodDecl, MethodSymbol methodSymbol, Scope scope)
    {
        if (methodDecl.Body == null) return; // abstract or interface method

        var previousMethod = _currentMethod;
        _currentMethod = methodSymbol;

        var methodScope = scope.CreateChild();

        // Declare 'this' for instance methods
        if (!methodSymbol.IsStatic && _currentType != null)
        {
            var thisLocal = new LocalSymbol("this", false, methodDecl.Span) { Type = _currentType };
            methodScope.TryDeclare(thisLocal);
        }

        // Declare parameters
        foreach (var param in methodSymbol.Parameters)
        {
            methodScope.TryDeclare(param);
        }

        // Check body
        CheckStatement(methodDecl.Body, methodScope);

        _currentMethod = previousMethod;
    }

    // ──────────────────────────────────────────────
    // Statement checking
    // ──────────────────────────────────────────────

    private void CheckStatement(StatementNode stmt, Scope scope)
    {
        switch (stmt)
        {
            case BlockStatement block:
            {
                var childScope = scope.CreateChild();
                foreach (var s in block.Statements)
                    CheckStatement(s, childScope);
                break;
            }
            case VariableDeclaration varDecl:
                CheckVariableDeclaration(varDecl, scope);
                break;
            case UsingVarDeclaration usingDecl:
                CheckUsingVarDeclaration(usingDecl, scope);
                break;
            case ReturnStatement returnStmt:
                CheckReturnStatement(returnStmt, scope);
                break;
            case IfStatement ifStmt:
                CheckIfStatement(ifStmt, scope);
                break;
            case WhileStatement whileStmt:
                CheckWhileStatement(whileStmt, scope);
                break;
            case ForStatement forStmt:
                CheckForStatement(forStmt, scope);
                break;
            case ForEachStatement forEachStmt:
                CheckForEachStatement(forEachStmt, scope);
                break;
            case MatchStatement matchStmt:
                CheckMatchStatement(matchStmt, scope);
                break;
            case ExpressionStatement exprStmt:
                CheckExpression(exprStmt.Expression, scope);
                break;
            case BreakStatement:
            case ContinueStatement:
                // No type checking needed
                break;
        }
    }

    private void CheckVariableDeclaration(VariableDeclaration varDecl, Scope scope)
    {
        TypeSymbol type;
        if (varDecl.Initializer != null)
        {
            type = CheckExpression(varDecl.Initializer, scope);
        }
        else if (varDecl.Type != null)
        {
            type = ResolveType(varDecl.Type, scope);
        }
        else
        {
            type = BuiltInTypes.Error;
        }

        // If an explicit type is given and an initializer is present, verify compatibility
        if (varDecl.Type != null && varDecl.Initializer != null)
        {
            var declaredType = ResolveType(varDecl.Type, scope);
            CheckTypeCompatibility(declaredType, type, varDecl.Span);
            type = declaredType;
        }

        var local = new LocalSymbol(varDecl.Name, false, varDecl.Span) { Type = type };
        if (!scope.TryDeclare(local))
        {
            _diagnostics.Error(SemanticDiagnosticIds.DuplicateDefinition,
                $"Variable '{varDecl.Name}' is already defined in this scope", varDecl.Span);
        }
    }

    private void CheckUsingVarDeclaration(UsingVarDeclaration usingDecl, Scope scope)
    {
        TypeSymbol type;
        if (usingDecl.Initializer != null)
        {
            type = CheckExpression(usingDecl.Initializer, scope);
        }
        else if (usingDecl.Type != null)
        {
            type = ResolveType(usingDecl.Type, scope);
        }
        else
        {
            type = BuiltInTypes.Error;
        }

        if (usingDecl.Type != null && usingDecl.Initializer != null)
        {
            var declaredType = ResolveType(usingDecl.Type, scope);
            CheckTypeCompatibility(declaredType, type, usingDecl.Span);
            type = declaredType;
        }

        var local = new LocalSymbol(usingDecl.Name, true, usingDecl.Span)
        {
            Type = type,
            Ownership = OwnershipModifier.Own,
        };
        if (!scope.TryDeclare(local))
        {
            _diagnostics.Error(SemanticDiagnosticIds.DuplicateDefinition,
                $"Variable '{usingDecl.Name}' is already defined in this scope", usingDecl.Span);
        }
    }

    private void CheckReturnStatement(ReturnStatement returnStmt, Scope scope)
    {
        if (_currentMethod == null) return;

        if (returnStmt.Expression != null)
        {
            var exprType = CheckExpression(returnStmt.Expression, scope);
            if (_currentMethod.ReturnType == BuiltInTypes.Void)
            {
                _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
                    "Cannot return a value from a void method", returnStmt.Span);
            }
            else
            {
                CheckTypeCompatibility(_currentMethod.ReturnType, exprType, returnStmt.Span);
            }
        }
        else
        {
            if (_currentMethod.ReturnType != BuiltInTypes.Void)
            {
                _diagnostics.Error(SemanticDiagnosticIds.MissingReturnValue,
                    $"Method '{_currentMethod.Name}' must return a value of type '{_currentMethod.ReturnType.Name}'",
                    returnStmt.Span);
            }
        }
    }

    private void CheckIfStatement(IfStatement ifStmt, Scope scope)
    {
        var condType = CheckExpression(ifStmt.Condition, scope);
        if (condType != BuiltInTypes.Bool && condType != BuiltInTypes.Error)
        {
            _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
                $"Condition must be of type 'bool', got '{condType.Name}'", ifStmt.Condition.Span);
        }
        CheckStatement(ifStmt.ThenBody, scope);
        if (ifStmt.ElseBody != null)
            CheckStatement(ifStmt.ElseBody, scope);
    }

    private void CheckWhileStatement(WhileStatement whileStmt, Scope scope)
    {
        var condType = CheckExpression(whileStmt.Condition, scope);
        if (condType != BuiltInTypes.Bool && condType != BuiltInTypes.Error)
        {
            _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
                $"Condition must be of type 'bool', got '{condType.Name}'", whileStmt.Condition.Span);
        }
        CheckStatement(whileStmt.Body, scope);
    }

    private void CheckForStatement(ForStatement forStmt, Scope scope)
    {
        var forScope = scope.CreateChild();
        if (forStmt.Initializer != null)
            CheckStatement(forStmt.Initializer, forScope);
        if (forStmt.Condition != null)
        {
            var condType = CheckExpression(forStmt.Condition, forScope);
            if (condType != BuiltInTypes.Bool && condType != BuiltInTypes.Error)
            {
                _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
                    $"Condition must be of type 'bool', got '{condType.Name}'", forStmt.Condition.Span);
            }
        }
        if (forStmt.Increment != null)
            CheckExpression(forStmt.Increment, forScope);
        CheckStatement(forStmt.Body, forScope);
    }

    private void CheckForEachStatement(ForEachStatement forEachStmt, Scope scope)
    {
        var iterableType = CheckExpression(forEachStmt.Iterable, scope);
        var forScope = scope.CreateChild();

        // For MVP, the element type is Object (we don't have generic iteration support yet)
        var elementType = BuiltInTypes.Object;

        var local = new LocalSymbol(forEachStmt.VariableName, false, forEachStmt.Span) { Type = elementType };
        forScope.TryDeclare(local);
        CheckStatement(forEachStmt.Body, forScope);
    }

    private void CheckMatchStatement(MatchStatement matchStmt, Scope scope)
    {
        var subjectType = CheckExpression(matchStmt.Subject, scope);
        foreach (var arm in matchStmt.Arms)
        {
            var armScope = scope.CreateChild();
            CheckPattern(arm.Pattern, subjectType, armScope);

            // Body can be a statement or expression wrapped in ExpressionStatement
            if (arm.Body is StatementNode stmtBody)
                CheckStatement(stmtBody, armScope);
            else if (arm.Body is ExpressionNode exprBody)
                CheckExpression(exprBody, armScope);
        }
    }

    private void CheckPattern(PatternNode pattern, TypeSymbol subjectType, Scope scope)
    {
        switch (pattern)
        {
            case VarPattern varPattern:
            {
                var local = new LocalSymbol(varPattern.VariableName, false, varPattern.Span) { Type = subjectType };
                scope.TryDeclare(local);
                break;
            }
            case VariantPattern variantPattern:
            {
                // Look up the variant
                var variant = _globalScope.Lookup(variantPattern.VariantName) as UnionVariantSymbol;
                if (variant != null)
                {
                    for (int i = 0; i < variantPattern.SubPatterns.Count && i < variant.Fields.Count; i++)
                    {
                        CheckPattern(variantPattern.SubPatterns[i], variant.Fields[i].Type, scope);
                    }
                }
                break;
            }
            case TypePattern typePattern:
            {
                var resolvedType = _globalScope.Lookup(typePattern.TypeName) as TypeSymbol;
                if (resolvedType == null)
                {
                    _diagnostics.Error(SemanticDiagnosticIds.UndefinedType,
                        $"Type '{typePattern.TypeName}' is not defined", typePattern.Span);
                    resolvedType = BuiltInTypes.Error;
                }
                if (typePattern.VariableName != null)
                {
                    var local = new LocalSymbol(typePattern.VariableName, false, typePattern.Span) { Type = resolvedType };
                    scope.TryDeclare(local);
                }
                break;
            }
            case DiscardPattern:
                break;
        }
    }

    // ──────────────────────────────────────────────
    // Expression checking
    // ──────────────────────────────────────────────

    private TypeSymbol CheckExpression(ExpressionNode expr, Scope scope)
    {
        return expr switch
        {
            LiteralExpression lit => CheckLiteral(lit),
            IdentifierExpression ident => CheckIdentifier(ident, scope),
            BinaryExpression bin => CheckBinary(bin, scope),
            UnaryExpression unary => CheckUnary(unary, scope),
            InvocationExpression invoke => CheckInvocation(invoke, scope),
            MemberAccessExpression memberAccess => CheckMemberAccess(memberAccess, scope),
            ObjectCreationExpression objCreate => CheckObjectCreation(objCreate, scope),
            OwnExpression ownExpr => CheckExpression(ownExpr.Inner, scope),
            RefMutExpression refMutExpr => CheckExpression(refMutExpr.Inner, scope),
            SwitchExpression switchExpr => CheckSwitchExpression(switchExpr, scope),
            IsPatternExpression isPattern => CheckIsPattern(isPattern, scope),
            AssignmentExpression assign => CheckAssignment(assign, scope),
            InterpolatedStringExpression interp => CheckInterpolatedString(interp, scope),
            IndexExpression indexExpr => CheckIndex(indexExpr, scope),
            ThisExpression thisExpr => CheckThis(thisExpr, scope),
            CastExpression castExpr => CheckCast(castExpr, scope),
            _ => BuiltInTypes.Error,
        };
    }

    private TypeSymbol CheckLiteral(LiteralExpression lit)
    {
        return lit.Kind switch
        {
            LiteralKind.Int => BuiltInTypes.Int,
            LiteralKind.Float => BuiltInTypes.Double,
            LiteralKind.String => BuiltInTypes.String,
            LiteralKind.Bool => BuiltInTypes.Bool,
            LiteralKind.Null => BuiltInTypes.Object,
            LiteralKind.Char => BuiltInTypes.Char,
            _ => BuiltInTypes.Error,
        };
    }

    private TypeSymbol CheckIdentifier(IdentifierExpression ident, Scope scope)
    {
        var symbol = scope.Lookup(ident.Name);

        // If not in scope, check current type's members (fields, properties, methods)
        if (symbol == null && _currentType != null)
        {
            foreach (var field in _currentType.Fields)
            {
                if (field.Name == ident.Name)
                    return field.Type;
            }
            foreach (var prop in _currentType.Properties)
            {
                if (prop.Name == ident.Name)
                    return prop.Type;
            }
            var method = FindMethodOnType(_currentType, ident.Name);
            if (method != null)
                return method.ReturnType;
        }

        if (symbol == null)
        {
            _diagnostics.Error(SemanticDiagnosticIds.UndefinedName,
                $"Name '{ident.Name}' is not defined", ident.Span);
            return BuiltInTypes.Error;
        }

        return symbol switch
        {
            LocalSymbol local => local.Type,
            ParameterSymbol param => param.Type,
            FieldSymbol field => field.Type,
            PropertySymbol prop => prop.Type,
            TypeSymbol type => type, // type used as a value (e.g., for static access)
            MethodSymbol method => method.ReturnType, // method group
            _ => BuiltInTypes.Error,
        };
    }

    private TypeSymbol CheckBinary(BinaryExpression bin, Scope scope)
    {
        var leftType = CheckExpression(bin.Left, scope);
        var rightType = CheckExpression(bin.Right, scope);

        // Comparison and logical operators return bool
        switch (bin.Operator)
        {
            case TokenKind.EqualsEquals:
            case TokenKind.BangEquals:
            case TokenKind.Less:
            case TokenKind.LessEquals:
            case TokenKind.Greater:
            case TokenKind.GreaterEquals:
                return BuiltInTypes.Bool;

            case TokenKind.AmpersandAmpersand:
            case TokenKind.PipePipe:
                if (leftType != BuiltInTypes.Bool && leftType != BuiltInTypes.Error)
                {
                    _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
                        $"Operator requires 'bool', got '{leftType.Name}'", bin.Left.Span);
                }
                if (rightType != BuiltInTypes.Bool && rightType != BuiltInTypes.Error)
                {
                    _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
                        $"Operator requires 'bool', got '{rightType.Name}'", bin.Right.Span);
                }
                return BuiltInTypes.Bool;

            case TokenKind.Plus:
                // String concatenation
                if (leftType == BuiltInTypes.String || rightType == BuiltInTypes.String)
                    return BuiltInTypes.String;
                return leftType != BuiltInTypes.Error ? leftType : rightType;

            case TokenKind.Minus:
            case TokenKind.Star:
            case TokenKind.Slash:
            case TokenKind.Percent:
                return leftType != BuiltInTypes.Error ? leftType : rightType;

            default:
                return leftType != BuiltInTypes.Error ? leftType : rightType;
        }
    }

    private TypeSymbol CheckUnary(UnaryExpression unary, Scope scope)
    {
        var operandType = CheckExpression(unary.Operand, scope);

        return unary.Operator switch
        {
            TokenKind.Bang => BuiltInTypes.Bool,
            TokenKind.Minus => operandType,
            TokenKind.PlusPlus or TokenKind.MinusMinus => operandType,
            _ => operandType,
        };
    }

    private TypeSymbol CheckInvocation(InvocationExpression invoke, Scope scope)
    {
        // Check argument expressions first
        var argTypes = new List<TypeSymbol>();
        foreach (var arg in invoke.Arguments)
        {
            argTypes.Add(CheckExpression(arg.Expression, scope));
        }

        // Determine what's being called
        if (invoke.Target is MemberAccessExpression memberAccess)
        {
            return CheckMethodCall(memberAccess, argTypes, invoke, scope);
        }

        if (invoke.Target is IdentifierExpression ident)
        {
            var symbol = scope.Lookup(ident.Name);
            if (symbol is MethodSymbol method)
            {
                if (method.Parameters.Count != invoke.Arguments.Count)
                {
                    _diagnostics.Error(SemanticDiagnosticIds.WrongArgumentCount,
                        $"Method '{method.Name}' expects {method.Parameters.Count} argument(s) but got {invoke.Arguments.Count}",
                        invoke.Span);
                }
                return method.ReturnType;
            }
            if (symbol is TypeSymbol type)
            {
                // Constructor call: TypeName(args)
                return type;
            }
            if (symbol is UnionVariantSymbol variant)
            {
                // Variant constructor call: VariantName(args)
                return variant.ContainingUnion;
            }
            if (symbol == null)
            {
                // Check current type's methods (for calling sibling methods by bare name)
                if (_currentType != null)
                {
                    var typeMethod = FindMethodOnType(_currentType, ident.Name);
                    if (typeMethod != null)
                    {
                        if (typeMethod.Parameters.Count != invoke.Arguments.Count)
                        {
                            _diagnostics.Error(SemanticDiagnosticIds.WrongArgumentCount,
                                $"Method '{typeMethod.Name}' expects {typeMethod.Parameters.Count} argument(s) but got {invoke.Arguments.Count}",
                                invoke.Span);
                        }
                        return typeMethod.ReturnType;
                    }
                }

                // Check if it's a union variant constructor
                var variantSymbol = _globalScope.Lookup(ident.Name);
                if (variantSymbol is UnionVariantSymbol)
                {
                    return (variantSymbol as UnionVariantSymbol)!.ContainingUnion;
                }

                _diagnostics.Error(SemanticDiagnosticIds.UndefinedName,
                    $"Name '{ident.Name}' is not defined", ident.Span);
                return BuiltInTypes.Error;
            }

            _diagnostics.Error(SemanticDiagnosticIds.NotCallable,
                $"'{ident.Name}' is not callable", invoke.Span);
            return BuiltInTypes.Error;
        }

        // Fallback: check the target expression
        var targetType = CheckExpression(invoke.Target, scope);
        return BuiltInTypes.Object;
    }

    private TypeSymbol CheckMethodCall(MemberAccessExpression memberAccess, List<TypeSymbol> argTypes,
        InvocationExpression invoke, Scope scope)
    {
        var targetType = CheckExpression(memberAccess.Target, scope);

        // If the target is a type symbol, look for static methods
        if (memberAccess.Target is IdentifierExpression targetIdent)
        {
            var targetSymbol = scope.Lookup(targetIdent.Name);
            if (targetSymbol is TypeSymbol typeSymbol)
                targetType = typeSymbol;
        }

        if (targetType == BuiltInTypes.Error)
            return BuiltInTypes.Error;

        // Look up the method on the type
        var method = FindMethodOnType(targetType, memberAccess.MemberName);
        if (method != null)
        {
            if (method.Parameters.Count != invoke.Arguments.Count)
            {
                _diagnostics.Error(SemanticDiagnosticIds.WrongArgumentCount,
                    $"Method '{method.Name}' expects {method.Parameters.Count} argument(s) but got {invoke.Arguments.Count}",
                    invoke.Span);
            }
            return method.ReturnType;
        }

        // For .NET types, be permissive — don't report missing members
        if (targetType.IsDotNetType || targetType.IsBuiltIn)
            return BuiltInTypes.Object;

        _diagnostics.Error(SemanticDiagnosticIds.UndefinedMember,
            $"Type '{targetType.Name}' does not contain a method '{memberAccess.MemberName}'",
            memberAccess.Span);
        return BuiltInTypes.Error;
    }

    private TypeSymbol CheckMemberAccess(MemberAccessExpression memberAccess, Scope scope)
    {
        var targetType = CheckExpression(memberAccess.Target, scope);

        // If the target is a type symbol, look for static members
        if (memberAccess.Target is IdentifierExpression targetIdent)
        {
            var targetSymbol = scope.Lookup(targetIdent.Name);
            if (targetSymbol is TypeSymbol typeSymbol)
                targetType = typeSymbol;
        }

        if (targetType == BuiltInTypes.Error)
            return BuiltInTypes.Error;

        // Look for fields
        foreach (var field in targetType.Fields)
        {
            if (field.Name == memberAccess.MemberName)
                return field.Type;
        }

        // Look for properties
        foreach (var prop in targetType.Properties)
        {
            if (prop.Name == memberAccess.MemberName)
                return prop.Type;
        }

        // Look for methods (as method group)
        var method = FindMethodOnType(targetType, memberAccess.MemberName);
        if (method != null)
            return method.ReturnType;

        // For .NET types, be permissive
        if (targetType.IsDotNetType || targetType.IsBuiltIn)
            return BuiltInTypes.Object;

        _diagnostics.Error(SemanticDiagnosticIds.UndefinedMember,
            $"Type '{targetType.Name}' does not contain a member '{memberAccess.MemberName}'",
            memberAccess.Span);
        return BuiltInTypes.Error;
    }

    private TypeSymbol CheckObjectCreation(ObjectCreationExpression objCreate, Scope scope)
    {
        var type = ResolveType(objCreate.Type, scope);

        foreach (var arg in objCreate.Arguments)
        {
            CheckExpression(arg.Expression, scope);
        }

        if (objCreate.InitializerClauses != null)
        {
            foreach (var clause in objCreate.InitializerClauses)
            {
                CheckExpression(clause.Value, scope);
            }
        }

        return type;
    }

    private TypeSymbol CheckSwitchExpression(SwitchExpression switchExpr, Scope scope)
    {
        var subjectType = CheckExpression(switchExpr.Subject, scope);
        TypeSymbol? resultType = null;

        foreach (var arm in switchExpr.Arms)
        {
            var armScope = scope.CreateChild();
            CheckPattern(arm.Pattern, subjectType, armScope);
            var armType = CheckExpression(arm.Expression, armScope);

            resultType ??= armType;
        }

        return resultType ?? BuiltInTypes.Object;
    }

    private TypeSymbol CheckIsPattern(IsPatternExpression isPattern, Scope scope)
    {
        CheckExpression(isPattern.Expression, scope);
        // Don't create a child scope for is-pattern; variables bind in the current scope
        var exprType = CheckExpression(isPattern.Expression, scope);
        CheckPattern(isPattern.Pattern, exprType, scope);
        return BuiltInTypes.Bool;
    }

    private TypeSymbol CheckAssignment(AssignmentExpression assign, Scope scope)
    {
        var targetType = CheckExpression(assign.Target, scope);
        var valueType = CheckExpression(assign.Value, scope);
        CheckTypeCompatibility(targetType, valueType, assign.Span);
        return targetType;
    }

    private TypeSymbol CheckInterpolatedString(InterpolatedStringExpression interp, Scope scope)
    {
        foreach (var part in interp.Parts)
        {
            if (part is InterpolatedStringInsertion insertion)
            {
                CheckExpression(insertion.Expression, scope);
            }
        }
        return BuiltInTypes.String;
    }

    private TypeSymbol CheckIndex(IndexExpression indexExpr, Scope scope)
    {
        CheckExpression(indexExpr.Target, scope);
        CheckExpression(indexExpr.Index, scope);
        return BuiltInTypes.Object; // simplified for MVP
    }

    private TypeSymbol CheckThis(ThisExpression thisExpr, Scope scope)
    {
        if (_currentType != null)
            return _currentType;

        _diagnostics.Error(SemanticDiagnosticIds.UndefinedName,
            "'this' is not valid in the current context", thisExpr.Span);
        return BuiltInTypes.Error;
    }

    private TypeSymbol CheckCast(CastExpression castExpr, Scope scope)
    {
        CheckExpression(castExpr.Expression, scope);
        return ResolveType(castExpr.Type, scope);
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static MethodSymbol? FindMethodOnType(TypeSymbol type, string methodName)
    {
        foreach (var method in type.Methods)
        {
            if (method.Name == methodName)
                return method;
        }

        // Search base types
        foreach (var baseType in type.BaseTypes)
        {
            var method = FindMethodOnType(baseType, methodName);
            if (method != null)
                return method;
        }

        return null;
    }

    private void CheckTypeCompatibility(TypeSymbol expected, TypeSymbol actual, SourceSpan span)
    {
        // Error type is always compatible (already reported)
        if (expected.Kind == TypeKind.Error || actual.Kind == TypeKind.Error)
            return;

        // Same type is always fine
        if (expected == actual)
            return;

        // Object accepts anything
        if (expected == BuiltInTypes.Object)
            return;

        // .NET types — be permissive for MVP
        if (expected.IsDotNetType || actual.IsDotNetType)
            return;

        // Numeric promotions
        if (IsNumericType(expected) && IsNumericType(actual))
            return;

        // Base type compatibility
        if (IsAssignableTo(actual, expected))
            return;

        _diagnostics.Error(SemanticDiagnosticIds.TypeMismatch,
            $"Cannot convert type '{actual.Name}' to '{expected.Name}'", span);
    }

    private static bool IsNumericType(TypeSymbol type) =>
        type == BuiltInTypes.Int || type == BuiltInTypes.Long ||
        type == BuiltInTypes.Float || type == BuiltInTypes.Double;

    private static bool IsAssignableTo(TypeSymbol source, TypeSymbol target)
    {
        foreach (var baseType in source.BaseTypes)
        {
            if (baseType == target)
                return true;
            if (IsAssignableTo(baseType, target))
                return true;
        }
        return false;
    }
}

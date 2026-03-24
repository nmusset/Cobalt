using Cobalt.Compiler.Diagnostics;

namespace Cobalt.Compiler.Syntax;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    public Parser(List<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    // ──────────────────────────────────────────────
    // Token navigation
    // ──────────────────────────────────────────────

    private Token Current => _position < _tokens.Count
        ? _tokens[_position]
        : _tokens[^1]; // EOF

    private Token Peek(int offset = 1) =>
        _position + offset < _tokens.Count
            ? _tokens[_position + offset]
            : _tokens[^1];

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
            _position++;
        return token;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool CheckIdentifier(string text) =>
        Current.Kind == TokenKind.Identifier && Current.Text == text;

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind) return false;
        Advance();
        return true;
    }

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind == kind)
            return Advance();

        _diagnostics.Error(ParseDiagnosticIds.ExpectedToken,
            $"Expected '{kind}' but found '{Current.Kind}' ('{Current.Text}')",
            Current.Span);
        return new Token(kind, "", Current.Span);
    }

    private Token ExpectIdentifier()
    {
        if (Current.Kind == TokenKind.Identifier)
            return Advance();

        _diagnostics.Error(ParseDiagnosticIds.ExpectedToken,
            $"Expected identifier but found '{Current.Kind}' ('{Current.Text}')",
            Current.Span);
        return new Token(TokenKind.Identifier, "<missing>", Current.Span);
    }

    // ──────────────────────────────────────────────
    // Error recovery
    // ──────────────────────────────────────────────

    private void Synchronize()
    {
        while (Current.Kind != TokenKind.EndOfFile)
        {
            // Stop at synchronization points
            if (Current.Kind == TokenKind.Semicolon)
            {
                Advance();
                return;
            }
            if (Current.Kind == TokenKind.CloseBrace)
                return;
            if (IsDeclarationStart() || IsStatementStart())
                return;
            Advance();
        }
    }

    private bool IsDeclarationStart()
    {
        return Current.Kind is TokenKind.Class or TokenKind.Trait or TokenKind.Impl
            or TokenKind.Union or TokenKind.Public or TokenKind.Private
            or TokenKind.Protected or TokenKind.Internal or TokenKind.Static
            or TokenKind.Sealed or TokenKind.Abstract or TokenKind.Override
            or TokenKind.Virtual or TokenKind.Namespace or TokenKind.Use;
    }

    private bool IsStatementStart()
    {
        return Current.Kind is TokenKind.Var or TokenKind.Using or TokenKind.If
            or TokenKind.While or TokenKind.For or TokenKind.Foreach
            or TokenKind.Return or TokenKind.Match or TokenKind.Break
            or TokenKind.Continue;
    }

    // ──────────────────────────────────────────────
    // Top-level: CompilationUnit
    // ──────────────────────────────────────────────

    public CompilationUnit ParseCompilationUnit()
    {
        var start = Current.Span;
        NamespaceDeclaration? ns = null;
        var uses = new List<UseDirective>();
        var members = new List<SyntaxNode>();

        // Optional namespace
        if (Check(TokenKind.Namespace))
            ns = ParseNamespaceDeclaration();

        // Use directives
        while (Check(TokenKind.Use))
            uses.Add(ParseUseDirective());

        // Members and top-level statements
        while (Current.Kind != TokenKind.EndOfFile)
        {
            var member = ParseTopLevelMember();
            if (member != null)
                members.Add(member);
            else
            {
                // Recovery: skip bad token
                if (Current.Kind != TokenKind.EndOfFile)
                {
                    _diagnostics.Error(ParseDiagnosticIds.UnexpectedToken,
                        $"Unexpected token '{Current.Text}'", Current.Span);
                    Advance();
                }
            }
        }

        var span = MakeSpan(start, Current.Span);
        return new CompilationUnit(ns, uses, members, span);
    }

    private NamespaceDeclaration ParseNamespaceDeclaration()
    {
        var start = Current.Span;
        Expect(TokenKind.Namespace);
        var name = ParseDottedName();
        Expect(TokenKind.Semicolon);
        return new NamespaceDeclaration(name, MakeSpan(start, Current.Span));
    }

    private UseDirective ParseUseDirective()
    {
        var start = Current.Span;
        Expect(TokenKind.Use);
        var name = ParseDottedName();
        Expect(TokenKind.Semicolon);
        return new UseDirective(name, MakeSpan(start, Current.Span));
    }

    private string ParseDottedName()
    {
        var name = ExpectIdentifier().Text;
        while (Match(TokenKind.Dot))
        {
            name += "." + ExpectIdentifier().Text;
        }
        return name;
    }

    // ──────────────────────────────────────────────
    // Top-level members
    // ──────────────────────────────────────────────

    private SyntaxNode? ParseTopLevelMember()
    {
        // Parse modifiers first
        var access = AccessModifier.None;
        bool isStatic = false, isSealed = false, isAbstract = false;
        bool isOverride = false, isVirtual = false;
        var modStart = Current.Span;

        ParseModifiers(ref access, ref isStatic, ref isSealed, ref isAbstract,
            ref isOverride, ref isVirtual);

        switch (Current.Kind)
        {
            case TokenKind.Class:
                return ParseClassDeclaration(access, isSealed, isAbstract, modStart);
            case TokenKind.Trait:
                return ParseTraitDeclaration(modStart);
            case TokenKind.Impl:
                return ParseImplBlock(modStart);
            case TokenKind.Union:
                return ParseUnionDeclaration(access, modStart);
            default:
                break;
        }

        // Try free-standing function: returnType Name(...)
        // Or method with modifiers: public static void Foo(...)
        // Must check this BEFORE expression statements, because `string Foo(...)` starts
        // with an Identifier which also matches IsExpressionStart.
        if (IsTypeStart() && LooksLikeFunctionOrField())
        {
            return ParseMethodOrFieldOrFunction(access, isStatic, isAbstract,
                isOverride, isVirtual, modStart);
        }

        // If we had no modifiers and this looks like a statement, parse as top-level statement
        if (access == AccessModifier.None && !isStatic && !isSealed && !isAbstract
            && !isOverride && !isVirtual)
        {
            if (IsStatementStart() || IsExpressionStart())
            {
                return ParseStatement();
            }
        }

        return null;
    }

    private void ParseModifiers(ref AccessModifier access, ref bool isStatic,
        ref bool isSealed, ref bool isAbstract, ref bool isOverride, ref bool isVirtual)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.Public:
                    access = AccessModifier.Public; Advance(); break;
                case TokenKind.Private:
                    access = AccessModifier.Private; Advance(); break;
                case TokenKind.Protected:
                    access = AccessModifier.Protected; Advance(); break;
                case TokenKind.Internal:
                    access = AccessModifier.Internal; Advance(); break;
                case TokenKind.Static:
                    isStatic = true; Advance(); break;
                case TokenKind.Sealed:
                    isSealed = true; Advance(); break;
                case TokenKind.Abstract:
                    isAbstract = true; Advance(); break;
                case TokenKind.Override:
                    isOverride = true; Advance(); break;
                case TokenKind.Virtual:
                    isVirtual = true; Advance(); break;
                default:
                    return;
            }
        }
    }

    private bool IsExpressionStart()
    {
        return Current.Kind is TokenKind.Identifier or TokenKind.IntLiteral
            or TokenKind.FloatLiteral or TokenKind.StringLiteral
            or TokenKind.InterpolatedStringStart or TokenKind.CharLiteral
            or TokenKind.This or TokenKind.New or TokenKind.Null
            or TokenKind.True or TokenKind.False or TokenKind.OpenParen
            or TokenKind.Bang or TokenKind.Minus or TokenKind.PlusPlus
            or TokenKind.MinusMinus or TokenKind.Own or TokenKind.Ref;
    }

    private bool IsTypeStart()
    {
        // Type can start with: own, ref, identifier, void, or built-in types
        return Current.Kind is TokenKind.Identifier or TokenKind.Void
            or TokenKind.Own or TokenKind.Ref;
    }

    /// <summary>
    /// Lookahead to distinguish a function/field declaration from an expression statement.
    /// Pattern: [own|ref [mut]] TypeName [&lt;...&gt;] Name [(|=|;|{]
    /// </summary>
    private bool LooksLikeFunctionOrField()
    {
        var saved = _position;
        try
        {
            // Skip ownership modifier
            if (Current.Kind == TokenKind.Own) Advance();
            else if (Current.Kind == TokenKind.Ref)
            {
                Advance();
                if (Current.Kind == TokenKind.Mut) Advance();
            }

            // Must have type name (identifier or void)
            if (Current.Kind == TokenKind.Void)
                Advance();
            else if (Current.Kind == TokenKind.Identifier)
            {
                Advance();
                // dotted name
                while (Current.Kind == TokenKind.Dot && Peek().Kind == TokenKind.Identifier)
                {
                    Advance();
                    Advance();
                }
            }
            else
                return false;

            // Skip generic type args: <...>
            if (Current.Kind == TokenKind.Less)
            {
                var depth = 1;
                Advance();
                while (depth > 0 && Current.Kind != TokenKind.EndOfFile)
                {
                    if (Current.Kind == TokenKind.Less) depth++;
                    else if (Current.Kind == TokenKind.Greater) depth--;
                    Advance();
                }
            }

            // Now we should see a Name (identifier)
            if (Current.Kind != TokenKind.Identifier)
                return false;
            Advance();

            // Followed by ( for method, = or ; for field, { for property
            return Current.Kind is TokenKind.OpenParen or TokenKind.Equals
                or TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.FatArrow;
        }
        finally
        {
            _position = saved;
        }
    }

    // ──────────────────────────────────────────────
    // Class declarations
    // ──────────────────────────────────────────────

    private ClassDeclaration ParseClassDeclaration(AccessModifier access,
        bool isSealed, bool isAbstract, SourceSpan start)
    {
        Expect(TokenKind.Class);
        var name = ExpectIdentifier().Text;

        var baseTypes = new List<TypeSyntax>();
        if (Match(TokenKind.Colon))
        {
            baseTypes.Add(ParseTypeSyntax());
            while (Match(TokenKind.Comma))
                baseTypes.Add(ParseTypeSyntax());
        }

        Expect(TokenKind.OpenBrace);
        var members = ParseClassMembers();
        Expect(TokenKind.CloseBrace);

        return new ClassDeclaration(name, access, isSealed, isAbstract,
            baseTypes, members, MakeSpan(start, Current.Span));
    }

    private List<SyntaxNode> ParseClassMembers()
    {
        var members = new List<SyntaxNode>();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            var member = ParseClassMember();
            if (member != null)
                members.Add(member);
            else
            {
                _diagnostics.Error(ParseDiagnosticIds.UnexpectedToken,
                    $"Unexpected token in class body: '{Current.Text}'", Current.Span);
                Synchronize();
            }
        }
        return members;
    }

    private SyntaxNode? ParseClassMember()
    {
        var access = AccessModifier.None;
        bool isStatic = false, isSealed = false, isAbstract = false;
        bool isOverride = false, isVirtual = false;
        var modStart = Current.Span;

        ParseModifiers(ref access, ref isStatic, ref isSealed, ref isAbstract,
            ref isOverride, ref isVirtual);

        // Constructor: ClassName( — we detect this by identifier followed by (
        // But we need to check if the identifier matches the containing class...
        // Simpler: if we see Identifier followed by ( and the identifier matches
        // a constructor pattern, parse it. For now, check if it looks like a type or name.

        if (!IsTypeStart() && Current.Kind != TokenKind.Identifier)
            return null;

        return ParseMethodOrFieldOrProperty(access, isStatic, isAbstract,
            isOverride, isVirtual, modStart);
    }

    private SyntaxNode ParseMethodOrFieldOrProperty(AccessModifier access,
        bool isStatic, bool isAbstract, bool isOverride, bool isVirtual,
        SourceSpan start)
    {
        // Parse ownership modifier on return type
        var returnOwnership = ParseOwnershipModifier();
        var type = ParseTypeSyntax();

        // Check for constructor: TypeName followed by (
        if (Current.Kind == TokenKind.OpenParen)
        {
            // This is a constructor
            return ParseConstructorRest(access, type.Name, start);
        }

        var name = ExpectIdentifier().Text;

        // Method: Type Name(
        if (Current.Kind == TokenKind.OpenParen)
        {
            return ParseMethodRest(name, access, isStatic, isAbstract,
                isOverride, isVirtual, type, returnOwnership, start);
        }

        // Property: Type Name { get; } or Type Name => expr;
        if (Current.Kind == TokenKind.OpenBrace && LooksLikePropertyBody())
        {
            return ParsePropertyRest(name, access, type, start);
        }
        if (Current.Kind == TokenKind.FatArrow)
        {
            return ParseExpressionBodiedProperty(name, access, type, start);
        }

        // Field: Type Name ; or Type Name = expr;
        return ParseFieldRest(name, access, type, returnOwnership, start);
    }

    private SyntaxNode ParseMethodOrFieldOrFunction(AccessModifier access,
        bool isStatic, bool isAbstract, bool isOverride, bool isVirtual,
        SourceSpan start)
    {
        // Same as class member but at top level (free-standing function)
        var returnOwnership = ParseOwnershipModifier();
        var type = ParseTypeSyntax();
        var name = ExpectIdentifier().Text;

        if (Current.Kind == TokenKind.OpenParen)
        {
            return ParseMethodRest(name, access, isStatic, isAbstract,
                isOverride, isVirtual, type, returnOwnership, start);
        }

        // Top-level field? Unusual but parse it anyway.
        return ParseFieldRest(name, access, type, returnOwnership, start);
    }

    private bool LooksLikePropertyBody()
    {
        // Look ahead: { get or { set
        if (Current.Kind != TokenKind.OpenBrace) return false;
        var next = Peek(1);
        return next.Kind is TokenKind.Get or TokenKind.Set;
    }

    private MethodDeclaration ParseMethodRest(string name, AccessModifier access,
        bool isStatic, bool isAbstract, bool isOverride, bool isVirtual,
        TypeSyntax returnType, OwnershipModifier returnOwnership, SourceSpan start)
    {
        Expect(TokenKind.OpenParen);
        var parameters = ParseParameterList();
        Expect(TokenKind.CloseParen);

        BlockStatement? body = null;
        if (Current.Kind == TokenKind.OpenBrace)
        {
            body = ParseBlock();
        }
        else
        {
            // Abstract method or interface method — no body, ends with ;
            Match(TokenKind.Semicolon);
        }

        return new MethodDeclaration(name, access, isStatic, isAbstract,
            isVirtual, isOverride, returnType, returnOwnership, parameters,
            body, MakeSpan(start, Current.Span));
    }

    private ConstructorDeclaration ParseConstructorRest(AccessModifier access,
        string typeName, SourceSpan start)
    {
        Expect(TokenKind.OpenParen);
        var parameters = ParseParameterList();
        Expect(TokenKind.CloseParen);

        BlockStatement? body = null;
        if (Current.Kind == TokenKind.OpenBrace)
            body = ParseBlock();
        else
            Match(TokenKind.Semicolon);

        return new ConstructorDeclaration(access, parameters, body,
            MakeSpan(start, Current.Span));
    }

    private PropertyDeclaration ParsePropertyRest(string name, AccessModifier access,
        TypeSyntax type, SourceSpan start)
    {
        Expect(TokenKind.OpenBrace);
        bool hasGetter = false, hasSetter = false;

        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            if (Check(TokenKind.Get))
            {
                hasGetter = true;
                Advance();
                Expect(TokenKind.Semicolon);
            }
            else if (Check(TokenKind.Set))
            {
                hasSetter = true;
                Advance();
                Expect(TokenKind.Semicolon);
            }
            else
            {
                break;
            }
        }

        Expect(TokenKind.CloseBrace);
        return new PropertyDeclaration(name, access, type, hasGetter, hasSetter,
            null, MakeSpan(start, Current.Span));
    }

    private PropertyDeclaration ParseExpressionBodiedProperty(string name,
        AccessModifier access, TypeSyntax type, SourceSpan start)
    {
        Expect(TokenKind.FatArrow);
        var expr = ParseExpression();
        Expect(TokenKind.Semicolon);
        return new PropertyDeclaration(name, access, type, true, false,
            expr, MakeSpan(start, Current.Span));
    }

    private FieldDeclaration ParseFieldRest(string name, AccessModifier access,
        TypeSyntax type, OwnershipModifier ownership, SourceSpan start)
    {
        ExpressionNode? initializer = null;
        if (Match(TokenKind.Equals))
        {
            initializer = ParseExpression();
        }
        Expect(TokenKind.Semicolon);
        return new FieldDeclaration(name, access, type, ownership,
            initializer, MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Trait declarations
    // ──────────────────────────────────────────────

    private TraitDeclaration ParseTraitDeclaration(SourceSpan start)
    {
        Expect(TokenKind.Trait);
        var name = ExpectIdentifier().Text;
        Expect(TokenKind.OpenBrace);

        var members = new List<SyntaxNode>();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            var member = ParseTraitMember();
            if (member != null)
                members.Add(member);
            else
            {
                _diagnostics.Error(ParseDiagnosticIds.UnexpectedToken,
                    $"Unexpected token in trait body: '{Current.Text}'", Current.Span);
                Synchronize();
            }
        }

        Expect(TokenKind.CloseBrace);
        return new TraitDeclaration(name, members, MakeSpan(start, Current.Span));
    }

    private SyntaxNode? ParseTraitMember()
    {
        if (!IsTypeStart() && Current.Kind != TokenKind.Identifier)
            return null;

        var memberStart = Current.Span;
        var returnOwnership = ParseOwnershipModifier();
        var type = ParseTypeSyntax();

        var name = ExpectIdentifier().Text;

        // Method signature: Type Name(...);
        if (Current.Kind == TokenKind.OpenParen)
        {
            return ParseMethodRest(name, AccessModifier.None, false, false,
                false, false, type, returnOwnership, memberStart);
        }

        // Property signature: Type Name { get; }
        if (Current.Kind == TokenKind.OpenBrace && LooksLikePropertyBody())
        {
            return ParsePropertyRest(name, AccessModifier.None, type, memberStart);
        }

        // Expression-bodied property: Type Name => expr;
        if (Current.Kind == TokenKind.FatArrow)
        {
            return ParseExpressionBodiedProperty(name, AccessModifier.None, type, memberStart);
        }

        return null;
    }

    // ──────────────────────────────────────────────
    // Impl blocks
    // ──────────────────────────────────────────────

    private ImplBlock ParseImplBlock(SourceSpan start)
    {
        Expect(TokenKind.Impl);
        var traitName = ExpectIdentifier().Text;
        Expect(TokenKind.For);
        var targetName = ExpectIdentifier().Text;
        Expect(TokenKind.OpenBrace);

        var members = ParseClassMembers();

        Expect(TokenKind.CloseBrace);
        return new ImplBlock(traitName, targetName, members, MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Union declarations
    // ──────────────────────────────────────────────

    private UnionDeclaration ParseUnionDeclaration(AccessModifier access, SourceSpan start)
    {
        Expect(TokenKind.Union);
        var name = ExpectIdentifier().Text;
        Expect(TokenKind.OpenBrace);

        var variants = new List<UnionVariant>();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            variants.Add(ParseUnionVariant());
            // Comma between variants (optional trailing comma)
            Match(TokenKind.Comma);
        }

        Expect(TokenKind.CloseBrace);
        return new UnionDeclaration(name, access, variants, MakeSpan(start, Current.Span));
    }

    private UnionVariant ParseUnionVariant()
    {
        var start = Current.Span;
        var name = ExpectIdentifier().Text;
        var fields = new List<FieldDeclaration>();

        if (Match(TokenKind.OpenParen))
        {
            while (Current.Kind != TokenKind.CloseParen && Current.Kind != TokenKind.EndOfFile)
            {
                var fieldStart = Current.Span;
                var ownership = ParseOwnershipModifier();
                var type = ParseTypeSyntax();
                var fieldName = ExpectIdentifier().Text;
                fields.Add(new FieldDeclaration(fieldName, AccessModifier.None,
                    type, ownership, null, MakeSpan(fieldStart, Current.Span)));
                if (!Match(TokenKind.Comma))
                    break;
            }
            Expect(TokenKind.CloseParen);
        }

        return new UnionVariant(name, fields, MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Parameters
    // ──────────────────────────────────────────────

    private List<ParameterSyntax> ParseParameterList()
    {
        var parameters = new List<ParameterSyntax>();
        if (Current.Kind == TokenKind.CloseParen)
            return parameters;

        parameters.Add(ParseParameter());
        while (Match(TokenKind.Comma))
            parameters.Add(ParseParameter());

        return parameters;
    }

    private ParameterSyntax ParseParameter()
    {
        var start = Current.Span;
        var ownership = ParseOwnershipModifier();
        var type = ParseTypeSyntax();
        var name = ExpectIdentifier().Text;
        return new ParameterSyntax(name, type, ownership, MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Types
    // ──────────────────────────────────────────────

    private OwnershipModifier ParseOwnershipModifier()
    {
        if (Check(TokenKind.Own))
        {
            Advance();
            return OwnershipModifier.Own;
        }
        if (Check(TokenKind.Ref))
        {
            if (Peek().Kind == TokenKind.Mut)
            {
                Advance(); // ref
                Advance(); // mut
                return OwnershipModifier.RefMut;
            }
            Advance();
            return OwnershipModifier.Ref;
        }
        return OwnershipModifier.None;
    }

    private TypeSyntax ParseTypeSyntax()
    {
        var start = Current.Span;

        // Ownership already parsed by caller usually, but handle it here too
        // for generic type arguments (e.g., List<own ITransform>)
        var ownership = OwnershipModifier.None;
        if (Check(TokenKind.Own))
        {
            ownership = OwnershipModifier.Own;
            Advance();
        }
        else if (Check(TokenKind.Ref))
        {
            if (Peek().Kind == TokenKind.Mut)
            {
                ownership = OwnershipModifier.RefMut;
                Advance();
                Advance();
            }
            else
            {
                ownership = OwnershipModifier.Ref;
                Advance();
            }
        }

        string name;
        if (Check(TokenKind.Void))
        {
            name = "void";
            Advance();
        }
        else if (Current.Kind == TokenKind.Identifier)
        {
            name = Advance().Text;
            // Dotted type name: System.IO.Stream
            while (Check(TokenKind.Dot) && Peek().Kind == TokenKind.Identifier)
            {
                Advance(); // .
                name += "." + Advance().Text;
            }
        }
        else
        {
            _diagnostics.Error(ParseDiagnosticIds.ExpectedTypeName,
                $"Expected type name but found '{Current.Kind}'", Current.Span);
            name = "<error>";
        }

        // Generic type arguments
        var typeArgs = new List<TypeSyntax>();
        if (Check(TokenKind.Less))
        {
            typeArgs = ParseTypeArguments();
        }

        return new TypeSyntax(name, typeArgs, ownership, MakeSpan(start, Current.Span));
    }

    private List<TypeSyntax> ParseTypeArguments()
    {
        var args = new List<TypeSyntax>();
        Expect(TokenKind.Less);
        args.Add(ParseTypeSyntax());
        while (Match(TokenKind.Comma))
            args.Add(ParseTypeSyntax());
        Expect(TokenKind.Greater);
        return args;
    }

    // ──────────────────────────────────────────────
    // Statements
    // ──────────────────────────────────────────────

    private BlockStatement ParseBlock()
    {
        var start = Current.Span;
        Expect(TokenKind.OpenBrace);
        var statements = new List<StatementNode>();

        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            try
            {
                var stmt = ParseStatement();
                statements.Add(stmt);
            }
            catch
            {
                _diagnostics.Error(ParseDiagnosticIds.ExpectedStatement,
                    $"Expected statement but found '{Current.Text}'", Current.Span);
                Synchronize();
            }
        }

        Expect(TokenKind.CloseBrace);
        return new BlockStatement(statements, MakeSpan(start, Current.Span));
    }

    private StatementNode ParseStatement()
    {
        switch (Current.Kind)
        {
            case TokenKind.Var:
                return ParseVariableDeclaration();
            case TokenKind.Using:
                if (Peek().Kind == TokenKind.Var)
                    return ParseUsingVarDeclaration();
                // using as a statement
                return ParseExpressionStatement();
            case TokenKind.If:
                return ParseIfStatement();
            case TokenKind.While:
                return ParseWhileStatement();
            case TokenKind.For:
                return ParseForStatement();
            case TokenKind.Foreach:
                return ParseForEachStatement();
            case TokenKind.Return:
                return ParseReturnStatement();
            case TokenKind.Match:
                return ParseMatchStatement();
            case TokenKind.Break:
                return ParseBreakStatement();
            case TokenKind.Continue:
                return ParseContinueStatement();
            case TokenKind.OpenBrace:
                return ParseBlock();
            default:
                return ParseExpressionStatement();
        }
    }

    private VariableDeclaration ParseVariableDeclaration()
    {
        var start = Current.Span;
        Expect(TokenKind.Var);
        var name = ExpectIdentifier().Text;
        ExpressionNode? initializer = null;
        if (Match(TokenKind.Equals))
        {
            initializer = ParseExpression();
        }
        Expect(TokenKind.Semicolon);
        return new VariableDeclaration(name, null, initializer, true,
            MakeSpan(start, Current.Span));
    }

    private UsingVarDeclaration ParseUsingVarDeclaration()
    {
        var start = Current.Span;
        Expect(TokenKind.Using);
        Expect(TokenKind.Var);
        var name = ExpectIdentifier().Text;
        ExpressionNode? initializer = null;
        if (Match(TokenKind.Equals))
        {
            initializer = ParseExpression();
        }
        Expect(TokenKind.Semicolon);
        return new UsingVarDeclaration(name, null, initializer,
            MakeSpan(start, Current.Span));
    }

    private IfStatement ParseIfStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.If);
        Expect(TokenKind.OpenParen);
        var condition = ParseExpression();
        Expect(TokenKind.CloseParen);
        var thenBody = ParseStatementOrBlock();
        StatementNode? elseBody = null;
        if (Match(TokenKind.Else))
        {
            elseBody = ParseStatementOrBlock();
        }
        return new IfStatement(condition, thenBody, elseBody,
            MakeSpan(start, Current.Span));
    }

    private WhileStatement ParseWhileStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.While);
        Expect(TokenKind.OpenParen);
        var condition = ParseExpression();
        Expect(TokenKind.CloseParen);
        var body = ParseStatementOrBlock();
        return new WhileStatement(condition, body, MakeSpan(start, Current.Span));
    }

    private ForStatement ParseForStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.For);
        Expect(TokenKind.OpenParen);

        // Initializer
        StatementNode? initializer = null;
        if (Current.Kind == TokenKind.Var)
        {
            initializer = ParseVariableDeclaration();
        }
        else if (Current.Kind != TokenKind.Semicolon)
        {
            initializer = ParseExpressionStatement();
        }
        else
        {
            Advance(); // ;
        }

        // Condition
        ExpressionNode? condition = null;
        if (Current.Kind != TokenKind.Semicolon)
        {
            condition = ParseExpression();
        }
        Expect(TokenKind.Semicolon);

        // Increment
        ExpressionNode? increment = null;
        if (Current.Kind != TokenKind.CloseParen)
        {
            increment = ParseExpression();
        }

        Expect(TokenKind.CloseParen);
        var body = ParseStatementOrBlock();
        return new ForStatement(initializer, condition, increment, body,
            MakeSpan(start, Current.Span));
    }

    private ForEachStatement ParseForEachStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.Foreach);
        Expect(TokenKind.OpenParen);

        var ownership = ParseOwnershipModifier();

        // Handle 'var' keyword as loop variable pattern (foreach (var item in ...))
        string varName;
        if (Check(TokenKind.Var))
        {
            Advance();
            varName = ExpectIdentifier().Text;
        }
        else
        {
            varName = ExpectIdentifier().Text;
        }

        Expect(TokenKind.In);
        var iterable = ParseExpression();
        Expect(TokenKind.CloseParen);
        var body = ParseStatementOrBlock();

        return new ForEachStatement(ownership, varName, iterable, body,
            MakeSpan(start, Current.Span));
    }

    private ReturnStatement ParseReturnStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.Return);
        ExpressionNode? expr = null;
        if (Current.Kind != TokenKind.Semicolon)
        {
            expr = ParseExpression();
        }
        Expect(TokenKind.Semicolon);
        return new ReturnStatement(expr, MakeSpan(start, Current.Span));
    }

    private MatchStatement ParseMatchStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.Match);
        Expect(TokenKind.OpenParen);
        var subject = ParseExpression();
        Expect(TokenKind.CloseParen);
        Expect(TokenKind.OpenBrace);

        var arms = new List<MatchArm>();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            arms.Add(ParseMatchArm());
            Match(TokenKind.Comma);
        }

        Expect(TokenKind.CloseBrace);
        Expect(TokenKind.Semicolon);
        return new MatchStatement(subject, arms, MakeSpan(start, Current.Span));
    }

    private MatchArm ParseMatchArm()
    {
        var start = Current.Span;
        var pattern = ParsePattern();
        Expect(TokenKind.FatArrow);
        // Body can be an expression (possibly including method calls)
        var body = (SyntaxNode)ParseExpression();
        return new MatchArm(pattern, body, MakeSpan(start, Current.Span));
    }

    private BreakStatement ParseBreakStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.Break);
        Expect(TokenKind.Semicolon);
        return new BreakStatement(MakeSpan(start, Current.Span));
    }

    private ContinueStatement ParseContinueStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.Continue);
        Expect(TokenKind.Semicolon);
        return new ContinueStatement(MakeSpan(start, Current.Span));
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        var start = Current.Span;
        var expr = ParseExpression();
        Expect(TokenKind.Semicolon);
        return new ExpressionStatement(expr, MakeSpan(start, Current.Span));
    }

    private StatementNode ParseStatementOrBlock()
    {
        if (Current.Kind == TokenKind.OpenBrace)
            return ParseBlock();
        return ParseStatement();
    }

    // ──────────────────────────────────────────────
    // Patterns
    // ──────────────────────────────────────────────

    private PatternNode ParsePattern()
    {
        var start = Current.Span;

        // Discard pattern: _
        if (Current.Kind == TokenKind.Identifier && Current.Text == "_")
        {
            Advance();
            return new DiscardPattern(MakeSpan(start, Current.Span));
        }

        // var pattern: var name
        if (Check(TokenKind.Var))
        {
            Advance();
            var name = ExpectIdentifier().Text;
            return new VarPattern(name, MakeSpan(start, Current.Span));
        }

        // Variant or Type pattern: Name(subpatterns) or TypeName varName
        if (Current.Kind == TokenKind.Identifier)
        {
            var name = Advance().Text;

            // Variant pattern: Name(subpatterns)
            if (Match(TokenKind.OpenParen))
            {
                var subPatterns = new List<PatternNode>();
                if (Current.Kind != TokenKind.CloseParen)
                {
                    subPatterns.Add(ParsePattern());
                    while (Match(TokenKind.Comma))
                        subPatterns.Add(ParsePattern());
                }
                Expect(TokenKind.CloseParen);
                return new VariantPattern(name, subPatterns, MakeSpan(start, Current.Span));
            }

            // Type pattern: TypeName variableName
            if (Current.Kind == TokenKind.Identifier)
            {
                var varName = Advance().Text;
                return new TypePattern(name, varName, MakeSpan(start, Current.Span));
            }

            // Just a type name pattern without variable
            return new TypePattern(name, null, MakeSpan(start, Current.Span));
        }

        _diagnostics.Error(ParseDiagnosticIds.ExpectedExpression,
            $"Expected pattern but found '{Current.Text}'", Current.Span);
        return new DiscardPattern(MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Expressions — Pratt-style precedence climbing
    // ──────────────────────────────────────────────

    private enum Precedence
    {
        None = 0,
        Assignment = 1,
        LogicalOr = 2,
        LogicalAnd = 3,
        Equality = 4,
        Comparison = 5,
        Addition = 6,
        Multiplication = 7,
        Unary = 8,
        Postfix = 9,
    }

    public ExpressionNode ParseExpression()
    {
        return ParseExpressionWithPrecedence(Precedence.Assignment);
    }

    private ExpressionNode ParseExpressionWithPrecedence(Precedence minPrecedence)
    {
        var left = ParseUnaryOrPrimary();

        while (true)
        {
            var prec = GetBinaryPrecedence(Current.Kind);
            if (prec == Precedence.None || prec < minPrecedence)
                break;

            // Handle assignment (right-associative)
            if (IsAssignmentOperator(Current.Kind))
            {
                if (prec < minPrecedence)
                    break;
                var op = Advance();
                var right = ParseExpressionWithPrecedence(Precedence.Assignment);
                if (op.Kind == TokenKind.Equals)
                {
                    left = new AssignmentExpression(left, right,
                        MakeSpan(left.Span, right.Span));
                }
                else
                {
                    // Compound assignment: expand a += b to a = a + b
                    var binaryOp = op.Kind switch
                    {
                        TokenKind.PlusEquals => TokenKind.Plus,
                        TokenKind.MinusEquals => TokenKind.Minus,
                        TokenKind.StarEquals => TokenKind.Star,
                        TokenKind.SlashEquals => TokenKind.Slash,
                        _ => TokenKind.Plus,
                    };
                    var binary = new BinaryExpression(left, binaryOp, right,
                        MakeSpan(left.Span, right.Span));
                    left = new AssignmentExpression(left, binary,
                        MakeSpan(left.Span, right.Span));
                }
                continue;
            }

            // Handle `is` pattern expression
            if (Current.Kind == TokenKind.Is)
            {
                Advance();
                var pattern = ParsePattern();
                left = new IsPatternExpression(left, pattern,
                    MakeSpan(left.Span, pattern.Span));
                continue;
            }

            // Regular binary operator (left-associative)
            var opToken = Advance();
            var nextPrec = (Precedence)((int)prec + 1);
            var rhs = ParseExpressionWithPrecedence(nextPrec);
            left = new BinaryExpression(left, opToken.Kind, rhs,
                MakeSpan(left.Span, rhs.Span));
        }

        return left;
    }

    private ExpressionNode ParseUnaryOrPrimary()
    {
        var start = Current.Span;

        // own expr
        if (Check(TokenKind.Own))
        {
            Advance();
            var inner = ParseUnaryOrPrimary();
            return new OwnExpression(inner, MakeSpan(start, inner.Span));
        }

        // ref mut expr
        if (Check(TokenKind.Ref) && Peek().Kind == TokenKind.Mut)
        {
            Advance(); // ref
            Advance(); // mut
            var inner = ParseUnaryOrPrimary();
            return new RefMutExpression(inner, MakeSpan(start, inner.Span));
        }

        // Prefix unary: !, -, ++, --
        if (Current.Kind is TokenKind.Bang or TokenKind.Minus
            or TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var op = Advance();
            var operand = ParseUnaryOrPrimary();
            return new UnaryExpression(op.Kind, operand, true,
                MakeSpan(start, operand.Span));
        }

        return ParsePostfix(ParsePrimary());
    }

    private ExpressionNode ParsePostfix(ExpressionNode left)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.Dot:
                {
                    Advance();
                    var member = ExpectIdentifier().Text;
                    left = new MemberAccessExpression(left, member,
                        MakeSpan(left.Span, Current.Span));
                    break;
                }
                case TokenKind.QuestionDot:
                {
                    Advance();
                    var member = ExpectIdentifier().Text;
                    left = new MemberAccessExpression(left, member,
                        MakeSpan(left.Span, Current.Span));
                    break;
                }
                case TokenKind.OpenParen:
                {
                    Advance();
                    var args = ParseArgumentList();
                    Expect(TokenKind.CloseParen);
                    left = new InvocationExpression(left, args,
                        MakeSpan(left.Span, Current.Span));
                    break;
                }
                case TokenKind.OpenBracket:
                {
                    Advance();
                    var index = ParseExpression();
                    Expect(TokenKind.CloseBracket);
                    left = new IndexExpression(left, index,
                        MakeSpan(left.Span, Current.Span));
                    break;
                }
                case TokenKind.PlusPlus:
                {
                    var op = Advance();
                    left = new UnaryExpression(op.Kind, left, false,
                        MakeSpan(left.Span, Current.Span));
                    break;
                }
                case TokenKind.MinusMinus:
                {
                    var op = Advance();
                    left = new UnaryExpression(op.Kind, left, false,
                        MakeSpan(left.Span, Current.Span));
                    break;
                }
                case TokenKind.Identifier when Current.Text == "switch":
                {
                    left = ParseSwitchExpression(left);
                    break;
                }
                default:
                    return left;
            }
        }
    }

    private ExpressionNode ParsePrimary()
    {
        var start = Current.Span;

        switch (Current.Kind)
        {
            case TokenKind.IntLiteral:
            {
                var token = Advance();
                return new LiteralExpression(token.Value, LiteralKind.Int, token.Span);
            }
            case TokenKind.FloatLiteral:
            {
                var token = Advance();
                return new LiteralExpression(token.Value, LiteralKind.Float, token.Span);
            }
            case TokenKind.StringLiteral:
            {
                var token = Advance();
                return new LiteralExpression(token.Value, LiteralKind.String, token.Span);
            }
            case TokenKind.CharLiteral:
            {
                var token = Advance();
                return new LiteralExpression(token.Value, LiteralKind.Char, token.Span);
            }
            case TokenKind.True:
            {
                Advance();
                return new LiteralExpression(true, LiteralKind.Bool, start);
            }
            case TokenKind.False:
            {
                Advance();
                return new LiteralExpression(false, LiteralKind.Bool, start);
            }
            case TokenKind.Null:
            {
                Advance();
                return new LiteralExpression(null, LiteralKind.Null, start);
            }
            case TokenKind.This:
            {
                Advance();
                return new ThisExpression(start);
            }
            case TokenKind.New:
                return ParseNewExpression();
            case TokenKind.InterpolatedStringStart:
                return ParseInterpolatedString();
            case TokenKind.OpenParen:
            {
                Advance();
                var expr = ParseExpression();
                Expect(TokenKind.CloseParen);
                return expr;
            }
            case TokenKind.Identifier:
            {
                var token = Advance();
                return new IdentifierExpression(token.Text, token.Span);
            }
            default:
            {
                _diagnostics.Error(ParseDiagnosticIds.ExpectedExpression,
                    $"Expected expression but found '{Current.Kind}' ('{Current.Text}')",
                    Current.Span);
                var token = Advance();
                return new IdentifierExpression("<error>", token.Span);
            }
        }
    }

    private ExpressionNode ParseNewExpression()
    {
        var start = Current.Span;
        Expect(TokenKind.New);

        // new() — parameterless
        if (Check(TokenKind.OpenParen))
        {
            Advance();
            var args = ParseArgumentList();
            Expect(TokenKind.CloseParen);
            var emptyType = new TypeSyntax("<inferred>", Array.Empty<TypeSyntax>(),
                OwnershipModifier.None, start);
            return new ObjectCreationExpression(emptyType, args, null,
                MakeSpan(start, Current.Span));
        }

        var type = ParseTypeSyntax();

        // new Type(args)
        if (Check(TokenKind.OpenParen))
        {
            Advance();
            var args = ParseArgumentList();
            Expect(TokenKind.CloseParen);
            return new ObjectCreationExpression(type, args, null,
                MakeSpan(start, Current.Span));
        }

        // new Type { initializers }
        if (Check(TokenKind.OpenBrace))
        {
            var initializers = ParseObjectInitializer();
            return new ObjectCreationExpression(type, Array.Empty<ArgumentSyntax>(),
                initializers, MakeSpan(start, Current.Span));
        }

        // new Type — no args, no braces (e.g., new UpperCaseTransform())
        // but without parens — treat as type with no args
        return new ObjectCreationExpression(type, Array.Empty<ArgumentSyntax>(),
            null, MakeSpan(start, Current.Span));
    }

    private List<InitializerClause> ParseObjectInitializer()
    {
        Expect(TokenKind.OpenBrace);
        var clauses = new List<InitializerClause>();

        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            var clauseStart = Current.Span;
            var fieldName = ExpectIdentifier().Text;
            Expect(TokenKind.Equals);
            var ownership = ParseOwnershipModifier();
            var value = ParseExpression();
            clauses.Add(new InitializerClause(fieldName, ownership, value,
                MakeSpan(clauseStart, Current.Span)));
            if (!Match(TokenKind.Comma))
                break;
        }

        Expect(TokenKind.CloseBrace);
        return clauses;
    }

    private InterpolatedStringExpression ParseInterpolatedString()
    {
        var start = Current.Span;
        Expect(TokenKind.InterpolatedStringStart);

        var parts = new List<InterpolatedStringPart>();

        while (Current.Kind != TokenKind.InterpolatedStringEnd
            && Current.Kind != TokenKind.EndOfFile)
        {
            if (Current.Kind == TokenKind.InterpolatedStringText)
            {
                var textToken = Advance();
                parts.Add(new InterpolatedStringText(textToken.Text, textToken.Span));
            }
            else
            {
                // Expression inside the interpolation
                var exprStart = Current.Span;
                var expr = ParseExpression();
                parts.Add(new InterpolatedStringInsertion(expr,
                    MakeSpan(exprStart, Current.Span)));
            }
        }

        Expect(TokenKind.InterpolatedStringEnd);
        return new InterpolatedStringExpression(parts, MakeSpan(start, Current.Span));
    }

    private SwitchExpression ParseSwitchExpression(ExpressionNode subject)
    {
        var start = subject.Span;
        // Current is 'switch' identifier
        Advance();
        Expect(TokenKind.OpenBrace);

        var arms = new List<SwitchArm>();
        while (Current.Kind != TokenKind.CloseBrace && Current.Kind != TokenKind.EndOfFile)
        {
            var armStart = Current.Span;
            var pattern = ParsePattern();
            Expect(TokenKind.FatArrow);
            var expr = ParseExpression();
            arms.Add(new SwitchArm(pattern, expr, MakeSpan(armStart, Current.Span)));
            Match(TokenKind.Comma);
        }

        Expect(TokenKind.CloseBrace);
        return new SwitchExpression(subject, arms, MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Arguments
    // ──────────────────────────────────────────────

    private List<ArgumentSyntax> ParseArgumentList()
    {
        var args = new List<ArgumentSyntax>();
        if (Current.Kind == TokenKind.CloseParen)
            return args;

        args.Add(ParseArgument());
        while (Match(TokenKind.Comma))
            args.Add(ParseArgument());

        return args;
    }

    private ArgumentSyntax ParseArgument()
    {
        var start = Current.Span;
        var ownership = OwnershipModifier.None;

        // Check for ownership modifier at call site
        if (Check(TokenKind.Own))
        {
            ownership = OwnershipModifier.Own;
            Advance();
        }
        else if (Check(TokenKind.Ref))
        {
            if (Peek().Kind == TokenKind.Mut)
            {
                ownership = OwnershipModifier.RefMut;
                Advance();
                Advance();
            }
            else
            {
                ownership = OwnershipModifier.Ref;
                Advance();
            }
        }

        var expr = ParseExpressionWithPrecedence(Precedence.LogicalOr);
        return new ArgumentSyntax(ownership, expr, MakeSpan(start, Current.Span));
    }

    // ──────────────────────────────────────────────
    // Precedence helpers
    // ──────────────────────────────────────────────

    private static Precedence GetBinaryPrecedence(TokenKind kind)
    {
        return kind switch
        {
            TokenKind.Equals or TokenKind.PlusEquals or TokenKind.MinusEquals
                or TokenKind.StarEquals or TokenKind.SlashEquals => Precedence.Assignment,
            TokenKind.PipePipe => Precedence.LogicalOr,
            TokenKind.AmpersandAmpersand => Precedence.LogicalAnd,
            TokenKind.EqualsEquals or TokenKind.BangEquals => Precedence.Equality,
            TokenKind.Less or TokenKind.Greater or TokenKind.LessEquals
                or TokenKind.GreaterEquals => Precedence.Comparison,
            TokenKind.Plus or TokenKind.Minus => Precedence.Addition,
            TokenKind.Star or TokenKind.Slash or TokenKind.Percent => Precedence.Multiplication,
            TokenKind.Is => Precedence.Comparison,
            _ => Precedence.None,
        };
    }

    private static bool IsAssignmentOperator(TokenKind kind)
    {
        return kind is TokenKind.Equals or TokenKind.PlusEquals
            or TokenKind.MinusEquals or TokenKind.StarEquals or TokenKind.SlashEquals;
    }

    // ──────────────────────────────────────────────
    // Span helpers
    // ──────────────────────────────────────────────

    private static SourceSpan MakeSpan(SourceSpan start, SourceSpan end) =>
        new(start.Start, end.End);
}

using Cobalt.Compiler.Diagnostics;

namespace Cobalt.Compiler.Syntax;

// ──────────────────────────────────────────────
// Enums
// ──────────────────────────────────────────────

public enum OwnershipModifier
{
    None,
    Own,
    Ref,
    RefMut,
}

public enum AccessModifier
{
    None,
    Public,
    Private,
    Protected,
    Internal,
}

public enum LiteralKind
{
    Int,
    Float,
    String,
    Bool,
    Null,
    Char,
}

// ──────────────────────────────────────────────
// Base
// ──────────────────────────────────────────────

public abstract record SyntaxNode(SourceSpan Span);

// ──────────────────────────────────────────────
// Top-level
// ──────────────────────────────────────────────

public sealed record CompilationUnit(
    NamespaceDeclaration? Namespace,
    IReadOnlyList<UseDirective> Uses,
    IReadOnlyList<SyntaxNode> Members,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record NamespaceDeclaration(
    string Name,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record UseDirective(
    string Name,
    SourceSpan Span) : SyntaxNode(Span);

// ──────────────────────────────────────────────
// Type declarations
// ──────────────────────────────────────────────

public sealed record ClassDeclaration(
    string Name,
    AccessModifier Access,
    bool IsSealed,
    bool IsAbstract,
    IReadOnlyList<TypeSyntax> BaseTypes,
    IReadOnlyList<SyntaxNode> Members,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record TraitDeclaration(
    string Name,
    IReadOnlyList<SyntaxNode> Members,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record ImplBlock(
    string TraitName,
    string TargetTypeName,
    IReadOnlyList<SyntaxNode> Members,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record UnionDeclaration(
    string Name,
    AccessModifier Access,
    IReadOnlyList<UnionVariant> Variants,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record UnionVariant(
    string Name,
    IReadOnlyList<FieldDeclaration> Fields,
    SourceSpan Span) : SyntaxNode(Span);

// ──────────────────────────────────────────────
// Members
// ──────────────────────────────────────────────

public sealed record MethodDeclaration(
    string Name,
    AccessModifier Access,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual,
    bool IsOverride,
    TypeSyntax ReturnType,
    OwnershipModifier ReturnOwnership,
    IReadOnlyList<ParameterSyntax> Parameters,
    BlockStatement? Body,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record FieldDeclaration(
    string Name,
    AccessModifier Access,
    TypeSyntax Type,
    OwnershipModifier Ownership,
    ExpressionNode? Initializer,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record PropertyDeclaration(
    string Name,
    AccessModifier Access,
    TypeSyntax Type,
    bool HasGetter,
    bool HasSetter,
    ExpressionNode? ExpressionBody,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record ParameterSyntax(
    string Name,
    TypeSyntax Type,
    OwnershipModifier Ownership,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record ConstructorDeclaration(
    AccessModifier Access,
    IReadOnlyList<ParameterSyntax> Parameters,
    BlockStatement? Body,
    SourceSpan Span) : SyntaxNode(Span);

// ──────────────────────────────────────────────
// Types
// ──────────────────────────────────────────────

public sealed record TypeSyntax(
    string Name,
    IReadOnlyList<TypeSyntax> TypeArguments,
    OwnershipModifier Ownership,
    SourceSpan Span) : SyntaxNode(Span);

// ──────────────────────────────────────────────
// Statements
// ──────────────────────────────────────────────

public abstract record StatementNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record BlockStatement(
    IReadOnlyList<StatementNode> Statements,
    SourceSpan Span) : StatementNode(Span);

public sealed record VariableDeclaration(
    string Name,
    TypeSyntax? Type,
    ExpressionNode? Initializer,
    bool IsVar,
    SourceSpan Span) : StatementNode(Span);

public sealed record UsingVarDeclaration(
    string Name,
    TypeSyntax? Type,
    ExpressionNode? Initializer,
    SourceSpan Span) : StatementNode(Span);

public sealed record ReturnStatement(
    ExpressionNode? Expression,
    SourceSpan Span) : StatementNode(Span);

public sealed record IfStatement(
    ExpressionNode Condition,
    StatementNode ThenBody,
    StatementNode? ElseBody,
    SourceSpan Span) : StatementNode(Span);

public sealed record WhileStatement(
    ExpressionNode Condition,
    StatementNode Body,
    SourceSpan Span) : StatementNode(Span);

public sealed record ForStatement(
    StatementNode? Initializer,
    ExpressionNode? Condition,
    ExpressionNode? Increment,
    StatementNode Body,
    SourceSpan Span) : StatementNode(Span);

public sealed record ForEachStatement(
    OwnershipModifier Ownership,
    string VariableName,
    ExpressionNode Iterable,
    StatementNode Body,
    SourceSpan Span) : StatementNode(Span);

public sealed record ExpressionStatement(
    ExpressionNode Expression,
    SourceSpan Span) : StatementNode(Span);

public sealed record MatchStatement(
    ExpressionNode Subject,
    IReadOnlyList<MatchArm> Arms,
    SourceSpan Span) : StatementNode(Span);

public sealed record BreakStatement(
    SourceSpan Span) : StatementNode(Span);

public sealed record ContinueStatement(
    SourceSpan Span) : StatementNode(Span);

// ──────────────────────────────────────────────
// Expressions
// ──────────────────────────────────────────────

public abstract record ExpressionNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record BinaryExpression(
    ExpressionNode Left,
    TokenKind Operator,
    ExpressionNode Right,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record UnaryExpression(
    TokenKind Operator,
    ExpressionNode Operand,
    bool IsPrefix,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record InvocationExpression(
    ExpressionNode Target,
    IReadOnlyList<ArgumentSyntax> Arguments,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record MemberAccessExpression(
    ExpressionNode Target,
    string MemberName,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record IdentifierExpression(
    string Name,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record LiteralExpression(
    object? Value,
    LiteralKind Kind,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record ObjectCreationExpression(
    TypeSyntax Type,
    IReadOnlyList<ArgumentSyntax> Arguments,
    IReadOnlyList<InitializerClause>? InitializerClauses,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record InitializerClause(
    string FieldName,
    OwnershipModifier Ownership,
    ExpressionNode Value,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record AssignmentExpression(
    ExpressionNode Target,
    ExpressionNode Value,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record OwnExpression(
    ExpressionNode Inner,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record RefMutExpression(
    ExpressionNode Inner,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record IsPatternExpression(
    ExpressionNode Expression,
    PatternNode Pattern,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record SwitchExpression(
    ExpressionNode Subject,
    IReadOnlyList<SwitchArm> Arms,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record InterpolatedStringExpression(
    IReadOnlyList<InterpolatedStringPart> Parts,
    SourceSpan Span) : ExpressionNode(Span);

public abstract record InterpolatedStringPart(SourceSpan Span) : SyntaxNode(Span);

public sealed record InterpolatedStringText(
    string Text,
    SourceSpan Span) : InterpolatedStringPart(Span);

public sealed record InterpolatedStringInsertion(
    ExpressionNode Expression,
    SourceSpan Span) : InterpolatedStringPart(Span);

public sealed record IndexExpression(
    ExpressionNode Target,
    ExpressionNode Index,
    SourceSpan Span) : ExpressionNode(Span);

public sealed record ThisExpression(
    SourceSpan Span) : ExpressionNode(Span);

public sealed record CastExpression(
    TypeSyntax Type,
    ExpressionNode Expression,
    SourceSpan Span) : ExpressionNode(Span);

// ──────────────────────────────────────────────
// Arguments
// ──────────────────────────────────────────────

public sealed record ArgumentSyntax(
    OwnershipModifier Ownership,
    ExpressionNode Expression,
    SourceSpan Span) : SyntaxNode(Span);

// ──────────────────────────────────────────────
// Match / Switch arms
// ──────────────────────────────────────────────

public sealed record MatchArm(
    PatternNode Pattern,
    SyntaxNode Body,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record SwitchArm(
    PatternNode Pattern,
    ExpressionNode Expression,
    SourceSpan Span) : SyntaxNode(Span);

// ──────────────────────────────────────────────
// Patterns
// ──────────────────────────────────────────────

public abstract record PatternNode(SourceSpan Span) : SyntaxNode(Span);

public sealed record VariantPattern(
    string VariantName,
    IReadOnlyList<PatternNode> SubPatterns,
    SourceSpan Span) : PatternNode(Span);

public sealed record VarPattern(
    string VariableName,
    SourceSpan Span) : PatternNode(Span);

public sealed record DiscardPattern(
    SourceSpan Span) : PatternNode(Span);

public sealed record TypePattern(
    string TypeName,
    string? VariableName,
    SourceSpan Span) : PatternNode(Span);

# Phase B Milestone 1: Core Language Compiler — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Cobalt compiler — parse `.co` files, type-check with ownership/borrow checking, and emit .NET IL assemblies via Mono.Cecil.

**Architecture:** The compiler follows a classic pipeline: source text → lexer → parser → AST → semantic analysis (type checking + borrow checking) → IL emission. Each stage is a separate project/namespace with clear interfaces. The compiler is a .NET console app (`cobaltc`) that reads `.co` files and produces `.dll`/`.exe` assemblies with Portable PDB debug info.

**Tech Stack:** .NET 9, C#, Mono.Cecil (IL emission), xUnit (testing), hand-written recursive-descent parser.

**Spec:** `docs/specs/2026-03-23-cobalt-syntax-design.md`
**Roadmap:** `docs/implementation-roadmap.md` (sections B.1–B.7)
**Sample input:** `samples/cobalt-syntax/` (main.co, processor.co, transforms.co)

### Diagnostic ID scheme

Phase A (Roslyn analyzers) used flat IDs: CB0001–CB0007. Phase B (compiler) uses category-prefixed IDs to separate concerns:
- **CB1xxx** — Parse errors
- **CB2xxx** — Semantic/type errors
- **CB3xxx** — Borrow checker errors

Overlap between CB0001 (analyzer: owned disposable not disposed) and CB3006 (compiler: owned not disposed) is intentional — they are different tools with separate ID namespaces. The analyzer runs on C# code; the compiler runs on Cobalt code.

### Stdlib scope

The roadmap lists 6 stdlib types. This plan implements only `Option<T>` and `Result<T, E>` — the two required by the syntax spec and sample program. `Vec<T>`, `String`, `Box<T>`, and `Span<T>` are deferred because the sample program uses .NET's `List<T>`, `string`, and `StreamReader` directly via interop. The remaining types will be added when the compiler needs them or when Cobalt-native collection APIs are designed.

---

## File Structure

```
src/
  Cobalt.Compiler/
    Cobalt.Compiler.csproj          — Main compiler library
    Syntax/
      Token.cs                      — Token types and Token struct
      Lexer.cs                      — Hand-written lexer
      SyntaxNode.cs                 — AST node base and all node types
      Parser.cs                     — Recursive-descent parser
      ParseDiagnostic.cs            — Parse-time error types
    Semantics/
      Symbol.cs                     — Symbol types (type, method, field, local, etc.)
      SymbolTable.cs                — Scoped symbol table
      TypeChecker.cs                — Type checking and resolution
      OwnershipState.cs             — Ownership state tracking (Active/Moved/Disposed/Borrowed)
      BorrowChecker.cs              — Borrow checking pass
      SemanticDiagnostic.cs         — Semantic error types
    Emit/
      ILEmitter.cs                  — Mono.Cecil-based IL code generation
      TypeEmitter.cs                — Type, trait, union IL emission
      MethodEmitter.cs              — Method body IL emission
    Diagnostics/
      Diagnostic.cs                 — Unified diagnostic type (error/warning/info)
      DiagnosticBag.cs              — Diagnostic collection
      SourceLocation.cs             — Source file + line + column
    Driver/
      Compilation.cs                — Orchestrates the full pipeline
  Cobalt.Compiler.Cli/
    Cobalt.Compiler.Cli.csproj      — cobaltc console app
    Program.cs                      — CLI entry point
  Cobalt.Stdlib/
    Cobalt.Stdlib.csproj            — Core standard library (Option, Result, etc.)
    Option.cs                       — Option<T> type
    Result.cs                       — Result<T, E> type
src/Cobalt.Analyzers.Tests/          — (existing, keep)
src/Cobalt.Analyzers/                — (existing, keep)
src/Cobalt.Annotations/             — (existing, keep)
tests/
  Cobalt.Compiler.Tests/
    Cobalt.Compiler.Tests.csproj    — Compiler test project
    Syntax/
      LexerTests.cs                 — Lexer unit tests
      ParserTests.cs                — Parser unit tests
    Semantics/
      TypeCheckerTests.cs           — Type checker tests
      BorrowCheckerTests.cs         — Borrow checker tests
    Emit/
      ILEmitterTests.cs             — End-to-end compile + run tests
    Integration/
      SampleProgramTests.cs         — Compile and verify the sample .co files
```

---

## Task 1: Project Scaffolding

**Files:**
- Create: `src/Cobalt.Compiler/Cobalt.Compiler.csproj`
- Create: `src/Cobalt.Compiler.Cli/Cobalt.Compiler.Cli.csproj`
- Create: `src/Cobalt.Stdlib/Cobalt.Stdlib.csproj`
- Create: `tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj`
- Modify: `src/Cobalt.slnx`

- [ ] **Step 1: Create the Cobalt.Compiler class library**

```bash
dotnet new classlib -n Cobalt.Compiler -o src/Cobalt.Compiler --framework net9.0
rm src/Cobalt.Compiler/Class1.cs
```

- [ ] **Step 2: Create the Cobalt.Compiler.Cli console app**

```bash
dotnet new console -n Cobalt.Compiler.Cli -o src/Cobalt.Compiler.Cli --framework net9.0
```

Add project reference to `Cobalt.Compiler`.

- [ ] **Step 3: Create the Cobalt.Stdlib class library**

```bash
dotnet new classlib -n Cobalt.Stdlib -o src/Cobalt.Stdlib --framework net9.0
rm src/Cobalt.Stdlib/Class1.cs
```

- [ ] **Step 4: Create the test project**

```bash
dotnet new xunit -n Cobalt.Compiler.Tests -o tests/Cobalt.Compiler.Tests --framework net9.0
rm tests/Cobalt.Compiler.Tests/UnitTest1.cs
```

Add project reference to `Cobalt.Compiler`.

- [ ] **Step 5: Add Mono.Cecil dependency**

```bash
dotnet add src/Cobalt.Compiler/Cobalt.Compiler.csproj package Mono.Cecil
```

- [ ] **Step 6: Add all projects to the solution**

```bash
dotnet sln src/Cobalt.slnx add src/Cobalt.Compiler/Cobalt.Compiler.csproj
dotnet sln src/Cobalt.slnx add src/Cobalt.Compiler.Cli/Cobalt.Compiler.Cli.csproj
dotnet sln src/Cobalt.slnx add src/Cobalt.Stdlib/Cobalt.Stdlib.csproj
dotnet sln src/Cobalt.slnx add tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj
```

- [ ] **Step 7: Create directory structure**

```bash
mkdir -p src/Cobalt.Compiler/Syntax
mkdir -p src/Cobalt.Compiler/Semantics
mkdir -p src/Cobalt.Compiler/Emit
mkdir -p src/Cobalt.Compiler/Diagnostics
mkdir -p src/Cobalt.Compiler/Driver
mkdir -p tests/Cobalt.Compiler.Tests/Syntax
mkdir -p tests/Cobalt.Compiler.Tests/Semantics
mkdir -p tests/Cobalt.Compiler.Tests/Emit
mkdir -p tests/Cobalt.Compiler.Tests/Integration
```

- [ ] **Step 8: Verify build**

```bash
dotnet build src/Cobalt.slnx
```

Expected: BUILD SUCCEEDED

- [ ] **Step 9: Commit**

```bash
git add src/Cobalt.Compiler/ src/Cobalt.Compiler.Cli/ src/Cobalt.Stdlib/ tests/Cobalt.Compiler.Tests/ src/Cobalt.slnx
git commit -m "Add Phase B M1 project scaffolding (compiler, CLI, stdlib, tests)"
```

---

## Task 2: Diagnostics Infrastructure

**Files:**
- Create: `src/Cobalt.Compiler/Diagnostics/SourceLocation.cs`
- Create: `src/Cobalt.Compiler/Diagnostics/Diagnostic.cs`
- Create: `src/Cobalt.Compiler/Diagnostics/DiagnosticBag.cs`

Build the diagnostic infrastructure first — every other component depends on it.

- [ ] **Step 1: Implement SourceLocation**

```csharp
// SourceLocation.cs
namespace Cobalt.Compiler.Diagnostics;

public readonly record struct SourceLocation(string FilePath, int Line, int Column)
{
    public override string ToString() => $"{FilePath}({Line},{Column})";
}

public readonly record struct SourceSpan(SourceLocation Start, SourceLocation End);
```

- [ ] **Step 2: Implement Diagnostic**

```csharp
// Diagnostic.cs
namespace Cobalt.Compiler.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed record Diagnostic(
    string Id,
    string Message,
    DiagnosticSeverity Severity,
    SourceSpan Span)
{
    public override string ToString() =>
        $"{Span.Start}: {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}
```

- [ ] **Step 3: Implement DiagnosticBag**

```csharp
// DiagnosticBag.cs
namespace Cobalt.Compiler.Diagnostics;

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> All => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Report(string id, string message, DiagnosticSeverity severity, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(id, message, severity, span));
    }

    public void Error(string id, string message, SourceSpan span) =>
        Report(id, message, DiagnosticSeverity.Error, span);

    public void Warning(string id, string message, SourceSpan span) =>
        Report(id, message, DiagnosticSeverity.Warning, span);

    public void AddRange(DiagnosticBag other) => _diagnostics.AddRange(other._diagnostics);
}
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build src/Cobalt.Compiler/Cobalt.Compiler.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Diagnostics/ && git commit -m "Add diagnostics infrastructure (SourceLocation, Diagnostic, DiagnosticBag)"
```

---

## Task 3: Lexer

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/Token.cs`
- Create: `src/Cobalt.Compiler/Syntax/Lexer.cs`
- Create: `tests/Cobalt.Compiler.Tests/Syntax/LexerTests.cs`

The lexer converts source text into a flat stream of tokens. It must handle all Cobalt keywords, operators, string interpolation, and produce meaningful diagnostics for invalid input.

- [ ] **Step 1: Define Token types**

```csharp
// Token.cs
namespace Cobalt.Compiler.Syntax;

public enum TokenKind
{
    // Literals
    IntLiteral, FloatLiteral, StringLiteral, InterpolatedStringLiteral, CharLiteral,
    BoolLiteral,

    // Identifiers and keywords
    Identifier,

    // Cobalt-specific keywords
    Own, Ref, Mut, Trait, Impl, Union, Match, Use,

    // C#-shared keywords
    Namespace, Class, Struct, Interface, Enum, Public, Private, Protected, Internal,
    Static, Sealed, Void, Return, If, Else, While, For, Foreach, In, Var, New, Using, Is,
    This, Null, True, False, Break, Continue, Throw, Try, Catch, Finally,
    Get, Set,  // property accessors

    // Reserved keywords (not used in MVP but reserved)
    Fn, Async, Await, Send, Sync,

    // Operators and punctuation
    Plus, Minus, Star, Slash, Percent,
    Equals, EqualsEquals, BangEquals, Less, LessEquals, Greater, GreaterEquals,
    Ampersand, AmpersandAmpersand, Pipe, PipePipe, Bang, Tilde, Caret,
    Dot, Comma, Semicolon, Colon, ColonColon,
    OpenParen, CloseParen, OpenBrace, CloseBrace, OpenBracket, CloseBracket,
    FatArrow,    // =>
    Arrow,       // ->
    DollarQuote, // $" for interpolated strings
    QuestionMark,

    // Special
    EndOfFile, Bad,
}

public readonly record struct Token(
    TokenKind Kind,
    string Text,
    SourceSpan Span,
    object? Value = null);
```

(Uses `Cobalt.Compiler.Diagnostics.SourceSpan`.)

- [ ] **Step 2: Implement Lexer**

Hand-written lexer that reads source text character by character. Key behaviors:
- Recognizes all Cobalt keywords (including reserved ones)
- Handles `//` line comments and `/* */` block comments
- Handles string literals (`"..."`) and interpolated strings (`$"...{expr}..."`)
- Handles numeric literals (int and float)
- Tracks line/column for source locations
- Reports diagnostics for unterminated strings, unknown characters, etc.

```csharp
// Lexer.cs — core structure
namespace Cobalt.Compiler.Syntax;

public sealed class Lexer
{
    private readonly string _source;
    private readonly string _filePath;
    private readonly DiagnosticBag _diagnostics;
    private int _position;
    private int _line = 1;
    private int _column = 1;

    public Lexer(string source, string filePath, DiagnosticBag diagnostics) { ... }

    public DiagnosticBag Diagnostics => _diagnostics;

    public List<Token> Tokenize() { ... }    // returns all tokens including EndOfFile
    private Token NextToken() { ... }         // main dispatch
    private Token ReadIdentifierOrKeyword() { ... }
    private Token ReadNumberLiteral() { ... }
    private Token ReadStringLiteral() { ... }
    private Token ReadInterpolatedString() { ... }
    private void SkipWhitespaceAndComments() { ... }
}
```

Implement the full lexer with all token types listed in `TokenKind`.

- [ ] **Step 3: Write lexer tests**

```csharp
// LexerTests.cs
namespace Cobalt.Compiler.Tests.Syntax;

public class LexerTests
{
    private static List<Token> Lex(string source) { ... }

    [Fact] public void Lex_Keywords() { ... }           // own, ref, mut, trait, impl, union, match, use
    [Fact] public void Lex_CSharpKeywords() { ... }     // namespace, class, var, return, etc.
    [Fact] public void Lex_ReservedKeywords() { ... }   // fn, async, await, send, sync
    [Fact] public void Lex_Operators() { ... }          // +, -, *, /, ==, =>, etc.
    [Fact] public void Lex_IntLiterals() { ... }        // 0, 42, 1_000
    [Fact] public void Lex_StringLiterals() { ... }     // "hello", "with \"escape\""
    [Fact] public void Lex_InterpolatedStrings() { ... }// $"hello {name}"
    [Fact] public void Lex_Comments() { ... }           // // line, /* block */
    [Fact] public void Lex_FullSample() { ... }         // Lex the transforms.co sample, verify token count and key tokens
    [Fact] public void Lex_UnterminatedString_ReportsDiagnostic() { ... }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~LexerTests"
```

Expected: all PASS

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Syntax/Token.cs src/Cobalt.Compiler/Syntax/Lexer.cs tests/Cobalt.Compiler.Tests/Syntax/LexerTests.cs
git commit -m "Add Cobalt lexer with full keyword and operator support"
```

---

## Task 4: AST Definitions

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/SyntaxNode.cs`

Define all AST node types. The AST is the compiler's internal representation after parsing. Each node type corresponds to a syntactic construct in the spec.

- [ ] **Step 1: Define AST nodes**

Organize as a sealed hierarchy with a base `SyntaxNode` record:

```csharp
namespace Cobalt.Compiler.Syntax;

// --- Base ---
public abstract record SyntaxNode(SourceSpan Span);

// --- Top-level ---
public sealed record CompilationUnit(
    NamespaceDeclaration? Namespace,
    List<UseDirective> Uses,
    List<SyntaxNode> Members,              // classes, traits, unions, functions, top-level stmts
    SourceSpan Span) : SyntaxNode(Span);

public sealed record NamespaceDeclaration(string Name, SourceSpan Span) : SyntaxNode(Span);
public sealed record UseDirective(string Namespace, SourceSpan Span) : SyntaxNode(Span);

// --- Type declarations ---
public sealed record ClassDeclaration(...) : SyntaxNode(Span);
public sealed record TraitDeclaration(...) : SyntaxNode(Span);
public sealed record ImplBlock(...) : SyntaxNode(Span);
public sealed record UnionDeclaration(...) : SyntaxNode(Span);
public sealed record UnionVariant(...) : SyntaxNode(Span);

// --- Members ---
public sealed record MethodDeclaration(...) : SyntaxNode(Span);
public sealed record FieldDeclaration(
    ..., SyntaxNode? Initializer               // e.g., = new() on field declarations
) : SyntaxNode(Span);
public sealed record PropertyDeclaration(
    ..., bool HasGetter, bool HasSetter,
    SyntaxNode? ExpressionBody                  // => expr for expression-bodied properties
) : SyntaxNode(Span);
public sealed record ParameterSyntax(...) : SyntaxNode(Span);

// --- Ownership ---
public enum OwnershipModifier { None, Own, Ref, RefMut }

// --- Statements ---
public sealed record BlockStatement(...) : SyntaxNode(Span);
public sealed record VariableDeclaration(...) : SyntaxNode(Span);     // var x = ...
public sealed record UsingVarDeclaration(...) : SyntaxNode(Span);     // using var x = ...
public sealed record ReturnStatement(...) : SyntaxNode(Span);
public sealed record IfStatement(...) : SyntaxNode(Span);
public sealed record WhileStatement(...) : SyntaxNode(Span);
public sealed record ForStatement(...) : SyntaxNode(Span);
public sealed record ForEachStatement(...) : SyntaxNode(Span);        // includes OwnershipModifier
public sealed record ExpressionStatement(...) : SyntaxNode(Span);
public sealed record MatchStatement(...) : SyntaxNode(Span);

// --- Expressions ---
public sealed record BinaryExpression(...) : SyntaxNode(Span);
public sealed record UnaryExpression(...) : SyntaxNode(Span);
public sealed record InvocationExpression(...) : SyntaxNode(Span);    // method call
public sealed record MemberAccessExpression(...) : SyntaxNode(Span);  // a.b
public sealed record IdentifierExpression(...) : SyntaxNode(Span);
public sealed record LiteralExpression(...) : SyntaxNode(Span);
public sealed record ObjectCreationExpression(
    ..., List<InitializerClause>? Initializer   // new T { field = own expr, ... }
) : SyntaxNode(Span);
public sealed record InitializerClause(
    string FieldName, OwnershipModifier Modifier, SyntaxNode Value, SourceSpan Span
) : SyntaxNode(Span);
public sealed record AssignmentExpression(...) : SyntaxNode(Span);
public sealed record OwnExpression(...) : SyntaxNode(Span);            // own expr
public sealed record IsPatternExpression(...) : SyntaxNode(Span);       // expr is Pattern
public sealed record SwitchExpression(...) : SyntaxNode(Span);
public sealed record InterpolatedStringExpression(...) : SyntaxNode(Span);
public sealed record IndexExpression(...) : SyntaxNode(Span);            // a[i]
public sealed record ThisExpression(...) : SyntaxNode(Span);

// --- Patterns ---
public sealed record VariantPattern(...) : SyntaxNode(Span);     // Success(var n)
public sealed record VarPattern(...) : SyntaxNode(Span);          // var x
public sealed record DiscardPattern(...) : SyntaxNode(Span);      // _

// --- Types ---
public sealed record TypeSyntax(string Name, List<TypeSyntax> TypeArguments, OwnershipModifier Ownership, SourceSpan Span) : SyntaxNode(Span);
```

Fill in all record parameters based on the syntax spec. Each node must capture enough information for semantic analysis and IL emission.

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/Cobalt.Compiler/Cobalt.Compiler.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/Cobalt.Compiler/Syntax/SyntaxNode.cs && git commit -m "Add AST node definitions for all Cobalt syntax constructs"
```

---

## Task 5: Parser

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/Parser.cs`
- Create: `src/Cobalt.Compiler/Syntax/ParseDiagnostic.cs`
- Create: `tests/Cobalt.Compiler.Tests/Syntax/ParserTests.cs`

Hand-written recursive-descent parser. Consumes token stream from the lexer and produces an AST.

- [ ] **Step 1: Implement ParseDiagnostic IDs**

```csharp
// ParseDiagnostic.cs
namespace Cobalt.Compiler.Syntax;

public static class ParseDiagnosticIds
{
    public const string UnexpectedToken = "CB1001";
    public const string ExpectedToken = "CB1002";
    public const string ExpectedExpression = "CB1003";
    public const string ExpectedStatement = "CB1004";
    public const string ExpectedTypeName = "CB1005";
    public const string InvalidModifier = "CB1006";
}
```

- [ ] **Step 2: Implement Parser core**

```csharp
// Parser.cs — structure
namespace Cobalt.Compiler.Syntax;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    public Parser(List<Token> tokens, DiagnosticBag diagnostics) { ... }

    public CompilationUnit ParseCompilationUnit() { ... }

    // Top-level
    private NamespaceDeclaration? ParseNamespaceDeclaration() { ... }
    private UseDirective ParseUseDirective() { ... }
    private SyntaxNode ParseTopLevelMember() { ... }    // dispatches to class/trait/union/function/stmt

    // Declarations
    private ClassDeclaration ParseClassDeclaration(List<string> modifiers) { ... }
    private TraitDeclaration ParseTraitDeclaration() { ... }
    private ImplBlock ParseImplBlock() { ... }
    private UnionDeclaration ParseUnionDeclaration() { ... }
    private MethodDeclaration ParseMethodDeclaration(...) { ... }
    private FieldDeclaration ParseFieldDeclaration(...) { ... }   // includes optional initializer
    private PropertyDeclaration ParsePropertyDeclaration(...) { ... } // { get; } or => expr
    private ParameterSyntax ParseParameter() { ... }

    // Statements
    private SyntaxNode ParseStatement() { ... }
    private VariableDeclaration ParseVariableDeclaration() { ... }
    private UsingVarDeclaration ParseUsingVarDeclaration() { ... }
    private IfStatement ParseIfStatement() { ... }
    private WhileStatement ParseWhileStatement() { ... }
    private ForStatement ParseForStatement() { ... }
    private ForEachStatement ParseForEachStatement() { ... }
    private ReturnStatement ParseReturnStatement() { ... }
    private MatchStatement ParseMatchStatement() { ... }

    // Expressions (Pratt parser / precedence climbing)
    private SyntaxNode ParseExpression(int precedence = 0) { ... }
    private SyntaxNode ParsePrimaryExpression() { ... }
    private SwitchExpression ParseSwitchExpression(SyntaxNode subject) { ... }
    private SyntaxNode ParseObjectCreation() { ... }     // includes optional { initializer }
    private List<InitializerClause> ParseObjectInitializer() { ... }

    // Types
    private TypeSyntax ParseType() { ... }

    // Patterns
    private SyntaxNode ParsePattern() { ... }

    // Utilities
    private Token Expect(TokenKind kind) { ... }
    private Token? TryConsume(TokenKind kind) { ... }
    private Token Current => _tokens[_position];
    private Token Peek(int offset = 0) { ... }
}
```

Implement the full parser. Use precedence climbing for expression parsing. Error recovery: on unexpected tokens, report diagnostic, skip to next synchronization point (`;`, `}`, or known keyword).

- [ ] **Step 3: Write parser tests**

```csharp
// ParserTests.cs
namespace Cobalt.Compiler.Tests.Syntax;

public class ParserTests
{
    private static CompilationUnit Parse(string source) { ... }

    [Fact] public void Parse_NamespaceAndUse() { ... }
    [Fact] public void Parse_ClassWithFields() { ... }
    [Fact] public void Parse_ClassWithOwnedFields() { ... }
    [Fact] public void Parse_MethodWithParameters() { ... }
    [Fact] public void Parse_OwnRefMutParameters() { ... }
    [Fact] public void Parse_TraitDeclaration() { ... }
    [Fact] public void Parse_ImplBlock() { ... }
    [Fact] public void Parse_UnionDeclaration() { ... }
    [Fact] public void Parse_VariableDeclaration() { ... }
    [Fact] public void Parse_UsingVarDeclaration() { ... }
    [Fact] public void Parse_IfElseStatement() { ... }
    [Fact] public void Parse_WhileLoop() { ... }
    [Fact] public void Parse_ForEachWithOwnershipModifier() { ... }
    [Fact] public void Parse_MatchStatement() { ... }
    [Fact] public void Parse_SwitchExpression() { ... }
    [Fact] public void Parse_BinaryExpressions() { ... }
    [Fact] public void Parse_OwnExpression() { ... }
    [Fact] public void Parse_ObjectCreation() { ... }
    [Fact] public void Parse_InterpolatedString() { ... }
    [Fact] public void Parse_IsPatternExpression() { ... }
    [Fact] public void Parse_ReturnStatement() { ... }
    [Fact] public void Parse_TopLevelStatements() { ... }
    [Fact] public void Parse_FreeStandingFunction() { ... }
    [Fact] public void Parse_PropertyWithGetter() { ... }              // string Name { get; }
    [Fact] public void Parse_ExpressionBodiedProperty() { ... }        // string Name => "value";
    [Fact] public void Parse_ObjectCreationWithInitializer() { ... }   // new T { field = own expr }
    [Fact] public void Parse_OwnInGenericTypeArgument() { ... }        // List<own ITransform>
    [Fact] public void Parse_FieldWithInitializer() { ... }            // own List<own T> _x = new();
    [Fact] public void Parse_ForLoop() { ... }                         // for (var i = 0; i < n; i++)
    [Fact] public void Parse_FullSample_TransformsCo() { ... }   // parse the actual sample file
    [Fact] public void Parse_ErrorRecovery_MissingSemicolon() { ... }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~ParserTests"
```

Expected: all PASS

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Syntax/Parser.cs src/Cobalt.Compiler/Syntax/ParseDiagnostic.cs tests/Cobalt.Compiler.Tests/Syntax/ParserTests.cs
git commit -m "Add recursive-descent parser for Cobalt syntax"
```

---

## Task 6: Symbol Table and Type Resolution

**Files:**
- Create: `src/Cobalt.Compiler/Semantics/Symbol.cs`
- Create: `src/Cobalt.Compiler/Semantics/SymbolTable.cs`
- Create: `src/Cobalt.Compiler/Semantics/TypeChecker.cs`
- Create: `src/Cobalt.Compiler/Semantics/SemanticDiagnostic.cs`
- Create: `tests/Cobalt.Compiler.Tests/Semantics/TypeCheckerTests.cs`

Build the semantic model: resolve types, check method signatures, validate trait implementations.

- [ ] **Step 1: Implement Symbol types**

```csharp
// Symbol.cs
namespace Cobalt.Compiler.Semantics;

public abstract record Symbol(string Name);

public sealed record TypeSymbol(
    string Name,
    TypeKind Kind,                          // Class, Struct, Trait, Union, BuiltIn, DotNet
    List<TypeSymbol> TypeParameters,
    TypeSymbol? BaseType,
    List<TypeSymbol> ImplementedTraits,
    List<FieldSymbol> Fields,
    List<MethodSymbol> Methods,
    List<UnionVariantSymbol>? Variants       // non-null for union types
) : Symbol(Name);

public enum TypeKind { Class, Struct, Trait, Union, BuiltIn, DotNet }

public sealed record FieldSymbol(string Name, TypeSymbol Type, OwnershipModifier Ownership, AccessModifier Access) : Symbol(Name);
public sealed record MethodSymbol(string Name, TypeSymbol ReturnType, OwnershipModifier ReturnOwnership, List<ParameterSymbol> Parameters, bool IsStatic, AccessModifier Access) : Symbol(Name);
public sealed record ParameterSymbol(string Name, TypeSymbol Type, OwnershipModifier Ownership) : Symbol(Name);
public sealed record LocalSymbol(string Name, TypeSymbol Type, OwnershipModifier Ownership, bool IsUsing) : Symbol(Name);
public sealed record UnionVariantSymbol(string Name, List<ParameterSymbol> Fields) : Symbol(Name);

public enum AccessModifier { Public, Private, Protected, Internal }
```

- [ ] **Step 2: Implement SymbolTable**

Scoped symbol table with parent chain for nested scopes (method body → class → namespace → global):

```csharp
// SymbolTable.cs
namespace Cobalt.Compiler.Semantics;

public sealed class SymbolTable
{
    private readonly SymbolTable? _parent;
    private readonly Dictionary<string, Symbol> _symbols = new();

    public SymbolTable(SymbolTable? parent = null) { ... }

    public bool TryDeclare(Symbol symbol) { ... }     // false if already declared in this scope
    public Symbol? Lookup(string name) { ... }         // walks parent chain
    public TypeSymbol? LookupType(string name) { ... }
    public SymbolTable CreateChildScope() { ... }

    // Pre-populated with .NET built-in types
    public static SymbolTable CreateGlobalScope() { ... }  // int, string, bool, void, object, etc.
}
```

- [ ] **Step 3: Implement TypeChecker**

Walks the AST and builds the symbol table, resolves types, checks:
- Type references resolve to known types
- Method call arguments match parameter types
- Trait implementations are complete
- Union variants are correctly constructed
- Access modifiers are respected

```csharp
// TypeChecker.cs
namespace Cobalt.Compiler.Semantics;

public sealed class TypeChecker
{
    private readonly DiagnosticBag _diagnostics;
    private SymbolTable _currentScope;

    public TypeChecker(DiagnosticBag diagnostics) { ... }

    public SymbolTable Check(CompilationUnit unit) { ... }

    // Declaration passes
    private void DeclareTypes(CompilationUnit unit) { ... }      // first pass: register all type names
    private void ResolveMembers(CompilationUnit unit) { ... }    // second pass: resolve fields, methods
    private void CheckBodies(CompilationUnit unit) { ... }       // third pass: check method bodies

    // Expression type resolution
    private TypeSymbol ResolveExpressionType(SyntaxNode expr) { ... }

    // .NET interop: when a .NET method returns a nullable reference type,
    // wrap the return type as Option<T>. This enables the `is Some(var x)`
    // pattern used in the sample program (e.g., reader.ReadLine()).
    private TypeSymbol WrapNullableReturnType(TypeSymbol dotNetReturnType) { ... }
}
```

- [ ] **Step 4: Implement SemanticDiagnostic IDs**

```csharp
// SemanticDiagnostic.cs
namespace Cobalt.Compiler.Semantics;

public static class SemanticDiagnosticIds
{
    public const string UndefinedType = "CB2001";
    public const string UndefinedSymbol = "CB2002";
    public const string TypeMismatch = "CB2003";
    public const string DuplicateDefinition = "CB2004";
    public const string MissingTraitImpl = "CB2005";
    public const string InvalidAccess = "CB2006";
    public const string ArgumentCountMismatch = "CB2007";
    public const string NonExhaustiveMatch = "CB2008";
}
```

- [ ] **Step 5: Write type checker tests**

```csharp
// TypeCheckerTests.cs
namespace Cobalt.Compiler.Tests.Semantics;

public class TypeCheckerTests
{
    private static (SymbolTable symbols, DiagnosticBag diagnostics) Check(string source) { ... }

    [Fact] public void Check_ClassDeclaration_RegistersType() { ... }
    [Fact] public void Check_TraitDeclaration_RegistersTrait() { ... }
    [Fact] public void Check_UnionDeclaration_RegistersVariants() { ... }
    [Fact] public void Check_UndefinedType_ReportsError() { ... }
    [Fact] public void Check_DuplicateTypeName_ReportsError() { ... }
    [Fact] public void Check_MethodCallWithWrongArgCount_ReportsError() { ... }
    [Fact] public void Check_TraitImplMissingMethod_ReportsError() { ... }
    [Fact] public void Check_VariableTyping() { ... }
    [Fact] public void Check_OwnField_ResolvesOwnership() { ... }
    [Fact] public void Check_NonExhaustiveMatch_ReportsError() { ... }
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~TypeCheckerTests"
```

- [ ] **Step 7: Commit**

```bash
git add src/Cobalt.Compiler/Semantics/ tests/Cobalt.Compiler.Tests/Semantics/TypeCheckerTests.cs
git commit -m "Add symbol table and type checker"
```

---

## Task 7: Borrow Checker

**Files:**
- Create: `src/Cobalt.Compiler/Semantics/OwnershipState.cs`
- Create: `src/Cobalt.Compiler/Semantics/BorrowChecker.cs`
- Create: `tests/Cobalt.Compiler.Tests/Semantics/BorrowCheckerTests.cs`

The core differentiator. Enforces ownership rules within method bodies.

- [ ] **Step 1: Implement OwnershipState**

```csharp
// OwnershipState.cs
namespace Cobalt.Compiler.Semantics;

public enum VariableState
{
    Active,          // Value is live and owned
    Moved,           // Ownership transferred via 'own'
    Disposed,        // .Dispose() called or using scope exited
    BorrowedShared,  // Currently lent as 'ref'
    BorrowedMut,     // Currently lent as 'ref mut'
}

public sealed class OwnershipTracker
{
    private readonly Dictionary<string, VariableState> _states = new();
    private readonly Dictionary<string, int> _borrowCount = new();      // shared borrow count

    public void Track(string variable, VariableState initial = VariableState.Active) { ... }
    public VariableState GetState(string variable) { ... }
    public void MarkMoved(string variable) { ... }
    public void MarkDisposed(string variable) { ... }
    public void BeginBorrow(string variable, bool mutable) { ... }
    public void EndBorrow(string variable) { ... }
    public bool IsUsable(string variable) { ... }   // Active or BorrowedShared
}
```

- [ ] **Step 2: Implement BorrowChecker**

Walks method bodies (post type checking) and tracks variable states:

```csharp
// BorrowChecker.cs
namespace Cobalt.Compiler.Semantics;

public sealed class BorrowChecker
{
    private readonly DiagnosticBag _diagnostics;
    private readonly SymbolTable _symbols;

    public BorrowChecker(DiagnosticBag diagnostics, SymbolTable symbols) { ... }

    public void Check(CompilationUnit unit) { ... }

    private void CheckMethodBody(MethodDeclaration method) { ... }
    private void CheckStatement(SyntaxNode stmt, OwnershipTracker tracker) { ... }
    private void CheckExpression(SyntaxNode expr, OwnershipTracker tracker) { ... }

    // Specific checks
    private void CheckOwnTransfer(OwnExpression expr, OwnershipTracker tracker) { ... }
    private void CheckUseAfterMove(IdentifierExpression expr, OwnershipTracker tracker) { ... }
    private void CheckRefMutExclusivity(string variable, OwnershipTracker tracker) { ... }
    private void CheckScopeExit(OwnershipTracker tracker, SourceSpan span) { ... }
}
```

Diagnostic IDs for borrow checker (add to `SemanticDiagnosticIds`):

```csharp
public const string UseAfterMove = "CB3001";
public const string UseAfterDispose = "CB3002";
public const string DoubleMove = "CB3003";
public const string BorrowWhileMutBorrowed = "CB3004";
public const string MutBorrowWhileBorrowed = "CB3005";
public const string OwnedNotDisposed = "CB3006";
public const string MoveOfBorrowed = "CB3007";
```

- [ ] **Step 3: Write borrow checker tests**

```csharp
// BorrowCheckerTests.cs
namespace Cobalt.Compiler.Tests.Semantics;

public class BorrowCheckerTests
{
    private static DiagnosticBag CheckBorrows(string source) { ... }

    // Move semantics
    [Fact] public void UseAfterMove_ReportsError() { ... }
    [Fact] public void DoubleMove_ReportsError() { ... }
    [Fact] public void MoveAndDontUse_NoError() { ... }

    // Borrowing
    [Fact] public void SharedBorrow_AllowsMultiple() { ... }
    [Fact] public void MutBorrow_ExcludesOtherBorrows() { ... }
    [Fact] public void BorrowWhileMutBorrowed_ReportsError() { ... }
    [Fact] public void MutBorrowWhileBorrowed_ReportsError() { ... }

    // Disposal
    [Fact] public void OwnedDisposable_NotDisposed_ReportsWarning() { ... }
    [Fact] public void UsingVar_AutoDispose_NoWarning() { ... }
    [Fact] public void UseAfterDispose_ReportsError() { ... }

    // Own at call site
    [Fact] public void OwnTransfer_MarksAsMoved() { ... }
    [Fact] public void OwnNewExpression_NoMoveTracking() { ... }

    // Control flow
    [Fact] public void MoveInOneBranch_UseInOther_ReportsError() { ... }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~BorrowCheckerTests"
```

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Semantics/OwnershipState.cs src/Cobalt.Compiler/Semantics/BorrowChecker.cs tests/Cobalt.Compiler.Tests/Semantics/BorrowCheckerTests.cs
git commit -m "Add ownership tracking and borrow checker"
```

---

## Task 8: Core Standard Library

**Files:**
- Create: `src/Cobalt.Stdlib/Option.cs`
- Create: `src/Cobalt.Stdlib/Result.cs`

Minimal stdlib types that the compiler needs to reference.

- [ ] **Step 1: Implement Option&lt;T&gt;**

```csharp
// Option.cs
namespace Cobalt.Stdlib;

public abstract record Option<T>
{
    public sealed record SomeCase(T Value) : Option<T>;
    public sealed record NoneCase : Option<T>;

    public static Option<T> Some(T value) => new SomeCase(value);
    public static Option<T> None => new NoneCase();

    public bool IsSome => this is SomeCase;
    public bool IsNone => this is NoneCase;

    public T Unwrap() => this is SomeCase s ? s.Value : throw new InvalidOperationException("Called Unwrap on None");
    public T UnwrapOr(T defaultValue) => this is SomeCase s ? s.Value : defaultValue;
}
```

- [ ] **Step 2: Implement Result&lt;T, E&gt;**

```csharp
// Result.cs
namespace Cobalt.Stdlib;

public abstract record Result<T, E>
{
    public sealed record OkCase(T Value) : Result<T, E>;
    public sealed record ErrCase(E Error) : Result<T, E>;

    public static Result<T, E> Ok(T value) => new OkCase(value);
    public static Result<T, E> Err(E error) => new ErrCase(error);

    public bool IsOk => this is OkCase;
    public bool IsErr => this is ErrCase;

    public T Unwrap() => this is OkCase ok ? ok.Value : throw new InvalidOperationException("Called Unwrap on Err");
}
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build src/Cobalt.Stdlib/Cobalt.Stdlib.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/Cobalt.Stdlib/ && git commit -m "Add Cobalt stdlib: Option<T> and Result<T, E>"
```

---

## Task 9: Type-Level IL Emission

**Files:**
- Create: `src/Cobalt.Compiler/Emit/ILEmitter.cs`
- Create: `src/Cobalt.Compiler/Emit/TypeEmitter.cs`
- Create: `tests/Cobalt.Compiler.Tests/Emit/ILEmitterTests.cs`

Type-level IL emission: creates type definitions, fields, method signatures, and trait-to-interface mappings using Mono.Cecil. Method bodies are implemented in Task 10.

- [ ] **Step 1: Implement TypeEmitter**

Creates type definitions in Cecil:

```csharp
// TypeEmitter.cs
namespace Cobalt.Compiler.Emit;

public sealed class TypeEmitter
{
    private readonly ModuleDefinition _module;

    public TypeEmitter(ModuleDefinition module) { ... }

    public TypeDefinition EmitClass(ClassDeclaration node, TypeSymbol symbol) { ... }
    public TypeDefinition EmitTrait(TraitDeclaration node, TypeSymbol symbol) { ... }  // → interface
    public TypeDefinition EmitUnion(UnionDeclaration node, TypeSymbol symbol) { ... }  // → sealed hierarchy
    public TypeDefinition EmitProperty(PropertyDeclaration node, TypeDefinition type) { ... }
    public void EmitImplBlock(ImplBlock node, TypeDefinition targetType) { ... }
    public void EmitFieldInitializer(FieldDeclaration node, MethodDefinition ctor) { ... }
}
```

Union emission details:
- Abstract sealed base class with a private constructor
- Nested sealed class per variant with a constructor taking the variant's fields
- Each variant class inherits from the base

- [ ] **Step 2: Implement ILEmitter (orchestrator skeleton)**

```csharp
// ILEmitter.cs
namespace Cobalt.Compiler.Emit;

public sealed class ILEmitter
{
    private readonly DiagnosticBag _diagnostics;

    public ILEmitter(DiagnosticBag diagnostics) { ... }

    public void Emit(CompilationUnit unit, SymbolTable symbols, string outputPath)
    {
        // 1. Create AssemblyDefinition via Cecil
        // 2. Add reference to Cobalt.Stdlib
        // 3. Add reference to System.Runtime
        // 4. Emit all types via TypeEmitter
        // 5. Emit method bodies via MethodEmitter
        // 6. Handle top-level statements → synthetic Program.Main
        // 7. Handle free-standing functions → synthetic static class
        // 8. Write assembly to outputPath with Portable PDB
    }
}
```

- [ ] **Step 3: Write type-level emission tests**

```csharp
// ILEmitterTests.cs (type-level tests)
namespace Cobalt.Compiler.Tests.Emit;

public class TypeEmitterTests
{
    private static Assembly CompileAndLoad(string source) { ... }

    [Fact] public void Emit_ClassWithFields_CreatesType() { ... }
    [Fact] public void Emit_TraitAsInterface_CreatesInterface() { ... }
    [Fact] public void Emit_UnionType_CreatesSealedHierarchy() { ... }
    [Fact] public void Emit_PropertyGetterOnly_CreatesProperty() { ... }
    [Fact] public void Emit_ExpressionBodiedProperty_Works() { ... }
    [Fact] public void Emit_TopLevelStatements_GeneratesMain() { ... }
    [Fact] public void Emit_FreeStandingFunction_GeneratesStaticMethod() { ... }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~TypeEmitterTests"
```

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler/Emit/TypeEmitter.cs tests/Cobalt.Compiler.Tests/Emit/
git commit -m "Add type-level IL emission (classes, traits, unions, properties)"
```

---

## Task 10: Method Body IL Emission

**Files:**
- Create: `src/Cobalt.Compiler/Emit/MethodEmitter.cs`
- Create: `tests/Cobalt.Compiler.Tests/Emit/MethodEmitterTests.cs`

Emits CIL instructions for method bodies: statements, expressions, control flow, pattern matching.

- [ ] **Step 1: Implement MethodEmitter**

```csharp
// MethodEmitter.cs
namespace Cobalt.Compiler.Emit;

public sealed class MethodEmitter
{
    private readonly ModuleDefinition _module;
    private readonly ILProcessor _il;

    public MethodEmitter(ModuleDefinition module, MethodDefinition method) { ... }

    public void EmitBody(MethodDeclaration node) { ... }

    // Statements
    private void EmitStatement(SyntaxNode stmt) { ... }
    private void EmitVariableDeclaration(VariableDeclaration node) { ... }
    private void EmitIfStatement(IfStatement node) { ... }
    private void EmitWhileStatement(WhileStatement node) { ... }
    private void EmitForStatement(ForStatement node) { ... }
    private void EmitForEachStatement(ForEachStatement node) { ... }
    private void EmitReturnStatement(ReturnStatement node) { ... }
    private void EmitMatchStatement(MatchStatement node) { ... }

    // Expressions (push result onto evaluation stack)
    private void EmitExpression(SyntaxNode expr) { ... }
    private void EmitLiteral(LiteralExpression node) { ... }
    private void EmitMethodCall(InvocationExpression node) { ... }
    private void EmitObjectCreation(ObjectCreationExpression node) { ... }
    private void EmitBinaryOp(BinaryExpression node) { ... }
    private void EmitMemberAccess(MemberAccessExpression node) { ... }
    private void EmitSwitchExpression(SwitchExpression node) { ... }
    private void EmitInterpolatedString(InterpolatedStringExpression node) { ... }
    private void EmitObjectInitializer(ObjectCreationExpression node) { ... }
}
```

- [ ] **Step 2: Write method body emission tests**

```csharp
// MethodEmitterTests.cs
namespace Cobalt.Compiler.Tests.Emit;

public class MethodEmitterTests
{
    private static Assembly CompileAndLoad(string source) { ... }

    [Fact] public void Emit_HelloWorld_PrintsOutput() { ... }
    [Fact] public void Emit_ClassWithMethod_Callable() { ... }
    [Fact] public void Emit_UnionType_ConstructAndMatch() { ... }
    [Fact] public void Emit_TraitAsInterface_Dispatches() { ... }
    [Fact] public void Emit_OptionType_SomeAndNone() { ... }
    [Fact] public void Emit_ForEach_IteratesCorrectly() { ... }
    [Fact] public void Emit_ForLoop_Works() { ... }
    [Fact] public void Emit_SwitchExpression_ReturnsValue() { ... }
    [Fact] public void Emit_MatchStatement_ExecutesArm() { ... }
    [Fact] public void Emit_ObjectInitializer_SetsFields() { ... }
    [Fact] public void Emit_InterpolatedString_Concatenates() { ... }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~MethodEmitterTests"
```

- [ ] **Step 4: Commit**

```bash
git add src/Cobalt.Compiler/Emit/MethodEmitter.cs tests/Cobalt.Compiler.Tests/Emit/MethodEmitterTests.cs
git commit -m "Add method body IL emission (statements, expressions, control flow)"
```

---

## Task 11: Compilation Driver

**Files:**
- Create: `src/Cobalt.Compiler/Driver/Compilation.cs`

Orchestrates the full pipeline: lex → parse → type check → borrow check → emit.

- [ ] **Step 1: Implement Compilation**

```csharp
// Compilation.cs
namespace Cobalt.Compiler.Driver;

public sealed class Compilation
{
    public DiagnosticBag Diagnostics { get; } = new();

    public bool Compile(string[] sourceFiles, string outputPath)
    {
        // 1. Validate all source files have .co extension
        // 2. Read all source files
        // 3. Lex each file
        // 4. Parse each file into CompilationUnit
        // 5. Merge units: collect all top-level members (classes, traits, unions,
        //    functions) from all files into a single CompilationUnit.
        //    All files must share the same namespace (enforced).
        //    This enables cross-file type references (e.g., main.co uses
        //    FileProcessor from processor.co).
        // 6. Type check (three passes: declare types, resolve members, check bodies)
        // 7. Borrow check
        // 8. If no errors, emit IL
        // 9. Return success/failure
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/Cobalt.Compiler/Cobalt.Compiler.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/Cobalt.Compiler/Driver/ && git commit -m "Add compilation driver orchestrating full pipeline"
```

---

## Task 12: CLI Entry Point (`cobaltc`)

**Files:**
- Modify: `src/Cobalt.Compiler.Cli/Program.cs`

- [ ] **Step 1: Implement CLI**

```csharp
// Program.cs
namespace Cobalt.Compiler.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cobaltc <source-files...> [-o output.dll]");
            return 1;
        }

        var sourceFiles = args.Where(a => !a.StartsWith('-')).ToArray();
        var outputIndex = Array.IndexOf(args, "-o");
        var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length
            ? args[outputIndex + 1]
            : Path.ChangeExtension(sourceFiles[0], ".dll");

        var compilation = new Compilation();
        var success = compilation.Compile(sourceFiles, outputPath);

        foreach (var diagnostic in compilation.Diagnostics.All)
        {
            var stream = diagnostic.Severity == DiagnosticSeverity.Error
                ? Console.Error : Console.Out;
            stream.WriteLine(diagnostic);
        }

        if (success)
        {
            Console.WriteLine($"Compiled successfully: {outputPath}");
        }

        return success ? 0 : 1;
    }
}
```

- [ ] **Step 2: Verify CLI builds and runs**

```bash
dotnet run --project src/Cobalt.Compiler.Cli/Cobalt.Compiler.Cli.csproj
```

Expected: prints usage message, exits with code 1.

- [ ] **Step 3: Commit**

```bash
git add src/Cobalt.Compiler.Cli/ && git commit -m "Add cobaltc CLI entry point"
```

---

## Task 13: Integration Tests with Sample Program

**Files:**
- Create: `tests/Cobalt.Compiler.Tests/Integration/SampleProgramTests.cs`

Compile the actual `.co` sample files and verify the output.

- [ ] **Step 1: Write integration tests**

```csharp
// SampleProgramTests.cs
namespace Cobalt.Compiler.Tests.Integration;

public class SampleProgramTests
{
    // Resolve repo root by walking up from the test assembly directory
    private static readonly string SamplesDir = Path.Combine(
        GetRepoRoot(), "samples", "cobalt-syntax");

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cobalt.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Cannot find repo root");
    }

    [Fact]
    public void Compile_TransformsCo_Succeeds()
    {
        var compilation = new Compilation();
        var success = compilation.Compile(
            [Path.Combine(SamplesDir, "transforms.co")],
            Path.GetTempFileName() + ".dll");

        Assert.Empty(compilation.Diagnostics.All.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.True(success);
    }

    [Fact]
    public void Compile_AllSampleFiles_Succeeds()
    {
        var files = Directory.GetFiles(SamplesDir, "*.co");
        var compilation = new Compilation();
        var output = Path.GetTempFileName() + ".dll";
        var success = compilation.Compile(files, output);

        Assert.Empty(compilation.Diagnostics.All.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.True(success);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Compile_AllSampleFiles_ProducesRunnableAssembly()
    {
        // Compile, then load and invoke Main via reflection
        var files = Directory.GetFiles(SamplesDir, "*.co");
        var output = Path.Combine(Path.GetTempPath(), "cobalt-sample-test.dll");
        var compilation = new Compilation();
        compilation.Compile(files, output);

        var asm = Assembly.LoadFrom(output);
        var programType = asm.GetType("Cobalt.Samples.Program");
        Assert.NotNull(programType);

        var main = programType!.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(main);
    }
}
```

- [ ] **Step 2: Run integration tests**

```bash
dotnet test tests/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj --filter "FullyQualifiedName~SampleProgramTests"
```

- [ ] **Step 3: Commit**

```bash
git add tests/Cobalt.Compiler.Tests/Integration/ && git commit -m "Add integration tests compiling sample .co files"
```

---

## Task 14: Update Roadmap and Documentation

**Files:**
- Modify: `docs/implementation-roadmap.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Mark Phase B M1 sections as complete in roadmap**

Add ✅ to B.1 through B.7 headers in `docs/implementation-roadmap.md`.

- [ ] **Step 2: Update CLAUDE.md**

Update current state and add build commands:

```markdown
## Build Commands

- `dotnet build src/Cobalt.slnx` — build everything
- `dotnet test` — run all tests
- `dotnet test --filter "FullyQualifiedName~LexerTests"` — run specific test class
- `dotnet run --project src/Cobalt.Compiler.Cli -- <file.co> [-o output.dll]` — compile a .co file
```

- [ ] **Step 3: Commit**

```bash
git add docs/implementation-roadmap.md CLAUDE.md
git commit -m "Mark Phase B Milestone 1 as complete, update CLAUDE.md with build commands"
```

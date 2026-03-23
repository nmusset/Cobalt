# Cobalt Compiler (Phase B, Milestone 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a compiler (`cobaltc`) that parses `.co` files, type-checks with ownership/borrow checking, and emits .NET IL assemblies via Mono.Cecil.

**Architecture:** The compiler is a pipeline: Source → Lexer → Parser (CST) → AST → Type Checker → Borrow Checker → IL Emitter → .NET Assembly. Each stage is a separate project/module with well-defined input/output types. The sample program (`samples/cobalt-syntax/`) is the end-to-end acceptance test.

**Tech Stack:** C# / .NET 10, Mono.Cecil for IL emission, xUnit for tests. Hand-written recursive-descent parser. Existing `Cobalt.Annotations` attributes reused for IL metadata encoding.

**Spec:** `docs/specs/2026-03-23-cobalt-syntax-design.md`
**Roadmap:** `docs/implementation-roadmap.md` (sections B.1–B.7)

---

## File Structure

```
src/
  Cobalt.Compiler/                    # Main compiler library
    Cobalt.Compiler.csproj
    Diagnostics/
      Diagnostic.cs                   # Diagnostic record (location, severity, message)
      DiagnosticBag.cs                # Collects diagnostics during compilation
    Syntax/
      Token.cs                        # Token type enum + Token record
      Lexer.cs                        # Tokenizer (source → token stream)
      SyntaxNode.cs                   # Base CST node types
      Nodes/                          # CST node definitions per construct
        CompilationUnitNode.cs        # Top-level: namespace, uses, declarations
        TypeDeclarationNodes.cs       # class, trait, union, impl
        MemberNodes.cs                # fields, methods, properties
        StatementNodes.cs             # var, if, for, foreach, while, using, return, match
        ExpressionNodes.cs            # literals, calls, member access, new, own, ref mut
        PatternNodes.cs               # match/switch patterns
      Parser.cs                       # Recursive-descent parser
    Binding/
      BoundNode.cs                    # Base bound (typed) AST node
      BoundNodes/
        BoundExpressions.cs           # Typed expression nodes
        BoundStatements.cs            # Typed statement nodes
        BoundDeclarations.cs          # Typed declarations
      Symbols/
        Symbol.cs                     # Base symbol type
        TypeSymbol.cs                 # Type symbols (class, trait, union, .NET types)
        MethodSymbol.cs               # Method/function symbols
        VariableSymbol.cs             # Local variables, parameters, fields
        NamespaceSymbol.cs            # Namespace symbols
      Binder.cs                       # CST → bound AST (type checking + name resolution)
      BuiltinTypes.cs                 # Option<T>, Result<T,E>, primitive type mappings
    Ownership/
      OwnershipState.cs               # Per-variable ownership state (owned, moved, borrowed, disposed)
      OwnershipAnalyzer.cs            # Walks bound AST to check ownership rules
      BorrowChecker.cs                # Enforces aliasing XOR mutability
    Emit/
      Emitter.cs                      # Bound AST → .NET assembly via Mono.Cecil
      TypeEmitter.cs                  # Emits class/trait/union type definitions
      MethodEmitter.cs                # Emits method bodies as IL
      BuiltinTypeEmitter.cs           # Emits Option<T>, Result<T,E> sealed hierarchies
    Compilation.cs                    # Orchestrates the full pipeline
  Cobalt.Cli/                         # cobaltc command-line tool
    Cobalt.Cli.csproj
    Program.cs                        # CLI entry point (parse args, invoke compiler)
  Cobalt.Compiler.Tests/              # Unit + integration tests
    Cobalt.Compiler.Tests.csproj
    Syntax/
      LexerTests.cs
      ParserTests.cs
    Binding/
      BinderTests.cs
    Ownership/
      OwnershipAnalyzerTests.cs
      BorrowCheckerTests.cs
    Emit/
      EmitterTests.cs
    Integration/
      EndToEndTests.cs                # Compile .co files → load and run the resulting assembly
```

---

## Task 1: Project Scaffolding

**Files:**
- Create: `src/Cobalt.Compiler/Cobalt.Compiler.csproj`
- Create: `src/Cobalt.Cli/Cobalt.Cli.csproj`
- Create: `src/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj`
- Modify: `Cobalt.slnx`

- [ ] **Step 1: Create the compiler class library project**

```bash
dotnet new classlib -n Cobalt.Compiler -o src/Cobalt.Compiler
rm src/Cobalt.Compiler/Class1.cs
```

Target `net10.0`. Add `Mono.Cecil` NuGet dependency and project reference to `Cobalt.Annotations`.

```xml
<ItemGroup>
    <PackageReference Include="Mono.Cecil" Version="0.11.6" />
    <ProjectReference Include="../Cobalt.Annotations/Cobalt.Annotations.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Create the CLI console project**

```bash
dotnet new console -n Cobalt.Cli -o src/Cobalt.Cli
rm src/Cobalt.Cli/Program.cs
```

Add project reference to `Cobalt.Compiler`.

- [ ] **Step 3: Create the test project**

```bash
dotnet new xunit -n Cobalt.Compiler.Tests -o src/Cobalt.Compiler.Tests
rm src/Cobalt.Compiler.Tests/UnitTest1.cs
```

Add project reference to `Cobalt.Compiler`.

- [ ] **Step 4: Add all projects to the solution**

```bash
dotnet sln Cobalt.slnx add src/Cobalt.Compiler/Cobalt.Compiler.csproj src/Cobalt.Cli/Cobalt.Cli.csproj src/Cobalt.Compiler.Tests/Cobalt.Compiler.Tests.csproj
```

- [ ] **Step 5: Create the Diagnostics infrastructure**

Create `Diagnostic.cs`:
```csharp
namespace Cobalt.Compiler.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

public record TextLocation(string FileName, int Line, int Column, int Length);

public record Diagnostic(DiagnosticSeverity Severity, string Id, string Message, TextLocation Location);
```

Create `DiagnosticBag.cs`:
```csharp
namespace Cobalt.Compiler.Diagnostics;

public class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Report(DiagnosticSeverity severity, string id, string message, TextLocation location)
        => _diagnostics.Add(new(severity, id, message, location));

    public void ReportError(string id, string message, TextLocation location)
        => Report(DiagnosticSeverity.Error, id, message, location);
}
```

- [ ] **Step 6: Verify the solution builds**

```bash
dotnet build Cobalt.slnx   # solution file is at repo root
```

Expected: Build succeeds with no errors.

- [ ] **Step 7: Commit**

```bash
git add src/Cobalt.Compiler/ src/Cobalt.Cli/ src/Cobalt.Compiler.Tests/ Cobalt.slnx
git commit -m "Add compiler project scaffolding (B.1 setup)"
```

---

## Task 2: Lexer

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/Token.cs`
- Create: `src/Cobalt.Compiler/Syntax/Lexer.cs`
- Create: `src/Cobalt.Compiler.Tests/Syntax/LexerTests.cs`

- [ ] **Step 1: Write failing lexer tests**

Test tokenization of key Cobalt constructs:

```csharp
public class LexerTests
{
    [Fact]
    public void Tokenizes_keywords()
    {
        var tokens = Lexer.Tokenize("namespace class trait union use own ref mut match impl using foreach var if else while for return new public private protected internal static void int string bool float double");
        var kinds = tokens.Select(t => t.Kind).ToList();

        Assert.Contains(TokenKind.Namespace, kinds);
        Assert.Contains(TokenKind.Class, kinds);
        Assert.Contains(TokenKind.Trait, kinds);
        Assert.Contains(TokenKind.Union, kinds);
        Assert.Contains(TokenKind.Use, kinds);
        Assert.Contains(TokenKind.Own, kinds);
        Assert.Contains(TokenKind.Ref, kinds);
        Assert.Contains(TokenKind.Mut, kinds);
        Assert.Contains(TokenKind.Match, kinds);
        Assert.Contains(TokenKind.Impl, kinds);
    }

    [Fact]
    public void Tokenizes_identifier()
    {
        var tokens = Lexer.Tokenize("myVariable _private Count123");
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("myVariable", tokens[0].Text);
    }

    [Fact]
    public void Tokenizes_string_literal()
    {
        var tokens = Lexer.Tokenize("\"hello world\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("hello world", tokens[0].Value);
    }

    [Fact]
    public void Tokenizes_interpolated_string()
    {
        var tokens = Lexer.Tokenize("$\"count is {n}\"");
        Assert.Equal(TokenKind.InterpolatedStringStart, tokens[0].Kind);
    }

    [Fact]
    public void Tokenizes_integer_literal()
    {
        var tokens = Lexer.Tokenize("42 0xFF 0b1010");
        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal(42, tokens[0].Value);
    }

    [Fact]
    public void Tokenizes_operators_and_punctuation()
    {
        var tokens = Lexer.Tokenize("{ } ( ) [ ] ; , . : => == != < > <= >= + - * / = !");
        Assert.Equal(TokenKind.OpenBrace, tokens[0].Kind);
        Assert.Equal(TokenKind.CloseBrace, tokens[1].Kind);
        Assert.Equal(TokenKind.Arrow, tokens.First(t => t.Kind == TokenKind.Arrow).Kind);
    }

    [Fact]
    public void Tracks_line_and_column()
    {
        var tokens = Lexer.Tokenize("a\nb");
        Assert.Equal(1, tokens[0].Location.Line);
        Assert.Equal(1, tokens[0].Location.Column);
        Assert.Equal(2, tokens[1].Location.Line);
        Assert.Equal(1, tokens[1].Location.Column);
    }

    [Fact]
    public void Skips_comments()
    {
        var tokens = Lexer.Tokenize("a // comment\nb");
        Assert.Equal(2, tokens.Count(t => t.Kind != TokenKind.EndOfFile));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "LexerTests" -v n
```

Expected: Compilation errors (types don't exist yet).

- [ ] **Step 3: Implement Token types**

Create `Token.cs` with `TokenKind` enum covering all Cobalt keywords, punctuation, literals, and special tokens:

```csharp
namespace Cobalt.Compiler.Syntax;

public enum TokenKind
{
    // Literals
    IntLiteral, FloatLiteral, StringLiteral, InterpolatedStringStart, InterpolatedStringEnd,
    InterpolatedStringText, InterpolatedStringHole, BoolLiteral,

    // Identifier
    Identifier,

    // Keywords
    Namespace, Use, Class, Trait, Union, Impl,
    Own, Ref, Mut, Match,
    Var, If, Else, While, For, Foreach, In, Return, New, Using, Switch,
    Public, Private, Protected, Internal, Static, Sealed, Abstract, Override, Virtual,
    Void, Int, String, Bool, Float, Double, Long, Byte, Char, Object,
    Is, Null, True, False, This, Base, Get, Set,
    Fn, Async, Await, Send, Sync, // reserved

    // Operators & Punctuation
    OpenBrace, CloseBrace, OpenParen, CloseParen, OpenBracket, CloseBracket,
    Semicolon, Comma, Dot, Colon, Arrow, // =>
    Equals, EqualsEquals, BangEquals, Bang,
    LessThan, GreaterThan, LessEquals, GreaterEquals,
    Plus, Minus, Star, Slash, Percent,
    AmpersandAmpersand, PipePipe,
    PlusEquals, MinusEquals, StarEquals, SlashEquals,
    DollarQuote, // $" for interpolated strings

    // Special
    EndOfFile, Bad,
}

public record Token(TokenKind Kind, string Text, object? Value, TextLocation Location);
```

- [ ] **Step 4: Implement the Lexer**

Create `Lexer.cs` — a character-by-character scanner that produces tokens. Key responsibilities:
- Keyword recognition (check identifier text against keyword table)
- String literals (regular and interpolated)
- Numeric literals (int, float, hex `0x`, binary `0b`)
- Operators and punctuation (including multi-char `=>`, `==`, `!=`, `<=`, `>=`, `&&`, `||`)
- Line/column tracking
- Single-line comments (`//`) and multi-line comments (`/* */`)
- The `Lexer.Tokenize(string source, string fileName = "")` static method returns `List<Token>`

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "LexerTests" -v n
```

Expected: All tests pass.

- [ ] **Step 6: Test lexer against the sample .co files**

Add an integration test that lexes the three sample files:

```csharp
[Fact]
public void Lexes_sample_main_co()
{
    var source = File.ReadAllText("../../../../../samples/cobalt-syntax/main.co");
    var tokens = Lexer.Tokenize(source, "main.co");
    Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Bad);
    Assert.Contains(tokens, t => t.Kind == TokenKind.Namespace);
    Assert.Contains(tokens, t => t.Kind == TokenKind.Use);
    Assert.Contains(tokens, t => t.Kind == TokenKind.Own);
    Assert.Contains(tokens, t => t.Kind == TokenKind.Match);
}
```

- [ ] **Step 7: Run integration test**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "Lexes_sample" -v n
```

Expected: PASS — all sample files lex without `Bad` tokens.

- [ ] **Step 8: Commit**

```bash
git add src/Cobalt.Compiler/Syntax/ src/Cobalt.Compiler.Tests/Syntax/
git commit -m "Add lexer with keyword, literal, and operator support (B.1)"
```

---

## Task 3: Parser — Declarations

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/SyntaxNode.cs`
- Create: `src/Cobalt.Compiler/Syntax/Nodes/CompilationUnitNode.cs`
- Create: `src/Cobalt.Compiler/Syntax/Nodes/TypeDeclarationNodes.cs`
- Create: `src/Cobalt.Compiler/Syntax/Nodes/MemberNodes.cs`
- Create: `src/Cobalt.Compiler/Syntax/Parser.cs` (partial — declarations only)
- Create: `src/Cobalt.Compiler.Tests/Syntax/ParserTests.cs`

- [ ] **Step 1: Write failing parser tests for declarations**

```csharp
public class ParserTests
{
    [Fact]
    public void Parses_namespace_and_use()
    {
        var tree = Parser.Parse("namespace Foo.Bar;\nuse System;\nuse System.IO;");
        Assert.Equal("Foo.Bar", tree.Namespace?.Name);
        Assert.Equal(2, tree.Uses.Count);
    }

    [Fact]
    public void Parses_class_declaration()
    {
        var tree = Parser.Parse("class Foo { }");
        Assert.Single(tree.Declarations);
        Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
    }

    [Fact]
    public void Parses_class_with_trait_implementation()
    {
        var tree = Parser.Parse("class Foo : IBar { }");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        Assert.Single(cls.BaseTypes);
    }

    [Fact]
    public void Parses_trait_declaration()
    {
        var tree = Parser.Parse("trait IFoo {\n  string Name { get; }\n  void Run();\n}");
        var trait = Assert.IsType<TraitDeclarationNode>(tree.Declarations[0]);
        Assert.Equal(2, trait.Members.Count);
    }

    [Fact]
    public void Parses_union_declaration()
    {
        var tree = Parser.Parse("union Result {\n  Ok(int Value),\n  Err(string Message),\n}");
        var union = Assert.IsType<UnionDeclarationNode>(tree.Declarations[0]);
        Assert.Equal(2, union.Variants.Count);
        Assert.Equal("Ok", union.Variants[0].Name);
    }

    [Fact]
    public void Parses_impl_block()
    {
        var tree = Parser.Parse("impl IFoo for Bar {\n  void Run() { }\n}");
        Assert.IsType<ImplBlockNode>(tree.Declarations[0]);
    }

    [Fact]
    public void Parses_method_with_ownership_modifiers()
    {
        var tree = Parser.Parse("class Foo {\n  void Process(own Stream s, ref mut List<int> l) { }\n}");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        var method = Assert.IsType<MethodDeclarationNode>(cls.Members[0]);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Equal(OwnershipModifier.Own, method.Parameters[0].Ownership);
        Assert.Equal(OwnershipModifier.RefMut, method.Parameters[1].Ownership);
    }

    [Fact]
    public void Parses_owned_field()
    {
        var tree = Parser.Parse("class Foo {\n  own Stream _input;\n}");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        var field = Assert.IsType<FieldDeclarationNode>(cls.Members[0]);
        Assert.Equal(OwnershipModifier.Own, field.Ownership);
    }

    [Fact]
    public void Parses_own_return_type()
    {
        var tree = Parser.Parse("class Foo {\n  public static own Foo Create() { }\n}");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        var method = Assert.IsType<MethodDeclarationNode>(cls.Members[0]);
        Assert.Equal(OwnershipModifier.Own, method.ReturnOwnership);
    }

    [Fact]
    public void Parses_free_standing_function()
    {
        var tree = Parser.Parse("string Summarize(ref int x) { }");
        Assert.Single(tree.Declarations);
        Assert.IsType<FunctionDeclarationNode>(tree.Declarations[0]);
    }

    [Fact]
    public void Parses_expression_bodied_property()
    {
        var tree = Parser.Parse("class Foo {\n  public string Name => \"hello\";\n}");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        Assert.IsType<PropertyDeclarationNode>(cls.Members[0]);
    }

    [Fact]
    public void Parses_field_with_initializer()
    {
        var tree = Parser.Parse("class Foo {\n  own List<own int> _items = new();\n}");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        var field = Assert.IsType<FieldDeclarationNode>(cls.Members[0]);
        Assert.NotNull(field.Initializer);
    }

    [Fact]
    public void Parses_own_in_generic_type_argument()
    {
        var tree = Parser.Parse("class Foo {\n  own List<own IBar> _items;\n}");
        var cls = Assert.IsType<ClassDeclarationNode>(tree.Declarations[0]);
        var field = Assert.IsType<FieldDeclarationNode>(cls.Members[0]);
        Assert.Equal(OwnershipModifier.Own, field.Ownership);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "ParserTests" -v n
```

Expected: Compilation errors.

- [ ] **Step 3: Implement CST node types**

Create `SyntaxNode.cs` with the base class and `OwnershipModifier` enum:

```csharp
public abstract record SyntaxNode;
public enum OwnershipModifier { None, Own, Ref, RefMut }
```

Create node files for each construct group:
- `CompilationUnitNode.cs` — top-level compilation unit with namespace, uses, declarations, top-level statements
- `TypeDeclarationNodes.cs` — `ClassDeclarationNode`, `TraitDeclarationNode`, `UnionDeclarationNode`, `ImplBlockNode`, `FunctionDeclarationNode`
- `MemberNodes.cs` — `MethodDeclarationNode`, `FieldDeclarationNode`, `PropertyDeclarationNode`, `ParameterNode`, `TypeReferenceNode`, `UnionVariantNode`

- [ ] **Step 4: Implement the recursive-descent parser (declarations only)**

Create `Parser.cs`. Key structure:
- Constructor takes `List<Token>` and a `DiagnosticBag`
- `Parse(string source)` static entry point: lex then parse
- `ParseCompilationUnit()` — namespace, uses, then loop on declarations
- `ParseDeclaration()` — peek at keywords to dispatch to class/trait/union/impl/function
- `ParseClassDeclaration()`, `ParseTraitDeclaration()`, `ParseUnionDeclaration()`, `ParseImplBlock()`
- `ParseMethod()`, `ParseField()`, `ParseProperty()`, `ParseParameter()`
- `ParseTypeReference()` — handles simple names, generics (`T<U>`), and ownership-qualified types (`own T`)
- `ParseOwnershipModifier()` — checks for `own`, `ref`, `ref mut`
- Error recovery: on unexpected token, skip to next `}` or `;` and report diagnostic

Statement and expression bodies are parsed as opaque blocks for now (skip matching `{}`). They'll be fully parsed in Tasks 4–5.

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "ParserTests" -v n
```

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Cobalt.Compiler/Syntax/ src/Cobalt.Compiler.Tests/Syntax/
git commit -m "Add parser for declarations: class, trait, union, impl, functions (B.1)"
```

---

## Task 4: Parser — Statements

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/Nodes/StatementNodes.cs`
- Modify: `src/Cobalt.Compiler/Syntax/Parser.cs` (add statement parsing)
- Modify: `src/Cobalt.Compiler.Tests/Syntax/ParserTests.cs`

- [ ] **Step 1: Write failing statement parser tests**

```csharp
[Fact]
public void Parses_var_declaration()
{
    var tree = ParseMethodBody("var x = 42;");
    Assert.IsType<VarDeclarationStatement>(tree.Statements[0]);
}

[Fact]
public void Parses_using_var()
{
    var tree = ParseMethodBody("using var s = Create();");
    var stmt = Assert.IsType<UsingVarStatement>(tree.Statements[0]);
    Assert.True(stmt.IsUsing);
}

[Fact]
public void Parses_if_else()
{
    var tree = ParseMethodBody("if (x > 0) { y = 1; } else { y = 2; }");
    Assert.IsType<IfStatement>(tree.Statements[0]);
}

[Fact]
public void Parses_while_loop()
{
    var tree = ParseMethodBody("while (x > 0) { x = x - 1; }");
    Assert.IsType<WhileStatement>(tree.Statements[0]);
}

[Fact]
public void Parses_for_loop()
{
    var tree = ParseMethodBody("for (var i = 0; i < 10; i++) { }");
    Assert.IsType<ForStatement>(tree.Statements[0]);
}

[Fact]
public void Parses_foreach_with_ownership()
{
    var tree = ParseMethodBody("foreach (ref item in list) { }");
    var stmt = Assert.IsType<ForeachStatement>(tree.Statements[0]);
    Assert.Equal(OwnershipModifier.Ref, stmt.Ownership);
}

[Fact]
public void Parses_foreach_ref_mut()
{
    var tree = ParseMethodBody("foreach (ref mut item in list) { }");
    var stmt = Assert.IsType<ForeachStatement>(tree.Statements[0]);
    Assert.Equal(OwnershipModifier.RefMut, stmt.Ownership);
}

[Fact]
public void Parses_return_statement()
{
    var tree = ParseMethodBody("return Success(42);");
    Assert.IsType<ReturnStatement>(tree.Statements[0]);
}

[Fact]
public void Parses_match_statement()
{
    var tree = ParseMethodBody("match (x) {\n  Some(var v) => Use(v),\n  None => { },\n};");
    Assert.IsType<MatchStatement>(tree.Statements[0]);
}
```

Helper method `ParseMethodBody(string body)` wraps body in a method and extracts the body block.

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement statement node types**

Create `StatementNodes.cs` with records for:
- `BlockStatement`, `VarDeclarationStatement`, `UsingVarStatement`
- `IfStatement`, `WhileStatement`, `ForStatement`, `ForeachStatement`
- `ReturnStatement`, `ExpressionStatement`, `MatchStatement`, `MatchArm`

- [ ] **Step 4: Implement statement parsing in Parser.cs**

Add `ParseStatement()` with dispatch on leading token:
- `var` → `ParseVarDeclaration()`
- `using` + `var` → `ParseUsingVar()`
- `if` → `ParseIf()`
- `while` → `ParseWhile()`
- `for` → `ParseFor()`
- `foreach` → `ParseForeach()` (check for `ref`, `ref mut`, `own`, `var` after `(`)
- `return` → `ParseReturn()`
- `match` → `ParseMatch()` (parse arms with patterns; handle nullary variants like `None` as bare identifiers without parentheses)
- `{` → `ParseBlock()`
- Otherwise → `ParseExpressionStatement()`

- [ ] **Step 5: Run tests to verify they pass**
- [ ] **Step 6: Commit**

```bash
git commit -m "Add parser for statements: var, if, while, for, foreach, match, return (B.1)"
```

---

## Task 5: Parser — Expressions

**Files:**
- Create: `src/Cobalt.Compiler/Syntax/Nodes/ExpressionNodes.cs`
- Create: `src/Cobalt.Compiler/Syntax/Nodes/PatternNodes.cs`
- Modify: `src/Cobalt.Compiler/Syntax/Parser.cs` (add expression parsing)
- Modify: `src/Cobalt.Compiler.Tests/Syntax/ParserTests.cs`

- [ ] **Step 1: Write failing expression parser tests**

```csharp
[Fact]
public void Parses_integer_literal() { ... }

[Fact]
public void Parses_string_literal() { ... }

[Fact]
public void Parses_binary_expression()
{
    var expr = ParseExpression("a + b * c");
    // * binds tighter than +
    Assert.IsType<BinaryExpression>(expr);
}

[Fact]
public void Parses_method_call()
{
    var expr = ParseExpression("obj.Method(a, b)");
    Assert.IsType<MemberAccessExpression>(expr);
}

[Fact]
public void Parses_new_expression()
{
    var expr = ParseExpression("new List<string>()");
    Assert.IsType<NewExpression>(expr);
}

[Fact]
public void Parses_own_at_call_site()
{
    var expr = ParseExpression("Process(own stream)");
    var call = Assert.IsType<CallExpression>(expr);
    Assert.Equal(OwnershipModifier.Own, call.Arguments[0].Ownership);
}

[Fact]
public void Parses_own_new_expression()
{
    var expr = ParseExpression("Process(own new Foo())");
    var call = Assert.IsType<CallExpression>(expr);
    Assert.Equal(OwnershipModifier.Own, call.Arguments[0].Ownership);
    Assert.IsType<NewExpression>(call.Arguments[0].Expression);
}

[Fact]
public void Parses_ref_mut_at_call_site()
{
    var expr = ParseExpression("Apply(ref mut list)");
    var call = Assert.IsType<CallExpression>(expr);
    Assert.Equal(OwnershipModifier.RefMut, call.Arguments[0].Ownership);
}

[Fact]
public void Parses_switch_expression()
{
    var expr = ParseExpression("x switch {\n  Success(var n) => n,\n  Error(var e) => 0,\n}");
    Assert.IsType<SwitchExpression>(expr);
}

[Fact]
public void Parses_is_pattern()
{
    var expr = ParseExpression("x is Some(var v)");
    Assert.IsType<IsPatternExpression>(expr);
}

[Fact]
public void Parses_object_initializer_with_own()
{
    var expr = ParseExpression("new Foo { Field = own value }");
    Assert.IsType<NewExpression>(expr);
}

[Fact]
public void Parses_interpolated_string()
{
    var expr = ParseExpression("$\"count is {n}\"");
    Assert.IsType<InterpolatedStringExpression>(expr);
}

[Fact]
public void Parses_lambda()
{
    var expr = ParseExpression("x => x + 1");
    Assert.IsType<LambdaExpression>(expr);
}
```

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement expression and pattern node types**

Create `ExpressionNodes.cs`:
- `LiteralExpression`, `IdentifierExpression`, `BinaryExpression`, `UnaryExpression`
- `CallExpression`, `MemberAccessExpression`, `IndexExpression`
- `NewExpression`, `ObjectInitializerExpression`
- `AssignmentExpression`, `CompoundAssignmentExpression`
- `SwitchExpression`, `IsPatternExpression`, `InterpolatedStringExpression`
- `LambdaExpression`, `CastExpression`
- `ArgumentNode` (expression + optional ownership modifier)

Create `PatternNodes.cs`:
- `VariantPattern` (e.g., `Some(var x)`)
- `VarPattern` (e.g., `var x`)
- `WildcardPattern` (e.g., `_`)

- [ ] **Step 4: Implement Pratt expression parser**

Use Pratt parsing (precedence climbing) for expressions. Define binding powers for operators:
- Assignment `=`: bp 1
- Logical `||`: bp 2, `&&`: bp 3
- Equality `==`, `!=`: bp 4
- Comparison `<`, `>`, `<=`, `>=`: bp 5
- Additive `+`, `-`: bp 6
- Multiplicative `*`, `/`, `%`: bp 7
- Unary `!`, `-`: bp 8
- Postfix `.`, `()`, `[]`: bp 9

Handle:
- `own`/`ref`/`ref mut` as prefix modifiers on arguments
- `is` as a postfix operator with pattern RHS
- `switch` as a postfix operator with match body
- `new` as a prefix keyword
- `$"..."` as interpolated strings

- [ ] **Step 5: Run tests to verify they pass**
- [ ] **Step 6: Parse the full sample files end-to-end**

```csharp
[Theory]
[InlineData("main.co")]
[InlineData("processor.co")]
[InlineData("transforms.co")]
public void Parses_sample_file(string fileName)
{
    var source = File.ReadAllText($"../../../../../samples/cobalt-syntax/{fileName}");
    var result = Parser.Parse(source, fileName);
    Assert.False(result.Diagnostics.HasErrors, string.Join("\n", result.Diagnostics.Diagnostics.Select(d => d.Message)));
}
```

- [ ] **Step 7: Run sample file tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "Parses_sample_file" -v n
```

Expected: All three sample files parse without errors.

- [ ] **Step 8: Commit**

```bash
git commit -m "Add parser for expressions, patterns, and full sample file parsing (B.1)"
```

---

## Task 6: Symbols and Type Resolution

**Files:**
- Create: `src/Cobalt.Compiler/Binding/Symbols/Symbol.cs`
- Create: `src/Cobalt.Compiler/Binding/Symbols/TypeSymbol.cs`
- Create: `src/Cobalt.Compiler/Binding/Symbols/MethodSymbol.cs`
- Create: `src/Cobalt.Compiler/Binding/Symbols/VariableSymbol.cs`
- Create: `src/Cobalt.Compiler/Binding/Symbols/NamespaceSymbol.cs`
- Create: `src/Cobalt.Compiler/Binding/BuiltinTypes.cs`
- Create: `src/Cobalt.Compiler/Binding/Scope.cs`
- Create: `src/Cobalt.Compiler.Tests/Binding/BinderTests.cs`

- [ ] **Step 1: Write failing symbol resolution tests**

```csharp
public class BinderTests
{
    [Fact]
    public void Resolves_class_type()
    {
        var bound = Bind("class Foo { }");
        Assert.Contains(bound.Types, t => t.Name == "Foo");
    }

    [Fact]
    public void Resolves_trait_as_interface_symbol()
    {
        var bound = Bind("trait IBar {\n  void Run();\n}");
        var trait = bound.Types.First(t => t.Name == "IBar");
        Assert.True(trait.IsTrait);
    }

    [Fact]
    public void Resolves_union_variants()
    {
        var bound = Bind("union Result {\n  Ok(int Value),\n  Err(string Message),\n}");
        var union = bound.Types.First(t => t.Name == "Result");
        Assert.Equal(2, union.Variants.Count);
    }

    [Fact]
    public void Resolves_dotnet_type_from_use()
    {
        var bound = Bind("use System;\nclass Foo {\n  void Bar() {\n    var s = Console.ReadLine();\n  }\n}");
        Assert.False(bound.Diagnostics.HasErrors);
    }

    [Fact]
    public void Reports_error_for_undeclared_type()
    {
        var bound = Bind("class Foo {\n  UnknownType _x;\n}");
        Assert.True(bound.Diagnostics.HasErrors);
    }

    [Fact]
    public void Resolves_builtin_option_type()
    {
        var bound = Bind("class Foo {\n  Option<int> _x;\n}");
        Assert.False(bound.Diagnostics.HasErrors);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement Symbol hierarchy**

Create symbol classes that represent resolved types, methods, variables, and namespaces. Key design:
- `TypeSymbol` holds: name, kind (class/trait/union/builtin/.net), members, variants (for unions), generic parameters, base types
- `MethodSymbol` holds: name, parameters (with ownership), return type, return ownership, containing type
- `VariableSymbol` holds: name, type, ownership, kind (local/parameter/field)
- `NamespaceSymbol` holds: name, child types

- [ ] **Step 4: Implement BuiltinTypes registry**

Create `BuiltinTypes.cs` with pre-defined symbols for:
- Primitives: `int`, `float`, `double`, `bool`, `string`, `void`, `long`, `byte`, `char`, `object`
- Cobalt built-ins: `Option<T>` (with `Some`, `None` variants), `Result<T, E>` (with `Ok`, `Err` variants)
- Map each to its .NET CLR type

- [ ] **Step 5: Implement Scope and .NET type resolution**

Create `Scope.cs` — a chain of scopes (namespace → type → method → block) for name lookup. The top-level scope loads .NET types from referenced assemblies using `System.Reflection` metadata reading (or Mono.Cecil's assembly reader) based on `use` declarations.

- [ ] **Step 6: Run tests to verify they pass**
- [ ] **Step 7: Commit**

```bash
git commit -m "Add symbol model, builtin types, and scope-based name resolution (B.2)"
```

---

## Task 7: Binder (Type Checking)

**Files:**
- Create: `src/Cobalt.Compiler/Binding/BoundNode.cs`
- Create: `src/Cobalt.Compiler/Binding/BoundNodes/BoundExpressions.cs`
- Create: `src/Cobalt.Compiler/Binding/BoundNodes/BoundStatements.cs`
- Create: `src/Cobalt.Compiler/Binding/BoundNodes/BoundDeclarations.cs`
- Create: `src/Cobalt.Compiler/Binding/Binder.cs`
- Modify: `src/Cobalt.Compiler.Tests/Binding/BinderTests.cs`

- [ ] **Step 1: Write failing type-checking tests**

```csharp
[Fact]
public void Type_checks_variable_declaration()
{
    var bound = Bind("class Foo {\n  void Bar() {\n    var x = 42;\n  }\n}");
    Assert.False(bound.Diagnostics.HasErrors);
}

[Fact]
public void Reports_type_mismatch_in_assignment()
{
    var bound = Bind("class Foo {\n  void Bar() {\n    int x = \"hello\";\n  }\n}");
    Assert.True(bound.Diagnostics.HasErrors);
}

[Fact]
public void Type_checks_method_call_arguments()
{
    var bound = Bind("class Foo {\n  void Take(int x) { }\n  void Bar() {\n    Take(42);\n  }\n}");
    Assert.False(bound.Diagnostics.HasErrors);
}

[Fact]
public void Reports_wrong_argument_count()
{
    var bound = Bind("class Foo {\n  void Take(int x) { }\n  void Bar() {\n    Take(1, 2);\n  }\n}");
    Assert.True(bound.Diagnostics.HasErrors);
}

[Fact]
public void Type_checks_union_variant_construction()
{
    var bound = Bind("union R { Ok(int V), Err(string M) }\nclass Foo {\n  R Bar() { return Ok(42); }\n}");
    Assert.False(bound.Diagnostics.HasErrors);
}

[Fact]
public void Type_checks_switch_expression_exhaustiveness()
{
    var bound = Bind("union R { A(int X), B(string Y) }\nclass Foo {\n  int Bar(R r) {\n    return r switch {\n      A(var x) => x,\n    };\n  }\n}");
    Assert.True(bound.Diagnostics.HasErrors); // missing B arm
}
```

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement bound AST nodes**

Create typed AST node types that mirror the CST but with resolved type information:
- `BoundExpressions.cs` — each expression carries its resolved `TypeSymbol`
- `BoundStatements.cs` — typed statements
- `BoundDeclarations.cs` — typed declarations with resolved symbols

- [ ] **Step 4: Implement the Binder**

Create `Binder.cs` — walks CST and produces bound AST:
- Registers all types in a first pass (forward declarations)
- Resolves method signatures in a second pass
- Binds method bodies in a third pass
- Type inference for `var` declarations
- Method overload resolution
- Union variant construction as function calls
- Pattern matching exhaustiveness checking (for switch/match on unions)
- Reports diagnostics for type errors

- [ ] **Step 5: Run tests to verify they pass**
- [ ] **Step 6: Commit**

```bash
git commit -m "Add binder with type checking, inference, and exhaustiveness checking (B.2)"
```

---

## Task 8: Ownership Analyzer

**Files:**
- Create: `src/Cobalt.Compiler/Ownership/OwnershipState.cs`
- Create: `src/Cobalt.Compiler/Ownership/OwnershipAnalyzer.cs`
- Create: `src/Cobalt.Compiler.Tests/Ownership/OwnershipAnalyzerTests.cs`

- [ ] **Step 1: Write failing ownership tests**

```csharp
public class OwnershipAnalyzerTests
{
    [Fact]
    public void Detects_use_after_move()
    {
        var diags = AnalyzeOwnership(@"
            class Foo {
                void Take(own int x) { }
                void Bar() {
                    var x = 42;
                    Take(own x);
                    var y = x; // ERROR: use after move
                }
            }");
        Assert.Contains(diags, d => d.Id == "CB1001"); // use-after-move
    }

    [Fact]
    public void Allows_use_before_move()
    {
        var diags = AnalyzeOwnership(@"
            class Foo {
                void Take(own int x) { }
                void Bar() {
                    var x = 42;
                    var y = x;
                    Take(own x);
                }
            }");
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Detects_owned_disposable_not_disposed()
    {
        var diags = AnalyzeOwnership(@"
            use System.IO;
            class Foo {
                void Bar() {
                    var s = File.OpenRead(""test"");
                    // s is never disposed or moved
                }
            }");
        Assert.Contains(diags, d => d.Id == "CB1002"); // owned disposable not disposed
    }

    [Fact]
    public void Using_var_satisfies_disposal()
    {
        var diags = AnalyzeOwnership(@"
            use System.IO;
            class Foo {
                void Bar() {
                    using var s = File.OpenRead(""test"");
                }
            }");
        Assert.DoesNotContain(diags, d => d.Id == "CB1002");
    }

    [Fact]
    public void Ownership_transfer_satisfies_disposal()
    {
        var diags = AnalyzeOwnership(@"
            use System.IO;
            class Foo {
                void Take(own Stream s) { }
                void Bar() {
                    var s = File.OpenRead(""test"");
                    Take(own s);
                }
            }");
        Assert.DoesNotContain(diags, d => d.Id == "CB1002");
    }

    [Fact]
    public void Detects_use_after_dispose()
    {
        var diags = AnalyzeOwnership(@"
            use System.IO;
            class Foo {
                void Bar() {
                    var s = File.OpenRead(""test"");
                    s.Dispose();
                    s.Read(new byte[1], 0, 1); // ERROR
                }
            }");
        Assert.Contains(diags, d => d.Id == "CB1003"); // use-after-dispose
    }

    [Fact]
    public void Foreach_own_consumes_collection()
    {
        var diags = AnalyzeOwnership(@"
            use System.Collections.Generic;
            class Foo {
                void Bar() {
                    var list = new List<int>();
                    foreach (own item in list) { }
                    list.Add(1); // ERROR: list was consumed by foreach own
                }
            }");
        Assert.Contains(diags, d => d.Id == "CB1001"); // use-after-move
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement OwnershipState**

Create `OwnershipState.cs`:
```csharp
public enum VariableState { Owned, Moved, Borrowed, MutBorrowed, Disposed, Uninitialized }
```

- [ ] **Step 4: Implement OwnershipAnalyzer**

Create `OwnershipAnalyzer.cs` — walks the bound AST and tracks per-variable state:
- On `var x = expr`: mark `x` as `Owned`
- On `Take(own x)`: mark `x` as `Moved`
- On `x.Dispose()`: mark `x` as `Disposed`
- On `using var x`: mark `x` as `Owned` with auto-dispose flag
- On any access to `x` after `Moved` → report CB1001
- On any access to `x` after `Disposed` → report CB1003
- At scope exit, any `Owned` variable implementing `IDisposable` not disposed or moved → report CB1002
- Track state through `if`/`else` branches (variable must be in same state on all paths)

- [ ] **Step 5: Run tests to verify they pass**
- [ ] **Step 6: Commit**

```bash
git commit -m "Add ownership analyzer: use-after-move, disposal tracking (B.3)"
```

---

## Task 9: Borrow Checker

**Files:**
- Create: `src/Cobalt.Compiler/Ownership/BorrowChecker.cs`
- Create: `src/Cobalt.Compiler.Tests/Ownership/BorrowCheckerTests.cs`

- [ ] **Step 1: Write failing borrow checker tests**

```csharp
public class BorrowCheckerTests
{
    [Fact]
    public void Allows_multiple_shared_borrows()
    {
        var diags = CheckBorrows(@"
            class Foo {
                void Read(ref int x) { }
                void Bar() {
                    var x = 42;
                    Read(ref x);
                    Read(ref x); // OK: multiple shared borrows
                }
            }");
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Allows_sequential_borrows_under_NLL()
    {
        // Under NLL, a borrow is dead after its last use.
        // Sequential calls don't overlap, so this is OK.
        var diags = CheckBorrows(@"
            class Foo {
                void Read(ref int x) { }
                void Write(ref mut int x) { }
                void Bar() {
                    var x = 42;
                    Read(ref x);      // shared borrow dies here (last use)
                    Write(ref mut x); // OK: no active shared borrow
                }
            }");
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Prevents_mutable_borrow_while_shared_borrow_active()
    {
        // The shared borrow is still live when the mutable borrow starts.
        var diags = CheckBorrows(@"
            class Foo {
                int _stored;
                void Store(ref int x) { _stored = x; }
                void Write(ref mut int x) { }
                void Bar() {
                    var x = 42;
                    Store(ref x);     // shared borrow — stored reference still live
                    Write(ref mut x); // ERROR: can't mutably borrow while shared borrow exists
                    Use(_stored);     // _stored still depends on x
                }
            }");
        Assert.Contains(diags, d => d.Id == "CB1004"); // aliasing violation
    }

    [Fact]
    public void Allows_sequential_mutable_borrows()
    {
        // Under NLL, sequential mutable borrows don't overlap.
        var diags = CheckBorrows(@"
            class Foo {
                void Write(ref mut int x) { }
                void Bar() {
                    var x = 42;
                    Write(ref mut x); // dies here
                    Write(ref mut x); // OK: previous borrow is dead
                }
            }");
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Prevents_move_while_borrowed()
    {
        var diags = CheckBorrows(@"
            class Foo {
                void Read(ref int x) { }
                void Take(own int x) { }
                void Bar() {
                    var x = 42;
                    Read(ref x);
                    Take(own x); // ERROR: can't move while borrowed
                }
            }");
        Assert.Contains(diags, d => d.Id == "CB1005");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement BorrowChecker**

Create `BorrowChecker.cs` — enforces aliasing XOR mutability:
- Track active borrows per variable with NLL-style liveness (a borrow lives until its last use, not until scope exit)
- Shared borrow (`ref`): allowed if no active mutable borrow
- Mutable borrow (`ref mut`): allowed only if no other borrows active
- Move (`own`): allowed only if no borrows active
- Report CB1004 for aliasing violations
- Report CB1005 for move-while-borrowed

- [ ] **Step 4: Run tests to verify they pass**
- [ ] **Step 5: Commit**

```bash
git commit -m "Add borrow checker: aliasing XOR mutability enforcement (B.3)"
```

---

## Task 10: IL Emitter — Type Definitions

**Files:**
- Create: `src/Cobalt.Compiler/Emit/Emitter.cs`
- Create: `src/Cobalt.Compiler/Emit/TypeEmitter.cs`
- Create: `src/Cobalt.Compiler/Emit/BuiltinTypeEmitter.cs`
- Create: `src/Cobalt.Compiler.Tests/Emit/EmitterTests.cs`

- [ ] **Step 1: Write failing emitter tests**

```csharp
public class EmitterTests
{
    [Fact]
    public void Emits_class_to_assembly()
    {
        var asm = Compile("class Foo { }");
        var type = asm.GetType("Foo");
        Assert.NotNull(type);
        Assert.True(type.IsClass);
    }

    [Fact]
    public void Emits_trait_as_interface()
    {
        var asm = Compile("trait IFoo {\n  void Run();\n}");
        var type = asm.GetType("IFoo");
        Assert.NotNull(type);
        Assert.True(type.IsInterface);
    }

    [Fact]
    public void Emits_union_as_sealed_hierarchy()
    {
        var asm = Compile("union Result {\n  Ok(int Value),\n  Err(string Message),\n}");
        var baseType = asm.GetType("Result");
        Assert.NotNull(baseType);
        Assert.True(baseType.IsAbstract);
        Assert.True(baseType.IsSealed is false); // abstract base, sealed variants
        var okType = asm.GetType("Result+Ok");
        Assert.NotNull(okType);
        Assert.True(okType.IsSealed);
    }

    [Fact]
    public void Emits_ownership_attributes_on_parameters()
    {
        var asm = Compile("class Foo {\n  void Bar(own int x) { }\n}");
        var method = asm.GetType("Foo")!.GetMethod("Bar")!;
        var param = method.GetParameters()[0];
        Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");
    }

    [Fact]
    public void Emits_class_implementing_trait()
    {
        var asm = Compile("trait IFoo {\n  void Run();\n}\nclass Bar : IFoo {\n  public void Run() { }\n}");
        var type = asm.GetType("Bar")!;
        Assert.Contains(type.GetInterfaces(), i => i.Name == "IFoo");
    }

    [Fact]
    public void Emits_impl_block_as_interface_implementation()
    {
        var asm = Compile("trait IFoo {\n  void Run();\n}\nclass Bar { }\nimpl IFoo for Bar {\n  public void Run() { }\n}");
        var type = asm.GetType("Bar")!;
        Assert.Contains(type.GetInterfaces(), i => i.Name == "IFoo");
    }

    [Fact]
    public void Emits_object_initializer()
    {
        var asm = Compile(@"
            class Foo {
                public int X;
                public static Foo Create() { return new Foo { X = 42 }; }
            }");
        var obj = asm.GetType("Foo")!.GetMethod("Create")!.Invoke(null, []);
        Assert.Equal(42, asm.GetType("Foo")!.GetField("X")!.GetValue(obj));
    }
}
```

Helper `Compile(string source)` runs the full pipeline and loads the resulting assembly.

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement Emitter orchestration**

Create `Emitter.cs`:
- Takes bound AST and outputs a Mono.Cecil `AssemblyDefinition`
- Coordinates `TypeEmitter` and `MethodEmitter`
- Adds assembly-level metadata
- Writes PDB alongside the assembly

- [ ] **Step 4: Implement TypeEmitter**

Create `TypeEmitter.cs`:
- `class` → Cecil `TypeDefinition` with class semantics
- `trait` → Cecil `TypeDefinition` with interface semantics
- `union` → abstract base `TypeDefinition` + nested sealed `TypeDefinition` per variant, each with a constructor taking variant fields
- `impl` block → adds interface implementation to the target type
- Ownership attributes encoded as custom attributes from `Cobalt.Annotations`

- [ ] **Step 5: Implement BuiltinTypeEmitter**

Create `BuiltinTypeEmitter.cs`:
- Emit `Option<T>` as a sealed hierarchy: abstract `Option<T>`, sealed `Some<T>(T Value)`, sealed `None<T>`
- Emit `Result<T, E>` similarly

- [ ] **Step 6: Run tests to verify they pass**
- [ ] **Step 7: Commit**

```bash
git commit -m "Add IL emitter for type definitions: class, trait, union (B.4)"
```

---

## Task 11: IL Emitter — Method Bodies

**Files:**
- Create: `src/Cobalt.Compiler/Emit/MethodEmitter.cs`
- Modify: `src/Cobalt.Compiler.Tests/Emit/EmitterTests.cs`

- [ ] **Step 1: Write failing method body tests**

```csharp
[Fact]
public void Emits_method_that_returns_constant()
{
    var asm = Compile("class Foo {\n  public int Bar() { return 42; }\n}");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    var result = asm.GetType("Foo")!.GetMethod("Bar")!.Invoke(obj, []);
    Assert.Equal(42, result);
}

[Fact]
public void Emits_method_with_parameters()
{
    var asm = Compile("class Foo {\n  public int Add(int a, int b) { return a + b; }\n}");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    var result = asm.GetType("Foo")!.GetMethod("Add")!.Invoke(obj, [3, 4]);
    Assert.Equal(7, result);
}

[Fact]
public void Emits_if_else()
{
    var asm = Compile(@"
        class Foo {
            public int Max(int a, int b) {
                if (a > b) { return a; } else { return b; }
            }
        }");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    Assert.Equal(5, asm.GetType("Foo")!.GetMethod("Max")!.Invoke(obj, [3, 5]));
}

[Fact]
public void Emits_while_loop()
{
    var asm = Compile(@"
        class Foo {
            public int Sum(int n) {
                var total = 0;
                var i = 1;
                while (i <= n) { total = total + i; i = i + 1; }
                return total;
            }
        }");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    Assert.Equal(15, asm.GetType("Foo")!.GetMethod("Sum")!.Invoke(obj, [5]));
}

[Fact]
public void Emits_for_loop()
{
    var asm = Compile(@"
        class Foo {
            public int Sum(int n) {
                var total = 0;
                for (var i = 1; i <= n; i = i + 1) { total = total + i; }
                return total;
            }
        }");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    Assert.Equal(10, asm.GetType("Foo")!.GetMethod("Sum")!.Invoke(obj, [4]));
}

[Fact]
public void Emits_new_and_method_call()
{
    var asm = Compile(@"
        use System.Collections.Generic;
        class Foo {
            public int Bar() {
                var list = new List<int>();
                list.Add(42);
                return list.Count;
            }
        }");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    Assert.Equal(1, asm.GetType("Foo")!.GetMethod("Bar")!.Invoke(obj, []));
}

[Fact]
public void Emits_string_interpolation()
{
    var asm = Compile(@"
        class Foo {
            public string Greet(string name) {
                return $""Hello, {name}!"";
            }
        }");
    var obj = Activator.CreateInstance(asm.GetType("Foo")!);
    Assert.Equal("Hello, World!", asm.GetType("Foo")!.GetMethod("Greet")!.Invoke(obj, ["World"]));
}

[Fact]
public void Emits_top_level_statements()
{
    var asm = Compile("use System;\nConsole.WriteLine(42);");
    var main = asm.GetType("Program")?.GetMethod("Main");
    Assert.NotNull(main);
}

[Fact]
public void Emits_free_standing_function()
{
    var asm = Compile("int Double(int x) { return x * 2; }");
    // compiled to static method on synthetic class
    Assert.NotNull(asm.GetTypes().SelectMany(t => t.GetMethods()).FirstOrDefault(m => m.Name == "Double"));
}
```

- [ ] **Step 2: Run tests to verify they fail**
- [ ] **Step 3: Implement MethodEmitter**

Create `MethodEmitter.cs` — translates bound statements and expressions to CIL:
- Local variable declarations → `ldloc`/`stloc`
- Arithmetic → `add`, `sub`, `mul`, `div`
- Comparisons → `ceq`, `clt`, `cgt`
- Method calls → `call`/`callvirt` (use `callvirt` for instance methods)
- `new` → `newobj`
- Field access → `ldfld`/`stfld`
- String interpolation → `string.Format` or `DefaultInterpolatedStringHandler`
- `if`/`else` → branch instructions (`br`, `brfalse`)
- `while`/`for` → backward branch loops
- `foreach` → `GetEnumerator()`/`MoveNext()`/`Current` pattern
- `return` → `ret`
- `using var` → `try`/`finally` with `Dispose()` call
- Top-level statements → wrap in `Program.Main(string[] args)`
- Free-standing functions → static methods on `$ModuleFunctions` class
- `match`/`switch` on unions → type-test branches (`isinst`)

- [ ] **Step 4: Run tests to verify they pass**
- [ ] **Step 5: Commit**

```bash
git commit -m "Add IL emitter for method bodies: expressions, statements, control flow (B.4)"
```

---

## Task 12: Compilation Pipeline

**Files:**
- Create: `src/Cobalt.Compiler/Compilation.cs`
- Modify: `src/Cobalt.Compiler.Tests/Emit/EmitterTests.cs`

- [ ] **Step 1: Write failing pipeline test**

```csharp
[Fact]
public void Compiles_multi_file_program()
{
    var files = new Dictionary<string, string>
    {
        ["main.co"] = "namespace Test;\nuse System;\nvar x = Helper.Double(21);\nConsole.WriteLine(x);",
        ["helper.co"] = "namespace Test;\nclass Helper {\n  public static int Double(int x) { return x * 2; }\n}",
    };
    var result = Compilation.Compile(files, "TestApp");
    Assert.False(result.Diagnostics.HasErrors);
    Assert.NotNull(result.Assembly);
}
```

- [ ] **Step 2: Run test to verify it fails**
- [ ] **Step 3: Implement Compilation orchestrator**

Create `Compilation.cs`:
```csharp
public class Compilation
{
    public static CompilationResult Compile(
        Dictionary<string, string> sourceFiles,
        string assemblyName,
        IEnumerable<string>? references = null)
    {
        // 1. Lex all files
        // 2. Parse all files → CST
        // 3. Collect all type declarations (first pass)
        // 4. Bind all files → typed AST
        // 5. Run ownership analyzer
        // 6. Run borrow checker
        // 7. Emit IL assembly
        // Return diagnostics + assembly bytes
    }
}
```

- [ ] **Step 4: Run test to verify it passes**
- [ ] **Step 5: Commit**

```bash
git commit -m "Add Compilation orchestrator for multi-file programs (B.4)"
```

---

## Task 13: CLI Tool (`cobaltc`)

**Files:**
- Create: `src/Cobalt.Cli/Program.cs`

- [ ] **Step 1: Write a manual integration test script**

Create a test `.co` file and verify the CLI compiles it:

```bash
echo 'use System;
Console.WriteLine("Hello from Cobalt!");' > /tmp/hello.co

dotnet run --project src/Cobalt.Cli -- /tmp/hello.co -o /tmp/hello.dll
dotnet /tmp/hello.dll
```

Expected output: `Hello from Cobalt!`

- [ ] **Step 2: Implement CLI entry point**

Create `Program.cs`:
```csharp
// cobaltc CLI
// Usage: cobaltc <source-files...> [-o output.dll] [--ref assembly.dll ...]

var sourceFiles = new Dictionary<string, string>();
string? outputPath = null;
var references = new List<string>();

// Parse CLI args
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length)
        outputPath = args[++i];
    else if (args[i] == "--ref" && i + 1 < args.Length)
        references.Add(args[++i]);
    else if (args[i].EndsWith(".co"))
        sourceFiles[args[i]] = File.ReadAllText(args[i]);
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 1;
    }
}

if (sourceFiles.Count == 0)
{
    Console.Error.WriteLine("Usage: cobaltc <source.co> [-o output.dll]");
    return 1;
}

outputPath ??= Path.ChangeExtension(sourceFiles.Keys.First(), ".dll");
var assemblyName = Path.GetFileNameWithoutExtension(outputPath);

var result = Compilation.Compile(sourceFiles, assemblyName, references);

foreach (var diag in result.Diagnostics.Diagnostics)
{
    var prefix = diag.Severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };
    Console.Error.WriteLine($"{diag.Location.FileName}({diag.Location.Line},{diag.Location.Column}): {prefix} {diag.Id}: {diag.Message}");
}

if (result.Diagnostics.HasErrors)
{
    Console.Error.WriteLine($"\nBuild failed: {result.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s).");
    return 1;
}

File.WriteAllBytes(outputPath, result.AssemblyBytes!);
Console.Error.WriteLine($"  -> {outputPath}");
return 0;
```

- [ ] **Step 3: Run the manual integration test**

```bash
dotnet run --project src/Cobalt.Cli -- /tmp/hello.co -o /tmp/hello.dll && dotnet /tmp/hello.dll
```

Expected: `Hello from Cobalt!`

- [ ] **Step 4: Commit**

```bash
git commit -m "Add cobaltc CLI tool (B.7)"
```

---

## Task 14: End-to-End — Compile the Sample Program

**Files:**
- Create: `src/Cobalt.Compiler.Tests/Integration/EndToEndTests.cs`

- [ ] **Step 1: Write the end-to-end test**

```csharp
public class EndToEndTests
{
    [Fact]
    public void Compiles_sample_program()
    {
        var files = new Dictionary<string, string>
        {
            ["main.co"] = File.ReadAllText("../../../../../samples/cobalt-syntax/main.co"),
            ["processor.co"] = File.ReadAllText("../../../../../samples/cobalt-syntax/processor.co"),
            ["transforms.co"] = File.ReadAllText("../../../../../samples/cobalt-syntax/transforms.co"),
        };

        var result = Compilation.Compile(files, "CobaltSample");

        var errorMessages = string.Join("\n",
            result.Diagnostics.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Location.FileName}({d.Location.Line}): {d.Id}: {d.Message}"));

        Assert.False(result.Diagnostics.HasErrors, $"Compilation failed:\n{errorMessages}");
        Assert.NotNull(result.AssemblyBytes);
    }

    [Fact]
    public void Sample_program_assembly_has_expected_types()
    {
        var files = new Dictionary<string, string>
        {
            ["main.co"] = File.ReadAllText("../../../../../samples/cobalt-syntax/main.co"),
            ["processor.co"] = File.ReadAllText("../../../../../samples/cobalt-syntax/processor.co"),
            ["transforms.co"] = File.ReadAllText("../../../../../samples/cobalt-syntax/transforms.co"),
        };

        var result = Compilation.Compile(files, "CobaltSample");
        Assert.False(result.Diagnostics.HasErrors);

        // Load and inspect the assembly
        var asm = Assembly.Load(result.AssemblyBytes!);
        Assert.NotNull(asm.GetType("Cobalt.Samples.FileProcessor"));
        Assert.NotNull(asm.GetType("Cobalt.Samples.ITransform"));  // trait → interface
        Assert.NotNull(asm.GetType("Cobalt.Samples.UpperCaseTransform"));
        Assert.NotNull(asm.GetType("Cobalt.Samples.LineNumberTransform"));
        Assert.NotNull(asm.GetType("Cobalt.Samples.ProcessResult")); // union base
        Assert.NotNull(asm.GetType("Cobalt.Samples.Program"));  // top-level → Program
    }
}
```

- [ ] **Step 2: Run the test — this is the final acceptance test**

```bash
dotnet test src/Cobalt.Compiler.Tests --filter "EndToEndTests" -v n
```

This test will likely fail initially. Iterate on earlier tasks until it passes.

- [ ] **Step 3: Fix any issues revealed by the end-to-end test**

Work backward from failures to fix parser, binder, ownership, or emitter issues.

- [ ] **Step 4: Run full test suite**

```bash
dotnet test src/Cobalt.Compiler.Tests -v n
```

Expected: All tests pass.

- [ ] **Step 5: Compile the sample with cobaltc CLI**

```bash
dotnet run --project src/Cobalt.Cli -- samples/cobalt-syntax/main.co samples/cobalt-syntax/processor.co samples/cobalt-syntax/transforms.co -o samples/cobalt-syntax/output/CobaltSample.dll
```

- [ ] **Step 6: Final commit**

```bash
git add -A
git commit -m "End-to-end: sample program compiles to .NET assembly (B.1-B.7 complete)"
```

---

## Deferred / Out of Scope

The following are explicitly NOT in this plan (per roadmap):

- **B.5 Core Standard Library** — `Vec<T>`, `String`, `Box<T>`, `Span<T>` wrappers. Only `Option<T>` and `Result<T,E>` are implemented (as built-in unions). Full stdlib is deferred.
- **MSBuild SDK / .cobaltproj** — Requires packaging work. `cobaltc` CLI is sufficient for Milestone 1.
- **PDB generation** — Listed in the emitter task but can be deferred to a follow-up if time-constrained.
- **Generics with trait bounds** — The type system handles .NET generics for interop, but Cobalt-defined generic types with trait bounds are deferred to a follow-up.
- **Lifetime annotations** — NLL-style liveness is implicit in the borrow checker. Explicit named lifetimes are deferred.
- **Async/await** — Explicitly deferred to Milestone 2.

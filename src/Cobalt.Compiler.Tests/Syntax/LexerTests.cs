using Cobalt.Compiler.Syntax;

namespace Cobalt.Compiler.Tests.Syntax;

public class LexerTests
{
    private static List<Token> Lex(string source)
    {
        var lexer = new Lexer(source);
        return lexer.Lex();
    }

    private static List<Token> LexWithDiagnostics(string source, out Cobalt.Compiler.Diagnostics.DiagnosticBag diagnostics)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Lex();
        diagnostics = lexer.Diagnostics;
        return tokens;
    }

    // ---- Cobalt keywords ----

    [Theory]
    [InlineData("own", TokenKind.Own)]
    [InlineData("ref", TokenKind.Ref)]
    [InlineData("mut", TokenKind.Mut)]
    [InlineData("trait", TokenKind.Trait)]
    [InlineData("impl", TokenKind.Impl)]
    [InlineData("union", TokenKind.Union)]
    [InlineData("match", TokenKind.Match)]
    [InlineData("use", TokenKind.Use)]
    public void CobaltKeywords_ProduceCorrectTokenKind(string text, TokenKind expected)
    {
        var tokens = Lex(text);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
    }

    // ---- C# shared keywords ----

    [Theory]
    [InlineData("namespace", TokenKind.Namespace)]
    [InlineData("class", TokenKind.Class)]
    [InlineData("struct", TokenKind.Struct)]
    [InlineData("interface", TokenKind.Interface)]
    [InlineData("enum", TokenKind.Enum)]
    [InlineData("public", TokenKind.Public)]
    [InlineData("private", TokenKind.Private)]
    [InlineData("protected", TokenKind.Protected)]
    [InlineData("internal", TokenKind.Internal)]
    [InlineData("static", TokenKind.Static)]
    [InlineData("sealed", TokenKind.Sealed)]
    [InlineData("void", TokenKind.Void)]
    [InlineData("return", TokenKind.Return)]
    [InlineData("if", TokenKind.If)]
    [InlineData("else", TokenKind.Else)]
    [InlineData("while", TokenKind.While)]
    [InlineData("for", TokenKind.For)]
    [InlineData("foreach", TokenKind.Foreach)]
    [InlineData("in", TokenKind.In)]
    [InlineData("var", TokenKind.Var)]
    [InlineData("new", TokenKind.New)]
    [InlineData("using", TokenKind.Using)]
    [InlineData("is", TokenKind.Is)]
    [InlineData("this", TokenKind.This)]
    [InlineData("null", TokenKind.Null)]
    [InlineData("true", TokenKind.True)]
    [InlineData("false", TokenKind.False)]
    [InlineData("break", TokenKind.Break)]
    [InlineData("continue", TokenKind.Continue)]
    [InlineData("throw", TokenKind.Throw)]
    [InlineData("try", TokenKind.Try)]
    [InlineData("catch", TokenKind.Catch)]
    [InlineData("finally", TokenKind.Finally)]
    [InlineData("get", TokenKind.Get)]
    [InlineData("set", TokenKind.Set)]
    [InlineData("abstract", TokenKind.Abstract)]
    [InlineData("override", TokenKind.Override)]
    [InlineData("virtual", TokenKind.Virtual)]
    public void CSharpKeywords_ProduceCorrectTokenKind(string text, TokenKind expected)
    {
        var tokens = Lex(text);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
    }

    // ---- Reserved keywords ----

    [Theory]
    [InlineData("fn", TokenKind.Fn)]
    [InlineData("async", TokenKind.Async)]
    [InlineData("await", TokenKind.Await)]
    [InlineData("send", TokenKind.Send)]
    [InlineData("sync", TokenKind.Sync)]
    public void ReservedKeywords_ProduceCorrectTokenKind(string text, TokenKind expected)
    {
        var tokens = Lex(text);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
    }

    // ---- Operators ----

    [Theory]
    [InlineData("+", TokenKind.Plus)]
    [InlineData("-", TokenKind.Minus)]
    [InlineData("*", TokenKind.Star)]
    [InlineData("/", TokenKind.Slash)]
    [InlineData("%", TokenKind.Percent)]
    [InlineData("=", TokenKind.Equals)]
    [InlineData("==", TokenKind.EqualsEquals)]
    [InlineData("!=", TokenKind.BangEquals)]
    [InlineData("<", TokenKind.Less)]
    [InlineData("<=", TokenKind.LessEquals)]
    [InlineData(">", TokenKind.Greater)]
    [InlineData(">=", TokenKind.GreaterEquals)]
    [InlineData("&", TokenKind.Ampersand)]
    [InlineData("&&", TokenKind.AmpersandAmpersand)]
    [InlineData("|", TokenKind.Pipe)]
    [InlineData("||", TokenKind.PipePipe)]
    [InlineData("!", TokenKind.Bang)]
    [InlineData("~", TokenKind.Tilde)]
    [InlineData("^", TokenKind.Caret)]
    [InlineData("++", TokenKind.PlusPlus)]
    [InlineData("--", TokenKind.MinusMinus)]
    [InlineData("+=", TokenKind.PlusEquals)]
    [InlineData("-=", TokenKind.MinusEquals)]
    [InlineData("*=", TokenKind.StarEquals)]
    [InlineData("/=", TokenKind.SlashEquals)]
    [InlineData("=>", TokenKind.FatArrow)]
    [InlineData("->", TokenKind.Arrow)]
    public void Operators_ProduceCorrectTokenKind(string text, TokenKind expected)
    {
        var tokens = Lex(text);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
    }

    // ---- Punctuation ----

    [Theory]
    [InlineData(".", TokenKind.Dot)]
    [InlineData(",", TokenKind.Comma)]
    [InlineData(";", TokenKind.Semicolon)]
    [InlineData(":", TokenKind.Colon)]
    [InlineData("::", TokenKind.ColonColon)]
    [InlineData("(", TokenKind.OpenParen)]
    [InlineData(")", TokenKind.CloseParen)]
    [InlineData("{", TokenKind.OpenBrace)]
    [InlineData("}", TokenKind.CloseBrace)]
    [InlineData("[", TokenKind.OpenBracket)]
    [InlineData("]", TokenKind.CloseBracket)]
    [InlineData("?", TokenKind.QuestionMark)]
    [InlineData("?.", TokenKind.QuestionDot)]
    public void Punctuation_ProduceCorrectTokenKind(string text, TokenKind expected)
    {
        var tokens = Lex(text);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
    }

    // ---- Int literals ----

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("1_000", 1000L)]
    public void IntLiterals_ParseCorrectly(string text, long expectedValue)
    {
        var tokens = Lex(text);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
        Assert.Equal(expectedValue, tokens[0].Value);
    }

    // ---- Float literals ----

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("1.0f", 1.0)]
    public void FloatLiterals_ParseCorrectly(string text, double expectedValue)
    {
        var tokens = Lex(text);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
        Assert.Equal(expectedValue, (double)tokens[0].Value!);
    }

    // ---- String literals ----

    [Fact]
    public void StringLiteral_Simple()
    {
        var tokens = Lex("\"hello\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("\"hello\"", tokens[0].Text);
        Assert.Equal("hello", tokens[0].Value);
    }

    [Fact]
    public void StringLiteral_WithEscapeSequences()
    {
        var tokens = Lex("\"with \\\"escape\\\"\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("with \"escape\"", tokens[0].Value);
    }

    // ---- Interpolated strings ----

    [Fact]
    public void InterpolatedString_Simple()
    {
        var tokens = Lex("$\"hello {name}\"");
        Assert.Equal(TokenKind.InterpolatedStringStart, tokens[0].Kind);
        Assert.Contains(tokens, t => t.Kind == TokenKind.InterpolatedStringText && t.Text == "hello ");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Identifier && t.Text == "name");
        Assert.Contains(tokens, t => t.Kind == TokenKind.InterpolatedStringEnd);
    }

    // ---- Comments ----

    [Fact]
    public void LineComment_IsSkipped()
    {
        var tokens = Lex("// this is a comment\n42");
        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal("42", tokens[0].Text);
    }

    [Fact]
    public void BlockComment_IsSkipped()
    {
        var tokens = Lex("/* block comment */ 42");
        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal("42", tokens[0].Text);
    }

    // ---- Sample file ----

    [Fact]
    public void TransformsSample_LexesWithoutErrors()
    {
        var samplePath = Path.Combine(FindRepoRoot(), "samples", "cobalt-syntax", "transforms.co");
        var source = File.ReadAllText(samplePath);
        var lexer = new Lexer(source, samplePath);
        var tokens = lexer.Lex();
        Assert.False(lexer.Diagnostics.HasErrors,
            $"Lexer reported errors: {string.Join("; ", lexer.Diagnostics.All)}");
        Assert.NotEmpty(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    // ---- Error diagnostics ----

    [Fact]
    public void UnterminatedString_ReportsDiagnostic()
    {
        var tokens = LexWithDiagnostics("\"unterminated", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == "CB1002");
    }

    [Fact]
    public void UnknownCharacter_ReportsDiagnostic()
    {
        var tokens = LexWithDiagnostics("`", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.All, d => d.Id == "CB1004");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Bad);
    }

    // ---- Identifiers ----

    [Fact]
    public void Identifier_ProducesCorrectToken()
    {
        var tokens = Lex("myVariable");
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("myVariable", tokens[0].Text);
    }

    [Fact]
    public void Identifier_WithUnderscore()
    {
        var tokens = Lex("_private");
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("_private", tokens[0].Text);
    }

    // ---- Source location tracking ----

    [Fact]
    public void SourceLocation_TracksLineAndColumn()
    {
        var tokens = Lex("a\nb");
        Assert.Equal(1, tokens[0].Span.Start.Line);
        Assert.Equal(1, tokens[0].Span.Start.Column);
        Assert.Equal(2, tokens[1].Span.Start.Line);
        Assert.Equal(1, tokens[1].Span.Start.Column);
    }

    // ---- EndOfFile ----

    [Fact]
    public void EmptyInput_ProducesOnlyEndOfFile()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
    }

    // ---- Helper ----

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "samples")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}

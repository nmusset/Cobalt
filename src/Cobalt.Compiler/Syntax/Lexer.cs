using Cobalt.Compiler.Diagnostics;

namespace Cobalt.Compiler.Syntax;

public sealed class Lexer
{
    private readonly string _source;
    private readonly string _filePath;
    private readonly DiagnosticBag _diagnostics = new();

    private int _position;
    private int _line = 1;
    private int _column = 1;

    private static readonly Dictionary<string, TokenKind> s_keywords = new()
    {
        // Cobalt keywords
        ["own"] = TokenKind.Own,
        ["ref"] = TokenKind.Ref,
        ["mut"] = TokenKind.Mut,
        ["trait"] = TokenKind.Trait,
        ["impl"] = TokenKind.Impl,
        ["union"] = TokenKind.Union,
        ["match"] = TokenKind.Match,
        ["use"] = TokenKind.Use,

        // C# shared keywords
        ["namespace"] = TokenKind.Namespace,
        ["class"] = TokenKind.Class,
        ["struct"] = TokenKind.Struct,
        ["interface"] = TokenKind.Interface,
        ["enum"] = TokenKind.Enum,
        ["public"] = TokenKind.Public,
        ["private"] = TokenKind.Private,
        ["protected"] = TokenKind.Protected,
        ["internal"] = TokenKind.Internal,
        ["static"] = TokenKind.Static,
        ["sealed"] = TokenKind.Sealed,
        ["void"] = TokenKind.Void,
        ["return"] = TokenKind.Return,
        ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else,
        ["while"] = TokenKind.While,
        ["for"] = TokenKind.For,
        ["foreach"] = TokenKind.Foreach,
        ["in"] = TokenKind.In,
        ["var"] = TokenKind.Var,
        ["new"] = TokenKind.New,
        ["using"] = TokenKind.Using,
        ["is"] = TokenKind.Is,
        ["this"] = TokenKind.This,
        ["null"] = TokenKind.Null,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["throw"] = TokenKind.Throw,
        ["try"] = TokenKind.Try,
        ["catch"] = TokenKind.Catch,
        ["finally"] = TokenKind.Finally,
        ["get"] = TokenKind.Get,
        ["set"] = TokenKind.Set,
        ["abstract"] = TokenKind.Abstract,
        ["override"] = TokenKind.Override,
        ["virtual"] = TokenKind.Virtual,

        // Reserved
        ["fn"] = TokenKind.Fn,
        ["async"] = TokenKind.Async,
        ["await"] = TokenKind.Await,
        ["send"] = TokenKind.Send,
        ["sync"] = TokenKind.Sync,
    };

    public Lexer(string source, string filePath = "<input>")
    {
        _source = source;
        _filePath = filePath;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    private char Current => _position < _source.Length ? _source[_position] : '\0';
    private char Peek(int offset = 1) =>
        _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private bool IsAtEnd => _position >= _source.Length;

    private SourceLocation CurrentLocation() => new(_filePath, _line, _column);

    private char Advance()
    {
        var ch = Current;
        if (ch == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        _position++;
        return ch;
    }

    public List<Token> Lex()
    {
        var tokens = new List<Token>();
        while (!IsAtEnd || _pendingTokens.Count > 0)
        {
            if (_pendingTokens.Count > 0)
            {
                tokens.Add(_pendingTokens.Dequeue());
                continue;
            }
            var token = NextToken();
            if (token != null)
            {
                tokens.Add(token.Value);
                while (_pendingTokens.Count > 0)
                    tokens.Add(_pendingTokens.Dequeue());
            }
        }
        while (_pendingTokens.Count > 0)
            tokens.Add(_pendingTokens.Dequeue());
        tokens.Add(new Token(TokenKind.EndOfFile, "", new SourceSpan(CurrentLocation(), CurrentLocation())));
        return tokens;
    }

    private Token? NextToken()
    {
        SkipWhitespace();
        if (IsAtEnd) return null;

        // Skip comments
        if (Current == '/' && Peek() == '/')
        {
            SkipLineComment();
            return NextToken();
        }
        if (Current == '/' && Peek() == '*')
        {
            SkipBlockComment();
            return NextToken();
        }

        var start = CurrentLocation();

        // Interpolated string
        if (Current == '$' && Peek() == '"')
        {
            return LexInterpolatedString();
        }

        // Identifiers and keywords
        if (char.IsLetter(Current) || Current == '_')
        {
            return LexIdentifierOrKeyword();
        }

        // Numeric literals
        if (char.IsDigit(Current))
        {
            return LexNumber();
        }

        // String literal
        if (Current == '"')
        {
            return LexString();
        }

        // Char literal
        if (Current == '\'')
        {
            return LexChar();
        }

        // Operators and punctuation
        return LexOperatorOrPunctuation();
    }

    private void SkipWhitespace()
    {
        while (!IsAtEnd && char.IsWhiteSpace(Current))
            Advance();
    }

    private void SkipLineComment()
    {
        while (!IsAtEnd && Current != '\n')
            Advance();
    }

    private void SkipBlockComment()
    {
        var start = CurrentLocation();
        Advance(); // /
        Advance(); // *
        var depth = 1;
        while (!IsAtEnd && depth > 0)
        {
            if (Current == '/' && Peek() == '*')
            {
                Advance();
                Advance();
                depth++;
            }
            else if (Current == '*' && Peek() == '/')
            {
                Advance();
                Advance();
                depth--;
            }
            else
            {
                Advance();
            }
        }
        if (depth > 0)
        {
            _diagnostics.Error("CB1001", "Unterminated block comment",
                new SourceSpan(start, CurrentLocation()));
        }
    }

    private Token LexIdentifierOrKeyword()
    {
        var start = CurrentLocation();
        var startPos = _position;
        while (!IsAtEnd && (char.IsLetterOrDigit(Current) || Current == '_'))
            Advance();
        var text = _source[startPos.._position];
        var span = new SourceSpan(start, CurrentLocation());
        var kind = s_keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
        return new Token(kind, text, span);
    }

    private Token LexNumber()
    {
        var start = CurrentLocation();
        var startPos = _position;
        var isFloat = false;

        while (!IsAtEnd && (char.IsDigit(Current) || Current == '_'))
            Advance();

        // Check for decimal point (but not range operator "..")
        if (Current == '.' && char.IsDigit(Peek()))
        {
            isFloat = true;
            Advance(); // .
            while (!IsAtEnd && (char.IsDigit(Current) || Current == '_'))
                Advance();
        }

        // Check for float suffix
        if (Current == 'f' || Current == 'F')
        {
            isFloat = true;
            Advance();
        }

        var text = _source[startPos.._position];
        var span = new SourceSpan(start, CurrentLocation());

        if (isFloat)
        {
            var cleanText = text.Replace("_", "").TrimEnd('f', 'F');
            double.TryParse(cleanText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var floatValue);
            return new Token(TokenKind.FloatLiteral, text, span, floatValue);
        }
        else
        {
            var cleanText = text.Replace("_", "");
            long.TryParse(cleanText, out var intValue);
            return new Token(TokenKind.IntLiteral, text, span, intValue);
        }
    }

    private Token LexString()
    {
        var start = CurrentLocation();
        var startPos = _position;
        Advance(); // opening "
        var sb = new System.Text.StringBuilder();
        while (!IsAtEnd && Current != '"')
        {
            if (Current == '\\')
            {
                Advance();
                sb.Append(ReadEscapeSequence());
            }
            else if (Current == '\n')
            {
                break; // unterminated
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }
        if (IsAtEnd || Current != '"')
        {
            _diagnostics.Error("CB1002", "Unterminated string literal",
                new SourceSpan(start, CurrentLocation()));
            var badText = _source[startPos.._position];
            return new Token(TokenKind.Bad, badText, new SourceSpan(start, CurrentLocation()));
        }
        Advance(); // closing "
        var text = _source[startPos.._position];
        var span = new SourceSpan(start, CurrentLocation());
        return new Token(TokenKind.StringLiteral, text, span, sb.ToString());
    }

    private Token LexChar()
    {
        var start = CurrentLocation();
        var startPos = _position;
        Advance(); // opening '
        char value;
        if (Current == '\\')
        {
            Advance();
            value = ReadEscapeSequence();
        }
        else
        {
            value = Current;
            Advance();
        }
        if (IsAtEnd || Current != '\'')
        {
            _diagnostics.Error("CB1003", "Unterminated character literal",
                new SourceSpan(start, CurrentLocation()));
            var badText = _source[startPos.._position];
            return new Token(TokenKind.Bad, badText, new SourceSpan(start, CurrentLocation()));
        }
        Advance(); // closing '
        var text = _source[startPos.._position];
        var span = new SourceSpan(start, CurrentLocation());
        return new Token(TokenKind.CharLiteral, text, span, value);
    }

    private char ReadEscapeSequence()
    {
        if (IsAtEnd) return '\0';
        var ch = Current;
        Advance();
        return ch switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            '0' => '\0',
            _ => ch,
        };
    }

    private Token LexInterpolatedString()
    {
        // Returns a sequence of tokens for interpolated strings.
        // For MVP, we produce InterpolatedStringStart for $", then
        // we recursively lex until we hit the closing ".
        // This method returns the InterpolatedStringStart token.
        // The caller (Lex) will then pick up subsequent tokens normally,
        // but we need to handle the content specially.
        //
        // Strategy: We produce InterpolatedStringStart, then for each
        // segment we produce InterpolatedStringText for literal parts
        // and regular tokens for expression parts inside {}.
        // Finally InterpolatedStringEnd for the closing ".

        var tokens = new List<Token>();
        var start = CurrentLocation();
        var startPos = _position;
        Advance(); // $
        Advance(); // "
        var text = _source[startPos..(_position)];
        var startToken = new Token(TokenKind.InterpolatedStringStart, text,
            new SourceSpan(start, CurrentLocation()));

        // Now we need to store the remaining tokens that are part of this
        // interpolated string. We'll add them to our pending queue.
        _pendingTokens.Enqueue(startToken);

        LexInterpolatedStringContent();

        // Return the first pending token
        return _pendingTokens.Dequeue();
    }

    private readonly Queue<Token> _pendingTokens = new();

    private void LexInterpolatedStringContent()
    {
        while (!IsAtEnd && Current != '"')
        {
            if (Current == '{')
            {
                // Expression hole
                var braceStart = CurrentLocation();
                var braceStartPos = _position;
                Advance(); // {

                // Lex tokens inside the expression until matching }
                var depth = 1;
                while (!IsAtEnd && depth > 0)
                {
                    SkipWhitespace();
                    if (IsAtEnd) break;
                    if (Current == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            Advance(); // closing }
                            break;
                        }
                    }
                    if (Current == '{')
                    {
                        depth++;
                    }
                    var innerToken = NextToken();
                    if (innerToken != null)
                        _pendingTokens.Enqueue(innerToken.Value);
                }
            }
            else if (Current == '\\')
            {
                // Escape in interpolated string
                var escStart = CurrentLocation();
                var escStartPos = _position;
                Advance();
                ReadEscapeSequence();
                var escText = _source[escStartPos.._position];
                _pendingTokens.Enqueue(new Token(TokenKind.InterpolatedStringText, escText,
                    new SourceSpan(escStart, CurrentLocation())));
            }
            else
            {
                // Regular text
                var textStart = CurrentLocation();
                var textStartPos = _position;
                while (!IsAtEnd && Current != '"' && Current != '{' && Current != '\\' && Current != '\n')
                    Advance();
                var segText = _source[textStartPos.._position];
                if (segText.Length > 0)
                {
                    _pendingTokens.Enqueue(new Token(TokenKind.InterpolatedStringText, segText,
                        new SourceSpan(textStart, CurrentLocation())));
                }
            }
        }

        // Closing "
        var endStart = CurrentLocation();
        if (!IsAtEnd && Current == '"')
        {
            Advance();
            _pendingTokens.Enqueue(new Token(TokenKind.InterpolatedStringEnd, "\"",
                new SourceSpan(endStart, CurrentLocation())));
        }
        else
        {
            _diagnostics.Error("CB1002", "Unterminated string literal",
                new SourceSpan(endStart, CurrentLocation()));
        }
    }

    private Token LexOperatorOrPunctuation()
    {
        var start = CurrentLocation();
        var startPos = _position;
        var ch = Current;

        switch (ch)
        {
            case '+':
                Advance();
                if (Current == '+') { Advance(); return MakeToken(TokenKind.PlusPlus, startPos, start); }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.PlusEquals, startPos, start); }
                return MakeToken(TokenKind.Plus, startPos, start);

            case '-':
                Advance();
                if (Current == '-') { Advance(); return MakeToken(TokenKind.MinusMinus, startPos, start); }
                if (Current == '=') { Advance(); return MakeToken(TokenKind.MinusEquals, startPos, start); }
                if (Current == '>') { Advance(); return MakeToken(TokenKind.Arrow, startPos, start); }
                return MakeToken(TokenKind.Minus, startPos, start);

            case '*':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.StarEquals, startPos, start); }
                return MakeToken(TokenKind.Star, startPos, start);

            case '/':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.SlashEquals, startPos, start); }
                return MakeToken(TokenKind.Slash, startPos, start);

            case '%':
                Advance();
                return MakeToken(TokenKind.Percent, startPos, start);

            case '=':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.EqualsEquals, startPos, start); }
                if (Current == '>') { Advance(); return MakeToken(TokenKind.FatArrow, startPos, start); }
                return MakeToken(TokenKind.Equals, startPos, start);

            case '!':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.BangEquals, startPos, start); }
                return MakeToken(TokenKind.Bang, startPos, start);

            case '<':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.LessEquals, startPos, start); }
                return MakeToken(TokenKind.Less, startPos, start);

            case '>':
                Advance();
                if (Current == '=') { Advance(); return MakeToken(TokenKind.GreaterEquals, startPos, start); }
                return MakeToken(TokenKind.Greater, startPos, start);

            case '&':
                Advance();
                if (Current == '&') { Advance(); return MakeToken(TokenKind.AmpersandAmpersand, startPos, start); }
                return MakeToken(TokenKind.Ampersand, startPos, start);

            case '|':
                Advance();
                if (Current == '|') { Advance(); return MakeToken(TokenKind.PipePipe, startPos, start); }
                return MakeToken(TokenKind.Pipe, startPos, start);

            case '~':
                Advance();
                return MakeToken(TokenKind.Tilde, startPos, start);

            case '^':
                Advance();
                return MakeToken(TokenKind.Caret, startPos, start);

            case '.':
                Advance();
                return MakeToken(TokenKind.Dot, startPos, start);

            case ',':
                Advance();
                return MakeToken(TokenKind.Comma, startPos, start);

            case ';':
                Advance();
                return MakeToken(TokenKind.Semicolon, startPos, start);

            case ':':
                Advance();
                if (Current == ':') { Advance(); return MakeToken(TokenKind.ColonColon, startPos, start); }
                return MakeToken(TokenKind.Colon, startPos, start);

            case '(':
                Advance();
                return MakeToken(TokenKind.OpenParen, startPos, start);

            case ')':
                Advance();
                return MakeToken(TokenKind.CloseParen, startPos, start);

            case '{':
                Advance();
                return MakeToken(TokenKind.OpenBrace, startPos, start);

            case '}':
                Advance();
                return MakeToken(TokenKind.CloseBrace, startPos, start);

            case '[':
                Advance();
                return MakeToken(TokenKind.OpenBracket, startPos, start);

            case ']':
                Advance();
                return MakeToken(TokenKind.CloseBracket, startPos, start);

            case '?':
                Advance();
                if (Current == '.') { Advance(); return MakeToken(TokenKind.QuestionDot, startPos, start); }
                return MakeToken(TokenKind.QuestionMark, startPos, start);

            default:
                Advance();
                _diagnostics.Error("CB1004", $"Unexpected character '{ch}'",
                    new SourceSpan(start, CurrentLocation()));
                return new Token(TokenKind.Bad, ch.ToString(),
                    new SourceSpan(start, CurrentLocation()));
        }
    }

    private Token MakeToken(TokenKind kind, int startPos, SourceLocation start)
    {
        var text = _source[startPos.._position];
        return new Token(kind, text, new SourceSpan(start, CurrentLocation()));
    }

}

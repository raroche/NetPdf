// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Minimal position-tracking CSS tokenizer designed for the Phase 2 Task 3 pre-pass. It does
/// not produce a full token stream per CSS Syntax L3 §4 — it only exposes the operations the
/// preprocessor actually needs: peek/consume single characters, skip whitespace and comments,
/// read at-keywords and identifiers, read balanced (...) and { ... } blocks, and read raw text
/// up to a delimiter. Strings (<c>"..."</c>, <c>'...'</c>) and comments
/// (<c>/* ... */</c>) are skipped wherever balanced-block reading would otherwise be confused.
/// </summary>
/// <remarks>
/// <para>
/// The type is a <c>ref struct</c> so it stays on the stack and operates over a
/// <see cref="ReadOnlySpan{T}"/> with no allocation. Position tracking is done by
/// counting newlines as characters are consumed — line and column are 1-indexed.
/// </para>
/// <para>
/// <b>What this tokenizer does NOT handle</b> (acceptable for the current pre-pass scope —
/// expand only when a real input demands it):
/// </para>
/// <list type="bullet">
///   <item><description>CSS escape sequences (<c>\41</c> for "A"). Identifiers with
///   escapes are treated as ending at the backslash for now.</description></item>
///   <item><description>CDO / CDC tokens (<c>&lt;!--</c> / <c>--&gt;</c>) — these were
///   for HTML &lt;style&gt; comment-hiding from XML-style processors. Unused in modern HTML.</description></item>
///   <item><description>Bad-string / bad-url recovery per CSS Syntax L3. We bail with a
///   clean "ran past end" rather than the spec's reset-and-resume.</description></item>
/// </list>
/// </remarks>
internal ref struct CssTokenizer
{
    private readonly ReadOnlySpan<char> _input;
    private readonly string? _source;
    private int _position;
    private int _line;
    private int _column;

    public CssTokenizer(ReadOnlySpan<char> input, string? source)
    {
        _input = input;
        _source = source;
        _position = 0;
        _line = 1;
        _column = 1;
    }

    public readonly bool IsEnd => _position >= _input.Length;

    public readonly int Position => _position;

    public readonly CssSourceLocation CurrentLocation => new(_source, _line, _column);

    public readonly char PeekChar() => IsEnd ? '\0' : _input[_position];

    public readonly char PeekCharAt(int offset)
    {
        var i = _position + offset;
        return i < 0 || i >= _input.Length ? '\0' : _input[i];
    }

    public char ReadChar()
    {
        if (IsEnd) return '\0';
        var c = _input[_position++];
        if (c == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        return c;
    }

    /// <summary>
    /// Skips ASCII whitespace and CSS block comments (<c>/* ... */</c>) repeatedly until
    /// a non-whitespace, non-comment character is reached.
    /// </summary>
    public void SkipWhitespaceAndComments()
    {
        while (!IsEnd)
        {
            var c = _input[_position];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f')
            {
                ReadChar();
            }
            else if (c == '/' && _position + 1 < _input.Length && _input[_position + 1] == '*')
            {
                // Consume "/*"
                ReadChar();
                ReadChar();
                while (!IsEnd)
                {
                    if (_input[_position] == '*' && _position + 1 < _input.Length && _input[_position + 1] == '/')
                    {
                        ReadChar();
                        ReadChar();
                        break;
                    }
                    ReadChar();
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Reads an at-keyword starting at the current <c>@</c>. Returns the identifier portion
    /// without the leading <c>@</c>. Returns an empty span if not positioned on <c>@</c>.
    /// </summary>
    public ReadOnlySpan<char> ReadAtKeyword()
    {
        if (PeekChar() != '@') return ReadOnlySpan<char>.Empty;
        ReadChar(); // consume '@'
        return ReadIdentifier();
    }

    /// <summary>
    /// Reads a CSS identifier per a relaxed CSS Syntax L3 §4.3.11: <c>[a-zA-Z_-][a-zA-Z0-9_-]*</c>
    /// with no escape handling. Returns an empty span if the current position is not on
    /// an identifier-start character.
    /// </summary>
    public ReadOnlySpan<char> ReadIdentifier()
    {
        var start = _position;
        if (IsEnd) return ReadOnlySpan<char>.Empty;
        var c = _input[_position];
        if (!IsIdentifierStart(c)) return ReadOnlySpan<char>.Empty;
        ReadChar();
        while (!IsEnd && IsIdentifierContinue(_input[_position]))
        {
            ReadChar();
        }
        return _input[start.._position];
    }

    /// <summary>
    /// Reads a balanced parenthesized block starting at the current <c>(</c>. Returns the
    /// content INCLUDING the surrounding parens. Skips strings and nested balanced
    /// parentheses correctly. Returns an empty span if not positioned on <c>(</c>.
    /// </summary>
    public ReadOnlySpan<char> ReadParenthesizedBlock()
    {
        if (PeekChar() != '(') return ReadOnlySpan<char>.Empty;
        var start = _position;
        ReadChar(); // consume '('
        var depth = 1;
        while (!IsEnd && depth > 0)
        {
            var c = _input[_position];
            if (c == '\'' || c == '"')
            {
                SkipString();
                continue;
            }
            if (c == '/' && _position + 1 < _input.Length && _input[_position + 1] == '*')
            {
                SkipWhitespaceAndComments();
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            ReadChar();
        }
        return _input[start.._position];
    }

    /// <summary>
    /// Reads a balanced curly block starting at the current <c>{</c>. Returns the content
    /// INCLUDING the surrounding braces. Skips strings, comments, and nested balanced curly
    /// blocks. Returns an empty span if not positioned on <c>{</c>.
    /// </summary>
    public ReadOnlySpan<char> ReadCurlyBlock()
    {
        if (PeekChar() != '{') return ReadOnlySpan<char>.Empty;
        var start = _position;
        ReadChar(); // consume '{'
        var depth = 1;
        while (!IsEnd && depth > 0)
        {
            var c = _input[_position];
            if (c == '\'' || c == '"')
            {
                SkipString();
                continue;
            }
            if (c == '/' && _position + 1 < _input.Length && _input[_position + 1] == '*')
            {
                SkipWhitespaceAndComments();
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            ReadChar();
        }
        return _input[start.._position];
    }

    /// <summary>
    /// Reads raw text up to (but not including) any of the given delimiter characters,
    /// respecting strings, comments, and parens. <c>{</c> / <c>}</c> are NOT skipped — every
    /// caller in the current preprocessor uses curly braces as the terminating delimiter
    /// itself (e.g., <c>";{"</c> when reading a prelude that ends at either the body's
    /// opening brace or a statement-form terminator). The delimiter character is not
    /// consumed. Useful for reading at-rule preludes that end at <c>;</c> or <c>{</c>.
    /// </summary>
    public ReadOnlySpan<char> ReadUntilAnyTopLevel(ReadOnlySpan<char> delimiters)
    {
        var start = _position;
        while (!IsEnd)
        {
            var c = _input[_position];
            if (c == '\'' || c == '"')
            {
                SkipString();
                continue;
            }
            if (c == '/' && _position + 1 < _input.Length && _input[_position + 1] == '*')
            {
                SkipWhitespaceAndComments();
                continue;
            }
            if (c == '(')
            {
                ReadParenthesizedBlock();
                continue;
            }
            if (delimiters.IndexOf(c) >= 0) break;
            ReadChar();
        }
        return _input[start.._position];
    }

    /// <summary>
    /// Skips a CSS string starting at the current <c>'</c> or <c>"</c>. Handles backslash
    /// escapes by consuming the next character verbatim. Stops at the matching quote or end
    /// of input.
    /// </summary>
    public void SkipString()
    {
        if (IsEnd) return;
        var quote = _input[_position];
        if (quote != '\'' && quote != '"') return;
        ReadChar(); // consume opening quote
        while (!IsEnd)
        {
            var c = _input[_position];
            if (c == '\\')
            {
                ReadChar(); // backslash
                if (!IsEnd) ReadChar(); // escaped character
                continue;
            }
            if (c == quote)
            {
                ReadChar();
                return;
            }
            // CSS Syntax L3 §4.3.5: a newline inside a string aborts the string with a bad-string
            // token. We stop here and let the caller advance — same effect for our skip purpose.
            if (c == '\n') return;
            ReadChar();
        }
    }

    /// <summary>
    /// Skips the rest of the current rule by consuming a single statement-form rule
    /// (everything up to and including the first top-level <c>;</c>) or a block-form rule
    /// (the first balanced curly block at the current depth). The tokenizer is left
    /// positioned just after the terminator.
    /// </summary>
    public void SkipRule()
    {
        // Find either ';' or '{' at the current top level.
        ReadUntilAnyTopLevel(";{");
        var c = PeekChar();
        if (c == ';')
        {
            ReadChar();
            return;
        }
        if (c == '{')
        {
            ReadCurlyBlock();
            return;
        }
    }

    /// <summary>
    /// Returns a string copy of the input slice <c>[start, start+length)</c>. Materializes
    /// the span — call sparingly (only when the result must outlive the tokenizer's stack
    /// frame, which is the case for recovery records).
    /// </summary>
    public readonly string GetSubstring(int start, int length)
    {
        if (length <= 0) return string.Empty;
        if (start < 0 || start + length > _input.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        return _input.Slice(start, length).ToString();
    }

    private static bool IsIdentifierStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-';

    private static bool IsIdentifierContinue(char c) =>
        IsIdentifierStart(c) || (c >= '0' && c <= '9');
}

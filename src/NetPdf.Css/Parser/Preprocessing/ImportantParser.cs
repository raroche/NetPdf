// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Recognizes a trailing <c>!important</c> annotation in a declaration value,
/// skipping strings, parens, and CSS block comments wherever they appear. Per CSS
/// Cascade Level 4 §3, <c>!important</c> is a delimiter token followed by an identifier
/// matching <c>important</c> (case-insensitive). Whitespace and comments are allowed
/// between <c>!</c> and <c>important</c>, and after <c>important</c> before the
/// terminator. The parser must not strip false matches like <c>content: "!important"</c>.
/// </summary>
internal static class ImportantParser
{
    /// <summary>
    /// Returns <c>(value, isImportant)</c>: the value with any trailing <c>!important</c>
    /// stripped (and surrounding whitespace trimmed) plus a flag indicating whether one
    /// was found. Tokens inside strings, parens, or comments are NEVER recognized as the
    /// marker — only top-level <c>! [comment-or-ws]* important [comment-or-ws]*</c> at the
    /// end of input qualifies.
    /// </summary>
    public static (string value, bool isImportant) Strip(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (raw, false);

        var span = raw.AsSpan();
        var tok = new CssTokenizer(span, null);
        var lastBangAt = -1;

        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == '\'' || c == '"')
            {
                tok.SkipString();
                continue;
            }
            if (c == '/' && tok.PeekCharAt(1) == '*')
            {
                tok.SkipWhitespaceAndComments();
                continue;
            }
            if (c == '(')
            {
                tok.ReadParenthesizedBlock();
                continue;
            }
            if (c == '!')
            {
                var bangPos = tok.Position;
                tok.ReadChar(); // consume '!'
                tok.SkipWhitespaceAndComments();
                var ident = tok.ReadIdentifier();
                if (ident.Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    // Consume any trailing whitespace + comments and check for end-of-input.
                    tok.SkipWhitespaceAndComments();
                    if (tok.IsEnd)
                    {
                        return (raw[..bangPos].TrimEnd(), true);
                    }
                    // If something else follows, this `!important` wasn't really the
                    // terminator — record it as a candidate but keep scanning for a later one.
                    lastBangAt = bangPos;
                }
                continue;
            }
            tok.ReadChar();
        }

        // No trailing !important survived the scan — return the trimmed original.
        // (lastBangAt is intentionally unused: a non-trailing `!important` followed by
        // more content is invalid CSS, but we leave the value untouched.)
        _ = lastBangAt;
        return (raw.Trim(), false);
    }
}

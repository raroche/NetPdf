// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.Parser.Preprocessing;

/// <summary>
/// Phase 2 Task 3 pre-pass: tokenizes raw CSS text and recovers information that
/// AngleSharp.Css 1.0.0-beta.144 will discard or mangle. Output is a side-channel
/// (<see cref="CssPreprocessResult"/>) that the <see cref="CssParserAdapter"/> merges
/// with the AngleSharp-derived CSSOM to produce a complete AST.
/// </summary>
/// <remarks>
/// <para>
/// <b>What's recovered:</b>
/// </para>
/// <list type="bullet">
///   <item><description><c>@page</c> selector text (<c>:first</c>, <c>:left</c>,
///   <c>:right</c>, named pages — anything AngleSharp drops to empty <c>SelectorText</c>).</description></item>
///   <item><description><c>@page</c> margin-box at-rules (<c>@top-center</c>, etc. — silently
///   dropped by AngleSharp).</description></item>
///   <item><description><c>@import</c> <c>layer(...)</c> and <c>supports(...)</c> clauses
///   — folded by AngleSharp into a malformed <c>"not all"</c> media query.</description></item>
///   <item><description>Source positions for every top-level rule.</description></item>
/// </list>
/// <para>
/// <b>Out of scope for this v1:</b> modern value functions (<c>oklch()</c>, <c>oklab()</c>,
/// <c>color-mix()</c>, <c>light-dark()</c>) and modern at-rules <c>@container</c> /
/// <c>@layer</c> block-form. The Phase 2 doc lists these for Task 3, but their consumers
/// (typed values, layer cascade) live in Tasks 9–10 and Task 7 — adding token capture
/// without a downstream consumer would be premature. Tracked as a Task 3 follow-up cycle.
/// </para>
/// <para>
/// <b>Robustness:</b> the preprocessor never throws on malformed CSS. Whatever it can't
/// parse it skips, advancing past the next <c>;</c> or balanced <c>{...}</c>. AngleSharp
/// is the canonical parser; the pre-pass only fills gaps.
/// </para>
/// </remarks>
internal static class CssPreprocessor
{
    /// <summary>
    /// Walks <paramref name="css"/> in source order and produces recovery side-data for the
    /// adapter. <paramref name="source"/> identifies the input (a stylesheet URL or the
    /// literal <c>"&lt;style&gt;"</c>) for source-location reporting.
    /// </summary>
    public static CssPreprocessResult Process(string css, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(css);
        return ProcessSpan(css.AsSpan(), source);
    }

    private static CssPreprocessResult ProcessSpan(ReadOnlySpan<char> css, string? source)
    {
        var pageRecoveries = ImmutableArray.CreateBuilder<CssPageRuleRecovery>();
        var importRecoveries = ImmutableArray.CreateBuilder<CssImportRuleRecovery>();
        var rulePositions = ImmutableArray.CreateBuilder<CssRuleSourcePosition>();
        var pageOrdinal = 0;
        var importOrdinal = 0;
        var ruleOrdinal = 0;

        var tok = new CssTokenizer(css, source);
        tok.SkipWhitespaceAndComments();

        while (!tok.IsEnd)
        {
            var ruleStart = tok.CurrentLocation;

            if (tok.PeekChar() == '@')
            {
                // Snapshot the at-keyword for routing then dispatch.
                var atKeyword = tok.ReadAtKeyword();
                if (atKeyword.Equals("page", StringComparison.OrdinalIgnoreCase))
                {
                    var rec = ParsePageRule(ref tok, pageOrdinal++, ruleStart);
                    pageRecoveries.Add(rec);
                }
                else if (atKeyword.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    var rec = ParseImportRule(ref tok, importOrdinal++, ruleStart);
                    importRecoveries.Add(rec);
                }
                else
                {
                    // Unknown at-rule (or one we don't recover from): skip its body.
                    tok.SkipRule();
                }
            }
            else
            {
                // Style rule: skip its prelude + body.
                tok.SkipRule();
            }

            rulePositions.Add(new CssRuleSourcePosition(ruleOrdinal++, ruleStart));
            tok.SkipWhitespaceAndComments();
        }

        return new CssPreprocessResult(
            pageRecoveries.Count == 0 ? ImmutableArray<CssPageRuleRecovery>.Empty : pageRecoveries.ToImmutable(),
            importRecoveries.Count == 0 ? ImmutableArray<CssImportRuleRecovery>.Empty : importRecoveries.ToImmutable(),
            rulePositions.Count == 0 ? ImmutableArray<CssRuleSourcePosition>.Empty : rulePositions.ToImmutable());
    }

    /// <summary>
    /// Parses an <c>@page</c> rule's prelude (selector) and body (declarations + margin-box
    /// at-rules). Position is on the character right after the <c>page</c> at-keyword.
    /// </summary>
    private static CssPageRuleRecovery ParsePageRule(ref CssTokenizer tok, int ordinal, CssSourceLocation location)
    {
        tok.SkipWhitespaceAndComments();

        // Selector runs from here to the opening '{'.
        var selectorSpan = tok.ReadUntilAnyTopLevel("{;");
        var selectorText = selectorSpan.ToString().Trim();

        var marginBoxes = ImmutableArray.CreateBuilder<CssMarginBoxRecovery>();
        if (tok.PeekChar() == '{')
        {
            // Walk the body looking for nested @<margin-box> rules. Anything else is
            // a declaration, which AngleSharp already captures correctly.
            tok.ReadChar(); // consume '{'
            tok.SkipWhitespaceAndComments();
            while (!tok.IsEnd && tok.PeekChar() != '}')
            {
                if (tok.PeekChar() == '@')
                {
                    var boxStart = tok.CurrentLocation;
                    var boxKeyword = tok.ReadAtKeyword().ToString();
                    tok.SkipWhitespaceAndComments();
                    if (tok.PeekChar() == '{')
                    {
                        var bodySpan = tok.ReadCurlyBlock();
                        // Strip the surrounding '{' and '}' and trim.
                        var body = bodySpan.Length >= 2
                            ? bodySpan[1..^1].ToString().Trim()
                            : string.Empty;
                        marginBoxes.Add(new CssMarginBoxRecovery(boxKeyword, body, boxStart));
                    }
                    else
                    {
                        // Statement-form at-rule inside @page (uncommon but defensive). Skip.
                        tok.ReadUntilAnyTopLevel(";}");
                        if (tok.PeekChar() == ';') tok.ReadChar();
                    }
                }
                else
                {
                    // Declaration line. Skip up to ';' or '}'.
                    tok.ReadUntilAnyTopLevel(";}");
                    if (tok.PeekChar() == ';') tok.ReadChar();
                }
                tok.SkipWhitespaceAndComments();
            }
            if (tok.PeekChar() == '}') tok.ReadChar();
        }
        else if (tok.PeekChar() == ';')
        {
            // Statement-form @page is invalid CSS but tolerate it — just consume the ';'.
            tok.ReadChar();
        }

        return new CssPageRuleRecovery(
            ordinal,
            selectorText,
            marginBoxes.Count == 0 ? ImmutableArray<CssMarginBoxRecovery>.Empty : marginBoxes.ToImmutable(),
            location);
    }

    /// <summary>
    /// Parses an <c>@import</c> rule's prelude. Position is right after the <c>import</c>
    /// at-keyword. Authored shape (per CSS Cascade L4 + Cascade L5):
    /// <c>@import &lt;url&gt; [layer | layer(name)] [supports(condition)] [media-query];</c>
    /// </summary>
    private static CssImportRuleRecovery ParseImportRule(ref CssTokenizer tok, int ordinal, CssSourceLocation location)
    {
        tok.SkipWhitespaceAndComments();

        // Read the URL — either url("..."), url('...'), url(...), "..." or '...'.
        var url = ReadImportUrl(ref tok);
        tok.SkipWhitespaceAndComments();

        string? layerName = null;
        string? supportsCondition = null;
        var media = string.Empty;

        // Optional clauses can appear in any order in CSS Cascade L5 §2.4, but the canonical
        // order is layer → supports → media. We accept any order.
        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == ';' || c == '\0') break;

            // Detect 'layer' keyword vs 'layer(...)'
            if (TryConsumeKeyword(ref tok, "layer"))
            {
                if (tok.PeekChar() == '(')
                {
                    var paren = tok.ReadParenthesizedBlock();
                    layerName = TrimSurroundingParens(paren).Trim().ToString();
                }
                else
                {
                    layerName = string.Empty; // anonymous layer
                }
                tok.SkipWhitespaceAndComments();
                continue;
            }

            if (TryConsumeKeyword(ref tok, "supports"))
            {
                if (tok.PeekChar() == '(')
                {
                    var paren = tok.ReadParenthesizedBlock();
                    supportsCondition = TrimSurroundingParens(paren).Trim().ToString();
                }
                tok.SkipWhitespaceAndComments();
                continue;
            }

            // Anything else is the media query — read to ';' or end.
            var rest = tok.ReadUntilAnyTopLevel(";").ToString().Trim();
            media = rest;
            break;
        }

        // Consume terminating ';' if present.
        if (tok.PeekChar() == ';') tok.ReadChar();

        return new CssImportRuleRecovery(
            ordinal,
            url,
            media,
            layerName,
            supportsCondition,
            location);
    }

    private static string ReadImportUrl(ref CssTokenizer tok)
    {
        var c = tok.PeekChar();
        if (c == '\'' || c == '"')
        {
            return ReadQuotedString(ref tok);
        }

        // Could be url(...) function or, defensively, an identifier-form URL we didn't expect.
        // Try to peek for "url(" case-insensitively.
        if (PeekKeyword(ref tok, "url") && tok.PeekCharAt(3) == '(')
        {
            tok.ReadChar(); tok.ReadChar(); tok.ReadChar(); // consume "url"
            var paren = tok.ReadParenthesizedBlock();
            var inner = TrimSurroundingParens(paren).Trim();
            // Strip optional surrounding quotes.
            if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[^1] == inner[0])
                inner = inner[1..^1];
            return inner.ToString();
        }

        // Fallback: read the next whitespace-delimited token.
        var fallback = tok.ReadUntilAnyTopLevel(" \t\r\n;").ToString().Trim();
        return fallback;
    }

    private static string ReadQuotedString(ref CssTokenizer tok)
    {
        var quote = tok.PeekChar();
        if (quote != '\'' && quote != '"') return string.Empty;
        var startLocation = tok.Position;
        tok.ReadChar(); // opening quote
        var contentStart = tok.Position;
        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == '\\')
            {
                tok.ReadChar();
                if (!tok.IsEnd) tok.ReadChar();
                continue;
            }
            if (c == quote)
            {
                var content = tok.GetSubstring(contentStart, tok.Position - contentStart);
                tok.ReadChar(); // closing quote
                return content;
            }
            if (c == '\n')
            {
                // Bad-string per CSS Syntax L3 — return what we have.
                return tok.GetSubstring(contentStart, tok.Position - contentStart);
            }
            tok.ReadChar();
        }
        return tok.GetSubstring(contentStart, tok.Position - contentStart);
    }

    private static bool TryConsumeKeyword(ref CssTokenizer tok, string keyword)
    {
        if (!PeekKeyword(ref tok, keyword)) return false;
        for (var i = 0; i < keyword.Length; i++) tok.ReadChar();
        return true;
    }

    private static bool PeekKeyword(ref CssTokenizer tok, string keyword)
    {
        for (var i = 0; i < keyword.Length; i++)
        {
            var c = tok.PeekCharAt(i);
            if (char.ToLowerInvariant(c) != char.ToLowerInvariant(keyword[i])) return false;
        }
        // Boundary check: the next character after the keyword must NOT continue the
        // identifier (no letters / digits / _ / -).
        var nextChar = tok.PeekCharAt(keyword.Length);
        return !IsIdentifierContinue(nextChar);
    }

    private static ReadOnlySpan<char> TrimSurroundingParens(ReadOnlySpan<char> input)
    {
        if (input.Length >= 2 && input[0] == '(' && input[^1] == ')')
            return input[1..^1];
        return input;
    }

    private static bool IsIdentifierContinue(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9') || c == '_' || c == '-';
}

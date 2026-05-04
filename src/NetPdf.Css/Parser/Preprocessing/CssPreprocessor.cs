// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;
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
///   dropped by AngleSharp). Validated against the 16-name CSS Paged Media L3 §6.4 list:
///   only spec-recognized names become <see cref="CssMarginBoxRecovery"/> entries.</description></item>
///   <item><description><c>@import</c> <c>layer(...)</c> and <c>supports(...)</c> clauses
///   — folded by AngleSharp into a malformed <c>"not all"</c> media query.</description></item>
///   <item><description>Modern at-rules <c>@container</c> and <c>@layer</c> (block + statement
///   forms) AngleSharp drops entirely. Captured as <see cref="CssOpaqueAtRuleSlot"/>
///   entries in <see cref="CssPreprocessResult.RuleSlots"/> so the adapter splices them into
///   the AST in source order.</description></item>
///   <item><description>Source positions for every top-level rule, recorded in
///   <see cref="CssPreprocessResult.RuleSlots"/>. The slot list is the single source of
///   truth for source order — the adapter pairs <see cref="CssAngleSharpRuleSlot"/> entries
///   with AngleSharp's emitted rules sequentially, fixing the ordinal-drift bug that occurs
///   when AngleSharp drops modern at-rules in the middle of a stylesheet.</description></item>
/// </list>
/// <para>
/// <b>Out of scope for this pass:</b>
/// </para>
/// <list type="bullet">
///   <item><description>Modern value functions (<c>oklch()</c>, <c>oklab()</c>,
///   <c>color-mix()</c>, <c>light-dark()</c>). AngleSharp parses some of them and silently
///   produces wrong colors (<c>oklch</c> → bogus rgba) or empty rule bodies (<c>color-mix</c>,
///   <c>light-dark</c>). Recovering these requires per-declaration value-text re-parsing,
///   which is plumbed into typed values in Tasks 9–10 of the Phase 2 plan. Tracked as a Task
///   3 follow-up cycle in <c>PROGRESS.md</c>; rendering for these is post-v1 anyway.</description></item>
///   <item><description>CSS escape sequences in identifiers (<c>\41 </c> = "A", etc.). The
///   <see cref="CssTokenizer"/> stops identifier reading at a backslash. Generated CSS
///   rarely uses identifier escapes — the limitation is pinned via tests.</description></item>
///   <item><description>Property-level source positions for top-level style rule
///   declarations: AngleSharp does not expose them, so <see cref="CssDeclaration.Location"/>
///   stays <see cref="CssSourceLocation.Unknown"/> for those. Margin-box declarations get
///   the parent margin-box's location.</description></item>
/// </list>
/// <para>
/// <b>Robustness:</b> the preprocessor never throws on malformed CSS. Whatever it can't
/// parse it skips, advancing past the next <c>;</c> or balanced <c>{...}</c>. AngleSharp
/// is the canonical parser; the pre-pass only fills gaps.
/// </para>
/// </remarks>
internal static class CssPreprocessor
{
    /// <summary>
    /// CSS Paged Media L3 §6.4 margin-box names. Anything else inside <c>@page</c> with the
    /// shape <c>@&lt;ident&gt; { ... }</c> is silently skipped (treated as malformed CSS).
    /// </summary>
    private static readonly FrozenSet<string> KnownMarginBoxNames = new[]
    {
        "top-left-corner",
        "top-left",
        "top-center",
        "top-right",
        "top-right-corner",
        "bottom-left-corner",
        "bottom-left",
        "bottom-center",
        "bottom-right",
        "bottom-right-corner",
        "left-top",
        "left-middle",
        "left-bottom",
        "right-top",
        "right-middle",
        "right-bottom",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// At-keywords that AngleSharp.Css 1.0.0-beta.144 silently drops. The preprocessor
    /// captures these as <see cref="CssOpaqueAtRuleSlot"/> entries so the adapter can splice
    /// opaque <see cref="CssAtRule"/> nodes into the AST in source order.
    /// </summary>
    private static readonly FrozenSet<string> AngleSharpDroppedAtRules = new[]
    {
        "container",
        "layer",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
        var slots = ImmutableArray.CreateBuilder<CssPreprocessRuleSlot>();
        var pageOrdinal = 0;
        var importOrdinal = 0;

        var tok = new CssTokenizer(css, source);
        tok.SkipWhitespaceAndComments();

        while (!tok.IsEnd)
        {
            var ruleStart = tok.CurrentLocation;

            if (tok.PeekChar() == '@')
            {
                var atKeyword = tok.ReadAtKeyword().ToString();
                if (atKeyword.Equals("page", StringComparison.OrdinalIgnoreCase))
                {
                    var rec = ParsePageRule(ref tok, pageOrdinal++, ruleStart);
                    pageRecoveries.Add(rec);
                    slots.Add(new CssAngleSharpRuleSlot(ruleStart));
                }
                else if (atKeyword.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    var rec = ParseImportRule(ref tok, importOrdinal++, ruleStart);
                    importRecoveries.Add(rec);
                    slots.Add(new CssAngleSharpRuleSlot(ruleStart));
                }
                else if (AngleSharpDroppedAtRules.Contains(atKeyword))
                {
                    var prelude = ReadAtRulePrelude(ref tok);
                    SkipAtRuleBodyOrTerminator(ref tok);
                    slots.Add(new CssOpaqueAtRuleSlot(atKeyword.ToLowerInvariant(), prelude, ruleStart));
                }
                else
                {
                    // Other at-rules — AngleSharp emits these. Skip the body without recovery.
                    tok.SkipRule();
                    slots.Add(new CssAngleSharpRuleSlot(ruleStart));
                }
            }
            else
            {
                // Style rule — skip its prelude + body. AngleSharp emits these.
                tok.SkipRule();
                slots.Add(new CssAngleSharpRuleSlot(ruleStart));
            }

            tok.SkipWhitespaceAndComments();
        }

        return new CssPreprocessResult(
            pageRecoveries.Count == 0 ? ImmutableArray<CssPageRuleRecovery>.Empty : pageRecoveries.ToImmutable(),
            importRecoveries.Count == 0 ? ImmutableArray<CssImportRuleRecovery>.Empty : importRecoveries.ToImmutable(),
            slots.Count == 0 ? ImmutableArray<CssPreprocessRuleSlot>.Empty : slots.ToImmutable());
    }

    /// <summary>
    /// Reads the prelude text of an at-rule (everything between the at-keyword and the next
    /// top-level <c>{</c> or <c>;</c>). The terminator is not consumed.
    /// </summary>
    private static string ReadAtRulePrelude(ref CssTokenizer tok)
    {
        tok.SkipWhitespaceAndComments();
        var span = tok.ReadUntilAnyTopLevel("{;");
        return span.ToString().Trim();
    }

    /// <summary>
    /// Consumes the body of an at-rule: either a balanced <c>{...}</c> block or, if the
    /// rule terminates with <c>;</c>, the semicolon.
    /// </summary>
    private static void SkipAtRuleBodyOrTerminator(ref CssTokenizer tok)
    {
        var c = tok.PeekChar();
        if (c == '{')
        {
            tok.ReadCurlyBlock();
        }
        else if (c == ';')
        {
            tok.ReadChar();
        }
    }

    /// <summary>
    /// Parses an <c>@page</c> rule's prelude (selector) and body (declarations + margin-box
    /// at-rules). Position is on the character right after the <c>page</c> at-keyword.
    /// </summary>
    private static CssPageRuleRecovery ParsePageRule(ref CssTokenizer tok, int ordinal, CssSourceLocation location)
    {
        tok.SkipWhitespaceAndComments();
        var selectorSpan = tok.ReadUntilAnyTopLevel("{;");
        var selectorText = selectorSpan.ToString().Trim();

        var marginBoxes = ImmutableArray.CreateBuilder<CssMarginBoxRecovery>();
        if (tok.PeekChar() == '{')
        {
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
                        if (KnownMarginBoxNames.Contains(boxKeyword))
                        {
                            var body = bodySpan.Length >= 2
                                ? bodySpan[1..^1].ToString().Trim()
                                : string.Empty;
                            marginBoxes.Add(new CssMarginBoxRecovery(boxKeyword, body, boxStart));
                        }
                        // Unknown nested at-rule names inside @page — skip silently. Per CSS
                        // Paged Media L3 §6.4 only the 16 known margin-boxes are valid here;
                        // anything else is malformed CSS that the cascade ignores.
                    }
                    else
                    {
                        tok.ReadUntilAnyTopLevel(";}");
                        if (tok.PeekChar() == ';') tok.ReadChar();
                    }
                }
                else
                {
                    tok.ReadUntilAnyTopLevel(";}");
                    if (tok.PeekChar() == ';') tok.ReadChar();
                }
                tok.SkipWhitespaceAndComments();
            }
            if (tok.PeekChar() == '}') tok.ReadChar();
        }
        else if (tok.PeekChar() == ';')
        {
            tok.ReadChar();
        }

        return new CssPageRuleRecovery(
            ordinal,
            selectorText,
            marginBoxes.Count == 0 ? ImmutableArray<CssMarginBoxRecovery>.Empty : marginBoxes.ToImmutable(),
            location);
    }

    /// <summary>
    /// Parses an <c>@import</c> rule's prelude. Authored shape (CSS Cascade L4 + L5):
    /// <c>@import &lt;url&gt; [layer | layer(name)] [supports(condition)] [media-query];</c>.
    /// Clauses can appear in any order.
    /// </summary>
    private static CssImportRuleRecovery ParseImportRule(ref CssTokenizer tok, int ordinal, CssSourceLocation location)
    {
        tok.SkipWhitespaceAndComments();

        var url = ReadImportUrl(ref tok);
        tok.SkipWhitespaceAndComments();

        string? layerName = null;
        string? supportsCondition = null;
        var media = string.Empty;

        while (!tok.IsEnd)
        {
            var c = tok.PeekChar();
            if (c == ';' || c == '\0') break;

            if (TryConsumeKeyword(ref tok, "layer"))
            {
                if (tok.PeekChar() == '(')
                {
                    var paren = tok.ReadParenthesizedBlock();
                    layerName = TrimSurroundingParens(paren).Trim().ToString();
                }
                else
                {
                    layerName = string.Empty;
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

            var rest = tok.ReadUntilAnyTopLevel(";").ToString().Trim();
            media = rest;
            break;
        }

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

        if (PeekKeyword(ref tok, "url") && tok.PeekCharAt(3) == '(')
        {
            tok.ReadChar(); tok.ReadChar(); tok.ReadChar();
            var paren = tok.ReadParenthesizedBlock();
            var inner = TrimSurroundingParens(paren).Trim();
            if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[^1] == inner[0])
                inner = inner[1..^1];
            return inner.ToString();
        }

        var fallback = tok.ReadUntilAnyTopLevel(" \t\r\n;").ToString().Trim();
        return fallback;
    }

    private static string ReadQuotedString(ref CssTokenizer tok)
    {
        var quote = tok.PeekChar();
        if (quote != '\'' && quote != '"') return string.Empty;
        tok.ReadChar();
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
                tok.ReadChar();
                return content;
            }
            if (c == '\n')
            {
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

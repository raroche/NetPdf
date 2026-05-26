// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Per Phase 3 Task 17 cycle 0b — resolves CSS values for the four
/// <see cref="PropertyType.GridLine"/> longhands (<c>grid-row-start</c> /
/// <c>grid-row-end</c> / <c>grid-column-start</c> / <c>grid-column-end</c>) per
/// CSS Grid L1 §8.3.
///
/// <para><b>Grammar accepted:</b>
/// <code>
/// &lt;grid-line&gt; = auto
///              | &lt;custom-ident&gt;
///              | [ &lt;integer&gt; &amp;&amp; &lt;custom-ident&gt;? ]
///              | [ span &amp;&amp; [ &lt;integer&gt; || &lt;custom-ident&gt; ] ]
/// </code>
/// (= the <c>&amp;&amp;</c> operator means any order; integer + ident can interleave.)</para>
///
/// <para><b>Default (auto)</b> resolves to <see cref="ComputedSlot.FromKeyword(int)"/>
/// with the <c>auto</c> keyword id (= 0) — NO side-table payload. The reader
/// (<see cref="GridReaders.ReadGridRowStart"/>) sees the non-side-table tag and
/// returns <see cref="GridLineValue.Auto"/>. Any non-default value lands a
/// <see cref="GridLineValue"/> in the side-table per the PR-#89 P1 #3 uniform-storage
/// decision.</para>
///
/// <para><b>Invalid input</b> (zero line number per §8.3, zero/negative span count,
/// "span" with no integer or ident, garbled tokens, <c>calc()</c>) emits
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/> and returns
/// <see cref="ResolverResult.Invalid"/>; the cascade falls back to the property
/// initial value (= <c>auto</c> per §8.3).</para>
/// </summary>
internal static class GridLineResolver
{
    /// <summary>The keyword id used for the <c>auto</c> default. Mirrors the
    /// <c>LengthResolver.KeywordIdAuto = 0</c> convention so any future "any
    /// keyword?" introspection sees a consistent id.</summary>
    public const int KeywordIdAuto = 0;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // Per L19+ deferral — calc() on grid-line values is out of scope.
        if (ContainsCaseInsensitive(value, "calc("))
        {
            EmitInvalid(diagnostics, propertyName, value,
                "calc() is not supported on grid-line values per L19+ deferral", location);
            return ResolverResult.Invalid();
        }

        var tokens = Tokenize(value);
        if (tokens.Count == 0)
        {
            EmitInvalid(diagnostics, propertyName, value,
                "empty grid-line value", location);
            return ResolverResult.Invalid();
        }

        // Fast path: bare "auto" — the default + no side-table entry.
        if (tokens.Count == 1
            && tokens[0].Kind == GridLineTokenKind.Ident
            && string.Equals(tokens[0].Text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(KeywordIdAuto));
        }

        if (!TryParseGridLine(tokens, out var parsed, out var reason))
        {
            EmitInvalid(diagnostics, propertyName, value, reason, location);
            return ResolverResult.Invalid();
        }

        // Box the struct so it can land in the side-table dictionary
        // (Dictionary<PropertyId, object>). The reader unboxes via
        // TryGetSideTablePayloadStruct<GridLineValue>.
        return ResolverResult.ResolvedSideTable((object)parsed);
    }

    /// <summary>Parse a sequence of grid-line tokens into a <see cref="GridLineValue"/>
    /// per §8.3. Returns <see langword="false"/> with a human-readable
    /// <paramref name="reason"/> on grammar / invariant failures.</summary>
    private static bool TryParseGridLine(
        System.Collections.Generic.List<GridLineToken> tokens,
        out GridLineValue value,
        out string reason)
    {
        value = default;
        reason = string.Empty;

        // Identify the "span" keyword's presence — it can only appear once and only
        // as the first token (per §8.3 the production is `span && [int || ident]`,
        // but the `span` keyword conventionally leads; we tolerate either order).
        var hasSpan = false;
        int? integerToken = null;
        string? identToken = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == GridLineTokenKind.Ident
                && string.Equals(t.Text, "span", StringComparison.OrdinalIgnoreCase))
            {
                if (hasSpan)
                {
                    reason = "grid-line cannot contain 'span' more than once";
                    return false;
                }
                hasSpan = true;
                continue;
            }
            if (t.Kind == GridLineTokenKind.Integer)
            {
                if (integerToken.HasValue)
                {
                    reason = "grid-line cannot contain more than one integer";
                    return false;
                }
                integerToken = t.IntegerValue;
                continue;
            }
            if (t.Kind == GridLineTokenKind.Ident)
            {
                if (identToken is not null)
                {
                    reason = "grid-line cannot contain more than one identifier";
                    return false;
                }
                // 'auto' is a CSS-wide reserved word; rejecting it as a custom-ident
                // matches §8.3's grammar (auto is its own production, not a custom-
                // ident). Combined with the bare-auto fast path in Resolve, any 'auto'
                // appearing here is in the wrong slot — reject.
                if (string.Equals(t.Text, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "'auto' cannot combine with span / integer / ident";
                    return false;
                }
                // 'span' is also reserved (handled above), but the loop already
                // intercepts it — by this branch we know t.Text is not 'span'.
                identToken = t.Text;
                continue;
            }
            reason = "unexpected token in grid-line value";
            return false;
        }

        if (hasSpan)
        {
            // span && [<integer> || <custom-ident>] — at least one of integer/ident
            // must be present per §8.3.
            if (integerToken is null && identToken is null)
            {
                reason = "'span' alone is invalid; provide an integer count and/or a named-line identifier";
                return false;
            }
            if (integerToken is null)
            {
                // span <custom-ident>  (e.g., "span foo")
                value = GridLineValue.ForSpanName(identToken!);
                return true;
            }
            if (identToken is null)
            {
                // span <integer>  (e.g., "span 2")
                if (integerToken.Value < 1)
                {
                    reason = "span count must be ≥ 1 per CSS Grid L1 §8.3";
                    return false;
                }
                value = GridLineValue.ForSpan(integerToken.Value);
                return true;
            }
            // span <custom-ident> <integer>  (e.g., "span foo 2")
            if (integerToken.Value < 1)
            {
                reason = "span occurrence count must be ≥ 1 per CSS Grid L1 §8.3";
                return false;
            }
            value = GridLineValue.ForSpanNameOccurrence(identToken!, integerToken.Value);
            return true;
        }

        // No span. The remaining productions are <integer> && <custom-ident>? OR
        // <custom-ident> (bare-named line).
        if (integerToken is null && identToken is null)
        {
            reason = "grid-line value has no integer, ident, or 'auto'";
            return false;
        }
        if (integerToken is null)
        {
            // Bare <custom-ident>.
            value = GridLineValue.ForNamedLine(identToken!);
            return true;
        }
        if (integerToken.Value == 0)
        {
            // §8.3 — line number 0 is invalid.
            reason = "grid line number 0 is invalid per CSS Grid L1 §8.3";
            return false;
        }
        if (identToken is null)
        {
            // Bare <integer>.
            value = GridLineValue.ForLineNumber(integerToken.Value);
            return true;
        }
        // <integer> + <custom-ident>  (e.g., "foo 2" or "2 foo")
        value = GridLineValue.ForNamedLineNumber(identToken!, integerToken.Value);
        return true;
    }

    // =================================================================
    //  Tokenizer
    // =================================================================

    private enum GridLineTokenKind : byte
    {
        Ident = 1,
        Integer = 2,
    }

    private readonly struct GridLineToken
    {
        public GridLineToken(GridLineTokenKind kind, string text, int integerValue)
        {
            Kind = kind;
            Text = text;
            IntegerValue = integerValue;
        }
        public GridLineTokenKind Kind { get; }
        public string Text { get; }
        public int IntegerValue { get; }
    }

    /// <summary>Whitespace-separated CSS tokens. Each token is either an integer
    /// (matching <c>[-+]?[0-9]+</c>) or an identifier (CSS identifiers per Syntax §4.4 —
    /// here approximated as alphanumeric + dash + underscore, sufficient for the
    /// custom-ident grammar at this layer).</summary>
    private static System.Collections.Generic.List<GridLineToken> Tokenize(string value)
    {
        var list = new System.Collections.Generic.List<GridLineToken>();
        var span = value.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f')
            {
                i++;
                continue;
            }
            if (IsIntegerStart(span, i))
            {
                var start = i;
                if (span[i] == '+' || span[i] == '-') i++;
                while (i < span.Length && IsAsciiDigit(span[i])) i++;
                var text = span.Slice(start, i - start).ToString();
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    // Garbled — emit a "fake" out-of-range token (kind Ident with empty
                    // text) so the parser can reject. Cleaner than throwing here.
                    list.Add(new GridLineToken(GridLineTokenKind.Ident, string.Empty, 0));
                    continue;
                }
                list.Add(new GridLineToken(GridLineTokenKind.Integer, text, n));
                continue;
            }
            if (IsIdentStart(c))
            {
                var start = i;
                while (i < span.Length && IsIdentContinue(span[i])) i++;
                var text = span.Slice(start, i - start).ToString();
                list.Add(new GridLineToken(GridLineTokenKind.Ident, text, 0));
                continue;
            }
            // Unknown character — emit a sentinel that the parser will reject.
            list.Add(new GridLineToken(GridLineTokenKind.Ident, string.Empty, 0));
            i++;
        }
        return list;
    }

    private static bool IsIntegerStart(ReadOnlySpan<char> span, int i)
    {
        var c = span[i];
        if (IsAsciiDigit(c)) return true;
        if ((c == '+' || c == '-') && i + 1 < span.Length && IsAsciiDigit(span[i + 1]))
            return true;
        return false;
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
    private static bool IsIdentStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    private static bool IsIdentContinue(char c)
        => IsIdentStart(c) || IsAsciiDigit(c) || c == '-';

    private static bool ContainsCaseInsensitive(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void EmitInvalid(
        ICssDiagnosticsSink? sink, string propertyName, string value, string reason,
        CssSourceLocation location)
    {
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        var safeReason = DiagnosticTextSanitizer.Sanitize(reason);
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {safeValue}' — {safeReason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}

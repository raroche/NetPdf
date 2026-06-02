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

        // Per PR-#90 review F3 — defense in depth against CSS-wide keywords
        // (initial / inherit / unset / revert / revert-layer per CSS Cascade
        // L4 §7.3 + L5 §7.4) reaching this resolver. The cascade SHOULD
        // intercept these and substitute the property's initial / inherited /
        // previous-stylesheet-layer value BEFORE dispatch (= the central fix
        // is a separate cycle's scope per the review reply). For now, reject
        // them so they can't be silently stored as a named-line "initial" /
        // "inherit" GridLineValue. The cascade's invalid-fallback path then
        // uses the property initial value (= auto), matching the spec
        // intent for `initial`. The `inherit` case won't pull the parent's
        // value — that's the known cycle-0b limitation tracked separately.
        if (IsCssWideKeyword(value))
        {
            EmitInvalid(diagnostics, propertyName, value,
                "CSS-wide keyword reached the grid-line resolver — cascade should have intercepted (cycle-0b defense-in-depth path)",
                location);
            return ResolverResult.Invalid();
        }

        var tokens = Tokenize(value);
        if (tokens.Count == 0)
        {
            EmitInvalid(diagnostics, propertyName, value,
                "empty grid-line value", location);
            return ResolverResult.Invalid();
        }

        // Per PR-#90 review F1 — bail BEFORE TryParseGridLine if any Error
        // token surfaced from the tokenizer. This ensures malformed input
        // like `@`, `#`, or overflowing integers cleanly become Invalid +
        // diagnostic rather than reaching a validating-factory throw.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == GridLineTokenKind.Error)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    tokens[i].Text, location);
                return ResolverResult.Invalid();
            }
        }

        // Fast path: bare "auto" — the default + no side-table entry.
        if (tokens.Count == 1
            && tokens[0].Kind == GridLineTokenKind.Ident
            && string.Equals(tokens[0].Text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(KeywordIdAuto));
        }

        GridLineValue parsed;
        string reason;
        try
        {
            if (!TryParseGridLine(tokens, out parsed, out reason))
            {
                EmitInvalid(diagnostics, propertyName, value, reason, location);
                return ResolverResult.Invalid();
            }
        }
        catch (ArgumentException ex)
        {
            // Per PR-#90 review F1 — defense in depth. If a validating-
            // factory throw escapes (= a parser path I didn't anticipate
            // fed it bad input), surface as Invalid + diagnostic. The
            // Error-token guard above is the primary fix; this catch is
            // belt-and-braces.
            EmitInvalid(diagnostics, propertyName, value, ex.Message, location);
            return ResolverResult.Invalid();
        }

        // Box the struct so it can land in the side-table dictionary
        // (Dictionary<PropertyId, object>). The reader unboxes via
        // TryGetSideTablePayloadStruct<GridLineValue>.
        return ResolverResult.ResolvedSideTable((object)parsed);
    }

    /// <summary>Per CSS Cascade L4 §7.3 + L5 §7.4 — the CSS-wide keywords
    /// every property accepts. The cascade should intercept these BEFORE
    /// per-property resolvers run; this method exists for defense-in-depth
    /// rejection at the grid resolvers per PR-#90 review F3.</summary>
    internal static bool IsCssWideKeyword(string value) => CssWideKeyword.Is(value);

    /// <summary>Per PR-#91 review F1 — side-effect-free validation of a
    /// single <c>&lt;grid-line&gt;</c> value. Used by the grid shorthand
    /// expanders (<c>GridLineShorthandExpander</c> /
    /// <c>GridAreaShorthandExpander</c> in
    /// <c>NetPdf.Css.Parser.Preprocessing</c>) to pre-validate every
    /// shorthand component BEFORE emitting any longhand recovery record.
    /// This implements the CSS Cascade L4 §4.2 "invalid shorthand
    /// declaration contributes none of its longhands" rule — if ANY
    /// component is invalid, the whole shorthand drops atomically.
    ///
    /// <para>Returns <see langword="true"/> for any input that would
    /// produce a <see cref="ResolverResult.Resolved"/> from <see cref="Resolve"/>;
    /// returns <see langword="false"/> for any input that would produce
    /// <see cref="ResolverResult.Invalid"/>. Does NOT emit a diagnostic
    /// (= the expander's caller is the source of truth for diagnostics
    /// when the whole shorthand drops).</para></summary>
    internal static bool TryValidate(string value)
    {
        if (value is null) return false;
        // Mirrors the early-rejection guards in Resolve() — must stay in
        // sync (= changes to those guards should change here too).
        if (ContainsCaseInsensitive(value, "calc(")) return false;
        if (IsCssWideKeyword(value)) return false;

        var tokens = Tokenize(value);
        if (tokens.Count == 0) return false;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == GridLineTokenKind.Error) return false;
        }

        // Bare "auto" fast path — would land Resolved at Resolve().
        if (tokens.Count == 1
            && tokens[0].Kind == GridLineTokenKind.Ident
            && string.Equals(tokens[0].Text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return TryParseGridLine(tokens, out _, out _);
        }
        catch (ArgumentException)
        {
            // Defense-in-depth — a validating-factory throw from a parser
            // path we didn't anticipate. Mirrors Resolve's catch.
            return false;
        }
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
                // Per PR-#90 review F3 — CSS-wide keywords cannot serve as
                // custom-idents (defense in depth on the parser side too;
                // Resolve's IsCssWideKeyword guard catches the bare-keyword
                // case, this catches the compound `2 initial` case).
                if (IsCssWideKeyword(t.Text))
                {
                    reason = $"CSS-wide keyword '{t.Text}' is not a valid custom-ident";
                    return false;
                }
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
        /// <summary>Per PR-#90 review F1 — explicit error sentinel emitted
        /// when the tokenizer can't classify input (unknown character,
        /// overflowing integer, malformed mixed-token sequence). The
        /// parser checks for Error tokens first and bails out with a
        /// diagnostic; this avoids the prior "fake empty Ident" sentinel
        /// pattern which fed empty strings into validating factories +
        /// triggered uncaught ArgumentException.</summary>
        Error = 255,
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

    /// <summary>Per PR-#90 review F8 — cap the maximum tokens produced from
    /// a single grid-line declaration. grid-line values have at most 3
    /// tokens per §8.3 (= <c>span foo 2</c>); a sane upper bound of 16
    /// catches hostile input without rejecting legal forms.</summary>
    private const int MaxGridLineTokens = 16;

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
            if (list.Count >= MaxGridLineTokens)
            {
                // Per F8 — bail with a single Error token; parser rejects.
                list.Add(new GridLineToken(GridLineTokenKind.Error,
                    "token budget exceeded", 0));
                return list;
            }
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
                    // Per F1 — overflowing / unparseable integer becomes an
                    // explicit Error token; parser rejects with diagnostic.
                    list.Add(new GridLineToken(GridLineTokenKind.Error,
                        $"integer '{text}' is out of range", 0));
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
            // Per F1 — unknown character becomes an explicit Error token
            // (NOT a fake empty Ident — that pattern fed empty strings into
            // validating factories which throw ArgumentException). Parser
            // rejects with a diagnostic.
            list.Add(new GridLineToken(GridLineTokenKind.Error,
                $"unexpected character '{c}'", 0));
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

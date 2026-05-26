// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Per Phase 3 Task 17 cycle 0b — resolves CSS values for the two
/// <see cref="PropertyType.GridTemplateList"/> longhands (<c>grid-template-rows</c> /
/// <c>grid-template-columns</c>) per CSS Grid L1 §7.2.
///
/// <para><b>Grammar accepted:</b>
/// <code>
/// &lt;track-list&gt; = [ &lt;line-names&gt;? [ &lt;track-size&gt; | &lt;track-repeat&gt; ] ]+ &lt;line-names&gt;?
/// &lt;line-names&gt; = '[' &lt;custom-ident&gt;* ']'
/// &lt;track-size&gt;  = &lt;track-breadth&gt;
///                | minmax( &lt;inflexible-breadth&gt; , &lt;track-breadth&gt; )
///                | fit-content( &lt;length-percentage&gt; )
/// &lt;track-breadth&gt;    = &lt;length-percentage&gt; | &lt;flex&gt; | min-content | max-content | auto
/// &lt;inflexible-breadth&gt; = &lt;length-percentage&gt; | min-content | max-content | auto
/// &lt;track-repeat&gt; = repeat( [ &lt;int [1,∞]&gt; | auto-fill | auto-fit ] , … )
/// </code></para>
///
/// <para><b>Default (none)</b> resolves to <see cref="ComputedSlot.FromKeyword(int)"/>
/// with id 0 — NO side-table payload. The reader returns <see cref="TrackList.None"/>
/// for that path. Any non-default track list lands a <see cref="TrackList"/> in the
/// side-table.</para>
///
/// <para><b>Layout-time expansion contract.</b> <c>repeat(&lt;integer&gt;, …)</c>
/// is preserved as a <see cref="TrackRepeat"/> with positive
/// <see cref="TrackRepeat.Count"/>; expansion to flat
/// <see cref="TrackEntry"/> tracks happens at layout time (= cycle 1+ work
/// in <c>GridLayouter</c>). <c>auto-fill</c> / <c>auto-fit</c> map to
/// <see cref="TrackRepeat.Count"/> = 0 / -1; expansion at layout time
/// depends on the container size.</para>
///
/// <para><b>DoS guards</b> per PR-#89 review P2 #7: <c>repeat(N, …)</c>
/// counts above <see cref="TrackRepeat.MaxRepeatCount"/> are rejected at
/// parse time (= <c>repeat(1000000000, 1px)</c> never produces a 1-billion-
/// element AST). Combined with <see cref="TrackList.MaxExpandedTrackCount"/>
/// (= layout-time gate), this bounds total CPU + allocation per grid
/// declaration.</para>
///
/// <para><b>L19+ deferrals.</b> <c>calc()</c> track sizes are rejected at
/// parse time with a diagnostic. Font-relative units (<c>em</c>, <c>rem</c>,
/// etc.) and viewport-relative units (<c>vw</c>, <c>vh</c>, etc.) are
/// rejected because the parse stage doesn't have the font / viewport context
/// to fold them; cycle 0b returns Invalid + a diagnostic. Cycle 1+ may
/// upgrade these to layout-time deferral once <c>GridLayouter</c> has the
/// resolved-px context.</para>
/// </summary>
internal static class GridTemplateListResolver
{
    /// <summary>The keyword id used for the <c>none</c> default. Mirrors
    /// <see cref="LengthResolver.KeywordIdNone"/> = 0.</summary>
    public const int KeywordIdNone = 0;

    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        // L19+ deferral — calc() on track sizes is out of scope for v1.
        if (ContainsCaseInsensitive(value, "calc("))
        {
            EmitInvalid(diagnostics, propertyName, value,
                "calc() is not supported on grid track sizes per L19+ deferral", location);
            return ResolverResult.Invalid();
        }

        var tokens = Tokenize(value, out var tokenizeError);
        if (tokenizeError is not null)
        {
            EmitInvalid(diagnostics, propertyName, value, tokenizeError, location);
            return ResolverResult.Invalid();
        }
        if (tokens.Count == 0)
        {
            EmitInvalid(diagnostics, propertyName, value, "empty track list", location);
            return ResolverResult.Invalid();
        }

        // Fast path: bare "none" — the default + no side-table entry.
        if (tokens.Count == 1
            && tokens[0].Kind == TokenKind.Ident
            && string.Equals(tokens[0].Text, "none", StringComparison.OrdinalIgnoreCase))
        {
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(KeywordIdNone));
        }

        var parser = new Parser(tokens);
        try
        {
            var items = parser.ParseTrackList();
            if (!parser.AtEnd)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    "trailing tokens after track list", location);
                return ResolverResult.Invalid();
            }
            if (items.Length == 0)
            {
                EmitInvalid(diagnostics, propertyName, value,
                    "track list must contain at least one track", location);
                return ResolverResult.Invalid();
            }
            var list = new TrackList(items);
            return ResolverResult.ResolvedSideTable(list);
        }
        catch (GridParseException ex)
        {
            EmitInvalid(diagnostics, propertyName, value, ex.Message, location);
            return ResolverResult.Invalid();
        }
        catch (ArgumentException ex)
        {
            // Validating-factory rejection — surface the message + emit invalid.
            EmitInvalid(diagnostics, propertyName, value, ex.Message, location);
            return ResolverResult.Invalid();
        }
    }

    // =================================================================
    //  Parser
    // =================================================================

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
            _pos = 0;
        }

        public bool AtEnd => _pos >= _tokens.Count;

        private Token Current => _tokens[_pos];
        private Token Peek(int offset)
            => _pos + offset < _tokens.Count
                ? _tokens[_pos + offset]
                : default;

        private void Advance() => _pos++;

        private bool TryConsume(TokenKind kind)
        {
            if (AtEnd || _tokens[_pos].Kind != kind) return false;
            _pos++;
            return true;
        }

        private bool IsIdent(string text)
            => !AtEnd
               && _tokens[_pos].Kind == TokenKind.Ident
               && string.Equals(_tokens[_pos].Text, text, StringComparison.OrdinalIgnoreCase);

        public ImmutableArray<TrackListItem> ParseTrackList()
        {
            var items = ImmutableArray.CreateBuilder<TrackListItem>();
            while (!AtEnd)
            {
                if (Current.Kind == TokenKind.LBracket)
                {
                    // Line names at the top level.
                    var names = ParseLineNames();
                    foreach (var n in names)
                    {
                        items.Add(TrackListNamedLine.Create(n));
                    }
                    continue;
                }

                if (Current.Kind == TokenKind.Ident
                    && string.Equals(Current.Text, "repeat", StringComparison.OrdinalIgnoreCase)
                    && Peek(1).Kind == TokenKind.LParen)
                {
                    var repeat = ParseRepeat();
                    items.Add(new TrackListRepeat(repeat));
                    continue;
                }

                // Try a track-size (track-breadth | minmax(...) | fit-content(...)).
                if (TryParseTrackSize(out var entry))
                {
                    items.Add(new TrackListEntry(entry));
                    continue;
                }

                throw new GridParseException(
                    $"unexpected token '{Current.Text}' in track list");
            }
            return items.ToImmutable();
        }

        private List<string> ParseLineNames()
        {
            // Caller knows Current is '['.
            if (!TryConsume(TokenKind.LBracket))
            {
                throw new GridParseException("expected '[' for line names");
            }
            var names = new List<string>();
            while (!AtEnd && Current.Kind != TokenKind.RBracket)
            {
                if (Current.Kind != TokenKind.Ident)
                {
                    throw new GridParseException(
                        "line-names group accepts identifiers only");
                }
                var t = Current;
                // Reserved words can't be custom-idents per §8.3 + CSS Values L4.
                if (string.Equals(t.Text, "auto", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Text, "span", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GridParseException(
                        $"'{t.Text}' is a reserved keyword and cannot be a named line");
                }
                names.Add(t.Text);
                Advance();
            }
            if (!TryConsume(TokenKind.RBracket))
            {
                throw new GridParseException("unterminated '[' in line names");
            }
            return names;
        }

        private TrackRepeat ParseRepeat()
        {
            // Current is the 'repeat' ident, Peek(1) is '('.
            Advance(); // consume 'repeat'
            if (!TryConsume(TokenKind.LParen))
            {
                throw new GridParseException("expected '(' after 'repeat'");
            }

            int count;
            if (Current.Kind == TokenKind.Ident
                && string.Equals(Current.Text, "auto-fill", StringComparison.OrdinalIgnoreCase))
            {
                count = 0;
                Advance();
            }
            else if (Current.Kind == TokenKind.Ident
                && string.Equals(Current.Text, "auto-fit", StringComparison.OrdinalIgnoreCase))
            {
                count = -1;
                Advance();
            }
            else if (Current.Kind == TokenKind.Number)
            {
                var n = Current.Number;
                if (!IsFiniteInteger(n))
                {
                    throw new GridParseException(
                        "repeat() count must be a positive integer");
                }
                var asInt = (int)n;
                if (asInt < 1)
                {
                    throw new GridParseException(
                        "repeat() integer count must be ≥ 1 per CSS Grid L1 §7.2.3");
                }
                if (asInt > TrackRepeat.MaxRepeatCount)
                {
                    throw new GridParseException(
                        $"repeat() count {asInt} exceeds MaxRepeatCount = "
                        + $"{TrackRepeat.MaxRepeatCount} (DoS guard per PR-#89 P2 #7)");
                }
                count = asInt;
                Advance();
            }
            else
            {
                throw new GridParseException(
                    "repeat() first argument must be a positive integer, 'auto-fill', or 'auto-fit'");
            }

            if (!TryConsume(TokenKind.Comma))
            {
                throw new GridParseException("expected ',' after repeat() count");
            }

            var pattern = ImmutableArray.CreateBuilder<TrackRepeatItem>();
            while (!AtEnd && Current.Kind != TokenKind.RParen)
            {
                if (Current.Kind == TokenKind.LBracket)
                {
                    var names = ParseLineNames();
                    foreach (var n in names)
                    {
                        pattern.Add(TrackRepeatNamedLine.Create(n));
                    }
                    continue;
                }
                if (Current.Kind == TokenKind.Ident
                    && string.Equals(Current.Text, "repeat", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GridParseException(
                        "nested repeat() is forbidden per CSS Grid L1 §7.2.3");
                }
                if (TryParseTrackSize(out var entry))
                {
                    pattern.Add(new TrackRepeatEntry(entry));
                    continue;
                }
                throw new GridParseException(
                    $"unexpected token '{Current.Text}' in repeat() pattern");
            }
            if (!TryConsume(TokenKind.RParen))
            {
                throw new GridParseException("unterminated 'repeat('");
            }
            return TrackRepeat.Create(count, pattern.ToImmutable());
        }

        private bool TryParseTrackSize(out TrackEntry entry)
        {
            entry = default;
            if (AtEnd) return false;

            // minmax( min , max )
            if (Current.Kind == TokenKind.Ident
                && string.Equals(Current.Text, "minmax", StringComparison.OrdinalIgnoreCase)
                && Peek(1).Kind == TokenKind.LParen)
            {
                Advance(); // consume 'minmax'
                Advance(); // consume '('
                if (!TryParseInflexibleBreadth(out var min))
                {
                    throw new GridParseException(
                        "minmax() first argument must be a non-flexible breadth (length/percent/auto/min-content/max-content)");
                }
                if (!TryConsume(TokenKind.Comma))
                {
                    throw new GridParseException("expected ',' inside minmax()");
                }
                if (!TryParseTrackBreadth(out var max))
                {
                    throw new GridParseException(
                        "minmax() second argument must be a track breadth");
                }
                if (!TryConsume(TokenKind.RParen))
                {
                    throw new GridParseException("unterminated 'minmax('");
                }
                entry = TrackEntry.ForMinMax(min, max);
                return true;
            }

            // fit-content( length-percentage )
            if (Current.Kind == TokenKind.Ident
                && string.Equals(Current.Text, "fit-content", StringComparison.OrdinalIgnoreCase)
                && Peek(1).Kind == TokenKind.LParen)
            {
                Advance(); // consume 'fit-content'
                Advance(); // consume '('
                if (Current.Kind != TokenKind.Dimension)
                {
                    throw new GridParseException(
                        "fit-content() requires a <length-percentage> argument");
                }
                if (!TryDimensionToTrackEntry(Current, out var limit, allowFr: false))
                {
                    throw new GridParseException(
                        $"fit-content() argument must be a length or percentage; got '{Current.Text}'");
                }
                Advance();
                if (!TryConsume(TokenKind.RParen))
                {
                    throw new GridParseException("unterminated 'fit-content('");
                }
                if (limit.IsPercentage)
                {
                    entry = TrackEntry.ForFitContent(limit.LengthPx, isPercentage: true);
                }
                else
                {
                    entry = TrackEntry.ForFitContent(limit.LengthPx);
                }
                return true;
            }

            // Track breadth.
            return TryParseTrackBreadth(out entry);
        }

        private bool TryParseTrackBreadth(out TrackEntry entry)
        {
            entry = default;
            if (AtEnd) return false;
            var t = Current;
            if (t.Kind == TokenKind.Ident)
            {
                // <flex> tokens may have been classified as Ident if they came in
                // unitless (= a bare number). But Number/Dimension tokens are
                // distinct; we'd only reach Ident here for keywords.
                if (string.Equals(t.Text, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    entry = TrackEntry.ForAuto();
                    return true;
                }
                if (string.Equals(t.Text, "min-content", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    entry = TrackEntry.ForMinContent();
                    return true;
                }
                if (string.Equals(t.Text, "max-content", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    entry = TrackEntry.ForMaxContent();
                    return true;
                }
                return false;
            }
            if (t.Kind == TokenKind.Dimension)
            {
                if (!TryDimensionToTrackEntry(t, out entry, allowFr: true))
                {
                    throw new GridParseException(
                        $"unsupported dimension unit '{t.Unit}' in track breadth");
                }
                Advance();
                return true;
            }
            return false;
        }

        private bool TryParseInflexibleBreadth(out TrackEntry entry)
        {
            // Same as <track-breadth> minus <flex>.
            entry = default;
            if (AtEnd) return false;
            var t = Current;
            if (t.Kind == TokenKind.Ident)
            {
                if (string.Equals(t.Text, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    entry = TrackEntry.ForAuto();
                    return true;
                }
                if (string.Equals(t.Text, "min-content", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    entry = TrackEntry.ForMinContent();
                    return true;
                }
                if (string.Equals(t.Text, "max-content", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    entry = TrackEntry.ForMaxContent();
                    return true;
                }
                return false;
            }
            if (t.Kind == TokenKind.Dimension)
            {
                // <flex> forbidden in min arg per §7.2.4.
                if (string.Equals(t.Unit, "fr", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (!TryDimensionToTrackEntry(t, out entry, allowFr: false))
                {
                    return false;
                }
                Advance();
                return true;
            }
            return false;
        }
    }

    /// <summary>Map a Dimension token to a <see cref="TrackEntry"/>. Handles
    /// absolute lengths (px/in/cm/mm/pt/pc/q) → ForLength; percentages →
    /// ForPercentage; flex (fr) → ForFr (when <paramref name="allowFr"/>);
    /// rejects relative units (em/rem/vw/etc.) at this layer.</summary>
    private static bool TryDimensionToTrackEntry(Token t, out TrackEntry entry, bool allowFr)
    {
        entry = default;
        if (!double.IsFinite(t.Number) || t.Number < 0)
        {
            throw new GridParseException(
                $"track size {t.Number}{t.Unit} must be finite + non-negative");
        }

        var unit = t.Unit;
        if (unit == "%")
        {
            entry = TrackEntry.ForPercentage(t.Number);
            return true;
        }
        if (string.Equals(unit, "fr", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowFr) return false;
            entry = TrackEntry.ForFr(t.Number);
            return true;
        }
        if (TryAbsoluteUnitToPx(unit, t.Number, out var px))
        {
            entry = TrackEntry.ForLength(px);
            return true;
        }
        // Font/viewport/container-relative units — rejected at this layer.
        return false;
    }

    private static bool TryAbsoluteUnitToPx(string unit, double number, out double px)
    {
        // CSS Values L4 §6.1.
        switch (unit)
        {
            case "px": px = number; return true;
            case "in": px = number * 96.0; return true;
            case "cm": px = number * (96.0 / 2.54); return true;
            case "mm": px = number * (96.0 / 25.4); return true;
            case "q": px = number * (96.0 / 25.4 / 4.0); return true;
            case "pt": px = number * (96.0 / 72.0); return true;
            case "pc": px = number * 16.0; return true;
            default: px = 0; return false;
        }
    }

    // =================================================================
    //  Tokenizer
    // =================================================================

    private enum TokenKind : byte
    {
        None = 0,
        Ident = 1,
        Number = 2,
        Dimension = 3,
        LParen = 4,
        RParen = 5,
        LBracket = 6,
        RBracket = 7,
        Comma = 8,
    }

    private readonly struct Token
    {
        public Token(TokenKind kind, string text, double number, string unit)
        {
            Kind = kind;
            Text = text;
            Number = number;
            Unit = unit;
        }
        public TokenKind Kind { get; }
        public string Text { get; }
        public double Number { get; }
        public string Unit { get; }
    }

    private static List<Token> Tokenize(string value, out string? error)
    {
        error = null;
        var list = new List<Token>();
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
            if (c == '(')
            {
                list.Add(new Token(TokenKind.LParen, "(", 0, string.Empty));
                i++;
                continue;
            }
            if (c == ')')
            {
                list.Add(new Token(TokenKind.RParen, ")", 0, string.Empty));
                i++;
                continue;
            }
            if (c == '[')
            {
                list.Add(new Token(TokenKind.LBracket, "[", 0, string.Empty));
                i++;
                continue;
            }
            if (c == ']')
            {
                list.Add(new Token(TokenKind.RBracket, "]", 0, string.Empty));
                i++;
                continue;
            }
            if (c == ',')
            {
                list.Add(new Token(TokenKind.Comma, ",", 0, string.Empty));
                i++;
                continue;
            }
            if (IsNumberStart(span, i))
            {
                var start = i;
                if (span[i] == '+' || span[i] == '-') i++;
                var sawDigit = false;
                while (i < span.Length && IsAsciiDigit(span[i]))
                {
                    i++;
                    sawDigit = true;
                }
                if (i < span.Length && span[i] == '.')
                {
                    i++;
                    while (i < span.Length && IsAsciiDigit(span[i]))
                    {
                        i++;
                        sawDigit = true;
                    }
                }
                if (!sawDigit)
                {
                    error = $"malformed numeric value at position {start}";
                    return list;
                }
                // Optional exponent (with lookahead guard so `1e` standalone doesn't break).
                if (i < span.Length && (span[i] == 'e' || span[i] == 'E'))
                {
                    var lookahead = i + 1;
                    if (lookahead < span.Length && (span[lookahead] == '+' || span[lookahead] == '-'))
                        lookahead++;
                    var sawExpDigit = false;
                    while (lookahead < span.Length && IsAsciiDigit(span[lookahead]))
                    {
                        sawExpDigit = true;
                        lookahead++;
                    }
                    if (sawExpDigit) i = lookahead;
                }
                var numText = span.Slice(start, i - start).ToString();
                if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    error = $"could not parse numeric value '{numText}'";
                    return list;
                }
                // Trailing unit? Could be % or an ident.
                if (i < span.Length && span[i] == '%')
                {
                    list.Add(new Token(TokenKind.Dimension, numText + "%", n, "%"));
                    i++;
                    continue;
                }
                if (i < span.Length && IsIdentStart(span[i]))
                {
                    var unitStart = i;
                    while (i < span.Length && IsIdentContinue(span[i])) i++;
                    var unit = span.Slice(unitStart, i - unitStart).ToString().ToLowerInvariant();
                    list.Add(new Token(TokenKind.Dimension, numText + unit, n, unit));
                    continue;
                }
                list.Add(new Token(TokenKind.Number, numText, n, string.Empty));
                continue;
            }
            if (IsIdentStart(c))
            {
                var start = i;
                while (i < span.Length && IsIdentContinue(span[i])) i++;
                var text = span.Slice(start, i - start).ToString();
                list.Add(new Token(TokenKind.Ident, text, 0, string.Empty));
                continue;
            }
            error = $"unexpected character '{c}' at position {i}";
            return list;
        }
        return list;
    }

    private static bool IsNumberStart(ReadOnlySpan<char> span, int i)
    {
        var c = span[i];
        if (IsAsciiDigit(c)) return true;
        if (c == '.' && i + 1 < span.Length && IsAsciiDigit(span[i + 1])) return true;
        if ((c == '+' || c == '-') && i + 1 < span.Length)
        {
            var next = span[i + 1];
            if (IsAsciiDigit(next)) return true;
            if (next == '.' && i + 2 < span.Length && IsAsciiDigit(span[i + 2])) return true;
        }
        return false;
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
    private static bool IsIdentStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    private static bool IsIdentContinue(char c)
        => IsIdentStart(c) || IsAsciiDigit(c) || c == '-';

    private static bool IsFiniteInteger(double n)
        => double.IsFinite(n) && Math.Floor(n) == n && n >= int.MinValue && n <= int.MaxValue;

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

    /// <summary>Internal control-flow exception used by the recursive-descent
    /// parser to bail out on the first grammar error. Caught by the public
    /// <see cref="Resolve"/> entry point + converted to an Invalid result +
    /// diagnostic. NOT thrown across public API surfaces.</summary>
    private sealed class GridParseException : Exception
    {
        public GridParseException(string message) : base(message) { }
    }
}

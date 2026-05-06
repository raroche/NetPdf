// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Math-function resolver per CSS Values L4 §10. Recognizes <c>calc()</c>, <c>min()</c>,
/// <c>max()</c>, <c>clamp()</c>, <c>abs()</c>, and <c>sign()</c> in declaration value
/// text and reduces them to a single concrete <see cref="CalcValue"/> when every
/// operand can be evaluated in isolation. Expressions with percentages or other
/// unresolved-at-compute-time units are left as the original function text so layout
/// (Phase 3) can finalize them; the v1 contract is "fully reduce when possible, preserve
/// otherwise".
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline position.</b> Runs after <see cref="VarSubstitution"/> per Tasks 8+9
/// — once <c>var()</c> references are resolved, math functions can see their final
/// operands. Output remains string-based; Task 10's typed-value tree replaces the
/// substituted text with structured <see cref="CalcValue"/> wrappers.
/// </para>
/// <para>
/// <b>Reducible vs deferred.</b> An expression reduces fully when every operand resolves
/// to either a <see cref="CalcUnit.Number"/> or a single dimensioned value sharing the
/// same <see cref="CalcUnit"/>. Mixed dimensions (e.g., <c>50% + 16px</c>) are deferred
/// — the original function text is preserved verbatim. Multiplication / division by a
/// number scales the dimension cleanly (so <c>16px * 2</c> reduces to <c>32px</c>).
/// </para>
/// <para>
/// <b>Diagnostics.</b> Syntactically broken expressions emit
/// <see cref="CssDiagnosticCodes.CssCalcInvalid001"/>; division by zero emits
/// <see cref="CssDiagnosticCodes.CssCalcDivByZero001"/>. In both cases the original
/// function text is preserved so the property's behavior remains observable to the user
/// (downstream stages may treat the un-reduced text as "invalid at computed value time"
/// per spec §10.5).
/// </para>
/// </remarks>
internal static class CalcResolver
{
    /// <summary>Maximum nested-paren / function depth before the parser bails — guards
    /// against pathological deeply-nested input. 32 matches our <see cref="VarSubstitution"/>
    /// limit for consistency.</summary>
    public const int MaxDepth = 32;

    /// <summary>Math-function names recognized by this resolver, lowercase. Detection
    /// is ASCII case-insensitive per CSS Syntax L3, but the name on the left is the
    /// canonical form used for dispatch.</summary>
    private static readonly string[] MathFunctionNames =
        { "calc", "clamp", "min", "max", "abs", "sign" };

    /// <summary>Substitute every fully-reducible math function in <paramref name="rawValue"/>.
    /// Returns the rewritten text. Functions with deferred (mixed-unit) operands are
    /// preserved verbatim. Diagnostics emit through <paramref name="diagnostics"/>.</summary>
    public static string Resolve(
        string rawValue,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default)
    {
        if (string.IsNullOrEmpty(rawValue)) return rawValue ?? string.Empty;
        if (!ContainsMathFunction(rawValue)) return rawValue;

        var output = new StringBuilder(rawValue.Length);
        var pos = 0;
        while (pos < rawValue.Length)
        {
            var c = rawValue[pos];
            if (c == '"' || c == '\'')
            {
                var stringEnd = SkipString(rawValue, pos);
                output.Append(rawValue, pos, stringEnd - pos);
                pos = stringEnd;
                continue;
            }
            if (TryReadFunctionStart(rawValue, pos, out var fnName, out var bodyStart))
            {
                var bodyEnd = FindMatchingCloseParen(rawValue, bodyStart);
                if (bodyEnd < 0)
                {
                    // Unterminated — pass through verbatim.
                    output.Append(rawValue, pos, rawValue.Length - pos);
                    return output.ToString();
                }
                var body = rawValue[bodyStart..bodyEnd];
                var reduced = TryReduceFunction(fnName, body, diagnostics, location);
                if (reduced.HasValue)
                {
                    output.Append(reduced.Value.ToCssText());
                }
                else
                {
                    // Preserve the original function text (deferred or invalid case).
                    output.Append(rawValue, pos, bodyEnd - pos + 1);
                }
                pos = bodyEnd + 1;
                continue;
            }
            output.Append(c);
            pos++;
        }
        return output.ToString();
    }

    /// <summary>Cheap fast-path: scan for any math-function start without entering the
    /// per-character walk for values that don't have any.</summary>
    private static bool ContainsMathFunction(string value)
    {
        var pos = 0;
        while (pos < value.Length)
        {
            var c = value[pos];
            if (c == '"' || c == '\'') { pos = SkipString(value, pos); continue; }
            if (TryReadFunctionStart(value, pos, out _, out _)) return true;
            pos++;
        }
        return false;
    }

    /// <summary>Detect a math-function start at <paramref name="pos"/>. Returns
    /// the function name (lowercase) + the position just past the opening
    /// <c>(</c>. ASCII case-insensitive per CSS Syntax L3.</summary>
    private static bool TryReadFunctionStart(string text, int pos, out string fnName, out int bodyStart)
    {
        fnName = string.Empty;
        bodyStart = -1;
        // Each candidate function — match prefix + '('.
        foreach (var name in MathFunctionNames)
        {
            if (pos + name.Length + 1 > text.Length) continue;
            bool ok = true;
            for (var i = 0; i < name.Length; i++)
            {
                if (AsciiToLower(text[pos + i]) != name[i]) { ok = false; break; }
            }
            if (!ok) continue;
            if (text[pos + name.Length] != '(') continue;
            // Must NOT be preceded by an ident-continue char (so `mycalc(...)` doesn't match).
            if (pos > 0 && IsIdentContinue(text[pos - 1])) continue;
            fnName = name;
            bodyStart = pos + name.Length + 1;
            return true;
        }
        return false;
    }

    /// <summary>Try to reduce a math function to a single <see cref="CalcValue"/>.
    /// Returns null when the expression is deferred (mixed units / contains
    /// percentage when context isn't known) or invalid. Catches the resolver's
    /// internal exception types and emits diagnostics.</summary>
    private static CalcValue? TryReduceFunction(string fnName, string body,
        ICssDiagnosticsSink? diagnostics, CssSourceLocation location)
    {
        try
        {
            return ReduceFunctionInternal(fnName, body);
        }
        catch (CalcParseException ex)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssCalcInvalid001,
                $"Invalid {fnName}() expression: {ex.Message}",
                CssDiagnosticSeverity.Warning, location));
            return null;
        }
        catch (CalcDivByZeroException)
        {
            diagnostics?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssCalcDivByZero001,
                $"Division by zero in {fnName}() expression.",
                CssDiagnosticSeverity.Warning, location));
            return null;
        }
        catch (CalcDeferredException)
        {
            // Mixed-unit / context-dependent — preserve verbatim. No diagnostic.
            return null;
        }
    }

    /// <summary>Inner reducer — does not catch exceptions. Used by the parser when a
    /// nested math function appears inside an expression (e.g.,
    /// <c>calc(min(16px, 4px) + 4px)</c>): the inner reduction needs to propagate
    /// deferred / invalid exceptions up so the outer expression bails appropriately.</summary>
    private static CalcValue? ReduceFunctionInternal(string fnName, string body)
    {
        return fnName switch
        {
            "calc" => ReduceSingleArgInternal(body),
            "abs" => ApplyUnaryInternal(body, Math.Abs),
            "sign" => ApplySignInternal(body),
            "min" => ApplyMinMaxInternal(body, isMin: true),
            "max" => ApplyMinMaxInternal(body, isMin: false),
            "clamp" => ApplyClampInternal(body),
            _ => null,
        };
    }

    private static CalcValue? ReduceSingleArgInternal(string body)
    {
        var parser = new CalcParser(body);
        var value = parser.ParseExpression();
        parser.SkipWhitespace();
        if (!parser.IsEnd) throw new CalcParseException("trailing tokens after expression");
        return value;
    }

    private static CalcValue? ApplyUnaryInternal(string body, Func<double, double> op)
    {
        var v = ReduceSingleArgInternal(body);
        return v is null ? null : new CalcValue(op(v.Value.Magnitude), v.Value.Unit);
    }

    private static CalcValue? ApplySignInternal(string body)
    {
        var v = ReduceSingleArgInternal(body);
        if (v is null) return null;
        var s = v.Value.Magnitude;
        var sign = s > 0 ? 1.0 : (s < 0 ? -1.0 : 0.0);
        return new CalcValue(sign, CalcUnit.Number);
    }

    private static CalcValue? ApplyMinMaxInternal(string body, bool isMin)
    {
        var args = SplitTopLevelCommas(body);
        if (args.Count == 0) throw new CalcParseException("min/max requires at least one argument");
        CalcValue best = ParseSingleExpression(args[0]);
        for (var i = 1; i < args.Count; i++)
        {
            var next = ParseSingleExpression(args[i]);
            if (next.Unit != best.Unit) throw new CalcDeferredException();
            best = isMin
                ? (next.Magnitude < best.Magnitude ? next : best)
                : (next.Magnitude > best.Magnitude ? next : best);
        }
        return best;
    }

    private static CalcValue? ApplyClampInternal(string body)
    {
        var args = SplitTopLevelCommas(body);
        if (args.Count != 3)
            throw new CalcParseException($"clamp() requires 3 arguments, got {args.Count}");
        var lo = ParseSingleExpression(args[0]);
        var val = ParseSingleExpression(args[1]);
        var hi = ParseSingleExpression(args[2]);
        if (lo.Unit != val.Unit || val.Unit != hi.Unit) throw new CalcDeferredException();
        var clamped = Math.Max(lo.Magnitude, Math.Min(val.Magnitude, hi.Magnitude));
        return new CalcValue(clamped, val.Unit);
    }

    private static CalcValue ParseSingleExpression(string text)
    {
        var p = new CalcParser(text);
        var v = p.ParseExpression();
        p.SkipWhitespace();
        if (!p.IsEnd) throw new CalcParseException("trailing tokens after expression");
        return v;
    }

    /// <summary>Split a function body on top-level commas (paren-balanced, string-aware).</summary>
    private static List<string> SplitTopLevelCommas(string body)
    {
        var result = new List<string>();
        int depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(body, i) - 1;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(body[start..i]);
                start = i + 1;
            }
        }
        result.Add(body[start..]);
        return result;
    }

    private static int FindMatchingCloseParen(string text, int start)
    {
        int depth = 1;
        var pos = start;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '"' || c == '\'') { pos = SkipString(text, pos); continue; }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return pos; }
            pos++;
        }
        return -1;
    }

    private static int SkipString(string text, int start)
    {
        var quote = text[start];
        var pos = start + 1;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '\\' && pos + 1 < text.Length) { pos += 2; continue; }
            pos++;
            if (c == quote) return pos;
        }
        return pos;
    }

    private static char AsciiToLower(char c) =>
        (c >= 'A' && c <= 'Z') ? (char)(c + ('a' - 'A')) : c;

    private static bool IsIdentContinue(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
        || c == '-' || c == '_' || c >= 0x80;

    /// <summary>Recursive-descent parser for a single calc expression body. Implements
    /// L4 §10.6's grammar: <c>expr := term (('+' | '-') term)*</c>;
    /// <c>term := factor (('*' | '/') factor)*</c>;
    /// <c>factor := value | '(' expr ')'</c>; <c>value := number | dimension | percentage</c>.
    /// </summary>
    private ref struct CalcParser
    {
        private readonly string _text;
        private int _pos;
        private int _depth;

        public CalcParser(string text) { _text = text; _pos = 0; _depth = 0; }
        public readonly bool IsEnd
        {
            get
            {
                var p = _pos;
                while (p < _text.Length && IsWhitespace(_text[p])) p++;
                return p >= _text.Length;
            }
        }

        public void SkipWhitespace()
        {
            while (_pos < _text.Length && IsWhitespace(_text[_pos])) _pos++;
        }

        public CalcValue ParseExpression()
        {
            if (_depth > MaxDepth) throw new CalcParseException("expression too deeply nested");
            SkipWhitespace();
            var left = ParseTerm();
            while (TryConsumeAdditiveOperator(out var op))
            {
                SkipWhitespace();
                var right = ParseTerm();
                left = (op == '+') ? Add(left, right) : Subtract(left, right);
            }
            return left;
        }

        /// <summary>Per CSS Values L4 §10.4 the additive operators MUST be surrounded by
        /// whitespace on both sides (so <c>1px-2px</c> stays a hyphenated identifier and
        /// <c>1px +2px</c> / <c>1px+ 2px</c> are syntax errors, not subtraction). Look
        /// ahead without committing _pos until both sides verify; throw on missing
        /// trailing whitespace.</summary>
        private bool TryConsumeAdditiveOperator(out char op)
        {
            op = '\0';
            var p = _pos;
            int wsBefore = 0;
            while (p < _text.Length && IsWhitespace(_text[p])) { p++; wsBefore++; }
            if (p >= _text.Length) return false;
            var c = _text[p];
            if (c != '+' && c != '-') return false;
            if (wsBefore == 0)
                throw new CalcParseException(
                    $"'{c}' operator requires whitespace on both sides per CSS Values L4 §10.4");
            if (p + 1 >= _text.Length || !IsWhitespace(_text[p + 1]))
                throw new CalcParseException(
                    $"'{c}' operator requires whitespace on both sides per CSS Values L4 §10.4");
            _pos = p + 1;
            op = c;
            return true;
        }

        private CalcValue ParseTerm()
        {
            var left = ParseFactor();
            while (TryConsumeMultiplicativeOperator(out var op))
            {
                SkipWhitespace();
                var right = ParseFactor();
                left = (op == '*') ? Multiply(left, right) : Divide(left, right);
            }
            return left;
        }

        /// <summary>Per CSS Values L4 §10.4 the multiplicative operators do NOT require
        /// surrounding whitespace (so <c>16px*2</c> is valid). Look ahead without
        /// committing _pos so the additive-operator detector upstream still sees its
        /// own leading whitespace.</summary>
        private bool TryConsumeMultiplicativeOperator(out char op)
        {
            op = '\0';
            var p = _pos;
            while (p < _text.Length && IsWhitespace(_text[p])) p++;
            if (p >= _text.Length) return false;
            var c = _text[p];
            if (c != '*' && c != '/') return false;
            _pos = p + 1;
            op = c;
            return true;
        }

        private CalcValue ParseFactor()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw new CalcParseException("unexpected end of expression");
            var c = _text[_pos];
            if (c == '(')
            {
                _pos++;
                _depth++;
                var inner = ParseExpression();
                _depth--;
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')')
                    throw new CalcParseException("expected ')'");
                _pos++;
                return inner;
            }
            // Nested math function: peek for one of the function names. If matched,
            // recursively reduce the inner expression and substitute the result as the
            // factor's value. Supports `calc(min(...) + ...)`, `max(calc(...), ...)`, etc.
            if (IsLetter(c) && TryReadFunctionStart(_text, _pos, out var fnName, out var bodyStart))
            {
                var bodyEnd = FindMatchingCloseParen(_text, bodyStart);
                if (bodyEnd < 0)
                    throw new CalcParseException($"unterminated {fnName}() inside expression");
                var body = _text[bodyStart..bodyEnd];
                _pos = bodyEnd + 1; // advance past the close paren
                var nested = ReduceFunctionInternal(fnName, body);
                if (nested is null) throw new CalcDeferredException();
                return nested.Value;
            }
            // Bare ident that's not a recognized function — defer (could be a custom
            // function like env() / attr() that v1 doesn't evaluate).
            if (IsLetter(c)) throw new CalcDeferredException();
            return ParseNumberOrDimension();
        }

        private CalcValue ParseNumberOrDimension()
        {
            // Parse signed number with optional decimal + exponent.
            var start = _pos;
            if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
            bool sawDigit = false;
            while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') { _pos++; sawDigit = true; }
            if (_pos < _text.Length && _text[_pos] == '.')
            {
                _pos++;
                while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') { _pos++; sawDigit = true; }
            }
            // Exponent only consumed when followed by a digit (optionally signed) — so
            // `2em` is parsed as 2 + unit "em", not as "2e" with empty exponent.
            if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                var lookahead = _pos + 1;
                if (lookahead < _text.Length
                    && (_text[lookahead] == '+' || _text[lookahead] == '-'))
                    lookahead++;
                if (lookahead < _text.Length
                    && _text[lookahead] >= '0' && _text[lookahead] <= '9')
                {
                    _pos++;
                    if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                    while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') _pos++;
                }
            }
            if (!sawDigit) throw new CalcParseException("expected number");
            var numText = _text[start.._pos];
            if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                throw new CalcParseException($"could not parse number '{numText}'");
            // Read unit suffix (letters / %).
            var unitStart = _pos;
            while (_pos < _text.Length && (IsLetter(_text[_pos]) || _text[_pos] == '%')) _pos++;
            var unitText = _text[unitStart.._pos];
            if (unitText.Length == 0) return new CalcValue(n, CalcUnit.Number);
            return UnitFromText(n, unitText);
        }

        private static CalcValue UnitFromText(double n, string unit)
        {
            switch (unit.ToLowerInvariant())
            {
                case "%": return new CalcValue(n, CalcUnit.Percent);
                case "px": return new CalcValue(n, CalcUnit.Px);
                case "in": return new CalcValue(n * 96, CalcUnit.Px);
                case "cm": return new CalcValue(n * 96 / 2.54, CalcUnit.Px);
                case "mm": return new CalcValue(n * 96 / 25.4, CalcUnit.Px);
                case "q": return new CalcValue(n * 96 / 25.4 / 4, CalcUnit.Px); // quarter-mm
                case "pt": return new CalcValue(n * 96 / 72, CalcUnit.Px);
                case "pc": return new CalcValue(n * 96 / 6, CalcUnit.Px);
                case "deg": return new CalcValue(n, CalcUnit.Deg);
                case "rad": return new CalcValue(n * 180 / Math.PI, CalcUnit.Deg);
                case "grad": return new CalcValue(n * 0.9, CalcUnit.Deg);
                case "turn": return new CalcValue(n * 360, CalcUnit.Deg);
                case "ms": return new CalcValue(n, CalcUnit.Ms);
                case "s": return new CalcValue(n * 1000, CalcUnit.Ms);
                case "hz": return new CalcValue(n, CalcUnit.Hz);
                case "khz": return new CalcValue(n * 1000, CalcUnit.Hz);
                case "dppx": case "x": return new CalcValue(n, CalcUnit.Dppx);
                case "dpi": return new CalcValue(n / 96, CalcUnit.Dppx);
                case "dpcm": return new CalcValue(n / 96 * 2.54, CalcUnit.Dppx);
                // Font-relative units (em, rem, ch, ex, ic, cap, lh, rlh): per CSS Values
                // L4 §6.1 these resolve against the element's (or root's) font metrics.
                // The cascade stage doesn't know font-size yet — that's Tasks 10+'s typed-
                // value pipeline once font matching has run. Defer so the original
                // function text is preserved for the resolver to revisit later.
                case "em": case "rem":
                case "ch": case "ex":
                case "ic": case "cap":
                case "lh": case "rlh":
                    throw new CalcDeferredException();
                // Viewport-relative units defer to layout (Phase 3) since the viewport
                // size depends on the page-box context.
                case "vw": case "vh": case "vmin": case "vmax":
                case "svw": case "svh": case "svmin": case "svmax":
                case "lvw": case "lvh": case "lvmin": case "lvmax":
                case "dvw": case "dvh": case "dvmin": case "dvmax":
                case "vi": case "vb":
                case "svi": case "svb":
                case "lvi": case "lvb":
                case "dvi": case "dvb":
                    throw new CalcDeferredException();
                // Container-relative units (cqw, cqh, cqi, cqb, cqmin, cqmax) are
                // post-v1 (Roadmap v1.4) per the @container gap; defer.
                case "cqw": case "cqh": case "cqi": case "cqb":
                case "cqmin": case "cqmax":
                    throw new CalcDeferredException();
                default: throw new CalcParseException($"unknown unit '{unit}'");
            }
        }

        private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    private static CalcValue Add(CalcValue a, CalcValue b)
    {
        if (a.Unit == CalcUnit.Number && b.Unit == CalcUnit.Number)
            return new CalcValue(a.Magnitude + b.Magnitude, CalcUnit.Number);
        if (a.Unit != b.Unit) throw new CalcDeferredException();
        return new CalcValue(a.Magnitude + b.Magnitude, a.Unit);
    }

    private static CalcValue Subtract(CalcValue a, CalcValue b)
    {
        if (a.Unit == CalcUnit.Number && b.Unit == CalcUnit.Number)
            return new CalcValue(a.Magnitude - b.Magnitude, CalcUnit.Number);
        if (a.Unit != b.Unit) throw new CalcDeferredException();
        return new CalcValue(a.Magnitude - b.Magnitude, a.Unit);
    }

    private static CalcValue Multiply(CalcValue a, CalcValue b)
    {
        // Per L4 §10.4: multiplication requires AT LEAST ONE operand to be a number.
        if (a.Unit == CalcUnit.Number && b.Unit == CalcUnit.Number)
            return new CalcValue(a.Magnitude * b.Magnitude, CalcUnit.Number);
        if (a.Unit == CalcUnit.Number)
            return new CalcValue(a.Magnitude * b.Magnitude, b.Unit);
        if (b.Unit == CalcUnit.Number)
            return new CalcValue(a.Magnitude * b.Magnitude, a.Unit);
        throw new CalcParseException("cannot multiply two dimensioned values");
    }

    private static CalcValue Divide(CalcValue a, CalcValue b)
    {
        if (b.Magnitude == 0) throw new CalcDivByZeroException();
        // Per CSS Values L4 §10.4: division either produces a number-on-the-right
        // result (length / number = length, etc.) OR cancels matching dimension
        // classes to produce a unitless number (length / length = number, etc.).
        // v1 supports those two cases. Cross-class division (length / time, etc.)
        // throws — that produces a "ratio" type the spec allows but our typed-value
        // pipeline doesn't yet have a slot for.
        if (b.Unit == CalcUnit.Number)
            return new CalcValue(a.Magnitude / b.Magnitude, a.Unit);
        if (a.Unit == b.Unit)
            return new CalcValue(a.Magnitude / b.Magnitude, CalcUnit.Number);
        throw new CalcParseException("cannot divide values of incompatible types");
    }

    /// <summary>Thrown when a math expression is syntactically invalid.</summary>
    private sealed class CalcParseException : Exception
    {
        public CalcParseException(string message) : base(message) { }
    }

    /// <summary>Thrown when a math expression is well-formed but contains a unit
    /// combination v1 cannot reduce (mixed % + length, viewport-relative without context).
    /// The caller catches this and preserves the original function text verbatim so
    /// layout (Phase 3) can finalize.</summary>
    private sealed class CalcDeferredException : Exception { }

    /// <summary>Thrown on division by zero; caller emits
    /// <see cref="CssDiagnosticCodes.CssCalcDivByZero001"/>.</summary>
    private sealed class CalcDivByZeroException : Exception { }
}

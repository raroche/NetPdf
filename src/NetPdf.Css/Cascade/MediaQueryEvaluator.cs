// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Evaluator for the subset of CSS Media Queries L4 that real-world documents use against
/// our v1 print/screen contexts. Handles the comma-separated query list, <c>not</c> /
/// <c>only</c> prefixes, type matching, and <c>and</c>-combined feature queries on the
/// common features (<c>min-width</c> / <c>max-width</c> / <c>min-height</c> /
/// <c>max-height</c> / <c>orientation</c> / <c>prefers-color-scheme</c> /
/// <c>resolution</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Soundness for unknown features.</b> A query referencing an unrecognized feature
/// (e.g., <c>(hover: hover)</c> in v1) evaluates to <c>false</c> for that branch — the
/// conservative choice per the recommendation, ensuring rules guarded by unsupported
/// features don't silently apply. The cascade emits no diagnostic per-occurrence (these
/// are common in real CSS); the omission is a documented v1 limitation. Comma-separated
/// alternatives let an unknown branch coexist with a known branch — the query as a whole
/// matches if any alternative does.
/// </para>
/// <para>
/// <b>Boolean shortcut.</b> A bare <c>(feature)</c> form (no value) is "true if the feature
/// has a non-zero / non-none value in the context". For the print context, <c>(color)</c>
/// would conventionally be true (color printer assumed); <c>(hover)</c> is false (no
/// pointer device). v1 hard-codes a small set of known-true / known-false features.
/// </para>
/// </remarks>
internal static class MediaQueryEvaluator
{
    /// <summary>Evaluate a comma-separated <see cref="CssMediaContext.Matches"/>-style
    /// query against <paramref name="ctx"/>. Returns <see langword="true"/> if any
    /// alternative matches.</summary>
    public static bool Evaluate(string? query, CssMediaContext ctx)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var raw in query.Split(','))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            try
            {
                if (EvaluateOne(trimmed, ctx)) return true;
            }
            catch (FormatException)
            {
                // A malformed alternative is silently skipped; a sibling alternative may
                // still match. Mirrors browser behavior — an unknown alternative shouldn't
                // poison the whole query.
            }
        }
        return false;
    }

    private static bool EvaluateOne(string query, CssMediaContext ctx)
    {
        var p = new MqParser(query);
        bool negate = false;
        p.SkipWhitespace();
        if (p.TryReadKeyword("not")) negate = true;
        else p.TryReadKeyword("only"); // CSS3 prefix — no-op in v1.
        p.SkipWhitespace();

        // Optional media type (print/screen/all/etc.). If the query starts with `(`, the
        // type is implicit "all".
        bool typeMatched = true;
        if (!p.IsEnd && p.Peek() != '(')
        {
            var typeToken = p.ReadIdentifierLike();
            typeMatched = string.Equals(typeToken, "all", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(typeToken, ctx.MediaType, StringComparison.OrdinalIgnoreCase);
            p.SkipWhitespace();
        }

        // Zero or more `and (feature ...)` clauses.
        bool featuresMatched = true;
        while (!p.IsEnd && featuresMatched)
        {
            if (p.TryReadKeyword("and"))
            {
                p.SkipWhitespace();
                if (p.IsEnd || p.Peek() != '(')
                    throw new FormatException("expected '(' after 'and'");
                featuresMatched &= EvaluateFeatureBlock(ref p, ctx);
                p.SkipWhitespace();
            }
            else if (p.Peek() == '(')
            {
                // Bare `(feature: ...)` (no leading type) — only legal at the start.
                featuresMatched &= EvaluateFeatureBlock(ref p, ctx);
                p.SkipWhitespace();
            }
            else
            {
                throw new FormatException($"unexpected token in media query: '{p.Peek()}'");
            }
        }

        var result = typeMatched && featuresMatched;
        return negate ? !result : result;
    }

    private static bool EvaluateFeatureBlock(ref MqParser p, CssMediaContext ctx)
    {
        if (p.IsEnd || p.Peek() != '(') throw new FormatException("expected '('");
        p.Advance();
        p.SkipWhitespace();
        var name = p.ReadIdentifierLike();
        if (string.IsNullOrEmpty(name)) throw new FormatException("expected feature name");
        p.SkipWhitespace();
        if (p.IsEnd) throw new FormatException("unterminated feature query");
        bool result;
        if (p.Peek() == ':')
        {
            p.Advance();
            p.SkipWhitespace();
            var value = p.ReadValueUntilCloseParen();
            result = EvaluateFeatureValue(name, value, ctx);
        }
        else
        {
            result = EvaluateFeatureBoolean(name, ctx);
        }
        p.SkipWhitespace();
        if (p.IsEnd || p.Peek() != ')') throw new FormatException("expected ')'");
        p.Advance();
        return result;
    }

    private static bool EvaluateFeatureValue(string feature, string value, CssMediaContext ctx)
    {
        feature = feature.ToLowerInvariant();
        value = value.Trim();
        return feature switch
        {
            "min-width" => ParseLengthPx(value, out var px) && ctx.ViewportWidthPx >= px,
            "max-width" => ParseLengthPx(value, out var px) && ctx.ViewportWidthPx <= px,
            "width" => ParseLengthPx(value, out var px) && Math.Abs(ctx.ViewportWidthPx - px) < 0.5,
            "min-height" => ParseLengthPx(value, out var px) && ctx.ViewportHeightPx >= px,
            "max-height" => ParseLengthPx(value, out var px) && ctx.ViewportHeightPx <= px,
            "height" => ParseLengthPx(value, out var px) && Math.Abs(ctx.ViewportHeightPx - px) < 0.5,
            "orientation" => string.Equals(value, GetOrientation(ctx), StringComparison.OrdinalIgnoreCase),
            "prefers-color-scheme" =>
                string.Equals(value, ctx.PreferredColorScheme, StringComparison.OrdinalIgnoreCase),
            "min-resolution" => ParseResolutionDppx(value, out var dppx) && ctx.DevicePixelRatio >= dppx,
            "max-resolution" => ParseResolutionDppx(value, out var dppx) && ctx.DevicePixelRatio <= dppx,
            "resolution" => ParseResolutionDppx(value, out var dppx) && Math.Abs(ctx.DevicePixelRatio - dppx) < 0.001,
            _ => false, // unknown feature — conservative false
        };
    }

    private static bool EvaluateFeatureBoolean(string feature, CssMediaContext ctx)
    {
        feature = feature.ToLowerInvariant();
        return feature switch
        {
            "color" => true,         // color printer / display assumed
            "monochrome" => false,
            "orientation" => true,   // boolean form: "feature is supported" — yes
            "width" or "height" => true,
            "prefers-color-scheme" => true,
            _ => false,
        };
    }

    private static string GetOrientation(CssMediaContext ctx) =>
        ctx.ViewportWidthPx > ctx.ViewportHeightPx ? "landscape" : "portrait";

    /// <summary>Parse a length value into CSS px. Supports plain numbers (interpreted as
    /// px) and explicit <c>px</c> unit. Other units (em / rem / vw / vh / cm / mm / in /
    /// pt / pc) accepted by converting against a 96 DPI ÷ 16 px-per-em assumption — good
    /// enough for media-query gating; precise units land with the typed value tree.</summary>
    private static bool ParseLengthPx(string s, out double px)
    {
        px = 0;
        if (string.IsNullOrEmpty(s)) return false;
        // Find unit suffix.
        int unitStart = -1;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E'))
            {
                unitStart = i;
                break;
            }
        }
        var numText = unitStart < 0 ? s : s[..unitStart];
        var unit = unitStart < 0 ? "" : s[unitStart..].ToLowerInvariant();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return false;
        px = unit switch
        {
            "" or "px" => n,
            "em" or "rem" => n * 16,
            "in" => n * 96,
            "cm" => n * 96 / 2.54,
            "mm" => n * 96 / 25.4,
            "pt" => n * 96 / 72,
            "pc" => n * 96 / 6,
            // Viewport-relative — caller-side context; v1 uses default print viewport.
            "vw" or "vh" or "vmin" or "vmax" => n * 8.16, // 1% of 816px default — rough
            _ => double.NaN,
        };
        return !double.IsNaN(px);
    }

    private static bool ParseResolutionDppx(string s, out double dppx)
    {
        dppx = 0;
        if (string.IsNullOrEmpty(s)) return false;
        int unitStart = -1;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E'))
            {
                unitStart = i;
                break;
            }
        }
        var numText = unitStart < 0 ? s : s[..unitStart];
        var unit = unitStart < 0 ? "" : s[unitStart..].ToLowerInvariant();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return false;
        dppx = unit switch
        {
            "dppx" => n,
            "x" => n,
            "dpi" => n / 96,
            "dpcm" => n / 96 * 2.54,
            _ => double.NaN,
        };
        return !double.IsNaN(dppx);
    }

    private ref struct MqParser
    {
        private readonly string _text;
        private int _pos;

        public MqParser(string text) { _text = text; _pos = 0; }
        public readonly bool IsEnd => _pos >= _text.Length;
        public readonly char Peek() => _text[_pos];
        public void Advance() => _pos++;

        public void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        public bool TryReadKeyword(string keyword)
        {
            if (_pos + keyword.Length > _text.Length) return false;
            for (var i = 0; i < keyword.Length; i++)
            {
                if (char.ToLowerInvariant(_text[_pos + i]) != keyword[i]) return false;
            }
            var next = _pos + keyword.Length;
            if (next < _text.Length)
            {
                var c = _text[next];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') return false;
            }
            _pos += keyword.Length;
            return true;
        }

        public string ReadIdentifierLike()
        {
            var start = _pos;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') _pos++;
                else break;
            }
            return _text[start.._pos];
        }

        public string ReadValueUntilCloseParen()
        {
            var start = _pos;
            int depth = 0;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    if (depth == 0) break;
                    depth--;
                }
                _pos++;
            }
            return _text[start.._pos].Trim();
        }
    }
}

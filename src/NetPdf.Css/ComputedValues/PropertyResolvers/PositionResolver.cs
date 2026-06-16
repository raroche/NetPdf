// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Validates the <c>&lt;position&gt;</c> value type (CSS Backgrounds &amp; Borders 3 §3.6 /
/// CSS Values 4 §3.4) — used by <c>object-position</c> (backlog #6). A position is 1–4
/// components, each a position KEYWORD (<c>left</c> / <c>center</c> / <c>right</c> /
/// <c>top</c> / <c>bottom</c>) or a <c>&lt;length-percentage&gt;</c> — including a math
/// function (<c>calc()</c> / <c>min()</c> / <c>max()</c> / <c>clamp()</c> / …), which a
/// PAREN-AWARE tokenizer keeps as one component (post-PR-#183 review P3). The edge-offset
/// forms like <c>left 10px top 5px</c> tokenize as keyword + length pairs, so they pass.
///
/// <para><b>Why this is registration-for-validation, not a computed value.</b> The image
/// painter consumes <c>object-position</c> from the RAW cascade winner at paint time (the
/// <c>ImgSpec</c> seam), so a VALID position returns <see cref="ResolverResult.Deferred"/>
/// carrying the raw text — the registration makes <c>@supports (object-position: …)</c>
/// answer correctly + surfaces an INVALID value as <c>CSS-PROPERTY-VALUE-INVALID-001</c>,
/// while the typed computed slot is intentionally not consumed (the painter reads the raw
/// winner, which is independent of typed resolution, so this can't regress rendering).</para>
///
/// <para><b>First-cut validation</b> (lenient by design — a stricter check that rejected a
/// valid form would only mis-report <c>@supports</c>, never break rendering): every
/// component must be a position keyword or a length-percentage; 1–4 components. The
/// component-ORDER + axis-conflict rules (CSS B&amp;B §3.6 — e.g. <c>top bottom</c> naming
/// the same axis twice) are not enforced.</para>
/// </summary>
internal static class PositionResolver
{
    public static ResolverResult Resolve(
        string value, PropertyId propertyId, string propertyName,
        ICssDiagnosticsSink? diagnostics, CssSourceLocation location)
    {
        // Paren-aware tokenization (post-PR-#183 review P3): split on whitespace at paren depth 0 so a
        // FUNCTIONAL component — a math function like `calc(50% - 10px)` — stays a SINGLE token instead
        // of fragmenting into broken pieces (`calc(50%`, `-`, `10px)`) that a plain whitespace split
        // produced, which wrongly mis-reported `@supports (object-position: calc(50% - 10px) top)` as
        // unsupported. Unbalanced parentheses → invalid.
        if (!CssShorthandHelpers.SplitTopLevel(value, out var tokens))
        {
            EmitInvalid(diagnostics, propertyName, value, "unbalanced parentheses", location);
            return ResolverResult.Invalid();
        }
        if (tokens.Count is < 1 or > 4)
        {
            EmitInvalid(diagnostics, propertyName, value,
                tokens.Count == 0 ? "empty value" : "a <position> has 1 to 4 components", location);
            return ResolverResult.Invalid();
        }
        foreach (var token in tokens)
        {
            if (IsPositionKeyword(token)) continue;
            // A math-function component (calc()/min()/max()/clamp()/…) is a valid <length-percentage>:
            // validate the EXPRESSION is well-formed + length-typed against a FINITE probe context (the
            // containing-block percentage base isn't known at cascade time, but a finite base proves the
            // expression itself is valid — a position offset can be negative, so don't clamp).
            if (CalcLengthEvaluator.IsMathFunction(token))
            {
                if (CalcLengthEvaluator.TryEvaluate(token, MathProbeContext, clampNonNegative: false, out _))
                    continue;
                EmitInvalid(diagnostics, propertyName, value,
                    $"'{DiagnosticTextSanitizer.Sanitize(token)}' is not a valid math function for a <position>",
                    location);
                return ResolverResult.Invalid();
            }
            // A plain <length-percentage> component — probe the shared length parser with a
            // NULL diagnostics sink (this is a validity probe, not the real emit).
            var probe = LengthResolver.Resolve(
                token, PropertyType.LengthPercentage, propertyId, propertyName,
                diagnostics: null, location);
            if (probe.State is ResolutionState.Resolved or ResolutionState.Deferred) continue;
            EmitInvalid(diagnostics, propertyName, value,
                $"'{DiagnosticTextSanitizer.Sanitize(token)}' is not a position keyword or a <length-percentage>",
                location);
            return ResolverResult.Invalid();
        }
        // A valid <position> — resolved from the raw winner at paint time.
        return ResolverResult.Deferred(value);
    }

    /// <summary>A finite probe context for VALIDATING a math-function component (post-PR-#183 review
    /// P3): the actual percentage base / font / viewport aren't known when <c>@supports</c> evaluates,
    /// but any finite set proves the expression is well-formed + length-typed (the only question
    /// validation answers). The painter re-evaluates with the real §3.6 range at paint time.</summary>
    private static readonly CalcLengthEvaluator.CalcContext MathProbeContext = new(
        PercentBasePx: 100.0, EmPx: 16.0, RootEmPx: 16.0, ViewportWidthPx: 800.0, ViewportHeightPx: 600.0);

    private static bool IsPositionKeyword(string token) =>
        token.Equals("left", StringComparison.OrdinalIgnoreCase)
        || token.Equals("center", StringComparison.OrdinalIgnoreCase)
        || token.Equals("right", StringComparison.OrdinalIgnoreCase)
        || token.Equals("top", StringComparison.OrdinalIgnoreCase)
        || token.Equals("bottom", StringComparison.OrdinalIgnoreCase);

    private static void EmitInvalid(
        ICssDiagnosticsSink? sink, string propertyName, string value, string reason,
        CssSourceLocation location)
    {
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        sink?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"Could not parse '{propertyName}: {safeValue}' — {reason}.",
            CssDiagnosticSeverity.Warning,
            location));
    }
}

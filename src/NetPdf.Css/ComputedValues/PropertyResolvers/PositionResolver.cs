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
/// <para><b>Validation.</b> Every component must be a position keyword or a length-percentage
/// (1–4 components), AND the component list must satisfy the CSS B&amp;B §3.6 grammar
/// (axis-conflict cycle): the 2-value form forbids naming the same axis twice (<c>top bottom</c>,
/// <c>left right</c>) and — when a length-percentage is involved — fixes the X-then-Y order
/// (<c>20px left</c> is invalid); the 3-/4-value edge-offset form must cover both axes with no
/// same-axis pair or leftover token, and <c>center</c> takes no offset.</para>
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
        // Classify each component (a position keyword, or a <length-percentage> / math function) AND
        // validate it. The kinds drive the §3.6 component-ORDER + axis-conflict check below.
        var kinds = new Comp[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (TryClassifyKeyword(token, out var kw)) { kinds[i] = kw; continue; }
            // A math-function component (calc()/min()/max()/clamp()/…) is a valid <length-percentage>:
            // validate the EXPRESSION is well-formed + length-typed against a FINITE probe context (the
            // containing-block percentage base isn't known at cascade time, but a finite base proves the
            // expression itself is valid — a position offset can be negative, so don't clamp).
            if (CalcLengthEvaluator.IsMathFunction(token))
            {
                if (CalcLengthEvaluator.TryEvaluate(token, MathProbeContext, clampNonNegative: false, out _))
                {
                    kinds[i] = Comp.LengthPct;
                    continue;
                }
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
            if (probe.State is ResolutionState.Resolved or ResolutionState.Deferred)
            {
                kinds[i] = Comp.LengthPct;
                continue;
            }
            EmitInvalid(diagnostics, propertyName, value,
                $"'{DiagnosticTextSanitizer.Sanitize(token)}' is not a position keyword or a <length-percentage>",
                location);
            return ResolverResult.Invalid();
        }
        // CSS B&B §3.6 component-ORDER + AXIS-CONFLICT rules (axis-conflict cycle): e.g. `top bottom`
        // names the Y axis twice, `20px left` puts an X keyword in the Y slot, and the 3-/4-value
        // edge-offset form must cover both axes with no same-axis pair / leftover. A per-token-valid but
        // grammatically-invalid position is rejected here (was leniently accepted).
        if (!IsValidPositionGrammar(kinds))
        {
            EmitInvalid(diagnostics, propertyName, value,
                "the components name the same axis twice or are out of order (CSS B&B §3.6)", location);
            return ResolverResult.Invalid();
        }
        // A valid <position> — resolved from the raw winner at paint time.
        return ResolverResult.Deferred(value);
    }

    /// <summary>A classified <c>&lt;position&gt;</c> component: a specific edge keyword, <c>center</c>, or
    /// a <c>&lt;length-percentage&gt;</c> (incl. a math function).</summary>
    private enum Comp { Left, Right, Top, Bottom, Center, LengthPct }

    /// <summary>The axis a classified component constrains: <c>'x'</c> (left/right), <c>'y'</c>
    /// (top/bottom), or <c>'c'</c> (center / a length-percentage — either axis).</summary>
    private static char Axis(Comp c) => c switch
    {
        Comp.Left or Comp.Right => 'x',
        Comp.Top or Comp.Bottom => 'y',
        _ => 'c',   // center or length-percentage — floats to the free axis
    };

    /// <summary>Validate the CSS B&amp;B §3.6 <c>&lt;position&gt;</c> component grammar (axis-conflict
    /// cycle) for an already-per-token-validated component list. ONE component is always fine; TWO follow
    /// the 2-value branch (both keywords → either order, but not the SAME axis twice; otherwise the strict
    /// X-then-Y order — a length-percentage can't reorder a keyword); THREE/FOUR are the edge-offset
    /// <c>&amp;&amp;</c> form (each component an edge keyword optionally followed by an offset, both axes
    /// covered, in either order, no same-axis pair, no leftover, <c>center</c> takes no offset).</summary>
    private static bool IsValidPositionGrammar(Comp[] k) => k.Length switch
    {
        1 => true,                          // any single keyword or length-percentage
        2 => IsValidTwoValue(k[0], k[1]),
        _ => IsValidEdgeOffset(k),          // 3 or 4 components
    };

    private static bool IsValidTwoValue(Comp a, Comp b)
    {
        var aKw = a != Comp.LengthPct;
        var bKw = b != Comp.LengthPct;
        if (aKw && bKw)
        {
            // Both keywords — accepted in EITHER order (the §3.6 `&&` keyword form), but they must not
            // name the SAME axis twice (`left right`, `top bottom`). A `center` floats, so it never
            // conflicts.
            var ax = Axis(a);
            var bx = Axis(b);
            return !(ax == 'x' && bx == 'x') && !(ax == 'y' && bx == 'y');
        }
        // At least one length-percentage — the strict 2-value branch: component 1 is the X axis
        // ({left|center|right|<lp>}), component 2 the Y axis ({top|center|bottom|<lp>}). So a Y keyword
        // can't lead, and an X keyword can't trail (a length-percentage can't reorder it the way two
        // keywords can).
        return a is not (Comp.Top or Comp.Bottom) && b is not (Comp.Left or Comp.Right);
    }

    private static bool IsValidEdgeOffset(Comp[] k)
    {
        // Parse exactly TWO (edge, offset?) components consuming EVERY component. Each edge is an edge
        // keyword (left/right/top/bottom) or center; an offset is a length-percentage following its edge.
        var i = 0;
        var e0 = k[i++];
        if (e0 == Comp.LengthPct) return false;                       // must start with an edge / center
        var o0 = i < k.Length && k[i] == Comp.LengthPct ? k[i++] : (Comp?)null;
        if (i >= k.Length) return false;                             // need a second component
        var e1 = k[i++];
        if (e1 == Comp.LengthPct) return false;
        var o1 = i < k.Length && k[i] == Comp.LengthPct ? k[i++] : (Comp?)null;
        if (i != k.Length) return false;                            // a leftover token → invalid
        // `center` takes no offset — on EITHER component (post-PR-#184 Copilot: `left 10px center 5px`
        // and `center center 10px` must reject, not just an offset after a LEADING center).
        if (e0 == Comp.Center && o0 is not null) return false;
        if (e1 == Comp.Center && o1 is not null) return false;
        // Resolve which component is X vs Y; two same-axis edges → invalid (a `center` floats).
        var a0 = Axis(e0);
        var a1 = Axis(e1);
        return !(a0 == 'x' && a1 == 'x') && !(a0 == 'y' && a1 == 'y');
    }

    /// <summary>A finite probe context for VALIDATING a math-function component (post-PR-#183 review
    /// P3): the actual percentage base / font / viewport aren't known when <c>@supports</c> evaluates,
    /// but any finite set proves the expression is well-formed + length-typed (the only question
    /// validation answers). The painter re-evaluates with the real §3.6 range at paint time.</summary>
    private static readonly CalcLengthEvaluator.CalcContext MathProbeContext = new(
        PercentBasePx: 100.0, EmPx: 16.0, RootEmPx: 16.0, ViewportWidthPx: 800.0, ViewportHeightPx: 600.0);

    /// <summary>Classify a position KEYWORD token (case-insensitive), or return <see langword="false"/>
    /// when it isn't one (the caller then tries the <c>&lt;length-percentage&gt;</c> path).</summary>
    private static bool TryClassifyKeyword(string token, out Comp kind)
    {
        if (token.Equals("left", StringComparison.OrdinalIgnoreCase)) { kind = Comp.Left; return true; }
        if (token.Equals("right", StringComparison.OrdinalIgnoreCase)) { kind = Comp.Right; return true; }
        if (token.Equals("top", StringComparison.OrdinalIgnoreCase)) { kind = Comp.Top; return true; }
        if (token.Equals("bottom", StringComparison.OrdinalIgnoreCase)) { kind = Comp.Bottom; return true; }
        if (token.Equals("center", StringComparison.OrdinalIgnoreCase)) { kind = Comp.Center; return true; }
        kind = Comp.LengthPct;
        return false;
    }

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

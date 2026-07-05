// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Single entry point for converting one property's resolved value text (post-<c>var()</c>
/// substitution + post-<c>calc()</c> reduction) into a <see cref="ResolverResult"/>.
/// Dispatches to a per-<see cref="PropertyType"/> resolver. The cycle-1 surface covers
/// the four highest-impact families: length / color / number / keyword.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline position.</b> Runs after <c>VarResolver</c> (Task 8) +
/// <c>CalcResolver</c> (Task 9). Output is a structured <see cref="ResolverResult"/>
/// — see its docs for the three-way Resolved / Deferred / Invalid contract.
/// </para>
/// <para>
/// <b>Cycle 2 unsupported PropertyTypes.</b> Property types not yet wired return
/// <see cref="ResolverResult.UnsupportedUnvalidated"/> (carrying the raw text for
/// re-resolution), NOT <see cref="ResolverResult.Deferred"/> and NOT
/// <see cref="ResolverResult.Invalid"/>. The 4-state distinction matters per the
/// Task 10 hardening review: <see cref="ResolutionState.Deferred"/> means "the
/// resolver validated the value against the property's grammar but cannot reduce
/// without context"; <see cref="ResolutionState.UnsupportedUnvalidated"/> means
/// "no resolver wired yet — raw text passed through unchecked, cycle-2 work
/// surface". Treating them identically would silently let typos through for
/// cycle-2 PropertyTypes. Still unwired: <c>Content</c>, <c>Url</c>, <c>String</c>,
/// <c>Time</c>, <c>Angle</c>, <c>Resolution</c>. (<c>LineHeight</c> + <c>VerticalAlign</c>
/// are now wired — <see cref="LineHeightResolver"/> / <see cref="VerticalAlignResolver"/>.)
/// </para>
/// </remarks>
internal static class PropertyResolverDispatch
{
    /// <summary>Resolve <paramref name="resolvedValue"/> into a typed result for
    /// the property identified by <paramref name="propertyId"/>.</summary>
    /// <param name="propertyId">The property whose value is being resolved — drives
    /// the dispatch via its <see cref="PropertyMeta.Type"/>.</param>
    /// <param name="resolvedValue">The post-<c>var()</c>, post-<c>calc()</c> value
    /// text. Whitespace at the edges is tolerated.</param>
    /// <param name="diagnostics">Sink for parse failures. <see langword="null"/> is
    /// allowed — failures are still observable through the returned
    /// <see cref="ResolverResult.State"/>.</param>
    /// <param name="location">Source location attached to any emitted diagnostic.</param>
    public static ResolverResult Resolve(
        PropertyId propertyId,
        string resolvedValue,
        ICssDiagnosticsSink? diagnostics = null,
        CssSourceLocation location = default)
    {
        ArgumentNullException.ThrowIfNull(resolvedValue);

        var idx = (int)propertyId;
        if (idx < 0 || idx >= PropertyMetadata.Count)
        {
            // Unknown PropertyId — nothing to do. Treat as Invalid (the cascade
            // shouldn't have given us this in the first place).
            return ResolverResult.Invalid();
        }

        var meta = PropertyMetadata.Table[idx];
        var trimmed = resolvedValue.AsSpan().Trim().ToString();
        if (trimmed.Length == 0)
        {
            // Empty value text → Invalid (CSS treats empty declaration values as
            // parse errors that the cascade discards).
            return ResolverResult.Invalid();
        }

        // outline-color: auto (CSS UI 4 §5.3 — post-PR-#173 review P2). CSS UI 4 RETIRED `invert` and
        // makes `auto` the initial, computing to a UA-chosen colour. We approximate it as currentcolor
        // (the same slot the explicit keyword resolves to), since `auto` isn't a <color> ColorResolver
        // would accept. Only outline-color admits `auto` — border-color: auto stays invalid.
        if (propertyId == PropertyId.OutlineColor
            && trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return ResolverResult.Resolved(ComputedSlot.CurrentColor);

        // CSS-wide keywords (CSS Cascade L5 §7.x) are valid on EVERY property and are the CASCADE's job,
        // not a per-property grammar — handle them HERE, once, correctly, before the per-type dispatch (they
        // still reach here via shorthand expansion, e.g. `background` → `background-attachment: initial`, and
        // reset stylesheets, e.g. `line-height: inherit`):
        //   * `initial` → the property's INITIAL value (resolve its DefaultValue). Crucially this RESETS an
        //     inherited property to its initial value rather than leaving the parent's inherited value — so
        //     `line-height: initial` under `line-height: 3` computes to `normal`, not `3`.
        //   * `inherit` / `unset` / `revert` / `revert-layer` → fall through as "declaration ignored"
        //     (Invalid). The cascade materializes that as the INHERITED value for an inherited property and
        //     the INITIAL value for a non-inherited one — exactly the spec meaning of `unset`/`revert`, and
        //     of `inherit` on an inherited property (the overwhelmingly common cases).
        // No CssPropertyValueInvalid001 diagnostic is emitted — a CSS-wide keyword is valid, not an error.
        if (CssWideKeyword.Is(trimmed))
        {
            return trimmed.Equals("initial", StringComparison.OrdinalIgnoreCase)
                ? Resolve(propertyId, meta.DefaultValue, diagnostics: null, location)   // DefaultValue is never CSS-wide → no recursion
                : ResolverResult.Invalid();
        }

        return meta.Type switch
        {
            PropertyType.Color => ColorResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // The dimension family — all seven share LengthResolver's parser, with
            // per-type acceptance rules (auto / none / normal / percentage) plus
            // per-PropertyId rules (letter-spacing rejects %, padding/width/etc.
            // reject negatives). Per Phase 3 Task 15 L8 the FlexBasis type
            // joined the family: it accepts `auto` (KeywordIdAuto = 0) /
            // `content` (KeywordIdContent = 1) / `<length-percentage>` per
            // CSS Flexbox L1 §7.2. Per Phase 3 Task 15 L12 the MaxSize type
            // joined the family (= the seventh member): it accepts `none`
            // (= no upper bound, KeywordIdNone = 0) / `<length-percentage>`
            // per CSS Sizing L3 §5.2 — used by `max-width` / `max-height`
            // for the §9.7 step-4 min/max clamping iteration in the
            // FlexLayouter.
            PropertyType.Length or
            PropertyType.LengthPercentage or
            PropertyType.LengthPercentageAuto or
            PropertyType.Percentage or
            PropertyType.TextSpacing or
            PropertyType.FlexBasis or
            PropertyType.MaxSize => LengthResolver.Resolve(
                trimmed, meta.Type, propertyId, meta.Name, diagnostics, location),

            PropertyType.Number => NumberResolver.ResolveNumber(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.Integer => NumberResolver.ResolveInteger(
                trimmed, propertyId, meta.Name, diagnostics, location),

            PropertyType.Keyword => KeywordResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Per Phase 3 Task 17 cycle 0b — the grid family. Parsed ASTs
            // (TrackList / GridLineValue) don't fit in an 8-byte
            // ComputedSlot, so the resolvers return ResolvedSideTable(payload);
            // ResolverResult.MaterializeInto stashes the payload in
            // ComputedStyle's side-table dictionary alongside the slot.
            // Defaults (none / auto) land as Keyword(0) slots WITHOUT a
            // side-table entry — the readers
            // (GridReaders.ReadGridTemplate{Rows,Columns} /
            //  ReadGridRow{Start,End} / ReadGridColumn{Start,End}) fall back
            // to TrackList.None / GridLineValue.Auto for that path.
            PropertyType.GridTemplateList => GridTemplateListResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.GridLine => GridLineResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.GridTemplateAreas => GridTemplateAreasResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Per Phase 5 layout→PDF cycle 3 — <line-width> (border-*-width,
            // column-rule-width): thin/medium/thick keywords + <length>, resolved
            // to a LengthPx slot. The used-value style gate (width 0 when
            // border-style is none/hidden) is applied downstream in layout/paint.
            PropertyType.LineWidth => LineWidthResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Per Phase 5 layout→PDF cycle 4 — the font-property family. font-size
            // resolves absolute forms here (keywords + lengths) + defers
            // parent-relative forms (em/%/larger/smaller) for the box-builder walk;
            // font-weight → integer (1..1000), bolder/lighter deferred; font-family
            // → a side-table FontFamilyList.
            PropertyType.FontSize => FontSizeResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.FontWeight => FontWeightResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.FontFamilyList => FontFamilyListResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),
            // Backlog #6 — validation-only registrations (@supports + invalid-value diagnostics);
            // both are consumed RAW downstream (object-position by the image painter, page by the
            // named-page machinery), so the typed slot is a Deferred raw-text carrier.
            PropertyType.Position => PositionResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),
            PropertyType.PageName => PageNameResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Inline-atomic vertical-align cycle (CSS 2.2 §10.8.1) — keywords (baseline / sub / super /
            // text-top / text-bottom / middle / top / bottom) → a Keyword slot; <length> / <percentage>
            // → a LengthPercentage slot (may be negative). Consumed by the inline-atomic placement.
            PropertyType.VerticalAlign => VerticalAlignResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // line-height cycle (CSS 2.2 §10.8.1 / CSS Inline 3) — `normal` → Keyword(0); a unitless
            // <number> → a Number slot (the multiplier, × the element's own font-size at use time);
            // <length>/<percentage> → LengthResolver (absolute → LengthPx, em/rem → Deferred raw the
            // box-builder folds, % → a Percentage slot). Pre-fix line-height was UNWIRED (every value →
            // UnsupportedUnvalidated), so a declared `line-height: 24px` never reached layout/paint.
            PropertyType.LineHeight => LineHeightResolver.Resolve(
                trimmed, propertyId, meta.Name, diagnostics, location),

            // Cycle-2 PropertyTypes — return UnsupportedUnvalidated (NOT Deferred)
            // because the dispatch hasn't validated the value text against the
            // property's grammar; a typo would silently pass through. Distinguishing
            // the two states lets cycle-2 audits find the work surface.
            _ => ResolverResult.UnsupportedUnvalidated(trimmed),
        };
    }
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;

namespace NetPdf.Rendering;

/// <summary>
/// Builds a <see cref="ComputedStyle"/> for a CSS Paged Media §6.4 margin box (or the page context)
/// from declared longhands + inherited values, and reads the alignment a declared <c>text-align</c>
/// / <c>vertical-align</c> implies. Phase 3 Task 21 cycle 4 (per-box style) + cycle 5 (inheritance)
/// — running headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inheritance (cycle 5).</b> The font + color style is built by INHERITING the supported
/// properties from a parent then cascading this level's own declarations on top. The CSS Page 3
/// chain is built in two calls: <c>pageContext = Build(@page declarations, rootElementStyle)</c>
/// then <c>marginBox = Build(box declarations, pageContext)</c> — so <c>@page { color: gray;
/// @top-center { … } }</c> tints the box and <c>html { font-family: … }</c> flows into headers/
/// footers. Own declarations cascade by importance then source order (a later normal can't override
/// an earlier <c>!important</c>) and override the inherited value; unspecified properties keep the
/// inherited one, or fall back to the reader defaults (16px / default family / 400 / black) when
/// nothing up the chain set them. CSS-wide keywords are handled at the cascade level:
/// <c>initial</c> resets to the property's initial value, <c>inherit</c>/<c>unset</c> keep the
/// inherited value, <c>revert</c>/<c>revert-layer</c> are approximated as inherited.
/// </para>
/// <para>
/// <b>Supported properties (a WHITELIST).</b> The inherited <c>font-family</c> / <c>font-size</c> /
/// <c>font-weight</c> / <c>font-style</c> / <c>color</c> are materialized + inherited. The
/// non-inherited <c>background-color</c> (cycle 8), the 12 <c>border-*-width</c> / <c>-style</c> /
/// <c>-color</c> longhands (border cycle), the 4 <c>padding-*</c> longhands (padding cycle),
/// <c>width</c> / <c>height</c> (explicit-size cycle), and <c>box-sizing</c> (box-sizing cycle) are
/// materialized from the box's OWN declarations — the painter fills a band behind the box's content,
/// strokes the border around its region, insets the text content origin by the used
/// border-width + padding on each side, and sizes the box along its §5.3 VARIABLE axis from an explicit
/// <c>width</c> (top/bottom) / <c>height</c> (left/right) — an absolute length or a percentage of the
/// band; <c>auto</c> shrink-to-fits (cycle 14); per CSS Basic UI 4 §10 the explicit size specifies the
/// content box (<c>box-sizing: content-box</c>, the initial) or the border box
/// (<c>box-sizing: border-box</c>). (A <i>non-absolute</i> padding — a percentage or a font-/
/// viewport-relative length — is accepted by the cascade but can't be resolved to used px here yet, so
/// it's diagnosed + dropped rather than silently zeroed; likewise a DEFERRED <c>width</c>/<c>height</c>
/// — a font-/viewport-relative or <c>calc()</c> size (a percentage IS supported) — is diagnosed +
/// dropped so the box EXPLICITLY shrink-to-fits rather than silently. The §5.3 margin-box sizing / font
/// context they would resolve against is deferred.) <c>text-align</c> /
/// <c>vertical-align</c> are NOT inherited
/// here — alignment is read from the box's OWN declarations (<see cref="HorizontalAlignFactor"/> /
/// <see cref="VerticalAlignFactor"/>) and overrides the box's name-derived default; inheriting the
/// page/root's UA-default <c>text-align: start</c> would otherwise spuriously override the name-derived
/// centering (post-PR-#134 review). The remaining box-model declarations (<c>border-radius</c>,
/// background <i>images</i>, …) stay deferred (deferrals.md).
/// </para>
/// <para>
/// <b>Relative font (cycle 7).</b> A parent-relative <c>font-size</c> (<c>em</c> / <c>ex</c> /
/// <c>ch</c> / <c>%</c> / <c>larger</c> / <c>smaller</c>) or <c>font-weight</c> (<c>bolder</c> /
/// <c>lighter</c>) that the dispatch leaves deferred is resolved against the inherited parent via
/// <see cref="DeferredFontResolver"/> after the cascade — so <c>@page { font-size: 20px;
/// @bottom-center { font-size: 1.5em } }</c> → 30px. (A non-parent-relative form — <c>rem</c> /
/// viewport / container units — stays deferred and falls back to the reader default, a documented gap.)
/// </para>
/// <para>
/// <b>Deferred (later cycles, deferrals.md#layout-to-pdf-pipeline).</b> <c>rem</c> / viewport-relative
/// font-size, page-context inheritance of alignment, precise <c>revert</c>, and — for the §5.3
/// three-box-per-edge sizing (shrink-to-fit + explicit <c>width</c>/<c>height</c> + the min/max-content
/// overlap distribution + <c>box-sizing</c> + line-granularity overflow clipping have shipped) —
/// unsupported relative/<c>calc()</c> <c>width</c>/<c>height</c> and partial-glyph clip paths.
/// </para>
/// </remarks>
internal static class MarginBoxStyle
{
    /// <summary>The INHERITED longhands, in a fixed order (deterministic materialization). All feed
    /// the shaper + text fill and are CSS inherited properties, so they flow root → page context →
    /// margin box. <c>text-align</c> / <c>vertical-align</c> are NOT here — alignment is read from the
    /// box's OWN declarations (<see cref="HorizontalAlignFactor"/> / <see cref="VerticalAlignFactor"/>)
    /// and overrides the box's name-derived default; it is NOT inherited from the page/root, whose
    /// (UA-default) <c>text-align: start</c> would otherwise spuriously override the name-derived
    /// centering (post-PR-#134 review).</summary>
    private static readonly ImmutableArray<PropertyId> SupportedStyleIds = ImmutableArray.Create(
        PropertyId.FontFamily, PropertyId.FontSize, PropertyId.FontWeight, PropertyId.FontStyle,
        PropertyId.Color);

    /// <summary>The inherited subset of <see cref="CascadedStyleIds"/> — drives the inheritance copy
    /// and the property-aware CSS-wide keyword handling (<c>unset</c>/<c>revert</c> behave as
    /// <c>inherit</c> for these, and as <c>initial</c> for the non-inherited <c>background-color</c>).</summary>
    private static readonly FrozenSet<PropertyId> InheritedStyleIdSet = SupportedStyleIds.ToFrozenSet();

    /// <summary>The longhands a margin box CASCADES from its OWN declarations: the inherited set plus
    /// the non-inherited <c>background-color</c> (cycle 8 — paints a band behind the box's content),
    /// the 12 <c>border-*-width</c> / <c>-style</c> / <c>-color</c> longhands (border cycle — painted
    /// around the box region), the 4 <c>padding-*</c> longhands (padding cycle — inset the box's
    /// content origin), <c>width</c> / <c>height</c> (explicit-size cycle — set the box's §5.3
    /// VARIABLE-axis size), and <c>box-sizing</c> (box-sizing cycle — whether that explicit size
    /// specifies the content box or the border box). These are materialized onto the style but
    /// deliberately left OUT of the inheritance copy above, since they are not CSS inherited
    /// properties.</summary>
    private static readonly ImmutableArray<PropertyId> CascadedStyleIds =
        SupportedStyleIds.AddRange(
            PropertyId.BackgroundColor,
            PropertyId.BorderTopWidth, PropertyId.BorderTopStyle, PropertyId.BorderTopColor,
            PropertyId.BorderRightWidth, PropertyId.BorderRightStyle, PropertyId.BorderRightColor,
            PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle, PropertyId.BorderBottomColor,
            PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle, PropertyId.BorderLeftColor,
            PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft,
            PropertyId.Width, PropertyId.Height, PropertyId.BoxSizing);

    private static readonly FrozenSet<PropertyId> CascadedStyleIdSet = CascadedStyleIds.ToFrozenSet();

    /// <summary>Build a margin-box (or page-context) <see cref="ComputedStyle"/>: first INHERIT the
    /// supported properties (all are inherited) from <paramref name="parentStyle"/>, then resolve +
    /// materialize this level's own declarations on top (each property's cascade winner — importance
    /// then source order). Used twice for the CSS Page 3 chain: page-context = Build(@page decls,
    /// rootStyle); margin box = Build(box decls, pageContext). <paramref name="parentStyle"/> null
    /// at the top (own-declarations only). <paramref name="diagnostics"/> receives invalid-value
    /// diagnostics (e.g. <c>color: bogus</c>). The result is marked box-owned (its <c>Dispose</c>
    /// becomes a no-op) for the synthetic <c>Box</c> the painter wraps it in.</summary>
    public static ComputedStyle Build(
        ImmutableArray<CssDeclaration> declarations,
        ComputedStyle? parentStyle = null,
        ICssDiagnosticsSink? diagnostics = null)
    {
        var style = ComputedStyle.Rent();

        // Inheritance (cycle 5): copy each supported property's slot from the parent first, so an
        // own-declaration below overwrites it but an unspecified one flows down (margin box ← @page
        // ← root). All five supported properties are inherited (vertical-align isn't, and is read
        // raw — never inherited). Mirrors BoxBuilder.ApplyInheritance (slot + side-table + deferred).
        if (parentStyle is not null)
            foreach (var id in SupportedStyleIds)
                InheritProperty(style, parentStyle, id);

        if (!declarations.IsDefaultOrEmpty)
        {
            // Cascade each supported longhand by importance then source order.
            Dictionary<PropertyId, Winner>? winners = null;
            foreach (var decl in declarations)
            {
                // A `font` shorthand that reached here means CssParserAdapter.ParseRawDeclarations
                // couldn't expand it (a system-font keyword or a malformed/invalid value) — it keeps
                // the raw declaration as a marker. Surface it (review #3) instead of silently
                // ignoring, then skip (it's not a supported longhand).
                if (string.Equals(decl.Property, "font", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics?.Emit(new CssDiagnostic(
                        CssDiagnosticCodes.CssPropertyValueInvalid001,
                        $"Could not apply 'font: {DiagnosticTextSanitizer.Sanitize(decl.Value.RawText)}' in an " +
                        "@page margin box — the value is a system-font keyword or an unsupported/malformed " +
                        "shorthand; use font-style/font-weight/font-size/font-family longhands.",
                        CssDiagnosticSeverity.Warning,
                        decl.Location));
                    continue;
                }
                // A `border` / `border-<side>` shorthand reaching here is likewise one
                // CssParserAdapter.ParseRawDeclarations could NOT expand (a malformed/invalid value —
                // a valid one is expanded to the border-* longhands upstream, and AngleSharp expands
                // page-context borders, so a surviving shorthand is by construction rejected). Surface
                // it (review P2) instead of silently dropping, then skip (a shorthand isn't a longhand).
                if (BorderShorthandExpander.IsBorderShorthand(decl.Property))
                {
                    diagnostics?.Emit(new CssDiagnostic(
                        CssDiagnosticCodes.CssPropertyValueInvalid001,
                        $"Could not apply '{decl.Property}: {DiagnosticTextSanitizer.Sanitize(decl.Value.RawText)}' in an " +
                        "@page margin box — the value is an unsupported or malformed border shorthand " +
                        "(<line-width> || <line-style> || <color>); use the border-<side>-width/-style/-color longhands.",
                        CssDiagnosticSeverity.Warning,
                        decl.Location));
                    continue;
                }
                // A `border-width` / `border-style` / `border-color` box shorthand reaching here is an
                // un-expandable one (a valid one becomes the four per-edge longhands upstream); surface
                // it instead of silently dropping.
                if (BorderBoxShorthandExpander.IsBorderBoxShorthand(decl.Property))
                {
                    diagnostics?.Emit(new CssDiagnostic(
                        CssDiagnosticCodes.CssPropertyValueInvalid001,
                        $"Could not apply '{decl.Property}: {DiagnosticTextSanitizer.Sanitize(decl.Value.RawText)}' in an " +
                        "@page margin box — the value is an unsupported or malformed border box shorthand " +
                        "(1–4 values); use the border-<side>-width/-style/-color longhands.",
                        CssDiagnosticSeverity.Warning,
                        decl.Location));
                    continue;
                }
                // A `padding` shorthand reaching here is likewise an un-expandable one (a valid one
                // becomes padding-* longhands upstream); surface it instead of silently dropping.
                if (PaddingShorthandExpander.IsPaddingShorthand(decl.Property))
                {
                    diagnostics?.Emit(new CssDiagnostic(
                        CssDiagnosticCodes.CssPropertyValueInvalid001,
                        $"Could not apply 'padding: {DiagnosticTextSanitizer.Sanitize(decl.Value.RawText)}' in an " +
                        "@page margin box — the value is an unsupported or malformed padding shorthand " +
                        "(<length>{1,4}); use the padding-<side> longhands.",
                        CssDiagnosticSeverity.Warning,
                        decl.Location));
                    continue;
                }
                if (!PropertyMetadata.NameToId.TryGetValue(decl.Property, out var id)) continue;
                if (!CascadedStyleIdSet.Contains(id)) continue; // whitelist (review P2; + background-color, cycle 8)
                winners ??= new Dictionary<PropertyId, Winner>();
                if (winners.TryGetValue(id, out var w) && w.Important && !decl.IsImportant) continue;
                winners[id] = new Winner(decl.Value.RawText, decl.IsImportant, decl.Location);
            }

            if (winners is not null)
            {
                // Materialize in the fixed whitelist order (deterministic; these longhands are
                // independent so order doesn't change the result, but the walk is stable).
                foreach (var id in CascadedStyleIds)
                {
                    if (!winners.TryGetValue(id, out var w)) continue;

                    // CSS-wide keywords are cascade-level, not leaf values (post-PR-#134 review P2) —
                    // and PROPERTY-AWARE now that the non-inherited `background-color` is in the set
                    // (post-PR-#137 review P2):
                    //   inherit → take the parent's value (for `background-color: inherit` this is the
                    //             explicit-inherit the non-inherited default would otherwise skip);
                    //   initial → reset to the property's initial value (clear the slot → reader default);
                    //   unset   → inherit for an INHERITED property, initial for a non-inherited one;
                    //   revert/revert-layer → approximated the same way (inherited keeps the parent,
                    //             non-inherited reverts to initial; a precise origin/layer rollback
                    //             needs the cascade machinery — deferrals.md).
                    // Skipping the leaf resolve also avoids a spurious invalid-value diagnostic.
                    if (CssWideKeyword.Is(w.Value))
                    {
                        ApplyCssWideKeyword(style, parentStyle, id, w.Value, InheritedStyleIdSet.Contains(id));
                        continue;
                    }

                    var resolved = PropertyResolverDispatch.Resolve(id, w.Value, diagnostics, w.Location);
                    resolved.MaterializeInto(style, id);

                    // Non-absolute padding — a percentage (CSS B&B §8.4: resolves against the containing
                    // block inline size) or a font-/viewport-relative length (`1em` / `5vw`, left
                    // DEFERRED by the resolver) — is a VALID value, but the margin-box painter can't
                    // resolve it to used px yet: it reads padding via ReadLengthPxOrZero, which honors
                    // ONLY a LengthPx slot and otherwise reads 0, so such a padding would SILENTLY
                    // vanish. Diagnose + drop it (an EXPLICIT deferral, not silent corruption — CLAUDE.md
                    // #7, review P2 + Copilot), pending the §5.3 margin-box sizing / a font-context
                    // resolve. `!resolved.IsInvalid` skips a genuinely-invalid value (Resolve already
                    // diagnosed it); border-*-width can't be non-px and the other cascaded properties
                    // aren't lengths, so padding is the only case here.
                    if (IsPaddingId(id) && !resolved.IsInvalid && style.Get(id).Tag != ComputedSlotTag.LengthPx)
                    {
                        diagnostics?.Emit(new CssDiagnostic(
                            CssDiagnosticCodes.CssPropertyValueInvalid001,
                            $"Padding '{DiagnosticTextSanitizer.Sanitize(w.Value)}' in an @page margin box " +
                            "isn't an absolute length — percentage and font-/viewport-relative padding " +
                            "aren't resolved to used px here yet (they would resolve to 0); use an " +
                            "absolute length (px/pt/cm/in). (deferrals.md)",
                            CssDiagnosticSeverity.Warning,
                            w.Location));
                        style.Unset(id);
                    }
                    // A DEFERRED `width`/`height` — a font-/viewport-relative length (`10em` / `5vh`) or a
                    // `calc()` the resolver couldn't resolve to a used size at cascade time (it returns
                    // Deferred, with NO diagnostic) — is a VALID value, but the margin-box painter only
                    // honors an absolute length or a percentage (TryReadExplicitSizePx); a deferred size
                    // would SILENTLY fall back to shrink-to-fit. Diagnose + drop it (an EXPLICIT deferral,
                    // not a silent fallback — CLAUDE.md #7, review P2), mirroring the padding policy,
                    // pending a font-/viewport-context resolve. Only the Deferred state reaches here:
                    // `auto` (a Resolved keyword), an absolute length (LengthPx), and a percentage
                    // (Percentage) are honored as-is, and an invalid value was already diagnosed by Resolve.
                    else if (IsSizeId(id) && resolved.IsDeferred)
                    {
                        diagnostics?.Emit(new CssDiagnostic(
                            CssDiagnosticCodes.CssPropertyValueInvalid001,
                            $"The margin-box {(id == PropertyId.Width ? "width" : "height")} " +
                            $"'{DiagnosticTextSanitizer.Sanitize(w.Value)}' isn't an absolute length or a " +
                            "percentage — font-/viewport-relative and calc() sizes aren't resolved to a used " +
                            "size here yet (the box falls back to shrink-to-fit); use an absolute length " +
                            "(px/pt/cm/in) or a percentage. (deferrals.md)",
                            CssDiagnosticSeverity.Warning,
                            w.Location));
                        style.Unset(id);
                    }
                }
            }
        }

        // Resolve a parent-relative font-size/weight the dispatch left DEFERRED (em/ex/ch/%/larger/
        // smaller font-size, bolder/lighter weight; see DeferredFontResolver)
        // against the inherited parent — Task 21 cycle 7. Runs after the cascade (the deferred raw is
        // on the style now) and after inheritance copied the parent's resolved px down, so `@page {
        // font-size: 20px; @bottom-center { font-size: 1.5em } }` → 30px. A non-parent-relative form
        // (rem/viewport) stays deferred → the reader default (still a documented gap, deferrals.md).
        DeferredFontResolver.ResolveAgainstParent(style, parentStyle);

        style.MarkAsBoxOwned();
        return style;
    }

    /// <summary>Whether <paramref name="id"/> is one of the four <c>padding-*</c> longhands (which can
    /// hold a non-absolute length the margin-box painter can't yet resolve to used px).</summary>
    private static bool IsPaddingId(PropertyId id) =>
        id is PropertyId.PaddingTop or PropertyId.PaddingRight
            or PropertyId.PaddingBottom or PropertyId.PaddingLeft;

    /// <summary>Whether <paramref name="id"/> is the box's <c>width</c> or <c>height</c> (which can hold
    /// a DEFERRED font-/viewport-relative or <c>calc()</c> size the margin-box painter can't yet resolve
    /// to a used size — only an absolute length or a percentage is honored on the §5.3 variable axis).</summary>
    private static bool IsSizeId(PropertyId id) => id is PropertyId.Width or PropertyId.Height;

    /// <summary>Copy <paramref name="parentStyle"/>'s value for <paramref name="id"/> onto
    /// <paramref name="style"/> — the slot (+ the side-table payload for the font-family list, + a
    /// deferred raw for an unresolved parent-relative value). A no-op when the parent doesn't have
    /// <paramref name="id"/> set. Shared by the inheritance copy and the <c>inherit</c> keyword.</summary>
    private static void InheritProperty(ComputedStyle style, ComputedStyle parentStyle, PropertyId id)
    {
        if (!parentStyle.IsSet(id)) return;
        var slot = parentStyle.Get(id);
        if (!slot.IsUnset)
        {
            style.Set(id, slot);
            if (slot.Tag == ComputedSlotTag.SideTableIndex
                && parentStyle.TryGetSideTablePayloadRaw(id, out var payload) && payload is not null)
                style.SetSideTablePayload(id, payload); // the font-family list
        }
        else if (parentStyle.TryGetDeferred(id, out var raw) && raw is not null)
        {
            style.SetDeferred(id, raw);
        }
    }

    /// <summary>Apply a CSS-wide keyword to <paramref name="id"/>, PROPERTY-AWARE (post-PR-#137 review
    /// P2): <c>inherit</c> takes the parent's value; <c>initial</c> clears to the initial value;
    /// <c>unset</c> / <c>revert</c> / <c>revert-layer</c> behave as <c>inherit</c> for an inherited
    /// property (<paramref name="isInherited"/>) and as <c>initial</c> for a non-inherited one (e.g.
    /// <c>background-color</c>). The slot is cleared first, then re-taken from the parent when the
    /// keyword resolves to <c>inherit</c> — so a pre-inherited slot is overwritten correctly and a
    /// null parent falls back to initial.</summary>
    private static void ApplyCssWideKeyword(
        ComputedStyle style, ComputedStyle? parentStyle, PropertyId id, string keyword, bool isInherited)
    {
        var kw = keyword.Trim();
        var takeParent = kw.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || (isInherited && !kw.Equals("initial", StringComparison.OrdinalIgnoreCase));
        style.Unset(id);
        if (takeParent && parentStyle is not null) InheritProperty(style, parentStyle, id);
    }

    /// <summary>The fraction of leftover inline space before the line (0 = start, 0.5 = center,
    /// 1 = end) implied by the box's OWN declared <c>text-align</c>, or <see langword="null"/> when
    /// the box declared none (the caller keeps the name-derived default). Read from the box's
    /// declarations — NOT the inherited style — so the page/root's UA-default <c>text-align: start</c>
    /// can't spuriously override the name-derived alignment (post-PR-#134 review). On a single
    /// margin-box line, <c>justify</c> / <c>justify-all</c> behave as start; <c>initial</c> is start
    /// (text-align's initial value); <c>inherit</c>/<c>unset</c>/<c>revert</c> (alignment isn't
    /// inherited here) keep the name-derived default. LTR mapping.</summary>
    public static double? HorizontalAlignFactor(ImmutableArray<CssDeclaration> declarations)
    {
        return WinningRawValue(declarations, "text-align")?.Trim().ToLowerInvariant() switch
        {
            "center" => 0.5,
            "right" or "end" => 1.0,
            "left" or "start" or "justify" or "justify-all" or "match-parent" or "initial" => 0.0,
            _ => null, // not declared / inherit / unset / revert / unknown → name-derived default
        };
    }

    /// <summary>The fraction of leftover block space before the line (0 = top, 0.5 = middle,
    /// 1 = bottom) implied by the box's OWN declared <c>vertical-align: top|middle|bottom</c>, or
    /// <see langword="null"/> when none / unrecognized (the caller keeps the name-derived default).
    /// <c>vertical-align</c> isn't an inherited property and isn't cascade-resolved, so it's read
    /// from the box's raw declarations (importance then source order).</summary>
    public static double? VerticalAlignFactor(ImmutableArray<CssDeclaration> declarations)
    {
        return WinningRawValue(declarations, "vertical-align")?.Trim().ToLowerInvariant() switch
        {
            "top" => 0.0,
            "middle" => 0.5,
            "bottom" => 1.0,
            _ => null, // baseline / initial / inherit / none → keep the name-derived default
        };
    }

    /// <summary>The importance-then-source-order winning raw value of <paramref name="property"/>
    /// among <paramref name="declarations"/> (a later normal can't override an earlier
    /// <c>!important</c>), or <see langword="null"/> when it isn't declared.</summary>
    private static string? WinningRawValue(ImmutableArray<CssDeclaration> declarations, string property)
    {
        if (declarations.IsDefaultOrEmpty) return null;
        string? winner = null;
        var winnerImportant = false;
        foreach (var decl in declarations)
        {
            if (!string.Equals(decl.Property, property, StringComparison.OrdinalIgnoreCase)) continue;
            if (winner is not null && winnerImportant && !decl.IsImportant) continue;
            winner = decl.Value.RawText;
            winnerImportant = decl.IsImportant;
        }
        return winner;
    }

    /// <summary>A property's cascade winner: its raw value, whether it came from an
    /// <c>!important</c> declaration, and the declaration's source location (for diagnostics).</summary>
    private readonly record struct Winner(string Value, bool Important, CssSourceLocation Location);
}

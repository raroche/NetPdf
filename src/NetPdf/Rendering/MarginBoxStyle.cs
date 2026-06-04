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
/// <b>Supported properties (a WHITELIST).</b> Only the inherited <c>font-family</c> /
/// <c>font-size</c> / <c>font-weight</c> / <c>font-style</c> / <c>color</c> are materialized +
/// inherited. <c>text-align</c> / <c>vertical-align</c> are NOT inherited here — alignment is read
/// from the box's OWN declarations (<see cref="HorizontalAlignFactor"/> / <see cref="VerticalAlignFactor"/>)
/// and overrides the box's name-derived default; inheriting the page/root's UA-default
/// <c>text-align: start</c> would otherwise spuriously override the name-derived centering
/// (post-PR-#134 review). Other declarations (<c>padding</c> / <c>border</c> / <c>background</c> /
/// …) are deliberately IGNORED — the painter derives content origin from the box's border +
/// padding, so materializing them would shift (or paint behind) the margin-box text.
/// </para>
/// <para>
/// <b>Relative font (cycle 7).</b> A parent-relative <c>font-size</c> (<c>em</c> / <c>%</c> /
/// <c>larger</c> / <c>smaller</c>) or <c>font-weight</c> (<c>bolder</c> / <c>lighter</c>) the dispatch
/// leaves deferred is resolved against the inherited parent via <see cref="DeferredFontResolver"/>
/// after the cascade — so <c>@page { font-size: 20px; @bottom-center { font-size: 1.5em } }</c> →
/// 30px. (A non-parent-relative form — <c>rem</c> / viewport / container units — stays deferred and
/// falls back to the reader default, a documented gap.)
/// </para>
/// <para>
/// <b>Deferred (later cycles, deferrals.md#layout-to-pdf-pipeline).</b> <c>rem</c> / viewport-relative
/// font-size, page-context inheritance of alignment, precise <c>revert</c>, and the §5.3
/// three-box-per-edge sizing.
/// </para>
/// </remarks>
internal static class MarginBoxStyle
{
    /// <summary>The materializable + inherited longhands, in a fixed order (deterministic
    /// materialization). All are inherited properties that feed the shaper + text fill.
    /// <c>text-align</c> / <c>vertical-align</c> are NOT here — alignment is read from the box's OWN
    /// declarations (<see cref="HorizontalAlignFactor"/> / <see cref="VerticalAlignFactor"/>) and
    /// overrides the box's name-derived default; it is NOT inherited from the page/root, whose
    /// (UA-default) <c>text-align: start</c> would otherwise spuriously override the name-derived
    /// centering (post-PR-#134 review).</summary>
    private static readonly ImmutableArray<PropertyId> SupportedStyleIds = ImmutableArray.Create(
        PropertyId.FontFamily, PropertyId.FontSize, PropertyId.FontWeight, PropertyId.FontStyle,
        PropertyId.Color);

    private static readonly FrozenSet<PropertyId> SupportedStyleIdSet = SupportedStyleIds.ToFrozenSet();

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
        {
            foreach (var id in SupportedStyleIds)
            {
                if (!parentStyle.IsSet(id)) continue;
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
        }

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
                if (!PropertyMetadata.NameToId.TryGetValue(decl.Property, out var id)) continue;
                if (!SupportedStyleIdSet.Contains(id)) continue; // whitelist (review P2)
                winners ??= new Dictionary<PropertyId, Winner>();
                if (winners.TryGetValue(id, out var w) && w.Important && !decl.IsImportant) continue;
                winners[id] = new Winner(decl.Value.RawText, decl.IsImportant, decl.Location);
            }

            if (winners is not null)
            {
                // Materialize in the fixed whitelist order (deterministic; these longhands are
                // independent so order doesn't change the result, but the walk is stable).
                foreach (var id in SupportedStyleIds)
                {
                    if (!winners.TryGetValue(id, out var w)) continue;

                    // CSS-wide keywords are cascade-level, not leaf values (post-PR-#134 review P2):
                    //   initial      → reset to the property's initial value (clear the slot → the
                    //                  reader falls back to the initial/default);
                    //   inherit/unset → keep the value already inherited from the parent above
                    //                  (these are inherited properties) — a no-op here;
                    //   revert/revert-layer → approximated as the inherited value (a precise
                    //                  origin/layer rollback needs the cascade machinery — deferrals.md).
                    // Skipping the leaf resolve also avoids a spurious invalid-value diagnostic.
                    if (CssWideKeyword.Is(w.Value))
                    {
                        if (w.Value.Trim().Equals("initial", StringComparison.OrdinalIgnoreCase))
                            style.Unset(id);
                        continue;
                    }

                    PropertyResolverDispatch.Resolve(id, w.Value, diagnostics, w.Location).MaterializeInto(style, id);
                }
            }
        }

        // Resolve a parent-relative font-size/weight the dispatch left DEFERRED (em/%/larger/bolder)
        // against the inherited parent — Task 21 cycle 7. Runs after the cascade (the deferred raw is
        // on the style now) and after inheritance copied the parent's resolved px down, so `@page {
        // font-size: 20px; @bottom-center { font-size: 1.5em } }` → 30px. A non-parent-relative form
        // (rem/viewport) stays deferred → the reader default (still a documented gap, deferrals.md).
        DeferredFontResolver.ResolveAgainstParent(style, parentStyle);

        style.MarkAsBoxOwned();
        return style;
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

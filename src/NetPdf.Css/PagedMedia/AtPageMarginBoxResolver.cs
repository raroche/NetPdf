// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;

namespace NetPdf.Css.PagedMedia;

/// <summary>
/// Resolves the page margin boxes declared inside bare <c>@page</c> rules — the 16
/// <c>@top-center</c> / <c>@bottom-right-corner</c> / … at-rules of CSS Paged Media L3 §6.4 —
/// down to (box name, raw <c>content</c> value) pairs. Phase 3 Task 21 cycle 3 — the
/// keystone for running headers/footers.
/// </summary>
/// <remarks>
/// <para>
/// The pre-pass (<c>CssPreprocessor</c>) recovers the margin boxes AngleSharp.Css drops and the
/// adapter re-parents them under the owning <c>@page</c> rule's <see cref="CssAtRule.ChildRules"/>
/// (each a <see cref="CssAtRule"/> whose <see cref="CssAtRule.Name"/> is the box name and whose
/// <see cref="CssAtRule.Declarations"/> are parsed). This resolver reads them. Applicability +
/// ordering reuse the shared <see cref="AtPageRules.EnumerateBarePageRules"/> (cascade-style
/// media / disabled filtering, bare <c>@page</c> only) — the paper-size conditioning that gates
/// <c>size</c> does NOT apply to margin boxes. Among contributing rules the LAST <c>content</c>
/// per box name wins (source order); a box with no <c>content</c> declaration is omitted (cycle 3
/// paints text only).
/// </para>
/// <para>
/// <b>Cycle 3 scope.</b> Only the raw <c>content</c> value is returned; the orchestrator resolves
/// it (literal strings + <c>attr()</c> via <c>CssContentList</c>) — <c>counter()</c> / <c>string()</c>
/// / <c>element()</c> generated content, per-box style (font / color / alignment), and the CSS
/// Page 3 §5.3 three-box-per-edge sizing algorithm are later cycles
/// (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class AtPageMarginBoxResolver
{
    /// <summary>A margin box resolved to its name + the raw value of its winning <c>content</c>
    /// declaration (importance/quoting intact — the orchestrator resolves it via
    /// <c>CssContentList</c>).</summary>
    internal readonly record struct ResolvedMarginBox(string Name, string ContentRawValue);

    /// <summary>The 16 CSS Paged Media L3 §6.4 margin-box names, in canonical paint order
    /// (corners + edges, top → bottom). The resolver emits present boxes in this order so the
    /// output is deterministic regardless of source order (CLAUDE.md #4).</summary>
    internal static readonly ImmutableArray<string> CanonicalNames = ImmutableArray.Create(
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner",
        "left-top", "left-middle", "left-bottom",
        "right-top", "right-middle", "right-bottom",
        "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right", "bottom-right-corner");

    private static readonly FrozenSet<string> KnownNames =
        CanonicalNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Walk the applicable bare <c>@page</c> rules and resolve each declared margin
    /// box's winning <c>content</c> value. Returns the content-bearing boxes in canonical order;
    /// empty when none declare <c>content</c>.</summary>
    public static ImmutableArray<ResolvedMarginBox> Resolve(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        // Keyed by lowercased box name; later contribution (source order across @page rules) wins.
        Dictionary<string, string>? winners = null;
        foreach (var at in AtPageRules.EnumerateBarePageRules(sheets, media))
        {
            foreach (var child in at.ChildRules)
            {
                if (child is not CssAtRule box) continue;
                var name = box.Name.ToLowerInvariant();
                if (!KnownNames.Contains(name)) continue;
                if (LastContentValue(box.Declarations) is not { } content) continue; // no content → not painted
                winners ??= new Dictionary<string, string>(StringComparer.Ordinal);
                winners[name] = content; // last wins
            }
        }

        if (winners is null) return ImmutableArray<ResolvedMarginBox>.Empty;

        var output = ImmutableArray.CreateBuilder<ResolvedMarginBox>(winners.Count);
        foreach (var name in CanonicalNames) // emit in canonical order for determinism
        {
            if (winners.TryGetValue(name, out var content))
                output.Add(new ResolvedMarginBox(name, content));
        }
        return output.ToImmutable();
    }

    /// <summary>The raw value of the LAST <c>content</c> declaration in source order (CSS
    /// last-declaration-wins), or <see langword="null"/> when the box declares none / an empty
    /// value.</summary>
    private static string? LastContentValue(ImmutableArray<CssDeclaration> declarations)
    {
        string? last = null;
        foreach (var decl in declarations)
        {
            if (!string.Equals(decl.Property, "content", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = decl.Value.RawText;
            if (!string.IsNullOrWhiteSpace(raw)) last = raw;
        }
        return last;
    }
}

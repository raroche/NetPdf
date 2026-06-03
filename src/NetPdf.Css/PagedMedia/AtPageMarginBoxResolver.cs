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
/// <c>size</c> does NOT apply to margin boxes. The cascade winner per box name is chosen by
/// importance then source order (an <c>!important</c> <c>content</c> beats a normal one; among
/// equal importance the last wins), within a box body AND across <c>@page</c> rules. A box whose
/// winning <c>content</c> is the bare keyword <c>none</c> / <c>normal</c> (= "no generated
/// content") is omitted WITHOUT a diagnostic, as is a box that declares no <c>content</c> (cycle 3
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

    /// <summary>Walk the applicable bare <c>@page</c> rules and resolve each declared margin box's
    /// cascade-winning <c>content</c> (importance then source order). Returns the renderable boxes
    /// in canonical order — omitting boxes with no <c>content</c> and boxes whose winner is
    /// <c>none</c> / <c>normal</c> (suppression); empty when none render.</summary>
    public static ImmutableArray<ResolvedMarginBox> Resolve(
        IEnumerable<CssStylesheet> sheets, CssMediaContext media)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(media);

        // Per box name, the cascade-winning `content`: importance then source order. A later
        // normal declaration can't override an earlier `!important` one — within a box body AND
        // across @page rules (post-PR-#132 review P1).
        Dictionary<string, Candidate>? candidates = null;
        foreach (var at in AtPageRules.EnumerateBarePageRules(sheets, media))
        {
            foreach (var child in at.ChildRules)
            {
                if (child is not CssAtRule box) continue;
                var name = box.Name.ToLowerInvariant();
                if (!KnownNames.Contains(name)) continue;
                foreach (var decl in box.Declarations)
                {
                    if (!string.Equals(decl.Property, "content", StringComparison.OrdinalIgnoreCase)) continue;
                    var raw = decl.Value.RawText;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    candidates ??= new Dictionary<string, Candidate>(StringComparer.Ordinal);
                    var c = candidates.TryGetValue(name, out var existing) ? existing : default;
                    Apply(ref c, raw, decl.IsImportant);
                    candidates[name] = c;
                }
            }
        }

        if (candidates is null) return ImmutableArray<ResolvedMarginBox>.Empty;

        var output = ImmutableArray.CreateBuilder<ResolvedMarginBox>(candidates.Count);
        foreach (var name in CanonicalNames) // emit in canonical order for determinism
        {
            // A winning `none` / `normal` means "no box" (suppression), not unsupported content —
            // omit it WITHOUT a diagnostic (post-PR-#132 review P2).
            if (candidates.TryGetValue(name, out var c) && c.Set && !IsSuppression(c.RawValue))
                output.Add(new ResolvedMarginBox(name, c.RawValue));
        }
        return output.Count == 0 ? ImmutableArray<ResolvedMarginBox>.Empty : output.ToImmutable();
    }

    /// <summary>Record <paramref name="raw"/> as the per-box cascade winner if it wins per CSS
    /// Cascade §5 (importance) + §7.4 (source order): an <c>!important</c> beats a normal
    /// declaration regardless of order; among equal importance the later (this one, visited in
    /// source order) wins.</summary>
    private static void Apply(ref Candidate candidate, string raw, bool important)
    {
        if (candidate.Set && candidate.Important && !important) return;
        candidate = new Candidate { Set = true, RawValue = raw, Important = important };
    }

    /// <summary>True when <paramref name="raw"/> is the bare keyword <c>none</c> or <c>normal</c> —
    /// both compute to "no generated content" for a margin box, so the box is omitted (NOT a
    /// diagnostic). A quoted <c>"none"</c> keeps its quotes and renders as the literal text.</summary>
    private static bool IsSuppression(string raw)
    {
        var v = raw.Trim();
        return v.Equals("none", StringComparison.OrdinalIgnoreCase)
            || v.Equals("normal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Per-box cascade candidate: the winning raw <c>content</c> value so far + whether it
    /// came from an <c>!important</c> declaration. <see cref="Set"/> distinguishes "no winner yet"
    /// from a real value.</summary>
    private struct Candidate
    {
        public bool Set;
        public string RawValue;
        public bool Important;
    }
}

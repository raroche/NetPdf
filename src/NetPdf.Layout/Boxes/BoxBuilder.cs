// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Walks a styled DOM (an <see cref="IDocument"/> + the
/// <see cref="ResolvedCascadeResult"/> from <see cref="VarResolver"/>) and produces
/// the box tree per CSS Display L3 §3 — generating one principal <see cref="Box"/>
/// per element, materializing <c>::before</c> and <c>::after</c> pseudo-element
/// boxes, wrapping anonymous text in <see cref="BoxKind.TextRun"/> boxes, and
/// inserting anonymous-block wrappers when a block-container has mixed
/// inline + block children per §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline position.</b> Runs after <see cref="VarResolver"/> (Task 8) +
/// <see cref="CalcResolver"/> (Task 9) + <see cref="PropertyResolverDispatch"/>
/// (Task 10). The walk computes per-element <see cref="ComputedStyle"/>s on the
/// fly: for each element it iterates the resolved declarations, dispatches each
/// through <see cref="PropertyResolverDispatch.Resolve"/>, and materializes the
/// result via <see cref="ResolverResult.MaterializeInto"/>. Inheritance happens
/// here too — properties not explicitly set on an element fall back to the
/// parent's value when the property's <see cref="PropertyMeta.Inherits"/> is
/// true (otherwise the registry default applies).
/// </para>
/// <para>
/// <b>Cycle 1 + hardening review scope.</b> DOM walk + display dispatch +
/// pseudo materialization (<c>::before</c> / <c>::after</c> with
/// single-string content per <see cref="CssStringParser"/>) +
/// anonymous-block insertion (Display L3 §3.1) + replaced-element detection
/// + <c>display: none</c> skip + <c>display: contents</c> child-promotion
/// (§3.1.1) + <c>&lt;br&gt;</c> as a forced <see cref="BoxKind.LineBreak"/>.
/// Out of scope (Task 13 + later cycles): table fixup (the Tables L3 §3
/// box-generation algorithm — synthesizing missing <c>tbody</c>/<c>tr</c>/<c>td</c>
/// wrappers, the table wrapper + grid materialization), <c>::marker</c>
/// generation, content-list parsing (counter / attr / open-quote /
/// close-quote / image / multi-token), block-in-inline split (§3.2).
/// </para>
/// <para>
/// <b>Table output is NOT layout-ready.</b> The Tables L3 §2.1 wrapper +
/// grid + table-fixup pass is Task 13. Cycle 1 emits <see cref="BoxKind.Table"/>
/// as the principal box but does not yet insert the
/// <see cref="BoxKind.TableGrid"/> intermediary or synthesize missing
/// table internals. Layout consumers should treat the table subtree as
/// structural-placeholder until Task 13 lands.
/// </para>
/// </remarks>
internal static class BoxBuilder
{
    /// <summary>Walk <paramref name="document"/>'s element tree + emit the box
    /// tree. Returns the synthetic <see cref="BoxKind.Root"/> box; the document
    /// element's principal box is its first child.</summary>
    /// <param name="document">The HTML document — typically from
    /// <c>HtmlParsingHost.ParseAsync</c>.</param>
    /// <param name="cascade">Resolved (post-<c>var()</c>) cascade output for
    /// every styled element + pseudo-element.</param>
    /// <param name="diagnostics">Sink for property-resolution failures.
    /// <see langword="null"/> is allowed.</param>
    public static Box Build(
        IDocument document,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cascade);

        var rootStyle = ComputedStyle.Rent();
        ApplyDefaults(rootStyle);
        var root = Box.CreateRoot(rootStyle);

        if (document.DocumentElement is null) return root;

        var docBoxes = BuildElementBoxes(
            document.DocumentElement, parentStyle: rootStyle,
            cascade, diagnostics);
        foreach (var box in docBoxes)
        {
            root.AppendChild(box);
        }
        FixupAnonymousBlocks(root);
        return root;
    }

    /// <summary>Recursive worker — emits the box(es) for an element. Usually
    /// returns a single-element list (the principal box), but returns an empty
    /// list for <c>display: none</c> and a flattened child list for
    /// <c>display: contents</c> (per Display L3 §3.1.1 the box itself
    /// disappears; the children become children of the grandparent).</summary>
    private static IReadOnlyList<Box> BuildElementBoxes(
        IElement element,
        ComputedStyle parentStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics)
    {
        // <br> is special — it's a forced line break, not a generic inline.
        // Per HTML "Rendering" §15.3.6 the UA stylesheet defines:
        //   br { content: "\A"; white-space: pre-line }
        // i.e., a hard newline. We model it explicitly so Phase 3 line layout
        // can treat it as a break opportunity instead of an empty inline that
        // silently disappears during line construction.
        if (element.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            var brStyle = ComputedStyle.Rent();
            ApplyDefaults(brStyle);
            ApplyInheritance(brStyle, parentStyle);
            ApplyResolvedDeclarations(brStyle, cascade.TryGetStylesFor(element), diagnostics);
            // BoxKind.LineBreak with the source element so diagnostics still
            // point back to the right DOM node. The kind is inline-level.
            return new[] { Box.ForElement(BoxKind.LineBreak, brStyle, element) };
        }

        var style = ComputedStyle.Rent();
        ApplyDefaults(style);
        ApplyInheritance(style, parentStyle);
        ApplyResolvedDeclarations(style, cascade.TryGetStylesFor(element), diagnostics);

        var displayText = ReadDisplay(style, element);
        var mapResult = DisplayMapper.Map(displayText, element.LocalName, out var kind);

        switch (mapResult)
        {
            case DisplayMapper.DisplayMappingResult.None:
                style.Dispose();
                return Array.Empty<Box>();

            case DisplayMapper.DisplayMappingResult.Contents:
                // Per Display L3 §3.1.1: the principal box disappears + each
                // in-flow child gets promoted to the grandparent. We still use
                // `style` for inheritance into the children even though the
                // box itself isn't created.
                var promoted = new List<Box>();
                foreach (var node in element.ChildNodes)
                {
                    BuildChildNode(node, style, element, cascade, diagnostics, promoted);
                }
                style.Dispose();
                return promoted;

            case DisplayMapper.DisplayMappingResult.Unsupported:
                // Cycle-1 fallback: ruby family + unknown values become block.
                kind = BoxKind.BlockContainer;
                break;
        }

        var box = Box.ForElement(kind, style, element);

        // ::before pseudo-element comes before the children. Keys in the cascade
        // are lowercase pseudo-element identifiers without the `::` prefix
        // (per SelectorCompiler's `PseudoElement(lower)` convention).
        var beforePseudo = BuildPseudo(element, "before", style, cascade, diagnostics);
        if (beforePseudo is not null) box.AppendChild(beforePseudo);

        // Children — DOM nodes in document order.
        var collector = new List<Box>(8);
        foreach (var node in element.ChildNodes)
        {
            BuildChildNode(node, style, element, cascade, diagnostics, collector);
        }
        foreach (var c in collector) box.AppendChild(c);

        // ::after pseudo-element comes after the children.
        var afterPseudo = BuildPseudo(element, "after", style, cascade, diagnostics);
        if (afterPseudo is not null) box.AppendChild(afterPseudo);

        FixupAnonymousBlocks(box);
        return new[] { box };
    }

    /// <summary>Append boxes for one DOM child node (text → TextRun;
    /// element → recurse with display:contents flattening). Used by both the
    /// principal-box path + the display:contents promotion path.</summary>
    private static void BuildChildNode(
        INode node,
        ComputedStyle parentStyle,
        IElement parentElement,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics,
        List<Box> collector)
    {
        switch (node)
        {
            case IText text:
                if (text.Data.Length > 0)
                {
                    collector.Add(Box.TextRun(text.Data, parentStyle, parentElement));
                }
                break;
            case IElement childElement:
                var produced = BuildElementBoxes(childElement, parentStyle, cascade, diagnostics);
                foreach (var child in produced) collector.Add(child);
                break;
            // Comment / CDATA / etc. are ignored for box generation.
        }
    }

    /// <summary>Generate the box for a pseudo-element rule set when one is
    /// registered + has a non-<c>none</c>/non-<c>normal</c> <c>content</c>
    /// property. Cycle 1 + hardening: only single-CSS-string <c>content</c> is
    /// rendered (via <see cref="CssStringParser"/>); <c>counter()</c> /
    /// <c>attr()</c> / <c>url()</c> / <c>open-quote</c> / <c>close-quote</c> /
    /// multi-token content is silently skipped (no pseudo box) — those need
    /// the typed content-list parser shipping in cycle 2.</summary>
    private static Box? BuildPseudo(
        IElement host,
        string pseudoName,
        ComputedStyle hostStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics)
    {
        var ruleSet = cascade.TryGetStylesForPseudo(host, pseudoName);
        if (ruleSet is null) return null;

        var contentDecl = ruleSet.GetWinner("content");
        if (contentDecl is null) return null;

        var rawContent = contentDecl.ResolvedValue.Trim();
        if (rawContent.Length == 0
            || rawContent.Equals("none", StringComparison.OrdinalIgnoreCase)
            || rawContent.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Only single-string content is supported in cycle 1. Non-string
        // content (counter/attr/url/quote/multi-token) silently produces no
        // pseudo box — emitting the literal text would be worse than nothing.
        if (!CssStringParser.TryParseSingleString(rawContent, out var generatedText))
        {
            return null;
        }

        // Build the pseudo's style — inherits from host, then applies the
        // pseudo's own resolved declarations.
        var pseudoStyle = ComputedStyle.Rent();
        ApplyDefaults(pseudoStyle);
        ApplyInheritance(pseudoStyle, hostStyle);
        ApplyResolvedDeclarations(pseudoStyle, ruleSet, diagnostics);

        // Per CSS Pseudo L4 §3.1: ::before / ::after default to display:inline
        // unless the cascade explicitly sets otherwise. We must NOT fall back
        // to the host's UA-default display (e.g., `<div>::before` would
        // wrongly default to block). And replaced-element detection must NOT
        // consult the host's local name (a pseudo-element on `<img>` is not
        // itself replaced).
        var displayText = ReadPseudoDisplay(pseudoStyle);
        var map = DisplayMapper.Map(displayText, elementLocalName: null, out var kind);
        if (map == DisplayMapper.DisplayMappingResult.None)
        {
            pseudoStyle.Dispose();
            return null;
        }
        if (map != DisplayMapper.DisplayMappingResult.Resolved)
        {
            kind = BoxKind.InlineBox;
        }

        var pseudo = pseudoName.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? BoxPseudo.Before
            : BoxPseudo.After;

        var pseudoBox = Box.ForPseudo(kind, pseudoStyle, host, pseudo);
        if (generatedText.Length > 0)
        {
            pseudoBox.AppendChild(Box.TextRun(generatedText, pseudoStyle, host));
        }
        return pseudoBox;
    }

    /// <summary>Inserts <see cref="BoxKind.AnonymousBlock"/> wrappers around
    /// contiguous inline-level child runs whenever <paramref name="parent"/> is
    /// a "block container" per CSS Display L3 §2.1 + §3.1 — that is, a box
    /// whose inner formatting context lays out children as either a BFC or an
    /// IFC. The set covers block-level outers (<see cref="BoxKind.Root"/>,
    /// <see cref="BoxKind.BlockContainer"/>, <see cref="BoxKind.ListItem"/>,
    /// <see cref="BoxKind.AnonymousBlock"/>) AND inline-level boxes whose
    /// inner FC is flow-root (<see cref="BoxKind.InlineBlockContainer"/>) AND
    /// table cells (<see cref="BoxKind.TableCell"/>) + table captions
    /// (<see cref="BoxKind.TableCaption"/>). Operates in-place on
    /// <see cref="Box.Children"/>.</summary>
    private static void FixupAnonymousBlocks(Box parent)
    {
        if (!IsBlockContainer(parent.Kind)) return;
        if (parent.Children.Count == 0) return;

        var hasBlock = false;
        var hasInline = false;
        for (var i = 0; i < parent.Children.Count; i++)
        {
            var c = parent.Children[i];
            if (c.IsBlockLevel) hasBlock = true;
            else if (c.IsInlineLevel) hasInline = true;
            if (hasBlock && hasInline) break;
        }
        if (!(hasBlock && hasInline)) return;

        var snapshot = new List<Box>(parent.Children.Count);
        foreach (var c in parent.Children) snapshot.Add(c);
        foreach (var c in snapshot) parent.RemoveChild(c);

        List<Box>? currentRun = null;
        foreach (var c in snapshot)
        {
            if (c.IsBlockLevel)
            {
                FlushRun(parent, ref currentRun);
                parent.AppendChild(c);
            }
            else
            {
                currentRun ??= new List<Box>();
                currentRun.Add(c);
            }
        }
        FlushRun(parent, ref currentRun);

        static void FlushRun(Box parent, ref List<Box>? run)
        {
            if (run is null || run.Count == 0) return;
            var wrapper = Box.Anonymous(BoxKind.AnonymousBlock, parent.Style);
            foreach (var child in run) wrapper.AppendChild(child);
            parent.AppendChild(wrapper);
            run = null;
        }
    }

    /// <summary><see langword="true"/> when <paramref name="kind"/> establishes
    /// a "block container" per Display L3 §2.1 — i.e., a box whose inner
    /// formatting context is block (flow / flow-root) and therefore subject
    /// to the §3.1 anonymous-block insertion rule when its children mix
    /// block-level + inline-level. Excludes flex / grid containers (their
    /// inner FCs are flex / grid, not flow) and table internals other than
    /// cell + caption (which have their own table-internal FC).</summary>
    private static bool IsBlockContainer(BoxKind kind) => kind switch
    {
        BoxKind.Root or BoxKind.BlockContainer or BoxKind.ListItem
            or BoxKind.AnonymousBlock or BoxKind.InlineBlockContainer
            or BoxKind.TableCell or BoxKind.TableCaption => true,
        _ => false,
    };

    // ============================================================
    // ComputedStyle construction helpers
    // ============================================================

    /// <summary>Read the computed <c>display</c> keyword. Falls back to the
    /// HTML UA-default per <see cref="HtmlDefaultDisplay"/> when the cascade
    /// didn't set it (no UA stylesheet shipped yet — cycle-2 work).</summary>
    private static string ReadDisplay(ComputedStyle style, IElement element)
    {
        if (style.IsDeferred(PropertyId.Display)
            && style.TryGetDeferred(PropertyId.Display, out var rawText)
            && rawText is not null)
        {
            return rawText;
        }
        if (style.IsSet(PropertyId.Display))
        {
            var slot = style.Get(PropertyId.Display);
            if (slot.Tag == ComputedSlotTag.Keyword)
            {
                var id = slot.AsKeyword();
                return DisplayKeywordName(id) ?? HtmlDefaultDisplay.GetDefault(element.LocalName);
            }
        }
        return HtmlDefaultDisplay.GetDefault(element.LocalName);
    }

    /// <summary>Read the computed <c>display</c> keyword for a pseudo-element
    /// — similar to <see cref="ReadDisplay"/> but falls back to <c>"inline"</c>
    /// (CSS Pseudo L4 §3.1 default for <c>::before</c> / <c>::after</c>) when
    /// the cascade didn't set it. Crucially does NOT consult the host's
    /// <see cref="HtmlDefaultDisplay"/> — a <c>div::before</c> defaults to
    /// inline, not block.</summary>
    private static string ReadPseudoDisplay(ComputedStyle style)
    {
        if (style.IsDeferred(PropertyId.Display)
            && style.TryGetDeferred(PropertyId.Display, out var rawText)
            && rawText is not null)
        {
            return rawText;
        }
        if (style.IsSet(PropertyId.Display))
        {
            var slot = style.Get(PropertyId.Display);
            if (slot.Tag == ComputedSlotTag.Keyword)
            {
                var id = slot.AsKeyword();
                var name = DisplayKeywordName(id);
                if (name is not null) return name;
            }
        }
        return "inline";
    }

    /// <summary>Reverse of <see cref="KeywordResolver"/>'s display table.
    /// Cycle-1 maintains this manually; cycle 2 will source-generate the
    /// reverse map alongside the keyword tables themselves.</summary>
    private static string? DisplayKeywordName(int id) => id switch
    {
        0 => "block", 1 => "inline", 2 => "inline-block", 3 => "list-item",
        4 => "flex", 5 => "inline-flex", 6 => "grid", 7 => "inline-grid",
        8 => "flow-root", 9 => "table", 10 => "inline-table",
        11 => "table-row-group", 12 => "table-header-group",
        13 => "table-footer-group", 14 => "table-row", 15 => "table-cell",
        16 => "table-column-group", 17 => "table-column", 18 => "table-caption",
        19 => "ruby", 20 => "ruby-base", 21 => "ruby-text",
        22 => "ruby-base-container", 23 => "ruby-text-container",
        24 => "contents", 25 => "none",
        _ => null,
    };

    /// <summary>Initialise <paramref name="style"/> with each property's
    /// registry default per <see cref="PropertyMetadata.Table"/>. Runs first
    /// so inheritance + explicit declarations overwrite later.</summary>
    /// <remarks>
    /// <b>Display is intentionally skipped.</b> The CSS spec default per
    /// properties.json is <c>inline</c>, but the proper default for an HTML
    /// element comes from the UA stylesheet (<c>div</c> → <c>block</c>,
    /// <c>tr</c> → <c>table-row</c>, etc. per HTML Living Standard "Rendering"
    /// §15.3). The UA stylesheet doesn't ship in cycle 1 — <see cref="ReadDisplay"/>
    /// consults <see cref="HtmlDefaultDisplay"/> as a stand-in when the cascade
    /// hasn't explicitly set <c>display</c>. Writing the registry's <c>inline</c>
    /// default here would mask that fallback and yield <see cref="BoxKind.InlineBox"/>
    /// for every unstyled HTML element.
    /// </remarks>
    private static void ApplyDefaults(ComputedStyle style)
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            if (meta.Id == PropertyId.Display) continue;
            var result = PropertyResolverDispatch.Resolve(meta.Id, meta.DefaultValue);
            result.MaterializeInto(style, meta.Id);
        }
    }

    /// <summary>Copy inherited properties from <paramref name="parentStyle"/>
    /// onto <paramref name="style"/>. Per CSS Cascade 4 §4.2 the cascade walks
    /// down through inheritance for properties whose
    /// <see cref="PropertyMeta.Inherits"/> is <see langword="true"/> and the
    /// property has no explicit declaration on the child.</summary>
    private static void ApplyInheritance(ComputedStyle style, ComputedStyle parentStyle)
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            if (!meta.Inherits) continue;
            if (parentStyle.IsSet(meta.Id))
            {
                var parentSlot = parentStyle.Get(meta.Id);
                if (!parentSlot.IsUnset)
                {
                    style.Set(meta.Id, parentSlot);
                }
                else if (parentStyle.TryGetDeferred(meta.Id, out var raw) && raw is not null)
                {
                    style.SetDeferred(meta.Id, raw);
                }
            }
        }
    }

    /// <summary>Iterate the resolved declarations + materialize each into
    /// <paramref name="style"/>. Each declaration goes through
    /// <see cref="PropertyResolverDispatch.Resolve"/> (with the
    /// declaration's source location threaded through
    /// per Task 12 hardening Rec 8 so any emitted diagnostic keeps its line/
    /// column origin) +
    /// <see cref="ResolverResult.MaterializeInto"/>.</summary>
    private static void ApplyResolvedDeclarations(
        ComputedStyle style,
        ResolvedRuleSet? ruleSet,
        ICssDiagnosticsSink? diagnostics)
    {
        if (ruleSet is null) return;
        foreach (var winner in ruleSet.Winners)
        {
            if (!PropertyMetadata.NameToId.TryGetValue(winner.Property, out var id))
                continue;
            var location = winner.OriginalDeclaration.Location;
            var result = PropertyResolverDispatch.Resolve(id, winner.ResolvedValue, diagnostics, location);
            result.MaterializeInto(style, id);
        }
    }
}

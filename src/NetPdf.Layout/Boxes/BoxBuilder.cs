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
/// inserting anonymous-block wrappers when a block container has mixed
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
/// <b>Cycle 1 scope.</b> DOM walk + display dispatch + pseudo materialization
/// (<c>::before</c> / <c>::after</c>) + anonymous-block insertion (Display L3
/// §3.1) + replaced-element detection. Out of scope (Task 13 + later cycles):
/// table fixup (the Tables L3 §3 box-generation algorithm — synthesizing
/// missing <c>tbody</c>/<c>tr</c>/<c>td</c> wrappers, the table wrapper +
/// grid materialization), <c>::marker</c> generation, block-in-inline split
/// (§3.2), <c>display: contents</c> child-promotion (cycle-1 treats it as
/// block).
/// </para>
/// <para>
/// <b>Diagnostics.</b> Unsupported display values (ruby family, etc.) are
/// silently treated as <c>block</c> in cycle 1 — no diagnostic code yet.
/// Cycle-2 will add <c>CSS-DISPLAY-UNSUPPORTED-001</c> + similar.
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

        // Synthetic root with an initial-style ComputedStyle. Anonymous boxes
        // can share this style; the root has no source element.
        var rootStyle = ComputedStyle.Rent();
        ApplyDefaults(rootStyle);
        var root = Box.CreateRoot(rootStyle);

        if (document.DocumentElement is null) return root;

        var docBox = BuildElement(
            document.DocumentElement, parentStyle: rootStyle,
            cascade, diagnostics);
        if (docBox is not null)
        {
            root.AppendChild(docBox);
            // Anonymous-block fixup runs bottom-up via BuildElement's recursion
            // but the root needs its own pass since it might receive mixed
            // children directly when document body is a single inline.
            FixupAnonymousBlocks(root);
        }
        return root;
    }

    /// <summary>Recursive worker — emits one principal box per element and its
    /// descendants. Returns <see langword="null"/> when the element has
    /// <c>display: none</c> (caller skips the attach).</summary>
    private static Box? BuildElement(
        IElement element,
        ComputedStyle parentStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics)
    {
        // Compute this element's style.
        var style = ComputedStyle.Rent();
        ApplyDefaults(style);
        ApplyInheritance(style, parentStyle);
        ApplyResolvedDeclarations(style, cascade.TryGetStylesFor(element), element, diagnostics);

        // Determine the box kind from the computed display.
        var displayText = ReadDisplay(style, element);
        var mapResult = DisplayMapper.Map(displayText, element.LocalName, out var kind);
        switch (mapResult)
        {
            case DisplayMapper.DisplayMappingResult.None:
                style.Dispose();   // no-op if box-owned, but we never attached the box
                return null;
            case DisplayMapper.DisplayMappingResult.Contents:
            case DisplayMapper.DisplayMappingResult.Unsupported:
                // Cycle-1 fallback: treat as block. Cycle-2 will diagnose +
                // promote-children for `contents`, and add unsupported code.
                kind = BoxKind.BlockContainer;
                break;
        }

        var box = Box.ForElement(kind, style, element);

        // ::before pseudo-element comes before the children. The cascade keys
        // pseudo-elements by their lowercase identifier WITHOUT the `::` prefix
        // (see SelectorCompiler — `PseudoElement(lower)` stores "before").
        var beforePseudo = BuildPseudo(element, "before", style, cascade, diagnostics);
        if (beforePseudo is not null) box.AppendChild(beforePseudo);

        // Children — DOM nodes in document order.
        foreach (var node in element.ChildNodes)
        {
            switch (node)
            {
                case IText text:
                    var trimmedText = text.Data;
                    // Skip pure-whitespace text nodes between block-level siblings —
                    // CSS Text 3 §4.1.2's white-space-collapse handling. Cycle 1
                    // is conservative: only skip when the text is empty or all
                    // ASCII whitespace AND the element is not in a preserve-
                    // whitespace context. We don't know the context yet so simply
                    // emit a TextRun for non-empty text (full handling is
                    // Phase 3's line-layout job).
                    if (trimmedText.Length > 0)
                    {
                        box.AppendChild(Box.TextRun(trimmedText, style, element));
                    }
                    break;
                case IElement childElement:
                    var childBox = BuildElement(childElement, style, cascade, diagnostics);
                    if (childBox is not null) box.AppendChild(childBox);
                    break;
                // Comment / CDATA / etc. are ignored for box generation.
            }
        }

        // ::after pseudo-element comes after the children. Same cascade-key
        // convention: lowercase ident without the `::` prefix.
        var afterPseudo = BuildPseudo(element, "after", style, cascade, diagnostics);
        if (afterPseudo is not null) box.AppendChild(afterPseudo);

        // Anonymous-block insertion fixup per Display L3 §3.1.
        FixupAnonymousBlocks(box);

        return box;
    }

    /// <summary>Generate the box for a pseudo-element rule set when one is
    /// registered + has a non-<c>none</c> <c>content</c> property. Cycle 1
    /// emits a single text run for string-valued <c>content</c>; counter() /
    /// attr() / image() are deferred to cycle 2.</summary>
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

        var contentText = contentDecl.ResolvedValue.Trim();
        if (contentText.Length == 0
            || contentText.Equals("none", StringComparison.OrdinalIgnoreCase)
            || contentText.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            // `none` suppresses the pseudo entirely; `normal` per Pseudo L4
            // §3.1 also produces no content for ::before / ::after.
            return null;
        }

        // Build the pseudo's style by inheriting from the host + applying its
        // resolved declarations.
        var pseudoStyle = ComputedStyle.Rent();
        ApplyDefaults(pseudoStyle);
        ApplyInheritance(pseudoStyle, hostStyle);
        ApplyResolvedDeclarations(pseudoStyle, ruleSet, host, diagnostics);

        var pseudo = pseudoName.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? BoxPseudo.Before
            : BoxPseudo.After;
        var displayText = ReadDisplay(pseudoStyle, host);
        var map = DisplayMapper.Map(displayText, host.LocalName, out var kind);
        if (map == DisplayMapper.DisplayMappingResult.None)
        {
            pseudoStyle.Dispose();
            return null;
        }
        if (map != DisplayMapper.DisplayMappingResult.Resolved)
        {
            kind = BoxKind.InlineBox;  // pseudo default
        }

        var pseudoBox = Box.ForPseudo(kind, pseudoStyle, host, pseudo);

        // Cycle-1 content: plain string content. The CSS Content 3 §2 production
        // permits `<string>` as one alternative — strip quotes if present.
        var generatedText = StripQuotes(contentText);
        if (generatedText.Length > 0)
        {
            pseudoBox.AppendChild(Box.TextRun(generatedText, pseudoStyle, host));
        }
        return pseudoBox;
    }

    /// <summary>Inserts <see cref="BoxKind.AnonymousBlock"/> wrappers around
    /// contiguous inline-level child runs whenever <paramref name="parent"/> has
    /// at least one block-level child. Per CSS Display L3 §3.1: "If a block
    /// container has any block-level children, all of its in-flow inline-level
    /// child boxes (including text) are wrapped together inside an anonymous
    /// block box." Operates in-place on <see cref="Box.Children"/>.</summary>
    private static void FixupAnonymousBlocks(Box parent)
    {
        if (!parent.IsBlockLevel) return;
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

        // Snapshot the current children, clear, then re-attach in passes:
        // contiguous inline runs become AnonymousBlock children; block children
        // come through as-is.
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
                // Prefer typed slot; fall back to deferred raw text.
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
    /// <see cref="PropertyResolverDispatch.Resolve"/> +
    /// <see cref="ResolverResult.MaterializeInto"/>. Properties unknown to
    /// the registry are skipped (they emitted a <c>CSS-PROPERTY-UNKNOWN-001</c>
    /// upstream during parsing if needed).</summary>
    private static void ApplyResolvedDeclarations(
        ComputedStyle style,
        ResolvedRuleSet? ruleSet,
        IElement element,
        ICssDiagnosticsSink? diagnostics)
    {
        if (ruleSet is null) return;
        foreach (var winner in ruleSet.Winners)
        {
            if (!PropertyMetadata.NameToId.TryGetValue(winner.Property, out var id))
                continue;
            var result = PropertyResolverDispatch.Resolve(id, winner.ResolvedValue, diagnostics);
            result.MaterializeInto(style, id);
        }
    }

    /// <summary>Strip surrounding quotes from a CSS string-valued <c>content</c>
    /// declaration. Cycle 1 handles only the simplest case: a single quoted
    /// string. Cycle 2 will parse the full <c>content-list</c> production
    /// (concatenation, counter(), attr(), image()).</summary>
    private static string StripQuotes(string text)
    {
        if (text.Length < 2) return text;
        var first = text[0];
        var last = text[^1];
        if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
        {
            return text[1..^1];
        }
        return text;
    }
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AngleSharp.Dom;

namespace NetPdf.Layout.Semantic;

/// <summary>
/// A node in the semantic tree — the structural / accessibility view of the
/// HTML document, parallel to the box tree's visual / layout view. Future
/// PDF/UA tagged-PDF emission (post-v1) walks this tree to produce the
/// /StructTreeRoot per ISO 32000-2 §14.7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built by <see cref="SemanticTreeBuilder"/>.</b> Reference identity is
/// meaningful (parent pointers + mutable children mid-build) so this is a
/// sealed class, not a record. After the builder returns, the tree is treated
/// as immutable for consumers.
/// </para>
/// <para>
/// <b>Relationship to <see cref="Boxes.Box"/>.</b> The semantic tree is
/// independent of box generation — it walks the styled DOM directly without
/// consulting cascade output. v1.1 will add a <c>SemanticId</c> back-reference
/// on <see cref="Boxes.Box"/> so paint code can attach text runs / images /
/// vector paths to their semantic role for tagged emission.
/// </para>
/// </remarks>
internal sealed class SemanticNode
{
    /// <summary>The semantic role this node represents.</summary>
    public SemanticKind Kind { get; }

    /// <summary>The DOM element that originated this node, or
    /// <see langword="null"/> for the synthetic <see cref="SemanticKind.Document"/>
    /// root. Carrying the back-reference lets diagnostics and post-walk passes
    /// re-enter the DOM (e.g., to re-read attributes that may change in cycle 2's
    /// dynamic resolver).</summary>
    public IElement? SourceElement { get; }

    /// <summary>The hyperlink target for <see cref="SemanticKind.Link"/> nodes,
    /// taken from the <c>href</c> attribute. <see langword="null"/> for every
    /// other kind.</summary>
    public string? Href { get; }

    /// <summary>The accessible-name text for image / figure nodes — taken in
    /// priority order from the <c>alt</c> attribute (<see cref="SemanticKind.Image"/>),
    /// the <c>aria-label</c> attribute, or the child <c>&lt;figcaption&gt;</c>'s
    /// text content (<see cref="SemanticKind.Figure"/>). <see langword="null"/>
    /// when no accessible name applies (or none of the resolution sources
    /// supplied one).</summary>
    public string? AltText { get; }

    /// <summary>The text content this node carries — populated for "leaf-ish"
    /// kinds (headings, paragraphs, list items, table cells, links, etc.) so
    /// downstream consumers don't have to walk the DOM again to reconstruct
    /// the readable text. Empty string for container kinds (Document, List,
    /// Table, Section family).</summary>
    /// <remarks>
    /// Cycle-1 captures the entire subtree text (via AngleSharp's
    /// <c>IElement.TextContent</c>), so a paragraph containing a link
    /// will surface the link's text in both the paragraph's
    /// <see cref="Text"/> AND the nested <see cref="SemanticKind.Link"/>
    /// node's <see cref="Text"/>. Phase 5 paint chooses the appropriate
    /// granularity by traversing the tree depth-first; double-counting is a
    /// known cycle-1 limitation that cycle 2 will resolve by interleaving
    /// text spans with semantic children.
    /// </remarks>
    public string Text { get; }

    /// <summary>The immediate children in document order. Backed by a
    /// <see cref="ReadOnlyCollection{T}"/> wrapper so consumers can't mutate
    /// the tree post-build.</summary>
    public ReadOnlyCollection<SemanticNode> Children { get; }

    private readonly List<SemanticNode> _children = new();

    public SemanticNode(
        SemanticKind kind,
        IElement? sourceElement = null,
        string? href = null,
        string? altText = null,
        string? text = null)
    {
        Kind = kind;
        SourceElement = sourceElement;
        Href = href;
        AltText = altText;
        Text = text ?? string.Empty;
        Children = new ReadOnlyCollection<SemanticNode>(_children);
    }

    /// <summary>Append <paramref name="child"/> as the last child of this
    /// node. Only called by <see cref="SemanticTreeBuilder"/> during
    /// construction.</summary>
    internal void AppendChild(SemanticNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
    }

    /// <summary>Total descendant count (recursive). Used by tests + debugging
    /// utilities; not meant for hot-path traversal.</summary>
    public int CountDescendants()
    {
        var total = 0;
        foreach (var child in _children)
        {
            total += 1 + child.CountDescendants();
        }
        return total;
    }
}

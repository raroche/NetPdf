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
    /// text content (<see cref="SemanticKind.Figure"/>).</summary>
    /// <remarks>
    /// <b>Tri-state semantics</b> per Task 15 review Rec 6 + HTML5 §4.8.3 +
    /// WAI-ARIA accessible-name computation:
    /// <list type="bullet">
    ///   <item><see langword="null"/> — no source supplied a name (no <c>alt</c>
    ///     attribute, no <c>aria-label</c>, no <c>&lt;figcaption&gt;</c>).
    ///     Phase-5 paint should flag this as a broken image / synthesize a
    ///     name from the filename.</item>
    ///   <item>Empty string (<c>""</c>) with <see cref="HasExplicitDecorativeAlt"/>
    ///     <see langword="true"/> — the author marked the image decorative
    ///     via <c>alt=""</c>. Phase-5 paint should emit the image as an
    ///     <c>/Artifact</c> per PDF/UA §7.1.</item>
    ///   <item>Non-empty string — the accessible name to render.</item>
    /// </list>
    /// </remarks>
    public string? AltText { get; }

    /// <summary>Per Task 15 review Rec 6 — distinguishes the missing-alt
    /// case (<c>&lt;img&gt;</c> with no <c>alt</c> attribute, broken
    /// accessibility) from the explicit-decorative case (<c>&lt;img alt=""&gt;</c>
    /// per HTML5 §4.8.3 — author intentionally marked the image as not
    /// contributing accessible text). <see langword="true"/> only when the
    /// HTML originally had <c>alt=""</c>; <see langword="false"/> when
    /// <c>alt</c> was missing entirely OR <c>alt</c> had a non-empty value.</summary>
    public bool HasExplicitDecorativeAlt { get; }

    /// <summary>Per Task 15 review Rec 5 — table-cell metadata
    /// (<c>rowspan</c> / <c>colspan</c> / <c>scope</c> / <c>headers</c> /
    /// <c>abbr</c>) for <see cref="SemanticKind.TableHeaderCell"/> +
    /// <see cref="SemanticKind.TableCell"/> nodes. <see langword="null"/>
    /// for every other kind.</summary>
    public TableCellMetadata? Cell { get; }

    /// <summary>Text content carried by this node. Per Task 15 review Rec 7,
    /// only <see cref="SemanticKind.InlineText"/> nodes populate this with
    /// non-empty text — every other kind carries its readable text via
    /// child <see cref="SemanticKind.InlineText"/> spans interleaved with
    /// nested semantic structure. This avoids the previous cycle's
    /// double-tagging where a paragraph and its child link both stored
    /// the link's text.</summary>
    /// <remarks>
    /// To compute the readable text of an arbitrary node (for accessibility
    /// names, summary strings, etc.), use <see cref="AggregateText"/> —
    /// it walks descendants depth-first and concatenates each leaf
    /// <see cref="SemanticKind.InlineText"/>'s text in document order.
    /// </remarks>
    public string Text { get; }

    /// <summary>Per Task 15 review Rec 7 — depth-first concatenation of every
    /// leaf <see cref="SemanticKind.InlineText"/> descendant's
    /// <see cref="Text"/>, in document order. The single source of truth
    /// for "what does this subtree read as" without storing redundant
    /// aggregates on container nodes.</summary>
    public string AggregateText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            AppendInto(this, sb);
            return sb.ToString();
            static void AppendInto(SemanticNode n, System.Text.StringBuilder sb)
            {
                if (n._children.Count == 0)
                {
                    sb.Append(n.Text);
                    return;
                }
                foreach (var c in n._children) AppendInto(c, sb);
            }
        }
    }

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
        bool hasExplicitDecorativeAlt = false,
        TableCellMetadata? cell = null,
        string? text = null)
    {
        Kind = kind;
        SourceElement = sourceElement;
        Href = href;
        AltText = altText;
        HasExplicitDecorativeAlt = hasExplicitDecorativeAlt;
        Cell = cell;
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

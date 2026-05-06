// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// A node in the box tree — the structural product of CSS box generation per
/// Display L3 §1, ready for Phase 3 layout to assign positions and dimensions.
/// One <see cref="Box"/> per principal box, anonymous box, line box, text run,
/// or pseudo-element box. Reference identity is meaningful (parent pointers,
/// mutable child list) so this is a sealed class, not a record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Boxes are constructed by <c>BoxBuilder</c> (Task 12) walking
/// the DOM + cascade output, mutated during table fixup + anonymous-box insertion,
/// then handed to Phase 3 layout. After layout begins, the structural fields
/// (<see cref="Kind"/>, <see cref="SourceElement"/>, etc.) are immutable from the
/// box's point of view — Phase 3 mutates layout-time fields that don't exist yet
/// in cycle 1.
/// </para>
/// <para>
/// <b>Style sharing.</b> Multiple boxes may share the same
/// <see cref="ComputedStyle"/> instance — anonymous boxes inherit their style
/// from the parent, line boxes inherit from the establishing block, text runs
/// inherit from the containing inline. The pool that owns the
/// <see cref="ComputedStyle"/> outlives the box tree (it's the cascade's pool).
/// </para>
/// <para>
/// <b>Children storage.</b> A <see cref="List{T}"/> rather than a doubly-linked
/// list — Phase 3 layout walks children sequentially in document order, and the
/// table-fixup + anonymous-block-insertion algorithms in Tasks 12+13 do random-
/// access splices. Sibling navigation is via <c>Parent.Children[index ± 1]</c>;
/// no explicit prev/next pointers (avoids the doubly-linked-invariant
/// maintenance burden).
/// </para>
/// <para>
/// <b>Anonymous boxes</b> have <see cref="SourceElement"/> = <see langword="null"/>
/// and <see cref="Pseudo"/> = <see cref="BoxPseudo.None"/>. Pseudo-element boxes
/// have a non-null <see cref="SourceElement"/> (the originating element) plus a
/// non-<see cref="BoxPseudo.None"/> <see cref="Pseudo"/>. Plain element boxes
/// have a non-null <see cref="SourceElement"/> + <see cref="BoxPseudo.None"/>.
/// </para>
/// </remarks>
internal sealed class Box
{
    /// <summary>The box's layout role per <see cref="BoxKind"/>.</summary>
    public BoxKind Kind { get; }

    /// <summary>The element that generated this box. <see langword="null"/> for
    /// anonymous boxes (line boxes, anonymous-block / anonymous-inline insertions,
    /// the root box).</summary>
    public IElement? SourceElement { get; }

    /// <summary>The pseudo-element designation when this box represents
    /// <c>::before</c> / <c>::after</c> / <c>::marker</c> / <c>::first-line</c> /
    /// <c>::first-letter</c> content. <see cref="BoxPseudo.None"/> for ordinary
    /// element boxes and anonymous boxes.</summary>
    public BoxPseudo Pseudo { get; }

    /// <summary>The computed style applied to this box. Anonymous boxes share
    /// their parent's <see cref="ComputedStyle"/>; line boxes share the
    /// establishing block's; text runs share the containing inline's.</summary>
    public ComputedStyle Style { get; }

    /// <summary>Text content for <see cref="BoxKind.TextRun"/> boxes. Empty for
    /// every other kind. Phase 3 segments + shapes + breaks this text per Text
    /// 3 + UAX #14 (line break) + UAX #29 (segmentation).</summary>
    public string Text { get; }

    /// <summary>The parent box. <see langword="null"/> only for the
    /// <see cref="BoxKind.Root"/> box.</summary>
    public Box? Parent { get; private set; }

    /// <summary>The immediate children in document order. Mutable during box
    /// generation + table fixup; logically immutable once Phase 3 layout begins.
    /// Use <see cref="AppendChild"/> + <see cref="RemoveChild"/> rather than
    /// mutating the underlying list directly so the parent pointer stays in sync.</summary>
    public IReadOnlyList<Box> Children => _children;

    private readonly List<Box> _children = new();

    /// <summary>Construct a box for an element (with optional pseudo
    /// designation). Most calls go through the static factories
    /// (<see cref="ForElement"/>, <see cref="Anonymous"/>, etc.).</summary>
    public Box(BoxKind kind, ComputedStyle style, IElement? sourceElement, BoxPseudo pseudo, string text)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(text);
        if (pseudo != BoxPseudo.None && sourceElement is null)
            throw new ArgumentException(
                "Pseudo-element boxes must reference their originating SourceElement.",
                nameof(sourceElement));
        Kind = kind;
        Style = style;
        SourceElement = sourceElement;
        Pseudo = pseudo;
        Text = text;
    }

    // ============================================================
    // Static factories — encode the construction patterns Task 12 needs
    // ============================================================

    /// <summary>Construct a principal box for an element. Pseudo defaults to
    /// <see cref="BoxPseudo.None"/>; <see cref="Text"/> is empty.</summary>
    public static Box ForElement(BoxKind kind, ComputedStyle style, IElement sourceElement)
    {
        ArgumentNullException.ThrowIfNull(sourceElement);
        return new Box(kind, style, sourceElement, BoxPseudo.None, string.Empty);
    }

    /// <summary>Construct a pseudo-element box. <paramref name="pseudo"/> must
    /// be non-<see cref="BoxPseudo.None"/>; <paramref name="sourceElement"/>
    /// is the originating element (NOT the pseudo itself, which has no DOM
    /// counterpart).</summary>
    public static Box ForPseudo(BoxKind kind, ComputedStyle style, IElement sourceElement, BoxPseudo pseudo)
    {
        ArgumentNullException.ThrowIfNull(sourceElement);
        if (pseudo == BoxPseudo.None)
            throw new ArgumentException(
                "Use ForElement(...) for non-pseudo boxes; ForPseudo requires a pseudo discriminator.",
                nameof(pseudo));
        return new Box(kind, style, sourceElement, pseudo, string.Empty);
    }

    /// <summary>Construct an anonymous box (line box, anonymous-block,
    /// anonymous-inline). The style is inherited from the parent — the caller
    /// passes the parent's style instance.</summary>
    public static Box Anonymous(BoxKind kind, ComputedStyle inheritedStyle)
    {
        return new Box(kind, inheritedStyle, sourceElement: null, BoxPseudo.None, string.Empty);
    }

    /// <summary>Construct a <see cref="BoxKind.TextRun"/> for the given text.
    /// The style is the containing inline's; <paramref name="sourceElement"/>
    /// is the parent element (or <see langword="null"/> when the text content
    /// has no DOM origin — e.g., generated content from <c>::before</c>).</summary>
    public static Box TextRun(string text, ComputedStyle style, IElement? sourceElement = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Box(BoxKind.TextRun, style, sourceElement, BoxPseudo.None, text);
    }

    /// <summary>Construct the single root box. Style is the synthesized
    /// initial-containing-block style; there's no source element.</summary>
    public static Box CreateRoot(ComputedStyle initialStyle)
    {
        return new Box(BoxKind.Root, initialStyle, sourceElement: null, BoxPseudo.None, string.Empty);
    }

    // ============================================================
    // Mutation — only used during box generation (Tasks 12 + 13)
    // ============================================================

    /// <summary>Append <paramref name="child"/> as the last child of this box.
    /// The child must be parent-less; double-attaching throws
    /// <see cref="InvalidOperationException"/> to catch box-generation bugs.</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="child"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When <paramref name="child"/>
    /// already has a parent or is this box.</exception>
    public void AppendChild(Box child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("Cannot make a box its own child.");
        if (child.Parent is not null)
            throw new InvalidOperationException(
                $"Box of kind {child.Kind} is already a child of a {child.Parent.Kind} parent — detach via RemoveChild first.");
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>Insert <paramref name="child"/> at <paramref name="index"/>.
    /// Used by table fixup + anonymous-box insertion which need positional
    /// inserts, not just appends.</summary>
    public void InsertChild(int index, Box child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if ((uint)index > (uint)_children.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Index {index} out of range for {_children.Count} children.");
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("Cannot make a box its own child.");
        if (child.Parent is not null)
            throw new InvalidOperationException(
                $"Box of kind {child.Kind} is already a child of a {child.Parent.Kind} parent — detach via RemoveChild first.");
        child.Parent = this;
        _children.Insert(index, child);
    }

    /// <summary>Detach <paramref name="child"/>. Throws if the box isn't a
    /// child of this parent.</summary>
    /// <exception cref="ArgumentException">When <paramref name="child"/> isn't
    /// in <see cref="Children"/>.</exception>
    public void RemoveChild(Box child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!ReferenceEquals(child.Parent, this))
            throw new ArgumentException(
                "Box is not a child of this parent.", nameof(child));
        _children.Remove(child);
        child.Parent = null;
    }

    // ============================================================
    // Convenience predicates over BoxKind
    // ============================================================

    /// <summary><see langword="true"/> when this box generates a block-level
    /// outer display per Display L3 §2.4. Phase 3 BFC code dispatches on this.</summary>
    public bool IsBlockLevel => Kind switch
    {
        BoxKind.Root or BoxKind.BlockContainer or BoxKind.ListItem
            or BoxKind.AnonymousBlock or BoxKind.Table or BoxKind.FlexContainer
            or BoxKind.GridContainer => true,
        _ => false,
    };

    /// <summary><see langword="true"/> when this box participates in an inline
    /// formatting context per Inline L3 §2.</summary>
    public bool IsInlineLevel => Kind switch
    {
        BoxKind.InlineBox or BoxKind.AtomicInline or BoxKind.TextRun
            or BoxKind.AnonymousInline => true,
        _ => false,
    };

    /// <summary><see langword="true"/> when this box was synthesized by box
    /// generation (no DOM origin, no pseudo designation).</summary>
    public bool IsAnonymous => SourceElement is null && Pseudo == BoxPseudo.None && Kind != BoxKind.Root;

    /// <summary><see langword="true"/> when this box represents a CSS pseudo-element.</summary>
    public bool IsPseudoElement => Pseudo != BoxPseudo.None;

    /// <summary><see langword="true"/> when this box is part of the table model
    /// per Tables L3 §2 (including the wrapper, row groups, rows, cells,
    /// columns, captions).</summary>
    public bool IsTablePart => Kind switch
    {
        BoxKind.Table or BoxKind.TableRowGroup or BoxKind.TableHeaderGroup
            or BoxKind.TableFooterGroup or BoxKind.TableRow or BoxKind.TableCell
            or BoxKind.TableColumnGroup or BoxKind.TableColumn or BoxKind.TableCaption => true,
        _ => false,
    };

    /// <summary>The first child or <see langword="null"/> if this box has none.</summary>
    public Box? FirstChild => _children.Count > 0 ? _children[0] : null;

    /// <summary>The last child or <see langword="null"/> if this box has none.</summary>
    public Box? LastChild => _children.Count > 0 ? _children[^1] : null;

    /// <summary>Total descendant count (recursive). Used by snapshot tests +
    /// debugging utilities; not meant for hot-path traversal.</summary>
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

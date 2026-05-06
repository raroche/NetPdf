// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// <b>ComputedStyle ownership (Task 11 hardening Rec 6).</b> The cascade rents
/// <see cref="ComputedStyle"/> instances from a process-wide pool. When a
/// <see cref="Box"/> attaches to a style, the constructor calls
/// <see cref="ComputedStyle.MarkAsBoxOwned"/> which causes
/// <see cref="ComputedStyle.Dispose"/> to skip pool re-rental — otherwise the
/// pool could re-rent the same instance to another caller and clear it,
/// silently corrupting the box tree's view. Cycle-2 / Phase 3 will add a
/// box-tree disposal sweep that releases ownership when the tree is discarded;
/// for cycle 1 the styles live for the process lifetime.
/// </para>
/// <para>
/// <b>Style sharing.</b> Multiple boxes may share the same
/// <see cref="ComputedStyle"/> instance — anonymous boxes inherit their style
/// from the parent, line boxes inherit from the establishing block, text runs
/// inherit from the containing inline. Marking the same style as box-owned
/// from multiple boxes is idempotent.
/// </para>
/// <para>
/// <b>Children storage.</b> A <see cref="List{T}"/> is the actual backing store
/// for random-access splices that table-fixup + anonymous-block-insertion need.
/// <see cref="Children"/> exposes a <see cref="ReadOnlyCollection{T}"/> wrapper
/// (Task 11 hardening Rec 3) so consumers cannot bypass the parent-pointer
/// invariant by casting to <see cref="IList{T}"/>. Sibling navigation uses
/// <c>Parent.Children[index ± 1]</c>; no explicit prev/next pointers.
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
    /// table grid boxes, the root box).</summary>
    public IElement? SourceElement { get; }

    /// <summary>The pseudo-element designation when this box represents
    /// <c>::before</c> / <c>::after</c> / <c>::marker</c> content.
    /// <see cref="BoxPseudo.None"/> for ordinary element boxes and anonymous
    /// boxes. (<c>::first-line</c> + <c>::first-letter</c> are layout-time
    /// fragment styling, not box-generation pseudos — see <see cref="BoxPseudo"/>.)</summary>
    public BoxPseudo Pseudo { get; }

    /// <summary>The computed style applied to this box. Anonymous boxes share
    /// their parent's <see cref="ComputedStyle"/>; line boxes share the
    /// establishing block's; text runs share the containing inline's.
    /// Box-owned per <see cref="ComputedStyle.MarkAsBoxOwned"/> so the pool
    /// won't re-rent the instance while this box is alive.</summary>
    public ComputedStyle Style { get; }

    /// <summary>Text content for <see cref="BoxKind.TextRun"/> boxes. Empty for
    /// every other kind (the constructor enforces this — only TextRun may carry
    /// non-empty text). Phase 3 segments + shapes + breaks this text per
    /// CSS Text 3 + UAX #14 (line break) + UAX #29 (segmentation).</summary>
    public string Text { get; }

    /// <summary>The parent box. <see langword="null"/> only for the
    /// <see cref="BoxKind.Root"/> box.</summary>
    public Box? Parent { get; private set; }

    /// <summary>The immediate children in document order. Backed by a
    /// <see cref="ReadOnlyCollection{T}"/> wrapper (Task 11 hardening Rec 3) —
    /// casting to <see cref="IList{T}"/> + mutating throws
    /// <see cref="NotSupportedException"/>. Use <see cref="AppendChild"/> /
    /// <see cref="InsertChild"/> / <see cref="RemoveChild"/> to mutate so the
    /// parent pointer stays in sync.</summary>
    public ReadOnlyCollection<Box> Children { get; }

    private readonly List<Box> _children = new();

    /// <summary>Construct a box. Most call sites go through the static
    /// factories (<see cref="ForElement"/>, <see cref="ForPseudo"/>,
    /// <see cref="Anonymous"/>, <see cref="TextRun"/>, <see cref="CreateRoot"/>)
    /// rather than calling this directly.</summary>
    /// <remarks>
    /// Defensive invariants enforced (Task 11 hardening Rec 4):
    /// <list type="bullet">
    ///   <item><see cref="BoxKind.Root"/> requires no source element + no pseudo
    ///     (use <see cref="CreateRoot"/>).</item>
    ///   <item><see cref="BoxKind.LineBox"/> / <see cref="BoxKind.AnonymousBlock"/>
    ///     / <see cref="BoxKind.AnonymousInline"/> / <see cref="BoxKind.TableGrid"/>
    ///     are always anonymous — no source element + no pseudo.</item>
    ///   <item><see cref="BoxPseudo.Marker"/> requires <see cref="BoxKind.Marker"/>.
    ///     The reverse is NOT enforced — <see cref="BoxKind.Marker"/> also
    ///     represents default list-item markers (Lists L3 §3.1) which carry
    ///     <see cref="BoxPseudo.None"/>. Invariant is one-way: pseudo→kind.</item>
    ///   <item><see cref="BoxPseudo.Before"/> / <see cref="BoxPseudo.After"/>
    ///     pseudos require a source element + must NOT pair with kinds that
    ///     are inherently anonymous.</item>
    ///   <item>Non-empty <see cref="Text"/> only allowed on
    ///     <see cref="BoxKind.TextRun"/> (use <see cref="TextRun"/> factory).</item>
    /// </list>
    /// </remarks>
    public Box(BoxKind kind, ComputedStyle style, IElement? sourceElement, BoxPseudo pseudo, string text)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(text);
        ValidateInvariants(kind, sourceElement, pseudo, text);
        Kind = kind;
        Style = style;
        SourceElement = sourceElement;
        Pseudo = pseudo;
        Text = text;
        Children = new ReadOnlyCollection<Box>(_children);
        // Box-ownership marker so the cascade pool won't recycle this style
        // while the box is alive (Task 11 hardening Rec 6).
        style.MarkAsBoxOwned();
    }

    private static void ValidateInvariants(
        BoxKind kind, IElement? sourceElement, BoxPseudo pseudo, string text)
    {
        // Always-anonymous kinds: no source, no pseudo.
        if (IsAlwaysAnonymous(kind))
        {
            if (sourceElement is not null)
                throw new ArgumentException(
                    $"{kind} is always anonymous; cannot attach a SourceElement.",
                    nameof(sourceElement));
            if (pseudo != BoxPseudo.None)
                throw new ArgumentException(
                    $"{kind} is always anonymous; cannot carry a pseudo designation.",
                    nameof(pseudo));
        }

        // Pseudo-element boxes always reference their originating element.
        if (pseudo != BoxPseudo.None && sourceElement is null)
            throw new ArgumentException(
                "Pseudo-element boxes must reference their originating SourceElement.",
                nameof(sourceElement));

        // Marker pseudo (`::marker`) must pair with Marker kind. The reverse
        // does NOT hold — BoxKind.Marker is also used for default list-item
        // markers (Lists L3 §3.1) which carry BoxPseudo.None, not Marker.
        // The invariant is one-way: pseudo→kind only.
        if (pseudo == BoxPseudo.Marker && kind != BoxKind.Marker)
            throw new ArgumentException(
                "Marker pseudo must pair with BoxKind.Marker.",
                nameof(pseudo));

        // Non-empty text only on TextRun.
        if (text.Length > 0 && kind != BoxKind.TextRun)
            throw new ArgumentException(
                $"Non-empty Text is only allowed on BoxKind.TextRun; got {kind}.",
                nameof(text));
    }

    /// <summary><see langword="true"/> for kinds that are always anonymous by
    /// construction (no DOM source element, no pseudo designation).</summary>
    private static bool IsAlwaysAnonymous(BoxKind kind) => kind switch
    {
        BoxKind.Root or BoxKind.LineBox or BoxKind.AnonymousBlock
            or BoxKind.AnonymousInline or BoxKind.TableGrid => true,
        _ => false,
    };

    // ============================================================
    // Static factories — encode the construction patterns Task 12 needs
    // ============================================================

    /// <summary>Construct a principal box for an element. Pseudo defaults to
    /// <see cref="BoxPseudo.None"/>; <see cref="Text"/> is empty. Throws if
    /// <paramref name="kind"/> is an always-anonymous kind.</summary>
    public static Box ForElement(BoxKind kind, ComputedStyle style, IElement sourceElement)
    {
        ArgumentNullException.ThrowIfNull(sourceElement);
        return new Box(kind, style, sourceElement, BoxPseudo.None, string.Empty);
    }

    /// <summary>Construct a pseudo-element box. <paramref name="pseudo"/> must
    /// be non-<see cref="BoxPseudo.None"/>; <paramref name="sourceElement"/>
    /// is the originating element. <see cref="BoxPseudo.Marker"/> requires
    /// <see cref="BoxKind.Marker"/>.</summary>
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
    /// anonymous-inline, table-grid). The style is inherited from the
    /// parent — the caller passes the parent's style instance. Throws if
    /// <paramref name="kind"/> is NOT an always-anonymous kind.</summary>
    public static Box Anonymous(BoxKind kind, ComputedStyle inheritedStyle)
    {
        if (!IsAlwaysAnonymous(kind))
            throw new ArgumentException(
                $"{kind} is not an always-anonymous kind. Use ForElement / ForPseudo / TextRun for kinds that may have a source.",
                nameof(kind));
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
    /// The child must be parent-less, must not be this box, and must not be an
    /// ancestor of this box (Task 11 hardening Rec 2 — prevents accidental
    /// cycles via ancestor reattachment).</summary>
    public void AppendChild(Box child)
    {
        ArgumentNullException.ThrowIfNull(child);
        EnsureAttachable(child);
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
        EnsureAttachable(child);
        child.Parent = this;
        _children.Insert(index, child);
    }

    /// <summary>Detach <paramref name="child"/>. Throws if the box isn't a
    /// child of this parent.</summary>
    public void RemoveChild(Box child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!ReferenceEquals(child.Parent, this))
            throw new ArgumentException(
                "Box is not a child of this parent.", nameof(child));
        _children.Remove(child);
        child.Parent = null;
    }

    /// <summary>Shared validation for AppendChild + InsertChild. Per Task 11
    /// hardening Rec 2, this rejects not just self-attach + double-attach but
    /// any attempt to make an ancestor a descendant — a cycle that would slip
    /// past the simple parent-null check when the ancestor is the root.</summary>
    private void EnsureAttachable(Box child)
    {
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("Cannot make a box its own child.");
        if (child.Parent is not null)
            throw new InvalidOperationException(
                $"Box of kind {child.Kind} is already a child of a {child.Parent.Kind} parent — detach via RemoveChild first.");
        // Walk up from this box; if we encounter `child`, attaching child here
        // would create a cycle (child becomes its own descendant).
        var ancestor = Parent;
        while (ancestor is not null)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException(
                    $"Cycle detected: cannot attach a {child.Kind} box as a child of its descendant.");
            ancestor = ancestor.Parent;
        }
    }

    // ============================================================
    // Convenience predicates over BoxKind
    // ============================================================

    /// <summary><see langword="true"/> when this box generates a block-level
    /// outer display per Display L3 §2. Phase 3 BFC code dispatches on this.</summary>
    public bool IsBlockLevel => Kind switch
    {
        BoxKind.Root or BoxKind.BlockContainer or BoxKind.ListItem
            or BoxKind.AnonymousBlock or BoxKind.Table or BoxKind.FlexContainer
            or BoxKind.GridContainer or BoxKind.BlockReplacedElement => true,
        _ => false,
    };

    /// <summary><see langword="true"/> when this box participates in an inline
    /// formatting context per Inline L3 §2 (atomic or non-atomic).
    /// <see cref="BoxKind.LineBreak"/> counts as inline-level — it appears
    /// inline alongside other inline content and just forces a line break.</summary>
    public bool IsInlineLevel => Kind switch
    {
        BoxKind.InlineBox or BoxKind.InlineBlockContainer
            or BoxKind.InlineFlexContainer or BoxKind.InlineGridContainer
            or BoxKind.InlineTable or BoxKind.InlineReplacedElement
            or BoxKind.AnonymousInline or BoxKind.TextRun
            or BoxKind.LineBreak => true,
        _ => false,
    };

    /// <summary><see langword="true"/> when this box is atomic to line layout
    /// — an inline-level box whose inner content lays out independently
    /// (inline-block, inline-flex, inline-grid, inline-table, inline replaced).</summary>
    public bool IsAtomicInline => Kind switch
    {
        BoxKind.InlineBlockContainer or BoxKind.InlineFlexContainer
            or BoxKind.InlineGridContainer or BoxKind.InlineTable
            or BoxKind.InlineReplacedElement => true,
        _ => false,
    };

    /// <summary><see langword="true"/> when this box is a replaced element
    /// (block- or inline-level).</summary>
    public bool IsReplaced =>
        Kind is BoxKind.BlockReplacedElement or BoxKind.InlineReplacedElement;

    /// <summary><see langword="true"/> when this box was synthesized by box
    /// generation (no DOM origin, no pseudo designation, not the root).</summary>
    public bool IsAnonymous =>
        SourceElement is null && Pseudo == BoxPseudo.None && Kind != BoxKind.Root;

    /// <summary><see langword="true"/> when this box represents a CSS pseudo-element.</summary>
    public bool IsPseudoElement => Pseudo != BoxPseudo.None;

    /// <summary><see langword="true"/> when this box is part of the table
    /// model per Tables L3 §2 — wrapper, grid, row groups, rows, cells,
    /// columns, captions.</summary>
    public bool IsTablePart => Kind switch
    {
        BoxKind.Table or BoxKind.InlineTable or BoxKind.TableGrid
            or BoxKind.TableRowGroup or BoxKind.TableHeaderGroup
            or BoxKind.TableFooterGroup or BoxKind.TableRow or BoxKind.TableCell
            or BoxKind.TableColumnGroup or BoxKind.TableColumn
            or BoxKind.TableCaption => true,
        _ => false,
    };

    /// <summary><see langword="true"/> when this box is a table wrapper —
    /// either block-level (<see cref="BoxKind.Table"/>) or inline-level
    /// (<see cref="BoxKind.InlineTable"/>). Wrappers carry the source element
    /// + outer-display semantics; their inner <see cref="BoxKind.TableGrid"/>
    /// child runs the actual table layout per Tables L3 §2.1.</summary>
    public bool IsTableWrapper =>
        Kind is BoxKind.Table or BoxKind.InlineTable;

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

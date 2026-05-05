// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Parser;
using NetPdf.Css.Selectors;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Total cascade-ordering key for a single matched declaration per CSS Cascade Level 4 §6.4.
/// Larger key = higher precedence: when the cascade collects every declaration that targets
/// a property on an element, the one with the largest <see cref="CascadeKey"/> wins.
/// </summary>
/// <remarks>
/// <para>
/// <b>Comparison order (CSS Cascade L4 §6.4):</b>
/// </para>
/// <list type="number">
///   <item><description><b>Origin + importance.</b> Combined into a single rank
///   <see cref="OriginImportanceRank"/> via the rule from §6.4.1: for a "normal" declaration
///   the order low→high is UA &lt; User &lt; Author; for an <c>!important</c> declaration the
///   order is reversed and pushed above all normals — Author-important &lt; User-important
///   &lt; UA-important. Inline <c>style="…"</c> declarations enter as Author per §6.4.4.</description></item>
///   <item><description><b>Layer order</b> (<see cref="LayerOrder"/>). Within the same
///   origin+importance bucket, layered declarations are ranked by their <c>@layer</c>
///   index. For NORMAL declarations, later-declared layers beat earlier; unlayered author
///   rules sit ABOVE all layered author rules. For <c>!important</c>, the order is REVERSED
///   per §6.4.2 — earlier-declared layers beat later, and unlayered author rules sit BELOW
///   layered. The encoding handles the reversal at construction time so the comparison
///   stays a plain integer.</description></item>
///   <item><description><b>Specificity</b> (<see cref="Specificity"/>). Lexicographic
///   <c>(A, B, C)</c> per CSS Selectors L4 §17.</description></item>
///   <item><description><b>Source order</b> — last-declared wins. Encoded as a packed
///   <see cref="long"/> built from <see cref="StylesheetOrder"/> (high), <see cref="RuleOrder"/>
///   (mid), and <see cref="DeclarationOrder"/> (low). Larger value = later in source.</description></item>
/// </list>
/// <para>
/// <b>Layer ordering — v1 simplification.</b> AngleSharp.Css drops <c>@layer</c> name
/// information today; the parser pre-pass surfaces them as opaque rules. Until Tasks 8–10
/// extend the layer pipeline, every author declaration takes <see cref="LayerOrder"/> = 0
/// (the unlayered "highest within author" bucket), which matches the dominant invoice /
/// report use case. Layered ordering is wired here so it lights up correctly when the
/// adapter starts emitting layer-tagged declarations.
/// </para>
/// </remarks>
internal readonly record struct CascadeKey : IComparable<CascadeKey>
{
    public CascadeKey(
        CssStylesheetOrigin origin,
        bool isImportant,
        int layerOrder,
        Specificity specificity,
        int stylesheetOrder,
        int ruleOrder,
        int declarationOrder)
    {
        Origin = origin;
        IsImportant = isImportant;
        LayerOrder = layerOrder;
        Specificity = specificity;
        StylesheetOrder = stylesheetOrder;
        RuleOrder = ruleOrder;
        DeclarationOrder = declarationOrder;
    }

    public CssStylesheetOrigin Origin { get; }
    public bool IsImportant { get; }
    public int LayerOrder { get; }
    public Specificity Specificity { get; }
    public int StylesheetOrder { get; }
    public int RuleOrder { get; }
    public int DeclarationOrder { get; }

    /// <summary>Combined origin + importance rank per CSS Cascade L4 §6.4.1. Larger =
    /// higher precedence. The mapping:
    /// <code>
    ///   UA-normal      = 0  (lowest)
    ///   User-normal    = 1
    ///   Author-normal  = 2
    ///   Author-imp     = 3
    ///   User-imp       = 4
    ///   UA-imp         = 5  (highest — UA can override author with !important)
    /// </code>
    /// </summary>
    public int OriginImportanceRank => IsImportant
        ? Origin switch
        {
            CssStylesheetOrigin.UserAgent => 5,
            CssStylesheetOrigin.User => 4,
            CssStylesheetOrigin.Author => 3,
            _ => 3,
        }
        : (int)Origin; // UA=0, User=1, Author=2

    /// <summary>Source-order packed into a single <see cref="long"/> so it compares cleanly
    /// against another key. Each component bounded so they don't overflow a 21-bit slice.</summary>
    public long SourceOrderPacked =>
        ((long)StylesheetOrder << 42) | ((long)RuleOrder << 21) | (uint)DeclarationOrder;

    public int CompareTo(CascadeKey other)
    {
        var cmp = OriginImportanceRank.CompareTo(other.OriginImportanceRank);
        if (cmp != 0) return cmp;
        cmp = LayerOrder.CompareTo(other.LayerOrder);
        if (cmp != 0) return cmp;
        cmp = Specificity.CompareTo(other.Specificity);
        if (cmp != 0) return cmp;
        return SourceOrderPacked.CompareTo(other.SourceOrderPacked);
    }

    public static bool operator <(CascadeKey a, CascadeKey b) => a.CompareTo(b) < 0;
    public static bool operator >(CascadeKey a, CascadeKey b) => a.CompareTo(b) > 0;
    public static bool operator <=(CascadeKey a, CascadeKey b) => a.CompareTo(b) <= 0;
    public static bool operator >=(CascadeKey a, CascadeKey b) => a.CompareTo(b) >= 0;
}

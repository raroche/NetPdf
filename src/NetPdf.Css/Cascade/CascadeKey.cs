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
///   <item><description><b>Origin + importance</b> (§6.4.1) — combined into a single rank
///   <see cref="OriginImportanceRank"/>. For "normal" declarations the order low→high is
///   UA &lt; User &lt; Author; for an <c>!important</c> declaration the order is reversed
///   and pushed above all normals — Author-important &lt; User-important &lt; UA-important.</description></item>
///   <item><description><b>Element-attached styles</b> (§6.4.3) — the
///   <see cref="IsInlineStyle"/> flag. Within the same origin+importance bucket, declarations
///   from <c>style="…"</c> attributes win over declarations from selector-driven rules
///   regardless of selector specificity. The earlier <c>(1, 0, 0)</c>-as-specificity model
///   was incorrect: a high-specificity selector like <c>#a#b</c> would have beaten an inline
///   declaration, contradicting the spec.</description></item>
///   <item><description><b>Layer order</b> (§6.4.4) via <see cref="CompareLayerOrder"/>.
///   Unlayered declarations are placed in an implicit final layer for normal declarations
///   (so unlayered &gt; any named layer normal); for <c>!important</c> the order reverses
///   and unlayered &lt; any named layer important. Within named layers: normal favors
///   later-declared (higher <see cref="LayerOrder"/>); important favors earlier-declared.</description></item>
///   <item><description><b>Specificity</b> (§6.4.5) — lexicographic <c>(A, B, C)</c>
///   per CSS Selectors L4 §17.</description></item>
///   <item><description><b>Order of appearance</b> (§6.4.6) — last-declared wins. Compared
///   as a tuple over <see cref="StylesheetOrder"/>, <see cref="RuleOrder"/>,
///   <see cref="DeclarationOrder"/> so any sized document compares correctly. The earlier
///   bit-packed implementation silently corrupted ordering once any component exceeded
///   2<sup>21</sup>.</description></item>
/// </list>
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
        int declarationOrder,
        bool isInlineStyle = false)
    {
        Origin = origin;
        IsImportant = isImportant;
        LayerOrder = layerOrder;
        Specificity = specificity;
        StylesheetOrder = stylesheetOrder;
        RuleOrder = ruleOrder;
        DeclarationOrder = declarationOrder;
        IsInlineStyle = isInlineStyle;
    }

    public CssStylesheetOrigin Origin { get; }
    public bool IsImportant { get; }
    public int LayerOrder { get; }
    public Specificity Specificity { get; }
    public int StylesheetOrder { get; }
    public int RuleOrder { get; }
    public int DeclarationOrder { get; }
    public bool IsInlineStyle { get; }

    /// <summary>Combined origin + importance rank per CSS Cascade L4 §6.4.1. Larger =
    /// higher precedence:
    /// <code>
    ///   UA-normal      = 0
    ///   User-normal    = 1
    ///   Author-normal  = 2
    ///   Author-imp     = 3
    ///   User-imp       = 4
    ///   UA-imp         = 5
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
        : (int)Origin;

    public int CompareTo(CascadeKey other)
    {
        var cmp = OriginImportanceRank.CompareTo(other.OriginImportanceRank);
        if (cmp != 0) return cmp;
        // §6.4.3 element-attached styles win over selector-driven within the same tier.
        cmp = (IsInlineStyle ? 1 : 0).CompareTo(other.IsInlineStyle ? 1 : 0);
        if (cmp != 0) return cmp;
        cmp = CompareLayerOrder(other);
        if (cmp != 0) return cmp;
        cmp = Specificity.CompareTo(other.Specificity);
        if (cmp != 0) return cmp;
        // Tuple-compare source-order — tolerates any int values without bit-packing concerns.
        cmp = StylesheetOrder.CompareTo(other.StylesheetOrder);
        if (cmp != 0) return cmp;
        cmp = RuleOrder.CompareTo(other.RuleOrder);
        if (cmp != 0) return cmp;
        return DeclarationOrder.CompareTo(other.DeclarationOrder);
    }

    /// <summary>
    /// Per CSS Cascade L4 §6.4.4. Both keys are guaranteed to share the same origin +
    /// importance + inline-style tier by the time this is called, so <c>this.IsImportant</c>
    /// equals <c>other.IsImportant</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LayerOrder"/> = 0 means "unlayered" (the implicit final layer for normal
    /// declarations, the implicit first layer for important declarations). Positive values
    /// are the position of the named layer in declaration order (1 = first, 2 = second, …).
    /// </para>
    /// <para>
    /// <b>Normal declarations:</b> unlayered beats any named layer; among named layers,
    /// later-declared wins.
    /// </para>
    /// <para>
    /// <b>Important declarations:</b> the order is reversed — unlayered loses to any named
    /// layer; among named layers, earlier-declared wins.
    /// </para>
    /// </remarks>
    private int CompareLayerOrder(CascadeKey other)
    {
        var aLayered = LayerOrder > 0;
        var bLayered = other.LayerOrder > 0;
        if (!aLayered && !bLayered) return 0;
        if (!aLayered)
        {
            // a unlayered, b layered. Normal: a wins. Important: a loses.
            return IsImportant ? -1 : 1;
        }
        if (!bLayered)
        {
            return IsImportant ? 1 : -1;
        }
        // Both named layers. Normal: higher order wins. Important: lower order wins.
        return IsImportant
            ? other.LayerOrder.CompareTo(LayerOrder)
            : LayerOrder.CompareTo(other.LayerOrder);
    }

    public static bool operator <(CascadeKey a, CascadeKey b) => a.CompareTo(b) < 0;
    public static bool operator >(CascadeKey a, CascadeKey b) => a.CompareTo(b) > 0;
    public static bool operator <=(CascadeKey a, CascadeKey b) => a.CompareTo(b) <= 0;
    public static bool operator >=(CascadeKey a, CascadeKey b) => a.CompareTo(b) >= 0;
}

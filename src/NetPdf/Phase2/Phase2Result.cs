// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Parser;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Semantic;

namespace NetPdf.Phase2;

/// <summary>
/// The artifacts produced by one invocation of <see cref="Phase2Pipeline"/>:
/// the box tree (visual layout), the semantic tree (accessibility / structure
/// for PDF/UA), the resolved cascade (typed property values per element /
/// pseudo), and the adapted stylesheet list (kept for snapshot tests + future
/// re-runs without re-parsing).
/// </summary>
/// <remarks>
/// <para>
/// <b>Disposal (Phase 2 deep review Rec 7).</b> Every box-tree node holds a
/// <see cref="ComputedStyle"/> reference whose <see cref="ComputedStyle.IsBoxOwned"/>
/// flag was set by <c>BoxBuilder</c> so the cascade-time <c>Dispose()</c>
/// couldn't return it to the pool (premature release would let pool re-rental
/// clear the slots while the box still references them). Without an explicit
/// disposal sweep, every conversion leaks one <see cref="ComputedStyle"/>
/// per styled box / pseudo / fragment-pseudo to GC, missing the pool fast
/// path on subsequent conversions. <see cref="Dispose"/> walks the tree
/// once + calls <see cref="ComputedStyle.ReleaseFromBox"/> on every unique
/// instance (deduplicated by reference identity in case future cascade
/// optimizations share styles across siblings).
/// </para>
/// <para>
/// <b>Use pattern.</b> <c>using var result = await Phase2Pipeline.RunFromHtmlAsync(...);</c>
/// is the typical call site. After disposal, the box tree's <c>Style</c>
/// properties are stale references that should not be read (any access
/// throws <see cref="ObjectDisposedException"/> via the soft guard until
/// the pool re-rents the instance).
/// </para>
/// <para>
/// <b>Single-call contract.</b> <see cref="Dispose"/> must be called AT MOST
/// ONCE per logical result, on the original instance — NOT on a copy. This
/// is a value-type with reference fields: copying a <see cref="Phase2Result"/>
/// (e.g., <c>var snap = result;</c>, passing by value, capturing in a closure)
/// shares the box tree by reference, so disposing both the original AND a
/// copy releases the same pooled <see cref="ComputedStyle"/> instances
/// twice. Under pool churn (any other <c>Phase2Pipeline</c> call between
/// the two disposals), the second release returns now-foreign styles to
/// the bag + corrupts the other run. Per PR #13 review feedback, the
/// previous "idempotent" guarantee was unsafe — the soft-guard
/// <c>_disposed</c> flag in <see cref="ComputedStyle"/> short-circuits the
/// same-instance double-release case, but it doesn't protect against the
/// re-rented case. Treat <see cref="Dispose"/> like a real
/// <c>IDisposable</c>: call once, on the owner.
/// </para>
/// </remarks>
/// <param name="BoxRoot">The root <see cref="Box"/> from
/// <see cref="BoxBuilder.Build"/>; always <see cref="BoxKind.Root"/>.</param>
/// <param name="SemanticRoot">The root <see cref="SemanticNode"/> from
/// <see cref="SemanticTreeBuilder.Build"/>; always <see cref="SemanticKind.Document"/>.</param>
/// <param name="Cascade">The cascade output post <see cref="VarResolver"/>;
/// available so consumers can re-resolve specific elements / pseudos for
/// ad-hoc inspection.</param>
/// <param name="Sheets">The adapted stylesheets in cascade order.</param>
internal readonly record struct Phase2Result(
    Box BoxRoot,
    SemanticNode SemanticRoot,
    ResolvedCascadeResult Cascade,
    ImmutableArray<CssStylesheet> Sheets) : IDisposable
{
    /// <summary>Walks <see cref="BoxRoot"/> + releases every unique
    /// box-owned <see cref="ComputedStyle"/> back to the pool. Per the
    /// "single-call contract" in the type's <c>&lt;remarks&gt;</c>, must
    /// be called at most once per logical result; calling on a copied
    /// instance after the original has been disposed can corrupt other
    /// pipeline runs that have re-rented the same pool slots.</summary>
    public void Dispose()
    {
        if (BoxRoot is null) return;
        // ReferenceEqualityComparer dedupes shared styles so a single instance
        // referenced by N boxes is only released once.
        var seen = new HashSet<ComputedStyle>(ReferenceEqualityComparer.Instance);
        DisposeBoxStyles(BoxRoot, seen);
    }

    private static void DisposeBoxStyles(Box box, HashSet<ComputedStyle> seen)
    {
        if (box.Style is not null && seen.Add(box.Style))
            box.Style.ReleaseFromBox();
        if (box.FirstLineStyle is not null && seen.Add(box.FirstLineStyle))
            box.FirstLineStyle.ReleaseFromBox();
        if (box.FirstLetterStyle is not null && seen.Add(box.FirstLetterStyle))
            box.FirstLetterStyle.ReleaseFromBox();
        foreach (var child in box.Children)
        {
            DisposeBoxStyles(child, seen);
        }
    }
}

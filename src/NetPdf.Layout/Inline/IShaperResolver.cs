// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Text.Shaping;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 2 — resolves a <see cref="HbShaper"/>
/// for a given <see cref="ComputedStyle"/>. Cycle 2's
/// <see cref="LineBuilder.Shape"/> takes one of these per
/// <c>Itemize</c>+<c>Shape</c> call instead of constructing shapers
/// itself, so:
/// <list type="bullet">
///   <item>The line builder stays focused on bidi + itemization +
///   advancing through itemized runs; font resolution + shaper
///   caching are an injected concern.</item>
///   <item>Tests can drive the line builder with a synthetic font
///   without depending on a system font registry.</item>
///   <item>Cycle 3 / Task 10 can ship a production resolver that
///   honors <c>font-family</c> stacks + size scaling + the
///   <c>FontRegistry</c>; that resolver swaps in transparently.</item>
/// </list>
///
/// <para><b>Ownership.</b> The resolver OWNS the returned shaper —
/// callers MUST NOT dispose. The resolver itself is
/// <see cref="System.IDisposable"/> for the integrating layout
/// pipeline to drive (typically <c>using var resolver = ...</c>);
/// production resolvers cache shapers across calls + dispose them
/// all at scope exit.</para>
///
/// <para><b>Cycle 2 contract.</b> A resolver SHOULD return shapers
/// stably — calling <see cref="Resolve"/> with the same
/// <see cref="ComputedStyle"/> twice should produce the same shaper
/// instance (identity preserved). Cycle 3's wrapping pass relies on
/// this for cache locality.</para>
/// </summary>
internal interface IShaperResolver : System.IDisposable
{
    /// <summary>Resolve the shaper for the given style. Style reads
    /// of interest: <c>font-family</c>, <c>font-size</c>,
    /// <c>font-weight</c>, <c>font-style</c>, <c>font-stretch</c>.
    /// The resolver maps these to a font face + size + opens a
    /// shaper.
    ///
    /// <para>Cycle 2 implementations are free to ignore style fields
    /// they don't yet honor — a synthetic-font test resolver returns
    /// the same shaper regardless of style.</para>
    ///
    /// <para>The returned shaper is OWNED by the resolver. Callers
    /// MUST NOT call <see cref="HbShaper.Dispose"/> on it.</para></summary>
    HbShaper Resolve(ComputedStyle style);
}

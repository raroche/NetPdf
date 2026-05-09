// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 1 — one homogenous-style chunk of text in
/// the inline pass's input. The integrating <c>InlineLayouter</c>
/// (Phase 3 Task 10) builds these from a block's inline-level child
/// boxes (<c>BoxKind.TextRun</c>, <c>BoxKind.InlineBox</c> wrappers,
/// etc.); each TextRun corresponds to a single <c>ComputedStyle</c>
/// the painter will draw with.
///
/// <para><b>Why a separate type from BoxKind.TextRun?</b> The box-tree
/// TextRun is a layout-tree node owning style + DOM origin; this type
/// is the line-builder's input view, decoupled from the box tree so
/// (a) tests can drive the line builder without a full box tree, and
/// (b) future generated content (<c>::before</c> with <c>content:
/// counter()</c>) can flow through the same type without owning a
/// box.</para>
///
/// <para><b>UTF-16 input.</b> <see cref="Text"/> is a UTF-16 string
/// matching the rest of the .NET text pipeline. The bidi + line-break
/// + shaping primitives in <c>NetPdf.Text</c> all consume
/// <c>ReadOnlySpan&lt;char&gt;</c>; cluster indices in shaper output
/// are UTF-16 code-unit offsets per HarfBuzz's
/// <c>hb_buffer_add_utf16</c> contract.</para>
///
/// <para><b>Cycle 1 scope.</b> Cycle 1 itemizes by direction (bidi
/// level changes between chars) + by source TextRun (so an
/// <c>InlineBox</c> with a different font produces a separate
/// itemized run). Script + font-fallback itemization is cycle 2;
/// the actual shaping happens in cycle 2 too.</para>
/// </summary>
/// <param name="Text">The UTF-16 text content of this run.</param>
/// <param name="Style">The computed style associated with the run.
/// The line builder reads font-size, font-family, font-weight, color,
/// text-decoration, white-space — anything the shaper or painter
/// needs to honor at glyph level.</param>
internal readonly record struct TextRun(string Text, ComputedStyle Style);

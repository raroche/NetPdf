// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Discriminator for the <see cref="DisplayCommand"/> tagged union. The full vocabulary
/// expands across Phases 1–4; only the kinds listed for Phase 1 below are emitted by
/// the painter at this point in the pipeline.
/// </summary>
/// <remarks>
/// Held in a single byte at offset 0 of <see cref="DisplayCommand"/>. Underlying type is
/// <see cref="byte"/> so reordering or insertion mid-list would shift values — append-only.
/// New kinds always appended at the end.
/// </remarks>
internal enum DisplayCommandKind : byte
{
    /// <summary>Sentinel — represents an uninitialized <see cref="DisplayCommand"/>.</summary>
    None = 0,

    /// <summary>Phase 1. Solid-color filled rectangle. Payload: <see cref="RectFillPayload"/>.</summary>
    RectFill = 1,

    /// <summary>Phase 1. Glyph run referencing a shaped <see cref="TextRun"/> in the side table. Payload: <see cref="TextRunPayload"/>.</summary>
    TextRun = 2,

    /// <summary>Phase 1. Image draw referencing a <see cref="RasterImage"/> in the side table. Payload: <see cref="ImageDrawPayload"/>.</summary>
    ImageDraw = 3,

    /// <summary>Phase 1. Save graphics state and concatenate an affine matrix. Payload: <see cref="TransformPushPayload"/>.</summary>
    TransformPush = 4,

    /// <summary>Phase 1. Restore graphics state matching the most recent <see cref="TransformPush"/>. No payload.</summary>
    TransformPop = 5,

    /// <summary>Phase 1. Save graphics state and apply an alpha. Payload: <see cref="OpacityPushPayload"/>.</summary>
    OpacityPush = 6,

    /// <summary>Phase 1. Restore graphics state matching the most recent <see cref="OpacityPush"/>. No payload.</summary>
    OpacityPop = 7,

    // Phase 3 expansions (PathFill, PathStroke, ClipPush/Pop, LinkAnnotation, BookmarkAnchor, ...) append here.
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Side-table entry for a shaped text run. The painter keeps these in
/// <see cref="DisplayList"/> rather than inlining them into <see cref="DisplayCommand"/>
/// because shaped runs carry variable-length glyph and advance buffers that don't fit in
/// a 64-byte command. <see cref="DisplayCommandKind.TextRun"/> commands reference an entry
/// here by index.
/// </summary>
/// <remarks>
/// <see cref="GlyphIds"/> and <see cref="Advances"/> are populated by the text-shaping stage
/// (Phase 1 Tasks 6–9). Until shaping lands, callers may emit a run with empty buffers and
/// the source <see cref="Text"/> string only — the PDF emitter will fall back to encoding
/// codepoints through the resolved font's <c>cmap</c>.
/// </remarks>
internal sealed class TextRun
{
    /// <summary>Resolved font registry id. <c>-1</c> means "font not yet resolved".</summary>
    public required int FontId { get; init; }

    /// <summary>Font size in CSS px. Converted to PDF points (×0.75) at emit time.</summary>
    public required double FontSize { get; init; }

    /// <summary>Fill color for the run.</summary>
    public required RgbaColor Color { get; init; }

    /// <summary>
    /// Source UTF-16 text. Drives the <c>ToUnicode</c> CMap so PDF readers can extract
    /// the original characters even after glyph subsetting.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Shaped glyph indices in the (subset) font. Empty before shaping; the emitter falls back
    /// to encoding <see cref="Text"/> through the font's <c>cmap</c> in that case.
    /// </summary>
    public ReadOnlyMemory<ushort> GlyphIds { get; init; } = ReadOnlyMemory<ushort>.Empty;

    /// <summary>
    /// Per-glyph x-advance in CSS px. Same length as <see cref="GlyphIds"/> when shaped.
    /// </summary>
    public ReadOnlyMemory<float> Advances { get; init; } = ReadOnlyMemory<float>.Empty;
}

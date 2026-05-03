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
/// <para>
/// <see cref="GlyphIds"/> and <see cref="Advances"/> are populated by the text-shaping stage
/// (Phase 1 Tasks 6–9). Until shaping lands, callers may emit a run with empty buffers and
/// the source <see cref="Text"/> string only — the PDF emitter will fall back to encoding
/// codepoints through the resolved font's <c>cmap</c>.
/// </para>
/// <para>
/// <b>Buffer ownership.</b> The <see cref="GlyphIds"/> and <see cref="Advances"/> setters
/// copy their input into a private array on assignment. A caller that mutates the source
/// buffer afterward cannot affect what this <see cref="TextRun"/> represents. Cost: one
/// short array allocation (typically &lt; 1 KB) per shaped run, which preserves the
/// determinism guarantee that <see cref="DisplayList"/> documents at the class level.
/// </para>
/// </remarks>
internal sealed class TextRun
{
    private readonly ReadOnlyMemory<ushort> _glyphIds = ReadOnlyMemory<ushort>.Empty;
    private readonly ReadOnlyMemory<float> _advances = ReadOnlyMemory<float>.Empty;

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
    /// <remarks>
    /// Setter copies the input into a private array — caller mutations to the source
    /// buffer after assignment do not affect this run.
    /// </remarks>
    public ReadOnlyMemory<ushort> GlyphIds
    {
        get => _glyphIds;
        init => _glyphIds = value.IsEmpty ? ReadOnlyMemory<ushort>.Empty : value.ToArray();
    }

    /// <summary>
    /// Per-glyph x-advance in CSS px. Same length as <see cref="GlyphIds"/> when shaped.
    /// </summary>
    /// <remarks>
    /// Setter copies the input into a private array — caller mutations to the source
    /// buffer after assignment do not affect this run.
    /// </remarks>
    public ReadOnlyMemory<float> Advances
    {
        get => _advances;
        init => _advances = value.IsEmpty ? ReadOnlyMemory<float>.Empty : value.ToArray();
    }
}

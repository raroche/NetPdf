// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.SystemFonts;

/// <summary>
/// One entry in the system-font index. Carries the metadata needed to match a
/// <c>FontQuery</c> without having to parse the font's full table set; the heavy parse
/// is deferred until the entry is selected.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FilePath"/> is the disk path the enumerator discovered. <see cref="FaceIndex"/>
/// is non-zero only for TrueType / OpenType collection files (<c>.ttc</c> / <c>.otc</c>),
/// each of which contains multiple faces — Phase 1 indexes face 0 only and will gain
/// multi-face support when collection parsing lands.
/// </para>
/// </remarks>
internal readonly record struct SystemFontEntry
{
    /// <summary>Absolute path to the font file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>Face index within a collection file. Always 0 for non-collection (TTF/OTF) files.</summary>
    public required int FaceIndex { get; init; }

    /// <summary>Family name extracted from the <c>name</c> table at indexing time.</summary>
    public required string FamilyName { get; init; }

    /// <summary>Subfamily name (e.g. "Regular", "Bold Italic").</summary>
    public required string SubfamilyName { get; init; }

    /// <summary>PostScript name (nameID 6); may be <c>null</c> if the font omits it.</summary>
    public required string? PostScriptName { get; init; }

    /// <summary>CSS-aligned weight (1..1000). 400 = normal; 700 = bold.</summary>
    public required int WeightCss { get; init; }

    /// <summary>CSS stretch (1..9). 5 = normal.</summary>
    public required int StretchCss { get; init; }

    /// <summary>
    /// True when the face is italic OR oblique. <b>Project policy (CSS Fonts 4 §5.2-permitted
    /// synonym collapse):</b> NetPdf treats italic and oblique as a single boolean for
    /// system-font matching, since most real document content uses <c>font-style: italic</c>
    /// interchangeably with <c>font-style: oblique</c> and most fonts ship only one of the
    /// two designs. A future spec-faithful matching mode that wants to distinguish them
    /// could read <c>FontMetadata.IsItalic</c> / <c>FontMetadata.IsOblique</c> directly,
    /// which preserve the OpenType-level distinction.
    /// </summary>
    public required bool IsItalic { get; init; }
}

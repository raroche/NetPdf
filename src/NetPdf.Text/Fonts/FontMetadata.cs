// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts;

/// <summary>
/// Identifying metadata for a parsed font face — the data needed for matching a
/// <c>FontQuery</c> against a face without re-parsing the underlying font tables on
/// every lookup. Extracted once at face-load time from <c>name</c>, <c>OS/2</c>, and
/// <c>head</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>CSS-aligned fields.</b> <see cref="WeightCss"/> and <see cref="StretchCss"/> are
/// the values a CSS engine compares against; <see cref="IsItalic"/> /
/// <see cref="IsOblique"/> map onto CSS's <c>font-style</c>. Older fonts that lack
/// <c>OS/2</c> bit data fall back to the <c>head.macStyle</c> bits.
/// </para>
/// <para>
/// <b>Italic vs oblique.</b> <see cref="FontMetadata"/> preserves the two flags
/// separately so a future system that wants spec-faithful CSS Fonts 4 §5.2 style
/// matching can distinguish them. The current product policy in <c>SystemFontEntry</c>
/// and <c>SystemFontResolver</c> collapses oblique into italic for matching purposes —
/// CSS Fonts Module Level 4 explicitly permits this: "User agents may treat italic and
/// oblique as synonyms when matching." Document any change to that policy here.
/// </para>
/// <para>
/// <b>Trust-boundary clamping.</b> Malformed <c>OS/2.usWeightClass</c> values (0 or
/// &gt; 1000) are normalized to 400 (CSS "normal") at extraction time so downstream
/// matchers never see out-of-range weights. Legacy 1..9 weight values are scaled to
/// CSS 100..900 per the OpenType spec footnote on the field's history.
/// </para>
/// </remarks>
internal sealed record FontMetadata
{
    /// <summary>Family name (preferring OpenType nameID 16 / "Typographic Family", falling back to nameID 1).</summary>
    public required string FamilyName { get; init; }

    /// <summary>Subfamily name (e.g. "Regular", "Bold Italic"). Best-effort; may be empty.</summary>
    public required string SubfamilyName { get; init; }

    /// <summary>PostScript name (nameID 6) — the unique embedding identifier required by PDF.</summary>
    public required string? PostScriptName { get; init; }

    /// <summary>CSS numeric weight, 1..1000. 400 = normal; 700 = bold. Sourced from <c>OS/2.usWeightClass</c>.</summary>
    public required int WeightCss { get; init; }

    /// <summary>CSS stretch, 1..9. 5 = normal. Sourced from <c>OS/2.usWidthClass</c>.</summary>
    public required int StretchCss { get; init; }

    /// <summary>True when the face is italic (slanted with a true italic design).</summary>
    public required bool IsItalic { get; init; }

    /// <summary>True when the face is oblique (slanted via shear, not a separate design).</summary>
    public required bool IsOblique { get; init; }

    /// <summary>True when the face's weight class is in the bold range (≥ 700) or marked bold via OS/2 / macStyle.</summary>
    public required bool IsBold { get; init; }

    /// <summary>
    /// Extract metadata from an already-parsed <see cref="OpenTypeFont"/>.
    /// </summary>
    public static FontMetadata Extract(OpenTypeFont font)
    {
        // Family + subfamily resolution: OpenType nameID 16 ("Typographic / Preferred Family")
        // is preferred when present (4-style families like "Roboto Slab" carry their preferred
        // family there); nameID 1 is the legacy 4-style-grouping name.
        const ushort NameIdTypographicFamily = 16;
        const ushort NameIdTypographicSubfamily = 17;
        var family = font.Name.GetName(NameIdTypographicFamily)
                  ?? font.Name.GetName(NameTable.NameIdFamilyName)
                  ?? string.Empty;
        var subfamily = font.Name.GetName(NameIdTypographicSubfamily)
                     ?? font.Name.GetName(NameTable.NameIdSubfamilyName)
                     ?? string.Empty;

        // OS/2 bit semantics (OpenType §"OS/2 — fsSelection"):
        //   bit 0 ITALIC, bit 5 BOLD, bit 6 REGULAR, bit 9 OBLIQUE.
        // head.macStyle is the older indicator: bit 0 BOLD, bit 1 ITALIC. Use it only as a
        // fallback when OS/2 bits are clearly not set, since some fonts forget OS/2 fsSelection.
        var fs = font.Os2.FsSelection;
        var mac = font.Head.MacStyle;
        var isItalic = (fs & 0x0001) != 0 || (mac & 0x0002) != 0;
        var isOblique = (fs & 0x0200) != 0;
        var isBold = (fs & 0x0020) != 0 || (mac & 0x0001) != 0 || font.Os2.UsWeightClass >= 700;

        // Weight: OS/2 usWeightClass is in CSS-equivalent 1..1000 space (per OpenType spec
        // since 2002). Some legacy fonts use a 1..9 scale — detect and rescale to 100..900.
        // Any out-of-CSS-range value (0, > 1000) is treated as "missing" and normalized to
        // 400 (CSS normal) — keeps malformed-font weights from leaking into the matcher and
        // pushing scoring off-axis.
        int weight = font.Os2.UsWeightClass;
        if (weight is > 0 and < 10)
        {
            weight *= 100;
        }
        if (weight is <= 0 or > 1000)
        {
            weight = 400;
        }

        // Stretch: OS/2 usWidthClass is 1..9 per spec. Clamp anything out-of-range to 5 (normal).
        int stretch = font.Os2.UsWidthClass;
        if (stretch is < 1 or > 9) stretch = 5;

        return new FontMetadata
        {
            FamilyName = family,
            SubfamilyName = subfamily,
            PostScriptName = font.Name.PostScriptName,
            WeightCss = weight,
            StretchCss = stretch,
            IsItalic = isItalic,
            IsOblique = isOblique,
            IsBold = isBold,
        };
    }
}

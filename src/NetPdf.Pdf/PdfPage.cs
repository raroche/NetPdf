// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Text;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// A single page in a <see cref="PdfDocument"/>. Owns its <c>/MediaBox</c>, per-page
/// <c>/Resources</c> dictionary (<c>/Font</c> + <c>/XObject</c> sub-dicts), and the
/// content-stream byte payload that the PDF reader executes to draw the page.
/// </summary>
/// <remarks>
/// <para>
/// Pages are obtained from <see cref="PdfDocument.AddPage(MediaBoxSize)"/>; the document
/// owns the page's indirect-object slot allocations. Content is built by appending
/// PDF content-stream operators via <see cref="AppendContent(string)"/> (the simplest
/// path for hand-built tests / AOT smoke) or by the higher-level draw helpers
/// (<see cref="PlaceImage(PdfIndirectRef, double, double, double, double)"/>).
/// </para>
/// </remarks>
internal sealed class PdfPage
{
    private readonly PdfIndirectRef _parentRef;
    private readonly PdfDictionary _fontsResource = new();
    private readonly PdfDictionary _xobjectsResource = new();
    private readonly PdfDictionary _extGStateResource = new();
    // Content-stream payload is built byte-oriented from the start: PDF content streams
    // are byte sequences (operators are ASCII, but text-show operands and inline-image
    // bytes can be arbitrary 8-bit data per ISO 32000-2 §7.8). A StringBuilder would
    // force an ASCII transcode at every write boundary and silently corrupt non-ASCII
    // bytes; ArrayBufferWriter<byte> stores the raw payload faithfully.
    private readonly ArrayBufferWriter<byte> _contentBuffer = new();
    private bool _finalized;

    public PdfIndirectRef PageRef { get; }
    public PdfIndirectRef ContentsRef { get; }
    public MediaBoxSize Size { get; }

    /// <summary>The page's <c>/Resources</c> dictionary; populated automatically by the placement helpers.</summary>
    public PdfDictionary Resources { get; } = new();

    internal PdfPage(PdfIndirectRef pageRef, PdfIndirectRef contentsRef, MediaBoxSize size, PdfIndirectRef parentRef)
    {
        PageRef = pageRef;
        ContentsRef = contentsRef;
        Size = size;
        _parentRef = parentRef;
    }

    /// <summary>
    /// Append ASCII PDF content-stream text (operators like <c>q</c> / <c>Q</c>,
    /// <c>cm</c>, <c>Do</c>, and ASCII text-show ops). Spaces / newlines between
    /// operators are the caller's responsibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ASCII-only.</b> Each char in the string must be in the range <c>U+0000</c>
    /// to <c>U+007F</c>. A non-ASCII character throws <see cref="ArgumentException"/>
    /// rather than silently transcoding (the safe default for content streams that
    /// will have arbitrary 8-bit bytes appended via the
    /// <see cref="AppendContent(ReadOnlySpan{byte})"/> overload alongside ASCII
    /// operators). For binary or non-ASCII content (e.g., raw bytes inside a literal
    /// string operand for a Type 3 font), use the byte overload.
    /// </para>
    /// </remarks>
    public void AppendContent(string contentStreamFragment)
    {
        ArgumentNullException.ThrowIfNull(contentStreamFragment);
        ThrowIfFinalized();
        if (contentStreamFragment.Length == 0) return;

        var span = _contentBuffer.GetSpan(contentStreamFragment.Length);
        for (var i = 0; i < contentStreamFragment.Length; i++)
        {
            var c = contentStreamFragment[i];
            if (c > 0x7F)
            {
                throw new ArgumentException(
                    $"AppendContent(string) requires ASCII-only input (each char ≤ U+007F); " +
                    $"got non-ASCII char U+{(int)c:X4} at index {i}. For binary or non-ASCII " +
                    $"content use the AppendContent(ReadOnlySpan<byte>) overload instead.",
                    nameof(contentStreamFragment));
            }
            span[i] = (byte)c;
        }
        _contentBuffer.Advance(contentStreamFragment.Length);
    }

    /// <summary>
    /// Append raw PDF content-stream bytes — the binary-safe overload. The caller is
    /// responsible for emitting only well-formed PDF content-stream syntax; this method
    /// performs no validation or escaping.
    /// </summary>
    public void AppendContent(ReadOnlySpan<byte> contentStreamFragment)
    {
        ThrowIfFinalized();
        if (contentStreamFragment.IsEmpty) return;
        _contentBuffer.Write(contentStreamFragment);
    }

    /// <summary>
    /// Place an Image XObject at the given page-space rectangle (PDF points; origin at
    /// bottom-left). Returns the resource name (e.g. <c>Im1</c>) used internally to
    /// reference the image. The image must already be registered with the parent
    /// document — use <see cref="PdfDocument.RegisterImage(Objects.PdfStream)"/> for
    /// opaque images (JPEG, opaque PNG / Raster) or
    /// <see cref="PdfDocument.RegisterImage(Images.ImageXObjectResult)"/> for any
    /// image carrying a soft mask (RGBA PNG, indexed PNG with non-binary tRNS, raster
    /// formats with full alpha). The latter wires the SMask through an indirect ref;
    /// passing a primary image whose dictionary already inlines a direct
    /// <c>/SMask</c> stream into the simpler overload would emit malformed PDF.
    /// </summary>
    public string PlaceImage(PdfIndirectRef imageRef, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(imageRef);
        ThrowIfFinalized();

        // Allocate a per-page resource name. The pattern "Im1, Im2, …" is conventional.
        var resourceName = $"Im{_xobjectsResource.Count + 1}";
        _xobjectsResource.Set(new PdfName(resourceName), imageRef);

        // Emit content operators: q (push graphics state), cm (set CTM to scale + translate),
        // /ResourceName Do (invoke the XObject), Q (pop graphics state).
        // The cm matrix [w 0 0 h x y] scales the unit square (0..1) to (w × h) and
        // translates to (x, y). Build the fragment in a small string then route through
        // AppendContent so the ASCII-validation path runs on the operator text.
        var sb = new StringBuilder(64);
        sb.Append("q ");
        AppendNumber(sb, width); sb.Append(' ');
        sb.Append("0 0 ");
        AppendNumber(sb, height); sb.Append(' ');
        AppendNumber(sb, x); sb.Append(' ');
        AppendNumber(sb, y); sb.Append(" cm /");
        sb.Append(resourceName);
        sb.Append(" Do Q\n");
        AppendContent(sb.ToString());
        return resourceName;
    }

    /// <summary>
    /// Register a font in this page's <c>/Font</c> resource and return the resource name a
    /// content-stream <c>Tf</c> operator references (e.g. <c>F1</c>). The font must already
    /// be registered with the parent document via
    /// <see cref="PdfDocument.RegisterFont(Fonts.EmbeddedFont)"/> — its returned Type 0
    /// indirect ref is what you pass here. Idempotent per referenced object: adding the same
    /// font twice yields one <c>/Font</c> entry and the same name.
    /// </summary>
    public PdfName AddFont(PdfIndirectRef fontRef)
    {
        ArgumentNullException.ThrowIfNull(fontRef);
        ThrowIfFinalized();

        // Dedup by the referenced object so a page using one font across many runs gets a
        // single /Font entry (the painter can also cache the name itself).
        foreach (var entry in _fontsResource)
        {
            // Full-identity match (incl. StoreId) — a foreign ref that happens to share a
            // local font's object number must NOT be conflated (post-PR-#122 review P2).
            if (entry.Value is PdfIndirectRef existing && existing.HasSameTarget(fontRef))
            {
                return entry.Key;
            }
        }

        // Per-page resource name. The "F1, F2, …" pattern mirrors the image "Im1, Im2, …".
        var name = new PdfName($"F{_fontsResource.Count + 1}");
        _fontsResource.Set(name, fontRef);
        return name;
    }

    /// <summary>
    /// Fill an axis-aligned rectangle with a solid RGB color at constant
    /// <paramref name="alpha"/>. Coordinates are in PDF points with the origin at the
    /// page's bottom-left (the <c>re</c>-operator convention — callers apply any CSS-px →
    /// pt scale + y-flip first); <paramref name="r"/> / <paramref name="g"/> /
    /// <paramref name="b"/> / <paramref name="alpha"/> are in [0, 1] and are clamped. A
    /// partial <paramref name="alpha"/> (&lt; 1) is composited via a PDF ExtGState
    /// constant-alpha (<c>/ca</c>) selected with the <c>gs</c> operator; opaque
    /// (<paramref name="alpha"/> = 1) emits no ExtGState. A non-positive
    /// <paramref name="width"/> or <paramref name="height"/> is a no-op. The fill is
    /// wrapped in its own <c>q</c> / <c>Q</c> pair so neither the color nor the alpha leaks
    /// into subsequent operators. Used by the layout → PDF paint bridge for backgrounds +
    /// solid border edges.
    /// </summary>
    public void FillRectangle(double x, double y, double width, double height, double r, double g, double b, double alpha = 1.0)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException(
                $"FillRectangle coordinates must be finite; got x={x}, y={y}, width={width}, height={height}.");
        }
        // Validate alpha BEFORE the Math.Clamp below: Math.Clamp(NaN, 0, 1) returns NaN, the
        // `alpha < 1.0` transparency test is then false, and the fill silently paints fully
        // opaque — a silent-corruption hole (post-PR-#125 review P2). Reject non-finite alpha
        // outright; finite out-of-range alpha still clamps into [0, 1] per the contract.
        if (!double.IsFinite(alpha))
        {
            throw new ArgumentException(
                $"FillRectangle alpha must be finite; got {alpha}.", nameof(alpha));
        }
        if (width <= 0 || height <= 0) return;

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);
        alpha = Math.Clamp(alpha, 0.0, 1.0);

        // q [/GSn gs] <r> <g> <b> rg <x> <y> <w> <h> re f Q — optionally select a constant
        // fill alpha, set the fill color, append the rectangle path, fill (non-zero winding),
        // restore graphics state (which also restores the alpha).
        var sb = new StringBuilder(64);
        sb.Append("q ");
        if (alpha < 1.0)
        {
            sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        }
        AppendNumber(sb, r); sb.Append(' ');
        AppendNumber(sb, g); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(" rg ");
        AppendNumber(sb, x); sb.Append(' ');
        AppendNumber(sb, y); sb.Append(' ');
        AppendNumber(sb, width); sb.Append(' ');
        AppendNumber(sb, height); sb.Append(" re f Q\n");
        AppendContent(sb.ToString());
    }

    /// <summary>Get (or create) the per-page <c>/ExtGState</c> resource name for a constant
    /// fill alpha (<c>/ca</c>), deduped by the alpha value (so equal alphas share one
    /// ExtGState). The name is derived from the value, so it's deterministic.</summary>
    private PdfName GetOrAddConstantAlpha(double alpha)
    {
        // Dedup by the EXACT serialized /ca value. The name must encode alpha at the same
        // precision PdfReal/PdfWriter.WriteReal emits (PdfWriter.CanonicalRealFormat — 6
        // fraction digits) — otherwise a coarser name lets two distinct alphas (e.g. 0.123456
        // and 0.123457) collide on one /GSca… name and silently reuse the wrong /ca value
        // (post-PR-#125 review P2). Sharing the canonical format makes the name 1:1 with the
        // serialized value: equal /ca bytes share one ExtGState, distinct /ca bytes never do.
        var canonical = alpha.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        var name = new PdfName("GSca" + canonical.Replace('.', '_'));
        if (!_extGStateResource.ContainsKey(name))
        {
            _extGStateResource.Set(name, new PdfDictionary()
                .Set(PdfNames.Type, PdfNames.ExtGState)
                .Set(PdfNames.ca, new PdfReal(alpha)));
        }
        return name;
    }

    /// <summary>
    /// Show a run of glyphs at a baseline origin, in a solid RGB fill color. The font MUST
    /// already be added to this page via <see cref="AddFont(PdfIndirectRef)"/> — passing a
    /// name not in the page's <c>/Font</c> resource throws (a font selected in the content
    /// stream with no matching resource entry is a malformed PDF). The font is a Type 0 /
    /// Identity-H font, so each <paramref name="glyphIds"/> entry is a 2-byte big-endian CID = glyph id;
    /// <paramref name="glyphIds"/> are the SUBSET glyph ids the embedded font program uses.
    /// Coordinates are in PDF points with the origin at the page's bottom-left (callers apply
    /// any CSS-px → pt scale + y-flip first); <paramref name="xPt"/>/<paramref name="yPt"/>
    /// is the text-line origin (the baseline left edge). Inter-glyph spacing comes from the
    /// font's <c>/W</c> advances (cycle 5a-2 "simple Td + Tj" first cut — GPOS-adjusted
    /// per-glyph positioning is a follow-up). The run is wrapped in its own <c>q</c> /
    /// <c>Q</c> pair so the color + text state don't leak. Empty input is a no-op; a
    /// <paramref name="fontSizePt"/> of 0 emits an (invisible) zero-size run.
    /// </summary>
    public void ShowGlyphs(
        PdfName fontResourceName, double fontSizePt, double xPt, double yPt,
        ReadOnlySpan<ushort> glyphIds, double r, double g, double b)
    {
        ArgumentNullException.ThrowIfNull(fontResourceName);
        ThrowIfFinalized();
        // ShowGlyphs may only reference a font already registered on this page via AddFont.
        // This guard closes two holes (post-PR-#123 review, 2× P2): (a) Save does NOT parse
        // content streams, so an unregistered name would emit `/Fn Tf` with no matching
        // /Resources /Font /Fn — a malformed PDF; (b) the only keys in _fontsResource are the
        // ones AddFont generates ("F1", "F2", …), which are escaping-safe simple names — so
        // requiring registration also makes the resource name injection-safe (no `/X Do`
        // sneaking through) WITHOUT a separate escape step. The text painter always AddFont()s
        // before ShowGlyphs, passing the returned name.
        if (!_fontsResource.ContainsKey(fontResourceName))
        {
            throw new ArgumentException(
                $"ShowGlyphs: font resource '/{fontResourceName.Value}' is not registered on this page — " +
                "call PdfPage.AddFont(...) first (its returned name is the one you pass here).",
                nameof(fontResourceName));
        }
        if (!double.IsFinite(fontSizePt) || fontSizePt < 0)
        {
            throw new ArgumentException(
                $"ShowGlyphs fontSizePt must be finite + non-negative; got {fontSizePt}.", nameof(fontSizePt));
        }
        if (!double.IsFinite(xPt) || !double.IsFinite(yPt))
        {
            throw new ArgumentException($"ShowGlyphs position must be finite; got x={xPt}, y={yPt}.");
        }
        if (glyphIds.IsEmpty) return;

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);

        // q <r> <g> <b> rg BT /Fn <size> Tf <x> <y> Td <glyph-hex> Tj ET Q — set the fill
        // color, open a text object, select the font + size, set the line origin, show the
        // glyph ids as a hex string (2 bytes / glyph, Identity-H), close.
        var sb = new StringBuilder(48 + (glyphIds.Length * 4));
        sb.Append("q ");
        AppendNumber(sb, r); sb.Append(' ');
        AppendNumber(sb, g); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(" rg BT /");
        sb.Append(fontResourceName.Value); sb.Append(' ');
        AppendNumber(sb, fontSizePt); sb.Append(" Tf ");
        AppendNumber(sb, xPt); sb.Append(' ');
        AppendNumber(sb, yPt); sb.Append(" Td <");
        foreach (var glyph in glyphIds)
        {
            sb.Append(HexNibble(glyph >> 12));
            sb.Append(HexNibble(glyph >> 8));
            sb.Append(HexNibble(glyph >> 4));
            sb.Append(HexNibble(glyph));
        }
        sb.Append("> Tj ET Q\n");
        AppendContent(sb.ToString());
    }

    private static char HexNibble(int value) => "0123456789ABCDEF"[value & 0xF];

    private static void AppendNumber(StringBuilder sb, double value)
    {
        // PDF numbers are written without exponent notation, finite, with a maximum
        // of 5 fractional digits per ISO 32000-2:2020 §7.3.3.
        if (!double.IsFinite(value))
        {
            throw new ArgumentException(
                $"PDF number must be finite; got {value}.", nameof(value));
        }
        sb.Append(value.ToString("0.#####", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Finalize: build the page dictionary, attach the resources tree, return the
    /// content-stream bytes for the document to assign to <see cref="ContentsRef"/>.
    /// Called by <see cref="PdfDocument.Save"/>.
    /// </summary>
    internal (PdfDictionary PageDict, byte[] ContentBytes) Finalize()
    {
        ThrowIfFinalized();
        _finalized = true;

        if (_fontsResource.Count > 0) Resources.Set(PdfNames.Font, _fontsResource);
        if (_xobjectsResource.Count > 0) Resources.Set(PdfNames.XObject, _xobjectsResource);
        if (_extGStateResource.Count > 0) Resources.Set(PdfNames.ExtGState, _extGStateResource);

        var mediaBox = new PdfArray()
            .Add(new PdfReal(0))
            .Add(new PdfReal(0))
            .Add(new PdfReal(Size.WidthPts))
            .Add(new PdfReal(Size.HeightPts));

        var pageDict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Page)
            .Set(PdfNames.Parent, _parentRef)
            .Set(PdfNames.Resources, Resources)
            .Set(PdfNames.MediaBox, mediaBox)
            .Set(PdfNames.Contents, ContentsRef);

        return (pageDict, _contentBuffer.WrittenSpan.ToArray());
    }

    private void ThrowIfFinalized()
    {
        if (_finalized)
        {
            throw new InvalidOperationException(
                "PdfPage has already been finalized via PdfDocument.Save() — adding content after save is not supported.");
        }
    }
}

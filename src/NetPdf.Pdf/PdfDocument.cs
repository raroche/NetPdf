// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetPdf.Pdf.Fonts;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// High-level PDF document builder. Owns the indirect-object store, the page list, the
/// per-document image XObject cache (with content-hash deduplication), and the
/// trailer / metadata wiring. Underneath it delegates byte emission to
/// <see cref="PdfDocumentWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 surface focuses on the orchestration the AOT-smoke test and Task 23
/// determinism harness need: add a page → place text or images → save to bytes. Drawing
/// primitives that depend on the layout / paint phases (Phase 3 work) come through the
/// public <c>HtmlPdf.Convert(html)</c> path, which constructs a <see cref="PdfDocument"/>
/// internally and feeds it via the display-list IR.
/// </para>
/// <para>
/// <b>Determinism.</b> Identical input must produce byte-identical output:
/// </para>
/// <list type="bullet">
///   <item><see cref="PdfDocumentWriter"/> already pins object numbering by allocation
///         order and xref padding to a fixed format.</item>
///   <item>The image cache deduplicates by SHA-256 of the stream payload <b>and</b> the
///         stream dictionary's canonical bytes (and, for transparent images, the SMask
///         bytes too) — the same image used N times produces a single XObject regardless
///         of how many pages reference it; two identical-pixel buffers with different
///         color spaces or filter params remain distinct.</item>
///   <item><see cref="CreationDate"/> / <see cref="ModDate"/> default to <c>null</c>;
///         when set, they are formatted via the ISO 32000 PDF date convention. Callers
///         that need bit-for-bit reproducibility leave them null.</item>
/// </list>
/// </remarks>
internal sealed class PdfDocument
{
    private readonly PdfDocumentWriter _writer;
    private readonly PdfIndirectRef _catalogRef;
    private readonly PdfIndirectRef _pagesRef;
    private readonly List<PdfPage> _pages = [];
    private readonly Dictionary<string, PdfIndirectRef> _imageCache = [];
    private readonly Dictionary<string, PdfIndirectRef> _fontCache = [];
    private bool _saved;

    public PdfDocument(string version = "1.7")
    {
        _writer = new PdfDocumentWriter { Version = version };
        _catalogRef = _writer.Objects.Allocate();
        _pagesRef = _writer.Objects.Allocate();
    }

    // ───── Document metadata (Info dict) ─────────────────────────────────────

    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Creator { get; set; }

    /// <summary>Producer string emitted in the Info dict. Defaults to "NetPdf".</summary>
    public string Producer { get; set; } = "NetPdf";

    /// <summary>
    /// Document creation date. Null = omit from the Info dict (the deterministic default).
    /// Set explicitly when the consumer wants a real timestamp emitted.
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Document modification date. Null = omit. Same determinism rule as <see cref="CreationDate"/>.</summary>
    public DateTimeOffset? ModDate { get; set; }

    /// <summary>Phase 4 links (PR 4) — opt into emitting hyperlink <c>/URI</c> link-annotation actions
    /// (the active-content preflight blocks <c>/URI</c> by default). When set, ONLY well-formed URI
    /// actions (<c>/S /URI</c>) pass; JavaScript / Launch / SubmitForm / embedded files stay blocked.</summary>
    public bool AllowUriLinkAnnotations
    {
        get => _writer.AllowUriLinkAnnotations;
        set => _writer.AllowUriLinkAnnotations = value;
    }

    // ───── Document outline (bookmarks) ──────────────────────────────────────

    /// <summary>Phase 4 outlines (PR 4) — one accumulated heading for the document outline: its level
    /// (1–6), title, target page, and the destination top in PDF points (page-bottom origin).</summary>
    private readonly record struct OutlineHeading(int Level, string Title, PdfPage Page, double TopPt);

    private List<OutlineHeading>? _outlineHeadings;

    /// <summary>Phase 4 outlines (PR 4) — add a heading to the document outline (bookmarks panel). Call in
    /// DOCUMENT ORDER; <see cref="SaveTo"/> nests headings by <paramref name="level"/> into the
    /// <c>/Outlines</c> tree, each item a <c>/XYZ</c> destination to <paramref name="page"/> at
    /// <paramref name="topPt"/>. An empty title is ignored.</summary>
    public void AddOutlineHeading(int level, string title, PdfPage page, double topPt)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(title);
        if (level < 1 || string.IsNullOrWhiteSpace(title)) return;
        (_outlineHeadings ??= new List<OutlineHeading>()).Add(new OutlineHeading(level, title.Trim(), page, topPt));
    }

    // ───── Pages ─────────────────────────────────────────────────────────────

    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Add a new page at <paramref name="size"/> and return it for content population.</summary>
    public PdfPage AddPage(MediaBoxSize size)
    {
        ThrowIfSaved();
        var pageRef = _writer.Objects.Allocate();
        var contentsRef = _writer.Objects.Allocate();
        var page = new PdfPage(pageRef, contentsRef, size, _pagesRef);
        _pages.Add(page);
        return page;
    }

    // ───── Image XObject registration with content-hash dedup ────────────────

    /// <summary>
    /// Register an opaque Image XObject stream with the document. Returns an indirect ref
    /// usable in any page's <see cref="PdfPage.PlaceImage"/> call. If a byte-identical
    /// Image XObject (same payload <b>and</b> same dictionary) has already been
    /// registered, the existing ref is returned — N references to the same image produce
    /// a single XObject in the emitted file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For images that carry a soft mask (PNG with full alpha, raster RGBA), use the
    /// <see cref="RegisterImage(ImageXObjectResult)"/> overload instead so the SMask
    /// is allocated as its own indirect object and wired through the primary image's
    /// <c>/SMask</c> entry. Passing a primary image with an inline SMask through this
    /// overload would emit a malformed PDF (direct streams nested in dictionaries are
    /// rejected by ISO 32000-2 §7.3.8).
    /// </para>
    /// </remarks>
    public PdfIndirectRef RegisterImage(PdfStream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        return RegisterImage(new ImageXObjectResult { Image = imageStream });
    }

    /// <summary>
    /// Register an Image XObject (and its optional SMask) with the document. The primary
    /// image and the SMask each get their own indirect-object slot; the primary image's
    /// <c>/SMask</c> entry is rewritten to an indirect reference to the SMask slot.
    /// Dedup keys cover the entire pair, so two registrations of the same
    /// <c>(Image, SMask)</c> content collapse to a single XObject pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Builders (<see cref="PngImageXObject"/>, <see cref="RasterImageXObject"/>,
    /// <see cref="JpegImageXObject"/>) construct an <see cref="ImageXObjectResult"/>
    /// whose primary image dictionary does <b>not</b> have <c>/SMask</c> set —
    /// registration is the place that allocates indirect refs and wires them in.
    /// </para>
    /// </remarks>
    public PdfIndirectRef RegisterImage(ImageXObjectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ThrowIfSaved();
        ValidateImageXObjectShape(result.Image, parameterName: nameof(result), isSMask: false);
        if (result.SMask is not null)
        {
            ValidateImageXObjectShape(result.SMask, parameterName: nameof(result), isSMask: true);
        }

        var key = ComputeContentKey(result);
        if (_imageCache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // SMask must be allocated and assigned before we set the primary image's /SMask
        // pointer — otherwise the primary's dictionary holds a forward ref to an
        // unallocated slot, which preflight would reject.
        //
        // The primary image's dictionary is CLONED before wiring /SMask. Mutating
        // the caller's instance would be a correctness bug:
        //   * Same-instance dedup would break: the first call hashes a clean dict and
        //     caches under key K1; a second call with the same instance would hash a
        //     now-mutated dict (carrying /SMask), miss the cache under K2, and
        //     allocate a duplicate XObject pair — silent emission bloat and broken
        //     dedup.
        //   * Cross-document reuse would carry document-A's indirect ref into
        //     document B's dictionary (different store id; preflight would reject it
        //     after a confusing chain of mutations).
        // Cloning is shallow (PdfDictionary entries shared by reference, payload
        // bytes shared via PdfStream.WithDictionary) — a few-ns allocation overhead
        // per registration, negligible vs. the SHA-256 dedup-key hash.
        var imageRef = _writer.Objects.Allocate();
        var imageToAssign = result.Image;
        if (result.SMask is not null)
        {
            var smaskRef = _writer.Objects.Allocate();
            _writer.Objects.Assign(smaskRef, result.SMask);
            imageToAssign = CloneWithSMaskWired(result.Image, smaskRef);
        }
        _writer.Objects.Assign(imageRef, imageToAssign);
        _imageCache[key] = imageRef;
        return imageRef;
    }

    private static PdfStream CloneWithSMaskWired(PdfStream source, PdfIndirectRef smaskRef)
    {
        // Shallow-copy the dictionary entries into a fresh one, then add /SMask.
        // PdfStream's constructor will reset /Length on the new dict from the payload
        // length, so we skip the source's /Length entry to avoid carrying a stale
        // value.
        var clonedDict = new PdfDictionary();
        foreach (var entry in source.Dictionary)
        {
            if (entry.Key.Equals(PdfNames.Length)) continue;
            clonedDict.Set(entry.Key, entry.Value);
        }
        clonedDict.Set(PdfNames.SMask, smaskRef);
        return source.WithDictionary(clonedDict);
    }

    /// <summary>
    /// Number of unique image registrations cached in the document. Each distinct
    /// <c>(Image, SMask?)</c> pair counts once, regardless of how many pages reference
    /// it. (An opaque image and the same image registered as part of an
    /// <see cref="ImageXObjectResult"/> with a non-null SMask are <b>distinct</b>
    /// entries — their emitted XObject graphs differ.)
    /// </summary>
    public int RegisteredImageCount => _imageCache.Count;

    // ───── Tiling-pattern registration (tiling-patterns cycle) ───────────────

    private readonly Dictionary<string, PdfIndirectRef> _patternCache = [];

    // Phase 4 gradients (PR #209 review [P3]) — N boxes painting the SAME gradient (repeated
    // cards / table rows) reuse one color function (position-independent — keyed on the stops)
    // and, when their axis/circle geometry also coincides, one shading object, instead of
    // allocating a fresh function + shading graph per painted fragment.
    private readonly Dictionary<string, PdfIndirectRef> _gradientFunctionCache = [];
    private readonly Dictionary<string, PdfIndirectRef> _gradientShadingCache = [];

    /// <summary>
    /// Register a TILING PATTERN (ISO 32000-2 §8.7.3, PatternType 1) that repeats one
    /// registered Image XObject on a <paramref name="tileWidthPt"/> ×
    /// <paramref name="tileHeightPt"/> grid anchored at
    /// (<paramref name="anchorXPt"/>, <paramref name="anchorYPt"/>) in DEFAULT page space.
    /// Pattern space is anchored to the default user space — NOT the CTM at fill time
    /// (§8.7.3.1) — so the grid's phase is baked into the pattern's <c>/Matrix</c>; any
    /// anchor congruent modulo the tile reproduces the same grid. Returns the indirect ref
    /// for <see cref="PdfPage.FillRectangleWithPattern"/>. One pattern object replaces an
    /// unbounded per-tile placement loop — O(1) content-stream size regardless of the tile
    /// count. Dedup by (image ref, tile size, anchor): N boxes sharing one image + phase
    /// emit one pattern.
    /// </summary>
    public PdfIndirectRef RegisterTilingPattern(
        PdfIndirectRef imageRef, double tileWidthPt, double tileHeightPt,
        double anchorXPt, double anchorYPt,
        double? xStepPt = null, double? yStepPt = null)
    {
        ArgumentNullException.ThrowIfNull(imageRef);
        ThrowIfSaved();
        if (!double.IsFinite(tileWidthPt) || !double.IsFinite(tileHeightPt)
            || tileWidthPt <= 0 || tileHeightPt <= 0)
        {
            throw new ArgumentException(
                $"Tiling pattern tile size must be finite and positive; got {tileWidthPt}×{tileHeightPt}.");
        }
        if (!double.IsFinite(anchorXPt) || !double.IsFinite(anchorYPt))
        {
            throw new ArgumentException(
                $"Tiling pattern anchor must be finite; got ({anchorXPt}, {anchorYPt}).");
        }
        // ORIGIN STEPS (space-round cycle) default to the tile size; a `space` gap makes the
        // step EXCEED the cell BBox — legal per §8.7.3.1 (tiles with gaps between them).
        var stepX = xStepPt ?? tileWidthPt;
        var stepY = yStepPt ?? tileHeightPt;
        if (!double.IsFinite(stepX) || !double.IsFinite(stepY) || stepX <= 0 || stepY <= 0)
        {
            throw new ArgumentException(
                $"Tiling pattern steps must be finite and positive; got ({stepX}, {stepY}).");
        }

        // ONE canonical numeric path (PR #168 review P2): quantize every input through the
        // SAME 6-fractional-digit canonical form PdfReal/PdfWriter serialize with
        // (CanonicalRealFormat), so the dedup KEY, the CELL content-stream numbers, and the
        // dictionary's /BBox + /XStep//YStep + /Matrix all agree to the digit — the pre-fix
        // 5-digit key/cell could disagree with the 6-digit dictionary AND dedup two distinct
        // 6-digit patterns into one cached object.
        var wText = tileWidthPt.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        var hText = tileHeightPt.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        var axText = anchorXPt.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        var ayText = anchorYPt.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        var sxText = stepX.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        var syText = stepY.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);
        tileWidthPt = double.Parse(wText, CultureInfo.InvariantCulture);
        tileHeightPt = double.Parse(hText, CultureInfo.InvariantCulture);
        anchorXPt = double.Parse(axText, CultureInfo.InvariantCulture);
        anchorYPt = double.Parse(ayText, CultureInfo.InvariantCulture);
        stepX = double.Parse(sxText, CultureInfo.InvariantCulture);
        stepY = double.Parse(syText, CultureInfo.InvariantCulture);

        // FULL indirect-ref identity in the key (PR #168 Copilot) — object numbers repeat
        // across stores (a foreign or synthetic StoreId-0 ref can share a number with a local
        // one), and HasSameTarget's contract is number + generation + store; a number-only key
        // could hand back a pattern for the WRONG image.
        var key = $"{imageRef.ObjectNumber}:{imageRef.Generation}:{imageRef.StoreId}|{wText}|{hText}|{axText}|{ayText}|{sxText}|{syText}";
        if (_patternCache.TryGetValue(key, out var existing)) return existing;

        // The cell paints the image stretched to the tile rect: q w 0 0 h 0 0 cm /ImP Do Q —
        // the numbers are the SAME canonical strings the dictionary reals serialize to.
        var content = $"q {wText} 0 0 {hText} 0 0 cm /ImP Do Q\n";
        var xobjects = new PdfDictionary().Set(new PdfName("ImP"), imageRef);
        var resources = new PdfDictionary().Set(PdfNames.XObject, xobjects);
        var bbox = new PdfArray()
            .Add(new PdfReal(0)).Add(new PdfReal(0))
            .Add(new PdfReal(tileWidthPt)).Add(new PdfReal(tileHeightPt));
        var matrix = new PdfArray()
            .Add(new PdfReal(1)).Add(new PdfReal(0)).Add(new PdfReal(0))
            .Add(new PdfReal(1)).Add(new PdfReal(anchorXPt)).Add(new PdfReal(anchorYPt));
        var dict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Pattern)           // ISO 32000-2 Table 74 (PR #168 Copilot)
            .Set(PdfNames.PatternType, new PdfInteger(1))   // tiling
            .Set(PdfNames.PaintType, new PdfInteger(1))     // colored (the image carries color)
            .Set(PdfNames.TilingType, new PdfInteger(1))    // constant spacing
            .Set(PdfNames.BBox, bbox)
            .Set(PdfNames.XStep, new PdfReal(stepX))
            .Set(PdfNames.YStep, new PdfReal(stepY))
            .Set(PdfNames.Resources, resources)
            .Set(PdfNames.Matrix, matrix);
        var patternRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(patternRef, new PdfStream(
            System.Text.Encoding.ASCII.GetBytes(content), dict));
        _patternCache[key] = patternRef;
        return patternRef;
    }

    /// <summary>Phase 4 gradients — register a PDF native AXIAL shading (ISO 32000-2
    /// §8.7.4.5.3, <c>ShadingType 2</c>) for a CSS <c>linear-gradient</c>. The gradient
    /// axis runs from <c>(<paramref name="x0"/>, <paramref name="y0"/>)</c> (offset 0) to
    /// <c>(<paramref name="x1"/>, <paramref name="y1"/>)</c> (offset 1) in PAGE user space
    /// (PDF points, bottom-left origin); the caller derives the axis endpoints from the box
    /// geometry + gradient angle. <c>/Extend [true true]</c> paints the end colors past the
    /// axis (CSS gradients fill the whole box). Returns the shading's indirect ref for
    /// <see cref="PdfPage.PaintShadingInRect"/>.
    /// <para>Colors are DeviceRGB; per-stop alpha is dropped (opaque) — soft-mask alpha
    /// shadings are a documented follow-up. Stops must be sorted with strictly increasing
    /// offsets in [0, 1]; the caller (gradient parser) normalizes per CSS Images §3.4.</para></summary>
    public PdfIndirectRef RegisterAxialShading(
        double x0, double y0, double x1, double y1,
        IReadOnlyList<PdfGradientStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ThrowIfSaved();
        if (stops.Count == 0)
            throw new ArgumentException("An axial shading needs at least one color stop.", nameof(stops));
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1))
            throw new ArgumentException($"Axial shading coords must be finite; got ({x0},{y0})-({x1},{y1}).");

        // Reuse an identical axial shading (same axis + stops) — PR #209 review [P3].
        var shadingKey = string.Concat(
            "A|", Canon(x0), ",", Canon(y0), ",", Canon(x1), ",", Canon(y1), "|", GradientStopsKey(stops));
        if (_gradientShadingCache.TryGetValue(shadingKey, out var cachedShading)) return cachedShading;

        var functionRef = BuildGradientFunction(stops);
        var coords = new PdfArray()
            .Add(new PdfReal(x0)).Add(new PdfReal(y0))
            .Add(new PdfReal(x1)).Add(new PdfReal(y1));
        var dict = new PdfDictionary()
            .Set(PdfNames.ShadingType, new PdfInteger(2)) // axial
            .Set(PdfNames.ColorSpace, PdfNames.DeviceRGB)
            .Set(PdfNames.Coords, coords)
            .Set(PdfNames.Function, functionRef)
            .Set(PdfNames.Extend, new PdfArray().Add(PdfBoolean.True).Add(PdfBoolean.True));
        var shadingRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(shadingRef, dict);
        _gradientShadingCache[shadingKey] = shadingRef;
        return shadingRef;
    }

    /// <summary>Phase 4 gradients — register a PDF native RADIAL shading (ISO 32000-2
    /// §8.7.4.5.4, <c>ShadingType 3</c>) for a CSS <c>radial-gradient</c>. The gradient runs
    /// between two concentric circles at <c>(<paramref name="cx"/>, <paramref name="cy"/>)</c>
    /// in PAGE user space: the inner circle (offset 0, radius <paramref name="r0"/>, usually 0)
    /// and the outer circle (offset 1, radius <paramref name="r1"/>). <c>/Extend [true true]</c>
    /// fills inside r0 with the first color and outside r1 with the last. Shares the color
    /// <see cref="BuildGradientFunction"/> with the axial path. Elliptical gradients are painted
    /// circularly in a CTM-scaled space by the caller; here the shape is always a circle.</summary>
    public PdfIndirectRef RegisterRadialShading(
        double cx, double cy, double r0, double r1,
        IReadOnlyList<PdfGradientStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ThrowIfSaved();
        if (stops.Count == 0)
            throw new ArgumentException("A radial shading needs at least one color stop.", nameof(stops));
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(r0) || !double.IsFinite(r1)
            || r0 < 0 || r1 < 0)
            throw new ArgumentException($"Radial shading circles must be finite + non-negative; got ({cx},{cy}) r0={r0} r1={r1}.");

        // Reuse an identical radial shading (same circles + stops) — PR #209 review [P3].
        var shadingKey = string.Concat(
            "R|", Canon(cx), ",", Canon(cy), ",", Canon(r0), ",", Canon(r1), "|", GradientStopsKey(stops));
        if (_gradientShadingCache.TryGetValue(shadingKey, out var cachedShading)) return cachedShading;

        var functionRef = BuildGradientFunction(stops);
        var coords = new PdfArray()
            .Add(new PdfReal(cx)).Add(new PdfReal(cy)).Add(new PdfReal(r0))
            .Add(new PdfReal(cx)).Add(new PdfReal(cy)).Add(new PdfReal(r1));
        var dict = new PdfDictionary()
            .Set(PdfNames.ShadingType, new PdfInteger(3)) // radial
            .Set(PdfNames.ColorSpace, PdfNames.DeviceRGB)
            .Set(PdfNames.Coords, coords)
            .Set(PdfNames.Function, functionRef)
            .Set(PdfNames.Extend, new PdfArray().Add(PdfBoolean.True).Add(PdfBoolean.True));
        var shadingRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(shadingRef, dict);
        _gradientShadingCache[shadingKey] = shadingRef;
        return shadingRef;
    }

    /// <summary>Phase 4 gradients — build the PDF function mapping the parametric
    /// gradient domain [0, 1] to a DeviceRGB color (ISO 32000-2 §7.10). One stop → a
    /// constant (C0 == C1) <c>FunctionType 2</c>; otherwise a <c>FunctionType 3</c>
    /// stitching function whose <c>/Bounds</c> are the STOP OFFSETS so each interpolation
    /// happens only between its two stops (an authored `red 10%, blue 90%` holds red over
    /// [0, .1], transitions over [.1, .9], holds blue over [.9, 1]). Leading/trailing
    /// constant segments are added when the first/last offset isn't 0/1, and the bounds are
    /// forced strictly increasing (an equal/hard-stop offset becomes a near-instant
    /// transition) so the PDF function contract holds (ISO 32000-2 §7.10.4).</summary>
    private PdfIndirectRef BuildGradientFunction(IReadOnlyList<PdfGradientStop> stops)
    {
        // Cache the color function by its normalized stops (PR #209 review [P3]) — the function
        // is position-independent, so every box painting the same gradient shares one graph.
        var key = GradientStopsKey(stops);
        if (_gradientFunctionCache.TryGetValue(key, out var cached)) return cached;
        var built = BuildGradientFunctionUncached(stops);
        _gradientFunctionCache[key] = built;
        return built;
    }

    /// <summary>A canonical cache key for a gradient's normalized stops (PR #209 review [P3]):
    /// each stop's offset + RGB serialized with the SAME canonical real format the PDF writer
    /// emits, so two stop lists that would produce a byte-identical function share one slot.</summary>
    private static string GradientStopsKey(IReadOnlyList<PdfGradientStop> stops)
    {
        var sb = new StringBuilder(stops.Count * 24);
        foreach (var s in stops)
            sb.Append(Canon(s.Offset)).Append(':')
              .Append(Canon(s.R)).Append(',').Append(Canon(s.G)).Append(',').Append(Canon(s.B)).Append(';');
        return sb.ToString();
    }

    private static string Canon(double v) =>
        v.ToString(PdfWriter.CanonicalRealFormat, CultureInfo.InvariantCulture);

    private PdfIndirectRef BuildGradientFunctionUncached(IReadOnlyList<PdfGradientStop> stops)
    {
        static PdfArray Rgb(PdfGradientStop s) => new PdfArray()
            .Add(new PdfReal(System.Math.Clamp(s.R, 0, 1)))
            .Add(new PdfReal(System.Math.Clamp(s.G, 0, 1)))
            .Add(new PdfReal(System.Math.Clamp(s.B, 0, 1)));

        PdfIndirectRef ExpFunction(PdfGradientStop a, PdfGradientStop b)
        {
            var fn = new PdfDictionary()
                .Set(PdfNames.FunctionType, new PdfInteger(2))
                .Set(PdfNames.Domain, new PdfArray().Add(new PdfReal(0)).Add(new PdfReal(1)))
                .Set(PdfNames.C0, Rgb(a))
                .Set(PdfNames.C1, Rgb(b))
                .Set(PdfNames.N, new PdfReal(1)); // linear interpolation
            var r = _writer.Objects.Allocate();
            _writer.Objects.Assign(r, fn);
            return r;
        }

        if (stops.Count == 1)
            return ExpFunction(stops[0], stops[0]);

        // Control points spanning the full [0, 1] domain: the stops at their offsets, plus a
        // leading/trailing constant hold when the first/last offset isn't 0/1.
        var pts = new List<(double T, PdfGradientStop S)>(stops.Count + 2);
        var first = stops[0];
        var last = stops[stops.Count - 1];
        if (System.Math.Clamp(first.Offset, 0, 1) > 0) pts.Add((0.0, first));
        for (var i = 0; i < stops.Count; i++) pts.Add((System.Math.Clamp(stops[i].Offset, 0, 1), stops[i]));
        if (System.Math.Clamp(last.Offset, 0, 1) < 1) pts.Add((1.0, last));

        // Normalize the control-point boundaries to a STRICTLY INCREASING sequence inside the
        // OPEN interval (0, 1) so the FunctionType 3 /Bounds contract holds (ISO 32000-2 §7.10.4
        // — interior bounds must be strictly increasing AND strictly between the domain
        // endpoints). PR #209 review [P1]: the old `Min(1, prev+eps)` ceiling-clamped TERMINAL
        // hard stops (e.g. `red 100%, blue 100%, green 100%`) to DUPLICATE /Bounds at 1.0 → a
        // malformed, reader-dependent PDF. Now the endpoints pin to 0 / 1 and each interior
        // point is clamped above its predecessor (+eps) but below 1 with eps headroom per
        // remaining interior point, so the points after it still fit. Non-degenerate gradients
        // are untouched (their offsets already sit well inside the window) ⇒ byte-identical.
        const double eps = 1e-6;
        var n = pts.Count;
        pts[0] = (0.0, pts[0].S);
        pts[n - 1] = (1.0, pts[n - 1].S);
        for (var i = 1; i < n - 1; i++)
        {
            var lo = pts[i - 1].T + eps;
            var hi = 1.0 - eps * (n - 1 - i); // reserve room for the remaining interior pts + the 1.0 endpoint
            pts[i] = (System.Math.Clamp(pts[i].T, lo, System.Math.Max(lo, hi)), pts[i].S);
        }

        // A single segment over [0, 1] (two stops at exactly 0 and 1) needs no stitch.
        if (pts.Count == 2)
            return ExpFunction(pts[0].S, pts[1].S);

        var functions = new PdfArray();
        var bounds = new PdfArray();
        var encode = new PdfArray();
        for (var i = 0; i < pts.Count - 1; i++)
        {
            functions.Add(ExpFunction(pts[i].S, pts[i + 1].S));
            encode.Add(new PdfReal(0)).Add(new PdfReal(1));
            if (i > 0) bounds.Add(new PdfReal(pts[i].T)); // interior boundaries only
        }
        var stitch = new PdfDictionary()
            .Set(PdfNames.FunctionType, new PdfInteger(3))
            .Set(PdfNames.Domain, new PdfArray().Add(new PdfReal(0)).Add(new PdfReal(1)))
            .Set(PdfNames.Functions, functions)
            .Set(PdfNames.Bounds, bounds)
            .Set(PdfNames.Encode, encode);
        var stitchRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(stitchRef, stitch);
        return stitchRef;
    }

    // ───── Embedded font registration (the deferred Phase 1 Task 22) ─────────

    /// <summary>
    /// Register an embedded subset font and return an indirect ref to its Type 0 font
    /// dictionary — the value a page's <c>/Font</c> resource points at (see
    /// <see cref="PdfPage.AddFont(PdfIndirectRef)"/>). The font's five objects (Type 0 +
    /// CIDFontType2 + FontDescriptor + FontFile2 + ToUnicode) each get their own
    /// indirect-object slot, and the structural cross-references — <c>/DescendantFonts[0]</c>,
    /// <c>/FontDescriptor</c>, <c>/FontFile2</c>, <c>/ToUnicode</c> — are rewritten from the
    /// <see cref="EmbeddedFont"/>'s direct nesting to those refs. If a byte-identical subset
    /// (same FontFile2 + ToUnicode) was already registered, the existing Type 0 ref is
    /// returned — N references to one subset produce a single font graph.
    /// </summary>
    /// <remarks>
    /// Objects are allocated + assigned BOTTOM-UP (leaf streams first) so every
    /// cross-reference points at an already-allocated slot; a dictionary carrying a forward
    /// ref to an unallocated slot would be rejected by save-time preflight. Each child
    /// dictionary is CLONED before its cross-ref key is swapped to an indirect ref, so the
    /// caller's <see cref="EmbeddedFont"/> (and any other document reusing it) is never
    /// mutated — the same correctness rule as <see cref="CloneWithSMaskWired"/>.
    /// </remarks>
    public PdfIndirectRef RegisterFont(EmbeddedFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        ThrowIfSaved();

        var key = ComputeFontContentKey(font);
        if (_fontCache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // FontFile2 (leaf) → FontDescriptor (/FontFile2 → ref).
        var fontFile2Ref = _writer.Objects.Allocate();
        _writer.Objects.Assign(fontFile2Ref, font.FontFile2Stream);
        var descriptorRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(descriptorRef,
            CloneReplacing(font.FontDescriptorDictionary, PdfNames.FontFile2, fontFile2Ref));

        // CIDFontType2 (/FontDescriptor → ref).
        var cidRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(cidRef,
            CloneReplacing(font.CidFontDictionary, PdfNames.FontDescriptor, descriptorRef));

        // ToUnicode (leaf) → Type 0 (/ToUnicode → ref, /DescendantFonts[0] → cidRef).
        var toUnicodeRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(toUnicodeRef, font.ToUnicodeStream);
        var type0Dict = CloneReplacing(font.Type0FontDictionary, PdfNames.ToUnicode, toUnicodeRef);
        type0Dict.Set(PdfNames.DescendantFonts, new PdfArray().Add(cidRef));
        var type0Ref = _writer.Objects.Allocate();
        _writer.Objects.Assign(type0Ref, type0Dict);

        _fontCache[key] = type0Ref;
        return type0Ref;
    }

    /// <summary>Number of unique embedded-font subsets registered (each distinct subset
    /// counts once, regardless of how many pages reference it).</summary>
    public int RegisteredFontCount => _fontCache.Count;

    /// <summary>Shallow-clone a dictionary's entries into a fresh one, replacing one key's
    /// value — used to swap a directly-nested child for its indirect ref without mutating
    /// the source (the structural-rewire pattern shared with <see cref="CloneWithSMaskWired"/>).</summary>
    private static PdfDictionary CloneReplacing(PdfDictionary source, PdfName key, PdfObject value)
    {
        var clone = new PdfDictionary();
        foreach (var entry in source)
        {
            clone.Set(entry.Key, entry.Value);
        }
        clone.Set(key, value);
        return clone;
    }

    /// <summary>Dedup key for an embedded font. The subset SFNT program (FontFile2) + the
    /// ToUnicode CMap are the bulk of it, but the Type0 / CID / FontDescriptor dictionaries
    /// also carry render-affecting metadata — <c>/W</c> advance widths, <c>/BaseFont</c>,
    /// <c>/CIDToGIDMap</c>, <c>/Encoding</c>, descriptor metrics — that an
    /// <see cref="EmbeddedFont"/> could in principle differ on while sharing the streams. So
    /// every dictionary is folded in too (minus its structural child cross-refs, which the
    /// stream hashes / the sibling dict hashes already cover), so two graphs that differ
    /// only in a dictionary can't collide (post-PR-#122 review P2).</summary>
    private static string ComputeFontContentKey(EmbeddedFont font)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(HashStream(font.FontFile2Stream));
        hasher.AppendData(HashStream(font.ToUnicodeStream));
        hasher.AppendData(HashDictExcludingKeys(font.Type0FontDictionary, PdfNames.DescendantFonts, PdfNames.ToUnicode));
        hasher.AppendData(HashDictExcludingKeys(font.CidFontDictionary, PdfNames.FontDescriptor));
        hasher.AppendData(HashDictExcludingKeys(font.FontDescriptorDictionary, PdfNames.FontFile2));
        return $"font:{Convert.ToHexString(hasher.GetHashAndReset())}";
    }

    /// <summary>SHA-256 of a dictionary's canonical bytes with <paramref name="excludedKeys"/>
    /// removed — folds a font sub-dictionary's render-affecting metadata into the dedup key
    /// while skipping the structural child cross-refs (the nested stream / child dict) that
    /// other hashes already cover. With those keys removed the dictionary holds no direct
    /// stream value, so it serializes cleanly.</summary>
    private static byte[] HashDictExcludingKeys(PdfDictionary dict, params PdfName[] excludedKeys)
    {
        var filtered = new PdfDictionary();
        foreach (var entry in dict)
        {
            var skip = false;
            foreach (var excluded in excludedKeys)
            {
                if (entry.Key.Equals(excluded)) { skip = true; break; }
            }
            if (!skip) filtered.Set(entry.Key, entry.Value);
        }
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PdfWriter(buffer);
        filtered.WriteTo(writer);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(buffer.WrittenSpan);
        return hasher.GetHashAndReset();
    }

    private static void ValidateImageXObjectShape(PdfStream stream, string parameterName, bool isSMask)
    {
        var dict = stream.Dictionary;
        var role = isSMask ? "SMask Image XObject" : "Image XObject";

        // Subtype is the canonical "this is an Image XObject" marker — required.
        var subtype = dict.Get(PdfNames.Subtype);
        if (subtype is not PdfName subtypeName || !subtypeName.Equals(PdfNames.Image))
        {
            throw new ArgumentException(
                $"{role} validation failed: /Subtype must be /Image " +
                $"(got {subtype?.GetType().Name ?? "null"}). Use the appropriate XObject " +
                "builder (JpegImageXObject, PngImageXObject, RasterImageXObject) to construct " +
                "Image XObjects with the required dictionary keys.",
                parameterName);
        }
        // Type is optional per §8.9.5 Table 87, but if present must be /XObject.
        var typeValue = dict.Get(PdfNames.Type);
        if (typeValue is PdfName typeName && !typeName.Equals(PdfNames.XObject))
        {
            throw new ArgumentException(
                $"{role} /Type is /{typeName.Value}; expected /XObject (or omitted).",
                parameterName);
        }

        // Width / Height are required and must be strictly positive — per ISO
        // 32000-2:2020 §8.9.5 Table 87, Width and Height "shall be the width [or
        // height], in samples, of the image". Zero or negative samples don't
        // describe a paintable image, and PDF readers silently misrender or crash on
        // them.
        if (dict.Get(PdfNames.Width) is not PdfInteger widthInt)
        {
            throw new ArgumentException(
                $"{role} is missing required /Width (PdfInteger).",
                parameterName);
        }
        if (widthInt.Value <= 0)
        {
            throw new ArgumentException(
                $"{role} /Width must be > 0 (got {widthInt.Value}).",
                parameterName);
        }
        if (dict.Get(PdfNames.Height) is not PdfInteger heightInt)
        {
            throw new ArgumentException(
                $"{role} is missing required /Height (PdfInteger).",
                parameterName);
        }
        if (heightInt.Value <= 0)
        {
            throw new ArgumentException(
                $"{role} /Height must be > 0 (got {heightInt.Value}).",
                parameterName);
        }

        if (dict.Get(PdfNames.ColorSpace) is null)
        {
            throw new ArgumentException(
                $"{role} is missing required /ColorSpace.",
                parameterName);
        }

        // BitsPerComponent: the spec-allowed values for general Image XObjects are
        // 1, 2, 4, 8, 16 (§8.9.5 Table 89). For soft masks, §11.6 narrows this to
        // 8 or 16 — sub-byte alpha planes aren't permitted because alpha is a
        // continuous value, not an indexed lookup.
        if (dict.Get(PdfNames.BitsPerComponent) is not PdfInteger bpcInt)
        {
            throw new ArgumentException(
                $"{role} is missing required /BitsPerComponent (PdfInteger).",
                parameterName);
        }
        if (isSMask)
        {
            if (bpcInt.Value != 8 && bpcInt.Value != 16)
            {
                throw new ArgumentException(
                    $"SMask Image XObject /BitsPerComponent must be 8 or 16 per ISO 32000-2 §11.6 " +
                    $"(got {bpcInt.Value}).",
                    parameterName);
            }
        }
        else
        {
            if (bpcInt.Value is not (1 or 2 or 4 or 8 or 16))
            {
                throw new ArgumentException(
                    $"Image XObject /BitsPerComponent must be one of {{1, 2, 4, 8, 16}} per " +
                    $"ISO 32000-2 §8.9.5 Table 89 (got {bpcInt.Value}).",
                    parameterName);
            }
        }
    }

    // ───── Save ──────────────────────────────────────────────────────────────

    /// <summary>Render the document to a byte array.</summary>
    public byte[] Save()
    {
        var buf = new ArrayBufferWriter<byte>();
        SaveTo(buf);
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>SEC-5 — render the document, aborting DURING serialization once the output would exceed
    /// <paramref name="maxBytes"/> (rather than checking after the whole PDF has been materialized + copied).
    /// Throws <see cref="PdfOutputSizeExceededException"/> when the cap is crossed. A non-positive or
    /// <see cref="long.MaxValue"/> cap means "unlimited" and takes the plain <see cref="Save()"/> path
    /// (byte-identical, no per-write overhead).</summary>
    public byte[] Save(long maxBytes)
    {
        if (maxBytes <= 0 || maxBytes >= long.MaxValue)
        {
            return Save();
        }

        var buf = new ArrayBufferWriter<byte>();
        SaveTo(new BoundedBufferWriter(buf, maxBytes));
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>Render the document to <paramref name="output"/>.</summary>
    public void SaveTo(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfSaved();
        _saved = true;

        // Finalize each page: build page dict + attach content stream.
        var kids = new PdfArray();
        foreach (var page in _pages)
        {
            var (pageDict, contentBytes) = page.Finalize();
            // Phase 4 links (PR 4) — promote the page's annotations to INDIRECT objects and reference them
            // from /Annots (ISO 32000 §12.5.2: page /Annots entries are indirect references).
            if (page.PendingAnnotations is { Count: > 0 } annotations)
            {
                var annots = new PdfArray();
                foreach (var annotation in annotations)
                {
                    var annotRef = _writer.Objects.Allocate();
                    _writer.Objects.Assign(annotRef, annotation);
                    annots.Add(annotRef);
                }
                pageDict.Set(PdfNames.Annots, annots);
            }
            _writer.Objects.Assign(page.PageRef, pageDict);
            // Wrap the content bytes in a PdfStream so the existing writer can length-prefix
            // and emit them. Compression is opt-in (off for now — keeps debugging trivial).
            _writer.Objects.Assign(page.ContentsRef, new PdfStream(contentBytes));
            kids.Add(page.PageRef);
        }

        // Build the /Pages and /Catalog dictionaries.
        var pagesDict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Pages)
            .Set(PdfNames.Kids, kids)
            .Set(PdfNames.Count, new PdfInteger(_pages.Count));
        _writer.Objects.Assign(_pagesRef, pagesDict);

        var catalogDict = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, _pagesRef);
        // Phase 4 outlines (PR 4) — the document outline (bookmarks) from <h1>–<h6>.
        if (_outlineHeadings is { Count: > 0 })
            catalogDict.Set(PdfNames.Outlines, BuildOutlineTree(_outlineHeadings));
        _writer.Objects.Assign(_catalogRef, catalogDict);
        _writer.Trailer.Set(PdfNames.Root, _catalogRef);

        // Build /Info dict (always — Producer is always emitted).
        var info = BuildInfoDictionary();
        var infoRef = _writer.Objects.Allocate();
        _writer.Objects.Assign(infoRef, info);
        _writer.Trailer.Set(PdfNames.Info, infoRef);

        _writer.WriteTo(output);
    }

    // ───── Outline tree construction ─────────────────────────────────────────

    private sealed class OutlineNode
    {
        public required OutlineHeading Heading { get; init; }
        public List<OutlineNode> Children { get; } = new();
        public PdfIndirectRef Ref { get; set; } = null!;
    }

    /// <summary>Build the <c>/Outlines</c> tree from the document-order headings: nest each heading under
    /// the most recent heading of a SMALLER level (so an h3 nests under the preceding h2 under its h1), then
    /// emit one indirect outline item per node with /Title /Parent /First /Last /Next /Prev /Count /Dest.
    /// Returns the <c>/Outlines</c> root indirect ref for the catalog.</summary>
    private PdfIndirectRef BuildOutlineTree(List<OutlineHeading> headings)
    {
        var roots = new List<OutlineNode>();
        var stack = new List<OutlineNode>();
        foreach (var h in headings)
        {
            var node = new OutlineNode { Heading = h };
            while (stack.Count > 0 && stack[^1].Heading.Level >= h.Level) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) roots.Add(node); else stack[^1].Children.Add(node);
            stack.Add(node);
        }

        var rootRef = _writer.Objects.Allocate();
        AllocateOutlineRefs(roots);
        var (first, last, count) = EmitOutlineSiblings(roots, rootRef);
        var outlinesDict = new PdfDictionary().Set(PdfNames.Type, PdfNames.Outlines);
        if (first is not null) outlinesDict.Set(PdfNames.First, first).Set(PdfNames.Last, last!);
        outlinesDict.Set(PdfNames.Count, new PdfInteger(count));
        _writer.Objects.Assign(rootRef, outlinesDict);
        return rootRef;
    }

    private void AllocateOutlineRefs(List<OutlineNode> nodes)
    {
        foreach (var n in nodes)
        {
            n.Ref = _writer.Objects.Allocate();
            AllocateOutlineRefs(n.Children);
        }
    }

    /// <summary>Emit a sibling list of outline items under <paramref name="parentRef"/>; returns the first +
    /// last child refs and the count of (open) descendant items in this list.</summary>
    private (PdfIndirectRef? First, PdfIndirectRef? Last, int Count) EmitOutlineSiblings(
        List<OutlineNode> nodes, PdfIndirectRef parentRef)
    {
        if (nodes.Count == 0) return (null, null, 0);
        var total = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var h = n.Heading;
            var dict = new PdfDictionary()
                .Set(PdfNames.Title, EncodeMetadataString(SanitizeMetadataString(h.Title)))
                .Set(PdfNames.Parent, parentRef)
                .Set(PdfNames.Dest, new PdfArray()
                    .Add(h.Page.PageRef).Add(new PdfName("XYZ"))
                    .Add(PdfNull.Instance).Add(new PdfReal(h.TopPt)).Add(PdfNull.Instance));
            if (i > 0) dict.Set(PdfNames.Prev, nodes[i - 1].Ref);
            if (i < nodes.Count - 1) dict.Set(PdfNames.Next, nodes[i + 1].Ref);

            var (cFirst, cLast, cCount) = EmitOutlineSiblings(n.Children, n.Ref);
            if (cFirst is not null)
            {
                dict.Set(PdfNames.First, cFirst).Set(PdfNames.Last, cLast!);
                dict.Set(PdfNames.Count, new PdfInteger(cCount)); // positive = subtree shown open
            }
            _writer.Objects.Assign(n.Ref, dict);
            total += 1 + cCount;
        }
        return (nodes[0].Ref, nodes[^1].Ref, total);
    }

    private PdfDictionary BuildInfoDictionary()
    {
        // The Producer is always emitted (NetPdf identifies itself); other fields are
        // emitted only when set by the caller.
        // Per Phase C C-3 (PR #17 review user-recommendation #3) — every
        // author-controlled metadata field flows through
        // SanitizeMetadataString first (strips C0 / DEL / C1 + caps length),
        // then EncodeMetadataString picks the right PDF text-string form:
        //   - ASCII-only sanitized text → PdfLiteralString (denser).
        //   - Any non-ASCII char in sanitized text → PdfHexString with
        //     UTF-16BE + BOM (PDF Reference §7.9.2.2 "PDFDocEncoded /
        //     UTF-16BE text string").
        // Pre-fix the sanitizer left non-ASCII chars unchanged + handed
        // them to PdfLiteralString which rejected anything > 0x7E. So a
        // legitimate Title like "Résumé" threw at Save time. Producer is
        // library-controlled (always ASCII), so it skips both.
        var info = new PdfDictionary();
        info.Set(PdfNames.Producer, new PdfLiteralString(Producer));
        if (Title is not null) info.Set(PdfNames.Title, EncodeMetadataString(SanitizeMetadataString(Title)));
        if (Author is not null) info.Set(PdfNames.Author, EncodeMetadataString(SanitizeMetadataString(Author)));
        if (Subject is not null) info.Set(PdfNames.Subject, EncodeMetadataString(SanitizeMetadataString(Subject)));
        if (Keywords is not null) info.Set(PdfNames.Keywords, EncodeMetadataString(SanitizeMetadataString(Keywords)));
        if (Creator is not null) info.Set(PdfNames.Creator, EncodeMetadataString(SanitizeMetadataString(Creator)));
        if (CreationDate is { } cd) info.Set(PdfNames.CreationDate, new PdfLiteralString(FormatPdfDate(cd)));
        if (ModDate is { } md) info.Set(PdfNames.ModDate, new PdfLiteralString(FormatPdfDate(md)));
        return info;
    }

    /// <summary>Per PR #17 review user-recommendation #3 — encode a
    /// sanitized metadata string into the PDF text-string form that
    /// fits its character set. ASCII (≤ 0x7E) inputs go through
    /// <see cref="PdfLiteralString"/>; anything else routes to
    /// <see cref="PdfHexString"/> with UTF-16BE + BOM per ISO 32000-2 §7.9.2.2.
    /// Both forms render to the same text in PDF viewers; the routing
    /// is purely a representation decision.</summary>
    internal static PdfObject EncodeMetadataString(string sanitized)
    {
        ArgumentNullException.ThrowIfNull(sanitized);
        // ASCII fast-path — most production metadata is ASCII (English-only
        // Title/Author/Subject); avoid the UTF-16BE allocation when we can.
        for (var i = 0; i < sanitized.Length; i++)
        {
            if (sanitized[i] > 0x7E)
            {
                return PdfHexString.FromUtf16BeWithBom(sanitized);
            }
        }
        return new PdfLiteralString(sanitized);
    }

    /// <summary>Per Phase C C-3 — maximum length (UTF-16 chars) of any
    /// single PDF metadata string. 4 KiB easily holds the longest sane
    /// document Title / Author / Subject. Beyond that is almost certainly
    /// an attacker piling kilobytes of poison into a <c>&lt;title&gt;</c>.</summary>
    internal const int MaxMetadataChars = 4096;

    /// <summary>Per Phase C C-3 — strip C0 (0x00..0x1F, except TAB/CR/LF)
    /// + DEL (0x7F) + C1 (0x80..0x9F) control characters from
    /// <paramref name="raw"/>; replace with the Unicode REPLACEMENT
    /// CHARACTER (U+FFFD). Then cap at <see cref="MaxMetadataChars"/>
    /// with the HORIZONTAL ELLIPSIS (U+2026). The control character set
    /// mirrors the CSS-side
    /// <c>NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize</c>
    /// surface so PDF metadata gets the same hardening Phase A landed on
    /// CSS diagnostic text.</summary>
    /// <remarks>
    /// <para>The PDF Reference §7.9.2 says <c>PdfLiteralString</c> bytes
    /// can be any byte 0x00..0xFF, but viewers rendering the catalog
    /// (Adobe Acrobat, Foxit, browsers' built-in viewers) display the
    /// string as text. Embedded NUL bytes truncate display in some
    /// viewers, ANSI escapes leak into terminal-style log shippers, and
    /// some viewers expose Title in window titles + clipboard where
    /// control characters are unsafe. The Producer field is exempted
    /// because it's library-controlled.</para>
    /// <para>Per PR #17 review user-recommendation #3 the sanitizer can
    /// produce non-ASCII output. Per PR #17 Copilot review #4 the docs
    /// now match the implementation. The downstream
    /// <see cref="EncodeMetadataString"/> routes ASCII output through
    /// <see cref="PdfLiteralString"/> + non-ASCII through
    /// <see cref="PdfHexString.FromUtf16BeWithBom"/>, so U+FFFD / U+2026
    /// flow through the hex-string path cleanly without
    /// <c>PdfLiteralString</c>'s &gt; 0x7E rejection.</para>
    /// </remarks>
    internal static string SanitizeMetadataString(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0) return raw;
        // Two-pass: count what needs replacement so the no-op fast path
        // returns the original reference (avoids gratuitous allocations
        // for the common clean-input case).
        var dirty = false;
        for (var i = 0; i < raw.Length; i++)
        {
            if (IsForbidden(raw[i])) { dirty = true; break; }
        }
        if (!dirty && raw.Length <= MaxMetadataChars) return raw;

        // U+FFFD REPLACEMENT CHARACTER + U+2026 HORIZONTAL ELLIPSIS as
        // per the documented contract. Both are non-ASCII so the
        // sanitized output may include them, which is fine: EncodeMetadataString
        // routes any non-ASCII output through PdfHexString (UTF-16BE + BOM)
        // rather than PdfLiteralString.
        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, MaxMetadataChars + 1));
        for (var i = 0; i < raw.Length && sb.Length < MaxMetadataChars; i++)
        {
            var c = raw[i];
            sb.Append(IsForbidden(c) ? '�' : c);
        }
        if (raw.Length > MaxMetadataChars) sb.Append('…');
        return sb.ToString();
    }

    private static bool IsForbidden(char c) =>
        // C0 except TAB (0x09), LF (0x0A), CR (0x0D) which are legitimate
        // text control chars in PDF strings.
        (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
        // DEL.
        || c == 0x7F
        // C1.
        || (c >= 0x80 && c <= 0x9F);

    /// <summary>
    /// Format a date per ISO 32000-2:2020 §7.9.4: <c>D:YYYYMMDDHHmmSSOHH'mm'</c>. The
    /// <c>O</c> sign is <c>+</c> / <c>-</c> / <c>Z</c> for UTC offset.
    /// </summary>
    private static string FormatPdfDate(DateTimeOffset value)
    {
        var local = value;
        var sign = local.Offset.Ticks switch
        {
            0L => "Z",
            > 0L => "+",
            _ => "-",
        };
        if (sign == "Z")
        {
            return local.UtcDateTime.ToString("'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture);
        }
        var offsetHours = Math.Abs(local.Offset.Hours);
        var offsetMinutes = Math.Abs(local.Offset.Minutes);
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{local:yyyyMMddHHmmss}{sign}{offsetHours:D2}'{offsetMinutes:D2}'");
    }

    private static string ComputeContentKey(ImageXObjectResult result)
    {
        // Dedup key covers the entire image graph: primary stream payload + dictionary
        // for the Image XObject, plus the same for the SMask when present. Hashing the
        // dictionary (not just the payload) is required for correctness: two pixel
        // buffers with identical bytes but different /ColorSpace, /BitsPerComponent,
        // /Filter, /DecodeParms, /Decode, or /Mask render entirely differently. Without
        // dictionary inclusion, two RGBA8888 pixel buffers that happen to coincide
        // post-FlateDecode but have different color models would silently dedupe — a
        // visual-corruption bug.
        var imageDigest = HashStream(result.Image);
        if (result.SMask is null)
        {
            return $"img1:{Convert.ToHexString(imageDigest)}";
        }
        var smaskDigest = HashStream(result.SMask);
        return $"img2:{Convert.ToHexString(imageDigest)}|{Convert.ToHexString(smaskDigest)}";
    }

    private static byte[] HashStream(PdfStream stream)
    {
        // SHA-256 over (payload bytes || dictionary canonical bytes). The dictionary's
        // own writer produces deterministic output (insertion order preserved by
        // OrderedDictionary), so two PdfStreams built from the same data + same Set()
        // sequence hash identically — that's the desired dedup grain.
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(stream.Data);
        var dictBuffer = new ArrayBufferWriter<byte>();
        var dictWriter = new PdfWriter(dictBuffer);
        stream.Dictionary.WriteTo(dictWriter);
        hasher.AppendData(dictBuffer.WrittenSpan);
        return hasher.GetHashAndReset();
    }

    private void ThrowIfSaved()
    {
        if (_saved)
        {
            throw new InvalidOperationException(
                "PdfDocument.Save has already been invoked — further mutation is not supported. Build a new PdfDocument for a second save.");
        }
    }
}

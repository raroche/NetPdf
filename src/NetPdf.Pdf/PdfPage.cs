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
    private readonly PdfDictionary _patternsResource = new();   // tiling-patterns cycle.
    private readonly PdfDictionary _shadingResource = new();     // Phase 4 gradients.
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

    /// <summary>Fill an axis-aligned rectangle with a registered TILING PATTERN
    /// (tiling-patterns cycle, ISO 32000-2 §8.7.3): selects the <c>/Pattern</c> colour space,
    /// sets the pattern as the fill colour (<c>scn</c>), fills the rect, and restores state —
    /// <c>q /Pattern cs /Pn scn x y w h re f Q</c>. The pattern must already be registered with
    /// the parent document (<see cref="PdfDocument.RegisterTilingPattern"/>); the grid's phase
    /// lives in the PATTERN's <c>/Matrix</c> (pattern space anchors to DEFAULT user space, not
    /// the CTM at fill time — §8.7.3.1). Idempotent per referenced pattern: one <c>/Pattern</c>
    /// resource entry per object, same name returned.</summary>
    public string FillRectangleWithPattern(
        PdfIndirectRef patternRef, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(patternRef);
        ThrowIfFinalized();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException(
                $"FillRectangleWithPattern coordinates must be finite; got x={x}, y={y}, width={width}, height={height}.");
        }
        if (width <= 0 || height <= 0) return string.Empty;

        // Dedup by the referenced object, like AddFont — full-identity match incl. StoreId.
        string? resourceName = null;
        foreach (var entry in _patternsResource)
        {
            if (entry.Value is PdfIndirectRef existing && existing.HasSameTarget(patternRef))
            {
                resourceName = entry.Key.Value;
                break;
            }
        }
        if (resourceName is null)
        {
            resourceName = $"P{_patternsResource.Count + 1}";
            _patternsResource.Set(new PdfName(resourceName), patternRef);
        }

        var sb = new StringBuilder(64);
        sb.Append("q /Pattern cs /").Append(resourceName).Append(" scn ");
        AppendNumber(sb, x); sb.Append(' ');
        AppendNumber(sb, y); sb.Append(' ');
        AppendNumber(sb, width); sb.Append(' ');
        AppendNumber(sb, height); sb.Append(" re f Q\n");
        AppendContent(sb.ToString());
        return resourceName;
    }

    /// <summary>Phase 4 gradients — paint a registered shading (axial
    /// <see cref="PdfDocument.RegisterAxialShading"/> OR radial
    /// <see cref="PdfDocument.RegisterRadialShading"/> — the <c>sh</c> operator is
    /// shading-type-agnostic) clipped to the rectangle
    /// <c>(<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>,
    /// <paramref name="height"/>)</c> (PDF points, bottom-left origin). Emits
    /// <c>q [/GSn gs] &lt;clip path&gt; W n /Shn sh Q</c>: the shading's <c>/Coords</c>
    /// (baked into the shading object) define the gradient axis in page space, and
    /// <c>/Extend</c> fills the whole clip. A non-null <paramref name="radii"/> with a
    /// positive corner clips to a rounded rect (border-radius); otherwise a plain rect.
    /// <paramref name="alpha"/> &lt; 1 applies a constant-alpha ExtGState. Dedups the
    /// shading into the page's <c>/Shading</c> resource by target identity. No-op (returns
    /// empty) for a non-positive rectangle.</summary>
    public string PaintShadingInRect(
        PdfIndirectRef shadingRef, double x, double y, double width, double height,
        CornerRadii? radii = null, double alpha = 1.0,
        // Phase 4 gradients (PR 1) — an optional CTM (a b c d e f) concatenated AFTER the clip
        // (`W n`) and BEFORE the `sh`, so the clip stays in PAGE space while the shading's /Coords
        // are interpreted in this transformed space. Used to render a radial shading registered as a
        // unit CIRCLE as an ELLIPSE (a scale about the center). Null → no transform (byte-identical).
        (double A, double B, double C, double D, double E, double F)? shadingCtm = null)
    {
        ArgumentNullException.ThrowIfNull(shadingRef);
        ThrowIfFinalized();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width)
            || !double.IsFinite(height) || !double.IsFinite(alpha))
        {
            throw new ArgumentException(
                $"PaintShadingInRect args must be finite; got x={x}, y={y}, w={width}, h={height}, alpha={alpha}.");
        }
        if (shadingCtm is { } cm && !(double.IsFinite(cm.A) && double.IsFinite(cm.B) && double.IsFinite(cm.C)
            && double.IsFinite(cm.D) && double.IsFinite(cm.E) && double.IsFinite(cm.F)))
        {
            throw new ArgumentException($"PaintShadingInRect CTM must be finite; got [{cm.A} {cm.B} {cm.C} {cm.D} {cm.E} {cm.F}].");
        }
        if (width <= 0 || height <= 0) return string.Empty;

        string? resourceName = null;
        foreach (var entry in _shadingResource)
        {
            if (entry.Value is PdfIndirectRef existing && existing.HasSameTarget(shadingRef))
            {
                resourceName = entry.Key.Value;
                break;
            }
        }
        if (resourceName is null)
        {
            resourceName = $"Sh{_shadingResource.Count + 1}";
            _shadingResource.Set(new PdfName(resourceName), shadingRef);
        }

        alpha = Math.Clamp(alpha, 0.0, 1.0);
        var sb = new StringBuilder(160);
        sb.Append("q ");
        if (alpha < 1.0) sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        var nr = radii?.NormalizedFor(width, height);
        if (nr is { AnyPositive: true } rounded)
        {
            AppendRoundedRectPath(sb, x, y, width, height, rounded);
        }
        else
        {
            AppendNumber(sb, x); sb.Append(' ');
            AppendNumber(sb, y); sb.Append(' ');
            AppendNumber(sb, width); sb.Append(' ');
            AppendNumber(sb, height); sb.Append(" re ");
        }
        sb.Append("W n ");
        if (shadingCtm is { } m)
        {
            AppendNumber(sb, m.A); sb.Append(' ');
            AppendNumber(sb, m.B); sb.Append(' ');
            AppendNumber(sb, m.C); sb.Append(' ');
            AppendNumber(sb, m.D); sb.Append(' ');
            AppendNumber(sb, m.E); sb.Append(' ');
            AppendNumber(sb, m.F); sb.Append(" cm ");
        }
        sb.Append('/').Append(resourceName).Append(" sh Q\n");
        AppendContent(sb.ToString());
        return resourceName;
    }

    /// <summary>Fill an axis-aligned ROUNDED rectangle (border-radius cycle) — the same contract
    /// as <see cref="FillRectangle"/> (PDF points, bottom-left origin, clamped colour/alpha,
    /// non-positive dimensions no-op, deterministic operator text) with the four corners rounded
    /// at <paramref name="radius"/> via cubic Béziers (the k ≈ 0.5523 circle approximation,
    /// ISO 32000-2 §8.5.2.2 <c>c</c> operator). The radius CLAMPS to half the smaller dimension
    /// (a larger value degenerates to a capsule, per CSS B&amp;B §5.5's overlap rule for the
    /// uniform case); a non-positive radius delegates to the plain rectangle.</summary>
    public void FillRoundedRectangle(
        double x, double y, double width, double height, double radius,
        double r, double g, double b, double alpha = 1.0)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || !double.IsFinite(radius))
        {
            throw new ArgumentException(
                $"FillRoundedRectangle arguments must be finite; got x={x}, y={y}, width={width}, height={height}, radius={radius}.");
        }
        if (!double.IsFinite(alpha))
        {
            throw new ArgumentException(
                $"FillRoundedRectangle alpha must be finite; got {alpha}.", nameof(alpha));
        }
        if (width <= 0 || height <= 0) return;
        if (radius <= 0)
        {
            FillRectangle(x, y, width, height, r, g, b, alpha);
            return;
        }

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);
        alpha = Math.Clamp(alpha, 0.0, 1.0);
        var rad = Math.Min(radius, Math.Min(width, height) / 2.0);
        const double kappa = 0.55228474983079; // 4/3 × (√2 − 1) — the cubic circle-quadrant constant.
        var k = rad * (1.0 - kappa);

        // q [/GSn gs] <r g b> rg <path: m, 4 × (l + c)> f Q — counter-clockwise from the
        // bottom edge's left arc-end; each corner is one Bézier quadrant.
        var sb = new StringBuilder(256);
        sb.Append("q ");
        if (alpha < 1.0)
        {
            sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        }
        AppendNumber(sb, r); sb.Append(' ');
        AppendNumber(sb, g); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(" rg ");
        void P(double vx, double vy) { AppendNumber(sb, vx); sb.Append(' '); AppendNumber(sb, vy); sb.Append(' '); }
        P(x + rad, y); sb.Append("m ");
        P(x + width - rad, y); sb.Append("l ");
        P(x + width - k, y); P(x + width, y + k); P(x + width, y + rad); sb.Append("c ");
        P(x + width, y + height - rad); sb.Append("l ");
        P(x + width, y + height - k); P(x + width - k, y + height); P(x + width - rad, y + height); sb.Append("c ");
        P(x + rad, y + height); sb.Append("l ");
        P(x + k, y + height); P(x, y + height - k); P(x, y + height - rad); sb.Append("c ");
        P(x, y + rad); sb.Append("l ");
        P(x, y + k); P(x + k, y); P(x + rad, y); sb.Append("c ");
        sb.Append("f Q\n");
        AppendContent(sb.ToString());
    }

    /// <summary>Fill a rectangle with PER-CORNER elliptical <c>border-radius</c> (CSS Backgrounds &amp;
    /// Borders 3 §4.1) — the general form of the uniform single-radius
    /// <see cref="FillRoundedRectangle(double,double,double,double,double,double,double,double,double)"/>.
    /// Radii are normalized (clamped ≥ 0, §4.2 overlap-scaled to half-extents) before the path is built;
    /// a set with NO positive radius delegates to the plain rectangle. Same colour/alpha/finite-validation
    /// contract as the other fills.</summary>
    public void FillRoundedRectangle(
        double x, double y, double width, double height, CornerRadii radii,
        double r, double g, double b, double alpha = 1.0)
    {
        ThrowIfFinalized();
        ValidateRoundedArgs(x, y, width, height, alpha);
        if (width <= 0 || height <= 0) return;
        var nr = radii.NormalizedFor(width, height);
        if (!nr.AnyPositive) { FillRectangle(x, y, width, height, r, g, b, alpha); return; }

        r = Math.Clamp(r, 0.0, 1.0); g = Math.Clamp(g, 0.0, 1.0); b = Math.Clamp(b, 0.0, 1.0);
        alpha = Math.Clamp(alpha, 0.0, 1.0);
        var sb = new StringBuilder(320);
        sb.Append("q ");
        if (alpha < 1.0) sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        AppendNumber(sb, r); sb.Append(' ');
        AppendNumber(sb, g); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(" rg ");
        AppendRoundedRectPath(sb, x, y, width, height, nr);
        sb.Append("f Q\n");
        AppendContent(sb.ToString());
    }

    /// <summary>Fill the RING (annulus) between an OUTER and an INNER per-corner rounded rectangle — the
    /// region inside the outer path but outside the inner one — via an even-odd fill
    /// (<c>q [gs] r g b rg &lt;outer path&gt; &lt;inner path&gt; f* Q</c>). This is how a UNIFORM rounded
    /// BORDER paints (border-radius-completion cycle, Task 2): the outer path is the border box, so its
    /// outer corner radius is EXACT for any border width (unlike a centerline stroke, which loses a small
    /// radius under a thick border); the inner path is the padding box with radii reduced by the border
    /// width. An empty/degenerate inner box fills the whole outer rounded rect (the border covers the
    /// box). Each rounded path falls back to a plain <c>re</c> rectangle subpath when its radii are all
    /// zero. Uses the FILL constant-alpha (<c>/ca</c>) — correct, since this is a fill, not a stroke. Same
    /// colour/alpha/finite-validation contract as the other fills.</summary>
    public void FillRoundedRectangleRing(
        double outerX, double outerY, double outerWidth, double outerHeight, CornerRadii outerRadii,
        double innerX, double innerY, double innerWidth, double innerHeight, CornerRadii innerRadii,
        double r, double g, double b, double alpha = 1.0)
    {
        ThrowIfFinalized();
        ValidateRoundedArgs(outerX, outerY, outerWidth, outerHeight, alpha);
        ValidateRoundedArgs(innerX, innerY, innerWidth, innerHeight, alpha);
        if (outerWidth <= 0 || outerHeight <= 0) return;

        r = Math.Clamp(r, 0.0, 1.0); g = Math.Clamp(g, 0.0, 1.0); b = Math.Clamp(b, 0.0, 1.0);
        alpha = Math.Clamp(alpha, 0.0, 1.0);
        var sb = new StringBuilder(560);
        sb.Append("q ");
        if (alpha < 1.0) sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        AppendNumber(sb, r); sb.Append(' ');
        AppendNumber(sb, g); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(" rg ");
        var outerNr = outerRadii.NormalizedFor(outerWidth, outerHeight);
        if (outerNr.AnyPositive) AppendRoundedRectPath(sb, outerX, outerY, outerWidth, outerHeight, outerNr);
        else AppendRectPath(sb, outerX, outerY, outerWidth, outerHeight);
        if (innerWidth > 0 && innerHeight > 0)
        {
            // Even-odd: the inner subpath cuts the annulus out of the outer fill (winding-direction
            // independent). A border thicker than the radius leaves a sharp inner corner — exactly CSS.
            var innerNr = innerRadii.NormalizedFor(innerWidth, innerHeight);
            if (innerNr.AnyPositive) AppendRoundedRectPath(sb, innerX, innerY, innerWidth, innerHeight, innerNr);
            else AppendRectPath(sb, innerX, innerY, innerWidth, innerHeight);
            sb.Append("f* Q\n");
        }
        else
        {
            sb.Append("f Q\n");   // no inner cut-out (border ≥ half the box) → a solid rounded rect
        }
        AppendContent(sb.ToString());
    }

    /// <summary>Push the graphics state and intersect the clip with a PER-CORNER rounded rectangle —
    /// <c>q &lt;path&gt; W n</c> (the rounded analogue of <see cref="BeginRectangleClip"/>). Everything
    /// painted until the balancing <see cref="RestoreGraphicsState"/> is clipped to the rounded box. An
    /// all-zero radius set (or a degenerate size) falls back to the rectangular clip (identical result,
    /// fewer operators). Callers MUST balance with <see cref="RestoreGraphicsState"/>.</summary>
    public void BeginRoundedRectangleClip(double x, double y, double width, double height, CornerRadii radii)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentException(
                $"BeginRoundedRectangleClip coordinates must be finite; got x={x}, y={y}, width={width}, height={height}.");
        var nr = (width > 0 && height > 0) ? radii.NormalizedFor(width, height) : default;
        if (!nr.AnyPositive) { BeginRectangleClip(x, y, width, height); return; }
        var sb = new StringBuilder(300);
        sb.Append("q ");
        AppendRoundedRectPath(sb, x, y, width, height, nr);
        sb.Append("W n\n");
        AppendContent(sb.ToString());
    }

    /// <summary>Push an EVEN-ODD clip to the region inside the outer rect but OUTSIDE the inner rounded box
    /// (<c>q &lt;outer path&gt; &lt;inner path&gt; W* n</c>) — the analogue of <see cref="FillRoundedRectangleRing"/>
    /// for clipping. Used to knock the border box out of an OUTSET box-shadow (CSS B&amp;B §6.1.1 — an
    /// outset shadow is not painted inside the element). A degenerate inner box (≤ 0) clips to the outer
    /// rect alone. Callers MUST balance with <see cref="RestoreGraphicsState"/>.</summary>
    public void BeginRoundedRectangleHoleClip(
        double outerX, double outerY, double outerWidth, double outerHeight,
        double innerX, double innerY, double innerWidth, double innerHeight, CornerRadii innerRadii)
    {
        ThrowIfFinalized();
        ValidateRoundedArgs(outerX, outerY, outerWidth, outerHeight, 1.0);
        ValidateRoundedArgs(innerX, innerY, innerWidth, innerHeight, 1.0);
        if (outerWidth <= 0 || outerHeight <= 0) return;
        var sb = new StringBuilder(420);
        sb.Append("q ");
        AppendRectPath(sb, outerX, outerY, outerWidth, outerHeight);
        if (innerWidth > 0 && innerHeight > 0)
        {
            var inr = innerRadii.NormalizedFor(innerWidth, innerHeight);
            if (inr.AnyPositive) AppendRoundedRectPath(sb, innerX, innerY, innerWidth, innerHeight, inr);
            else AppendRectPath(sb, innerX, innerY, innerWidth, innerHeight);
        }
        sb.Append("W* n\n");   // even-odd: keep the area inside the outer rect and outside the inner box
        AppendContent(sb.ToString());
    }

    private static void ValidateRoundedArgs(double x, double y, double width, double height, double alpha)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentException(
                $"Rounded-rectangle arguments must be finite; got x={x}, y={y}, width={width}, height={height}.");
        if (!double.IsFinite(alpha))
            throw new ArgumentException($"Rounded-rectangle alpha must be finite; got {alpha}.", nameof(alpha));
    }

    /// <summary>Append a per-corner rounded-rectangle PATH (no paint operator) to <paramref name="sb"/>:
    /// <c>m</c> then four edge <c>l</c> + corner <c>c</c> pairs + <c>h</c>, counter-clockwise from the
    /// bottom edge (matching the uniform overload's vertex order). Each corner is one cubic-Bézier
    /// quadrant (k ≈ 0.5523, ISO 32000-2 §8.5.2.2) using its own horizontal/vertical radius — a circle
    /// for X == Y, an ellipse otherwise. CSS corner names map to this bottom-left-origin space: top-left
    /// → (x, y+h), bottom-left → (x, y). Radii MUST already be normalized
    /// (<see cref="CornerRadii.NormalizedFor"/>).</summary>
    private static void AppendRoundedRectPath(
        StringBuilder sb, double x, double y, double width, double height, CornerRadii radii)
    {
        const double kappa = 0.55228474983079; // 4/3 × (√2 − 1) — the cubic circle-quadrant constant.
        double tlx = radii.TopLeftX, tly = radii.TopLeftY;
        double trx = radii.TopRightX, tryy = radii.TopRightY;
        double brx = radii.BottomRightX, bry = radii.BottomRightY;
        double blx = radii.BottomLeftX, bly = radii.BottomLeftY;
        static double K(double radius) => radius * (1.0 - kappa);

        void P(double vx, double vy) { AppendNumber(sb, vx); sb.Append(' '); AppendNumber(sb, vy); sb.Append(' '); }
        // Bottom edge → bottom-right corner.
        P(x + blx, y); sb.Append("m ");
        P(x + width - brx, y); sb.Append("l ");
        P(x + width - K(brx), y); P(x + width, y + K(bry)); P(x + width, y + bry); sb.Append("c ");
        // Right edge → top-right corner.
        P(x + width, y + height - tryy); sb.Append("l ");
        P(x + width, y + height - K(tryy)); P(x + width - K(trx), y + height); P(x + width - trx, y + height); sb.Append("c ");
        // Top edge → top-left corner.
        P(x + tlx, y + height); sb.Append("l ");
        P(x + K(tlx), y + height); P(x, y + height - K(tly)); P(x, y + height - tly); sb.Append("c ");
        // Left edge → bottom-left corner (closes to the start).
        P(x, y + bly); sb.Append("l ");
        P(x, y + K(bly)); P(x + K(blx), y); P(x + blx, y); sb.Append("c ");
        sb.Append("h ");
    }

    /// <summary>Append a plain rectangle subpath (<c>x y w h re</c>) — the zero-radius fallback the ring
    /// fill uses for a square outer/inner edge, combinable with a Bézier subpath under one fill.</summary>
    private static void AppendRectPath(StringBuilder sb, double x, double y, double width, double height)
    {
        AppendNumber(sb, x); sb.Append(' ');
        AppendNumber(sb, y); sb.Append(' ');
        AppendNumber(sb, width); sb.Append(' ');
        AppendNumber(sb, height); sb.Append(" re ");
    }

    /// <summary>Phase 4 clip-path (PR 3) — push the graphics state and intersect the clip with a
    /// POLYGON (<c>q x0 y0 m x1 y1 l … h W n</c>) for <c>clip-path: polygon(...)</c>. Points are PDF
    /// points (bottom-left origin). Fewer than 3 points → no clip change (the <c>q</c> still opens, so
    /// the caller still balances with <see cref="RestoreGraphicsState"/>). Non-finite points throw.</summary>
    public void BeginPolygonClip(IReadOnlyList<(double X, double Y)> points)
    {
        ThrowIfFinalized();
        ArgumentNullException.ThrowIfNull(points);
        var sb = new StringBuilder(16 + points.Count * 16);
        sb.Append("q ");
        if (points.Count >= 3)
        {
            for (var i = 0; i < points.Count; i++)
            {
                var (px, py) = points[i];
                if (!double.IsFinite(px) || !double.IsFinite(py))
                    throw new ArgumentException($"BeginPolygonClip points must be finite; got ({px},{py}).");
                AppendNumber(sb, px); sb.Append(' '); AppendNumber(sb, py);
                sb.Append(i == 0 ? " m " : " l ");
            }
            sb.Append("h W n");
        }
        sb.Append('\n');
        AppendContent(sb.ToString());
    }

    /// <summary>Phase 4 clip-path (PR 3) — push the graphics state and intersect the clip with an
    /// ELLIPSE centered (<paramref name="cx"/>, <paramref name="cy"/>) with radii
    /// (<paramref name="rx"/>, <paramref name="ry"/>) via four cubic-Bézier quadrants (k ≈ 0.5523) —
    /// for <c>clip-path: circle()</c> (rx == ry) / <c>ellipse()</c>. PDF points, bottom-left origin.
    /// A non-positive radius opens the <c>q</c> with no clip (a zero-area clip). Callers MUST balance
    /// with <see cref="RestoreGraphicsState"/>.</summary>
    public void BeginEllipseClip(double cx, double cy, double rx, double ry)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(rx) || !double.IsFinite(ry))
            throw new ArgumentException($"BeginEllipseClip args must be finite; got c=({cx},{cy}) r=({rx},{ry}).");
        var sb = new StringBuilder(160);
        sb.Append("q ");
        if (rx > 0 && ry > 0)
        {
            const double k = 0.55228474983079;
            var kx = rx * k;
            var ky = ry * k;
            void P(double x, double y) { AppendNumber(sb, x); sb.Append(' '); AppendNumber(sb, y); sb.Append(' '); }
            P(cx + rx, cy); sb.Append("m ");
            P(cx + rx, cy + ky); P(cx + kx, cy + ry); P(cx, cy + ry); sb.Append("c ");
            P(cx - kx, cy + ry); P(cx - rx, cy + ky); P(cx - rx, cy); sb.Append("c ");
            P(cx - rx, cy - ky); P(cx - kx, cy - ry); P(cx, cy - ry); sb.Append("c ");
            P(cx + kx, cy - ry); P(cx + rx, cy - ky); P(cx + rx, cy); sb.Append("c ");
            sb.Append("h W n");
        }
        sb.Append('\n');
        AppendContent(sb.ToString());
    }

    /// <summary>
    /// Push the graphics state and intersect the clip path with an axis-aligned rectangle —
    /// <c>q &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; re W n</c> (ISO 32000-2 §8.5.4: <c>W</c> sets the
    /// clip from the current path, <c>n</c> ends the path without painting it). Everything painted
    /// until the balancing <see cref="RestoreGraphicsState"/> renders only inside the rectangle.
    /// Coordinates are PDF points, bottom-left origin (the <c>re</c> convention). A non-positive
    /// <paramref name="width"/>/<paramref name="height"/> is CLAMPED to 0 and emitted — a DEGENERATE
    /// zero-area clip that paints nothing (deliberate: a clip request must never silently widen, and a
    /// negative <c>re</c> dimension is NOT equivalent to an empty rectangle — it would flip the rect
    /// across its origin; contrast <see cref="FillRectangle"/>'s no-op). Callers MUST balance every call
    /// with <see cref="RestoreGraphicsState"/> — the page does not auto-close at finalize.
    /// </summary>
    public void BeginRectangleClip(double x, double y, double width, double height)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException(
                $"BeginRectangleClip coordinates must be finite; got x={x}, y={y}, width={width}, height={height}.");
        }

        var sb = new StringBuilder(48);
        sb.Append("q ");
        AppendNumber(sb, x); sb.Append(' ');
        AppendNumber(sb, y); sb.Append(' ');
        AppendNumber(sb, Math.Max(width, 0)); sb.Append(' ');
        AppendNumber(sb, Math.Max(height, 0)); sb.Append(" re W n\n");
        AppendContent(sb.ToString());
    }

    /// <summary>Pop the graphics state (<c>Q</c>) — balances <see cref="BeginRectangleClip"/>,
    /// restoring the clip (and any other state) saved by its <c>q</c>.</summary>
    public void RestoreGraphicsState()
    {
        ThrowIfFinalized();
        AppendContent("Q\n");
    }

    /// <summary>Phase 4 transforms — push the graphics state and concatenate a 2D affine CTM
    /// (<c>q a b c d e f cm</c>, ISO 32000-2 §8.3.4): a point (x, y) maps to
    /// (a·x + c·y + e, b·x + d·y + f) in PDF user space. Callers MUST balance with
    /// <see cref="RestoreGraphicsState"/>. Used to wrap a transformed element's painting (its
    /// decoration + text) so it renders under the CSS <c>transform</c>.</summary>
    public void BeginTransform(double a, double b, double c, double d, double e, double f)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)
            || !double.IsFinite(d) || !double.IsFinite(e) || !double.IsFinite(f))
        {
            throw new ArgumentException(
                $"BeginTransform matrix must be finite; got [{a} {b} {c} {d} {e} {f}].");
        }
        var sb = new StringBuilder(64);
        sb.Append("q ");
        AppendNumber(sb, a); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(' ');
        AppendNumber(sb, c); sb.Append(' ');
        AppendNumber(sb, d); sb.Append(' ');
        AppendNumber(sb, e); sb.Append(' ');
        AppendNumber(sb, f); sb.Append(" cm\n");
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
    /// per-glyph positioning is a follow-up). A partial <paramref name="alpha"/> (&lt; 1) is
    /// composited via the same per-page constant-alpha (<c>/ca</c>) ExtGState the fills use
    /// (selected with <c>gs</c>); opaque (<paramref name="alpha"/> = 1) emits no ExtGState.
    /// <paramref name="r"/>/<paramref name="g"/>/<paramref name="b"/>/<paramref name="alpha"/>
    /// are in [0, 1] and clamped. The run is wrapped in its own <c>q</c> / <c>Q</c> pair so the
    /// color + alpha + text state don't leak. Empty input is a no-op; a
    /// <paramref name="fontSizePt"/> of 0 emits an (invisible) zero-size run.
    /// </summary>
    public void ShowGlyphs(
        PdfName fontResourceName, double fontSizePt, double xPt, double yPt,
        ReadOnlySpan<ushort> glyphIds, double r, double g, double b, double alpha = 1.0)
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
        // Reject non-finite alpha before clamping (Math.Clamp(NaN,0,1) is NaN, which would fail
        // the `alpha < 1.0` test and silently paint opaque — same hole closed in FillRectangle).
        if (!double.IsFinite(alpha))
        {
            throw new ArgumentException($"ShowGlyphs alpha must be finite; got {alpha}.", nameof(alpha));
        }
        if (glyphIds.IsEmpty) return;

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);
        alpha = Math.Clamp(alpha, 0.0, 1.0);

        // q [/GSn gs] <r> <g> <b> rg BT /Fn <size> Tf <x> <y> Td <glyph-hex> Tj ET Q —
        // optionally select a constant fill alpha, set the fill color, open a text object,
        // select the font + size, set the line origin, show the glyph ids as a hex string
        // (2 bytes / glyph, Identity-H), close.
        var sb = new StringBuilder(48 + (glyphIds.Length * 4));
        sb.Append("q ");
        if (alpha < 1.0)
        {
            sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        }
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

    /// <summary>Phase 4 borders (PR 3) — stroke a single line segment (<paramref name="x1"/>,
    /// <paramref name="y1"/>)→(<paramref name="x2"/>, <paramref name="y2"/>) (PDF points, bottom-left
    /// origin) with <paramref name="width"/> line width, an optional <paramref name="dash"/> pattern
    /// (PDF user-space units) at <paramref name="dashPhase"/>, and a line cap (<paramref name="lineCap"/>:
    /// 0 butt, 1 round, 2 square). Used for dashed / dotted border + outline edges. Emits
    /// <c>q [/GSn gs] &lt;width&gt; w [&lt;cap&gt; J] [[&lt;dash&gt;] &lt;phase&gt; d] r g b RG x1 y1 m x2 y2 l S Q</c>.
    /// A non-positive width no-ops; non-finite args throw (as the fills do). Partial alpha → the
    /// per-page constant-alpha ExtGState (<c>/ca</c> — fill alpha applies to the stroke too here).</summary>
    public void StrokeLine(
        double x1, double y1, double x2, double y2, double width,
        double r, double g, double b, double alpha = 1.0,
        double[]? dash = null, double dashPhase = 0.0, int lineCap = 0)
    {
        ThrowIfFinalized();
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)
            || !double.IsFinite(width) || !double.IsFinite(alpha) || !double.IsFinite(dashPhase))
        {
            throw new ArgumentException(
                $"StrokeLine args must be finite; got ({x1},{y1})-({x2},{y2}) w={width} alpha={alpha} phase={dashPhase}.");
        }
        if (width <= 0) return;
        r = Math.Clamp(r, 0.0, 1.0); g = Math.Clamp(g, 0.0, 1.0); b = Math.Clamp(b, 0.0, 1.0);
        alpha = Math.Clamp(alpha, 0.0, 1.0);

        var sb = new StringBuilder(112);
        sb.Append("q ");
        if (alpha < 1.0) sb.Append('/').Append(GetOrAddConstantAlpha(alpha).Value).Append(" gs ");
        AppendNumber(sb, width); sb.Append(" w ");
        if (lineCap is 1 or 2) { sb.Append(lineCap); sb.Append(" J "); }
        if (dash is { Length: > 0 })
        {
            sb.Append('[');
            for (var i = 0; i < dash.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                AppendNumber(sb, Math.Max(0, dash[i]));
            }
            sb.Append("] "); AppendNumber(sb, dashPhase); sb.Append(" d ");
        }
        AppendNumber(sb, r); sb.Append(' ');
        AppendNumber(sb, g); sb.Append(' ');
        AppendNumber(sb, b); sb.Append(" RG ");
        AppendNumber(sb, x1); sb.Append(' '); AppendNumber(sb, y1); sb.Append(" m ");
        AppendNumber(sb, x2); sb.Append(' '); AppendNumber(sb, y2); sb.Append(" l S Q\n");
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
        if (_patternsResource.Count > 0) Resources.Set(PdfNames.Pattern, _patternsResource);
        if (_shadingResource.Count > 0) Resources.Set(PdfNames.Shading, _shadingResource);

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

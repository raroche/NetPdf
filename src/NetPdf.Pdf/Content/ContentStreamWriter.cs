// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Content;

/// <summary>
/// Emits PDF content-stream operators per ISO 32000-2 §8 / §9. Operands are written first,
/// then the operator name, then a newline (the spec accepts any whitespace; we use newlines
/// for byte-deterministic, line-by-line output that is also legible during debugging).
/// <para>
/// Phase 1 covers a minimal vocabulary: graphics state (q/Q/cm), path construction
/// (m/l/re/h), path painting (f/S/n), device colors (rg/RG/g/G), line width (w),
/// text (BT/ET/Tf/Td/TD/Tj/TJ), XObjects (Do), and marked content (BMC/BDC/EMC).
/// The full operator set lands across Phases 1–4 as paint/text features ship.
/// </para>
/// <para>
/// The writer enforces a few non-negotiable nesting invariants at runtime: q/Q balance,
/// BT/ET pairing, and BMC/BDC/EMC depth. <see cref="Finish"/> verifies the stream ends in
/// a balanced state — call it before extracting bytes for embedding in a <see cref="PdfStream"/>.
/// </para>
/// </summary>
internal sealed class ContentStreamWriter : IContentStream
{
    private readonly PdfWriter _writer;
    private int _saveDepth;
    private int _markedContentDepth;
    private bool _inText;
    private bool _finished;

    public ContentStreamWriter(PdfWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>
    /// Verify the stream ends in a balanced state. Call once after the last operator.
    /// Throws <see cref="InvalidOperationException"/> with a specific message if any of
    /// q/Q, BT/ET, or BMC/EMC are unbalanced.
    /// </summary>
    public void Finish()
    {
        if (_finished) throw new InvalidOperationException("Finish was already called.");
        if (_saveDepth != 0)
        {
            throw new InvalidOperationException(
                $"Content stream ends with {_saveDepth} unmatched 'q' (SaveState) operator(s); each must be paired with a 'Q' (RestoreState).");
        }
        if (_inText)
        {
            throw new InvalidOperationException(
                "Content stream ends inside a BT/ET text object; missing a closing EndText().");
        }
        if (_markedContentDepth != 0)
        {
            throw new InvalidOperationException(
                $"Content stream ends with {_markedContentDepth} unmatched marked-content begin operator(s); each BMC/BDC must be paired with an EMC.");
        }
        _finished = true;
    }

    // --------------------------------------------------- Graphics state (q/Q/cm)

    /// <summary>§8.4.4 — <c>q</c>: push the graphics state stack.</summary>
    public void SaveState()
    {
        EnsureNotFinished();
        EnsureOutsideText("SaveState (q)");
        _saveDepth++;
        EmitOperator("q");
    }

    /// <summary>§8.4.4 — <c>Q</c>: pop the graphics state stack. Throws if no matching SaveState.</summary>
    public void RestoreState()
    {
        EnsureNotFinished();
        EnsureOutsideText("RestoreState (Q)");
        if (_saveDepth == 0)
        {
            throw new InvalidOperationException("RestoreState (Q) called with no matching SaveState (q).");
        }
        _saveDepth--;
        EmitOperator("Q");
    }

    /// <summary>§8.4.4 — <c>cm</c>: concatenate the matrix [a b c d e f] to the current transform.</summary>
    public void ConcatMatrix(double a, double b, double c, double d, double e, double f)
    {
        EnsureNotFinished();
        EnsureOutsideText("ConcatMatrix (cm)");
        WriteOperands(a, b, c, d, e, f);
        EmitOperator("cm");
    }

    // --------------------------------------------------- Path construction (m/l/re/h)

    /// <summary>§8.5.2 — <c>m</c>: move to (x, y), starting a new subpath.</summary>
    public void MoveTo(double x, double y)
    {
        EnsureNotFinished();
        EnsureOutsideText("MoveTo (m)");
        WriteOperands(x, y);
        EmitOperator("m");
    }

    /// <summary>§8.5.2 — <c>l</c>: append a straight line from the current point to (x, y).</summary>
    public void LineTo(double x, double y)
    {
        EnsureNotFinished();
        EnsureOutsideText("LineTo (l)");
        WriteOperands(x, y);
        EmitOperator("l");
    }

    /// <summary>§8.5.2 — <c>re</c>: append a rectangle (x, y, width, height).</summary>
    public void Rectangle(double x, double y, double width, double height)
    {
        EnsureNotFinished();
        EnsureOutsideText("Rectangle (re)");
        WriteOperands(x, y, width, height);
        EmitOperator("re");
    }

    /// <summary>§8.5.2 — <c>h</c>: close the current subpath with a straight line back to the start.</summary>
    public void ClosePath()
    {
        EnsureNotFinished();
        EnsureOutsideText("ClosePath (h)");
        EmitOperator("h");
    }

    // --------------------------------------------------- Path painting (f/S/n)

    /// <summary>§8.5.3 — <c>f</c>: fill the path using the non-zero winding rule.</summary>
    public void Fill()
    {
        EnsureNotFinished();
        EnsureOutsideText("Fill (f)");
        EmitOperator("f");
    }

    /// <summary>§8.5.3 — <c>S</c>: stroke the path.</summary>
    public void Stroke()
    {
        EnsureNotFinished();
        EnsureOutsideText("Stroke (S)");
        EmitOperator("S");
    }

    /// <summary>§8.5.3 — <c>n</c>: end the path without filling or stroking (used to apply a clip).</summary>
    public void EndPath()
    {
        EnsureNotFinished();
        EnsureOutsideText("EndPath (n)");
        EmitOperator("n");
    }

    // --------------------------------------------------- Device color (rg/RG/g/G)

    /// <summary>§8.6.8 — <c>rg</c>: set the non-stroking color to DeviceRGB(r, g, b). Each component must be in [0, 1].</summary>
    public void SetFillRgb(double r, double g, double b)
    {
        EnsureNotFinished();
        EnsureNormalizedComponent(r, nameof(r));
        EnsureNormalizedComponent(g, nameof(g));
        EnsureNormalizedComponent(b, nameof(b));
        WriteOperands(r, g, b);
        EmitOperator("rg");
    }

    /// <summary>§8.6.8 — <c>RG</c>: set the stroking color to DeviceRGB(r, g, b). Each component must be in [0, 1].</summary>
    public void SetStrokeRgb(double r, double g, double b)
    {
        EnsureNotFinished();
        EnsureNormalizedComponent(r, nameof(r));
        EnsureNormalizedComponent(g, nameof(g));
        EnsureNormalizedComponent(b, nameof(b));
        WriteOperands(r, g, b);
        EmitOperator("RG");
    }

    /// <summary>§8.6.8 — <c>g</c>: set the non-stroking color to DeviceGray(value). Value must be in [0, 1].</summary>
    public void SetFillGray(double gray)
    {
        EnsureNotFinished();
        EnsureNormalizedComponent(gray, nameof(gray));
        WriteOperands(gray);
        EmitOperator("g");
    }

    /// <summary>§8.6.8 — <c>G</c>: set the stroking color to DeviceGray(value). Value must be in [0, 1].</summary>
    public void SetStrokeGray(double gray)
    {
        EnsureNotFinished();
        EnsureNormalizedComponent(gray, nameof(gray));
        WriteOperands(gray);
        EmitOperator("G");
    }

    /// <summary>
    /// Validate that <paramref name="value"/> is a finite number in [0, 1]. Out-of-range or
    /// NaN inputs would produce viewer-dependent clamping; we reject at the API boundary so
    /// later layers can trust normalized colors.
    /// </summary>
    private static void EnsureNormalizedComponent(double value, string paramName)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value,
                "Color component must be a finite number in [0, 1] (PDF DeviceRGB / DeviceGray colors are normalized).");
        }
    }

    // --------------------------------------------------- Line width (w)

    /// <summary>§8.4.4 — <c>w</c>: set the line width. Value must be ≥ 0.</summary>
    public void SetLineWidth(double width)
    {
        EnsureNotFinished();
        EnsureOutsideText("SetLineWidth (w)");
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Line width must be non-negative.");
        }
        WriteOperands(width);
        EmitOperator("w");
    }

    // --------------------------------------------------- Text (BT/ET/Tf/Td/TD/Tj/TJ)

    /// <summary>§9.4.1 — <c>BT</c>: begin a text object.</summary>
    public void BeginText()
    {
        EnsureNotFinished();
        if (_inText)
        {
            throw new InvalidOperationException("BeginText (BT) called inside an already-open text object.");
        }
        _inText = true;
        EmitOperator("BT");
    }

    /// <summary>§9.4.1 — <c>ET</c>: end the current text object.</summary>
    public void EndText()
    {
        EnsureNotFinished();
        if (!_inText)
        {
            throw new InvalidOperationException("EndText (ET) called with no matching BeginText (BT).");
        }
        _inText = false;
        EmitOperator("ET");
    }

    /// <summary>§9.3.1 — <c>Tf</c>: set the current font and font size. Font is a resource name (e.g., /F1).</summary>
    public void SetFont(PdfName fontResourceName, double size)
    {
        ArgumentNullException.ThrowIfNull(fontResourceName);
        EnsureNotFinished();
        EnsureInText("SetFont (Tf)");
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Font size must be positive.");
        }
        fontResourceName.WriteTo(_writer);
        _writer.WriteSpace();
        WriteOperands(size);
        EmitOperator("Tf");
    }

    /// <summary>§9.4.2 — <c>Td</c>: move text position to start of next line, offset (tx, ty).</summary>
    public void MoveTextPosition(double tx, double ty)
    {
        EnsureNotFinished();
        EnsureInText("MoveTextPosition (Td)");
        WriteOperands(tx, ty);
        EmitOperator("Td");
    }

    /// <summary>§9.4.2 — <c>TD</c>: like Td, plus set the text leading to -ty.</summary>
    public void MoveTextPositionAndSetLeading(double tx, double ty)
    {
        EnsureNotFinished();
        EnsureInText("MoveTextPositionAndSetLeading (TD)");
        WriteOperands(tx, ty);
        EmitOperator("TD");
    }

    /// <summary>
    /// §9.4.3 — <c>Tj</c>: show a string. <paramref name="bytes"/> is the literal byte payload
    /// (typically PDFDocEncoding for simple fonts, or 2-byte CIDs for Type 0 fonts). Backslashes
    /// and parens are escaped per §7.3.4.2.
    /// </summary>
    public void ShowText(ReadOnlySpan<byte> bytes)
    {
        EnsureNotFinished();
        EnsureInText("ShowText (Tj)");
        WriteLiteralString(bytes);
        _writer.WriteSpace();
        EmitOperator("Tj");
    }

    /// <summary>
    /// §9.4.3 — <c>TJ</c>: show a sequence of strings interspersed with positioning adjustments.
    /// Each element is either a byte string (rendered) or a number (advance offset in
    /// thousandths of a unit, subtracted from the current text position).
    /// </summary>
    public void ShowTextArray(ReadOnlySpan<TextArrayElement> elements)
    {
        EnsureNotFinished();
        EnsureInText("ShowTextArray (TJ)");
        _writer.WriteByte((byte)'[');
        for (int i = 0; i < elements.Length; i++)
        {
            if (i > 0) _writer.WriteSpace();
            var e = elements[i];
            if (e.IsString)
            {
                WriteLiteralString(e.StringBytes.Span);
            }
            else
            {
                _writer.WriteReal(e.Offset);
            }
        }
        _writer.WriteByte((byte)']');
        _writer.WriteSpace();
        EmitOperator("TJ");
    }

    // --------------------------------------------------- XObject (Do)

    /// <summary>§8.8 — <c>Do</c>: paint an external object referenced by resource name (e.g., /Im1).</summary>
    public void PaintXObject(PdfName xobjectResourceName)
    {
        ArgumentNullException.ThrowIfNull(xobjectResourceName);
        EnsureNotFinished();
        EnsureOutsideText("PaintXObject (Do)");
        xobjectResourceName.WriteTo(_writer);
        _writer.WriteSpace();
        EmitOperator("Do");
    }

    // --------------------------------------------------- Marked content (BMC/BDC/EMC)

    /// <summary>§14.6 — <c>BMC</c>: begin a marked-content sequence with a tag.</summary>
    public void BeginMarkedContent(PdfName tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        EnsureNotFinished();
        _markedContentDepth++;
        tag.WriteTo(_writer);
        _writer.WriteSpace();
        EmitOperator("BMC");
    }

    /// <summary>§14.6 — <c>BDC</c>: begin a marked-content sequence with a tag and properties.</summary>
    public void BeginMarkedContentWithProperties(PdfName tag, PdfDictionary properties)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(properties);
        EnsureNotFinished();
        _markedContentDepth++;
        tag.WriteTo(_writer);
        _writer.WriteSpace();
        properties.WriteTo(_writer);
        _writer.WriteSpace();
        EmitOperator("BDC");
    }

    /// <summary>§14.6 — <c>EMC</c>: end the current marked-content sequence.</summary>
    public void EndMarkedContent()
    {
        EnsureNotFinished();
        if (_markedContentDepth == 0)
        {
            throw new InvalidOperationException("EndMarkedContent (EMC) called with no matching BeginMarkedContent (BMC/BDC).");
        }
        _markedContentDepth--;
        EmitOperator("EMC");
    }

    // ------------------------------------------------------------------------------------

    private void EmitOperator(string opName)
    {
        _writer.WriteAscii(opName);
        _writer.WriteNewLine();
    }

    private void WriteOperands(double a)
    {
        _writer.WriteReal(a);
        _writer.WriteSpace();
    }

    private void WriteOperands(double a, double b)
    {
        _writer.WriteReal(a); _writer.WriteSpace();
        _writer.WriteReal(b); _writer.WriteSpace();
    }

    private void WriteOperands(double a, double b, double c)
    {
        _writer.WriteReal(a); _writer.WriteSpace();
        _writer.WriteReal(b); _writer.WriteSpace();
        _writer.WriteReal(c); _writer.WriteSpace();
    }

    private void WriteOperands(double a, double b, double c, double d)
    {
        _writer.WriteReal(a); _writer.WriteSpace();
        _writer.WriteReal(b); _writer.WriteSpace();
        _writer.WriteReal(c); _writer.WriteSpace();
        _writer.WriteReal(d); _writer.WriteSpace();
    }

    private void WriteOperands(double a, double b, double c, double d, double e, double f)
    {
        _writer.WriteReal(a); _writer.WriteSpace();
        _writer.WriteReal(b); _writer.WriteSpace();
        _writer.WriteReal(c); _writer.WriteSpace();
        _writer.WriteReal(d); _writer.WriteSpace();
        _writer.WriteReal(e); _writer.WriteSpace();
        _writer.WriteReal(f); _writer.WriteSpace();
    }

    /// <summary>
    /// Emit a literal string with §7.3.4.2 escapes. Inlined to avoid allocating a
    /// PdfLiteralString wrapper per call (text-heavy streams call this thousands of times).
    /// </summary>
    private void WriteLiteralString(ReadOnlySpan<byte> bytes)
    {
        _writer.WriteByte((byte)'(');
        foreach (byte b in bytes)
        {
            if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
            {
                _writer.WriteByte((byte)'\\');
            }
            _writer.WriteByte(b);
        }
        _writer.WriteByte((byte)')');
    }

    private void EnsureNotFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("Cannot emit operators after Finish() has been called.");
        }
    }

    private void EnsureOutsideText(string opLabel)
    {
        if (_inText)
        {
            throw new InvalidOperationException(
                $"{opLabel} is not valid inside a BT/ET text object; emit it before BeginText or after EndText.");
        }
    }

    private void EnsureInText(string opLabel)
    {
        if (!_inText)
        {
            throw new InvalidOperationException(
                $"{opLabel} is only valid inside a BT/ET text object; call BeginText first.");
        }
    }
}

/// <summary>
/// One element in a TJ array — either a byte string to render or a numeric advance
/// adjustment (in thousandths of a font unit, subtracted from the current text position).
/// </summary>
internal readonly struct TextArrayElement
{
    public ReadOnlyMemory<byte> StringBytes { get; }
    public double Offset { get; }
    public bool IsString { get; }

    private TextArrayElement(ReadOnlyMemory<byte> stringBytes, double offset, bool isString)
    {
        StringBytes = stringBytes;
        Offset = offset;
        IsString = isString;
    }

    public static TextArrayElement String(ReadOnlyMemory<byte> bytes) => new(bytes, 0, true);
    public static TextArrayElement Adjust(double offset) => new(default, offset, false);
}

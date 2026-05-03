// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;

namespace NetPdf.AotSmoke;

/// <summary>
/// Builds the canonical smoke document and validates the emitted bytes. Lives in its own
/// class (not <see cref="Program"/>) so the AOT binary entry point and the JIT/AOT
/// parity test (<c>NetPdf.UnitTests.Pdf.AotJitParityTests</c>) call the <b>same</b>
/// build code — they cannot drift out of sync because there is only one definition.
/// <para>
/// The factory keeps zero external dependencies beyond <c>NetPdf.Pdf</c> so the
/// AOT-published native image stays small and fast: no test-fixture references, no
/// reflection-driven assembly loading, no dynamic resource lookup.
/// </para>
/// </summary>
public static class SmokeDocumentFactory
{
    /// <summary>
    /// Build a small but representative PDF that exercises every Phase 1 byte-emit path
    /// the AOT canary cares about: page allocation, deterministic metadata, JPEG
    /// content-hash dedup, transparent GIF through <c>RasterImageXObject</c> (which
    /// invokes the alpha-split <c>/SMask</c> indirect-reference branch — the most
    /// complex recent code path), <see cref="PdfPage.PlaceImage"/>, both
    /// <c>AppendContent</c> overloads, and trailer <c>/ID</c> auto-derivation.
    /// </summary>
    public static byte[] BuildSmokeDocument()
    {
        var doc = new PdfDocument
        {
            Title = "NetPdf AOT Smoke",
            Author = "NetPdf.AotSmoke",
            Subject = "Phase 1 Task 24",
            Creator = "NetPdf.AotSmoke",
            // Deterministic timestamp so identical AOT runs produce identical output.
            CreationDate = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        };

        // JPEG — exercises the passthrough wrapper + content-hash dedup.
        var jpeg = JpegImageXObject.Build(BuildMinimalBaselineJpeg());
        var jpegRef = doc.RegisterImage(jpeg);
        var jpegRefDedup = doc.RegisterImage(jpeg); // dedup: must reuse jpegRef
        if (jpegRef.ObjectNumber != jpegRefDedup.ObjectNumber)
        {
            throw new InvalidOperationException(
                "Image dedup did not reuse the existing indirect ref — caller mutation regression?");
        }

        // Transparent GIF — exercises the alpha-split path:
        // RasterImageDecoder (SkiaSharp) → RasterImageXObject (RGB plane + grayscale
        // alpha plane both compressed) → PdfDocument.RegisterImage(ImageXObjectResult)
        // (clones the primary image dict, allocates an indirect SMask slot, wires
        // /SMask to that slot). Hits the most subtle recent fix.
        var rasterResult = RasterImageXObject.Build(BuildMinimalTransparentGif());
        if (rasterResult.SMask is null)
        {
            throw new InvalidOperationException(
                "Transparent GIF did not produce an SMask — alpha-split path may have regressed.");
        }
        var rasterRef = doc.RegisterImage(rasterResult);

        var p1 = doc.AddPage(MediaBoxSize.A4);
        p1.AppendContent("0.95 0.95 0.95 rg 0 0 595 842 re f\n");
        p1.PlaceImage(jpegRef, x: 100, y: 600, width: 100, height: 100);
        p1.PlaceImage(rasterRef, x: 250, y: 600, width: 100, height: 100);

        var p2 = doc.AddPage(MediaBoxSize.Letter);
        // Byte overload exercises the binary AppendContent path through to the
        // ArrayBufferWriter<byte> backing store.
        ReadOnlySpan<byte> ops =
        [
            (byte)'q', (byte)' ',
            (byte)'1', (byte)' ', (byte)'0', (byte)' ', (byte)'0', (byte)' ', (byte)'1', (byte)' ',
            (byte)'5', (byte)'0', (byte)' ', (byte)'5', (byte)'0', (byte)' ',
            (byte)'c', (byte)'m', (byte)' ',
            (byte)'Q', (byte)'\n',
        ];
        p2.AppendContent(ops);
        p2.PlaceImage(jpegRefDedup, x: 100, y: 100, width: 80, height: 80);

        return doc.Save();
    }

    /// <summary>
    /// Stronger structural verifier — actually parses the <c>startxref</c> offset and
    /// confirms it points at an <c>xref</c>-keyword block (or an indirect-object header
    /// when the file uses xref streams). Catches the failure mode where the keyword
    /// search alone passes ("xref" appears in any random byte sequence) but the file is
    /// structurally malformed because the xref offset is wrong.
    /// </summary>
    public static bool TryVerifyPdfStructure(byte[] bytes, out string failure)
    {
        if (bytes.Length < 256)
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"PDF bytes too short ({bytes.Length}); minimum is 256.");
            return false;
        }

        // Header: "%PDF-X.Y" at byte 0.
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(8, bytes.Length));
        if (!head.StartsWith("%PDF-", StringComparison.Ordinal))
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"missing %PDF- header (head bytes: '{head}').");
            return false;
        }

        // Tail: must contain "%%EOF" within the trailing 16 bytes.
        var tailLen = Math.Min(16, bytes.Length);
        var tailStart = bytes.Length - tailLen;
        var tail = Encoding.ASCII.GetString(bytes, tailStart, tailLen);
        if (!tail.Contains("%%EOF", StringComparison.Ordinal))
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"missing trailing %%EOF (tail bytes: '{tail}').");
            return false;
        }

        // startxref: scan back from the tail for the literal "startxref\n<digits>".
        // The byte offset that follows must point at the start of either the "xref"
        // keyword (classic table) OR a positive object header (xref streams).
        if (!TryParseStartXref(bytes, out var xrefOffset, out failure)) return false;
        if (xrefOffset < 0 || xrefOffset >= bytes.Length)
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"startxref offset {xrefOffset} is out of bounds (file length {bytes.Length}).");
            return false;
        }
        if (!IsXrefBlockAtOffset(bytes, xrefOffset))
        {
            // Show the bytes at the offset for diagnostics — most failures here are
            // off-by-N and the local context tells you which way.
            var snippet = ReadAsciiSnippet(bytes, xrefOffset, 16);
            failure = string.Create(CultureInfo.InvariantCulture,
                $"startxref offset {xrefOffset} does not point at an xref block (got: '{snippet}').");
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryParseStartXref(byte[] bytes, out long offset, out string failure)
    {
        // Scan the trailing 1024 bytes (generous) for "startxref\n<digits>\n%%EOF".
        // ISO 32000-2 §7.5.5: startxref is followed by a positive integer offset on
        // the next line, then "%%EOF".
        offset = -1;
        var scanStart = Math.Max(0, bytes.Length - 1024);
        var scanLen = bytes.Length - scanStart;
        var window = Encoding.ASCII.GetString(bytes, scanStart, scanLen);
        var idx = window.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0)
        {
            failure = "missing 'startxref' keyword in trailing 1024 bytes.";
            return false;
        }
        // Skip the keyword, any whitespace, then read the integer up to the next
        // non-digit. Stay tolerant about CR / LF / CRLF.
        var cursor = idx + "startxref".Length;
        while (cursor < window.Length && (window[cursor] == '\r' || window[cursor] == '\n' || window[cursor] == ' '))
        {
            cursor++;
        }
        var digitsStart = cursor;
        while (cursor < window.Length && window[cursor] >= '0' && window[cursor] <= '9')
        {
            cursor++;
        }
        if (cursor == digitsStart)
        {
            failure = "startxref keyword not followed by a numeric offset.";
            return false;
        }
        var digits = window.AsSpan(digitsStart, cursor - digitsStart);
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"could not parse startxref offset (digits: '{digits}').");
            return false;
        }
        failure = "";
        return true;
    }

    private static bool IsXrefBlockAtOffset(byte[] bytes, long offset)
    {
        // Classic xref table starts with the literal "xref" on its own line.
        if (offset + 4 <= bytes.Length)
        {
            var head = Encoding.ASCII.GetString(bytes, (int)offset, 4);
            if (head.Equals("xref", StringComparison.Ordinal)) return true;
        }
        // Xref stream (PDF 1.5+): starts with an indirect-object header
        // "<num> <gen> obj". We are emitting classic xref in Phase 1, but accept the
        // stream form so the structural check survives a future EmittedPdfVersion=V2_0
        // toggle without spurious failures.
        var snippet = ReadAsciiSnippet(bytes, offset, 32);
        // Pattern: "<digits> <digits> obj" — quick check via index of " obj".
        var objIdx = snippet.IndexOf(" obj", StringComparison.Ordinal);
        return objIdx > 0;
    }

    private static string ReadAsciiSnippet(byte[] bytes, long offset, int length)
    {
        if (offset >= bytes.Length) return "";
        var available = (int)Math.Min(length, bytes.Length - offset);
        return Encoding.ASCII.GetString(bytes, (int)offset, available);
    }

    /// <summary>
    /// Hand-craft a minimal valid baseline JPEG (SOI / APP0 JFIF / minimal SOF0 / SOS /
    /// one MCU / EOI). The bytes are syntactically valid enough for
    /// <see cref="JpegHeaderParser"/> to accept (size + component count) and for
    /// <see cref="JpegImageXObject"/> to wrap as a passthrough Image XObject.
    /// </summary>
    private static byte[] BuildMinimalBaselineJpeg() =>
    [
        0xFF, 0xD8,                                         // SOI
        // APP0: JFIF v1.01, units=0, X/Y=1/1, no thumbnail
        0xFF, 0xE0, 0x00, 0x10,                             // APP0 length 16
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
        0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        // SOF0: precision=8, height=1, width=1, components=3 (Y'CbCr)
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03,
        0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        // SOS: 3 components scanning Y/Cb/Cr, Ss/Se/Ah/Al = 0/63/0/0
        0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
        0x00, // entropy-coded segment placeholder
        0xFF, 0xD9, // EOI
    ];

    /// <summary>
    /// Hand-crafted minimal GIF89a (1×1) with a Graphic Control Extension marking
    /// palette index 0 as transparent. Goes through the <c>RasterImageDecoder</c>
    /// (SkiaSharp) → <c>RasterImageXObject</c> alpha-split path, which is the
    /// stress-test for <c>PdfDocument.RegisterImage(ImageXObjectResult)</c>'s
    /// <c>/SMask</c> indirect-reference wiring.
    /// </summary>
    private static byte[] BuildMinimalTransparentGif() =>
    [
        // Header
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        // LSD: 1×1, gct flag, 2-color table
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        // GCT: white + black
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        // Graphic Control Extension: transparency index = 0
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,
        // Image Descriptor + 1×1 image data (single transparent pixel)
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        // Trailer
        0x3B,
    ];
}

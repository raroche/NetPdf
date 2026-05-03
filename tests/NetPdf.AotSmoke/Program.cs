// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;

namespace NetPdf.AotSmoke;

/// <summary>
/// AOT smoke. CI publishes this with <c>-p:PublishAot=true</c> and runs the resulting
/// native binary; failure to publish or execute blocks the merge. Confirms NetPdf is
/// reflection-free and trim-friendly throughout the call stack from
/// <see cref="PdfDocument"/> down through the byte writer, image XObjects, and trailer
/// derivation.
/// <para>
/// The smoke deliberately stays simple: build a small representative document, save it
/// to bytes, structurally validate the bytes (header + xref + startxref + trailing
/// %%EOF), and print the byte count + SHA-256. Anything richer belongs in
/// <c>PdfDocumentDeterminismHarnessTests</c>; this canary's job is to prove the
/// generated native image runs the same path without trim warnings or AOT failures.
/// </para>
/// <para>
/// Exit codes: 0 = success; 1 = build/save threw; 2 = structural sanity failed; 3 =
/// optional <paramref name="args"/> path could not be written. Any non-zero exit blocks
/// the CI step that runs this binary.
/// </para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBuildOrSaveFailed = 1;
    private const int ExitStructuralSanityFailed = 2;
    private const int ExitWriteFailed = 3;

    public static int Main(string[] args)
    {
        byte[] bytes;
        try
        {
            bytes = BuildSmokeDocument();
        }
        catch (Exception ex)
        {
            // No reflective stack-trace formatting — Console.Error stays AOT-friendly.
            Console.Error.WriteLine("AOT smoke: build/save threw.");
            Console.Error.WriteLine(ex);
            return ExitBuildOrSaveFailed;
        }

        if (!IsWellFormedPdf(bytes, out var sanityFailure))
        {
            Console.Error.WriteLine($"AOT smoke: structural sanity failed: {sanityFailure}");
            return ExitStructuralSanityFailed;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"NetPdf.AotSmoke phase=1 ok byteCount={bytes.Length} sha256={hash}"));

        // If a path argument is supplied, also write the bytes there. Useful for
        // CI scripts that want to feed the output into qpdf --check / PDFium.
        if (args.Length >= 1)
        {
            try
            {
                File.WriteAllBytes(args[0], bytes);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"NetPdf.AotSmoke wrote {bytes.Length} bytes to {args[0]}"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AOT smoke: writing to '{args[0]}' failed: {ex.Message}");
                return ExitWriteFailed;
            }
        }

        return ExitOk;
    }

    /// <summary>
    /// Build a small but representative PDF: two pages of mixed sizes with light raw
    /// content-stream operators and a tiny embedded JPEG. Exercises page allocation,
    /// metadata emission with explicit deterministic dates, image registration with
    /// content-hash dedup (the JPEG is registered twice — second call must hit the
    /// cache), <see cref="PdfPage.PlaceImage"/>, both <c>AppendContent</c> overloads,
    /// and trailer <c>/ID</c> auto-derivation.
    /// </summary>
    private static byte[] BuildSmokeDocument()
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

        // Tiny inline JPEG: 1×1 baseline, 3 components — hand-crafted so the smoke
        // does not depend on test fixtures from NetPdf.UnitTests (which would pull a
        // huge transitive graph into the AOT image).
        var jpeg = JpegImageXObject.Build(BuildMinimalBaselineJpeg());
        var imageRef = doc.RegisterImage(jpeg);
        var imageRefDedup = doc.RegisterImage(jpeg); // dedup: must reuse imageRef
        if (imageRef.ObjectNumber != imageRefDedup.ObjectNumber)
        {
            throw new InvalidOperationException(
                "Image dedup did not reuse the existing indirect ref — caller mutation regression?");
        }

        var p1 = doc.AddPage(MediaBoxSize.A4);
        p1.AppendContent("0.95 0.95 0.95 rg 0 0 595 842 re f\n");
        p1.PlaceImage(imageRef, x: 100, y: 600, width: 100, height: 100);

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

        return doc.Save();
    }

    /// <summary>
    /// Cheap structural sanity over the emitted bytes — header + xref + startxref +
    /// trailing %%EOF. Mirrors the property the unit-test harness'
    /// <c>DeterminismDiagnostics.AssertWellFormedPdfShape</c> enforces. We re-implement
    /// inline rather than referencing the test helper to keep the AOT smoke project
    /// dependency-light and to avoid pulling test infrastructure into a published
    /// native image.
    /// </summary>
    private static bool IsWellFormedPdf(byte[] bytes, out string failure)
    {
        if (bytes.Length < 256)
        {
            failure = $"PDF bytes too short ({bytes.Length}); minimum is 256.";
            return false;
        }
        var head = Encoding.ASCII.GetString(bytes, 0, 8);
        if (!head.StartsWith("%PDF-", StringComparison.Ordinal))
        {
            failure = $"missing %PDF- header (head bytes: '{head}').";
            return false;
        }
        var content = Encoding.ASCII.GetString(bytes);
        if (!content.Contains("xref", StringComparison.Ordinal))
        {
            failure = "missing 'xref' keyword.";
            return false;
        }
        if (!content.Contains("startxref", StringComparison.Ordinal))
        {
            failure = "missing 'startxref' keyword.";
            return false;
        }
        var tailLen = Math.Min(16, bytes.Length);
        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - tailLen, tailLen);
        if (!tail.Contains("%%EOF", StringComparison.Ordinal))
        {
            failure = $"missing trailing %%EOF (tail bytes: '{tail}').";
            return false;
        }
        failure = "";
        return true;
    }

    /// <summary>
    /// Hand-craft a minimal valid baseline JPEG (SOI / APP0 JFIF / minimal SOF0 / DHT /
    /// DQT / SOS / one MCU of zeros / EOI). The bytes are syntactically valid enough
    /// for <see cref="JpegHeaderParser"/> to accept (size + component count) and for
    /// <see cref="JpegImageXObject"/> to wrap. We do not need the JPEG to decode to a
    /// meaningful image — the smoke only exercises the wrap/passthrough path.
    /// </summary>
    private static byte[] BuildMinimalBaselineJpeg()
    {
        // SOI (FF D8) — APP0 JFIF (FF E0 ... ) — DQT (FF DB ...) — SOF0 (FF C0 ...) —
        // DHT (FF C4 ...) — SOS (FF DA ...) — image data — EOI (FF D9). For the
        // passthrough wrapper we only need: SOI, then enough markers that the parser
        // can find SOFn (with width/height/components), then SOS, then EOI. The
        // bytes between SOS and EOI may be arbitrary so long as they don't contain a
        // valid marker; we use a single 0x00 byte (escape for FF in entropy-coded
        // segments not relevant here, kept short).
        return
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
            // entropy-coded segment (single zero byte stand-in)
            0x00,
            // EOI
            0xFF, 0xD9,
        ];
    }
}

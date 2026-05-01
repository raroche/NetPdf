// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

public sealed class PdfDocumentIdTests
{
    [Fact]
    public void Trailer_id_is_auto_derived_when_not_set()
    {
        var bytes = Render(BuildMinimalWriter());
        var ascii = Encoding.Latin1.GetString(bytes);

        // /ID appears in trailer.
        Assert.Contains("/ID [", ascii);
    }

    [Fact]
    public void Auto_derived_id_is_an_array_of_two_equal_16_byte_hex_strings()
    {
        var bytes = Render(BuildMinimalWriter());
        var (first, second) = ExtractIdHexStrings(bytes);

        // 16 bytes = 32 uppercase hex digits.
        Assert.Equal(32, first.Length);
        Assert.Equal(32, second.Length);
        Assert.Matches(@"\A[0-9A-F]{32}\z", first);
        Assert.Matches(@"\A[0-9A-F]{32}\z", second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Auto_derived_id_is_first_16_bytes_of_sha256_of_body()
    {
        // The body is everything emitted before the "trailer\n" keyword; /ID lives inside
        // the trailer, so the hash must not include the trailer.
        var bytes = Render(BuildMinimalWriter());
        ReadOnlySpan<byte> trailerKeyword = "trailer\n"u8;
        int trailerIdx = bytes.AsSpan().IndexOf(trailerKeyword);
        Assert.True(trailerIdx > 0, "trailer keyword not found");

        var bodyHash = SHA256.HashData(bytes.AsSpan(0, trailerIdx));
        var expectedFirst16Hex = Convert.ToHexString(bodyHash.AsSpan(0, 16));

        var (idHex, _) = ExtractIdHexStrings(bytes);
        Assert.Equal(expectedFirst16Hex, idHex);
    }

    [Fact]
    public void Identical_inputs_produce_identical_ids()
    {
        var a = Render(BuildMinimalWriter());
        var b = Render(BuildMinimalWriter());
        Assert.Equal(ExtractIdHexStrings(a).First, ExtractIdHexStrings(b).First);
    }

    [Fact]
    public void Different_inputs_produce_different_ids()
    {
        // Adding an extra integer changes the body bytes → different hash → different /ID.
        var w1 = BuildMinimalWriter();

        var w2 = BuildMinimalWriter();
        w2.Objects.Add(new PdfInteger(42));

        Assert.NotEqual(
            ExtractIdHexStrings(Render(w1)).First,
            ExtractIdHexStrings(Render(w2)).First);
    }

    [Fact]
    public void User_provided_id_is_preserved()
    {
        // When a caller sets /ID explicitly, the writer respects it and skips auto-derivation.
        var w = BuildMinimalWriter();
        var customId = new PdfHexString(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(customId).Add(customId));

        var bytes = Render(w);
        var (first, second) = ExtractIdHexStrings(bytes);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", first);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", second);
    }

    [Fact]
    public void Determinism_byte_equal_output_includes_id()
    {
        // Belt-and-braces: the foundational determinism property must hold even now that
        // /ID is content-derived. Two identical builds → identical bytes including /ID.
        Assert.Equal(Render(BuildMinimalWriter()), Render(BuildMinimalWriter()));
    }

    // ------------------------------------------------------------------------------------

    private static PdfDocumentWriter BuildMinimalWriter()
    {
        var w = new PdfDocumentWriter();
        var catalogRef = w.Objects.Allocate();
        var pagesRef = w.Objects.Allocate();

        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, pagesRef);
        var pages = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Pages)
            .Set(PdfNames.Kids, new PdfArray())
            .Set(PdfNames.Count, new PdfInteger(0));

        w.Objects.Assign(catalogRef, catalog);
        w.Objects.Assign(pagesRef, pages);
        w.Trailer.Set(PdfNames.Root, catalogRef);
        return w;
    }

    private static byte[] Render(PdfDocumentWriter w)
    {
        var buf = new ArrayBufferWriter<byte>();
        w.WriteTo(buf);
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Pulls the two hex-string values out of the trailer's <c>/ID [&lt;...&gt; &lt;...&gt;]</c>
    /// without parsing the full PDF. Returns the inner hex content (no <c>&lt;</c>/<c>&gt;</c>).
    /// </summary>
    private static (string First, string Second) ExtractIdHexStrings(byte[] bytes)
    {
        var ascii = Encoding.Latin1.GetString(bytes);
        int idIdx = ascii.IndexOf("/ID [", StringComparison.Ordinal);
        Assert.True(idIdx >= 0, "/ID not present in output");

        int firstOpen = ascii.IndexOf('<', idIdx);
        int firstClose = ascii.IndexOf('>', firstOpen + 1);
        int secondOpen = ascii.IndexOf('<', firstClose + 1);
        int secondClose = ascii.IndexOf('>', secondOpen + 1);

        var first = ascii.Substring(firstOpen + 1, firstClose - firstOpen - 1);
        var second = ascii.Substring(secondOpen + 1, secondClose - secondOpen - 1);
        return (first, second);
    }
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Round-2 hardening coverage: tighter /Root validation, store-scoped Get, fail-fast
/// WriteAscii, and PdfStream's /Length self-correction.
/// </summary>
public sealed class PdfRound2HardeningTests
{
    // ---------------------------------------------------------------- /Root tightening

    [Fact]
    public void Root_pointing_to_pdfinteger_object_throws()
    {
        // /Root is a valid indirect ref, but it resolves to a PdfInteger, not a dictionary.
        // The previous lenient validator silently accepted this; the tightened version
        // requires the target to be a PdfDictionary with /Type /Catalog.
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfInteger(42));
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("must reference a dictionary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_pointing_to_dictionary_without_type_throws()
    {
        // A dictionary with no /Type entry is not a valid Catalog. Previously accepted
        // because the lenient check only triggered when /Type was set and wrong.
        var w = new PdfDocumentWriter();
        var bareDict = new PdfDictionary().Set(PdfNames.Pages, new PdfIndirectRef(1));
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(bareDict));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("missing the /Type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_pointing_to_dictionary_with_non_name_type_throws()
    {
        // /Type with a non-name value is structurally malformed.
        var w = new PdfDocumentWriter();
        var weirdDict = new PdfDictionary().Set(PdfNames.Type, new PdfInteger(0));
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(weirdDict));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("/Type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_pointing_to_pdfarray_object_throws()
    {
        var w = new PdfDocumentWriter();
        w.Objects.Add(new PdfArray());                                       // object 1: array
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(new ArrayBufferWriter<byte>()));
        Assert.Contains("dictionary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- Store.Get scope

    [Fact]
    public void Get_with_foreign_store_ref_throws()
    {
        var storeA = new IndirectObjectStore();
        var foreignRef = storeA.Add(PdfBoolean.True);

        var storeB = new IndirectObjectStore();
        storeB.Add(PdfBoolean.False); // ensure storeB has the same number allocated

        var ex = Assert.Throws<InvalidOperationException>(() => storeB.Get(foreignRef));
        Assert.Contains("different IndirectObjectStore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_with_synthetic_ref_resolves_against_local_numbers()
    {
        var s = new IndirectObjectStore();
        var added = s.Add(PdfBoolean.True);

        // Synthetic ref (StoreId == 0) is opaque — the store treats it as "trust the number."
        var synthetic = new PdfIndirectRef(added.ObjectNumber);
        Assert.Same(PdfBoolean.True, s.Get(synthetic));
    }

    [Fact]
    public void Get_with_local_store_ref_resolves_correctly()
    {
        var s = new IndirectObjectStore();
        var r = s.Add(PdfBoolean.True);
        Assert.Same(PdfBoolean.True, s.Get(r));
    }

    [Fact]
    public void Get_with_unallocated_local_number_returns_null()
    {
        // Either a local out-of-range number or a synthetic out-of-range number returns null
        // (resolution miss, not a structural error).
        var s = new IndirectObjectStore();
        Assert.Null(s.Get(new PdfIndirectRef(42)));
    }

    // ---------------------------------------------------------------- WriteAscii fail-fast

    [Fact]
    public void WriteAscii_throws_on_non_ascii_string()
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buf);

        // 'é' is U+00E9, beyond ASCII. Previously silently emitted as 0xE9; now throws.
        var ex = Assert.Throws<ArgumentException>(() => w.WriteAscii("café"));
        Assert.Contains("ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteAscii_throws_on_high_unicode_char()
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buf);

        // U+2603 SNOWMAN (☃) — well outside ASCII.
        Assert.Throws<ArgumentException>(() => w.WriteAscii("hello ☃"));
    }

    [Fact]
    public void WriteAscii_accepts_full_ascii_range()
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buf);

        // Every char from 0x20 (space) through 0x7E (~) is valid ASCII for our purposes.
        for (int c = 0x20; c <= 0x7E; c++)
        {
            w.WriteAscii(((char)c).ToString());
        }

        Assert.Equal(0x7E - 0x20 + 1, buf.WrittenCount);
    }

    [Fact]
    public void Write_byte_span_handles_arbitrary_bytes()
    {
        // Non-ASCII bytes are fine via Write(span); they're just bytes, no character semantics.
        var buf = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buf);

        w.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 });

        Assert.Equal(4, buf.WrittenCount);
        Assert.Equal(0xE2, buf.WrittenSpan[0]);
        Assert.Equal(0xD3, buf.WrittenSpan[3]);
    }

    // ---------------------------------------------------------------- PdfStream /Length invariant

    [Fact]
    public void Stream_writeto_resets_length_after_user_overwrites_it()
    {
        var data = new byte[] { (byte)'B', (byte)'T', (byte)' ', (byte)'E', (byte)'T' };
        var s = new PdfStream(data);

        // User tampers with /Length post-construction.
        s.Dictionary.Set(PdfNames.Length, new PdfInteger(99999));

        var rendered = Render(s);
        var ascii = System.Text.Encoding.Latin1.GetString(rendered);

        // The emitted dictionary must contain the correct length, not the tampered value.
        Assert.Contains($"/Length {data.Length}", ascii);
        Assert.DoesNotContain("/Length 99999", ascii);
    }

    [Fact]
    public void Stream_writeto_restores_length_after_user_removes_it()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var s = new PdfStream(data);

        // User removes /Length entirely (e.g., dictionary cleared).
        s.Dictionary.Remove(PdfNames.Length);

        var rendered = Render(s);
        var ascii = System.Text.Encoding.Latin1.GetString(rendered);

        Assert.Contains($"/Length {data.Length}", ascii);
    }

    [Fact]
    public void Stream_writeto_with_correct_length_emits_unchanged()
    {
        // Idempotent: the constructor's /Length value is correct, and re-setting at
        // emit time produces the same value, so the dictionary contents don't change.
        var data = new byte[] { 1, 2, 3 };
        var s = new PdfStream(data);

        var initial = ((PdfInteger)s.Dictionary.Get(PdfNames.Length)!).Value;
        Render(s);
        var afterEmit = ((PdfInteger)s.Dictionary.Get(PdfNames.Length)!).Value;

        Assert.Equal(initial, afterEmit);
        Assert.Equal(data.Length, afterEmit);
    }

    // ------------------------------------------------------------------------------------

    private static byte[] Render(PdfObject obj)
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buf);
        obj.WriteTo(w);
        return buf.WrittenSpan.ToArray();
    }
}

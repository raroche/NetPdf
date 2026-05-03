// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Hardening pass coverage: the preflight validator's expanded scope (trailer-graph walk,
/// foreign-store ref rejection, cycle protection, explicit /ID shape) and writer reuse
/// behavior. Pairs with PdfPreflightValidatorTests for /Root and basic checks.
/// </summary>
public sealed class PdfPreflightHardeningTests
{
    // ------------------------------------------------------------------- Trailer-graph walk

    [Fact]
    public void Dangling_ref_in_trailer_info_throws()
    {
        var w = MinimalWriter();
        // /Info → some object that doesn't exist.
        w.Trailer.Set(PdfNames.Info, new PdfIndirectRef(99));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("dangling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dangling_ref_in_trailer_nested_dict_throws()
    {
        var w = MinimalWriter();
        var nestedTrailerDict = new PdfDictionary().Set(PdfNames.Author, new PdfIndirectRef(99));
        w.Trailer.Set(PdfNames.Info, nestedTrailerDict);

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    // -------------------------------------------------------- Foreign-store reference rejection

    [Fact]
    public void Foreign_store_ref_in_dictionary_throws()
    {
        // Two stores; ref allocated in storeA is embedded in storeB's catalog.
        var storeA = new IndirectObjectStore();
        var foreignRef = storeA.Add(PdfBoolean.True);

        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Pages, foreignRef); // foreign!
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("StoreId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_store_ref_in_array_throws()
    {
        var storeA = new IndirectObjectStore();
        var foreignRef = storeA.Add(PdfBoolean.True);

        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Kids, new PdfArray().Add(foreignRef));
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    [Fact]
    public void Foreign_store_ref_in_trailer_throws()
    {
        var storeA = new IndirectObjectStore();
        var foreignRef = storeA.Add(PdfBoolean.True);

        var w = MinimalWriter();
        w.Trailer.Set(PdfNames.Info, foreignRef);

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    [Fact]
    public void Synthetic_ref_with_inrange_number_is_accepted()
    {
        // StoreId=0 (synthetic) refs are opaque emit-only pointers — they're allowed to
        // appear in the trailer (e.g., as /Root) as long as the object number resolves.
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        w.Objects.Add(catalog); // becomes object 1
        w.Trailer.Set(PdfNames.Root, new PdfIndirectRef(1)); // synthetic, in-range

        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    // ----------------------------------------------------------------- Cycle protection

    [Fact]
    public void Direct_self_reference_in_array_is_blocked_at_add()
    {
        var arr = new PdfArray();
        var ex = Assert.Throws<InvalidOperationException>(() => arr.Add(arr));
        Assert.Contains("direct cycle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_self_reference_in_dictionary_is_blocked_at_set()
    {
        var dict = new PdfDictionary();
        var ex = Assert.Throws<InvalidOperationException>(() => dict.Set(PdfNames.Title, dict));
        Assert.Contains("direct cycle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Indirect_cycle_in_direct_object_graph_is_rejected_by_preflight()
    {
        // Mutual direct reference: dictA.Resources → dictB and dictB.Parent → dictA.
        // The direct-cycle guard at Set() doesn't catch this (each Set is a→b or b→a, not
        // a→a). Without preflight cycle detection, emission stack-overflows when
        // PdfDictionary.WriteTo recurses indefinitely. Validator must reject before write.
        var dictA = new PdfDictionary();
        var dictB = new PdfDictionary();
        dictA.Set(PdfNames.Resources, dictB);
        dictB.Set(PdfNames.Parent, dictA);

        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Resources, dictA);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cycle_via_indirect_reference_is_allowed()
    {
        // Same shape as the rejected case but the back-edge goes through an indirect ref —
        // dictB.Parent → ref(catalog), not dictB.Parent → catalog directly. Indirect refs
        // are pointers, not inline structure; emission terminates because refs emit as
        // "<n> 0 R" without recursing.
        var w = new PdfDocumentWriter();
        var catalogRef = w.Objects.Allocate();
        var dictB = new PdfDictionary().Set(PdfNames.Parent, catalogRef);
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Resources, dictB);
        w.Objects.Assign(catalogRef, catalog);
        w.Trailer.Set(PdfNames.Root, catalogRef);

        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    [Fact]
    public void Sibling_sharing_of_direct_object_is_allowed()
    {
        // Two dicts in different positions both containing the same PdfHexString instance.
        // Not a cycle — the shared object isn't an ancestor of itself. PDF emit duplicates
        // the bytes, which is wasteful but well-formed. The validator must allow this.
        var shared = new PdfHexString(Convert.FromHexString("DEADBEEF"));
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Title, shared)
            .Set(PdfNames.Author, shared);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    // ----------------------------------------------------------- Explicit /ID shape validation

    [Fact]
    public void Explicit_id_that_is_not_an_array_throws()
    {
        var w = MinimalWriter();
        w.Trailer.Set(PdfNames.ID, new PdfInteger(42));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("/ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_id_with_wrong_arity_throws()
    {
        var w = MinimalWriter();
        var hex = new PdfHexString(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(hex)); // only 1 entry

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("two byte strings", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_id_with_three_entries_throws()
    {
        var w = MinimalWriter();
        var hex = new PdfHexString(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(hex).Add(hex).Add(hex));

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    [Fact]
    public void Explicit_id_with_non_string_entries_throws()
    {
        var w = MinimalWriter();
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(new PdfInteger(1)).Add(new PdfInteger(2)));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("byte string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_id_with_indirect_ref_entry_throws()
    {
        var w = MinimalWriter();
        var bogus = new PdfIndirectRef(1); // valid emit-only ref to the catalog
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(bogus).Add(bogus));

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    [Fact]
    public void Explicit_id_with_two_byte_strings_passes()
    {
        var w = MinimalWriter();
        var hex = new PdfHexString(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(hex).Add(hex));

        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    [Fact]
    public void Explicit_id_with_literal_string_entries_passes()
    {
        var w = MinimalWriter();
        var literal = new PdfLiteralString("16-byte-id-here!");
        w.Trailer.Set(PdfNames.ID, new PdfArray().Add(literal).Add(literal));

        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    // -------------------------------------------------------------------- Writer reuse

    [Fact]
    public void Writer_reused_after_body_mutation_rederives_id()
    {
        // Build, write, capture /ID.
        var w = MinimalWriter();
        var firstBytes = WriteAndCapture(w);
        var firstId = ExtractFirstIdHex(firstBytes);

        // Mutate the body (add an integer object), write again, capture /ID.
        w.Objects.Add(new PdfInteger(42));
        var secondBytes = WriteAndCapture(w);
        var secondId = ExtractFirstIdHex(secondBytes);

        // Different bodies → different content hashes → different /IDs.
        // Without the transient-trailer fix the second /ID would match the first because
        // /ID would be cached in w.Trailer between writes.
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void Writer_reused_with_unchanged_body_produces_identical_output()
    {
        var w = MinimalWriter();
        var first = WriteAndCapture(w);
        var second = WriteAndCapture(w);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Writer_trailer_is_not_mutated_by_writeto()
    {
        // After WriteTo, the user's Trailer must contain only what the user explicitly set.
        // /Size and /ID (when auto-derived) live only in the transient emit dict.
        var w = MinimalWriter();
        var keysBefore = TrailerKeys(w.Trailer);
        WriteAndCapture(w);
        var keysAfter = TrailerKeys(w.Trailer);

        Assert.Equal(keysBefore, keysAfter);
        Assert.False(w.Trailer.ContainsKey(PdfNames.Size));
        Assert.False(w.Trailer.ContainsKey(PdfNames.ID));
    }

    // ------------------------------------------------------------- Nested direct stream rejection

    [Fact]
    public void Direct_stream_nested_inside_dictionary_is_rejected()
    {
        // ISO 32000-2 §7.3.8: streams shall be indirect objects. A PdfStream nested as a
        // value inside another dict (rather than wrapped in IndirectObjectStore.Add) would
        // produce malformed PDF bytes — preflight catches it.
        var nestedStream = new PdfStream([0xDE, 0xAD]);
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(new PdfName("Metadata"), nestedStream); // /Metadata stream nested directly
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
        Assert.Contains("nested", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indirect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Direct_stream_nested_inside_array_is_rejected()
    {
        var nestedStream = new PdfStream([0xDE, 0xAD]);
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(PdfNames.Kids, new PdfArray().Add(nestedStream));
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        Assert.Throws<InvalidOperationException>(() => w.WriteTo(Buffer()));
    }

    [Fact]
    public void Stream_at_top_of_indirect_object_slot_is_allowed()
    {
        // The legitimate case: a PdfStream lives at the top level of an indirect-object
        // slot. Other objects reference it via PdfIndirectRef. This must pass preflight.
        var w = new PdfDocumentWriter();
        var streamRef = w.Objects.Add(new PdfStream([1, 2, 3]));
        var catalog = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Catalog)
            .Set(new PdfName("Metadata"), streamRef);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));

        var buf = Buffer();
        w.WriteTo(buf);
        Assert.True(buf.WrittenCount > 0);
    }

    // ------------------------------------------------------------------------------------

    private static PdfDocumentWriter MinimalWriter()
    {
        var w = new PdfDocumentWriter();
        var catalog = new PdfDictionary().Set(PdfNames.Type, PdfNames.Catalog);
        w.Trailer.Set(PdfNames.Root, w.Objects.Add(catalog));
        return w;
    }

    private static ArrayBufferWriter<byte> Buffer() => new();

    private static byte[] WriteAndCapture(PdfDocumentWriter w)
    {
        var buf = Buffer();
        w.WriteTo(buf);
        return buf.WrittenSpan.ToArray();
    }

    private static string ExtractFirstIdHex(byte[] bytes)
    {
        var ascii = System.Text.Encoding.Latin1.GetString(bytes);
        int idIdx = ascii.IndexOf("/ID [", StringComparison.Ordinal);
        Assert.True(idIdx >= 0);
        int open = ascii.IndexOf('<', idIdx);
        int close = ascii.IndexOf('>', open + 1);
        return ascii.Substring(open + 1, close - open - 1);
    }

    private static List<PdfName> TrailerKeys(PdfDictionary trailer)
    {
        var keys = new List<PdfName>();
        foreach (var entry in trailer) keys.Add(entry.Key);
        return keys;
    }
}

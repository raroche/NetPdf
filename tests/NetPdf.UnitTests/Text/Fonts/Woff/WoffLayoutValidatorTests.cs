// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Tests for <see cref="WoffLayoutValidator"/> — the strict file-layout enforcement that
/// runs after header + directory parse and before any decompression. Each test mutates a
/// synthetic WOFF to violate one specific layout invariant and asserts the validator
/// rejects with <see cref="InvalidDataException"/>.
/// </summary>
public sealed class WoffLayoutValidatorTests
{
    private const int HeaderTotalSfntSizeOffset = 16;
    private const int FirstRecordOffset = 44;
    private const int RecordOffsetField = 4;
    private const int RecordCompLengthField = 8;
    private const int RecordOrigLengthField = 12;

    [Fact]
    public void Validate_accepts_well_formed_synthetic_woff()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        // Should not throw.
        WoffLayoutValidator.Validate(header, entries, woffBytes);
    }

    [Fact]
    public void Validate_rejects_totalSfntSize_mismatch()
    {
        // Bump totalSfntSize by 4 (still a multiple of 4 so WoffHeader.Parse passes) — the
        // value no longer matches the cumulative origLengths derived sum, and the layout
        // validator should reject.
        var woffBytes = SyntheticWoff.Build();
        var current = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(HeaderTotalSfntSizeOffset, 4));
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(HeaderTotalSfntSizeOffset, 4), current + 4);
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, woffBytes));
        Assert.Contains("totalSfntSize", ex.Message);
    }

    [Fact]
    public void Validate_rejects_huge_origLength_via_max_size_cap()
    {
        // Set the first table's origLength to a value that, when summed, exceeds MaxSfntSize.
        // The validator should reject before any per-table allocation — this is the
        // memory-exhaustion-attack defense.
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt32BigEndian(
            woffBytes.AsSpan(FirstRecordOffset + RecordOrigLengthField, 4),
            (uint)WoffLayoutValidator.MaxSfntSize + 1024);
        // Also bump compLength to avoid the per-entry compLength <= origLength check from
        // succeeding on a tiny compressed payload (we don't want a different failure path).
        BinaryPrimitives.WriteUInt32BigEndian(
            woffBytes.AsSpan(FirstRecordOffset + RecordCompLengthField, 4),
            (uint)WoffLayoutValidator.MaxSfntSize + 1024);
        var header = WoffHeader.Parse(woffBytes);
        // ParseDirectory may itself reject for offset+compLength > file_length; that's fine —
        // either rejection prevents the unbounded allocation.
        try
        {
            var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
            var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, woffBytes));
            Assert.Contains("safety cap", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            // Acceptable — the trust boundary held earlier in the pipeline.
            Assert.Contains("WOFF", ex.Message);
        }
    }

    [Fact]
    public void Validate_rejects_table_offset_not_4_byte_aligned()
    {
        // Shift the first table offset by 1 (was 4-byte aligned by construction). To keep
        // the layout otherwise sane, we move both offset and compLength so the table still
        // fits — but the alignment violation is what we want the validator to catch.
        var woffBytes = SyntheticWoff.Build();
        var origOffset = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(FirstRecordOffset + RecordOffsetField, 4));
        // Increment offset by 1 — now it's not 4-byte aligned. We don't actually move the
        // payload bytes, so the contiguity check would also catch this — but the validator
        // checks alignment first per the order of validations.
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(FirstRecordOffset + RecordOffsetField, 4), origOffset + 1);
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, woffBytes));
        // Either the alignment error or the contiguity error is acceptable rejection.
        Assert.True(
            ex.Message.Contains("4-byte aligned") || ex.Message.Contains("extraneous"),
            $"Unexpected rejection message: {ex.Message}");
    }

    [Fact]
    public void Validate_rejects_extraneous_trailing_bytes()
    {
        // Append a few extraneous bytes to the end of the file and bump header.length to
        // match, so WoffHeader.Parse passes (length == buffer length). The layout validator
        // detects that the final block ends earlier than header.Length.
        var woffBytes = SyntheticWoff.Build();
        var withTail = new byte[woffBytes.Length + 8];
        woffBytes.AsSpan().CopyTo(withTail);
        BinaryPrimitives.WriteUInt32BigEndian(withTail.AsSpan(8, 4), (uint)withTail.Length);
        var header = WoffHeader.Parse(withTail);
        var entries = WoffTableEntry.ParseDirectory(withTail, header);
        var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, withTail));
        Assert.Contains("trailing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_non_zero_alignment_padding()
    {
        // Find a synthetic table that ends mid-4-byte-boundary so we know there's an
        // alignment-padding byte, then write a non-zero value into that byte.
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        for (var i = 0; i < entries.Length - 1; i++)
        {
            var endOfThisTable = entries[i].Offset + entries[i].CompLength;
            var startOfNextTable = entries[i + 1].Offset;
            if (startOfNextTable > endOfThisTable)
            {
                // Write a non-zero byte in the padding.
                woffBytes[(int)endOfThisTable] = 0xFF;
                var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, woffBytes));
                Assert.Contains("padding", ex.Message, StringComparison.OrdinalIgnoreCase);
                return;
            }
        }
        // If no padding exists between any pair (all tables happened to be exact 4-byte
        // multiples), this test is inert — the synthetic font's structure makes it very
        // likely at least one pair has padding, but assert defensively that we exercised a case.
        Assert.Fail("Synthetic font produced no inter-table padding to mutate; test cannot exercise the validator path.");
    }

    [Fact]
    public void Validate_rejects_overlapping_tables()
    {
        // Set the second table's offset to the same value as the first — guaranteed to
        // overlap (first table's payload still occupies that byte range) and 4-byte
        // aligned (since the first offset was), so the alignment check passes and the
        // overlap check is reached.
        var woffBytes = SyntheticWoff.Build();
        var firstOffset = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(FirstRecordOffset + RecordOffsetField, 4));
        const int secondRecordOffset = FirstRecordOffset + 20;
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(secondRecordOffset + RecordOffsetField, 4), firstOffset);
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, woffBytes));
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaxSfntSize_is_a_reasonable_safety_cap()
    {
        // Cap is documented as 256 MiB. Lock in via a property-based assertion.
        Assert.Equal(256L * 1024 * 1024, WoffLayoutValidator.MaxSfntSize);
    }

    // ───── Layout walks must respect physical (offset) order, not directory order ─

    [Fact]
    public void Validate_accepts_offset_order_different_from_directory_tag_order()
    {
        // W3C WOFF 1.0 §3 mandates tag-ascending DIRECTORY order but leaves on-disk table
        // order unspecified — a conforming WOFF can lay tables out in any offset order.
        // The layout validator must walk the offset-sorted view, not the directory order,
        // so this test pins down the correct behavior.
        var woffBytes = SyntheticWoff.BuildWithReversedPayloadOrder();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        // Directory remains tag-ascending (would have failed ParseDirectory otherwise).
        // Offsets are reversed relative to the tag order — first directory entry has the
        // LARGEST offset; last directory entry has the smallest.
        Assert.True(entries[0].Offset > entries[^1].Offset);
        // Layout validator must accept this conforming layout.
        WoffLayoutValidator.Validate(header, entries, woffBytes);
    }

    // ───── Metadata + private padding (W3C WOFF 1.0 §3 PrivateData) ─────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Validate_accepts_zero_to_three_padding_bytes_between_metadata_and_private(int pad)
    {
        // §3 PrivateData: "with up to three bytes of zero padding to align it on a 4-byte
        // boundary". The validator must accept any pad in [0, 3].
        // Use metaCompLength values that combine with `pad` to land privOffset on a 4-byte
        // boundary — metaCompLength=5 leaves 3 bytes of natural padding; we add `pad` more
        // to test the various alignment scenarios.
        var woffBytes = SyntheticWoff.BuildWithMetadataAndPrivate(metaCompLength: 5, padBetween: pad, privLength: 16);
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        WoffLayoutValidator.Validate(header, entries, woffBytes);
    }

    [Fact]
    public void Validate_rejects_more_than_three_padding_bytes_between_metadata_and_private()
    {
        // Build metadata + private with the standard (≤3) padding, then mutate the
        // private offset to push it 4 more bytes ahead — that's 4-7 bytes of padding,
        // which exceeds the spec's 3-byte cap.
        var woffBytes = SyntheticWoff.BuildWithMetadataAndPrivate(metaCompLength: 5, padBetween: 0, privLength: 16);
        var origPrivOffset = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(36, 4));
        // Move privOffset 8 bytes ahead (still 4-byte aligned, but creates 8-byte gap).
        var newPrivOffset = origPrivOffset + 8;
        var origPrivLength = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(40, 4));
        // Need to grow the file by 8 to accommodate the shifted private block.
        var grown = new byte[woffBytes.Length + 8];
        woffBytes.AsSpan(0, (int)origPrivOffset).CopyTo(grown);
        // Leave the 8 extra bytes as zero (would be the "extra padding").
        woffBytes.AsSpan((int)origPrivOffset, (int)origPrivLength).CopyTo(grown.AsSpan((int)newPrivOffset));
        BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(8, 4), (uint)grown.Length);
        BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(36, 4), newPrivOffset);

        var header = WoffHeader.Parse(grown);
        var entries = WoffTableEntry.ParseDirectory(grown, header);
        var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, grown));
        Assert.Contains("extraneous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ───── Metadata-present + private-absent layout ────────────────────────────

    [Fact]
    public void Validate_accepts_metadata_present_with_private_absent()
    {
        // metaCompLength = 5, no private. The metadata block ends at metaOffset + 5; per
        // spec there must be no extraneous trailing bytes (file ends exactly there). The
        // synthesizer with privLength=0 produces this layout.
        var woffBytes = SyntheticWoff.BuildWithMetadataAndPrivate(metaCompLength: 5, padBetween: 0, privLength: 0);
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        WoffLayoutValidator.Validate(header, entries, woffBytes);
    }

    [Fact]
    public void Validate_rejects_extraneous_bytes_after_metadata_when_private_absent()
    {
        // Build metadata-only layout, then append 4 trailing bytes + bump header.length
        // to match. The validator must reject — §3 forbids extraneous data after the
        // last block.
        var baseline = SyntheticWoff.BuildWithMetadataAndPrivate(metaCompLength: 8, padBetween: 0, privLength: 0);
        var grown = new byte[baseline.Length + 4];
        baseline.CopyTo(grown, 0);
        BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(8, 4), (uint)grown.Length);
        var header = WoffHeader.Parse(grown);
        var entries = WoffTableEntry.ParseDirectory(grown, header);
        var ex = Assert.Throws<InvalidDataException>(() => WoffLayoutValidator.Validate(header, entries, grown));
        Assert.Contains("trailing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_documents_metadata_present_origLength_zero_behavior()
    {
        // When metadata is present (metaOffset > 0, metaLength > 0), the spec strictly
        // requires metaOrigLength to be the uncompressed metadata XML size. The current
        // implementation does NOT verify that consistency — it accepts metaOrigLength = 0
        // with non-zero metaLength. This test pins down that current behavior so a future
        // strict-validation pass can find it (Stage 12.4 hardening or post-1.0).
        var woffBytes = SyntheticWoff.BuildWithMetadataAndPrivate(metaCompLength: 8, padBetween: 0, privLength: 0);
        // Patch metaOrigLength to 0 (intentionally inconsistent — non-zero metaLength,
        // zero origLength). Position 32-35 is the metaOrigLength field.
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(32, 4), 0);
        // Currently accepts. If a future hardening pass tightens this, the test will fail
        // and need updating with a documenting note about the new strict behavior.
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        WoffLayoutValidator.Validate(header, entries, woffBytes);
    }

    [Fact]
    public void Decode_round_trips_through_reversed_payload_order_layout()
    {
        // End-to-end: the WoffDecoder pipeline must accept the reversed-payload layout
        // and produce a valid SFNT. Confirms the layout validator wiring + decompression
        // path both honor offset order.
        var woffBytes = SyntheticWoff.BuildWithReversedPayloadOrder();
        var sfnt = WoffDecoder.Decode(woffBytes);
        Assert.NotEmpty(sfnt);
        // The decoded SFNT must be parseable by OpenTypeFont.Parse — basic structural check.
        var font = NetPdf.Text.Fonts.OpenType.OpenTypeFont.Parse(sfnt);
        Assert.NotNull(font);
        Assert.True(font.HasTrueTypeOutlines);
    }
}

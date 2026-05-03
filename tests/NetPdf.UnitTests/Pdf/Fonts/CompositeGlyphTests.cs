// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Pdf.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Unit coverage for the shared <see cref="CompositeGlyph"/> walker — the single source
/// of truth for component-record validation that both the planner and the emitter call.
/// </summary>
public sealed class CompositeGlyphTests
{
    [Fact]
    public void IsComposite_returns_true_when_numberOfContours_is_negative()
    {
        var bytes = SyntheticFontWithComposite.BuildCompositeGlyph(referencedGlyphId: 1);
        Assert.True(CompositeGlyph.IsComposite(bytes));
    }

    [Fact]
    public void IsComposite_returns_false_for_simple_glyph()
    {
        var bytes = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.GlyfBytes(); // simple glyph
        Assert.False(CompositeGlyph.IsComposite(bytes));
    }

    [Fact]
    public void IsComposite_returns_false_for_empty_or_short_glyph_bytes()
    {
        Assert.False(CompositeGlyph.IsComposite(ReadOnlySpan<byte>.Empty));
        Assert.False(CompositeGlyph.IsComposite(new byte[] { 0xFF })); // 1 byte — too short to read header
    }

    [Fact]
    public void EnsureValidHeader_passes_for_empty_or_full_header()
    {
        CompositeGlyph.EnsureValidHeader(ReadOnlySpan<byte>.Empty, glyphIndex: 0);     // empty is OK
        CompositeGlyph.EnsureValidHeader(new byte[10], glyphIndex: 0);                 // exact header is OK
        CompositeGlyph.EnsureValidHeader(new byte[100], glyphIndex: 0);                // larger is OK
    }

    [Fact]
    public void EnsureValidHeader_throws_for_non_empty_data_smaller_than_header()
    {
        for (var len = 1; len < 10; len++)
        {
            Assert.Throws<InvalidDataException>(() =>
                CompositeGlyph.EnsureValidHeader(new byte[len], glyphIndex: 7));
        }
    }

    [Fact]
    public void EnumerateComponents_returns_one_entry_per_component_record()
    {
        var bytes = SyntheticFontWithComposite.BuildCompositeGlyph(referencedGlyphId: 1);
        var components = CompositeGlyph.EnumerateComponents(bytes);
        Assert.Single(components);
        Assert.Equal((ushort)1, components[0].GlyphIndex);
        // The synthetic builder lays glyphIndex at offset 12 (10-byte header + 2-byte flags).
        Assert.Equal(12, components[0].GlyphIndexByteOffset);
    }

    [Fact]
    public void EnumerateComponents_walks_a_two_component_chain()
    {
        var bytes = BuildTwoComponentComposite(firstGlyph: 5, secondGlyph: 9);
        var components = CompositeGlyph.EnumerateComponents(bytes);
        Assert.Equal(2, components.Count);
        Assert.Equal((ushort)5, components[0].GlyphIndex);
        Assert.Equal((ushort)9, components[1].GlyphIndex);
    }

    [Fact]
    public void EnumerateComponents_validates_WE_HAVE_INSTRUCTIONS_trailer()
    {
        var bytes = BuildCompositeWithInstructions(
            referencedGlyph: 1,
            instructionBytes: new byte[] { 0xAA, 0xBB, 0xCC });
        var components = CompositeGlyph.EnumerateComponents(bytes);
        Assert.Single(components);
        Assert.Equal((ushort)1, components[0].GlyphIndex);
    }

    [Fact]
    public void EnumerateComponents_throws_on_truncated_instruction_payload()
    {
        // Declare 5 instruction bytes but only supply 2.
        var bytes = BuildCompositeWithInstructions(referencedGlyph: 1, instructionBytes: new byte[] { 1, 2 });
        // Now corrupt the instructionLength field to 99.
        var instructionLengthOffset = bytes.Length - 4; // 2 bytes length + 2 bytes payload
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(instructionLengthOffset, 2), 99);
        Assert.Throws<InvalidDataException>(() => CompositeGlyph.EnumerateComponents(bytes));
    }

    [Fact]
    public void EnumerateComponents_throws_on_truncated_component_header()
    {
        // 10-byte glyph header + only 2 bytes of a component (need 4 for flags + glyphIndex).
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(0, 2), -1); // composite
        Assert.Throws<InvalidDataException>(() => CompositeGlyph.EnumerateComponents(bytes));
    }

    [Fact]
    public void EnumerateComponents_throws_on_truncated_argument_pair()
    {
        // 10-byte header + flags (with ARG_1_AND_2_ARE_WORDS=1) + glyphIndex but no args.
        var bytes = new byte[14];
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(0, 2), -1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), 0x0001); // word args, no MORE
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12, 2), 7); // glyphIndex
        // No arg bytes follow → truncated.
        Assert.Throws<InvalidDataException>(() => CompositeGlyph.EnumerateComponents(bytes));
    }

    [Fact]
    public void EnumerateComponents_throws_on_truncated_transform()
    {
        // 10-byte header + flags(0x0080 = WE_HAVE_A_TWO_BY_TWO, no MORE) + glyphIndex + 2 byte args + only 4 bytes of an 8-byte transform.
        var bytes = new byte[20];
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(0, 2), -1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), 0x0080);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12, 2), 7);
        // arg1, arg2 (1 byte each) at 14, 15
        // transform should be 8 bytes at offsets 16..24 — only 4 supplied
        Assert.Throws<InvalidDataException>(() => CompositeGlyph.EnumerateComponents(bytes));
    }

    [Fact]
    public void EnumerateComponents_throws_when_called_on_simple_glyph()
    {
        var simple = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.GlyfBytes();
        Assert.Throws<InvalidOperationException>(() => CompositeGlyph.EnumerateComponents(simple));
    }

    /// <summary>
    /// Build an 18 + 8 = 26-byte composite glyph with two single-component records.
    /// First record has MORE_COMPONENTS set; second has it cleared.
    /// </summary>
    private static byte[] BuildTwoComponentComposite(ushort firstGlyph, ushort secondGlyph)
    {
        var bytes = new byte[26];
        var span = bytes.AsSpan();
        // Header
        BinaryPrimitives.WriteInt16BigEndian(span[0..2], -1);
        // First component: flags = ARG_1_AND_2_ARE_WORDS | MORE_COMPONENTS = 0x0021
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0x0021);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], firstGlyph);
        // 4 bytes of args (word form): arg1 + arg2 at 14..18
        // Second component: flags = ARG_1_AND_2_ARE_WORDS = 0x0001 (no MORE)
        BinaryPrimitives.WriteUInt16BigEndian(span[18..20], 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(span[20..22], secondGlyph);
        // 4 bytes of args (word form) at 22..26
        return bytes;
    }

    /// <summary>
    /// Build a composite glyph whose single component sets WE_HAVE_INSTRUCTIONS, followed
    /// by the instructionLength field and exactly that many instruction bytes.
    /// </summary>
    private static byte[] BuildCompositeWithInstructions(ushort referencedGlyph, byte[] instructionBytes)
    {
        // 10-byte header + 8-byte component (no transform, word args) + 2-byte instructionLength + N instruction bytes
        var bytes = new byte[10 + 8 + 2 + instructionBytes.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt16BigEndian(span[0..2], -1);
        // Component flags = WE_HAVE_INSTRUCTIONS | ARG_1_AND_2_ARE_WORDS = 0x0101
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], referencedGlyph);
        // arg1, arg2 (word form) at 14..18
        // Instruction trailer at 18..18+2+N
        BinaryPrimitives.WriteUInt16BigEndian(span[18..20], (ushort)instructionBytes.Length);
        instructionBytes.CopyTo(bytes, 20);
        return bytes;
    }
}

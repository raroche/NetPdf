// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

public sealed class CffDictTests
{
    [Fact]
    public void Decodes_one_byte_int_operand()
    {
        // Single-byte int 0 = byte 139 (encodes operand value 0). Followed by op 17 (CharStrings).
        var dict = new byte[] { 139, 17 };
        var entries = CffDict.Parse(dict);
        Assert.True(entries.ContainsKey(CffDict.OpCharStrings));
        Assert.Single(entries[CffDict.OpCharStrings]);
        Assert.Equal(0.0, entries[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_one_byte_int_operand_at_full_range()
    {
        // Byte 32 → operand -107; byte 246 → operand +107.
        Assert.Equal(-107.0, CffDict.Parse(new byte[] { 32, 17 })[CffDict.OpCharStrings][0]);
        Assert.Equal(107.0, CffDict.Parse(new byte[] { 246, 17 })[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_two_byte_positive_int_operand()
    {
        // (247..250) high-positive form: ((b0 - 247) * 256) + b1 + 108.
        // 247, 0 → 108. 250, 255 → ((250-247)*256) + 255 + 108 = 768 + 363 = 1131.
        Assert.Equal(108.0, CffDict.Parse(new byte[] { 247, 0, 17 })[CffDict.OpCharStrings][0]);
        Assert.Equal(1131.0, CffDict.Parse(new byte[] { 250, 255, 17 })[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_two_byte_negative_int_operand()
    {
        // (251..254) low-negative form: -((b0 - 251) * 256) - b1 - 108.
        Assert.Equal(-108.0, CffDict.Parse(new byte[] { 251, 0, 17 })[CffDict.OpCharStrings][0]);
        Assert.Equal(-1131.0, CffDict.Parse(new byte[] { 254, 255, 17 })[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_three_byte_int_operand()
    {
        // 28, 0x12, 0x34 → 0x1234 = 4660.
        Assert.Equal(4660.0, CffDict.Parse(new byte[] { 28, 0x12, 0x34, 17 })[CffDict.OpCharStrings][0]);
        // Negative form: 28, 0xFF, 0xFE → -2.
        Assert.Equal(-2.0, CffDict.Parse(new byte[] { 28, 0xFF, 0xFE, 17 })[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_five_byte_int_operand()
    {
        // 29, 0x00, 0x01, 0x00, 0x00 → 65536.
        Assert.Equal(65536.0, CffDict.Parse(new byte[] { 29, 0x00, 0x01, 0x00, 0x00, 17 })[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_real_operand()
    {
        // Real -2.25E5 → nibbles e (= '-') 2 a (= '.') 2 5 b (= 'E') 5 f (= terminator).
        // Pack into bytes: 0xE2, 0xA2, 0x5B, 0x5F.
        var dict = new byte[] { 30, 0xE2, 0xA2, 0x5B, 0x5F, 17 };
        var entries = CffDict.Parse(dict);
        Assert.Equal(-2.25E5, entries[CffDict.OpCharStrings][0]);
    }

    [Fact]
    public void Decodes_two_byte_operator_with_escape()
    {
        // Empty operand stack + escape 12 + 30 (ROS marker).
        var dict = new byte[] { CffDict.EscapeByte, 30 };
        var entries = CffDict.Parse(dict);
        Assert.True(entries.ContainsKey(CffDict.OpRos));
        Assert.Empty(entries[CffDict.OpRos]);
    }

    [Fact]
    public void Operator_resets_operand_stack_for_next_operator()
    {
        // [5][op 17 CharStrings] [10][op 15 Charset]
        var dict = new byte[] { 144, 17, 149, 15 };
        var entries = CffDict.Parse(dict);
        Assert.Equal(5.0, entries[CffDict.OpCharStrings][0]);
        Assert.Equal(10.0, entries[CffDict.OpCharset][0]);
    }

    [Fact]
    public void Multiple_operands_accumulate_until_operator()
    {
        // [1] [2] [3] [4] op 5 (FontBBox is operator 5).
        var dict = new byte[] { 140, 141, 142, 143, 5 };
        var entries = CffDict.Parse(dict);
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0 }, entries[5]);
    }

    [Fact]
    public void Throws_on_truncated_three_byte_operand()
    {
        Assert.Throws<InvalidDataException>(() => CffDict.Parse(new byte[] { 28, 0x12 }));
    }

    [Fact]
    public void Throws_on_reserved_operand_byte()
    {
        Assert.Throws<InvalidDataException>(() => CffDict.Parse(new byte[] { 22, 17 }));
    }
}

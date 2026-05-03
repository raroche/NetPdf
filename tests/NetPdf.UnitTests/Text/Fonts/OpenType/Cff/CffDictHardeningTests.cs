// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Post-Task-7 hardening: <see cref="CffDict.Parse"/> surfaces malformed real operands as
/// <see cref="InvalidDataException"/> rather than letting <c>FormatException</c> /
/// <c>OverflowException</c> escape from <c>double.Parse</c>.
/// </summary>
public sealed class CffDictHardeningTests
{
    [Fact]
    public void ParseReal_throws_when_operand_contains_only_a_terminator()
    {
        // 0x1E (real marker) immediately followed by 0xF0 (terminator nibble first) →
        // empty digit sequence.
        Assert.Throws<InvalidDataException>(() => CffDict.Parse(new byte[] { 30, 0xFF, 17 }));
    }

    [Fact]
    public void ParseReal_throws_on_repeated_exponent_marker()
    {
        // Nibbles: 1 E E 5 F → "1EE5" — invalid double literal.
        // Pack: 0x1B 0xB5 0xF0 (1, B=E, B=E, 5, F)... that's 5 nibbles plus a dangling F.
        // Let's pack: nibbles [1, B, B, 5, F] → bytes 0x1B 0xB5 0xF0.
        Assert.Throws<InvalidDataException>(() => CffDict.Parse(new byte[] { 30, 0x1B, 0xB5, 0xF0, 17 }));
    }

    [Fact]
    public void ParseReal_throws_on_orphan_exponent_minus()
    {
        // Nibbles: 1 C F → "1E-" with no exponent digits.
        // Bytes: 0x1C 0xF0 (1, C=E-, F).
        Assert.Throws<InvalidDataException>(() => CffDict.Parse(new byte[] { 30, 0x1C, 0xF0, 17 }));
    }

    [Fact]
    public void Valid_real_still_parses_after_hardening()
    {
        // Sanity check: -2.25E5 must still decode correctly through the hardened path.
        var entries = CffDict.Parse(new byte[] { 30, 0xE2, 0xA2, 0x5B, 0x5F, 17 });
        Assert.Equal(-2.25E5, entries[CffDict.OpCharStrings][0]);
    }
}

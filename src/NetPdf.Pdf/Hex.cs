// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf;

internal static class Hex
{
    /// <summary>Returns the upper-case hex digit (0-9, A-F) for the high nibble of <paramref name="b"/>.</summary>
    public static byte HighNibble(byte b) => HexDigit(b >> 4);

    /// <summary>Returns the upper-case hex digit for the low nibble of <paramref name="b"/>.</summary>
    public static byte LowNibble(byte b) => HexDigit(b & 0x0F);

    private static byte HexDigit(int nibble) =>
        (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}

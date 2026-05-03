// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Variable-length integer encodings used by WOFF 2.0. Spec basis: W3C "WOFF File Format
/// 2.0" Recommendation §4.1 (UIntBase128 in the table directory) and §5.2 + §6.1.1
/// (255UInt16 in the glyf transform). Clean-room implementation from the spec text.
/// </summary>
internal static class WoffTwoVarInt
{
    /// <summary>
    /// Read a UIntBase128-encoded unsigned integer from <paramref name="span"/> starting
    /// at <paramref name="cursor"/>. Advances <paramref name="cursor"/> past the consumed
    /// bytes. Returns the decoded value.
    /// </summary>
    /// <remarks>
    /// Per §4.1: "A UIntBase128 encoded number is a sequence of bytes for which the most
    /// significant bit is set for all but the last byte, and clear for the last byte." The
    /// decoder rejects:
    /// <list type="bullet">
    ///   <item>Leading-byte 0x80 (would represent a leading zero — invalid per spec).</item>
    ///   <item>Sequences longer than 5 bytes.</item>
    ///   <item>Values that overflow the 32-bit unsigned target.</item>
    /// </list>
    /// </remarks>
    public static uint ReadUIntBase128(ReadOnlySpan<byte> span, ref int cursor)
    {
        uint accum = 0;
        for (var i = 0; i < 5; i++)
        {
            if (cursor >= span.Length)
            {
                throw new InvalidDataException("WOFF2: UIntBase128 sequence truncated by end-of-buffer.");
            }
            var b = span[cursor++];

            // Reject leading 0x80 — per spec it would encode a leading zero, which is invalid.
            if (i == 0 && b == 0x80)
            {
                throw new InvalidDataException("WOFF2: UIntBase128 leading byte 0x80 is invalid (leading zero).");
            }

            // Detect 32-bit overflow before the shift.
            if ((accum & 0xFE000000u) != 0)
            {
                throw new InvalidDataException("WOFF2: UIntBase128 value exceeds 32-bit unsigned range.");
            }

            accum = (accum << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return accum;
            }
        }
        throw new InvalidDataException("WOFF2: UIntBase128 sequence exceeds maximum 5 bytes.");
    }

    /// <summary>
    /// Read a 255UInt16-encoded unsigned integer from <paramref name="span"/> starting at
    /// <paramref name="cursor"/>. Advances <paramref name="cursor"/> past the consumed
    /// bytes. Result is in the range <c>[0, 65535]</c>.
    /// </summary>
    /// <remarks>
    /// Per §6.1.1: a single byte encodes [0, 252]; <c>255</c> followed by a byte encodes
    /// <c>253 + b</c>; <c>254</c> followed by a byte encodes <c>506 + b</c>; <c>253</c>
    /// followed by two bytes encodes the 16-bit big-endian value. The encoding is
    /// non-canonical — a value in [0, 252] could be represented either as a single byte
    /// or via the <c>253</c>-prefix word form; the spec's reference table treats both as
    /// valid (we do too, matching encoder flexibility).
    /// </remarks>
    public static ushort Read255UInt16(ReadOnlySpan<byte> span, ref int cursor)
    {
        const byte OneMoreByteCode1 = 255;
        const byte OneMoreByteCode2 = 254;
        const byte WordCode = 253;
        const ushort LowestUCode = 253;

        if (cursor >= span.Length)
        {
            throw new InvalidDataException("WOFF2: 255UInt16 truncated at start.");
        }
        var code = span[cursor++];
        switch (code)
        {
            case WordCode:
                if (cursor + 2 > span.Length)
                {
                    throw new InvalidDataException("WOFF2: 255UInt16 word-form truncated.");
                }
                var hi = span[cursor++];
                var lo = span[cursor++];
                return (ushort)((hi << 8) | lo);
            case OneMoreByteCode1:
                if (cursor >= span.Length)
                {
                    throw new InvalidDataException("WOFF2: 255UInt16 oneMoreByte1 truncated.");
                }
                return (ushort)(LowestUCode + span[cursor++]);
            case OneMoreByteCode2:
                if (cursor >= span.Length)
                {
                    throw new InvalidDataException("WOFF2: 255UInt16 oneMoreByte2 truncated.");
                }
                return (ushort)(LowestUCode + 253 + span[cursor++]);
            default:
                return code;
        }
    }
}

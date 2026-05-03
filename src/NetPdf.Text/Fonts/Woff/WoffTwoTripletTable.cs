// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// 128-entry triplet decoding lookup table for WOFF 2.0 simple-glyph coordinates.
/// Spec basis: W3C "WOFF File Format 2.0" §5.2 Table 6, transcribed verbatim
/// (reference data, not implementation code — clean-room).
/// </summary>
/// <remarks>
/// <para>
/// Each entry tells the glyf transform reversal how many bytes to read from
/// <c>glyphStream</c> after a triplet-coded flag byte and how to decode them into a
/// signed (xDelta, yDelta) point delta. Entry indices 0..127 correspond to bits 0..6
/// of the flag byte (bit 7 carries the on-curve bit, handled separately).
/// </para>
/// <para>
/// Decode formula per row: <c>raw</c> is the unsigned value extracted from coord bytes
/// (<c>0..(2^XBits − 1)</c>); the produced delta is
/// <c>(raw + Delta) × Sign</c>, with the sign read off the table. When a direction's
/// bit-count is 0, that direction contributes 0 (the row's Delta and Sign are unused
/// for that direction).
/// </para>
/// </remarks>
internal static class WoffTwoTripletTable
{
    /// <summary>One row of the triplet decoding table.</summary>
    public readonly record struct Entry(byte ByteCount, byte XBits, byte YBits, short DeltaX, short DeltaY, sbyte XSign, sbyte YSign);

    /// <summary>The 128-entry lookup. Index = bits 0..6 of a flagStream byte.</summary>
    public static readonly Entry[] Entries = BuildEntries();

    private static Entry[] BuildEntries()
    {
        var t = new Entry[128];

        // Indices 0..9 — yBits=8, xBits=0. ByteCount=2 (flag + 1 Y byte).
        // deltaY = 0, 256, 512, 768, 1024 paired by (negative, positive).
        short[] yDeltas09 = [0, 256, 512, 768, 1024];
        for (var i = 0; i < 10; i++)
        {
            var dy = yDeltas09[i / 2];
            sbyte ys = (i & 1) == 0 ? (sbyte)-1 : (sbyte)+1;
            t[i] = new Entry(ByteCount: 2, XBits: 0, YBits: 8, DeltaX: 0, DeltaY: dy, XSign: 0, YSign: ys);
        }

        // Indices 10..19 — xBits=8, yBits=0. ByteCount=2 (flag + 1 X byte).
        // deltaX = 0, 256, 512, 768, 1024 paired by sign.
        short[] xDeltas1019 = [0, 256, 512, 768, 1024];
        for (var i = 10; i < 20; i++)
        {
            var k = i - 10;
            var dx = xDeltas1019[k / 2];
            sbyte xs = (k & 1) == 0 ? (sbyte)-1 : (sbyte)+1;
            t[i] = new Entry(ByteCount: 2, XBits: 8, YBits: 0, DeltaX: dx, DeltaY: 0, XSign: xs, YSign: 0);
        }

        // Indices 20..83 — xBits=4, yBits=4. ByteCount=2 (flag + 1 packed byte).
        // 4-byte sign axis (xSign × ySign with both axes flipping) gives 4 sign-pairs
        // per (deltaX, deltaY) tuple. Spec orders the (deltaX, deltaY) pairs as:
        //   (1,1), (17,1), (33,1), (49,1),
        //   (1,17), (17,17), (33,17), (49,17),
        //   (33,33), (49,33),
        //   (1,49), (17,49), (49,49), (33,49),
        //   (1,33), (17,33)
        // — the exact 16-pair sequence transcribed from §5.2 Table 6.
        (short dx, short dy)[] pairs2083 =
        [
            (1, 1),  (17, 1),  (33, 1),  (49, 1),
            (1, 17), (17, 17), (33, 17), (49, 17),
            (33, 33), (49, 33),
            (1, 49), (17, 49), (49, 49), (33, 49),
            (1, 33), (17, 33),
        ];
        for (var p = 0; p < pairs2083.Length; p++)
        {
            var (dx, dy) = pairs2083[p];
            for (var s = 0; s < 4; s++)
            {
                // Sign bits: bit 0 = xSign(+ if 1), bit 1 = ySign(+ if 1).
                sbyte xs = (s & 1) == 0 ? (sbyte)-1 : (sbyte)+1;
                sbyte ys = (s & 2) == 0 ? (sbyte)-1 : (sbyte)+1;
                t[20 + p * 4 + s] = new Entry(2, 4, 4, dx, dy, xs, ys);
            }
        }

        // Indices 84..119 — xBits=8, yBits=8. ByteCount=3 (flag + 1 X byte + 1 Y byte).
        // Pairs of (deltaX, deltaY) cover {1, 257, 513} × {1, 257, 513} with a specific
        // ordering. 9 pairs × 4 sign combinations = 36 entries.
        (short dx, short dy)[] pairs84119 =
        [
            (1, 1),     (257, 1),   (513, 1),
            (1, 257),   (257, 257), (513, 257),
            (1, 513),   (257, 513), (513, 513),
        ];
        for (var p = 0; p < pairs84119.Length; p++)
        {
            var (dx, dy) = pairs84119[p];
            for (var s = 0; s < 4; s++)
            {
                sbyte xs = (s & 1) == 0 ? (sbyte)-1 : (sbyte)+1;
                sbyte ys = (s & 2) == 0 ? (sbyte)-1 : (sbyte)+1;
                t[84 + p * 4 + s] = new Entry(3, 8, 8, dx, dy, xs, ys);
            }
        }

        // Indices 120..123 — xBits=12, yBits=12. ByteCount=4 (flag + 3 packed bytes).
        // delta = 0, raw is signed via the explicit signs.
        for (var s = 0; s < 4; s++)
        {
            sbyte xs = (s & 1) == 0 ? (sbyte)-1 : (sbyte)+1;
            sbyte ys = (s & 2) == 0 ? (sbyte)-1 : (sbyte)+1;
            t[120 + s] = new Entry(4, 12, 12, 0, 0, xs, ys);
        }

        // Indices 124..127 — xBits=16, yBits=16. ByteCount=5 (flag + 2 X + 2 Y).
        for (var s = 0; s < 4; s++)
        {
            sbyte xs = (s & 1) == 0 ? (sbyte)-1 : (sbyte)+1;
            sbyte ys = (s & 2) == 0 ? (sbyte)-1 : (sbyte)+1;
            t[124 + s] = new Entry(5, 16, 16, 0, 0, xs, ys);
        }

        return t;
    }
}

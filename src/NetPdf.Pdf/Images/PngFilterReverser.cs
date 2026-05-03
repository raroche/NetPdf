// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Images;

/// <summary>
/// Reverses the per-scanline PNG filter (None / Sub / Up / Average / Paeth) over a
/// decompressed pixel buffer. Used by <see cref="PngImageXObject"/> on the
/// alpha-split path where the decoder has to materialize raw pixels before separating
/// the alpha channel.
/// </summary>
/// <remarks>
/// <para>
/// Spec basis: W3C PNG (Third Edition) §9 (Filtering). All arithmetic is unsigned
/// modulo 256. The Paeth predictor follows §9.4 verbatim.
/// </para>
/// </remarks>
internal static class PngFilterReverser
{
    /// <summary>
    /// Decode <paramref name="filteredScanlines"/> (height × (1 + scanlineByteWidth)
    /// bytes, including the per-scanline filter prefix) into raw pixel bytes
    /// (height × scanlineByteWidth, no filter prefix). Each row is unfiltered using the
    /// previous decoded row as context.
    /// </summary>
    public static byte[] Reverse(ReadOnlySpan<byte> filteredScanlines, int height, int scanlineByteWidth, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scanlineByteWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerPixel);
        var expectedSize = (long)height * (1 + scanlineByteWidth);
        if (filteredScanlines.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"PNG: decompressed image data is {filteredScanlines.Length} bytes; expected {expectedSize} (height × (1 + scanlineByteWidth)).");
        }

        var output = new byte[height * scanlineByteWidth];
        Span<byte> outputSpan = output;

        // We need access to the previous row when reverse-filtering the current row. The
        // first row has no "previous"; treat as all-zero.
        Span<byte> prevRow = scanlineByteWidth <= 4096
            ? stackalloc byte[scanlineByteWidth]
            : new byte[scanlineByteWidth];
        prevRow.Clear();

        var inputCursor = 0;
        for (var y = 0; y < height; y++)
        {
            var filterType = filteredScanlines[inputCursor++];
            var inRow = filteredScanlines.Slice(inputCursor, scanlineByteWidth);
            inputCursor += scanlineByteWidth;
            var outRow = outputSpan.Slice(y * scanlineByteWidth, scanlineByteWidth);

            switch (filterType)
            {
                case 0: // None
                    inRow.CopyTo(outRow);
                    break;
                case 1: // Sub
                    ApplySub(inRow, outRow, bytesPerPixel);
                    break;
                case 2: // Up
                    ApplyUp(inRow, outRow, prevRow);
                    break;
                case 3: // Average
                    ApplyAverage(inRow, outRow, prevRow, bytesPerPixel);
                    break;
                case 4: // Paeth
                    ApplyPaeth(inRow, outRow, prevRow, bytesPerPixel);
                    break;
                default:
                    throw new InvalidDataException(
                        $"PNG: unknown filter type {filterType} on row {y}.");
            }
            outRow.CopyTo(prevRow);
        }
        return output;
    }

    private static void ApplySub(ReadOnlySpan<byte> inRow, Span<byte> outRow, int bpp)
    {
        // Recon(x) = Filt(x) + Recon(a) where a is the byte 'bpp' positions to the left.
        for (var i = 0; i < inRow.Length; i++)
        {
            var left = i >= bpp ? outRow[i - bpp] : (byte)0;
            outRow[i] = (byte)(inRow[i] + left);
        }
    }

    private static void ApplyUp(ReadOnlySpan<byte> inRow, Span<byte> outRow, ReadOnlySpan<byte> prevRow)
    {
        // Recon(x) = Filt(x) + Recon(b) where b is the byte directly above.
        for (var i = 0; i < inRow.Length; i++)
        {
            outRow[i] = (byte)(inRow[i] + prevRow[i]);
        }
    }

    private static void ApplyAverage(ReadOnlySpan<byte> inRow, Span<byte> outRow, ReadOnlySpan<byte> prevRow, int bpp)
    {
        // Recon(x) = Filt(x) + floor((Recon(a) + Recon(b)) / 2).
        for (var i = 0; i < inRow.Length; i++)
        {
            var left = i >= bpp ? outRow[i - bpp] : (byte)0;
            var up = prevRow[i];
            outRow[i] = (byte)(inRow[i] + (left + up) / 2);
        }
    }

    private static void ApplyPaeth(ReadOnlySpan<byte> inRow, Span<byte> outRow, ReadOnlySpan<byte> prevRow, int bpp)
    {
        // Recon(x) = Filt(x) + PaethPredictor(Recon(a), Recon(b), Recon(c)).
        for (var i = 0; i < inRow.Length; i++)
        {
            var left = i >= bpp ? outRow[i - bpp] : (byte)0;
            var up = prevRow[i];
            var upLeft = i >= bpp ? prevRow[i - bpp] : (byte)0;
            outRow[i] = (byte)(inRow[i] + PaethPredictor(left, up, upLeft));
        }
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        // Per §9.4: arithmetic uses signed integers with no overflow.
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType.Cff;

/// <summary>
/// Parsed CFF DICT (Adobe Technical Note #5176 §"4 DICT Data"). DICTs encode font metadata
/// as a stream of operand-then-operator pairs. Operands are integers or reals; operators
/// are 1-byte (0..21) or 2-byte (escape byte 12 followed by a second byte).
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator (<see cref="CffTable"/>) walks Top DICT, Private DICT, and Font DICT
/// (CID-keyed) through this reader. Phase 1 only reads the operators it needs to find
/// downstream pointers (CharStrings offset, charset offset, Private DICT location, ROS for
/// CID detection). Other operators decode but their operands stay as raw <see cref="double"/>
/// values; consumers interpret them as needed.
/// </para>
/// <para>
/// Operator encoding: 1-byte ops occupy 0..21. The escape byte 12 introduces a 2-byte op
/// whose second byte is in 0..38. Storage form: 1-byte ops live as-is; 2-byte ops
/// are packed as <c>(12 &lt;&lt; 8) | secondByte</c>.
/// </para>
/// </remarks>
internal static class CffDict
{
    public const int EscapeByte = 12;

    public const int OpCharset = 15;
    public const int OpEncoding = 16;
    public const int OpCharStrings = 17;
    public const int OpPrivate = 18;
    public const int OpRos = (EscapeByte << 8) | 30;
    public const int OpFdArray = (EscapeByte << 8) | 36;
    public const int OpFdSelect = (EscapeByte << 8) | 37;

    /// <summary>
    /// Parse a DICT into a map from packed operator code → operand stack at the time of
    /// the operator.
    /// </summary>
    public static IReadOnlyDictionary<int, double[]> Parse(ReadOnlySpan<byte> dict)
    {
        var entries = new Dictionary<int, double[]>();
        var operands = new List<double>(8);
        var i = 0;
        while (i < dict.Length)
        {
            var b0 = dict[i];

            if (b0 <= 21)
            {
                int op;
                if (b0 == EscapeByte)
                {
                    if (i + 1 >= dict.Length)
                    {
                        throw new InvalidDataException("CFF DICT: escaped operator missing second byte.");
                    }
                    op = (EscapeByte << 8) | dict[i + 1];
                    i += 2;
                }
                else
                {
                    op = b0;
                    i += 1;
                }
                entries[op] = operands.ToArray();
                operands.Clear();
                continue;
            }

            if (b0 == 28)
            {
                if (i + 2 >= dict.Length)
                {
                    throw new InvalidDataException("CFF DICT: 3-byte integer operand truncated.");
                }
                var value = (short)((dict[i + 1] << 8) | dict[i + 2]);
                operands.Add(value);
                i += 3;
            }
            else if (b0 == 29)
            {
                if (i + 4 >= dict.Length)
                {
                    throw new InvalidDataException("CFF DICT: 5-byte integer operand truncated.");
                }
                var value = (dict[i + 1] << 24) | (dict[i + 2] << 16) | (dict[i + 3] << 8) | dict[i + 4];
                operands.Add(value);
                i += 5;
            }
            else if (b0 == 30)
            {
                operands.Add(ParseReal(dict, ref i));
            }
            else if (b0 is >= 32 and <= 246)
            {
                operands.Add(b0 - 139);
                i += 1;
            }
            else if (b0 is >= 247 and <= 250)
            {
                if (i + 1 >= dict.Length)
                {
                    throw new InvalidDataException("CFF DICT: 2-byte positive integer truncated.");
                }
                operands.Add(((b0 - 247) * 256) + dict[i + 1] + 108);
                i += 2;
            }
            else if (b0 is >= 251 and <= 254)
            {
                if (i + 1 >= dict.Length)
                {
                    throw new InvalidDataException("CFF DICT: 2-byte negative integer truncated.");
                }
                operands.Add(-((b0 - 251) * 256) - dict[i + 1] - 108);
                i += 2;
            }
            else
            {
                throw new InvalidDataException($"CFF DICT: reserved operand byte 0x{b0:X2}.");
            }
        }
        return entries;
    }

    private static double ParseReal(ReadOnlySpan<byte> dict, ref int i)
    {
        // CFF real: byte 30, then a sequence of nibbles encoding digits, '.', 'E', 'E-', '-'.
        // Terminated by nibble 0xF. Two nibbles per byte, high nibble first.
        i += 1; // consume the 0x1E marker
        Span<char> chars = stackalloc char[64];
        var len = 0;
        var done = false;

        while (!done)
        {
            if (i >= dict.Length)
            {
                throw new InvalidDataException("CFF DICT: real operand truncated.");
            }
            var b = dict[i++];
            for (var shift = 4; shift >= 0 && !done; shift -= 4)
            {
                var nibble = (b >> shift) & 0x0F;
                done = AppendNibble(nibble, chars, ref len);
            }
        }

        if (len == 0)
        {
            throw new InvalidDataException("CFF DICT: real operand contains no digits.");
        }

        // Use TryParse so semantically malformed sequences (empty exponent, repeated 'E',
        // etc.) surface as InvalidDataException rather than escaping as FormatException /
        // OverflowException — keeps malformed-font handling uniform across the parser.
        var text = new string(chars[..len]);
        if (!double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var result)
            || !double.IsFinite(result))
        {
            throw new InvalidDataException($"CFF DICT: malformed real operand '{text}'.");
        }
        return result;
    }

    private static bool AppendNibble(int nibble, Span<char> chars, ref int len)
    {
        if (len >= chars.Length)
        {
            throw new InvalidDataException("CFF DICT: real operand exceeds reasonable length.");
        }
        switch (nibble)
        {
            case <= 9:
                chars[len++] = (char)('0' + nibble);
                return false;
            case 10:
                chars[len++] = '.';
                return false;
            case 11:
                chars[len++] = 'E';
                return false;
            case 12:
                chars[len++] = 'E';
                if (len >= chars.Length) { throw new InvalidDataException("CFF DICT: real overflow."); }
                chars[len++] = '-';
                return false;
            case 14:
                chars[len++] = '-';
                return false;
            case 15:
                return true; // terminator
            default:
                throw new InvalidDataException($"CFF DICT: invalid real nibble 0x{nibble:X1}.");
        }
    }
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;

namespace NetPdf.Css.Cascade;

/// <summary>
/// Unit class for a <see cref="CalcValue"/> per CSS Values L4 §10.1. The classifier is
/// intentionally coarse — Task 9's resolver only needs to know "are these two values
/// addable as-is" — fine-grained typing (color components, angles, times, frequencies)
/// lands with the typed-value tree in Task 10+. Conversion within a class (e.g., cm → px,
/// turn → deg) happens before the operation; cross-class arithmetic is a type mismatch
/// and emits <c>CSS-CALC-INVALID-001</c>.
/// </summary>
internal enum CalcUnit : byte
{
    /// <summary>Unitless number. Compatible with all multiplication / division
    /// operands and never an addition operand for a dimensioned value (per L4 §10.1).</summary>
    Number = 0,
    /// <summary>Absolute length, normalized to CSS px (1in = 96px, 1cm = 96/2.54px,
    /// 1pt = 96/72px, etc.). Font-relative units (<c>em</c>, <c>rem</c>, <c>ch</c>,
    /// <c>ex</c>, <c>lh</c>, <c>rlh</c>, <c>cap</c>, <c>ic</c>) are <b>not</b> folded
    /// here — <see cref="CalcResolver"/> defers them by throwing internally so the
    /// outer reducer preserves the source text verbatim. Task 10's typed-value pipeline
    /// finalizes them once font metrics are known. Same goes for viewport- and
    /// container-relative units.</summary>
    Px = 1,
    /// <summary>Percentage. Kept distinct from Px because percentages resolve against
    /// a property-specific reference at LAYOUT time (parent width for margins / padding
    /// / width, font-size for line-height, etc.). v1 preserves any expression with
    /// percentages as opaque text; layout (Phase 3) finalizes.</summary>
    Percent = 2,
    /// <summary>An angle in degrees (turn → 360, rad → 180/π, grad → 0.9). Used by
    /// transforms / gradients; v1 evaluates fully when all operands are angles.</summary>
    Deg = 3,
    /// <summary>A time in milliseconds. Used by transitions / animations.</summary>
    Ms = 4,
    /// <summary>A frequency in Hz.</summary>
    Hz = 5,
    /// <summary>A resolution in dppx (1dpi = 1/96 dppx).</summary>
    Dppx = 6,
}

/// <summary>
/// A typed numeric value produced by <see cref="CalcResolver"/>. Carries the magnitude
/// + a coarse <see cref="CalcUnit"/> classifier. Task 10's typed-value tree consumes
/// these as the leaf nodes for length / angle / time / frequency / resolution properties.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a struct, not a class.</b> Math-function evaluation produces lots of
/// intermediate values; pass-by-value avoids GC pressure on hot paths. 16 bytes
/// (8-byte double + 1-byte enum + padding) is small enough for register-friendly args.
/// </para>
/// <para>
/// <b>Reduction outcome.</b> When <see cref="CalcResolver"/> can fully reduce a math
/// function (no percentages, no viewport-relative units that depend on context),
/// it emits the <see cref="CalcValue"/>'s <see cref="ToCssText"/> form back into the
/// declaration string. Cases that can't reduce (e.g., <c>calc(100% - 16px)</c>) leave
/// the original function text in place for layout (Phase 3) to finalize.
/// </para>
/// </remarks>
internal readonly record struct CalcValue(double Magnitude, CalcUnit Unit)
{
    /// <summary>Render this value as the canonical CSS text (e.g., <c>"16px"</c>,
    /// <c>"50%"</c>, <c>"0.5"</c>). Used to substitute the resolved value back into
    /// the raw declaration text. Numbers serialize without a unit; dimensioned values
    /// serialize with their canonical unit suffix.</summary>
    public string ToCssText()
    {
        var num = FormatNumber(Magnitude);
        return Unit switch
        {
            CalcUnit.Number => num,
            CalcUnit.Px => num + "px",
            CalcUnit.Percent => num + "%",
            CalcUnit.Deg => num + "deg",
            CalcUnit.Ms => num + "ms",
            CalcUnit.Hz => num + "hz",
            CalcUnit.Dppx => num + "dppx",
            _ => num,
        };
    }

    /// <summary>Format the magnitude for serialization. Trims trailing zeros (so
    /// <c>16.0</c> renders as <c>"16"</c>, <c>0.5</c> stays as <c>"0.5"</c>). Uses
    /// invariant culture so the output is locale-independent.</summary>
    private static string FormatNumber(double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n)) return n.ToString(CultureInfo.InvariantCulture);
        // "G" with a high-enough precision preserves doubles cleanly while still
        // suppressing trailing zeros that "F" would emit.
        var s = n.ToString("G15", CultureInfo.InvariantCulture);
        // Replace exponent-form output with fixed (CSS doesn't accept 1e2; use 100).
        if (s.Contains('E') || s.Contains('e'))
        {
            s = n.ToString("0.################", CultureInfo.InvariantCulture);
        }
        return s;
    }

    public override string ToString() => ToCssText();
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Selectors;

/// <summary>
/// CSS Selectors L4 §17 specificity triple <c>(A, B, C)</c>: A counts ID selectors, B counts
/// class / attribute / structural-pseudo selectors, C counts type / pseudo-element selectors.
/// Cascade comparison uses lexicographic ordering — A dominant, then B, then C. The triple
/// is computed by <see cref="SelectorCompiler"/> at compile time and stored on each
/// <see cref="SelectorBytecode"/> so the cascade resolver (Task 7) reads it directly without
/// re-parsing the selector.
/// </summary>
/// <param name="A">ID selector count.</param>
/// <param name="B">Class / attribute / structural-pseudo selector count.</param>
/// <param name="C">Type / pseudo-element selector count.</param>
internal readonly record struct Specificity(int A, int B, int C) : IComparable<Specificity>
{
    public static Specificity Zero => default;

    public static Specificity operator +(Specificity left, Specificity right) =>
        new(left.A + right.A, left.B + right.B, left.C + right.C);

    public int CompareTo(Specificity other)
    {
        var cmp = A.CompareTo(other.A);
        if (cmp != 0) return cmp;
        cmp = B.CompareTo(other.B);
        if (cmp != 0) return cmp;
        return C.CompareTo(other.C);
    }

    public static bool operator <(Specificity left, Specificity right) => left.CompareTo(right) < 0;
    public static bool operator >(Specificity left, Specificity right) => left.CompareTo(right) > 0;
    public static bool operator <=(Specificity left, Specificity right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Specificity left, Specificity right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"({A},{B},{C})";
}

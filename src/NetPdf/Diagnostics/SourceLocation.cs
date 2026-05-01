// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Optional source location attached to a <see cref="Diagnostic"/> — typically the position
/// in the input HTML or a fetched stylesheet where the issue originated.
/// </summary>
public readonly record struct SourceLocation(string? File, int Line, int Column)
{
    /// <summary>The unknown / unavailable location.</summary>
    public static SourceLocation Unknown { get; } = new(null, 0, 0);
}

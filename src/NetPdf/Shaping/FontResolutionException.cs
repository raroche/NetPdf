// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Shaping;

/// <summary>
/// Thrown by <see cref="HarfBuzzShaperResolver"/> when a <c>font-family</c> stack resolves to
/// no usable face, or the resolved bytes are rejected as unsafe / WOFF-wrapped. It is a
/// RECOVERABLE condition: the render pipeline catches it as a backstop (text shaping happens
/// during layout, so the failure surfaces from <c>BlockLayouter.AttemptLayout</c>) and degrades
/// to a valid PDF plus a <c>PAINT-TEXT-FONT-UNRESOLVED-001</c> diagnostic rather than failing
/// the whole conversion.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> for backward compatibility with code
/// (and tests) that caught the bare <see cref="InvalidOperationException"/> the resolver used to
/// throw; the distinct subtype lets the pipeline catch ONLY font-resolution failures without
/// masking other operational errors. The async-resolver failure is a SEPARATE case
/// (<see cref="NotSupportedException"/> from the synchronous-completion guard), already handled
/// gracefully at the inline-layout seam.
/// </remarks>
internal sealed class FontResolutionException : InvalidOperationException
{
    public FontResolutionException(string message) : base(message) { }
}

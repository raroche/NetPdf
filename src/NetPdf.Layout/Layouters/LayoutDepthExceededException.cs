// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Thrown by <see cref="BlockLayouter"/> when the box tree recurses past
/// <c>MaxRecursionDepth</c> — a DoS guard against pathologically deep untrusted HTML that
/// would otherwise <c>StackOverflow</c> and halt the process (the DOM parser's own nesting
/// cap is higher, so a document between the two limits reaches layout). It is a RECOVERABLE
/// condition: the render pipeline catches it as a backstop and degrades to a valid PDF plus a
/// <c>LAYOUT-RECURSION-DEPTH-EXCEEDED-001</c> diagnostic rather than letting an untyped
/// exception escape <c>HtmlPdf.Convert</c>.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> for backward compatibility with code
/// (and tests) that caught the bare <see cref="InvalidOperationException"/> the guard used to
/// throw; the distinct subtype lets the pipeline catch ONLY this DoS-guard trip without masking
/// other operational errors (an NRE / index bug must still surface — the security fuzz harness
/// relies on that distinction). Mirrors <c>FontResolutionException</c>.
/// </remarks>
internal sealed class LayoutDepthExceededException : InvalidOperationException
{
    public LayoutDepthExceededException(string message) : base(message) { }
}

// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Selectors;

/// <summary>
/// Thrown by <see cref="SelectorCompiler.Compile"/> when the input selector text is
/// syntactically invalid. The cascade resolver (Task 7) catches this, emits
/// <c>CSS-PARSE-WARNING-001</c> with the offending selector text, and skips the rule —
/// the rest of the stylesheet still loads. Per CLAUDE.md "Diagnostics, not silent
/// corruption", malformed selectors never crash the pipeline.
/// </summary>
internal sealed class SelectorParseException : Exception
{
    public SelectorParseException(string selectorText, int offset, string reason)
        : base($"Invalid selector \"{selectorText}\" at offset {offset}: {reason}")
    {
        SelectorText = selectorText;
        Offset = offset;
        Reason = reason;
    }

    public string SelectorText { get; }
    public int Offset { get; }
    public string Reason { get; }
}

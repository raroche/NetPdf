// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Thrown when conversion fails or, in strict mode, when an unsupported feature is
/// encountered. Carries the diagnostic code that triggered the failure.
/// </summary>
public sealed class HtmlPdfException : Exception
{
    public string Code { get; }

    public HtmlPdfException(string code, string message) : base(message)
    {
        Code = code;
    }

    public HtmlPdfException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}

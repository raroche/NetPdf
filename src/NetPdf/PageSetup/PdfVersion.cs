// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// The PDF specification version emitted in the file header. Default is
/// <see cref="V1_7"/> for broad viewer compatibility. ISO 32000-2:2020 is the
/// normative reference for our writer; emitting older bytes is a forward-compatible choice.
/// </summary>
public enum PdfVersion
{
    /// <summary>PDF 1.4 (2001). Minimum recommended for transparency.</summary>
    V1_4,

    /// <summary>PDF 1.5 (2003). Adds object streams and xref streams.</summary>
    V1_5,

    /// <summary>PDF 1.6 (2004).</summary>
    V1_6,

    /// <summary>PDF 1.7 (2008, ISO 32000-1). NetPdf's default emission.</summary>
    V1_7,

    /// <summary>PDF 2.0 (ISO 32000-2:2020). Enables xref streams + AES-256.</summary>
    V2_0,
}

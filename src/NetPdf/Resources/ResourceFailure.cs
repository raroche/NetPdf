// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// A resource load that did not produce content. Surfaced via <see cref="PdfRenderResult.ResourceFailures"/>.
/// </summary>
public sealed class ResourceFailure
{
    public required Uri Uri { get; init; }
    public required ResourceKind Kind { get; init; }
    public required string Reason { get; init; }
    public Exception? Exception { get; init; }
}

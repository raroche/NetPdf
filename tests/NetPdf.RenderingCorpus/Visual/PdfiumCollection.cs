// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>xUnit collection that SERIALIZES every test which calls into PDFium (via <c>PDFtoImage</c>) —
/// PDFium's native rendering is not thread-safe, so the visual-harness classes that rasterize must not run
/// concurrently. Non-PDFium classes (e.g. <c>PixelDiff</c> unit tests) stay parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfiumCollection
{
    public const string Name = "PDFium (serialized — native rendering is not thread-safe)";
}

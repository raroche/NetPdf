// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Resolves external resources referenced by HTML/CSS — images, fonts, stylesheets.
/// Implementations may load from disk, embedded assemblies, HTTP, in-memory caches, etc.
/// NetPdf provides no default loader; if a resource is referenced but no loader is set,
/// the resource is skipped and a <c>RES-LOAD-FAILED-001</c> diagnostic is emitted.
/// </summary>
public interface IResourceLoader
{
    /// <summary>
    /// Load the resource at <paramref name="uri"/>.
    /// </summary>
    /// <param name="uri">Absolute URI resolved against <see cref="HtmlPdfOptions.BaseUri"/>.</param>
    /// <param name="kind">What the resource will be used as.</param>
    /// <param name="ct">Cancellation token honored by the loader.</param>
    /// <returns>The bytes plus optional MIME / charset hints.</returns>
    ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct);
}

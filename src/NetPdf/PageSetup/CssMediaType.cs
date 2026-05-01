// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Which CSS media-type stylesheet block applies. Default is <see cref="Print"/> because
/// PDF is paged output. <c>@media print { ... }</c> rules are honored when this is
/// <see cref="Print"/>; <c>@media screen</c> applies when this is <see cref="Screen"/>.
/// </summary>
public enum CssMediaType
{
    Print,
    Screen,
}

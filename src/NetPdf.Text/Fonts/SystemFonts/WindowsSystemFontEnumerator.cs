// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.SystemFonts;

/// <summary>
/// Windows system-font enumerator. Walks <c>%WINDIR%\Fonts</c> (typically
/// <c>C:\Windows\Fonts</c>) and the per-user font directory introduced in Windows 10
/// 1809 (<c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c>) — the latter is where fonts
/// installed without administrator privilege land.
/// </summary>
internal sealed class WindowsSystemFontEnumerator : SystemFontEnumerator
{
    protected override IEnumerable<string> FontDirectories
    {
        get
        {
            var winDir = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(winDir))
            {
                yield return Path.Combine(winDir, "Fonts");
            }
            else
            {
                // Fallback: classic default. Some constrained environments don't set WINDIR.
                yield return @"C:\Windows\Fonts";
            }
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
            }
        }
    }
}

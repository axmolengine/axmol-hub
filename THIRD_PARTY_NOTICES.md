# Third-party notices

Axmol Hub's MIT license covers its own code only. Third-party components retain their respective licenses and notices.

## Included license files

The License dialog displays the following files, also shipped in the application's `licenses/` directory:

| File | Contents |
| --- | --- |
| `PackageLicenses.txt` | Resolved NuGet package names, versions, license mappings, and shared license texts |
| `Anthropic.txt` | Anthropic SDK Apache-2.0 license and attribution |
| `ANGLE.txt` | ANGLE's BSD-3-Clause variant |
| `Velopack.txt` | Velopack's MIT license |
| `PipeWire.txt` | MIT notice for PipeWire/SPA header code used by the Linux portal helper |
| `SkiaSharp-THIRD-PARTY-NOTICES.txt` | Upstream notices bundled with SkiaSharp and HarfBuzzSharp native assets, including Skia, FreeType, ICU, and image codecs |
| `Microsoft-THIRD-PARTY-NOTICES.txt` | Shared upstream notices supplied by Microsoft .NET packages |

Package versions are pinned in `src/AxmolHub/packages.lock.json`. Licenses include Apache-2.0 (Anthropic), MIT, BSD-2-Clause (Markdig), MS-PL (Svg.Custom), and the ANGLE BSD variant; embedded components have additional terms in the upstream notices. `AvaloniaUI.DiagnosticsSupport` is development-only and excluded from Release assets; its package metadata does not specify a license.

Self-contained .NET distributions also retain the runtime's license and third-party notices. Velopack packaging tools are pinned in `installer/packaging-manifest.json`.

## Downloaded and host-provided components

The embedded browser uses `Avalonia.Controls.WebView` (MIT) with host-provided engines: Microsoft Edge WebView2 Evergreen Runtime on Windows, WKWebView on macOS, and WebKitGTK on Linux. Hub does not bundle Chromium/CEF or those system engines; they retain their platform licenses and receive updates outside Hub. See `docs/webview.md` for prerequisites and verification status.

On Linux, `axmolhub-portal-helper` links at build time against GIO/GLib and PipeWire development headers and at run time against the host's PipeWire client library. It does not bundle PipeWire, the XDG desktop portal or a desktop backend. Those host components remain subject to their own licenses.

Axmol engines, development tools, and SDKs are separate from Hub's bundled application dependencies. Downloads are specified in `manifests/`; their original licenses and notices remain with the downloaded components. MSVC, Windows and Android SDKs, OpenJDK, Gradle, and Apple Xcode/SDKs remain subject to their own terms. Hub's MIT license does not relicense them. Games created with Hub must retain the notices required by their engine and dependencies.

## Artwork

Hub's icon is an original redesign based on Axmol's logo motifs. `src/AxmolHub/Assets/hub-icon.svg` is the master for its PNG, ICO, and ICNS variants.

# Photo2Cull.Net

A C# / [Photino.Blazor](https://github.com/tryphotino/photino.Blazor) port of
[Photo2Cull](https://github.com/peterbuitho/Photo2Cull) (the original Rust +
egui desktop app). Same idea -- scan a folder of photos, score sharpness and
overall quality, find duplicate/burst groups, and shortlist the keepers --
rebuilt with a C# backend and an HTML/CSS UI running in a native webview
(WebView2 / WKWebView / WebKitGTK) instead of egui.

**Status: early port, not yet at feature parity with the Rust app.** The
domain logic (sharpness scoring, ranking, dedupe, face detection) is ported
closely from the original source; the UI is a from-scratch rebuild of the
same workflow in Razor components, not a line-by-line translation of the
egui layout code.

## What's ported

- `PhotoMode`, `Metrics`/`Weights`/`Ranking.OverallScore`, `Sharpness`
  (variance-of-Laplacian), `Dedupe` (dHash + union-find grouping),
  `FaceDetector` (the same bundled `version-RFB-320.onnx` model, same prior
  generation and box decoding) -- these track the Rust modules closely and
  carry the same doc comments explaining *why*, not just *what*.
- Scan orchestration (`Scan.RunScanAsync`/`RunRecomputeAsync`), streaming
  results via `System.Threading.Channels` the way the Rust app streams
  `ScanEvent`s over an `mpsc` channel.
- A working Blazor UI: folder scan, sortable photo grid with thumbnails,
  per-photo mode override + recompute, duplicate grouping, and a ranking
  pipeline (technical cutoff -> dedupe -> rank -> shortlist) with live
  weight sliders.

## Known gaps / approximations

- **RAW decoding is Windows x64 and Ubuntu 22.04 x64 only.** The only
  maintained .NET LibRaw binding, [`Sdcb.LibRaw`](https://github.com/sdcb/Sdcb.LibRaw),
  has no macOS native package upstream. `RawPhoto.IsSupported` gates this;
  a native ImageIO/Core Image-based backend for macOS (no LibRaw needed
  there) is the natural follow-up. Standard formats (PNG/JPEG/TIFF/BMP/WebP)
  work everywhere via SkiaSharp.
- **The Laplacian filter's border handling is a standard clamp-to-edge
  convolution**, not verified bit-for-bit against `imageproc::filter::laplacian_filter`'s
  exact edge behavior. Functionally equivalent; absolute values very near
  a frame's edge may differ slightly.
- **The UI doesn't have a native folder picker yet** -- the scan folder is
  a plain text field. Platform-specific pickers (Win32 `IFileDialog`,
  macOS `NSOpenPanel`, GTK on Linux) are a follow-up, not something Photino
  ships out of the box.
- The exact duplicate-grouping Hamming-distance default and technical-
  sharpness cutoff shown in the UI are reasonable starting values, not
  necessarily the exact constants tuned into the original app's UI (the
  ranking *math* itself -- `Metrics`, `Weights`, `Ranking.OverallScore` --
  is ported exactly).

## Why these libraries

See the [Photo2Cull README](https://github.com/peterbuitho/Photo2Cull) and
that project's chat history for the fuller reasoning. Short version:

| Concern | Library | Why |
|---|---|---|
| UI shell | `Photino.Blazor` | Lightweight native webview (no bundled Chromium); UI written in C#/Razor, no JS |
| Standard image decode | `SkiaSharp` | MIT licensed and genuinely cross-platform (SixLabors.ImageSharp 3+ moved to a split commercial license) |
| RAW decode | `Sdcb.LibRaw` | Only actively maintained .NET LibRaw binding; native binaries are LGPL-2.1/CDDL (LibRaw's own license) -- the Rust app avoids this via a pure-Rust decoder, so this is a real license-shape difference from the original |
| Face detection | `Microsoft.ML.OnnxRuntime` | First-party ONNX Runtime bindings; reuses the exact bundled model |

## Build & run

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test                                    # Core.Tests
dotnet run --project src/Photo2CullNet.App
```

## License

The bundled face-detection model (`src/Photo2CullNet.Core/Assets/Models/version-RFB-320.onnx`)
is MIT licensed -- see `NOTICE.md` next to it. No license has been chosen
for the rest of this repo yet.

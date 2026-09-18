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
- **`Photo2CullNet.App.Avalonia`**: a second, parallel UI (same Core,
  same feature set, plain code-behind against Avalonia controls instead
  of Razor) built specifically because Native AOT is confirmed working
  under Avalonia (screenshot-verified) and confirmed *not* working under
  Photino.Blazor 4.0.13 -- see "Release/deployment" below. Functionally
  verified end-to-end with real UI-automation-driven interaction (typed a
  folder path, clicked Scan, watched real photos get scored, sorted, and
  displayed) -- **except one known bug**: some thumbnails render as a
  flat color block instead of the actual photo. A `Dispatcher.UIThread.Invoke`
  fix (forcing thumbnail `Bitmap` construction onto the UI thread rather
  than trusting the calling context) fixed most but not all of them in
  testing; the remainder wasn't root-caused before running out of time in
  the session that built this. Whoever picks this up next: reproduce with
  a real mouse-driven scan (this was found via UI Automation clicks,
  which may not perfectly mirror real input) and check whether it's
  timing/race-related in `MainWindow.AddCard`.

## Known gaps / approximations

- **RAW decoding is Windows x64 and Ubuntu 22.04 x64 only.** The only
  maintained .NET LibRaw binding, [`Sdcb.LibRaw`](https://github.com/sdcb/Sdcb.LibRaw),
  has no macOS native package upstream. `RawPhoto.IsSupported` gates this;
  a native ImageIO/Core Image-based backend for macOS (no LibRaw needed
  there) is the natural follow-up. Standard formats (PNG/JPEG/TIFF/BMP/WebP)
  work everywhere via SkiaSharp.
- **No native folder picker on macOS/Linux beyond `osascript`/`zenity`/`kdialog`.**
  `Native/FolderPicker.cs` uses `SHBrowseForFolder` on Windows and shells
  out to whichever of those is installed elsewhere, falling back to the
  plain text field if none are found.
- The exact duplicate-grouping Hamming-distance default and technical-
  sharpness cutoff shown in the UI are reasonable starting values, not
  necessarily the exact constants tuned into the original app's UI (the
  ranking *math* itself -- `Metrics`, `Weights`, `Ranking.OverallScore` --
  is ported exactly). The sharpness score itself *is* now verified to
  match: see `Sharpness`/`ImageOps.ResizeTriangle`'s doc comments for the
  two bugs (wrong Laplacian kernel, aliasing resize) this took to fix, and
  the ground-truth numbers they were checked against.
- **Native AOT doesn't work with this Photino.Blazor version yet** -- see
  "Release/deployment" below.

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

## Release/deployment

.NET has three ways to ship this that matter here, in increasing order of
"how close to the Rust build's single native `.exe` with zero install step":

1. **Framework-dependent** (`dotnet publish`, default) -- smallest output,
   but needs the .NET runtime already installed on the machine. Not what
   we want to ship.
2. **Self-contained** (`dotnet publish -r win-x64 --self-contained`) --
   bundles the .NET runtime into the output folder. No install step, but
   it's still JIT-compiled IL running on a bundled CoreCLR, not native
   machine code, and it's a folder of ~15-20 files rather than one exe.
   **This is what works today** and is the safe choice to actually ship.
3. **Native AOT** (`dotnet publish -p:PublishAot=true`) -- true native
   machine code, no CoreCLR/JIT at all, closest to the Rust build's
   deployment story. **Investigated, currently broken for this app.**
   Details below.

### Native AOT: what was actually tried

AOT compilation itself succeeds cleanly and produces a genuine
runtime-free binary -- confirmed by inspecting the publish output
directly: a native `Photo2CullNet.App.exe` (~9MB) alongside only *native*
support DLLs (Photino.Native, WebView2Loader, ONNX Runtime, LibRaw's
dependencies, libSkiaSharp) and critically **no** `hostfxr.dll`/`coreclr.dll`
-- there is no .NET runtime in that output at all.

But the published exe doesn't work: it opens its window, starts loading
the page, then either exits silently (code 1, no exception surfaces
anywhere -- not even the app's own top-level `UnhandledException` handler)
or hangs before ever sending Blazor's first render batch, depending on
one environment variable (`DOTNET_EnableWriteXorExecute=0` changes the
failure from a crash to a hang, not to success). This happens even after
applying the exact mitigations from Photino.Blazor's own official
`Samples/Photino.Blazor.NativeAOT` sample (an `.rd.xml` pair retaining the
JS-interop reflection metadata AOT's trimming otherwise strips) -- that
sample targets net8.0, and one of its settings
(`<PublishTrimmed>false</PublishTrimmed>`) is flatly rejected by the .NET
10 SDK's ILCompiler ("PublishTrimmed is implied by native compilation and
cannot be disabled"), and one of its `IlcArg`s (`--nometadatablocking`)
crashes that same newer ILCompiler outright. Whatever's actually broken
survives applying the rest of that sample's fixes, so this looks like a
genuine incompatibility between Photino.Blazor 4.0.13's WebView bridge and
.NET's Native AOT, not a missing trimming directive.

**Also retargeted to net9.0 and re-ran the whole experiment** (Native AOT
has existed since .NET 7; Blazor Hybrid's AOT support has always been the
rough-edged kind, unlike Blazor WebAssembly's own separate, unrelated AOT
story) in case .NET 10's newer ILCompiler was itself the problem, not just
the two settings it happened to reject. Same `--nometadatablocking`
rejection under net9.0's ILCompiler too (so that flag is just gone from
modern ILC generally, nothing to do with .NET 10 specifically) -- dropped
it and published successfully anyway. Result: identical failure mode,
window opens, page starts loading, nothing ever renders. So this isn't a
.NET-10-vs-9 issue; it's Photino.Blazor 4.0.13 itself under Native AOT.

Not pursued further here since self-contained already answers "no install
step" -- AOT would only additionally shrink the deployment and speed up
startup, not change whether a user has to install anything.
If you want to pick this up: start from a Photino.Blazor version bump (a
newer release may have fixed this) before re-attempting the `.rd.xml`
route, and verify with an actual launch every time, not just a clean
`dotnet publish` -- that part was never the problem.

## License

The bundled face-detection model (`src/Photo2CullNet.Core/Assets/Models/version-RFB-320.onnx`)
is MIT licensed -- see `NOTICE.md` next to it. No license has been chosen
for the rest of this repo yet.

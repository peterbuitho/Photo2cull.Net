# Photo2Cull.Net

A C# / [Avalonia](https://avaloniaui.net/) port of
[Photo2Cull](https://github.com/peterbuitho/Photo2Cull) (the original Rust +
egui desktop app). Same idea -- scan a folder of photos, score sharpness and
overall quality, find duplicate/burst groups, and shortlist the keepers --
rebuilt with a C# backend and a native Avalonia UI (code-behind against
Avalonia controls, not egui, and not a webview).

**Status: early port, not yet at feature parity with the Rust app.** The
domain logic (sharpness scoring, ranking, dedupe, face detection) is ported
closely from the original source; the UI is a from-scratch rebuild of the
same workflow, not a line-by-line translation of the egui layout code.

## What's ported

- `PhotoMode`, `Metrics`/`Weights`/`Ranking.OverallScore`, `Sharpness`
  (variance-of-Laplacian), `Dedupe` (dHash + union-find grouping),
  `FaceDetector` (the same bundled `version-RFB-320.onnx` model, same prior
  generation and box decoding) -- these track the Rust modules closely and
  carry the same doc comments explaining *why*, not just *what*.
- Scan orchestration (`Scan.RunScanAsync`/`RunRecomputeAsync`), streaming
  results via `System.Threading.Channels` the way the Rust app streams
  `ScanEvent`s over an `mpsc` channel.
- A working Avalonia UI (`Photo2CullNet.App`, code-behind against
  Avalonia controls): folder scan (native `OpenFolderPickerAsync`, no
  hand-rolled dialog needed), sortable photo grid with thumbnails, per-photo
  mode override + recompute, duplicate grouping, a ranking pipeline
  (technical cutoff -> dedupe -> rank -> shortlist) with live weight
  sliders, and a full-size preview overlay. Built on Avalonia specifically
  because Native AOT is confirmed working under it (screenshot-verified,
  and re-verified from a clean publish -- see "Release/deployment" below).
  Functionally verified end-to-end with real UI-automation-driven
  interaction (typed a folder path, clicked Scan, watched real photos get
  scored, sorted, and displayed) -- **except one known bug**: some
  thumbnails render as a flat color block instead of the actual photo. A
  `Dispatcher.UIThread.Invoke` fix (forcing thumbnail `Bitmap` construction
  onto the UI thread rather than trusting the calling context) fixed most
  but not all of them in testing; the remainder wasn't root-caused before
  running out of time in the session that built this. Whoever picks this
  up next: reproduce with a real mouse-driven scan (this was found via UI
  Automation clicks, which may not perfectly mirror real input) and check
  whether it's timing/race-related in `MainWindow.AddCard`.

## Known gaps / approximations

- **RAW decoding is Windows x64 and Ubuntu 22.04 x64 only.** The only
  maintained .NET LibRaw binding, [`Sdcb.LibRaw`](https://github.com/sdcb/Sdcb.LibRaw),
  has no macOS native package upstream. `RawPhoto.IsSupported` gates this;
  a native ImageIO/Core Image-based backend for macOS (no LibRaw needed
  there) is the natural follow-up. Standard formats (PNG/JPEG/TIFF/BMP/WebP)
  work everywhere via SkiaSharp.
- The exact duplicate-grouping Hamming-distance default and technical-
  sharpness cutoff shown in the UI are reasonable starting values, not
  necessarily the exact constants tuned into the original app's UI (the
  ranking *math* itself -- `Metrics`, `Weights`, `Ranking.OverallScore` --
  is ported exactly). The sharpness score itself *is* now verified to
  match: see `Sharpness`/`ImageOps.ResizeTriangle`'s doc comments for the
  two bugs (wrong Laplacian kernel, aliasing resize) this took to fix, and
  the ground-truth numbers they were checked against.

## Why these libraries

See the [Photo2Cull README](https://github.com/peterbuitho/Photo2Cull) and
that project's chat history for the fuller reasoning. Short version:

| Concern | Library | Why |
|---|---|---|
| UI shell | `Avalonia` | Native, cross-platform desktop UI (no bundled webview/Chromium) that confirmably supports Native AOT, unlike the Photino.Blazor UI this app started with |
| Standard image decode | `SkiaSharp` | MIT licensed and genuinely cross-platform (SixLabors.ImageSharp 3+ moved to a split commercial license) |
| RAW decode | `Sdcb.LibRaw` | Only actively maintained .NET LibRaw binding; native binaries are LGPL-2.1/CDDL (LibRaw's own license) -- the Rust app avoids this via a pure-Rust decoder, so this is a real license-shape difference from the original |
| Face detection | `Microsoft.ML.OnnxRuntime` | First-party ONNX Runtime bindings; reuses the exact bundled model |

## Build & run

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test                                              # Core.Tests
dotnet run --project src/Photo2CullNet.App
```

## Release/deployment

`Photo2CullNet.App` ships with `PublishAot` left on, so
`dotnet publish -c Release -r win-x64 --self-contained true` produces a
genuine native `Photo2CullNet.App.exe` (~22MB) with **no**
`hostfxr.dll`/`coreclr.dll` in the output -- true native machine code, no
CoreCLR/JIT at all, the same "single no-install executable" story as the
Rust build. This was confirmed by an earlier screenshot-verified spike
(the reason Avalonia was chosen over this app's original Photino.Blazor
UI, which hit an unresolved Native AOT incompatibility) and re-verified
since from a clean publish plus an actual launch, not just a successful
build.

`PublishAot`'s native linker step can't fold *native* (non-.NET)
dependencies into that exe -- `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`
(Avalonia's rendering/text-shaping backend), `onnxruntime.dll` (face
detection), `raw_r.dll` (LibRaw, RAW decoding), and ANGLE/image-codec
DLLs (`av_libglesv2.dll`, `jpeg8.dll`, `lcms2.dll`, `zlib1.dll`) all ship
as separate files alongside the exe regardless, the same way the Rust app
would if it linked those dynamically instead of statically. The project
sets `<DebugType>none</DebugType>` for non-Debug builds so at least the
AOT compiler's own ~74MB native debug-symbol `.pdb` isn't generated;
`libSkiaSharp.pdb`/`libHarfBuzzSharp.pdb` (~105MB combined) are still
present because those are bundled by their NuGet packages as content,
not something `DebugType` controls -- excluding them would need an
explicit `ResolvedFileToPublish` removal target, not attempted here.

On a machine where the AOT linker step fails with `'vswhere.exe' is not
recognized as an internal or external command` (and a garbled downstream
`MSB3073` about `link.exe`), the cause is that
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` (where
`vswhere.exe` actually lives) isn't on `PATH` -- the ILCompiler NuGet
targets shell out to it by bare name to locate the VC toolset, and
silently fold its failure output into the linker command it builds.
Add that directory to `PATH` for the build session and retry; no VS
Developer Command Prompt or other environment setup is needed beyond
that one directory. Also, after changing AOT-related project settings
(e.g. `DebugType`), delete `bin`/`obj` before republishing -- the native
link step can otherwise treat its prior output as up to date and skip
relinking, silently keeping stale output.

## License

The bundled face-detection model (`src/Photo2CullNet.Core/Assets/Models/version-RFB-320.onnx`)
is MIT licensed -- see `NOTICE.md` next to it. No license has been chosen
for the rest of this repo yet.

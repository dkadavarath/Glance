# Glance — working notes

Context for Claude Code sessions on this repo. Written 2026-09-21.

## What this is

A WinUI 3 PDF viewer for Windows, hybrid C# + Rust. Forked from `jonas1ara/Glance`
(all history before Sept 2026 is upstream's work).

- **Frontend:** WinUI 3 / .NET 10, `net10.0-windows10.0.26100.0`
- **Backend:** Rust cdylib wrapping PDFium via `pdfium-render`
- **Bridge:** P/Invoke, `src/Interop/GlanceNative.cs`
- **Burn-in:** PDFsharp, `src/Services/PdfExportService.cs`

## Owner's target

A lightweight, native-feeling Windows PDF viewer with a responsive Fluent GUI,
**first-class precision-touchpad and multi-touch support**, and annotations
including stamps, signature images, and notes.

WinUI 3 is Windows-only; cross-platform was considered and dropped. Do not
reintroduce it without asking.

## Build

Rust first — the C# build fails without it.

```powershell
cd native\glance-native
cargo build --release --target x86_64-pc-windows-msvc
cd ..\..
dotnet run --project src\Glance.csproj
```

**Always pass `--target` explicitly**, even for the host architecture. Without it
cargo writes to `target/release/` and the C# build looks in
`target/<triple>/release/`.

Requires: .NET 10 SDK, Rust 1.85+ (Cargo.toml is edition 2024), VS 2022 with
ManagedDesktop + VCTools + Universal workloads and the Win11 26100 SDK, and
Developer Mode enabled.

## Gaps against the target

Findings from a full read of the source (2026-09-20). None of these are
speculative — each is a specific thing the code does or does not do.

**Touch and trackpad — rewritten 2026-09-21, unverified on hardware.** Drawing
state is now `Dictionary<uint, PointerSession>` keyed by `PointerId`
(`MainPage.xaml.cs`), so a second contact no longer corrupts a stroke in flight.
Pen draws and discards in-flight touch strokes (palm rejection); mouse draws on
left button only; a second finger rolls back the tentative stroke and leaves the
event unhandled for the `ScrollViewer`. `e.Handled` is set only when a session
consumed the event, and `PointerCanceled`/`PointerCaptureLost` are handled.
`MainPage.Input.cs` adds cursor-anchored zoom, Alt+wheel horizontal pan and a
space-bar hand tool with inertia. None of the *feel* has been tested — this box
has no digitizer or precision touchpad.

**Ink is hand-rolled.** Strokes are `Polyline` shapes bound to an observable point
collection (`MainPage.xaml:344`), position-only, no pressure or tilt, re-rendered on
the UI thread per pointer move. WinUI's `InkCanvas`/`InkPresenter` would provide
wet-ink on a dedicated thread, pressure, tilt, Bezier fitting, and palm rejection
(`InputDeviceTypes = Pen`) for free. This is the single highest-value swap.

**No stamp or signature-image annotations.** `AnnotationType` is
`{ Highlight, Note, Pen }` (`MainPage.xaml.cs:32`). No image annotation exists.

**Annotations are not real PDF annotations.** `PdfExportService.cs:96-117` paints
highlights and ink into the page content stream via `XGraphics` — flattened and
uneditable elsewhere. Only notes become a true `/Text` annot. No `/Highlight`,
`/Ink`, or `/Stamp`.

**Two PDF engines load every document.** pdfium via Rust for rendering, plus
`Windows.Data.Pdf` for page metadata — and the latter copies the whole file into an
`InMemoryRandomAccessStream` first (`MainPage.xaml.cs:1305-1313`). Rendering also
round-trips PNG: encoded in Rust, decoded in C#, per page. Raw BGRA into a
`WriteableBitmap` would skip both.

**`_ffiLock` serializes all rendering.** A single `SemaphoreSlim(1,1)` in
`PdfRenderService.cs` means no page renders in parallel, thumbnails included.

**`EmptyWorkingSet` on minimize** (`MainWindow.xaml.cs:69`) shrinks the reported
working set without freeing anything. Cosmetic.

**Soundness risk in Rust.** `pdf_engine.rs` uses `std::mem::transmute` to fake a
`'static` lifetime on a self-referential struct. Field order makes drop order
correct today, but it is fragile.

**No tests; CI builds but does not test.** `src/Tests/**` is excluded from
compilation via `<Compile Remove>`; `TestDllLoad` is not in the solution.
`.github/workflows/build.yml` builds and releases but runs nothing. Unit tests
are still blocked until logic is extracted out of `MainPage.xaml.cs`.

## Decisions made

- **Windows-only.** Cross-platform dropped; WinUI 3 cannot deliver it.
- **Unpackaged, shipped as an MSI.** `<WindowsPackageType>None</WindowsPackageType>`,
  installed by `installer/Glance.wxs`. An unsigned MSIX effectively will not install
  whereas an unsigned MSI is ordinary, and it suits LTSC images with no Store. This
  supersedes the `.msixbundle` plan.
- **Storage goes through `Glance.Services.AppData`.** Unpackaged builds have no
  package identity, so `Windows.Storage.ApplicationData.Current` throws. Its
  replacement resolves a path without creating it — hence the directory creation
  in `AppDataService.cs`. Never reintroduce `ApplicationData.Current`.
- **WiX pinned to v5.** v7 is gated behind the Open Source Maintenance Fee EULA
  (`WIX7015`), a licensing decision for the owner, not a build detail.
- **CI is x64 only.** ARM64 was left out rather than adding cross-compilation that
  could not be tested locally. Revisit when ARM64 hardware is available.
- **pdfium pinned to 151.0.7920** for both architectures. Keep them version-matched
  — a skew causes per-arch rendering differences that are very hard to diagnose.
- **Prefer platform APIs over hand-rolled input.** `InkPresenter` and
  `ScrollView`/`InteractionTracker` are already tuned by Microsoft; gesture physics
  should not be discovered empirically.

## Open items

Requested by the owner, in rough priority order:

- Stamp and signature-image annotations, burned into the saved PDF.
- Print.
- Clickable links, and text/image selection and copy. Both need new pdfium FFI
  surface in the Rust layer, not just C# work.
- Split, merge and resize documents.
- `InkPresenter` for strokes — pressure, tilt, wet ink. Touches the persistence
  format and `PdfExportService` as well as the input layer.
- Continue extracting logic from `MainPage.xaml.cs` so unit tests become possible.
  `MainPage.Input.cs` and `MainPage.ViewModes.cs` are a start.

## Testing constraints

Multi-touch **correctness** can be covered with `InputInjector` synthesis. Gesture
**feel** — latency, inertia, palm rejection — needs real hardware with a precision
touchpad and a 10-point digitizer. VMs expose neither. Mica also does not composite
over RDP, and a GPU-less VM falls back to WARP software rendering, so performance
numbers from one are meaningless.

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

**Touch and trackpad — effectively absent.** No `ManipulationDelta`, no
`PointerDeviceType` checks, no gesture recognizer anywhere. Pinch/pan comes free
from `ScrollViewer ZoomMode="Enabled"` (`MainPage.xaml:283`) and nothing more.
Drawing state is a single `bool _isDrawingHighlight` plus one `_activeHighlight`
field (`MainPage.xaml.cs:1948`) — structurally single-pointer, so a second contact
corrupts the first stroke. Handlers also set `e.Handled = true` unconditionally in
edit modes, which suppresses the built-in pan the ScrollViewer would otherwise give.

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

**No tests, no CI.** `src/Tests/**` is excluded from compilation via
`<Compile Remove>`; `TestDllLoad` is not in the solution. `.github/` holds only
`funding.yml`. Unit tests are blocked until logic is extracted out of the
2,990-line `MainPage.xaml.cs`.

## Decisions made

- **Windows-only.** Cross-platform dropped; WinUI 3 cannot deliver it.
- **x64 and ARM64, no x86.** One codebase, per-RID native assets, ship as a
  single `.msixbundle`.
- **pdfium pinned to 151.0.7920** for both architectures. Keep them version-matched
  — a skew causes per-arch rendering differences that are very hard to diagnose.
- **Prefer platform APIs over hand-rolled input.** `InkPresenter` and
  `ScrollView`/`InteractionTracker` are already tuned by Microsoft; gesture physics
  should not be discovered empirically.

## Open items

- `<WindowsPackageType>None</WindowsPackageType>` for local iteration — proposed,
  not yet applied. Unpackaged launch is faster and avoids MSIX/Store dependencies,
  which matters on LTSC images with no Microsoft Store. Owner wanted to approve first.
- Input-layer rewrite: `InkPresenter` for strokes, `PointerId`-keyed state for
  multi-contact, device-type routing, drop the blanket `e.Handled`.
- Extract logic from `MainPage.xaml.cs` so unit tests become possible.
- Stamp and signature-image annotation types.

## Testing constraints

Multi-touch **correctness** can be covered with `InputInjector` synthesis. Gesture
**feel** — latency, inertia, palm rejection — needs real hardware with a precision
touchpad and a 10-point digitizer. VMs expose neither. Mica also does not composite
over RDP, and a GPU-less VM falls back to WARP software rendering, so performance
numbers from one are meaningless.

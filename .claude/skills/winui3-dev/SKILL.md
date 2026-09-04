---
name: winui3-dev
description: >-
  Leaf coding conventions for WinUI 3 / Windows App SDK 2.x on .NET 10 with Native AOT and trimming:
  x:Bind-only binding, partial classes for CsWinRT, no reflection or dynamic, source-generated JSON,
  DispatcherQueue threading, AppWindow/title bar, ThemeResource theming, DPI (RasterizationScale) handling,
  tile rendering rules. Use whenever creating or editing .xaml, .xaml.cs, ViewModel, App.xaml.cs, Viewer/*
  or .csproj files, adding controls, bindings, resources, or fixing XAML compile errors, IL2xxx/IL3xxx
  trim/AOT warnings, CsWinRT or WinRT interop issues in this repo.
user-invocable: false
paths:
  - "src/Leaf/**/*.xaml"
  - "src/Leaf/**/*.cs"
  - "**/*.csproj"
  - "**/Directory.Build.props"
metadata:
  owner: leaf
---

# WinUI 3 conventions for Leaf

Leaf is an UNPACKAGED, self-contained WinUI 3 app published with Native AOT. Ignore MSIX / `winapp run` /
`Package.appxmanifest` advice from other skills; build with the commands in the `leaf-build` skill.

## Project shape (do not change without a DECISIONS.md entry)
- `src/Leaf/Leaf.csproj`: `net10.0-windows10.0.26100.0`, `Platforms=x64`, `RuntimeIdentifier=win-x64`, `UseWinUI`,
  `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `SelfContained=true`, `PublishAot=true`,
  `CsWinRTAotOptimizerEnabled=true`, `DISABLE_XAML_GENERATED_MAIN` (custom `Program.Main` does single-instance redirect).
- Only `Microsoft.WindowsAppSDK.WinUI` is referenced (never the meta-package). Versions live in `Directory.Packages.props`.
- Warnings are errors, including IL2xxx/IL3xxx. Fix the root cause; never blanket-suppress.

## AOT / trimming rules
- Binding: `{x:Bind}` only, `Mode=OneWay` for changing values, `OneTime` is the default. Never `{Binding}`, never `DataContext`-based binding.
- Any class used from XAML (Window, Page, UserControl, custom Panel, converters, x:Bind sources, ObservableCollection item types) must be `partial`.
- INotifyPropertyChanged is hand-written (small) or via `CommunityToolkit.Mvvm` source generators if adopted; no reflection-based frameworks.
- JSON: `System.Text.Json` with `[JsonSerializable]` contexts only. No `Newtonsoft`, no `JsonSerializer.Serialize<T>` without a context.
- Native interop: `[LibraryImport]` + `[UnmanagedCallersOnly]` function pointers; COM via `[GeneratedComInterface]`; never `ComImport`, `Marshal.GetDelegateForFunctionPointer`, or `DllImport` with non-blittable types.
- No `dynamic`, no `Reflection.Emit`, no `Activator.CreateInstance(string)`, no `Type.GetType(string)`.
- Collection expressions and `Span<T>` are fine inside the app; do not pass collection expressions across the WinRT ABI.

## Threading
- UI objects (DependencyObject, WriteableBitmap, SoftwareBitmapSource) are created and touched only on the UI thread.
- Background results come back through `DispatcherQueue.TryEnqueue`. Never `.Result`/`.Wait()` on the UI thread.
- `SoftwareBitmap` is agile: build it on the pdfium thread, hand it to the UI thread, `SoftwareBitmapSource.SetBitmapAsync` there.
- pdfium calls happen only on `PdfiumThread` (see the `pdfium-api` skill).

## Rendering & DPI
- Render scale = `zoom * XamlRoot.RasterizationScale`. Tiles are device pixels; the tile `Image` gets `Width = px / RasterizationScale`,
  `Stretch=Fill`, and lives in a canvas with `UseLayoutRounding="False"` so 1024 px lands on exact device pixels at 125 %.
- Subscribe to `XamlRoot.Changed` and bump the render generation when `RasterizationScale` changes.
- Never allocate whole-page bitmaps at high zoom; go through `TileCache` (byte budget, LRU, pinned visible set).
- Zoom: apply a `ScaleTransform` preview during the gesture, commit after a 150 ms `DispatcherQueueTimer` with relayout + `ChangeView(disableAnimation:true)`.
- Overlays (search hits, selection) are one `Path` with a `GeometryGroup` per page, `IsHitTestVisible=False`.

## Window / shell
- `ExtendsContentIntoTitleBar=true`, `SetTitleBar(DragRegion)`, `MicaBackdrop`, `AppWindow.SetIcon(...)`.
- Keyboard: `KeyboardAccelerator` on the root for Ctrl+O/W/F/G/0/+/-; main-row `+`/`-` are `(VirtualKey)0xBB`/`0xBD` added in code.
- Ctrl+wheel: `AddHandler(UIElement.PointerWheelChangedEvent, handler, handledEventsToo: true)` on the ScrollViewer.
- Pickers need `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)`.
- Theme: follow the system; page paper is always white; use `{ThemeResource}` brushes elsewhere.

## Error handling
- Engine failures surface as `PdfException`; the UI shows an `InfoBar`/`ContentDialog`. A bad PDF must never crash the process.
- `App.UnhandledException` logs and marks handled where recovery is possible.

## When a XAML compile fails
- Check: element name vs `x:Name` typo, missing `partial`, event handler signature, `xmlns:local` namespace, unsupported property in WinUI 3 (not UWP/WPF).
- Read the generated files under `obj/x64/Debug/net10.0-windows10.0.26100.0/win-x64/` (`*.g.cs`, `XamlTypeInfo.g.cs`) when a type is "not found".

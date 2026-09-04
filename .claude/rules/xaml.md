---
paths:
  - "**/*.xaml"
  - "**/*.xaml.cs"
---
# XAML rules for Leaf
- Bind with `{x:Bind}` only; use `Mode=OneWay` for values that change; never `{Binding}` (reflection, trimmed under AOT).
- Every class referenced from XAML (pages, windows, user controls, custom panels, view models, x:Bind sources) is `partial`.
- Colors and brushes come from `{ThemeResource}` so light and dark themes work; page paper stays white in both themes.
- Prefer x:Bind function bindings over IValueConverter; never `Converter={x:Null}`.
- Keep the visual tree small: no per-hit Rectangles; overlays are a single Path with a GeometryGroup.
- Heavy work never runs on the UI thread; marshal results back with `DispatcherQueue.TryEnqueue`.

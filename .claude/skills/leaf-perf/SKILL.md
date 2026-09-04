---
name: leaf-perf
description: >-
  Measure and enforce Leaf performance budgets: cold/warm startup time to first painted page, working set and private
  bytes at idle and after scrolling, publish and installer size, and an Adobe Acrobat comparison. Use when the user asks
  about startup, speed, memory, footprint, RAM, latency, jank, profiling or benchmarking, and before merging changes to
  App.xaml.cs, Program.cs, Viewer/*, TileCache, RenderScheduler or Leaf.Pdfium rendering code.
argument-hint: "[startup|memory|size|acrobat|all] [file.pdf]"
allowed-tools: Bash(pwsh -File scripts/measure-perf.ps1 *) Bash(powershell -File scripts/measure-perf.ps1 *) Bash(pwsh -NoProfile -File scripts/measure-perf.ps1 *)
metadata:
  owner: leaf
---

# Measuring Leaf

Always measure the **Release AOT publish** (`artifacts/publish/Leaf.exe`), never a Debug build.

```
pwsh -NoProfile -File scripts/measure-perf.ps1 -Pdf "C:\path\to\sample.pdf" -Runs 5
pwsh -NoProfile -File scripts/measure-perf.ps1 -Pdf "..." -Acrobat      # also samples Acrobat on the same file
pwsh -NoProfile -File scripts/measure-perf.ps1 -SizeOnly                 # publish folder + installer size
```

What the script reports per run: window-up time (ms), first-frame time from the app's own stamp (`%LOCALAPPDATA%\Leaf\perf.log`,
written when `LEAF_PERF=1`), working set, private working set (Task Manager "Memory" column), private bytes (commit), peak working set.
Run 1 after a reboot is the cold number; later runs are warm.

## Budgets (Release AOT, this laptop: i7-11800H, 1920x1200 @125 %)
| Metric | Budget |
|---|---|
| Warm time to window | <= 500 ms |
| Warm time to first painted page (text PDF) | <= 1000 ms |
| Private working set, one document idle | <= 100 MB |
| Working set after scrolling a 60-page PDF | <= 200 MB |
| Publish folder (without PDB) | <= 80 MB |
| Installer | <= 40 MB |

Record results in `docs/PERF.md` (date, commit, numbers) whenever a budget-relevant area changes. If a number regresses,
say so plainly in the final message with the before/after table.

## Deeper profiling
- `wpr -start GeneralProfile -start XAMLActivity -filemode`, launch, `wpr -stop artifacts\startup.etl` (needs WPA to analyze).
- `dotnet-counters monitor -n Leaf` / `dotnet-trace collect -n Leaf` work only on non-AOT builds.

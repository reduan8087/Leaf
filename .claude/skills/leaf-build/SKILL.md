---
name: leaf-build
description: >-
  Build, run, test, publish and clean the Leaf solution (unpackaged WinUI 3, net10.0-windows10.0.26100.0, x64,
  Native AOT publish). Use whenever you need to compile, run the app on a PDF, run unit tests, produce the Release/AOT
  publish folder, restore packages, or diagnose build/restore/XAML-compiler/MSBuild/ILC errors. Do not use winapp run,
  Package.appxmanifest or MSIX flows for this repo; use the commands in this skill.
argument-hint: "[build|run|test|publish|clean] [Debug|Release] [file.pdf]"
allowed-tools: Bash(dotnet restore *) Bash(dotnet build *) Bash(dotnet run *) Bash(dotnet test *) Bash(dotnet publish *) Bash(dotnet clean *) Bash(dotnet --info) Bash(dotnet --list-sdks)
metadata:
  owner: leaf
---

# Building Leaf

Run from the repo root (`C:\MY PROJECT 2026 - Claude\PDF Reader`). Paths contain spaces: quote them.

| Task | Command |
|---|---|
| Restore | `dotnet restore Leaf.slnx -p:Platform=x64` |
| Build (Debug) | `dotnet build Leaf.slnx -c Debug -p:Platform=x64 -nologo -v:m` |
| Build (Release, no AOT compile) | `dotnet build Leaf.slnx -c Release -p:Platform=x64 -nologo -v:m` |
| Run | `dotnet run --project src/Leaf -c Debug -p:Platform=x64 --no-build -- "C:\path\to\file.pdf"` |
| Run the built exe directly | `src\Leaf\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Leaf.exe "file.pdf"` |
| Tests | `dotnet test tests/Leaf.Pdfium.Tests -c Release -nologo` |
| Corpus smoke test (opt-in) | `$env:LEAF_CORPUS_DIR="C:\Users\e_w_a\Documents"; dotnet test tests/Leaf.Pdfium.Tests -c Release --filter Category=Corpus` |
| Publish (Native AOT) | `dotnet publish src/Leaf -c Release -r win-x64 -o artifacts/publish -nologo` (2-5 min; needs VS C++ tools, present) |
| Clean | `dotnet clean Leaf.slnx -p:Platform=x64` then delete `src/*/obj` if XAML codegen is stale |

Notes
- `-p:Platform=x64` is required: the app project declares `Platforms=x64` and pdfium ships only per-RID natives.
- `PublishAot=true` is always on; `dotnet build` does not run ILC, only analyzers. `dotnet publish` runs ILC and takes minutes.
- Output of publish: `artifacts/publish/Leaf.exe` + `pdfium.dll` + WinUI/WinAppSDK natives + `Assets/`. No single-file.
- Logs: pass `-bl:artifacts/build.binlog` to capture a binary log for hard failures.

Common failures
- `error MSB4057`/`NETSDK1082` about RID: add `-p:Platform=x64` or `-r win-x64`.
- XAML `WMC` errors: see the `winui3-dev` skill (partial classes, x:Bind, generated files under `obj`).
- `IL2xxx`/`IL3xxx`: trim/AOT analyzer; fix the code (no reflection), never suppress silently.
- pdfium `DllNotFoundException`: `pdfium.dll` must sit next to the exe; check `runtimes/win-x64/native` flattening and the RID.
- `WindowsAppSDK` version conflicts: all versions are pinned in `Directory.Packages.props`; do not add package versions in csproj files.

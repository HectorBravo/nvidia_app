# nvidia_app

![Screenshot](screenshot.png)

![Screenshot 2](screenshot2.png)

A **Windows-native** application (C# / .NET 8) that reads NVIDIA GPUs by talking **directly**
to the NVIDIA Management Library (`nvml.dll`) through P/Invoke — it does **not** shell out to
`nvidia-smi`. There are two front-ends over the same NVML engine:

- **`NvidiaMonitor`** — a **WPF** live dashboard (Overview grid + synchronized Charts, `~1 Hz`
  polling) with built-in **self-update** from GitHub Releases.
- **`NvmlTest`** — a headless console CLI that prints an `nvidia-smi`-style table once
  (`--print`) or as a live feed (`--watch`).

## Features
### Telemetry (both front-ends)
- Live per-GPU telemetry read straight from NVML: utilization (GPU + memory), **SM
  utilization**, temperature, power draw and limits, memory (used/total/free), clocks, fan
  speed, perf state (P-state), **PCIe link (gen × width) + derived theoretical bandwidth
  (GB/s)**, ECC mode/errors, NVLink count, and current throttle reasons.
- Per-GPU **running-process** enumeration (compute + graphics) with PID, kind and per-process
  memory.
- **System-wide physical RAM** read from the OS (`kernel32!GlobalMemoryStatusEx`, not NVML) —
  feeds the dashboard's System RAM chart.
- Values NVML cannot report for a device render as **N/A**, so one missing metric never
  aborts the whole read.

### WPF dashboard (`NvidiaMonitor`)
- Two tabs: an all-GPU **Overview** grid (index, name, GPU/mem utilization, temp, power,
  memory used/total, memory clock, fan, P-state) and a **Charts** tab — which is the tab the
  app opens on.
- **10 live charts**, drawn with native WPF shapes (no charting library): **System RAM (%)**,
  **VRAM Memory Used (%)**, **GPU Utilization (%)**, VRAM Memory Utilization (%), Temperature,
  SM Utilization, Power, PCIe Bandwidth (log scale), GPU Clock, Memory Clock. All share one
  timeline and one crosshair; `Ctrl`+wheel zooms the window (2–60 min, default 10); history is
  a 720-sample ring buffer (~12 min @ `1 Hz`).
- **Collapsible cards**: every chart has a top-left chevron that folds it down to its header.
  Only **System RAM**, **VRAM Memory Used** and **GPU Utilization** start expanded, which keeps
  the window short.
- Window opens at a **calculated fit-to-content size** (never maximized), dark theme with the
  NVIDIA-green accent and green "slide" scrollbars, NVIDIA logo app/window icon, and the
  **build stamp** under the title.
- **Header device list**: top-right of the header, one row per GPU with a colour swatch matching
  that GPU's chart series, its `cuda:N` ordinal, card name and max memory.
- **Live polling**: a background thread reads NVML ~once a second and republishes snapshots to
  the UI thread (never blocking it); a status-change event refreshes the view if a GPU is
  plugged/unplugged or the driver comes and goes.
- **Self-update**: checks GitHub Releases at startup and every hour; when a newer build exists,
  a flashing **"New version available: `gui/<tag>` (click to update)"** link appears next to the
  build stamp — one click downloads the release zip, swaps the files and relaunches the app.
  Checks are authenticated (`GITHUB_TOKEN`/`GH_TOKEN`, else `gh auth token`) when available, so
  they are not throttled by the anonymous 60 req/h limit.
- **Single-instance guard**: a second concurrent dashboard launch prints a refusal and exits
  (headless `--print` is unaffected).

### Headless CLI (`NvmlTest`)
- `--print` — one `nvidia-smi`-style table (including an **SM** column and a **PCIe** column
  rendered as `gen × width / bandwidth GB/s`, e.g. `4.0 x16 / 32 GB/s`), then exit.
- `--watch [<seconds>]` — the same table as a **live feed**, re-rendered every `<seconds>` like
  `watch nvidia-smi`. The value is optional (**bare `--watch` = every 1 s**), implies `--print`,
  and `Ctrl-C` stops it cleanly (exit `0`). An explicit non-numeric value exits `2`.
- `--probe` — a one-shot NVML diagnostic: NVML version plus both per-process SM-utilization
  APIs (legacy and batched) with their raw return codes. Useful when a metric looks wrong.
- Both print a `Build: yyyy-MM-dd HH:mm` stamp first, so a binary can be matched to a release by
  eye.
- `NvidiaMonitor --print` runs the same read through the WPF entry point (no window is opened),
  but instead of the full table it prints a **compact one-line summary per GPU** (a
  `[NvidiaMonitor] driver=… cuda=… nvml=… gpus=N` header line, then one line per GPU:
  `GPU[N] <name> util=% sm=% mem=% temp=°C power=W mem=used/total MB fan=% perf=Pn`).

### Graceful fallback & exit codes
When no GPU / driver / `nvml.dll` is present, the UI shows a friendly explanation and the CLI
prints the reason instead of crashing.

| Exit code | Meaning |
|-----------|---------|
| `0` | success (also a clean `Ctrl-C` out of `--watch`) |
| `1` | NVML or a GPU is unavailable — the reason is on stdout |
| `2` | missing / bad arguments — usage on stderr |

### Available in the shared `Contracts` project (ready to surface)
- `Exporter.ToCsv` / `Exporter.ToXml`: serialize a snapshot to nvidia-smi-style **CSV** or
  **XML**. (Wired to the engine; a one-click export button in the UI is a planned enhancement.)

### Planned (next iteration)
- One-click **CSV / XML export** in the WPF UI (the exporter already exists).
- **Process-name resolution** for the per-GPU process list (currently PID / kind / memory).

## Solution layout
| Project | Role |
|---------|------|
| `src/HectorBravo.Nvidia.Contracts` | Shared data + service contracts (the only surface both sides depend on) |
| `src/NvmlCore` | NVML P/Invoke engine + polling service (implements the contracts) |
| `src/NvidiaMonitor` | WPF application (the dashboard) |
| `src/NvmlTest` | Headless console CLI used to verify NVML without a window |

`src/NvmlCore/ref/nvml.h` is the official NVML header kept as the authoritative reference for
signatures, structs and enums.

## Build
```powershell
# .NET 8 SDK required — build the whole solution
dotnet build nvidia_app.sln -c Release

# …or use the helper script (same build, output logged to %TEMP%\full_build.log)
powershell -File build_release.ps1
```

### Produce the runnable executables
The ready-to-run binaries live in the repo-local, **git-ignored** `bin/` folder and are created
with `dotnet publish` (framework-dependent — they need the .NET 8 *runtime*, not a full SDK):

```powershell
dotnet publish src/NvmlTest      -c Release -o bin/NvmlTest      # -> bin\NvmlTest\NvmlTest.exe
dotnet publish src/NvidiaMonitor -c Release -o bin/NvidiaMonitor # -> bin\NvidiaMonitor\NvidiaMonitor.exe
```

### Release policy (compile-date GitHub Releases)
Each app prints its own **compile date** (`LastWriteTime` of its executable —
the GUI shows it under the title, the CLI prints a `Build: …` line). After every
merge into `main`, run:

```powershell
.\release.ps1 --merge <pre-merge-commit> <post-merge-commit>
```

It publishes each app and creates a GitHub Release named `gui/<yyyyMMdd_HHmm>`
(NvidiaMonitor) / `cli/<yyyyMMdd_HHmm>` (NvmlTest), attaching a zip of that
app's published output (exe + DLLs + runtime manifests, no `.pdb`). Only apps
whose code actually changed in the merge get a new release; unchanged apps are
skipped. The script is idempotent — an existing tag is skipped. Close a running
dashboard first (it locks its own DLLs).

The dashboard's **self-update** reads exactly those `gui/…` tags, so the tag, the
zip name and the compile stamp baked into the binaries always agree.

Every release body also carries a **`## What's changed` changelog**, generated
automatically from the conventional commit messages that touched each app's
dependency closure over the merge range (docs/`ai/` commits are excluded). To
(re)patch the notes of the newest existing release without rebuilding, run:

```powershell
.\release.ps1 --changelog <base> [<head>] [gui|cli]   # head defaults to HEAD
```

## Run
Run the published executables directly — no `dotnet run` needed (build them first with
`dotnet publish`, above):

```powershell
# WPF dashboard (live, ~1 Hz)
.\bin\NvidiaMonitor\NvidiaMonitor.exe

# Headless one-shot nvidia-smi-style table (no window)
.\bin\NvmlTest\NvmlTest.exe --print

# Live feed — re-prints the table every 1 s until Ctrl-C (bare --watch = 1 s)
.\bin\NvmlTest\NvmlTest.exe --watch

# Live feed at another interval (seconds; fractional values are accepted)
.\bin\NvmlTest\NvmlTest.exe --watch 5

# One-shot NVML / SM-utilization diagnostic
.\bin\NvmlTest\NvmlTest.exe --probe

# Same, through the WPF entry point (no window) — one-line summary per GPU
.\bin\NvidiaMonitor\NvidiaMonitor.exe --print
```

> The WPF dashboard polls in the background; closing the window stops polling and releases
> NVML. The `--print` paths never open a window, and only one dashboard instance may run at a
> time. `--watch` repaints in place (`Ctrl-C` to stop); redirecting its output to a file simply
> appends every frame instead of clearing the screen.

<details>
<summary><b>Prefer to run straight from source (without publishing)?</b></summary>

```powershell
dotnet run --project src/NvidiaMonitor -c Release                    # WPF dashboard
dotnet run --project src/NvidiaMonitor -c Release -- --print         # headless (WPF entry point)
dotnet run --project src/NvmlTest      -c Release -- --print         # headless (CLI, one shot)
dotnet run --project src/NvmlTest      -c Release -- --watch 5       # headless (CLI, live feed)
```

</details>

## Requirements
- Windows with an NVIDIA GPU and a driver that ships `nvml.dll`
  (typically under `%CommonProgramFiles(x86)%\NVIDIA Corporation\NVSMI\`).
- To **run** the published `bin/` executables: the .NET 8 runtime — the .NET 8 **Desktop**
  runtime for the WPF `NvidiaMonitor`, or just the base .NET 8 runtime for the console
  `NvmlTest`.
- To **build / publish** from source: the .NET 8 SDK.

## Notes
- **No third-party NuGet dependencies** — the only reference is the OS `nvml.dll` (loaded at
  runtime via P/Invoke). The WPF UI and every chart are built from the native
  `System.Windows.Controls` / `System.Windows.Shapes` stack only.
- Values NVML cannot report for a device are rendered as **N/A** so a single missing metric
  never aborts the whole read.
- **VRAM Memory Used (%)** and **VRAM Memory Utilization (%)** measure different things and
  rarely agree: *Used* is how much of the card's framebuffer is allocated/reserved (sticky, drops
  only when buffers are freed); *Utilization* is the driver-sampled fraction of recent time the
  memory bus was actually transferring data. A resident-but-idle model shows high Used, low
  Utilization.
- **SM utilization** has no device-level NVML call: it is the sum of the per-process `smUtil`
  samples (the same source as `nvidia-smi dmon`'s `sm` column), clamped to 100 %.
- NVML is loaded from the driver's `nvml.dll` (typically
  `%CommonProgramFiles(x86)%\NVIDIA Corporation\NVSMI`, falling back to `System32`). If the
  library or a GPU is unavailable, the app degrades to a friendly message rather than crashing.
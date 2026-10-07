# nvidia_app

A **Windows-native** application (C# / .NET 8) that reads NVIDIA GPUs by talking **directly**
to the NVIDIA Management Library (`nvml.dll`) through P/Invoke — it does **not** shell out to
`nvidia-smi`. There are two front-ends over the same NVML engine:

- **`NvmlTest`** — a headless console CLI that prints a one-shot `nvidia-smi`-style table.
- **`NvidiaMonitor`** — a **WPF** live dashboard (all-GPU overview grid, `~1 Hz` polling).

## Features (v1)
- Live per-GPU telemetry read straight from NVML: utilization (GPU + memory), temperature,
  power draw and limits, memory (used/total/free), clocks, fan speed, perf state (P-state),
  ECC mode/errors, NVLink count, and current throttle reasons.
- Per-GPU **running-process** enumeration (compute + graphics) with PID, kind and per-process
  memory.
- **WPF dashboard**: a single all-GPU overview grid (index, name, GPU/mem utilization, temp,
  power, memory used/total, memory clock, fan, P-state) with a header showing the
  driver / CUDA / NVML versions and a status line; dark theme with the NVIDIA-green accent.
- **Live polling**: a background thread reads NVML ~once a second and republishes snapshots to
  the UI thread (never blocking the UI); a status-change event refreshes the view if a GPU is
  plugged/unplugged or the driver comes and goes.
- **Headless CLI**: `NvmlTest --print` prints one `nvidia-smi`-style table and exits `0` on
  success (`1` when NVML / a GPU is unavailable, with the reason on stdout).
  `NvidiaMonitor --print` does the same through the WPF entry point (no window is opened), but
  instead of the full table it prints a **compact one-line summary per GPU** (a
  `[NvidiaMonitor] driver=… cuda=… nvml=… gpus=N` header line, then one line per GPU:
  `util=% mem=% temp=°C power=W mem=used/total MB fan=% perf=Pn`).
- **Graceful fallback**: when no GPU / driver / `nvml.dll` is present, the UI shows a friendly
  explanation and the CLI prints the reason and a non-zero exit code.

### Available in the shared `Contracts` project (ready to surface)
- `Exporter.ToCsv` / `Exporter.ToXml`: serialize a snapshot to nvidia-smi-style **CSV** or
  **XML**. (Wired to the engine; a one-click export button in the UI is a planned enhancement.)

### Planned (next iteration)
- Per-GPU detail tabs with live **charts** (utilization / memory / power / temperature / clock)
  driven by `GpuHistory` (the ring-buffer contract is already in `Contracts`).
- A **Processes** tab and an **activity log** tab in the UI (the engine already emits both).
- **Process-name resolution** for the per-GPU process list (currently PID / kind / memory).
- `--print --watch <seconds>` live feed and one-click **CSV / XML export** in the WPF UI.

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

## Run
Run the published executables directly — no `dotnet run` needed (build them first with
`dotnet publish`, above):

```powershell
# WPF dashboard (live, ~1 Hz)
.\bin\NvidiaMonitor\NvidiaMonitor.exe

# Headless one-shot nvidia-smi-style table (no window)
.\bin\NvmlTest\NvmlTest.exe --print

# Same, through the WPF entry point (no window) — one-line summary per GPU
.\bin\NvidiaMonitor\NvidiaMonitor.exe --print
```

> The WPF dashboard polls in the background; closing the window stops polling and releases
> NVML. The `--print` paths never open a window.

<details>
<summary><b>Prefer to run straight from source (without publishing)?</b></summary>

```powershell
dotnet run --project src/NvidiaMonitor -c Release                 # WPF dashboard
dotnet run --project src/NvidiaMonitor -c Release -- --print      # headless (WPF entry point)
dotnet run --project src/NvmlTest      -c Release -- --print      # headless (CLI)
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
  runtime via P/Invoke). The WPF UI is built from the native `System.Windows.Controls` /
  `System.Windows.Shapes` stack only.
- Values NVML cannot report for a device are rendered as **N/A** so a single missing metric
  never aborts the whole read.
- NVML is loaded from the driver's `nvml.dll` (typically
  `%CommonProgramFiles(x86)%\NVIDIA Corporation\NVSMI`, falling back to `System32`). If the
  library or a GPU is unavailable, the app degrades to a friendly message rather than crashing.
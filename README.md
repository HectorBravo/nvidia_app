# nvidia_app

A **Windows-native** WPF application (C# / .NET 8) that replicates `nvidia-smi` by talking
**directly** to the NVIDIA Management Library (`nvml.dll`) through P/Invoke — it does **not**
shell out to `nvidia-smi`.

## Features
- Live per-GPU telemetry: utilization, temperature, power, memory, clocks, fan, perf state,
  ECC, NVLink and throttle reasons — all read from NVML.
- Per-GPU running process list (compute + graphics) with process-name resolution.
- A full **WPF** dashboard: all-GPU overview grid, per-GPU detail panels with live charts,
  a processes tab, and an activity log.
- Headless **CLI**: run the monitor's entry point with `--print` for a one-shot `nvidia-smi`
  style table, or `--print --watch <seconds>` for a live-updating feed.
- CSV / XML export of snapshots.
- Graceful **no-GPU / no-driver** fallback with a human-readable explanation.

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
# .NET 8 SDK required
dotnet build nvidia_app.sln -c Release
```

## Run
```powershell
# WPF dashboard
dotnet run --project src/NvidiaMonitor -c Release

# Headless one-shot table
dotnet run --project src/NvidiaMonitor -c Release -- --print

# Live headless feed (reprint every 2s, Ctrl-C to stop)
dotnet run --project src/NvidiaMonitor -c Release -- --print --watch 2
```

## Requirements
- Windows with an NVIDIA GPU and a driver that ships `nvml.dll`
  (typically under `%CommonProgramFiles(x86)%\NVIDIA Corporation\NVSMI\`).
- .NET 8 SDK.

## Notes
- No third-party NuGet dependencies; charts are drawn with native WPF shapes.
- Values NVML cannot report for a device are rendered as **N/A**.
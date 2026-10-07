# AI Tasks Log

## Summary

| Created | Task | Status | Type | Subtasks | Time Spent | Blockers |
|---------|------|--------|------|----------|------------|----------|
| 07-10-2026 16:59:24 | [T1: NVIDIA GPU Monitor (WPF/.NET 8, NVML P/Invoke)](#task-t1-nvidia-gpu-monitor-wpfnet-8-nvml-pinvoke) | done | feat | 16/17 | 4h 07m | none |

> 1 completed task(s) - [View completed tasks](#completed-tasks)

---

## Task T1: NVIDIA GPU Monitor (WPF/.NET 8, NVML P/Invoke)

- **Status**: done
- **Type**: feat
- **Created**: 07-10-2026 16:59:24
- **Last Updated**: 07-10-2026 19:52:00
- **Time Spent**: 4h 15m
- **Branch**: [`feat/ai-nvidia-monitor-app`](https://github.com/HectorBravo/nvidia_app/tree/feat/ai-nvidia-monitor-app) - created+pushed; ALL implementation happens here (NOT main)
- **Commit(s)**: [dd17559](https://github.com/HectorBravo/nvidia_app/commit/dd1755906b6bfd1be53812d39929101ca3900dcf) full implementation + README (feat branch, PUSHED); [c6fd96a](https://github.com/HectorBravo/nvidia_app/commit/c6fd96a6ba193e9889c12275263140d8157de9bd) scaffold (main)
- **Blockers**: none
- **Findings & Notes**:
  - **EXECUTION MODEL** (user directive ~18:45 prior session): two parallel subagents + integrator.
  - **GIT WORKFLOW CORRECTION** (07-10-2026 17:55, user directive): all implementation goes on `feat/ai-nvidia-monitor-app` (created+pushed), committed+pushed INCREMENTALLY as each subagent delivers; this log goes to `main` with findings/issues recorded as they surface. (Was initially holding code for a single final close-out push - corrected.)
    - **Subagent A "NvmlCore"** = NVML engine (P/Invoke) + headless CLI. Owns ONLY `src/NvmlCore/**` + `src/NvmlTest/**`.
    - **Subagent B "NvidiaMonitor"** = full WPF UI. Owns ONLY `src/NvidiaMonitor/**`; builds with `-p:BuildProjectReferences=false` against the pre-built core so the two never collide on the same `obj/`.
  - **ARCHITECTURE**: `HectorBravo.Nvidia.Contracts` (net8.0) is the ONLY shared seam (records + interfaces + history + log + exporter). NvmlCore->Contracts; NvidiaMonitor->NvmlCore+Contracts at solution level, but Subagent B's code may reference Contracts-only public API.
  - **BUILD STATUS**: full solution builds clean in Release, **0 warnings / 0 errors**.
  - **POST-RELEASE FIX** (07-10-2026 19:52, user report): two real bugs surfaced when comparing vs `nvidia-smi`.
    1. **Idle GPUs (util/mem = 0%) wrongly showed `N/A`** in `NvmlTest --print`. Root cause: `SmiFormatter.Int` used `v == 0 ? "N/A"` and the engine's `GetU32`/`GetU32Two` returned `0` on failure — so a *legitimately idle* GPU (0%) was indistinguishable from "unavailable" and N/A'd out. **Fix:** engine now returns a **-1 sentinel** on unavailable (missing symbol / NVML error); `SmiFormatter.Int`/`Power` render `N/A` only for `< 0`, so a valid `0` prints as `0`. NVLink guarded (`total <= 0`). Verified: idle GPUs 1&2 now show `0` (matches nvidia-smi).
    2. **WPF headless `--print` showed comma decimals** (`power=43,7 W`) and a mislabeled memory (`mem=3.183 MiB` = GB-scale value). Root cause: `Theme.MiB`/`Theme.Watts` used the thread culture (`:N0` / `0.0`) not `InvariantCulture`. **Fix:** both now format with `InvariantCulture`; `Watts` N/A-gate changed to `< 0`. Verified: `power=42.9 W`, `mem=3336/24576 MiB`.
    - **DELIVERABLE**: standalone **`dotnet publish`** executables into repo-local `bin/` (git-ignored): `bin\NvidiaMonitor\NvidiaMonitor.exe` and `bin\NvmlTest\NvmlTest.exe` — runnable directly, no `dotnet run` needed.
  - **FINDING** (07-10-2026 17:56): both subagent runs were **interrupted** (interactive abort when user sent a message). Partial files on disk (7 total, uncommitted) are GOOD QUALITY: NvmlCore foundation (loader/types/delegates/errors) + NvidiaMonitor (poller/factory/theme). Remaining: NvmlCoreService impl, SmiFormatter, NvmlTest CLI, App.cs, MainWindow.cs. Bugs found in partial code: NvmlLoader missing `using System.IO`; NvmlTypes DriverWddm/Wdm values swapped (WDM=0, WDDM=1 per header). Integrator is taking over to finish. Three scaffold bugs fixed 17:4x: `Exporter.cs` (void-chained `.Add` split), `AppLog.cs` (removed unused `_disposed`), `MainWindow.cs` (added `using System.Windows`).
  - **nvml.dll** lives at `%ProgramW6432%\NVIDIA Corporation\NVSMI` and `C:\Windows\System32`. Driver **32.0.16.1692** (R560-era). Loader must try NVSMI paths first, then System32.
  - **ref/nvml.h** = NVIDIA NVML **13.4.92** (pulled from NVIDIA/go-nvml). v2 APIs are safe (exist since NVML 9.0); any 13.x-only function must P/Invoke with an unversioned-symbol fallback and degrade to N/A on `NVML_ERROR_FUNCTION_NOT_FOUND`.
  - **Process names**: NVML returns PID only (v2 adds per-process memory). Resolve names via `Process.GetProcessById`, catch -> null (nvidia-smi behaves the same).
  - **ENV**: .NET SDK **8.0.425** at `C:\Program Files\dotnet` (winget MSI was half-broken; completed via `dotnet-install.ps1`). `nvidia-smi.exe` at `C:\windows\system32`. Use the full dotnet path; PATH may not be refreshed in existing shells.
  - **GIT/SSH**: user's global git uses `core.sshCommand=plink -agent` (PuTTY/pageant). The OpenSSH key `hector.bravo.20260612` loads after an ACL fix (`/inheritance:r` + grant `destr0:F`) but is NOT registered on GitHub. The GitHub-registered key is RSA `hector.bravo.20260219` in a PuTTY agent. **DECISION: push over HTTPS using the `gh` token; never touch the user's SSH keys or global git config.**
  - **WPF charts**: no third-party NuGet (private repo, zero external deps). Draw with `System.Windows.Shapes` (Polyline/Path) fed by `GpuHistory`.
  - **Hidden CLI**: `NvidiaMonitor.exe --print` (one nvidia-smi-style table, exit) and `--print --watch <s>` (loop, Ctrl-C clean). The WPF window must be suppressed when `--print` is present (parse args in App startup before building any window).
  - **Warnings policy**: `TreatWarningsAsErrors=true` on Contracts + NvmlCore; enable on all four at the final gate to guarantee zero warnings.
  - **Build command**: `powershell -File build_release.ps1` (repo root) -> `dotnet build nvidia_app.sln -c Release`.
  - **DONE** (07-10-2026 19:27): all subtasks complete. Feature branch pushed. NvmlTest --print verified vs nvidia-smi (driver 617.42, 3x RTX 3090, 24 GB). NvidiaMonitor --print verified headless. Full solution: 0 warnings / 0 errors (TreatWarningsAsErrors on all 4 projects). Planned next: per-GPU charts, processes/log tabs, process-name resolution, --watch, UI export buttons.
- **Post-release fixes** (08-10-2026, user-reported): (a) idle GPUs showed all `N/A` — engine `GetU32`/`GetU32Two` returned `0` for unavailable, colliding with a valid idle `0`; now a `-1` sentinel is returned and only `<0` renders `N/A`, so idle GPUs correctly show `0`. (b) WPF numeric format used the thread (Spanish) culture → comma decimals (`43,7 W`); fixed with `InvariantCulture`. (c) memory display switched to **MB** (from GB/MiB), plain integer, no thousands/comma separator → `3364/24576 MB`. (d) **CLI (`SmiFormatter`) formatting**: memory column now space-separated (`3227 / 24576 MB`) and all numeric value columns (Util %, Mem %, Temp, Power, Memory, MemClk, PSt, Fan) right-aligned; `GPU` index and `Name` stay left-aligned. Committed `9d682e8` (a,b) + `5ec4352` (c) + `af7561f` (d) on `feat/ai-nvidia-monitor-app`; tasklog mirrored to `main`. Standalone framework-dependent `.exe`s regenerated into git-ignored `bin/`.

### User Confirmations

**Pending (awaiting user response):**

None yet.

**Confirmed (user provided):**

- (07-10-2026 ~18:45) "split tasks as much as you can into two separate subagents, and remember this across the task list here. I have set up another qwen3.8 27B model running in parallel together with the one you are using now" -> **Two parallel subagents: A=NvmlCore (headless NVML engine+CLI), B=NvidiaMonitor (WPF UI)**; recorded here for recovery.
- (07-10-2026 17:50) Approved act-mode plan; chose "Kick off gh auth login --web now and give me the fresh device code" for GitHub auth.
- (prior session) UI = Overview tab with ALL GPUs together + per-GPU detail tabs + Processes + Log; stack = C# WPF .NET 8; private repo `HectorBravo/nvidia_app`.
- (07-10-2026 17:53:00) GitHub device code `C867-B042` authorized in browser -> `gh auth status` = logged in to **HectorBravo** (repo scope, HTTPS).
- (08-10-2026 22:06:50) Display memory in **MB** (not GB/MiB), no thousands/comma separator -> `3364/24576 MB` (commit 5ec4352).

### Subtasks / Plan

- [x] T1.1 - Environment: .NET SDK 8.0.425 installed and verified (`C:\Program Files\dotnet\dotnet.exe --list-sdks`)
- [x] T1.2 - SSH key chain resolved + ACL fixed (OpenSSH loads `hector.bravo.20260612`; key not on GitHub -> use HTTPS/gh instead)
- [x] T1.3 - GitHub `gh auth login --web` (USER ACTION: authorize code `C867-B042` in browser) -> `gh auth status` OK
- [x] T1.4 - Create private repo `HectorBravo/nvidia_app` via `gh repo create`
- [x] T1.5 - Local `git init -b main`, initial commit (README/.gitignore/this log/scaffold) as `chore(ai): [ai] scaffold NVML monitor solution` with AI_bot identity
- [x] T1.6 - Scaffold solution: 4 projects (Contracts / NvmlCore / NvmlTest / NvidiaMonitor) + sln + Contracts API surface (records, interfaces, history, log, exporter)
- [x] T1.7 - Pull official `nvml.h` (13.4.92) into `src/NvmlCore/ref/nvml.h` as the P/Invoke reference
- [x] T1.8 - Verify full solution builds clean (Release, 0 warnings / 0 errors) - DONE after fixing 3 scaffold bugs
- [x] T1.9 - SPAWN Subagent A "NvmlCore": NVML loader + `NvmlCore : IGpuMonitorService` + `GpuPollingService : IGpuPollingService` + `NvmlTest` CLI (`--print`, `--print --watch`) ; verify headlessly vs 3x RTX 3090; **only touch src/NvmlCore + src/NvmlTest**
- [x] T1.10 - SPAWN Subagent B "NvidiaMonitor": WPF UI (overview all-GPUs, per-GPU detail + charts, processes, log, header, pause/refresh, CSV/XML export, no-GPU fallback, `--print` window suppression); **only touch src/NvidiaMonitor**; build with `-p:BuildProjectReferences=false`
- [x] T1.11 - Integrate: full solution Release build, fix cross-boundary issues, zero warnings
- [x] T1.12 - Live verify: `NvmlTest.exe --print` (compare vs `nvidia-smi`), launch WPF app, poll, screenshot
- [x] T1.13 - Commit feature branch `feat/ai-nvidia-monitor-app` (code + [ai] tag) and push to remote (HTTPS via gh token)
- [x] T1.14 - Update this log after push (commits, hashes) + push log to `main`
- [ ] T1.15 - (Optional, if asked) `dotnet publish` single-file win-x64 self-contained
- [x] T1.16 - README with feature list, usage (incl. hidden CLI), build instructions
- [x] T1.17 - POST-RELEASE: fix idle-GPU `N/A` (engine -1 sentinel) + WPF comma-decimal/MiB bugs; publish standalone `bin\NvidiaMonitor\NvidiaMonitor.exe` + `bin\NvmlTest\NvmlTest.exe` (git-ignored). Verified vs `nvidia-smi`.

### Full Context Notes for AI Agents

> **Purpose**: self-contained knowledge base so a NEW session can resume WITHOUT any other context. Read Summary -> this task -> here.

**Repo layout (all under `D:\Repos\nvidia_app`):**

```
nvidia_app.sln                     # 4 projects wired up
.gitignore                         # bin/ obj/ out/ publish/ .vs/ *.user etc.
build_release.ps1                  # dotnet build nvidia_app.sln -c Release (logs to %TEMP%\full_build.log)
README.md                          # (TO CREATE at close-out)
ai/AI_TASKLOG.md                   # THIS FILE
src/
  HectorBravo.Nvidia.Contracts/    # net8.0  TreatWarningsAsErrors=true  (SHARED SEAM - integrator-owned)
    NvmlException.cs               #   NvmlException + NvmlErrorCode enum (values mirror nvml.h)
    GpuSnapshot.cs                 #   record: one GPU (identity, util, temps, power, mem, clocks, fan, ECC, throttle, nvlink, modes)
    GpuProcessInfo.cs              #   record + GpuProcessKind enum (Compute/Graphics)
    SystemSnapshot.cs              #   record: driver/cuda/nvml versions + Gpus[] + Processes[]
    GpuMetricSample.cs             #   record: one chart point (t, util, memUsed, power, temp, clock)
    GpuHistory.cs                  #   thread-safe ring buffer per GPU (default cap 300)
    AppLog.cs                      #   AppLogEntry + AppLogLevel + AppLog (bounded queue + EntryAdded event)
    IGpuMonitorService.cs          #   IGpuMonitorService + IGpuPollingService interfaces
    Exporter.cs                    #   static ToCsv / ToXml (nvidia-smi-style) + BytesToMiB
    DisplayValue.cs                #   value-or-N/A wrapper
  NvmlCore/                        # net8.0  TreatWarningsAsErrors=true  refs Contracts  (SUBAGENT A)
    Bootstrap.cs                   #   PLACEHOLDER - Subagent A deletes/replaces when engine lands
    ref/nvml.h                     #   NVIDIA NVML 13.4.92 header (~880 KB) - authoritative signatures/structs/enums
  NvmlTest/                        # net8.0 console Exe  refs NvmlCore+Contracts  (SUBAGENT A)
    Program.cs                     #   PLACEHOLDER - Subagent A implements --print / --print --watch
  NvidiaMonitor/                   # net8.0-windows WPF (UseWPF=true)  refs NvmlCore+Contracts  (SUBAGENT B)
    App.cs                         #   minimal Main (no XAML yet) - Subagent B rewrites (must honor --print args)
    MainWindow.cs                  #   placeholder window - Subagent B replaces with full UI
```

**SUBAGENT A "NvmlCore" (headless NVML engine + CLI) - owns ONLY `src/NvmlCore/**` + `src/NvmlTest/**`:**

1. **NVML loader**: `LoadLibraryW`/`GetProcAddress` (NOT static DllImport-only). Search order: `%CommonProgramFiles(x86)%\NVIDIA Corporation\NVSMI\nvml.dll`, `%ProgramW6432%\NVIDIA Corporation\NVSMI\nvml.dll`, `C:\Windows\System32\nvml.dll`. Cache function pointers in a dictionary.
2. **`NvmlCore : IGpuMonitorService`** (namespace `HectorBravo.Nvidia.Core`):
   - `InitializeAsync`: call `nvmlInit_v2` (fallback `nvmlInit`). Success -> IsAvailable=true. Any failure -> IsAvailable=false, set StatusMessage to a friendly reason (missing lib / no driver / no GPU), DO NOT throw.
   - `RefreshAsync`: one full pass -> SystemSnapshot. Get `nvmlSystemGetDriverVersion`, `nvmlSystemGetCudaDriverVersion_v2`, `nvmlSystemGetNVMLVersion`, `nvmlDeviceGetCount_v2` (fallback v1). For each device index: name, uuid, serial, busId (pci busId), vbios, utilization (gpu+mem+enc+dec), temps (gpu+mem), power (present + limits default/min/max), memory (total/used/free), clocks (gpu+mem+video+max), fan, perfState, ECC (current+persisting+errors), throttle reasons (bitmask -> string list), violation status, nvlink count, persistence mode, num cores, power source. Then per-GPU compute + graphics running processes via `nvmlDeviceGetComputeRunningProcesses_v2` / `nvmlDeviceGetGraphicsRunningProcesses_v2` (fallback unversioned) -> resolve names via `Process.GetProcessById` (catch -> null name).
   - 13.x-only functions: wrap in try, on `NVML_ERROR_FUNCTION_NOT_FOUND`/missing symbol -> set field to sentinel (-1 / 0 / empty) and move on. Never let one missing metric abort the whole pass.
3. **`GpuPollingService : IGpuPollingService`**: a `System.Threading.Timer` (default 2s) that calls the monitor's `RefreshAsync` on a worker (NOT UI thread); raises `SnapshotAvailable` with the SystemSnapshot and `StatusChanged` when IsAvailable flips. `Interval` settable (re-arms timer). Start/Pause/Resume/RefreshNowAsync/Dispose. Thread-safe.
4. **`NvmlTest/Program.cs`**: `--print` => one nvidia-smi-style table to stdout then exit 0; `--print --watch <sec>` => reprint every <sec> until Ctrl-C (clean exit); no args => print usage/help. Table = the standard `nvidia-smi` box (header w/ driver+CUDA, per-GPU rows w/ temp/power/util/mem, then a Processes section w/ GPU/PID/Type/Name/Mem). Use `Exporter`-style formatting or hand-build; must look like nvidia-smi.
5. **Verify**: `& 'C:\Program Files\dotnet\dotnet.exe' build src/NvmlCore -c Release` then `... run --project src/NvmlTest -c Release -- --print`. Compare name (RTX 3090), memory (~24576 MiB / ~24 GB), temps, driver `32.0.16.1692` against `nvidia-smi` output (3 GPUs on this machine).

**SUBAGENT B "NvidiaMonitor" (WPF UI) - owns ONLY `src/NvidiaMonitor/**`:**

- **Build isolation**: the core (NvmlCore+Contracts) is already built, so build ONLY this project with:
  `& 'C:\Program Files\dotnet\dotnet.exe' build src/NvidiaMonitor/NvidiaMonitor.csproj -c Release -p:BuildProjectReferences=false`
  This avoids rebuilding/owning NvmlCore's obj/ (prevents A/B collisions). If a needed public API is missing, code against what exists and list what you expect in your final message for the integrator - do NOT edit NvmlCore/Contracts.
- **`App` startup**: parse args BEFORE creating any window. If `--print` is present, delegate to the shared nvidia-smi print path (reuse NvmlTest's print logic via a small public helper, or call NvmlCore + format) and `Environment.Exit` - NO window. Only otherwise show the main window.
- **UI (replace App.cs/MainWindow.cs; XAML files preferred)**:
  - **Header**: driver version, CUDA version, NVML version, GPU count, refresh-interval ComboBox (1/2/5/10s), Pause/Resume + Refresh Now buttons, Export CSV + Export XML buttons.
  - **Overview tab**: one `DataGrid` showing ALL GPUs as rows (idx/name/driver-model/bus/temp/power/util/mem-used/mem-total/clocks/fan/perf/ECC/nvlink).
  - **Per-GPU tabs** (one per GPU): detail grid + live charts (Polyline/Path over `GpuHistory` for util, memory, power, temp, gpu-clock).
  - **Processes tab**: `DataGrid` of all GPU processes (GPU idx / PID / process name / kind / mem).
  - **Log tab**: `DataGrid`/`ListBox` bound to `AppLog` (filter by level, clear, export to file).
  - **No-GPU fallback**: when `IsAvailable == false`, show a big friendly screen with `StatusMessage`.
- **Threading**: subscribe to `IGpuPollingService.SnapshotAvailable` and marshal to the UI thread via `Application.Current.Dispatcher.BeginInvoke`. Push each sample into `GpuHistory` (cap 300). Never block the UI on NVML.
- **Theme**: dark (`#0f1117` background), NVIDIA green accent `#76b900`, monospaced numbers, modern layout.
- **Verify**: build (with `-p:BuildProjectReferences=false`), launch, confirm it renders, polls live, and `--print` runs headless with no window.

**INTEGRATOR (me) owns**: Contracts, `nvidia_app.sln`, `.gitignore`, `README.md`, `build_release.ps1`, `ai/`. Spawns A and B in parallel via `team_run_task` (async); waits for both; then does the full solution Release build, fixes cross-boundary issues, then git close-out.

**GIT CLOSE-OUT (integrator only, after both subagents done + full build green):**

- **Identity** (env vars ONLY; never `git config`): `GIT_AUTHOR_NAME=AI_bot`, `GIT_COMMITTER_NAME=AI_bot`. Do NOT set `GIT_AUTHOR_EMAIL`/`GIT_COMMITTER_EMAIL` (use the user's `git config user.email` = hbravo@cuic.net as-is). Every commit message includes `[ai]` after the type prefix.
- **Auth**: push over **HTTPS** with the `gh` token (`gh auth setup-git` or inline `https://x-access-token:<token>@github.com/...`). NEVER use the user's SSH keys/agent (ed25519 not on GitHub; RSA only in a PuTTY agent).
- **Sequence**: `git init -b main` -> commit `chore(ai): [ai] scaffold NVML monitor solution` (README, .gitignore, log, scaffold) -> `gh repo create nvidia_app --private --source . --remote origin --push` (pushes main) -> `git checkout -b feat/ai-nvidia-monitor-app` -> commit(s) for the implemented code (`feat(gpu-monitor): [ai] implement WPF NVIDIA GPU monitor (NVML P/Invoke)`) -> `git push origin feat/ai-nvidia-monitor-app`.
- **Log commits**: a dedicated `docs(ai): [ai] update task log before push` commit (only `ai/AI_TASKLOG.md`) BEFORE the code push, and another AFTER the push recording commit hashes. Push the log to the user's current branch (main).
- `gh` is at `C:\Program Files\GitHub CLI\gh.exe` (may not be on PATH).

**ENVIRONMENT GOTCHAS (learned the hard way - read before doing any shell work):**

- This PowerShell runs through a wrapper that **mangles double-quotes and some special chars** inside command strings. **DO** use single-quoted PowerShell strings and single-quoted here-strings (`@'...'@`) - verified to pass backticks, `$`, single quotes, and `**` through intact. AVOID `\"`, embedded `>` redirection in `Start-Process -ArgumentList`, and complex inline quoting; instead write a `.ps1` file and call it with `-File`.
- **File editing**: the `editor` tool is NOT available in this session. Create/overwrite files with `[System.IO.File]::WriteAllText($path, $hereString)` and append with `[System.IO.File]::AppendAllText`. Keep each write <= ~5 KB (the wrapper truncates very long command strings); split big content across sequential calls (create then append).
- **Background long-running processes**: `Start-Process -FilePath ... -NoNewWindow` returns control while the child keeps running (good for the `gh` login which waits for browser auth). Note: output of a backgrounded process is not captured afterwards - redirect the child's stdout/stderr to a log file and read that file.
- **`gh` interactive login**: `gh auth login --web` needs a TTY; run it as a background `Start-Process` with `-RedirectStandardOutput/-RedirectStandardError` to a log, then read the one-time code from the log for the user. The process stays alive (waiting for browser auth) until the user authorizes, then it exits and writes the token.
- **SDK**: `C:\Program Files\dotnet\dotnet.exe` (8.0.425). Call it by absolute path; `dotnet` may not be on PATH in existing shells.
- **GitHub reachability**: web, API, and raw are all reachable (200). `raw.githubusercontent.com` used to pull nvml.h.
- **dotnet output language**: the SDK prints in Spanish ("Determinando los proyectos...", "Compilacion correcta", "0 Advertencia(s)", "0 Errores") - locale is Spanish; that is normal, not an error.

**STATUS AT LAST UPDATE (07-10-2026 17:50:49):**
- T1.1, T1.2, T1.6, T1.7, T1.8 **done** (SDK + SSH ACL + scaffold + nvml.h + clean Release build of all 4 projects).
- T1.3 **in progress**: `gh auth login --web` running in background (PID 42772), code `C867-B042` handed to user; waiting for browser authorization.
- **Next**: (a) confirm `gh auth status` once user authorizes -> T1.4/T1.5 (repo + main commit); (b) spawn Subagent A and Subagent B **in parallel** via team_run_task (async) with the specs above; (c) wait for both; (d) full solution Release build + fix; (e) live verify (NvmlTest --print vs nvidia-smi, launch WPF, screenshot); (f) git close-out (feature branch + main, HTTPS via gh token, [ai] tag, AI_bot identity); (g) update this log with commit hashes before & after push.

---

## Completed Tasks

| Created | Task | Type | Subtasks | Time Spent |
|---------|------|------|----------|------------|

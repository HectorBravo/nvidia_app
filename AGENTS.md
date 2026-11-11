# AGENTS.md — Rules for AI Agents

<!-- PROJECT_INDEXER:BEGIN (managed block — do not edit by hand) -->
## Mandatory Pre-flight Reading

> [!IMPORTANT]
> Before writing, refactoring, or reviewing ANY code in this repository, you
> MUST read and follow the project map. It is the source of truth for stack,
> layout, entry points, build/run/test commands, and conventions.

**Required reading (in order):**
1. `ai/PROJECT_DESCRIPTION.md` — project map (stack, layout, build, conventions)

_(There is no `docs/` folder in this repo, so no additional `docs/*.md` reading
is required. If a `docs/` directory is added later, every `*.md` under it MUST
be listed here and read before code changes.)_

## Documentation Freshness Rule

> [!CAUTION]
> `ai/PROJECT_DESCRIPTION.md` must stay in sync with the code.
> - **At task start:** if its `git_head` (see the `PROJECT_INDEXER` block)
>   differs from `git rev-parse HEAD`, the doc is STALE — regenerate it first
>   using the `project-indexer` skill before doing other work.
> - **At task end:** any task that adds/modifies/deletes source, build,
>   config, packaging, or dependency files MUST end by refreshing
>   `ai/PROJECT_DESCRIPTION.md` (re-run the `project-indexer` workflow,
>   Steps 2–4) and updating its `last_indexed` + `git_head`.
>
> Do not conclude a code-changing task while the project map is stale.
<!-- PROJECT_INDEXER:END -->

<!-- User-managed content below this line. The project-indexer skill never edits it. -->

## Compile-Date Releases Rule (mandatory)

> [!IMPORTANT]
> Both apps print their own **compile date** (LastWriteTime of the executing
> assembly): `NvidiaMonitor` in its header (`MainWindow.cs`), `NvmlTest` on
> stdout (`Program.cs`). Every merge into `main` MUST be followed by a
> **GitHub Release per changed app**, named after that compile date:
>
> - `gui/<yyyy-MM-dd-HHmm>` → NvidiaMonitor (only if its dependency closure
>   changed in the merge)
> - `cli/<yyyy-MM-dd-HHmm>` → NvmlTest (same rule)
>
> Closure diff = shared libs (`src/NvmlCore`, `src/HectorBravo.Nvidia.Contracts`)
> bump BOTH apps; app-local changes bump only that app.
>
> **Run after every merge into `main`:**
> ```powershell
> .\release.ps1 --merge <pre-merge-commit> <post-merge-commit>
> ```
> The script builds + publishes both apps, zips each app's `bin\<App>\` output
> (exe + dlls + json manifests, **no `.pdb`**) and runs
> `gh release create gui|cli/<date> …`. It is idempotent (skips existing tags).
> Close the running dashboard first — a running `NvidiaMonitor.exe` locks its
> own DLLs.
>
> > [!IMPORTANT]
> > **Release text must carry a changelog.** In `--merge` mode the script
> > automatically prepends a `## What's changed` section built from the
> > conventional commit messages that touched each app's dependency closure
> > over the merge range (docs/`ai/` commits excluded). Never release with
> > only the static compile-date blurb. To (re)patch the notes of the newest
> > existing release without rebuilding: `.\release.ps1 --changelog <base> [<head>] [gui|cli]`.

Do NOT conclude a merge-into-main task while this step was skipped.


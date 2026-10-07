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

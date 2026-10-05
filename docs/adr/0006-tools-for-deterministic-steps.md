# 0006 — Tools, not models, for deterministic steps

**Status:** accepted

**Context.** Validation, schema migrations and committing have objectively correct outcomes. Asking a model to judge "do the tests pass" or to hand-write EF Core snapshots adds risk without value.

**Decision.** `validate` runs `dotnet build`, `dotnet test` and `gitleaks dir`; `migration` runs `dotnet ef migrations add`; `merge` runs `git commit`. Models are used only where judgment is needed (requirements, design, code, tests, docs, release readiness). Processes run with argument lists (no shell) and with MSBuild/compiler build servers disabled (they otherwise hold output pipes open and hang the run).

**Consequences.** + Ground truth for gates; reproducible results; cheaper. − Tool steps are slower (a real build and test run per validation, roughly 15–25 s here).

# Agentic SDLC Orchestrator

A working prototype that takes a change request and drives it through the software delivery lifecycle — requirements, design, implementation, tests, docs, validation, release readiness, merge — as an explicit dependency graph with gates, policy guardrails, human approval checkpoints, retries, rollback, safe-stop, dynamic re-planning, an audit trail and reliability metrics.

The system it builds and changes is a small **URL Shortener** (.NET 10, PostgreSQL), written first as the brownfield codebase the orchestrator works on.

| | |
|---|---|
| **Demo run reports (no setup needed)** | **https://rutujd92.github.io/agentic-sdlc/demo/** |
| Architecture and control flow | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Engineering summary (plan, risks, trade-offs, assumptions, limitations) | [docs/SUMMARY.md](docs/SUMMARY.md) |
| Design decisions | [docs/adr/](docs/adr/) |
| Scenarios | [scenarios/README.md](scenarios/README.md) |
| How the code was built (task plans used with Claude Code) | [docs/workflows/](docs/workflows/) |

## Quick start

Prerequisites: .NET SDK 10.0.301+ (`global.json`), Git, Docker (only for running the URL shortener against PostgreSQL). Optional: `gitleaks` (the validate step skips it with a note if missing).

```bash
git clone https://github.com/rutujd92/agentic-sdlc.git && cd agentic-sdlc
dotnet build
dotnet test            # 184 tests: 109 orchestrator, 75 URL shortener
```

### Run the scenarios (offline, no API key)

Every command prints the exact next command (approve / reject / revise / resume) with the run id filled in, and writes an HTML report to `runs/<runId>/report.html`.

```bash
# 1. Greenfield — QR code endpoint (parallel agents, retry with feedback, dependency review)
dotnet run --project src/Orchestrator.Cli -- run --scenario scenarios/greenfield
dotnet run --project src/Orchestrator.Cli -- approve <runId> implement --note "QRCoder is MIT"
dotnet run --project src/Orchestrator.Cli -- approve <runId> merge --note "Ship"

# 2. Brownfield — click analytics (re-plan adds an EF migration, two approvals; reject = rollback)
dotnet run --project src/Orchestrator.Cli -- run --scenario scenarios/brownfield
dotnet run --project src/Orchestrator.Cli -- approve <runId> migration --note "Additive table"     # approve the plan
dotnet run --project src/Orchestrator.Cli -- approve <runId> migration --note "Reviewed SQL"       # approve the generated migration
dotnet run --project src/Orchestrator.Cli -- approve <runId> merge --note "Ship"
#    ...or reject instead: dotnet run --project src/Orchestrator.Cli -- reject <runId> migration --reason "Schema freeze"

# 3. Ambiguous — "Make the links safer." (open questions pause; clarification re-plans)
dotnet run --project src/Orchestrator.Cli -- run --scenario scenarios/ambiguous
dotnet run --project src/Orchestrator.Cli -- revise <runId> requirements --guidance "$(cat scenarios/ambiguous/GUIDANCE.txt)"
#    optional: from a second terminal while it runs → dotnet run --project src/Orchestrator.Cli -- stop <runId>
#              then                                  → dotnet run --project src/Orchestrator.Cli -- resume <runId>
dotnet run --project src/Orchestrator.Cli -- approve <runId> merge --note "Ship"

# Inspect
dotnet run --project src/Orchestrator.Cli -- status <runId>      # state rebuilt from the event log
dotnet run --project src/Orchestrator.Cli -- report <runId>      # regenerate the HTML report
dotnet run --project src/Orchestrator.Cli -- metrics             # reliability metrics across all runs
git log --oneline main..orch/<runId>                             # the agents' change, on its own branch
```

Each scenario run creates a git worktree at `runs/<runId>/worktree` on branch `orch/<runId>`; the agents never touch your checkout or `main`. Clean up with `rm -rf runs && git worktree prune && git branch -D $(git branch --list 'orch/*')`.

### Live mode (Claude)

Replay is the default. With credit on an Anthropic account:

```bash
export ANTHROPIC_API_KEY=...        # never committed; read by the official SDK
dotnet run --project src/Orchestrator.Cli -- run --scenario scenarios/greenfield --live --record
```

`--record` overwrites the scenario's `responses/` with the live ones so the run can be replayed exactly. Live mode uses `claude-opus-5-5` with structured JSON outputs, a cached repository context and server-side refusal fallback.

> **Honest note:** the recorded responses in `scenarios/` were **hand-authored** (no API credit was available), then verified by building and testing the resulting code. The live request format is verified by a test against a local listener; live mode has not been exercised against the real API.

### Simulated runs (engine features without agents)

```bash
dotnet run --project src/Orchestrator.Cli -- graph                                   # the SDLC graph and its parallel layers
dotnet run --project src/Orchestrator.Cli -- run "Add QR codes" --flaky implement    # retry with backoff
dotnet run --project src/Orchestrator.Cli -- run "Add QR codes" --fail docs          # retry → fallback → skip downstream
dotnet run --project src/Orchestrator.Cli -- run "Configure DB" --with secret        # policy blocks a secret
dotnet run --project src/Orchestrator.Cli -- run "Add analytics" --with migration    # planner adds a migration node
```

### URL Shortener API

```bash
cp .env.example .env              # set a local password
docker compose up -d
dotnet user-secrets set "ConnectionStrings:Shortener" "Host=localhost;Port=5432;Database=shortener;Username=shortener;Password=<from .env>" --project src/UrlShortener.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/UrlShortener.Api --urls http://localhost:5080
curl -s -X POST localhost:5080/api/links -H 'Content-Type: application/json' -d '{"url":"https://example.com"}'
```

| Method | Path | Result |
|---|---|---|
| POST | `/api/links` `{url, alias?}` | 201 `{code, shortUrl, longUrl}` · 400 invalid · 409 alias taken · 429 rate limited |
| GET | `/{code}` | 302 with `Cache-Control: no-store`, atomic click count · 404 |
| GET | `/api/links/{code}/stats` | `{code, longUrl, createdAt, clickCount}` · 404 |
| GET | `/health` | Database health check |

## Testing approach

| Layer | What | How |
|---|---|---|
| Shortener Core | generator, validator, link service | unit tests with fakes (no DB) |
| Shortener Infrastructure | repository: insert-then-translate uniqueness, atomic increment | EF Core SQLite in-memory |
| Shortener API | every status code, 20 concurrent redirects, rate limit, health | `WebApplicationFactory` + temp SQLite file |
| Orchestrator engine | ordering, **parallel overlap and join**, gates, retries, fallback, rollback, stop/resume, approvals, re-planning, invalidation races | fake executors and in-memory stores; timing-sensitive tests run repeatedly to check flakiness |
| Governance | each policy rule, glob semantics, secret never echoed, license allow/deny/unknown | pure unit tests + the repository's own `policies.json` |
| Agents and sandbox | worktree diffs, path traversal, lanes, replay/record, malformed model output, request wire format | temporary git repos; a local HTTP listener stands in for the API |
| Metrics and report | latency, human wait, MTTR, success/rollback rates; DAG and HTML encoding | fixed event lists |
| End to end | three scenarios | real worktree, real `dotnet build`/`dotnet test`/`dotnet ef`/`gitleaks` |

Every change was test-first (tests shown failing, then implemented) and merged through a PR gated by CI: build + test + gitleaks, with branch protection on `main`.

## Repository layout

```
src/UrlShortener.{Core,Infrastructure,Api}       the product (clean architecture, dependencies point inward)
src/Orchestrator.Core                            graph, engine, state, governance, planning, metrics (no I/O)
src/Orchestrator.Infrastructure                  event/artifact stores, git worktree, agents, tool executors, report
src/Orchestrator.Cli                             composition root and commands
tests/                                           xUnit test projects
scenarios/                                       change requests + recorded agent responses
policies.json                                    guardrails (change control, secrets, licenses)
runs/                                            run outputs (git-ignored)
```

# CLAUDE.md — Agentic SDLC Orchestrator

This repository contains an agentic software delivery pipeline (the **Orchestrator**) and the sample product it builds and changes (the **URL Shortener**). Everything in the stack is open source.

**Time budget: one working day.** The Orchestrator is the primary deliverable. The URL Shortener is the subject it operates on. Keep the shortener small and correct, and spend the remaining time on orchestration.

## Projects

| Project | Purpose | Time box |
|---|---|---|
| **UrlShortener** | Sample product: create short links, redirect, click count, stats, health | 2.5 h (workflow 01) |
| **Orchestrator** | Runs a change request through requirements, design, implementation, tests, docs and release readiness, with gates, policies and human approvals | 4.75 h (workflow 02) |
| **Scenarios + docs** | Greenfield, brownfield and ambiguous runs; architecture, ADRs, summary | 3.25 h (workflow 03) |

The Angular Dashboard is replaced by a generated HTML run report (DAG, audit log, metrics). See ADR 0003.

## Tech Stack

| Layer | Choice |
|---|---|
| Backend | .NET 10, ASP.NET Core minimal APIs |
| Data (shortener) | PostgreSQL 16, EF Core (Npgsql provider) |
| Orchestrator state | Append-only JSONL event log per run (`runs/<runId>/events.jsonl`) |
| Tests | xUnit, `Microsoft.AspNetCore.Mvc.Testing`, EF Core SQLite in-memory; optional Testcontainers.PostgreSql smoke test |
| AI agents | `IChatProvider` abstraction: `ClaudeChatProvider` (Anthropic Messages API over `HttpClient`) and `ReplayChatProvider` (recorded responses, default, no API key needed). No Semantic Kernel (ADR 0002). |
| Containers | Docker, Docker Compose |
| Security checks | Gitleaks (if installed, else a built-in regex secret scan), .NET analyzers |

Only add packages with OSI-approved licenses (MIT, Apache 2.0, BSD, PostgreSQL License). Ask before adding any package not listed in the current workflow file.

## Repository Layout

```
/src
  UrlShortener.Core             domain, short-code generator, validator, LinkService, interfaces
  UrlShortener.Infrastructure   EF Core DbContext, repository, migrations, DependencyInjection.cs
  UrlShortener.Api              minimal API endpoints, DI, startup
  Orchestrator.Core             graph model, engine, gates, policies, events, metrics (no I/O)
  Orchestrator.Infrastructure   event store, git worktree, process runner, chat providers, node executors
  Orchestrator.Cli              run | approve | reject | stop | status | report
/tests
  UrlShortener.Tests            unit (Core) and integration (Api)
  Orchestrator.Tests            engine, gates, policies, retry/rollback, re-plan
/scenarios                      change requests + recorded agent responses for replay
/runs                           run outputs (git-ignored except committed demo runs)
/docs
  workflows/                    one file per workflow
  adr/                          architecture decision records
  ARCHITECTURE.md
docker-compose.yml
.env.example                    copy to .env (git-ignored)
Directory.Build.props
```

## Architecture Rules

- Dependencies point inward: Api → Infrastructure → Core, and Cli → Orchestrator.Infrastructure → Orchestrator.Core. Core projects reference nothing else in the solution.
- Interfaces live in Core; implementations live in Infrastructure.
- Business rules stay in Core and are unit-tested without a database, network or LLM.
- Orchestrator.Core is deterministic. Time comes from `TimeProvider`, and all LLM, git and process calls go through interfaces.

## Working Rules for Claude

1. Work on **one task at a time** from the current workflow file in `docs/workflows/`.
2. **Tests first:** write the tests for the task, run them, and confirm they fail before implementing.
3. Implement until `dotnet test` passes for the whole solution.
4. Summarize the change (files touched, tests added) and **wait for approval** before committing.
5. Commit messages follow Conventional Commits, e.g. `feat(core): add short-code generator`.
6. Never commit secrets, passwords, connection strings with passwords, or API keys.
7. Respect the task's time box. If a task overruns by more than 50%, stop and say so instead of expanding scope.
8. Do not build anything listed under "Out of Scope".

## Database

- PostgreSQL runs in Docker Compose. Credentials come from `.env` (git-ignored); `.env.example` is committed with placeholder values.
- Connection string key `ConnectionStrings:Shortener`, set via `dotnet user-secrets` locally or environment variables elsewhere.
- Add migrations with: `dotnet ef migrations add <Name> --project src/UrlShortener.Infrastructure --startup-project src/UrlShortener.Api`
- Migrations are applied on startup in Development only. Integration tests use SQLite in-memory with `EnsureCreated()` (Npgsql migrations do not run on SQLite) and keep the connection open for the lifetime of the test.
- All database calls are async and accept a `CancellationToken`.
- Uniqueness is enforced by the database. Insert first and translate the unique violation. Never check-then-insert.
- Counters are updated atomically (`ExecuteUpdateAsync`). Never read-modify-write.

## Configuration

- `Shortener:BaseUrl` builds `shortUrl`. Never derive it from the request `Host` header.
- `Anthropic:ApiKey` comes from user-secrets or the environment only. `Orchestrator:Provider` is `replay` (default) or `claude`.

## API Conventions

- Minimal APIs grouped by feature; errors returned as `ProblemDetails`.
- Status codes: 201 created, 302 redirect, 400 invalid input, 404 unknown code, 409 alias taken, 429 rate limited.
- Redirects send `Cache-Control: no-store` so every click reaches the server.
- OpenAPI via `Microsoft.AspNetCore.OpenApi`, exposed in Development only.

## Security Rules

- Accept only absolute `http`/`https` URLs up to 2,048 characters. Reject `javascript:`, `data:`, `file:`, relative URLs, URLs with user-info (`user@host`), and URLs pointing at the shortener's own host.
- Custom aliases: 3–30 characters, letters, digits, `-` and `_`, case-sensitive. Reserved words are rejected: `api`, `health`, `openapi`, `swagger`, `favicon.ico`.
- Short codes come from `RandomNumberGenerator.GetString` (cryptographically secure, no modulo bias).
- Rate limit link creation with the ASP.NET Core built-in rate limiter.
- Run `gitleaks detect` before every push.

## Orchestrator Rules

- Agents only modify files inside a per-run **git worktree** on branch `orch/<runId>`. They never touch `main`.
- High-impact actions always require human approval: requirement sign-off when ambiguity is found, database migrations, new packages, changes outside the allowed paths, and the final merge.
- Every state change is an event in the run's append-only log. The log is the audit trail, the resume point and the metrics source.

## Code Conventions

- English everywhere. Nullable enabled; warnings as errors (generated `Migrations/` excluded via `.editorconfig`).
- Async all the way; no `.Result` or `.Wait()`.
- Small, focused classes; no static state except constants.

## Commands

```bash
cp .env.example .env && docker compose up -d
dotnet build
dotnet test
dotnet run --project src/UrlShortener.Api
dotnet run --project src/Orchestrator.Cli -- run scenarios/greenfield
dotnet run --project src/Orchestrator.Cli -- approve <runId> <nodeId>
dotnet run --project src/Orchestrator.Cli -- report <runId>
```

## Out of Scope

- Authentication and user accounts
- Angular Dashboard (replaced by the HTML run report)
- Link expiry and detailed click analytics, except when produced by the Orchestrator's brownfield scenario
- Distributed orchestration, multi-tenant runs, and cloud deployment

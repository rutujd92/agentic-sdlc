# Workflow 01 — URL Shortener Foundation

**Time box:** 2.5 hours · **Goal:** a running URL Shortener API with create, redirect, stats and health, backed by PostgreSQL, with about 25 green tests. This is the codebase the Orchestrator will change.

## How to run each task

> Read CLAUDE.md and docs/workflows/01-url-shortener-foundation.md. Do task T<n> only. Write the tests first and show me they fail, then implement until `dotnet test` is green for the whole solution. Use only the packages listed for this task. Summarize the change and wait for my approval before committing.

## Tasks

| # | Time | Task | Ends at |
|---|---|---|---|
| T1 | 15 min | Solution skeleton | 0:15 |
| T2 | 25 min | Short-code generator + validator | 0:40 |
| T3 | 30 min | Database, entity, repository | 1:10 |
| T4 | 20 min | Link service | 1:30 |
| T5 | 35 min | Create + redirect endpoints | 2:05 |
| T6 | 25 min | Stats, health, rate limit | 2:30 |

### T1 — Solution skeleton (15 min)
- `git init`; `UrlShortener.sln` with `UrlShortener.Core`, `.Infrastructure`, `.Api`, `UrlShortener.Tests` per CLAUDE.md layout.
- `Directory.Build.props`: `net10.0`, nullable, warnings as errors, latest C#.
- `.editorconfig` marking `**/Migrations/*.cs` as `generated_code = true`.
- `.gitignore` for .NET plus `.env` and `runs/`; `.env.example` with placeholder Postgres values.
- Copy `CLAUDE.md` and the workflow files into the repo (`docs/workflows/`).
- One placeholder test.
- **Packages:** xUnit template defaults only.
- **Done when:** `dotnet build` and `dotnet test` succeed. Commit.

### T2 — Short-code generator + validator (25 min)
- `IShortCodeGenerator` in Core. The implementation uses `RandomNumberGenerator.GetString("0-9A-Za-z" alphabet, 7)`.
- `LinkValidator` in Core with `ValidateUrl(string)` and `ValidateAlias(string?)`, returning a result with an error message. It takes the shortener's own host (from options) so it can reject self-links.
- **Tests:**
  - code length 7, every char in the alphabet, 10,000 codes with no duplicates
  - accepts absolute `http`/`https`
  - rejects `javascript:`, `data:`, `file:`, `ftp:`, relative, empty, >2,048 chars, user-info URLs, own-host URLs
  - alias accepts 3–30 `[A-Za-z0-9_-]` and `null`; rejects short, long, spaces, symbols and reserved words (`api`, `health`, `openapi`, `swagger`, `favicon.ico`)
- **Done when:** tests pass. Commit.

### T3 — Database, entity, repository (30 min)
- `docker-compose.yml`: PostgreSQL 16, named volume, port 5432, credentials from `.env`.
- `Link` entity in Core: `Id`, `Code`, `LongUrl`, `CreatedAt`, `ClickCount`.
- `ILinkRepository` in Core with `AddAsync`, `GetByCodeAsync`, `IncrementClicksAsync`. `AddAsync` returns `Added` or `DuplicateCode`.
- `ShortenerDbContext` and `EfLinkRepository` in Infrastructure:
  - unique index on `Code`
  - unique violations (Postgres `23505`, SQLite constraint) translated to `DuplicateCode`
  - `IncrementClicksAsync` uses `ExecuteUpdateAsync(... ClickCount + 1)`
- `DependencyInjection.cs`; first migration `InitialCreate`; migrate on startup in Development.
- **Packages:** `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Sqlite` (tests only).
- **Tests (SQLite in-memory, open connection, `EnsureCreated`):** save and reload; duplicate code returns `DuplicateCode`; increment twice gives 2.
- **Done when:** tests pass, and `docker compose up -d` plus `dotnet run` creates the table. Commit.

### T4 — Link service (20 min)
- `LinkService.CreateAsync(url, alias, ct)` in Core returns `Created(link)`, `InvalidUrl(msg)`, `InvalidAlias(msg)`, `AliasTaken` or `CodeSpaceExhausted`.
- Insert first. A generated code that hits `DuplicateCode` is retried up to 5 times; an alias that hits it returns `AliasTaken`.
- `CreatedAt` comes from `TimeProvider`.
- **Tests (fake repository, fake generator, `FakeTimeProvider` or a hand-written stub):** valid → `Created`; invalid URL → `InvalidUrl`; taken alias → `AliasTaken`; collision retried then succeeds; 5 collisions → `CodeSpaceExhausted`.
- **Done when:** tests pass. Commit.

### T5 — Create + redirect endpoints (35 min)
- `POST /api/links` `{ url, alias? }` returns 201 `{ code, shortUrl, longUrl }` (`shortUrl` built from `Shortener:BaseUrl`), 400 `ProblemDetails`, or 409 for a taken alias.
- `GET /{code}` returns 302 with `Location` and `Cache-Control: no-store`, and increments clicks atomically. An unknown code returns 404.
- **Packages:** `Microsoft.AspNetCore.Mvc.Testing` (tests only).
- **Tests (WebApplicationFactory + SQLite in-memory):** 201, 400 and 409 for create; 302 with `Location` header; 404 for an unknown code; 20 parallel redirects give `ClickCount` 20.
- **Done when:** tests pass. Commit.

### T6 — Stats, health, rate limit (25 min)
- `GET /api/links/{code}/stats` returns `{ code, longUrl, createdAt, clickCount }`, or 404.
- `GET /health` runs the EF Core DB health check.
- Fixed-window rate limiter on `POST /api/links`, configurable, returning 429 `ProblemDetails`.
- **Packages:** `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`.
- **Tests:** stats 200 and 404; health 200; exceeding the limit returns 429.
- **Done when:** all tests pass. Commit, run `gitleaks detect`, and push.

## Definition of Done
- From a fresh clone, `cp .env.example .env && docker compose up -d && dotnet build && dotnet test` succeeds.
- About 25 tests green; one commit per task.

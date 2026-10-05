# Final engineering summary

## 1. Problem as understood

Build a prototype that turns a change request into a reviewable engineering outcome through an agentic, governed SDLC — and prove it on a real codebase. I read the brief as three deliverables in priority order:

1. **The orchestration layer** (the stated differentiator): explicit dependency graph, entry/exit gates, sequential and parallel paths with synchronization, cross-stage context and lineage, human approval for high-impact actions, bounded retries, fallback, rollback, safe-stop, policy guardrails, audit trail, reliability metrics, dynamic re-planning.
2. **A real target system** to change: a URL shortener built first, small but production-shaped (validation, uniqueness under concurrency, atomic counters, rate limiting, health, ProblemDetails).
3. **Three scenarios** — greenfield, brownfield, ambiguous — each showing decomposition, orchestration and validation, runnable by a reviewer.

**Ambiguities resolved by assumption:** "agents" means model calls for judgment work and tools for deterministic work; "human approval" can be CLI-based; a dashboard is optional if the audit trail and metrics are inspectable; "release" means a reviewed commit on a branch, not a deployment.

## 2. Plan and how it was executed

One working day, three workflows (task lists in `docs/workflows/`), every task test-first and merged via a PR gated by CI (build, test, gitleaks) with branch protection:

| Workflow | Tasks | PRs |
|---|---|---|
| 01 URL shortener | skeleton, generator + validator, persistence, service, endpoints, stats/health/rate limit | #2–#6 |
| 02 Orchestrator | graph model, engine, reliability, governance, re-planning, agents + sandbox, metrics + report | #7–#14 |
| 03 Scenarios + docs | smoke, greenfield, brownfield, ambiguous, documentation | #12, #15, this PR |

Scope cuts made deliberately (see ADRs): Angular dashboard → HTML report; Semantic Kernel → thin model seam; database-backed orchestrator state → event log; live recordings → hand-authored replays.

## 3. What was built (artifacts)

- `src/UrlShortener.*` — the product; 75 tests.
- `src/Orchestrator.*` — engine, governance, planning, agents, tools, metrics, report, CLI; 109 tests.
- `policies.json` — change control, secret patterns, license allowlist.
- `scenarios/` — greenfield, brownfield, ambiguous (+ smoke) with replayable responses.
- `docs/ARCHITECTURE.md`, `docs/adr/0001–0006`, this summary, `README.md`.
- Per run: `events.jsonl` (audit log), `artifacts/` (requirements, design, diffs, validation report), `report.html`, and a branch `orch/<runId>` with the agents' change.

## 4. Scenario outcomes (recorded from actual runs)

| Scenario | Decomposition | Orchestration exercised | Validation | Outcome |
|---|---|---|---|---|
| **Greenfield** — QR code endpoint | requirements → design → implement ∥ tests ∥ docs → validate → release → merge | lane violation by the tests agent **retried with the rejection as feedback**; new dependency (QRCoder, MIT) → 2 policy findings → **output review**; merge approval | real build + 78 tests + gitleaks | 5 files, +111 on `orch/…` |
| **Brownfield** — click analytics | design reasons across Core / Infrastructure / Api and **re-plans**: adds `migration` after implement, before validate | migration **approved twice** (plan, then generated files); `dotnet ef` generated the migration; merge approval | real build + 80 tests | 12 files, +401 incl. migration |
| Brownfield, alternative | same | human **rejects** the migration | — | downstream skipped, **rolled back** to last green checkpoint, clean worktree |
| **Ambiguous** — "Make the links safer." | requirements produce **3 open questions** → pause | human **revises** with an answer → requirements re-run with guidance → full pipeline; operator **stop** during validate → **resume** from the log | real build + 86 tests | configurable domain denylist, 11 new tests |

Aggregate over the 17 local runs (including simulated failure demos): success rate 60% of finished runs, rollback frequency 13%, retry rate 3.6% of attempts, MTTR 0.9 s. Human wait is reported separately from active time (the long brownfield run spans an overnight pause).

## 5. Validation strategy

- **Test pyramid:** pure unit tests for Core logic (no DB, network or model); SQLite-backed repository tests; `WebApplicationFactory` API tests incl. 20 concurrent redirects; engine tests with fake executors for every control path; worktree tests on real temporary git repos; a wire-format test for the Claude request against a local HTTP listener; end-to-end scenarios that run real builds and tests.
- **Flakiness:** timing-sensitive engine tests were run repeatedly (5–15×) before merging.
- **Gates in the product itself:** policy gate on every diff, open-questions gate, release-readiness checklist, real `dotnet test` in validate.
- **Bugs found by this process and fixed:** stop checked only once per scheduling pass; scheduler using a stale node definition after a mid-pass re-plan; a race where parallel siblings invalidated a join twice; a fake AWS key in a test caught by CI's secret scan (fixed without weakening the scanner); report drawing a re-planned node under the wrong parent; `dotnet ef` failing in a fresh worktree (no restore); builds hanging because MSBuild/compiler servers held the output pipes open.

## 6. Risks, trade-offs and guardrails

| Risk | Mitigation in place | Residual |
|---|---|---|
| Agent writes outside its remit or to `main` | per-run worktree, lanes, path-traversal guard, blocked paths incl. CI config and `policies.json` | a malicious diff inside an allowed path still needs human review (merge approval) |
| Secrets leak via generated code | secret patterns on added lines (value never logged) + gitleaks in validate | pattern-based detection has false negatives |
| Unvetted dependencies | license allowlist; unknown licenses blocked; allowed ones need sign-off | no vulnerability (CVE) scan yet |
| Schema changes | migrations generated by tooling, two human checkpoints, rollback on reject | no down-migration rehearsal against PostgreSQL |
| Runaway loops / cost | bounded retries, attempt and time budgets, safe-stop | no token budget yet |
| Humans approving stale work | output approvals are hash-checked; changed inputs invalidate pending approvals | — |
| Parallel implementer / test writer disagree | design must specify the shared contract; validate catches mismatches | a mismatch fails validation and rolls back instead of being auto-repaired |

Key trade-offs: determinism and auditability over agent autonomy (one structured call per node, no free-running tool loops); local single-process orchestration over distributed scale; tool-based validation over model judgment.

## 7. Assumptions

- A human operator with git identity is the approver (`git config user.email`); no authentication or RBAC for approvals.
- The target repository is .NET with the layout in `CLAUDE.md`; lanes and policies are configured for it.
- Worktrees branch from the current `HEAD` of the repository.
- PostgreSQL is used only by the running API; tests use SQLite.

## 8. Limitations

- **Live model runs not exercised:** recordings are hand-authored (no API credit); live mode is verified only at the request-format level.
- Single-process orchestrator; one writer per run; no distributed locking or queueing.
- Approvals via CLI; no dashboard, notifications or SLA timers on human waits.
- Validate failures are not automatically fed back to the implementer (they fail and roll back the run); a human can `revise` with the error.
- Integration tests run on SQLite, not PostgreSQL (Testcontainers smoke test was cut).
- No CVE scanning, SBOM or signed commits; merge stops at a local branch (pushing/PR is manual by design).

## 9. Next steps

1. Record the three scenarios live with `--live --record` and compare against the hand-authored baselines.
2. Feed validation failures back to implement as automatic re-plans (bounded).
3. Token/cost budgets per run alongside attempt and time budgets.
4. Database-backed event store and a small web UI for approvals, using the same log.
5. Dependency vulnerability scanning in the policy gate; PostgreSQL integration tests via Testcontainers.

# Architecture

## Components

```
                    ┌──────────────────────────── Orchestrator.Cli (composition root) ────────────────────────────┐
                    │ run | resume | approve | reject | revise | stop | status | report | metrics | graph        │
                    └───────────────┬───────────────────────────────────────────────────────┬──────────────────┘
                                    │                                                       │
┌──────────────── Orchestrator.Core (deterministic, no I/O) ───────────────┐   ┌──── Orchestrator.Infrastructure ────────────┐
│ Graph       WorkflowGraph (validate, topo order, layers), GraphChange     │   │ Storage    JsonlEventStore, FileArtifactStore│
│ Execution   OrchestrationEngine, gates, retry policy, checkpoints, stop   │   │            FileStopSignal, PolicyFile         │
│ State       RunState = projection of the event log                       │   │ Workspace  GitWorktree (sandbox + checkpoints)│
│ Governance  PolicyEngine/PolicyGate, OpenQuestionsGate, ApprovalService   │◄──│            ProcessRunner (no shell)           │
│ Planning    ReplanService (revise), plan validation in the engine        │   │ Agents     LlmNodeExecutor + AgentSpecs       │
│ Agents      IChatProvider (seam to any model)                             │   │            Claude / Replay / Recording        │
│ Metrics     RunMetrics, AggregateMetrics (from events only)               │   │            Validate / Migration / Merge tools │
└───────────────────────────────────────────────────────────────────────────┘   │ Reporting  RunReport (HTML + Mermaid DAG)     │
                                                                                └──────────────────────────────────────────────┘
                          operates on ▼ (in runs/<runId>/worktree, branch orch/<runId>)
        UrlShortener.Api ──► UrlShortener.Infrastructure ──► UrlShortener.Core          (+ PostgreSQL, tests)
```

Both products follow the same rule: dependencies point inward, Core references nothing else in the solution (enforced by tests that read the `.csproj` files).

## Orchestration model

The standard graph (`orchestrator graph`):

```
requirements ─► design ─┬─► implement ─┐
                        ├─► tests ─────┼─► validate ─► release-readiness ─► merge [human approval]
                        └─► docs ──────┘
```

Planners can extend it at run time, e.g. design adds `migration` (after implement, before validate, approval forced).

### Per-node control flow

```
deps all Succeeded ─► NodeReady ─► entry gates ──fail──► NodeFailed
                                     │pass
                       RequiresApproval? ──yes, not granted──► ApprovalRequested(pre) ─► pause
                                     │
             ┌──────── attempt (primary: N attempts with backoff, then each fallback once) ────────┐
             │ executor ─► output ─► exit gates (policy, open questions, …) ─► plan validation      │
             │   fail → RetryScheduled (failure text becomes Feedback for the next attempt)        │
             └─────────────────────────────────────────────────────────────────────────────────────┘
                 │ all attempts failed                │ gate says "needs review"        │ pass
                 ▼                                    ▼                                  ▼
            NodeFailed                    ApprovalRequested(output) ─► pause     Replanned? ─► invalidate stale
                                          approve → completes from the           dependents ─► NodeSucceeded
                                          reviewed artifact (hash-checked)        ─► CheckpointCreated
```

### Run level

- **Scheduling:** event-driven (`Task.WhenAny`): every node whose dependencies have succeeded starts immediately, up to `MaxParallelism`. Independent branches run in parallel; joins wait for all inputs; a slow branch never blocks an unrelated one. Readiness is always evaluated against the *current* graph (it can change mid-pass).
- **Failure:** independent branches finish; downstream nodes are `NodeSkipped` with the blocking cause; `RunFailed`, then **rollback** to the last green checkpoint (`git reset --hard` + `clean`).
- **Pause:** when nothing can progress and approvals are pending → `RunPaused`. Humans act through `approve`, `reject` or `revise` (appended to the log with `human:<git user.email>`), then the run resumes in any process.
- **Safe-stop:** operator stop file, attempt budget or wall-clock budget, checked before every node start. Running nodes finish; nothing new starts; the run is resumable.
- **Re-planning:** a revision (or a changed upstream output) re-runs a node; if its output hash changes, direct dependents that consumed the old output are invalidated and re-run, cascading only while outputs keep changing. Pending approvals on changed inputs are invalidated too.

### State and lineage

The run's **append-only event log** (`runs/<id>/events.jsonl`) is the single source of truth:

- `RunState` is a projection applied live by the engine *and* rebuilt from the log by the same code (`status`, `resume`, approvals).
- Each event carries sequence, timestamp, actor (`system`, `agent:<role>`, `tool:<name>`, `human:<email>`), node, message/rationale and data.
- `NodeSucceeded` records the output hash, the hashes of every upstream input, and model/token telemetry: decision lineage is "this output was produced by this actor from exactly these inputs".
- Metrics and the HTML report are computed from the log, so they cannot drift from what happened.

Event types: `RunStarted, NodeReady, NodeStarted, GatePassed, GateFailed, NodeSucceeded, NodeFailed, NodeSkipped, RetryScheduled, FallbackUsed, ApprovalRequested, ApprovalGranted, ApprovalRejected, PolicyViolation, CheckpointCreated, RolledBack, Replanned, NodeInvalidated, SafeStopped, RunResumed, RunPaused, RunCompleted, RunFailed`.

## Agents and tools

| Node | Executor | Output |
|---|---|---|
| requirements | Claude, JSON schema | problem, acceptance criteria, assumptions, **open questions**, out of scope |
| design | Claude, JSON schema | approach, impacted files, API/schema contract, risks; may return a **plan** (migration node) |
| implement / tests / docs | Claude, JSON schema | complete file contents, confined to a **lane**; applied in the worktree; artifact = git diff |
| migration | `dotnet ef migrations add` | generated migration diff |
| validate | `dotnet build`, `dotnet test`, `gitleaks dir` | validation report |
| release-readiness | Claude, JSON schema | checklist, risks, ready / not ready (not ready fails the node) |
| merge | `git commit` on `orch/<runId>` | summary + PR command (pushing stays human) |

Model calls go through `IChatProvider`: `ClaudeChatProvider` (official Anthropic SDK, `claude-opus-5-5`, structured outputs, cached repository context, server-side refusal fallback), `ReplayChatProvider` (default; recordings keyed by node + feedback hash) and `RecordingChatProvider`.

## Governance

| Control | Where | Effect |
|---|---|---|
| Change control | `policies.json` allowed / blocked / approval path globs | outside allowed or blocked path (CI config, `policies.json`, keys, `.env`) → block; migrations, `*.csproj`, compose → human review |
| Secrets | regex patterns on **added** lines | block; finding names file + pattern, never the value |
| Licenses | new `PackageReference` vs allowlist | unknown or disallowed → block; allowed → human sign-off |
| Lanes | per-agent writable paths | out-of-lane write fails the attempt |
| Sandbox | git worktree per run, path-traversal guard | agents cannot touch `main` or escape the worktree |
| Ambiguity | `OpenQuestionsGate` on requirements | open questions pause for clarification |
| High-impact actions | `RequiresApproval` (merge; migrations and high-risk nodes added by plans are forced) | pause before executing |
| Plan safety | engine | plans cannot rewire completed nodes; resulting graph must validate (no cycles) |

Blocks fail the attempt with the finding as feedback, so the agent can fix it on retry.

## Reliability metrics

Per run and across runs (`orchestrator metrics`, and tiles in each report): success rate, rollback frequency, retry rate, **MTTR** (a node's first failure signal → its recovery), end-to-end latency split into **active time** and **human wait**, attempts, fallbacks, policy findings, approvals, re-plans, invalidations, tokens per node.

## Key decisions

See [adr/](adr/): event log instead of a database (0001), thin model seam instead of an agent framework (0002), HTML report instead of a dashboard (0003), replay by default (0004), git worktrees as the sandbox (0005), tools for deterministic steps (0006).

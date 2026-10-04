# Workflow 02 — Orchestrator Core

**Time box:** 4.75 hours · **Goal:** a CLI that runs a change request through an explicit SDLC dependency graph. The run has entry and exit gates, parallel branches with a join, human approvals, policy guardrails, bounded retries, fallback, rollback, safe-stop, dynamic re-planning, an audit log and reliability metrics.

## Control model

```
Requirements ──► [Clarify? human] ──► Design ──► [Approve design if high-impact] ─┬─► Implement ──┐
                                                                                  ├─► Tests ──────┼─► Validate (join) ─► ReleaseReadiness ─► [Approve merge] ─► Merge
                                                                                  └─► Docs ───────┘
```

- **Node:** `Id`, `Kind`, `DependsOn`, `EntryGate`, `Executor`, `ExitGates`, `RetryPolicy`, `RiskLevel`.
- **Node states:** `Pending → Ready → Running → Succeeded | Failed | AwaitingApproval | Skipped | Invalidated`.
- **Run states:** `Running`, `Paused` (awaiting a human), `Stopped` (safe-stop), `Succeeded`, `Failed`, `RolledBack`.
- **Events** (append-only JSONL, each with `runId`, `nodeId`, `timestamp`, `actor` (agent, human, or system), `inputHashes`, `outputHash`, `rationale`): `RunStarted`, `NodeReady`, `NodeStarted`, `GatePassed`, `GateFailed`, `RetryScheduled`, `FallbackUsed`, `ApprovalRequested`, `ApprovalGranted`, `ApprovalRejected`, `PolicyViolation`, `CheckpointCreated`, `RolledBack`, `Replanned`, `NodeInvalidated`, `SafeStopped`, `RunCompleted`.
- **State is rebuilt by replaying events**, so `approve` resumes a paused run after a process restart.

## Tasks

| # | Time | Task |
|---|---|---|
| O1 | 30 min | Projects + graph model |
| O2 | 60 min | Engine: scheduler, gates, parallel join, event store |
| O3 | 45 min | Reliability: retry, fallback, checkpoint/rollback, safe-stop |
| O4 | 45 min | Governance: policy engine + approval checkpoints + CLI |
| O5 | 30 min | Dynamic re-planning |
| O6 | 45 min | Agents and executors |
| O7 | 30 min | Metrics + HTML run report |

### O1 — Projects + graph model (30 min)
- Add `Orchestrator.Core`, `.Infrastructure`, `.Cli` and `Orchestrator.Tests`.
- `WorkflowGraph` built from nodes, with validation: unknown dependency, cycle detection, a single entry node, and topological order.
- **Tests:** a valid graph gives its topological order; a cycle is rejected with its path; an unknown dependency is rejected.

### O2 — Engine (60 min)
- `OrchestrationEngine.RunAsync`:
  - repeatedly starts every `Ready` node in parallel (`Task.WhenAll`, bounded concurrency);
  - evaluates the entry gate before each node and the exit gates after it;
  - marks a node `Ready` only when all dependencies have `Succeeded` (the join).
- `IEventStore` in Core; `JsonlEventStore` in Infrastructure; `RunState` projection rebuilt from events.
- `RunContext` carries artifacts between nodes as content-hashed blobs plus a decision log. This keeps cross-stage context and lineage.
- **Tests (fake executors):** sequential order respected; the three parallel branches overlap in time and the join waits for all; an exit-gate failure fails the node; state rebuilt from events equals live state.

### O3 — Reliability (45 min)
- `RetryPolicy(maxAttempts, backoff)`. Gate failure details are fed into the next attempt's context.
- Fallback chain per node: primary provider, then secondary, then escalation to a human (`AwaitingApproval`).
- `ICheckpointStore`: git commit of the worktree after each green node. `Rollback` resets to the last checkpoint, and the run moves to `RolledBack` on unrecoverable failure.
- Safe-stop: a `stop` command (a stop file or flag read at node boundaries), a max-total-attempts budget, and a wall-clock budget. Stop leaves the run resumable.
- **Tests:** fail-twice-then-pass runs 3 attempts; retries exhausted triggers fallback; fallback exhausted rolls back to the checkpoint; stop during a run halts before the next node and emits `SafeStopped`.

### O4 — Governance (45 min)
- `PolicyEngine` loads JSON rules (`policies.json`) to avoid adding a YAML dependency. Examples:
  - allowed path globs per scenario;
  - `**/Migrations/**` changed → high impact → approval;
  - new `PackageReference` → check the license allowlist, then approval;
  - secret pattern found → block;
  - edits to `main` → block.
- Approval checkpoints pause the run (`ApprovalRequested`).
- CLI: `approve <runId> <nodeId> [--note]`, `reject <runId> <nodeId> --reason`, `status <runId>`, `stop <runId>`. The approver identity comes from `git config user.email`.
- **Tests:** a migration diff requires approval; a secret blocks; approve resumes the run; reject fails the node and triggers rollback.

### O5 — Dynamic re-planning (30 min)
- When an artifact's hash changes (for example after a clarification answer or a rejected design that gets revised), invalidate every transitive dependent whose input hashes differ and re-run only those.
- The planner may add nodes, such as a `Migration` node. Graph diffs are policy-checked, and an added high-impact node inherits an approval gate. Emits `Replanned` with the diff and rationale.
- **Tests:** changing the Requirements output invalidates Design and all downstream nodes but not unrelated completed nodes; adding a migration node adds an approval gate.

### O6 — Agents and executors (45 min)
- `IChatProvider` with two implementations:
  - `ReplayChatProvider` (default) reads `scenarios/<name>/responses/<nodeId>.md`.
  - `ClaudeChatProvider` calls the Anthropic Messages API via `HttpClient`, using model `claude-sonnet-5-5` with `claude-opus-5-5` for Design. The API key comes from user-secrets.
- Executors:
  - `RequirementsAgent`: normalized problem, acceptance criteria, ambiguities, assumptions.
  - `DesignAgent`: impacted modules, API/schema changes, risks.
  - `ImplementAgent`: produces a unified diff, applied with `git apply` in the worktree.
  - `TestAgent`: test diff.
  - `DocsAgent`: README/CHANGELOG diff.
  - `ValidateExecutor`: `dotnet build`, `dotnet test`, gitleaks or the regex scan, then policy evaluation of the combined diff.
  - `ReleaseReadinessExecutor`: checklist plus risk summary.
- `IProcessRunner` and `IGitWorkspace` abstractions keep Core testable.
- **Tests:** the replay provider returns recorded output; a malformed diff fails the exit gate and is retried; validate runs real `dotnet test` in an integration test, tagged `Category=Slow`.

### O7 — Metrics + run report (30 min)
- Computed from events, per run and across `runs/`:
  - success rate
  - retry rate
  - rollback frequency
  - MTTR (first failure to next green)
  - per-node and end-to-end latency
  - human wait time
- `report <runId>` writes `runs/<runId>/report.html`: a Mermaid DAG coloured by state, the timeline, the audit table (actor, event, rationale), approvals, metrics and the final diff.
- **Tests:** metrics calculator over a fixed event list gives the expected numbers.

## Definition of Done
- `dotnet test` green, with slow tests optional via a filter.
- `orchestrator run scenarios/greenfield` completes in replay mode with no API key and produces a report.

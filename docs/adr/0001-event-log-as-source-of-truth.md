# 0001 — Append-only event log as the orchestrator's source of truth

**Status:** accepted

**Context.** The assignment needs stateful, resumable runs, audit-grade traceability, decision lineage and reliability metrics. Storing current state in a database and logging separately creates two truths that drift.

**Decision.** Each run is an append-only JSON Lines file (`runs/<id>/events.jsonl`). Run state is a projection (`RunState.Apply`) used live by the engine and rebuilt by the same code for `status`, `resume` and approvals. Metrics and reports are computed from the log. Appends are serialized per run, so sequence numbers are contiguous.

**Consequences.** + One truth; resume after a crash or in another process; audit trail and metrics cannot disagree; human-readable, diffable. − Single writer per run; no cross-run queries without scanning files; a database-backed `IEventStore` would be needed for concurrent multi-instance orchestration (the interface allows it).

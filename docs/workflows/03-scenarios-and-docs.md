# Workflow 03 — Scenarios and Documentation

**Time box:** 3.25 hours. Each scenario folder holds `request.md`, `policies.json` overrides and `responses/` (recorded agent outputs). Record them once with the Claude provider, or write them by hand, so the demo is deterministic.

### S1 — Greenfield (30 min)
- **Request:** "Add `GET /api/links/{code}/qr` returning a PNG QR code of the short URL."
- **Shows:** the full DAG, the parallel Implement/Tests/Docs branches, and auto-approval of low-risk nodes. The new package triggers a license check and an approval.

### S2 — Brownfield (45 min)
- **Request:** "Add click analytics: record each click (timestamp, referrer, user-agent) and add `expiresAt` to links. Expired links return 410."
- **Shows:** codebase reasoning (impacted Link, LinkService, redirect, stats, migration) and the migration approval gate.
- Includes a forced first-attempt test failure, showing retry then success.
- A second run has the human reject the migration, showing rollback.

### S3 — Ambiguous (30 min)
- **Request:** "Make the links safer."
- **Shows:** the Requirements agent lists interpretations (malware blocklist? expiry? rate limits? preview page?) and the run pauses for clarification.
- The human answers "block known-malicious domains via a configurable denylist". The Requirements artifact changes, triggering re-planning: downstream nodes are invalidated and re-run.
- Also demonstrates safe-stop mid-run, then resume.

### D1 — Documentation (1.5 h)
- `README.md`: purpose, quick start (setup, run the API, run the three scenarios, open the reports), and testing approach.
- `docs/ARCHITECTURE.md`: components, the orchestration model, the control-flow diagram, the state machine, the event schema, and how each assignment requirement maps to the code.
- `docs/adr/`:
  - 0001 JSONL event log instead of a DB for orchestrator state
  - 0002 a thin `IChatProvider` instead of Semantic Kernel
  - 0003 HTML report instead of an Angular dashboard
  - 0004 replay-by-default for reproducible demos
  - 0005 git worktrees as the agent sandbox
- `docs/SUMMARY.md`, the final engineering summary: plan and rationale, artifacts, risks and trade-offs, failure scenarios and guardrails, validation, assumptions and limitations.
- Known limitations to state:
  - single-process engine
  - replay responses are curated
  - the LLM diff quality depends on the model
  - no auth
  - the SQLite vs Postgres test gap
- Commit the three demo runs (`runs/demo-*`) so reviewers can open the reports without running anything.

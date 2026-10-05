# 0003 — Generated HTML report instead of an Angular dashboard

**Status:** accepted

**Context.** One working day. A dashboard needs an API, auth decisions and a frontend build; reviewers mainly need to see the graph, decisions, audit trail and metrics.

**Decision.** `RunReport` renders a self-contained HTML file per run (Mermaid DAG coloured by status, metric tiles, decisions, policy findings, per-node stats, timeline, artifacts), written automatically after every run and resume. Approvals happen in the CLI.

**Consequences.** + Zero infrastructure, works offline (except the Mermaid script), easy to attach to a PR. − Not live-updating; no approve buttons; one run per page. A dashboard can be built later on the same event log.

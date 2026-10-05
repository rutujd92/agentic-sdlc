# 0004 — Replay recorded agent responses by default

**Status:** accepted

**Context.** Reviewers must be able to run the prototype end to end without an API key or cost, and demos must reliably reach specific controls (retry, dependency review, migration approval, rollback, clarification).

**Decision.** `ReplayChatProvider` is the default. Recordings are keyed by node + hash of the feedback/guidance (not the full prompt), so they survive unrelated repository changes while revisions and retries get their own recording. `--live --record` captures new ones.

**Consequences.** + Deterministic, offline, free demos; scenarios double as regression tests of the orchestration. − Replays do not show model variability. In this submission the recordings were hand-authored (no API credit) and verified by building and testing the resulting code; the live request format is verified against a local listener only.

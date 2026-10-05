# 0005 — A git worktree per run as the agents' sandbox

**Status:** accepted

**Context.** Agents write code. They must never touch the developer's checkout or `main`, changes must be reviewable as diffs, and failures must be reversible.

**Decision.** Each run gets `runs/<id>/worktree` on branch `orch/<id>`. Agents return whole files; the worktree writes them (path-traversal guarded, lane-checked) and git produces the diff that policy evaluates. The worktree is also the checkpoint store (commit after each green node; rollback = `reset --hard` + `clean -fd`). Merge commits to the run branch; pushing and opening the PR stay human actions. Git operations are serialized because parallel nodes share one index.

**Consequences.** + Real isolation with standard tooling; rollback and review for free; the result is a normal branch. − Disk use per run (clean up with `git worktree prune`); worktrees branch from the current `HEAD`; the build output in `runs/` should be excluded from desktop indexers.

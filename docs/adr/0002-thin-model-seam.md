# 0002 — A thin `IChatProvider` seam instead of an agent framework

**Status:** accepted (supersedes the original Semantic Kernel plan)

**Context.** The orchestration logic (graph, gates, retries, approvals) is the differentiator and must be deterministic and unit-testable. Agent frameworks bring their own planning loops and abstractions that overlap with the engine.

**Decision.** Core defines `IChatProvider` (one schema-constrained request → JSON). Infrastructure implements it with the official Anthropic SDK, a replay provider and a recording decorator. Each node is one structured call; orchestration stays in our engine.

**Consequences.** + Engine fully testable with fakes; any model or provider can be added behind one interface; structured outputs make agent results machine-checkable. − No multi-turn tool-using agents inside a node (an agent cannot run the tests itself); quality depends on giving each agent good context in a single call.

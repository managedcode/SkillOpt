# SkillOpt source parity

## Source provenance

The C# library is maintained in this GitHub fork of [`microsoft/SkillOpt`](https://github.com/microsoft/SkillOpt). Its base is upstream commit [`fa4ca184573e42ec11472959dd57422381418096`](https://github.com/microsoft/SkillOpt/commit/fa4ca184573e42ec11472959dd57422381418096). Upstream reports MIT license, package version 0.2.0, and copyright Microsoft Corporation (2026); the fork retains the upstream `LICENSE`. The C# package starts at independent NuGet version 0.1.0.

Primary reviewed upstream implementation: `skillopt/engine/trainer.py`, `skillopt/gradient/reflect.py`, `skillopt/gradient/aggregate.py`, `skillopt/optimizer/clip.py`, `skillopt/evaluation/gate.py`, `skillopt/optimizer/slow_update.py`, and `skillopt/optimizer/meta_skill.py`. The upstream training guide is `docs/guide/training-loop.md`.

## Implemented in ManagedCode.SkillOpt

| Upstream behavior | C# coverage |
|---|---|
| Frozen target rollout against changing skill Markdown | One caller-provided `TargetChatClient` instance is reused across all cases and skill revisions. Cases carry multi-message history. |
| Evaluate rollouts | Microsoft `IEvaluator`; caller selects one named numeric metric, raw range, and explicit higher-is-better/lower-is-better direction. All returned numeric, boolean, and string metrics plus reasons, interpretations, and diagnostics remain in typed per-case evidence. |
| Reflect from failures and successes | Separate outcome groups and file-backed reflection prompts; `FailureOnly` is configurable. Actual target responses and scores are included. |
| Hierarchical patch aggregation | Repeated bounded merge batches; deterministic concatenation fallback on malformed aggregate JSON. |
| Rank and clip edits | Optimizer-selected zero-based indexes, strict edit count budget, deterministic top-order truncation fallback. |
| Patch skill text | Exact-target append, insert-after, replace, and delete operations. Ambiguous or missing anchors are skipped. |
| Rewrite skill text | `Patch`, `RewriteFromSuggestions`, and `FullRewrite` modes. |
| Selection gate | Strict improvement over current objective score, with configurable minimum improvement; optional application-owned candidate gate receives all per-case selection evidence and can veto despite a better mean. Best skill is retained separately. |
| Learning budgets | Required limits for epochs, steps, rollouts, optimizer calls, evaluator calls, edits per step, and rejected-edit history. Constant, linear, and cosine edit budgets are available. |
| Rejected-edit memory | Bounded candidate/rejection text is fed into later reflection prompts and carried in typed state. |
| Epoch-level slow and meta updates | From the second completed epoch, prompts compare prior/current skill, update meta memory, and gate a slow patch on the selection split. |
| Best skill artifact | `BestSkill` returns the accepted best Markdown as data, ready for the host to save as `best_skill.md`. |
| Split discipline and final reporting | Training/selection are required. Optional test cases are excluded from optimizer prompts and evaluated at the end for initial and best skill reports; an empty test split reports null mean and zero cases. |
| Training state/resume | Immutable typed run state includes current/best skills and scores, memory, history, usage counters, seed, completed position, and split-data/configuration fingerprint. Async checkpoints are awaited before every model/evaluator call and safe boundary. Uncertain in-flight operations are marked unsafe and rejected on resume; only completed step/epoch boundaries resume. |
| Cancellation | Cancellation tokens flow through target and optimizer `IChatClient` calls and evaluator calls. |

## Known deviations and exclusions

This is a reusable text-space library port, not a port of the complete Python research repository.

- Candidate ranking uses one normalized numeric objective metric. Upstream supports separate hard and soft scores, mixed ranking policies, metric-specific caches, and optional semantic-density weighting. The package exposes all evaluator evidence and an application-owned veto callback, but does not implement upstream multi-objective aggregation itself.
- `FullRewriteMinibatch` and `rewrite_from_suggestions`'s full upstream configurable reasoning/token controls are not reproduced. `FullRewrite` issues one whole-step rewrite request; `RewriteFromSuggestions` uses the bounded patch selector then materializes the selected edits.
- The Python benchmark datasets/loaders, built-in benchmark adapters, arbitrary environment lifecycle hooks, task-type stratification, multi-turn agent execution harnesses, and domain-specific rollout/evaluator code are not included. The package takes explicit chat cases and executes one target response per case.
- Python SkillOpt-Sleep, session harvesting/replay, plugin integrations, MCP server, WebUI, CLI programs, config-file ecosystem, and research experiment runner are not included.
- Upstream's filesystem run directories, per-stage reports, prediction caches, crash recovery, and multi-process/concurrent analyst workers are not reproduced. C# returns typed per-case evidence and caller-persisted checkpoints; uncertain model effects are never silently replayed.
- Upstream-specific failure taxonomy, support-count bookkeeping, optimizer-token usage accounting, model/backend routers, model-freeze verification, and final unseen split workflow are not implemented. A host must keep the target client's model configuration stable for the duration of a run.
- Failure/success classification follows the normalized objective boundary: only 1.0 is a success. Callers requiring different semantics should encode that policy in the selected metric's range/direction and application-owned candidate gate.

The package makes no claim of full feature or benchmark parity. Re-evaluate this matrix against upstream whenever the pinned base is advanced.

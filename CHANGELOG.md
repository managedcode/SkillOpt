# Changelog

Changes to the ManagedCode.SkillOpt .NET port are documented here. This file
covers the NuGet package only; the original Microsoft Python project's history
remains in the [upstream repository](https://github.com/microsoft/SkillOpt).

## 0.1.3 — 2026-10-03

- Correct the packaged README installation example to reference the released 0.1.3 package.

## 0.1.2 — 2026-10-03

- Preserve the official evaluator's typed interpretation-failed flag in per-case
  metric evidence so application quality gates can reject failed interpretations.
- Keep checkpoints limited to scalar optimizer progress; metric evidence remains
  in selection and split reports.

## 0.1.1 — 2026-10-03

- Add an optional typed `TargetMessageFactory` that receives frozen case messages
  and candidate skill text separately for every target rollout.

## 0.1.0

- Initial .NET package release of the in-process text-space SkillOpt optimizer.

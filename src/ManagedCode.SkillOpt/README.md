# ManagedCode.SkillOpt

ManagedCode.SkillOpt is the .NET 10 in-process C# port of Microsoft's original Python [SkillOpt](https://github.com/microsoft/SkillOpt) text-space optimizer. It uses a caller-supplied target `IChatClient`, a separate optimizer `IChatClient`, and Microsoft's `IEvaluator` contract. No Python runtime, subprocess, hosted optimizer, provider SDK, or custom model HTTP transport is included.

## Install

```xml
<PackageReference Include="ManagedCode.SkillOpt" Version="0.1.3" />
```

## Run an optimization

Create required, disjoint training and selection case sets, plus an optional disjoint test set. A case contains its stable id, the chat history for the task, any typed `EvaluationContext` values required by the evaluator, and a stable `ContentFingerprint` covering the complete prompt and evaluation context. Set `RunIdentity` to a stable identifier covering the target/optimizer models, evaluator profile, candidate-gate policy, and (when customized) target message-factory behavior. Configure a named `NumericMetric`, its expected range, and whether high or low values are better. Keep the target client fixed for the complete run; SkillOpt uses the same instance for every skill candidate.

```csharp
var result = await SkillOptOptimizer.OptimizeAsync(new SkillOptRequest
{
    InitialSkill = skillMarkdown,
    TrainingCases = trainingCases,
    SelectionCases = validationCases,
    TestCases = testCases,
    TargetChatClient = frozenTargetClient,
    // Optional: candidate and frozen case messages are passed separately on every rollout.
    // Omit this property to preserve the built-in system-message composition behavior.
    TargetMessageFactory = (caseMessages, candidateSkill) =>
        [new ChatMessage(ChatRole.System, candidateSkill), .. caseMessages],
    OptimizerChatClient = optimizerClient,
    Evaluator = evaluator,
    EvaluationChatConfiguration = evaluatorChatConfiguration,
    RunIdentity = "target-model-profile|optimizer-model-profile|evaluator-profile|privacy-policy-v1",
    CandidateGate = candidate => candidate.SelectionReport.Cases.All(IsPrivacySafe),
    Options = new SkillOptOptions
    {
        Epochs = 3,
        StepsPerEpoch = 4,
        BatchSize = 8,
        MaxRollouts = 500,
        MaxOptimizerCalls = 100,
        MaxEvaluationCalls = 500,
        MaxEditsPerStep = 4,
        MaxRejectedEdits = 32,
        ScoreMetricName = "quality",
        MetricMinimum = 0,
        MetricMaximum = 1,
        UpdateMode = SkillUpdateMode.Patch,
        EditBudgetSchedule = SkillEditBudgetSchedule.Cosine
    },
    Checkpoint = async (state, token) => await SaveCheckpointAsync(state, token)
}, cancellationToken);

var bestSkillMarkdown = result.BestSkill;
var heldOutEvidence = result.BestTest;
```

`SaveCheckpointAsync` should return only after the run state is durably stored. The callback receives reserved usage before an external call, then safe step/epoch state, and finally the completed state after final reports are ready. `IsPrivacySafe` is application-owned policy and can inspect the raw named metrics in each case:

```csharp
static bool IsPrivacySafe(SkillOptCaseScore score) =>
    score.Metrics.Single(metric => metric.Name == "privacy-events").NumericValue == 0;
```

The selection split gates every candidate. Set `ScoreDirection` to `LowerIsBetter` for metrics such as privacy-event counts; the optimizer normalizes both directions to a higher-is-better objective. Every final per-case report preserves all official evaluator metrics and their typed numeric, boolean, or string values, reasons, interpretations, and diagnostics. An optional `CandidateGate` receives the complete selection evidence and may reject a candidate even when its mean objective score improves. Include the identity/version of that policy in `RunIdentity`.

`TargetMessageFactory` is called for initial, training, selection, and final test rollouts. It receives the untouched case message list and the current candidate skill as separate arguments; it returns the exact target conversation. Use it when candidate skill must be a distinct System message while the case's frozen System context remains intact. The factory does not change evaluator contexts or optimizer prompt content. Its stable behavior/version belongs in `RunIdentity` so a resume cannot silently change target composition.

The test split is optional and, when supplied, is evaluated only for final baseline and best-skill reporting. An omitted test split returns reports with `CaseCount = 0`, `MeanScore = null`, and no case rows; it never reports a fabricated zero score. Split ids and full-content fingerprints must be disjoint. `ContentFingerprint` lets hosts include non-text and evaluator-context data without putting hidden references in the optimizer prompt. `RunIdentity` is combined with the initial skill, all split identities, and optimization options to reject incompatible resumes. The async checkpoint callback is awaited before every target, optimizer, or evaluator call and at safe boundaries. It records reserved usage before the external call; an interrupted in-flight operation is marked unsafe to resume because its effect may be uncertain. Safe checkpoints are emitted at completed step/epoch boundaries and can be passed back through `ResumeState`. Use the same cases, client/model configuration, options, policy, and seed when resuming.

## Update modes

- `Patch` reflects on failure and success rollouts, hierarchically aggregates edit patches, selects up to the scheduled edit budget, and applies exact-anchor add, insert, replace, or delete edits.
- `RewriteFromSuggestions` follows the patch path and asks the optimizer to materialize the selected suggestions as a complete replacement document.
- `FullRewrite` asks the optimizer for a complete skill document from each step's trajectories.

The package owns candidate generation and gating. The host owns client construction, evaluator policy, data collection, persistence, billing, and lifecycle.

See the [architecture](../../docs/Architecture.md) and [upstream parity matrix](../../docs/Parity.md) before relying on behavior outside these modes.

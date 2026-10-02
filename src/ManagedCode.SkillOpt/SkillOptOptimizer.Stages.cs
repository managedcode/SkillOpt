using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ManagedCode.SkillOpt;

public static partial class SkillOptOptimizer
{
    private static SkillOptRunState CreateRunState(
        SkillOptOptions options, string currentSkill, string bestSkill, double currentScore,
        double bestScore, int bestStep, string metaSkill, IReadOnlyList<string> rejected,
        IReadOnlyList<SkillOptStepRecord> history, int completedSteps, int completedEpochs,
        SkillOptRunPhase phase, string epochStartSkill, RunCounters counters, string fingerprint, bool resumeSafe) => new()
        {
            Seed = options.RandomSeed,
            CompletedSteps = completedSteps,
            CompletedEpochs = completedEpochs,
            Phase = phase,
            IsResumeSafe = resumeSafe,
            EpochStartSkill = epochStartSkill,
            CurrentSkill = currentSkill,
            BestSkill = bestSkill,
            CurrentScore = currentScore,
            BestScore = bestScore,
            BestStep = bestStep,
            MetaSkill = metaSkill,
            RejectedEdits = rejected.ToArray(),
            History = history.ToArray(),
            RolloutsUsed = counters.Rollouts,
            OptimizerCallsUsed = counters.OptimizerCalls,
            EvaluationCallsUsed = counters.EvaluationCalls,
            DatasetFingerprint = fingerprint
        };

    private static async Task<SkillOptRunState> InitializeAsync(
        SkillOptRequest request,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var counters = new RunCounters();
        async ValueTask SaveInitializationCheckpointAsync(CancellationToken token)
        {
            var reserved = new SkillOptRunState
            {
                Seed = request.Options.RandomSeed,
                CompletedSteps = 0,
                CompletedEpochs = 0,
                Phase = SkillOptRunPhase.Initializing,
                IsResumeSafe = false,
                EpochStartSkill = request.InitialSkill,
                CurrentSkill = request.InitialSkill,
                BestSkill = request.InitialSkill,
                CurrentScore = 0,
                BestScore = 0,
                BestStep = 0,
                MetaSkill = string.Empty,
                RejectedEdits = [],
                History = [],
                RolloutsUsed = counters.Rollouts,
                OptimizerCallsUsed = counters.OptimizerCalls,
                EvaluationCallsUsed = counters.EvaluationCalls,
                DatasetFingerprint = fingerprint
            };
            await SaveExternalCheckpointAsync(request, reserved, token);
        }

        counters.Checkpoint = SaveInitializationCheckpointAsync;
        var baseline = await EvaluateSplitAsync(request, request.SelectionCases,
            request.InitialSkill, "selection-baseline", counters, cancellationToken);
        var score = OptimizationHelpers.Mean(baseline);
        var initialized = new SkillOptRunState
        {
            Seed = request.Options.RandomSeed,
            CompletedSteps = 0,
            CompletedEpochs = 0,
            Phase = SkillOptRunPhase.Training,
            IsResumeSafe = true,
            EpochStartSkill = request.InitialSkill,
            CurrentSkill = request.InitialSkill,
            BestSkill = request.InitialSkill,
            CurrentScore = score,
            BestScore = score,
            BestStep = 0,
            MetaSkill = string.Empty,
            RejectedEdits = [],
            History = [],
            RolloutsUsed = counters.Rollouts,
            OptimizerCallsUsed = counters.OptimizerCalls,
            EvaluationCallsUsed = counters.EvaluationCalls,
            DatasetFingerprint = fingerprint
        };
        await SaveExternalCheckpointAsync(request, initialized, cancellationToken);
        return initialized;
    }

    private static async Task<double[]> EvaluateSplitAsync(
        SkillOptRequest request,
        IReadOnlyList<SkillOptCase> cases,
        string skill,
        string split,
        RunCounters counters,
        CancellationToken cancellationToken)
    {
        _ = split;
        if (counters.Rollouts + cases.Count > request.Options.MaxRollouts ||
            counters.EvaluationCalls + cases.Count > request.Options.MaxEvaluationCalls)
        {
            throw new SkillOptBudgetExceededException("rollout or evaluation");
        }

        var observations = await EvaluateCasesAsync(request, cases, skill, counters, cancellationToken);
        return observations.Select(static observation => observation.Score).ToArray();
    }

    private static async Task<SkillOptSplitReport> EvaluateSplitReportAsync(
        SkillOptRequest request,
        IReadOnlyList<SkillOptCase> cases,
        string skill,
        string split,
        RunCounters counters,
        CancellationToken cancellationToken)
    {
        var observations = await EvaluateCasesAsync(request, cases, skill, counters, cancellationToken);
        return OptimizationHelpers.Report(split, cases, observations, request.Options.ScoreMetricName);
    }

    private static async Task<List<PatchBundle>> ReflectAsync(
        SkillOptRequest request,
        IReadOnlyList<(SkillOptCase item, CaseObservation observation)> cases,
        string skill,
        SkillOptOutcome outcome,
        string metaSkill,
        List<string> rejected,
        RunCounters counters,
        CancellationToken cancellationToken)
    {
        var patches = new List<PatchBundle>();
        foreach (var batch in cases.Chunk(request.Options.BatchSize))
        {
            var context = $"## Current Skill\n{skill}\n\n## Edit Budget\nAt most {request.Options.MaxEditsPerStep} edits.\n\n" +
                (string.IsNullOrWhiteSpace(metaSkill) ? "" : $"## Meta Skill Memory\n{metaSkill}\n\n") +
                (rejected.Count == 0 ? "" : $"## Previously Rejected Changes\n{string.Join("\n", rejected.TakeLast(request.Options.MaxRejectedEdits))}\n\n") +
                $"## {outcome} Trajectories\n{FormatTrajectories(batch, skill)}";
            var response = await CallOptimizerAsync(request, counters,
                OptimizationHelpers.Prompt("reflect"), context, cancellationToken);
            try
            {
                var json = OptimizationHelpers.ReadJson(response);
                var edits = OptimizationHelpers.ParseEdits(json, outcome);
                if (edits.Count > 0)
                {
                    patches.Add(new PatchBundle(edits, OptimizationHelpers.ReadReason(json) ?? ""));
                }
            }
            catch (JsonException)
            {
                // An unusable reflection contributes no candidate patch; other minibatches continue.
            }
        }

        return patches;
    }

    private static async Task<PatchBundle> AggregateAsync(
        SkillOptRequest request,
        string skill,
        IReadOnlyList<PatchBundle> patches,
        string metaSkill,
        RunCounters counters,
        CancellationToken cancellationToken)
    {
        var current = patches.ToList();
        while (current.Count > 1)
        {
            var next = new List<PatchBundle>();
            foreach (var batch in current.Chunk(request.Options.MergeBatchSize))
            {
                if (batch.Length == 1)
                {
                    next.Add(batch[0]);
                    continue;
                }

                var serialized = JsonSerializer.Serialize(batch.Select(static patch => new
                {
                    reasoning = patch.Reasoning,
                    edits = patch.Edits
                }));
                var user = $"## Current Skill\n{skill}\n\n## Patches to merge\n{serialized}\n\n" +
                    (string.IsNullOrWhiteSpace(metaSkill) ? "" : $"## Meta Skill Memory\n{metaSkill}");
                var response = await CallOptimizerAsync(request, counters,
                    OptimizationHelpers.Prompt("aggregate"), user, cancellationToken);
                try
                {
                    var json = OptimizationHelpers.ReadJson(response);
                    next.Add(new PatchBundle(OptimizationHelpers.ParseEdits(json, null),
                        OptimizationHelpers.ReadReason(json) ?? ""));
                }
                catch (JsonException)
                {
                    next.Add(new PatchBundle(batch.SelectMany(static patch => patch.Edits).ToList(),
                        "Aggregation fallback kept the input patches."));
                }
            }

            current = next;
        }

        return current[0];
    }

    private static async Task<List<SkillOptEdit>> SelectEditsAsync(
        SkillOptRequest request,
        string skill,
        List<SkillOptEdit> edits,
        int budget,
        string metaSkill,
        RunCounters counters,
        CancellationToken cancellationToken)
    {
        if (edits.Count <= budget)
        {
            return edits.ToList();
        }

        var candidates = JsonSerializer.Serialize(edits.Select((edit, index) => new
        {
            index,
            operation = edit.Operation.ToString(),
            edit.Target,
            edit.Content,
            edit.SupportCount
        }));
        var user = $"## Current Skill\n{skill}\n\n## Edit Pool (budget {budget})\n{candidates}\n\n" +
            (string.IsNullOrWhiteSpace(metaSkill) ? "" : $"## Meta Skill Memory\n{metaSkill}");
        try
        {
            var response = await CallOptimizerAsync(request, counters,
                OptimizationHelpers.Prompt("select"), user, cancellationToken);
            using var document = JsonDocument.Parse(OptimizationHelpers.ReadJson(response));
            if (document.RootElement.TryGetProperty("selected_indices", out var indexes) && indexes.ValueKind == JsonValueKind.Array)
            {
                var selected = new List<SkillOptEdit>();
                var seen = new HashSet<int>();
                foreach (var value in indexes.EnumerateArray())
                {
                    if (value.TryGetInt32(out var index) && index >= 0 && index < edits.Count && seen.Add(index))
                    {
                        selected.Add(edits[index]);
                        if (selected.Count == budget)
                        {
                            break;
                        }
                    }
                }

                if (selected.Count > 0)
                {
                    return selected;
                }
            }
        }
        catch (JsonException)
        {
            // Preserve the deterministic upstream-style truncation fallback below.
        }

        return edits.Take(budget).ToList();
    }

    private static async Task<SlowUpdateOutcome> ApplySlowUpdateAsync(
        SkillOptRequest request,
        string response,
        string priorSkill,
        string currentSkill,
        double currentScore,
        string bestSkill,
        double bestScore,
        int bestStep,
        int step,
        List<string> rejected,
        List<SkillOptStepRecord> history,
        RunCounters counters,
        CancellationToken cancellationToken)
    {
        var json = OptimizationHelpers.ReadJson(response);
        var edits = OptimizationHelpers.ParseEdits(json, null);
        var candidate = OptimizationHelpers.ApplyEdits(currentSkill, edits, out var applied);
        if (applied == 0 || string.Equals(candidate, currentSkill, StringComparison.Ordinal))
        {
            return new SlowUpdateOutcome(currentSkill, currentScore, bestSkill, bestScore, bestStep);
        }

        var selectionReport = await EvaluateSplitReportAsync(request, request.SelectionCases, candidate,
            "selection-slow-update", counters, cancellationToken);
        var candidateScore = selectionReport.MeanScore!.Value;
        var beforeSkill = currentSkill;
        var action = Gate(candidate, candidateScore, ref currentSkill, ref currentScore,
            ref bestSkill, ref bestScore, ref bestStep, step, request.Options,
            request.CandidateGate, selectionReport);
        if (action.StartsWith("reject", StringComparison.Ordinal))
        {
            RememberRejected(rejected, candidate, currentSkill, request.Options.MaxRejectedEdits);
        }
        history.Add(new SkillOptStepRecord(step, request.Options.StepsPerEpoch,
            0, 0, 0, request.Options.MaxEditsPerStep, currentScore, candidateScore,
            $"slow_update_{action}", applied,
            $"Compared epoch-start skill length {priorSkill.Length} with current skill length {beforeSkill.Length}.",
            OptimizationHelpers.ReadReason(json) ?? "Epoch-level slow update"));
        return new SlowUpdateOutcome(currentSkill, currentScore, bestSkill, bestScore, bestStep);
    }

    private static async Task<string> CallOptimizerAsync(
        SkillOptRequest request,
        RunCounters counters,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        if (counters.OptimizerCalls >= request.Options.MaxOptimizerCalls)
        {
            throw new SkillOptBudgetExceededException("optimizer call");
        }

        counters.OptimizerCalls++;
        if (counters.Checkpoint is not null)
        {
            await counters.Checkpoint(cancellationToken);
        }
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt)
        };
        var response = await request.OptimizerChatClient.GetResponseAsync(messages,
            request.OptimizerChatOptions, cancellationToken);
        return response.Text;
    }

}

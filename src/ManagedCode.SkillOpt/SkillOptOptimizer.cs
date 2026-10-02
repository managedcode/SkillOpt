using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ManagedCode.SkillOpt;

/// <summary>Runs SkillOpt's text-space optimization loop using caller-supplied Microsoft clients.</summary>
public static partial class SkillOptOptimizer
{
    /// <summary>Optimizes one skill and returns the best skill plus held-out evidence.</summary>
    public static async Task<SkillOptResult> OptimizeAsync(SkillOptRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var options = request.Options;
        var fingerprint = OptimizationHelpers.Fingerprint(request);
        var state = request.ResumeState ?? await InitializeAsync(request, fingerprint, cancellationToken);
        ValidateResume(state, request, fingerprint);
        await SaveExternalCheckpointAsync(request, state, cancellationToken);
        var history = state.History.ToList();
        var rejected = state.RejectedEdits.ToList();
        var counters = new RunCounters(state.RolloutsUsed, state.OptimizerCallsUsed, state.EvaluationCallsUsed);
        var currentSkill = state.CurrentSkill;
        var bestSkill = state.BestSkill;
        var currentScore = state.CurrentScore;
        var bestScore = state.BestScore;
        var bestStep = state.BestStep;
        var metaSkill = state.MetaSkill;
        var completedSteps = state.CompletedSteps;
        var completedEpochs = state.CompletedEpochs;
        var phase = state.Phase;
        var epochStartSkill = state.EpochStartSkill;
        var resumeSafe = state.IsResumeSafe;
        async ValueTask SaveCheckpointAsync(bool? safe = null)
        {
            if (safe.HasValue)
            {
                resumeSafe = safe.Value;
            }

            await SaveExternalCheckpointAsync(request, CreateRunState(options, currentSkill,
            bestSkill, currentScore, bestScore, bestStep, metaSkill, rejected, history,
            completedSteps, completedEpochs, phase, epochStartSkill, counters, fingerprint, resumeSafe), cancellationToken);
        }

        counters.Checkpoint = async token => await SaveCheckpointAsync(false);

        async Task CompleteEpochBoundaryAsync()
        {
            if (phase == SkillOptRunPhase.EpochMetaUpdate)
            {
                var memoryResponse = await CallOptimizerAsync(request, counters,
                    OptimizationHelpers.Prompt("meta"), FormatEpochMemory(history, metaSkill), cancellationToken);
                if (!string.IsNullOrWhiteSpace(memoryResponse))
                {
                    metaSkill = memoryResponse.Trim();
                }

                phase = SkillOptRunPhase.EpochSlowUpdate;
                await SaveCheckpointAsync(true);
            }

            if (phase == SkillOptRunPhase.EpochSlowUpdate)
            {
                var slowPatch = await CallOptimizerAsync(request, counters,
                    OptimizationHelpers.Prompt("slow_update"), FormatSlowUpdate(epochStartSkill,
                        currentSkill, history.Where(record => record.Epoch == completedEpochs).ToArray(), metaSkill),
                    cancellationToken);
                var slowUpdate = await ApplySlowUpdateAsync(request, slowPatch, epochStartSkill,
                    currentSkill, currentScore, bestSkill, bestScore, bestStep, completedSteps,
                    rejected, history, counters, cancellationToken);
                currentSkill = slowUpdate.CurrentSkill;
                currentScore = slowUpdate.CurrentScore;
                bestSkill = slowUpdate.BestSkill;
                bestScore = slowUpdate.BestScore;
                bestStep = slowUpdate.BestStep;
                phase = SkillOptRunPhase.Training;
                epochStartSkill = currentSkill;
                await SaveCheckpointAsync(true);
            }
        }

        if (phase is SkillOptRunPhase.EpochMetaUpdate or SkillOptRunPhase.EpochSlowUpdate)
        {
            await CompleteEpochBoundaryAsync();
        }

        for (var epoch = completedEpochs; epoch < options.Epochs; epoch++)
        {
            var epochStart = currentSkill;
            var firstStep = epoch == completedEpochs ? completedSteps % options.StepsPerEpoch : 0;
            for (var stepInEpoch = firstStep; stepInEpoch < options.StepsPerEpoch; stepInEpoch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = completedSteps + 1;
                var trainCases = SelectBatch(request.TrainingCases, options.BatchSize,
                    options.RandomSeed + step);
                var beforeScore = currentScore;
                var observations = await EvaluateCasesAsync(request, trainCases, currentSkill,
                    counters, cancellationToken);
                var rolloutScores = observations.Select(static observation => observation.Score).ToArray();
                var outcomeScores = trainCases.Select((item, index) =>
                    (item, observation: observations[index])).ToArray();
                var failures = outcomeScores.Where(static row => row.observation.Score < 1).ToArray();
                var successes = outcomeScores.Where(static row => row.observation.Score >= 1).ToArray();
                var edits = new List<SkillOptEdit>();
                var reasoning = "";

                if (options.UpdateMode == SkillUpdateMode.FullRewrite)
                {
                    var response = await CallOptimizerAsync(request, counters, OptimizationHelpers.Prompt("rewrite"),
                        FormatTrajectories(outcomeScores.Select(static row => (row.item, row.observation)), currentSkill), cancellationToken);
                    var replacement = response.Trim();
                    if (replacement.StartsWith("```", StringComparison.Ordinal))
                    {
                        replacement = StripCodeFence(replacement);
                    }

                    if (replacement.Length > 0)
                    {
                        var selectionReport = await EvaluateSplitReportAsync(request, request.SelectionCases,
                                replacement, "selection", counters, cancellationToken);
                        var selectedScore = selectionReport.MeanScore!.Value;
                        var action = Gate(replacement, selectedScore, ref currentSkill, ref currentScore,
                            ref bestSkill, ref bestScore, ref bestStep, step, options,
                            request.CandidateGate, selectionReport);
                        if (action.StartsWith("reject", StringComparison.Ordinal))
                        {
                            RememberRejected(rejected, currentSkill, replacement, options.MaxRejectedEdits);
                        }

                        history.Add(new SkillOptStepRecord(step, epoch + 1, trainCases.Length,
                            failures.Length, successes.Length, 0, beforeScore, selectedScore, action, 0,
                            SummarizeFailures(outcomeScores), "Full skill rewrite"));
                    }
                }
                else
                {
                    var failurePatches = await ReflectAsync(request, failures, currentSkill,
                        SkillOptOutcome.Failure, metaSkill, rejected, counters, cancellationToken);
                    var successPatches = options.FailureOnly ? [] : await ReflectAsync(request,
                        successes, currentSkill, SkillOptOutcome.Success, metaSkill, rejected,
                        counters, cancellationToken);
                    var allPatches = failurePatches.Concat(successPatches).ToList();
                    if (allPatches.Count > 0)
                    {
                        var aggregate = await AggregateAsync(request, currentSkill, allPatches,
                            metaSkill, counters, cancellationToken);
                        reasoning = aggregate.Reasoning;
                        var budget = GetEditBudget(options, step, options.Epochs * options.StepsPerEpoch);
                        var selected = await SelectEditsAsync(request, currentSkill, aggregate.Edits,
                            budget, metaSkill, counters, cancellationToken);
                        var candidate = OptimizationHelpers.ApplyEdits(currentSkill, selected, out var applied);
                        if (options.UpdateMode == SkillUpdateMode.RewriteFromSuggestions && applied > 0)
                        {
                            candidate = await CallOptimizerAsync(request, counters,
                                OptimizationHelpers.Prompt("rewrite_suggestions"),
                                $"## Current Skill\n{currentSkill}\n\n## Selected Suggestions\n{JsonSerializer.Serialize(selected)}\n\n" + metaSkill,
                                cancellationToken);
                        }

                        if (applied > 0 && !string.Equals(candidate, currentSkill, StringComparison.Ordinal))
                        {
                            var selectionReport = await EvaluateSplitReportAsync(request,
                                request.SelectionCases, candidate, "selection", counters, cancellationToken);
                            var selectedScore = selectionReport.MeanScore!.Value;
                            var action = Gate(candidate, selectedScore, ref currentSkill, ref currentScore,
                                ref bestSkill, ref bestScore, ref bestStep, step, options,
                                request.CandidateGate, selectionReport);
                            if (action.StartsWith("reject", StringComparison.Ordinal))
                            {
                                RememberRejected(rejected, candidate, currentSkill, options.MaxRejectedEdits);
                            }

                            history.Add(new SkillOptStepRecord(step, epoch + 1, trainCases.Length,
                                failures.Length, successes.Length, budget, beforeScore, selectedScore,
                                action, applied, SummarizeFailures(outcomeScores), reasoning));
                        }
                        else
                        {
                            history.Add(new SkillOptStepRecord(step, epoch + 1, trainCases.Length,
                                failures.Length, successes.Length, budget, beforeScore, beforeScore,
                                "skip_no_patches", 0, SummarizeFailures(outcomeScores), reasoning));
                        }
                    }
                    else
                    {
                        history.Add(new SkillOptStepRecord(step, epoch + 1, trainCases.Length,
                            failures.Length, successes.Length, GetEditBudget(options, step,
                                options.Epochs * options.StepsPerEpoch), beforeScore, beforeScore,
                            "skip_no_patches", 0, SummarizeFailures(outcomeScores), "No usable edits."));
                    }
                }

                completedSteps++;
                completedEpochs = completedSteps / options.StepsPerEpoch;
                var completedEpoch = completedSteps % options.StepsPerEpoch == 0;
                phase = completedEpoch && completedEpochs >= 2 && !string.Equals(epochStartSkill, currentSkill, StringComparison.Ordinal)
                    ? SkillOptRunPhase.EpochMetaUpdate
                    : SkillOptRunPhase.Training;
                if (completedEpoch && phase == SkillOptRunPhase.Training)
                {
                    epochStartSkill = currentSkill;
                }

                await SaveCheckpointAsync(true);
            }

            completedEpochs = epoch + 1;
            if (phase is SkillOptRunPhase.EpochMetaUpdate or SkillOptRunPhase.EpochSlowUpdate)
            {
                await CompleteEpochBoundaryAsync();
            }
        }

        phase = SkillOptRunPhase.FinalReporting;
        await SaveCheckpointAsync(false);
        var baselineTestCases = await EvaluateCasesAsync(request, request.TestCases,
            request.InitialSkill, counters, cancellationToken);
        var bestTestCases = await EvaluateCasesAsync(request, request.TestCases,
            bestSkill, counters, cancellationToken);
        var finalState = new SkillOptRunState
        {
            Seed = options.RandomSeed,
            CompletedSteps = completedSteps,
            CompletedEpochs = completedEpochs,
            Phase = SkillOptRunPhase.Completed,
            IsResumeSafe = true,
            EpochStartSkill = currentSkill,
            CurrentSkill = currentSkill,
            BestSkill = bestSkill,
            CurrentScore = currentScore,
            BestScore = bestScore,
            BestStep = bestStep,
            MetaSkill = metaSkill,
            RejectedEdits = rejected,
            History = history,
            RolloutsUsed = counters.Rollouts,
            OptimizerCallsUsed = counters.OptimizerCalls,
            EvaluationCallsUsed = counters.EvaluationCalls,
            DatasetFingerprint = fingerprint
        };

        await SaveExternalCheckpointAsync(request, finalState, cancellationToken);

        return new SkillOptResult(bestSkill, currentSkill,
            OptimizationHelpers.Report("test-baseline", request.TestCases, baselineTestCases, options.ScoreMetricName),
            OptimizationHelpers.Report("test-best", request.TestCases, bestTestCases, options.ScoreMetricName), finalState);
    }

}


/// <summary>Thrown when an explicit optimization, rollout, or evaluation budget is exhausted.</summary>
public sealed class SkillOptBudgetExceededException(string budget)
    : InvalidOperationException($"SkillOpt {budget} budget was exhausted.")
{
    /// <summary>The budget category that prevented the next operation.</summary>
    public string Budget { get; } = budget;
}

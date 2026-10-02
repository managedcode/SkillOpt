using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ManagedCode.SkillOpt;

public static partial class SkillOptOptimizer
{
    private static string Gate(
        string candidate,
        double candidateScore,
        ref string currentSkill,
        ref double currentScore,
        ref string bestSkill,
        ref double bestScore,
        ref int bestStep,
        int step,
        SkillOptOptions options,
        Func<SkillOptCandidateEvaluation, bool>? candidateGate,
        SkillOptSplitReport selectionReport)
    {
        if (candidateGate is not null && !candidateGate(new SkillOptCandidateEvaluation(candidate, selectionReport)))
        {
            return "reject_policy_gate";
        }

        if (candidateScore <= currentScore + options.MinimumImprovement)
        {
            return "reject";
        }

        currentSkill = candidate;
        currentScore = candidateScore;
        if (candidateScore > bestScore)
        {
            bestSkill = candidate;
            bestScore = candidateScore;
            bestStep = step;
            return "accept_new_best";
        }

        return "accept";
    }

    private static string FormatTrajectories(
        IEnumerable<(SkillOptCase item, CaseObservation observation)> items,
        string skill) => JsonSerializer.Serialize(new
        {
            current_skill = skill,
            trajectories = items.Select(static value => new
            {
                id = value.item.Id,
                score = value.observation.Score,
                messages = value.item.Messages.Select(static message => new
                {
                    role = message.Role.Value,
                    text = message.Text
                }),
                response = value.observation.ResponseText
            })
        });

    private static string FormatEpochMemory(IReadOnlyList<SkillOptStepRecord> history, string oldMemory) =>
        $"Existing memory:\n{oldMemory}\n\nRecent optimization steps:\n{JsonSerializer.Serialize(history.TakeLast(32))}";

    private static ValueTask SaveExternalCheckpointAsync(
        SkillOptRequest request,
        SkillOptRunState state,
        CancellationToken cancellationToken) =>
        request.Checkpoint?.Invoke(state, cancellationToken) ?? ValueTask.CompletedTask;

    private static string FormatSlowUpdate(string oldSkill, string currentSkill,
        IReadOnlyList<SkillOptStepRecord> records, string metaSkill) =>
        $"## Previous Epoch Skill\n{oldSkill}\n\n## Current Skill\n{currentSkill}\n\n" +
        $"## Current Meta Skill\n{metaSkill}\n\n## Recent Evidence\n{JsonSerializer.Serialize(records)}";

    private static string SummarizeFailures(IEnumerable<(SkillOptCase item, CaseObservation observation)> items) =>
        string.Join("; ", items.Where(static item => item.observation.Score < 1).Select(static item =>
            $"{item.item.Id}={item.observation.Score:F3}").Take(16));

    private static SkillOptCase[] SelectBatch(IReadOnlyList<SkillOptCase> cases, int size, int seed)
    {
        var random = new Random(seed);
        return cases.OrderBy(_ => random.Next()).Take(size).ToArray();
    }

    private static int GetEditBudget(SkillOptOptions options, int step, int totalSteps)
    {
        if (options.EditBudgetSchedule == SkillEditBudgetSchedule.Constant || totalSteps <= 1)
        {
            return options.MaxEditsPerStep;
        }

        var progress = Math.Clamp((step - 1d) / (totalSteps - 1d), 0, 1);
        var factor = options.EditBudgetSchedule == SkillEditBudgetSchedule.Cosine
            ? (1 + Math.Cos(Math.PI * progress)) / 2
            : 1 - progress;
        return Math.Clamp((int)Math.Round(options.MinimumEditsPerStep +
            (options.MaxEditsPerStep - options.MinimumEditsPerStep) * factor),
            options.MinimumEditsPerStep, options.MaxEditsPerStep);
    }

    private static string StripCodeFence(string response)
    {
        var firstLine = response.IndexOf('\n');
        var end = response.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && end > firstLine ? response[(firstLine + 1)..end].Trim() : response;
    }

    private static void RememberRejected(List<string> rejected, string candidate, string retained, int limit)
    {
        var summary = $"Rejected candidate: {candidate}\nRetained skill: {retained}";
        rejected.Add(summary);
        if (rejected.Count > limit)
        {
            rejected.RemoveRange(0, rejected.Count - limit);
        }
    }

    private static async Task<CaseObservation[]> EvaluateCasesAsync(
        SkillOptRequest request, IReadOnlyList<SkillOptCase> cases, string skill, RunCounters counters,
        CancellationToken cancellationToken)
    {
        if (counters.Rollouts + cases.Count > request.Options.MaxRollouts ||
            counters.EvaluationCalls + cases.Count > request.Options.MaxEvaluationCalls)
        {
            throw new SkillOptBudgetExceededException("rollout or evaluation");
        }

        var observations = new CaseObservation[cases.Count];
        for (var index = 0; index < cases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations[index] = await OptimizationHelpers.EvaluateCaseAsync(request, cases[index], skill,
                cancellationToken,
                async token =>
                {
                    counters.Rollouts++;
                    if (counters.Checkpoint is not null)
                    {
                        await counters.Checkpoint(token);
                    }
                },
                async token =>
                {
                    counters.EvaluationCalls++;
                    if (counters.Checkpoint is not null)
                    {
                        await counters.Checkpoint(token);
                    }
                });
        }

        return observations;
    }

    private static void Validate(SkillOptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InitialSkill);
        ArgumentNullException.ThrowIfNull(request.TargetChatClient);
        ArgumentNullException.ThrowIfNull(request.OptimizerChatClient);
        ArgumentNullException.ThrowIfNull(request.Evaluator);
        ArgumentNullException.ThrowIfNull(request.EvaluationChatConfiguration);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunIdentity);
        request.Options.Validate();
        ValidateCases(request.TrainingCases, nameof(request.TrainingCases));
        ValidateCases(request.SelectionCases, nameof(request.SelectionCases));
        ValidateCases(request.TestCases, nameof(request.TestCases), allowEmpty: true);
        var allCases = request.TrainingCases.Concat(request.SelectionCases).Concat(request.TestCases).ToArray();
        if (allCases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != allCases.Length ||
            allCases.Select(static item => item.ContentFingerprint).Distinct(StringComparer.Ordinal).Count() != allCases.Length)
        {
            throw new ArgumentException("Training, selection, and test splits must have disjoint case ids and content fingerprints.");
        }
    }

    private static void ValidateCases(IReadOnlyList<SkillOptCase> cases, string name, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if ((!allowEmpty && cases.Count == 0) || cases.Any(static item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Messages.Count == 0))
        {
            throw new ArgumentException("Each split must contain identified cases with at least one chat message.", name);
        }

        if (cases.Any(static item => string.IsNullOrWhiteSpace(item.ContentFingerprint)))
        {
            throw new ArgumentException("Each case requires a stable content fingerprint covering its full prompt and evaluation context.", name);
        }

        if (cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
        {
            throw new ArgumentException("Case identifiers must be unique within each split.", name);
        }
    }

    private static void ValidateResume(SkillOptRunState state, SkillOptRequest request, string fingerprint)
    {
        if (!state.IsResumeSafe || state.Phase is SkillOptRunPhase.Initializing or SkillOptRunPhase.FinalReporting or SkillOptRunPhase.Completed)
        {
            throw new ArgumentException("This checkpoint has an uncertain in-flight operation or completed run and cannot be resumed safely.", nameof(request));
        }

        if (state.Seed != request.Options.RandomSeed || !string.Equals(state.DatasetFingerprint,
                fingerprint, StringComparison.Ordinal) || state.CompletedSteps > request.Options.Epochs * request.Options.StepsPerEpoch ||
            state.RolloutsUsed > request.Options.MaxRollouts || state.OptimizerCallsUsed > request.Options.MaxOptimizerCalls ||
            state.EvaluationCallsUsed > request.Options.MaxEvaluationCalls)
        {
            throw new ArgumentException("Resume state does not match this request's data, seed, or budgets.", nameof(request));
        }
    }

    private sealed record PatchBundle(List<SkillOptEdit> Edits, string Reasoning);
    private sealed record SlowUpdateOutcome(string CurrentSkill, double CurrentScore, string BestSkill, double BestScore, int BestStep);

    private sealed class RunCounters(int rollouts = 0, int optimizerCalls = 0, int evaluationCalls = 0)
    {
        public Func<CancellationToken, ValueTask>? Checkpoint { get; set; }
        public int Rollouts { get; set; } = rollouts;
        public int OptimizerCalls { get; set; } = optimizerCalls;
        public int EvaluationCalls { get; set; } = evaluationCalls;
    }
}

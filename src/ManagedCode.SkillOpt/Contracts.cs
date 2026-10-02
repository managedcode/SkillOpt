using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace ManagedCode.SkillOpt;

/// <summary>A stable task case in one of the explicit data splits.</summary>
public sealed record SkillOptCase
{
    /// <summary>Unique caller-supplied identity within and across all splits.</summary>
    public required string Id { get; init; }
    /// <summary>Stable identity for the complete task and evaluation context, including non-text inputs.</summary>
    public required string ContentFingerprint { get; init; }
    /// <summary>Messages sent to the target model before the skill text is added.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    /// <summary>Typed Microsoft.Extensions.AI evaluation context for this case.</summary>
    public IReadOnlyList<EvaluationContext> EvaluationContexts { get; init; } = [];
}

/// <summary>Supported text-space update strategies.</summary>
public enum SkillUpdateMode
{
    /// <summary>Apply selected bounded add, insert, replace, or delete edits directly.</summary>
    Patch,
    /// <summary>Ask the optimizer model to return a complete replacement skill.</summary>
    FullRewrite,
    /// <summary>Apply selected edits, then ask the optimizer model to rewrite the complete skill.</summary>
    RewriteFromSuggestions
}

/// <summary>Controls the edit budget at each optimization step.</summary>
public enum SkillOptRunPhase
{
    /// <summary>Establishing the initial selection score.</summary>
    Initializing,
    /// <summary>Executing bounded optimization steps.</summary>
    Training,
    /// <summary>Updating epoch-level meta memory.</summary>
    EpochMetaUpdate,
    /// <summary>Applying and validating the epoch-level slow update.</summary>
    EpochSlowUpdate,
    /// <summary>Producing optional final held-out reports.</summary>
    FinalReporting,
    /// <summary>All requested optimization and reporting work finished.</summary>
    Completed
}

/// <summary>Schedules the per-step edit count across the run.</summary>
public enum SkillEditBudgetSchedule
{
    /// <summary>Use the maximum edit count at every step.</summary>
    Constant,
    /// <summary>Decrease linearly from the maximum toward the minimum.</summary>
    Linear,
    /// <summary>Decrease on a cosine schedule from the maximum toward the minimum.</summary>
    Cosine
}

/// <summary>Optimization direction for the selected numeric metric.</summary>
public enum SkillOptScoreDirection
{
    /// <summary>Higher raw metric values are better.</summary>
    HigherIsBetter,
    /// <summary>Lower raw metric values are better.</summary>
    LowerIsBetter
}

/// <summary>All explicit limits and selection settings for one run.</summary>
public sealed record SkillOptOptions
{
    /// <summary>Number of training epochs.</summary>
    public required int Epochs { get; init; }
    /// <summary>Optimization steps per epoch.</summary>
    public required int StepsPerEpoch { get; init; }
    /// <summary>Maximum task cases sampled into a rollout batch.</summary>
    public required int BatchSize { get; init; }
    /// <summary>Maximum target-model rollouts, including initialization and held-out reports.</summary>
    public required int MaxRollouts { get; init; }
    /// <summary>Maximum optimizer-model calls.</summary>
    public required int MaxOptimizerCalls { get; init; }
    /// <summary>Maximum evaluator calls.</summary>
    public required int MaxEvaluationCalls { get; init; }
    /// <summary>Maximum selected edits applied in one step.</summary>
    public required int MaxEditsPerStep { get; init; }
    /// <summary>Maximum rejected candidate summaries retained in run memory.</summary>
    public required int MaxRejectedEdits { get; init; }
    /// <summary>Lower bound for scheduled edit counts.</summary>
    public int MinimumEditsPerStep { get; init; } = 1;
    /// <summary>Schedule used to calculate each step's edit limit.</summary>
    public SkillEditBudgetSchedule EditBudgetSchedule { get; init; } = SkillEditBudgetSchedule.Constant;
    /// <summary>Text-space candidate update strategy.</summary>
    public SkillUpdateMode UpdateMode { get; init; } = SkillUpdateMode.Patch;
    /// <summary>When true, reflection includes failure trajectories only.</summary>
    public bool FailureOnly { get; init; }
    /// <summary>Maximum number of candidate patches merged in one aggregation prompt.</summary>
    public int MergeBatchSize { get; init; } = 8;
    /// <summary>Seed for deterministic case-batch selection.</summary>
    public int RandomSeed { get; init; }
    /// <summary>Name of the numeric metric returned by the evaluator.</summary>
    public required string ScoreMetricName { get; init; }
    /// <summary>Whether larger or smaller raw values of the selected metric are preferred.</summary>
    public SkillOptScoreDirection ScoreDirection { get; init; } = SkillOptScoreDirection.HigherIsBetter;
    /// <summary>Inclusive minimum allowed metric value.</summary>
    public double MetricMinimum { get; init; }
    /// <summary>Inclusive maximum allowed metric value.</summary>
    public double MetricMaximum { get; init; } = 1;
    /// <summary>Minimum selection-score gain required to accept a candidate.</summary>
    public double MinimumImprovement { get; init; }

    internal void Validate()
    {
        if (Epochs <= 0 || StepsPerEpoch <= 0 || BatchSize <= 0 || MaxRollouts <= 0 ||
            MaxOptimizerCalls <= 0 || MaxEvaluationCalls <= 0 || MaxEditsPerStep <= 0 || MaxRejectedEdits <= 0 ||
            MinimumEditsPerStep <= 0 || MinimumEditsPerStep > MaxEditsPerStep || MergeBatchSize < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(SkillOptOptions), "All run limits must be positive and internally consistent.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ScoreMetricName);
        if (!double.IsFinite(MetricMinimum) || !double.IsFinite(MetricMaximum) || MetricMaximum <= MetricMinimum)
        {
            throw new ArgumentException("MetricMaximum must be finite and greater than MetricMinimum.");
        }

        if (!double.IsFinite(MinimumImprovement) || MinimumImprovement < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumImprovement));
        }

        if (!Enum.IsDefined(UpdateMode) || !Enum.IsDefined(EditBudgetSchedule) || !Enum.IsDefined(ScoreDirection))
        {
            throw new ArgumentOutOfRangeException(nameof(SkillOptOptions), "UpdateMode, EditBudgetSchedule, and ScoreDirection must be supported values.");
        }

        if ((long)Epochs * StepsPerEpoch > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(SkillOptOptions), "Epochs multiplied by StepsPerEpoch must fit in a 32-bit step count.");
        }
    }
}

/// <summary>One exact, supported text edit proposed by the optimizer.</summary>
public sealed record SkillOptEdit
{
    /// <summary>Text edit operation to apply.</summary>
    public required SkillOptEditOperation Operation { get; init; }
    /// <summary>Exact existing text anchor used by insert, replace, or delete.</summary>
    public string Target { get; init; } = string.Empty;
    /// <summary>New text appended, inserted, or substituted.</summary>
    public string Content { get; init; } = string.Empty;
    /// <summary>Number of supporting trajectories reported by reflection.</summary>
    public int SupportCount { get; init; }
    /// <summary>Outcome class from which this edit was reflected, if known.</summary>
    public SkillOptOutcome? SourceOutcome { get; init; }
}

/// <summary>Bounded exact-text operations supported by the optimizer.</summary>
public enum SkillOptEditOperation
{
    /// <summary>Append content to the end of the skill.</summary>
    Append,
    /// <summary>Insert content immediately after an exact anchor.</summary>
    InsertAfter,
    /// <summary>Replace an exact anchor with content.</summary>
    Replace,
    /// <summary>Delete an exact anchor.</summary>
    Delete
}

/// <summary>Trajectory outcome category passed to reflection prompts.</summary>
public enum SkillOptOutcome
{
    /// <summary>The target task did not meet the full-score threshold.</summary>
    Failure,
    /// <summary>The target task met the full-score threshold.</summary>
    Success
}

/// <summary>Typed optimizer memory retained across optimization steps and resume.</summary>
public sealed record SkillOptRunState
{
    /// <summary>Random seed used for deterministic batch selection.</summary>
    public required int Seed { get; init; }
    /// <summary>Number of fully completed training steps.</summary>
    public required int CompletedSteps { get; init; }
    /// <summary>Number of fully completed epochs.</summary>
    public required int CompletedEpochs { get; init; }
    /// <summary>Current resumable phase or uncertain-operation phase.</summary>
    public required SkillOptRunPhase Phase { get; init; }
    /// <summary>False means the last checkpoint reserves an operation whose outcome is uncertain; it cannot be resumed.</summary>
    public required bool IsResumeSafe { get; init; }
    /// <summary>Skill at the start of the active epoch.</summary>
    public required string EpochStartSkill { get; init; }
    /// <summary>Current accepted skill text.</summary>
    public required string CurrentSkill { get; init; }
    /// <summary>Best selection-scoring skill observed so far.</summary>
    public required string BestSkill { get; init; }
    /// <summary>Selection score of the current skill.</summary>
    public required double CurrentScore { get; init; }
    /// <summary>Best selection score observed so far.</summary>
    public required double BestScore { get; init; }
    /// <summary>Training step at which the best skill was accepted.</summary>
    public required int BestStep { get; init; }
    /// <summary>Epoch-level optimizer memory.</summary>
    public required string MetaSkill { get; init; }
    /// <summary>Bounded rejected-candidate memory.</summary>
    public required IReadOnlyList<string> RejectedEdits { get; init; }
    /// <summary>Completed step-level evidence.</summary>
    public required IReadOnlyList<SkillOptStepRecord> History { get; init; }
    /// <summary>Target-model rollouts already reserved or completed.</summary>
    public required int RolloutsUsed { get; init; }
    /// <summary>Optimizer-model calls already reserved or completed.</summary>
    public required int OptimizerCallsUsed { get; init; }
    /// <summary>Evaluator calls already reserved or completed.</summary>
    public required int EvaluationCallsUsed { get; init; }
    /// <summary>Canonical identity of run configuration, splits, and initial skill.</summary>
    public required string DatasetFingerprint { get; init; }
}

/// <summary>Evidence recorded for one completed optimization step.</summary>
public sealed record SkillOptStepRecord(
    int Step,
    int Epoch,
    int TrainCaseCount,
    int Failures,
    int Successes,
    int EditBudget,
    double BeforeScore,
    double CandidateScore,
    string Action,
    int AppliedEdits,
    string FailureSummary,
    string OptimizerReasoning);

/// <summary>Measured score report for a data split; mean is null when no cases were supplied.</summary>
public sealed record SkillOptSplitReport(
    string Split,
    int CaseCount,
    double? MeanScore,
    IReadOnlyList<SkillOptCaseScore> Cases);

/// <summary>Measured score and evaluator reason for one case.</summary>
public sealed record SkillOptCaseScore(
    string CaseId,
    double Score,
    string? MetricReason,
    IReadOnlyList<SkillOptMetricEvidence> Metrics);

/// <summary>Typed, per-case projection of an official Microsoft evaluation metric.</summary>
public sealed record SkillOptMetricEvidence(
    string Name,
    string MetricType,
    double? NumericValue,
    bool? BooleanValue,
    string? TextValue,
    string? Reason,
    string? Interpretation,
    IReadOnlyList<SkillOptEvaluationDiagnostic> Diagnostics);

/// <summary>Candidate text plus all official evaluator evidence from its selection split.</summary>
public sealed record SkillOptCandidateEvaluation(string CandidateSkill, SkillOptSplitReport SelectionReport);

/// <summary>Diagnostic emitted by Microsoft.Extensions.AI.Evaluation for a metric.</summary>
public sealed record SkillOptEvaluationDiagnostic(EvaluationDiagnosticSeverity Severity, string Message);

/// <summary>Best and final skill artifacts, split evidence, and resumable state.</summary>
public sealed record SkillOptResult(
    string BestSkill,
    string CurrentSkill,
    SkillOptSplitReport BaselineTest,
    SkillOptSplitReport BestTest,
    SkillOptRunState State);

/// <summary>One optimization run, including all three data splits and caller-owned AI clients.</summary>
public sealed record SkillOptRequest
{
    /// <summary>Initial Markdown skill text to optimize.</summary>
    public required string InitialSkill { get; init; }
    /// <summary>Cases used for target rollouts and reflection.</summary>
    public required IReadOnlyList<SkillOptCase> TrainingCases { get; init; }
    /// <summary>Held-out cases used to accept or reject candidates.</summary>
    public required IReadOnlyList<SkillOptCase> SelectionCases { get; init; }
    /// <summary>Optional final reporting cases, never included in optimizer prompts.</summary>
    public required IReadOnlyList<SkillOptCase> TestCases { get; init; }
    /// <summary>Fixed target-model client used to execute each case.</summary>
    public required IChatClient TargetChatClient { get; init; }
    /// <summary>Optimizer client used for reflection and text updates.</summary>
    public required IChatClient OptimizerChatClient { get; init; }
    /// <summary>Microsoft.Extensions.AI evaluator used to measure model responses.</summary>
    public required IEvaluator Evaluator { get; init; }
    /// <summary>Chat configuration supplied to the evaluator.</summary>
    public required ChatConfiguration EvaluationChatConfiguration { get; init; }
    /// <summary>Explicit run limits and selection settings.</summary>
    public required SkillOptOptions Options { get; init; }
    /// <summary>Caller-defined stable model and evaluator profile identity, used to reject incompatible resumes.</summary>
    public required string RunIdentity { get; init; }
    /// <summary>Safe completed-step/epoch state to continue from.</summary>
    public SkillOptRunState? ResumeState { get; init; }
    /// <summary>Persists each reservation and safe boundary before the optimizer continues.</summary>
    public Func<SkillOptRunState, CancellationToken, ValueTask>? Checkpoint { get; init; }
    /// <summary>Optional application-owned hard gate over complete per-case selection evidence; false rejects regardless of mean score.</summary>
    public Func<SkillOptCandidateEvaluation, bool>? CandidateGate { get; init; }
    /// <summary>Optional target-model generation settings.</summary>
    public ChatOptions? TargetChatOptions { get; init; }
    /// <summary>Optional optimizer-model generation settings.</summary>
    public ChatOptions? OptimizerChatOptions { get; init; }
}

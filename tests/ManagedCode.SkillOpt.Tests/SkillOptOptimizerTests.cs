using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace ManagedCode.SkillOpt.Tests;

public sealed partial class SkillOptOptimizerTests
{
    [Fact]
    public async Task OptimizeAsyncAcceptsImprovementAndReportsIsolatedTestSplit()
    {
        var target = new DelegateChatClient(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal)
                ? "success" : "failure");
        var optimizer = new DelegateChatClient(_ => "{\"reasoning\":\"Fix the repeated failure\",\"edits\":[{\"op\":\"append\",\"content\":\"Better instruction\"}]}");
        var request = CreateRequest(target, optimizer, new ScoreEvaluator(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal) ? 1 : 0));

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Contains("Better instruction", result.BestSkill, StringComparison.Ordinal);
        Assert.Equal("accept_new_best", Assert.Single(result.State.History).Action);
        Assert.Equal(0, result.BaselineTest.MeanScore);
        Assert.Equal(1, result.BestTest.MeanScore);
        Assert.Contains("failure", optimizer.SeenMessages.Single()[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("held-out question", string.Join("\n", optimizer.SeenMessages.SelectMany(static batch => batch).Select(static message => message.Text)), StringComparison.Ordinal);
        Assert.Equal(5, result.State.RolloutsUsed);
    }

    [Fact]
    public async Task OptimizeAsyncUsesTargetMessageFactoryForEverySplitAndPreservesFrozenSystemContext()
    {
        var factoryInputs = new List<(IReadOnlyList<ChatMessage> Messages, string Candidate)>();
        var target = new DelegateChatClient(messages =>
            messages.Any(message => message.Role == ChatRole.System &&
                                    message.Text == "Candidate skill:\noriginal skill")
                ? "baseline" : "improved");
        var optimizer = new DelegateChatClient(_ =>
            "{\"edits\":[{\"op\":\"append\",\"content\":\"Candidate instruction\"}]}");
        var evaluator = new ScoreEvaluator(messages =>
            messages.First(message => message.Role == ChatRole.System).Text ==
                "Candidate skill:\noriginal skill" ? 0 : 1);
        var request = CreateRequest(target, optimizer, evaluator) with
        {
            TrainingCases = [CreateFrozenCase("train", "training prompt")],
            SelectionCases = [CreateFrozenCase("selection", "selection prompt")],
            TestCases = [CreateFrozenCase("test", "test prompt")],
            TargetMessageFactory = (messages, candidate) =>
            {
                factoryInputs.Add((messages.ToArray(), candidate));
                return [new ChatMessage(ChatRole.System, $"Candidate skill:\n{candidate}"), .. messages];
            }
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Contains("Candidate instruction", result.BestSkill, StringComparison.Ordinal);
        Assert.Equal(5, factoryInputs.Count);
        Assert.Contains(factoryInputs, input => input.Candidate == "original skill" &&
            input.Messages[0].Text == "Frozen scenario context" && input.Messages[1].Text == "training prompt");
        Assert.Contains(factoryInputs, input => input.Candidate == "original skill" &&
            input.Messages[0].Text == "Frozen scenario context" && input.Messages[1].Text == "selection prompt");
        Assert.Contains(factoryInputs, input => input.Candidate.Contains("Candidate instruction", StringComparison.Ordinal) &&
            input.Messages[0].Text == "Frozen scenario context" && input.Messages[1].Text == "selection prompt");
        Assert.Contains(factoryInputs, input => input.Candidate == "original skill" &&
            input.Messages[0].Text == "Frozen scenario context" && input.Messages[1].Text == "test prompt");
        Assert.Contains(factoryInputs, input => input.Candidate.Contains("Candidate instruction", StringComparison.Ordinal) &&
            input.Messages[0].Text == "Frozen scenario context" && input.Messages[1].Text == "test prompt");
        Assert.All(target.SeenMessages, messages =>
        {
            Assert.Equal(ChatRole.System, messages[0].Role);
            Assert.StartsWith("Candidate skill:\n", messages[0].Text);
            Assert.Equal(ChatRole.System, messages[1].Role);
            Assert.Equal("Frozen scenario context", messages[1].Text);
        });
    }

    [Fact]
    public async Task OptimizeAsyncRejectsHeldOutRegressionAndRetainsRejectedCandidate()
    {
        var target = new DelegateChatClient(_ => "same response");
        var optimizer = new DelegateChatClient(_ => "{\"edits\":[{\"op\":\"append\",\"content\":\"Worse change\"}]}");
        var evaluator = new ScoreEvaluator(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Worse change", StringComparison.Ordinal) ? 0.3 : 0.8);

        var result = await SkillOptOptimizer.OptimizeAsync(CreateRequest(target, optimizer, evaluator));

        Assert.Equal("original skill", result.BestSkill);
        Assert.Equal("reject", Assert.Single(result.State.History).Action);
        Assert.Single(result.State.RejectedEdits);
    }

    [Fact]
    public async Task OptimizeAsyncMalformedReflectionIsSkippedWithoutChangingSkill()
    {
        var target = new DelegateChatClient(_ => "failure");
        var optimizer = new DelegateChatClient(_ => "not JSON");
        var result = await SkillOptOptimizer.OptimizeAsync(CreateRequest(target, optimizer, new ScoreEvaluator(_ => 0.5)));

        Assert.Equal("original skill", result.BestSkill);
        Assert.Equal("skip_no_patches", Assert.Single(result.State.History).Action);
    }

    [Fact]
    public async Task OptimizeAsyncPropagatesCancellationBeforeCallingModels()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var target = new DelegateChatClient(_ => "must not run");
        var optimizer = new DelegateChatClient(_ => "must not run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SkillOptOptimizer.OptimizeAsync(
            CreateRequest(target, optimizer, new ScoreEvaluator(_ => 0.5)), cancellation.Token));

        Assert.Empty(target.SeenMessages);
        Assert.Empty(optimizer.SeenMessages);
    }

    [Theory]
    [InlineData("append", "", "Added rule", "Added rule", "")]
    [InlineData("insert_after", "Anchor one", "Inserted rule", "Inserted rule", "")]
    [InlineData("replace", "Anchor one", "Replacement rule", "Replacement rule", "Anchor one")]
    [InlineData("delete", "Anchor two", "", "Anchor one", "Anchor two")]
    public async Task OptimizeAsyncAppliesSupportedExactTextOperations(
        string operation,
        string editTarget,
        string content,
        string expectedText,
        string absentText)
    {
        var json = $$"""{"edits":[{"op":"{{operation}}","target":"{{editTarget}}","content":"{{content}}"}]}""";
        var optimizer = new DelegateChatClient(_ => json);
        var evaluator = new ScoreEvaluator(messages =>
        {
            var skill = messages.First(message => message.Role == ChatRole.System).Text;
            return skill.Contains(expectedText, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(absentText) || !skill.Contains(absentText, StringComparison.Ordinal)) ? 1 : 0;
        });
        var request = CreateRequest(new DelegateChatClient(_ => "task response"), optimizer, evaluator) with
        {
            InitialSkill = "Anchor one\n\nAnchor two"
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Contains(expectedText, result.BestSkill, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(absentText))
        {
            Assert.DoesNotContain(absentText, result.BestSkill, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OptimizeAsyncSupportsFullRewriteMode()
    {
        var evaluator = new ScoreEvaluator(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Full rewrite skill", StringComparison.Ordinal) ? 1 : 0);
        var request = CreateRequest(new DelegateChatClient(_ => "task response"),
            new DelegateChatClient(_ => "Full rewrite skill"), evaluator) with
        {
            Options = CreateOptions() with { UpdateMode = SkillUpdateMode.FullRewrite }
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Equal("Full rewrite skill", result.BestSkill);
    }

    [Fact]
    public async Task OptimizeAsyncSupportsRewriteFromSuggestionsMode()
    {
        var optimizerResponses = new Queue<string>([
            "{\"edits\":[{\"op\":\"append\",\"content\":\"Suggestion\"}]}",
            "Materialized rewrite skill"
        ]);
        var evaluator = new ScoreEvaluator(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Materialized rewrite skill", StringComparison.Ordinal) ? 1 : 0);
        var optimizer = new DelegateChatClient(_ => optimizerResponses.Dequeue());
        var request = CreateRequest(new DelegateChatClient(_ => "task response"), optimizer, evaluator) with
        {
            Options = CreateOptions() with { UpdateMode = SkillUpdateMode.RewriteFromSuggestions }
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Equal("Materialized rewrite skill", result.BestSkill);
        Assert.Equal(2, optimizer.SeenMessages.Count);
    }

    [Fact]
    public async Task OptimizeAsyncRejectsResumeStateWhenDatasetChanged()
    {
        var checkpoints = new List<SkillOptRunState>();
        var request = CreateRequest(new DelegateChatClient(_ => "ok"), new DelegateChatClient(_ => "{}"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) =>
            {
                checkpoints.Add(state);
                return ValueTask.CompletedTask;
            }
        };
        await SkillOptOptimizer.OptimizeAsync(request);
        var safe = LastSafeTrainingCheckpoint(checkpoints);
        var resumed = await SkillOptOptimizer.OptimizeAsync(request with { Checkpoint = null, ResumeState = safe });
        Assert.Equal(SkillOptRunPhase.Completed, resumed.State.Phase);
        var changed = request with
        {
            ResumeState = safe,
            TrainingCases = [CreateCase("train", "changed task")]
        };

        await Assert.ThrowsAsync<ArgumentException>(() => SkillOptOptimizer.OptimizeAsync(changed));
    }

    [Fact]
    public async Task OptimizeAsyncRespectsEvaluationBudgetBeforeCallingTarget()
    {
        var target = new DelegateChatClient(_ => "never called");
        var request = CreateRequest(target, new DelegateChatClient(_ => "{}"), new ScoreEvaluator(_ => 0.5)) with
        {
            Options = CreateOptions() with { MaxRollouts = 1 }
        };

        await Assert.ThrowsAsync<SkillOptBudgetExceededException>(() => SkillOptOptimizer.OptimizeAsync(request));
        Assert.Single(target.SeenMessages);
    }


    [Fact]
    public async Task OptimizeAsyncRejectsChangedRunIdentityWhenResuming()
    {
        var checkpoints = new List<SkillOptRunState>();
        var request = CreateRequest(new DelegateChatClient(_ => "ok"), new DelegateChatClient(_ => "{}"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) =>
            {
                checkpoints.Add(state);
                return ValueTask.CompletedTask;
            }
        };
        await SkillOptOptimizer.OptimizeAsync(request);
        var safe = LastSafeTrainingCheckpoint(checkpoints);
        var resumed = await SkillOptOptimizer.OptimizeAsync(request with { Checkpoint = null, ResumeState = safe });
        Assert.Equal(SkillOptRunPhase.Completed, resumed.State.Phase);

        await Assert.ThrowsAsync<ArgumentException>(() => SkillOptOptimizer.OptimizeAsync(request with
        {
            ResumeState = safe,
            RunIdentity = "different-model-or-evaluator"
        }));
    }

    [Fact]
    public async Task OptimizeAsyncRejectsOverlappingSplitIdentity()
    {
        var request = CreateRequest(new DelegateChatClient(_ => "ok"), new DelegateChatClient(_ => "{}"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            SelectionCases = [CreateCase("train", "selection question")]
        };

        await Assert.ThrowsAsync<ArgumentException>(() => SkillOptOptimizer.OptimizeAsync(request));
    }

    [Fact]
    public async Task OptimizeAsyncFailsClosedOnEvaluatorErrorDiagnostic()
    {
        var request = CreateRequest(new DelegateChatClient(_ => "success"), new DelegateChatClient(_ => "{}"),
            new ScoreEvaluator(_ => 1, returnError: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SkillOptOptimizer.OptimizeAsync(request));
    }

    [Fact]
    public async Task OptimizeAsyncFailsClosedOnOutOfRangeScore()
    {
        var request = CreateRequest(new DelegateChatClient(_ => "success"), new DelegateChatClient(_ => "{}"),
            new ScoreEvaluator(_ => 1.01));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SkillOptOptimizer.OptimizeAsync(request));
    }

    private static SkillOptRequest CreateRequest(DelegateChatClient target, DelegateChatClient optimizer, IEvaluator evaluator) => new()
    {
        InitialSkill = "original skill",
        TrainingCases = [CreateCase("train", "training question")],
        SelectionCases = [CreateCase("selection", "selection question")],
        TestCases = [CreateCase("test", "held-out question")],
        TargetChatClient = target,
        OptimizerChatClient = optimizer,
        Evaluator = evaluator,
        EvaluationChatConfiguration = new ChatConfiguration(new DelegateChatClient(_ => "judge")),
        Options = CreateOptions(),
        RunIdentity = "target-model-v1|optimizer-v1|evaluator-v1"
    };

    private static SkillOptRunState LastSafeTrainingCheckpoint(IReadOnlyList<SkillOptRunState> checkpoints) =>
        checkpoints.Last(state => state.IsResumeSafe && state.Phase == SkillOptRunPhase.Training && state.CompletedSteps == 1);

    private static SkillOptOptions CreateOptions() => new()
    {
        Epochs = 1,
        StepsPerEpoch = 1,
        BatchSize = 1,
        MaxRollouts = 8,
        MaxOptimizerCalls = 8,
        MaxEvaluationCalls = 8,
        MaxEditsPerStep = 2,
        MaxRejectedEdits = 4,
        ScoreMetricName = "quality"
    };

    private static SkillOptCase CreateCase(string id, string text) => new()
    {
        Id = id,
        ContentFingerprint = $"{id}|{text}",
        Messages = [new ChatMessage(ChatRole.User, text)]
    };

    private static SkillOptCase CreateFrozenCase(string id, string text) => new()
    {
        Id = id,
        ContentFingerprint = $"{id}|frozen-context|{text}",
        Messages =
        [
            new ChatMessage(ChatRole.System, "Frozen scenario context"),
            new ChatMessage(ChatRole.User, text)
        ]
    };

    private sealed class ScoreEvaluator(
        Func<IEnumerable<ChatMessage>, double> score,
        bool returnError = false,
        Func<IEnumerable<ChatMessage>, double>? privacyMetric = null) : IEvaluator
    {
        public IReadOnlyCollection<string> EvaluationMetricNames { get; } = privacyMetric is null
            ? ["quality"]
            : ["quality", "privacy-events"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metric = new NumericMetric("quality", score(messages));
            if (returnError)
            {
                metric.Diagnostics = [EvaluationDiagnostic.Error("Evaluation failed.")];
            }

            return ValueTask.FromResult(privacyMetric is null
                ? new EvaluationResult(metric)
                : new EvaluationResult(metric, new NumericMetric("privacy-events", privacyMetric(messages))));
        }
    }

    private sealed class DelegateChatClient(Func<IEnumerable<ChatMessage>, string> response) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> SeenMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            SeenMessages.Add(snapshot);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response(snapshot))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, response(messages));
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

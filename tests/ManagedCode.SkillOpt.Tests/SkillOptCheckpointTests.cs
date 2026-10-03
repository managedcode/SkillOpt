using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace ManagedCode.SkillOpt.Tests;

public sealed partial class SkillOptOptimizerTests
{
    [Fact]
    public async Task OptimizeAsyncCheckpointsCountCallsAndExplicitPhase()
    {
        var checkpoints = new List<SkillOptRunState>();
        var request = CreateRequest(new DelegateChatClient(_ => "failure"), new DelegateChatClient(_ => "bad json"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) =>
            {
                checkpoints.Add(state);
                return ValueTask.CompletedTask;
            }
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Equal(SkillOptRunPhase.Completed, result.State.Phase);
        Assert.Contains(checkpoints, state => state.Phase == SkillOptRunPhase.Training && state.RolloutsUsed > 0);
        Assert.True(checkpoints.Zip(checkpoints.Skip(1), (previous, current) =>
            current.RolloutsUsed >= previous.RolloutsUsed && current.EvaluationCallsUsed >= previous.EvaluationCallsUsed).All(static value => value));
    }

    [Fact]
    public async Task OptimizeAsyncPersistsCompletedStateAfterHeldOutReports()
    {
        var checkpoints = new List<SkillOptRunState>();
        var request = CreateRequest(new DelegateChatClient(_ => "failure"), new DelegateChatClient(_ => "bad json"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) =>
            {
                checkpoints.Add(state);
                return ValueTask.CompletedTask;
            }
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Equal(SkillOptRunPhase.Completed, result.State.Phase);
        Assert.Single(checkpoints, state => state.Phase == SkillOptRunPhase.Completed && state.IsResumeSafe);
        Assert.Equal(result.State.RolloutsUsed, checkpoints[^1].RolloutsUsed);
    }

    [Fact]
    public async Task OptimizeAsyncDoesNotReturnResultWhenCompletedCheckpointFails()
    {
        var completed = false;
        var request = CreateRequest(new DelegateChatClient(_ => "failure"), new DelegateChatClient(_ => "bad json"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) =>
            {
                if (state.Phase == SkillOptRunPhase.Completed)
                {
                    completed = true;
                    return ValueTask.FromException(new IOException("final checkpoint failed"));
                }

                return ValueTask.CompletedTask;
            }
        };

        await Assert.ThrowsAsync<IOException>(() => SkillOptOptimizer.OptimizeAsync(request));

        Assert.True(completed);
    }

    [Fact]
    public async Task OptimizeAsyncPersistsReservedOptimizerCallAndRejectsUncertainResume()
    {
        var checkpoints = new List<SkillOptRunState>();
        var request = CreateRequest(new DelegateChatClient(_ => "failure"),
            new DelegateChatClient(_ => throw new InvalidOperationException("simulated interrupted call")),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) =>
            {
                checkpoints.Add(state);
                return ValueTask.CompletedTask;
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => SkillOptOptimizer.OptimizeAsync(request));

        var uncertain = Assert.Single(checkpoints, state => state.OptimizerCallsUsed == 1 && !state.IsResumeSafe);
        await Assert.ThrowsAsync<ArgumentException>(() => SkillOptOptimizer.OptimizeAsync(request with { ResumeState = uncertain }));
    }

    [Fact]
    public async Task OptimizeAsyncCheckpointFailureStopsBeforeOptimizerCall()
    {
        var optimizer = new DelegateChatClient(_ => "{}");
        var request = CreateRequest(new DelegateChatClient(_ => "failure"), optimizer,
            new ScoreEvaluator(_ => 0.5)) with
        {
            Checkpoint = (state, _) => state.OptimizerCallsUsed > 0
                ? ValueTask.FromException(new IOException("checkpoint store unavailable"))
                : ValueTask.CompletedTask
        };

        await Assert.ThrowsAsync<IOException>(() => SkillOptOptimizer.OptimizeAsync(request));

        Assert.Empty(optimizer.SeenMessages);
    }

    [Fact]
    public async Task OptimizeAsyncResumesFromCompletedStepWithoutResettingSpentCalls()
    {
        SkillOptRunState? saved = null;
        var request = CreateRequest(new DelegateChatClient(_ => "failure"), new DelegateChatClient(_ => "bad json"),
            new ScoreEvaluator(_ => 0.5)) with
        {
            Options = CreateOptions() with { Epochs = 2 },
            Checkpoint = (state, _) =>
            {
                if (state.CompletedSteps == 1 && state.IsResumeSafe)
                {
                    saved = state;
                    return ValueTask.FromException(new IOException("simulate process stop after durable checkpoint"));
                }

                return ValueTask.CompletedTask;
            }
        };

        await Assert.ThrowsAsync<IOException>(() => SkillOptOptimizer.OptimizeAsync(request));
        Assert.NotNull(saved);
        var callsSpent = saved.RolloutsUsed;

        var result = await SkillOptOptimizer.OptimizeAsync(request with
        {
            Checkpoint = null,
            ResumeState = saved
        });

        Assert.Equal(2, result.State.CompletedSteps);
        Assert.True(result.State.RolloutsUsed > callsSpent);
    }

    [Fact]
    public async Task OptimizeAsyncRejectsCompletedResumeWithoutRepeatingHeldOutCalls()
    {
        var target = new DelegateChatClient(_ => "same response");
        var request = CreateRequest(target, new DelegateChatClient(_ => "bad json"), new ScoreEvaluator(_ => 0.5));
        var completed = await SkillOptOptimizer.OptimizeAsync(request);
        var targetCalls = target.SeenMessages.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => SkillOptOptimizer.OptimizeAsync(request with
        {
            ResumeState = completed.State
        }));

        Assert.Equal(targetCalls, target.SeenMessages.Count);
    }

    [Fact]
    public async Task OptimizeAsyncRejectsNegativeResumeCountersBeforeCallingModels()
    {
        var checkpoints = new List<SkillOptRunState>();
        var request = CreateRequest(new DelegateChatClient(_ => "failure"), new DelegateChatClient(_ => "bad json"),
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
        var invalidStates = new Func<SkillOptRunState, SkillOptRunState>[]
        {
            state => state with { RolloutsUsed = -1 },
            state => state with { OptimizerCallsUsed = -1 },
            state => state with { EvaluationCallsUsed = -1 },
            state => state with { CompletedSteps = -1 },
            state => state with { CompletedEpochs = -1 }
        };

        foreach (var makeInvalid in invalidStates)
        {
            var target = new DelegateChatClient(_ => "must not run");
            var optimizer = new DelegateChatClient(_ => "must not run");
            await Assert.ThrowsAsync<ArgumentException>(() => SkillOptOptimizer.OptimizeAsync(request with
            {
                TargetChatClient = target,
                OptimizerChatClient = optimizer,
                Checkpoint = null,
                ResumeState = makeInvalid(safe)
            }));
            Assert.Empty(target.SeenMessages);
            Assert.Empty(optimizer.SeenMessages);
        }
    }

    [Fact]
    public async Task OptimizeAsyncRejectsUnsupportedOptionsBeforeCallingModels()
    {
        var target = new DelegateChatClient(_ => "must not run");
        var optimizer = new DelegateChatClient(_ => "must not run");
        var request = CreateRequest(target, optimizer, new ScoreEvaluator(_ => 0.5));
        foreach (var options in new[]
        {
            CreateOptions() with { UpdateMode = (SkillUpdateMode)int.MaxValue },
            CreateOptions() with { EditBudgetSchedule = (SkillEditBudgetSchedule)int.MaxValue },
            CreateOptions() with { ScoreDirection = (SkillOptScoreDirection)int.MaxValue }
        })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SkillOptOptimizer.OptimizeAsync(request with { Options = options }));
        }

        Assert.Empty(target.SeenMessages);
        Assert.Empty(optimizer.SeenMessages);
    }

    [Fact]
    public async Task OptimizeAsyncRejectsStepCountOverflowBeforeCallingModels()
    {
        var target = new DelegateChatClient(_ => "must not run");
        var request = CreateRequest(target, new DelegateChatClient(_ => "{}"), new ScoreEvaluator(_ => 0.5)) with
        {
            Options = CreateOptions() with { Epochs = int.MaxValue, StepsPerEpoch = 2 }
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SkillOptOptimizer.OptimizeAsync(request));
        Assert.Empty(target.SeenMessages);
    }

    [Fact]
    public async Task OptimizeAsyncReportsUnavailableTestEvidenceWithoutInventingZero()
    {
        var request = CreateRequest(new DelegateChatClient(_ => "same response"),
            new DelegateChatClient(_ => "bad json"), new ScoreEvaluator(_ => 0.5)) with
        {
            TestCases = []
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Equal(0, result.BaselineTest.CaseCount);
        Assert.Null(result.BaselineTest.MeanScore);
        Assert.Empty(result.BaselineTest.Cases);
        Assert.Null(result.BestTest.MeanScore);
    }

    [Fact]
    public async Task OptimizeAsyncMinimizesMetricAndReturnsAllPerCaseMetricEvidence()
    {
        var target = new DelegateChatClient(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal)
                ? "success" : "failure");
        var optimizer = new DelegateChatClient(_ => "{\"edits\":[{\"op\":\"append\",\"content\":\"Better instruction\"}]}");
        var evaluator = new ScoreEvaluator(
            messages => messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal) ? 2 : 8,
            privacyMetric: messages => messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal) ? 1 : 4);
        var request = CreateRequest(target, optimizer, evaluator) with
        {
            Options = CreateOptions() with
            {
                MetricMinimum = 0,
                MetricMaximum = 10,
                ScoreDirection = SkillOptScoreDirection.LowerIsBetter
            }
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Contains("Better instruction", result.BestSkill, StringComparison.Ordinal);
        var testCase = Assert.Single(result.BestTest.Cases);
        Assert.Equal(0.8, testCase.Score);
        var privacy = Assert.Single(testCase.Metrics, metric => metric.Name == "privacy-events");
        Assert.Equal(1, privacy.NumericValue);
    }

    [Fact]
    public async Task OptimizeAsyncCandidateGateCanRejectPrivacyMetricDespiteQualityImprovement()
    {
        var target = new DelegateChatClient(messages =>
            messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal)
                ? "success" : "failure");
        var optimizer = new DelegateChatClient(_ => "{\"edits\":[{\"op\":\"append\",\"content\":\"Better instruction\"}]}");
        var evaluator = new ScoreEvaluator(
            messages => messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal) ? 1 : 0.2,
            privacyMetric: messages => messages.First(message => message.Role == ChatRole.System).Text.Contains("Better instruction", StringComparison.Ordinal) ? 1 : 0);
        var request = CreateRequest(target, optimizer, evaluator) with
        {
            CandidateGate = candidate => candidate.SelectionReport.Cases.All(testCase =>
                testCase.Metrics.Single(metric => metric.Name == "privacy-events").NumericValue == 0)
        };

        var result = await SkillOptOptimizer.OptimizeAsync(request);

        Assert.Equal("original skill", result.BestSkill);
        Assert.Equal("reject_policy_gate", Assert.Single(result.State.History).Action);
    }


    [Fact]
    public async Task OptimizeAsyncRejectsChangedInitialSkillWhenResuming()
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
            InitialSkill = "changed initial skill"
        }));
    }

    [Fact]
    public async Task OptimizeAsyncRejectsChangedContextFingerprintWhenResuming()
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
            TrainingCases = [CreateCase("train", "training question") with { ContentFingerprint = "changed-hidden-context" }]
        }));
    }

}

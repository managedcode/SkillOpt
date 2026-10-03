using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace ManagedCode.SkillOpt;

internal static class OptimizationHelpers
{
    public static string Fingerprint(SkillOptRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("run:").Append(request.RunIdentity).Append('\0');
        builder.Append("target-message-factory:")
            .Append(request.TargetMessageFactory is null ? "default" : "custom")
            .Append('\0');
        builder.Append("initial:").Append(request.InitialSkill).Append('\0');
        builder.Append("options:").Append(JsonSerializer.Serialize(request.Options)).Append('\0');
        AppendSplit(builder, "training", request.TrainingCases);
        AppendSplit(builder, "selection", request.SelectionCases);
        AppendSplit(builder, "test", request.TestCases);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendSplit(StringBuilder builder, string name, IReadOnlyList<SkillOptCase> cases)
    {
        builder.Append("split:").Append(name).Append('\0');
        foreach (var item in cases)
        {
            builder.Append(item.Id).Append('\0').Append(item.ContentFingerprint).Append('\0');
            foreach (var message in item.Messages)
            {
                builder.Append(message.Role.Value).Append(':').Append(message.Text).Append('\0');
            }
        }
    }

    public static string Prompt(string name)
    {
        var assembly = typeof(OptimizationHelpers).Assembly;
        var resourceName = $"ManagedCode.SkillOpt.Prompts.{name}.md";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Required prompt resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<ChatMessage> AddSkill(IReadOnlyList<ChatMessage> messages, string skill)
    {
        var systemText = string.Join("\n\n", messages.Where(static message => message.Role == ChatRole.System)
            .Select(static message => message.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));
        var result = new List<ChatMessage>(messages.Count + 1)
        {
            new(ChatRole.System, string.IsNullOrEmpty(systemText) ? skill : $"{systemText}\n\n{skill}")
        };
        result.AddRange(messages.Where(static message => message.Role != ChatRole.System));
        return result;
    }

    public static async Task<CaseObservation> EvaluateCaseAsync(
        SkillOptRequest request,
        SkillOptCase item,
        string skill,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? beforeTargetCall = null,
        Func<CancellationToken, ValueTask>? beforeEvaluationCall = null)
    {
        IReadOnlyList<ChatMessage>? messages = request.TargetMessageFactory is { } factory
            ? factory(item.Messages, skill)
            : AddSkill(item.Messages, skill);
        if (messages is null)
        {
            throw new InvalidOperationException("Target message factory returned null messages.");
        }
        if (beforeTargetCall is not null)
        {
            await beforeTargetCall(cancellationToken);
        }

        var response = await request.TargetChatClient.GetResponseAsync(messages, request.TargetChatOptions, cancellationToken);
        if (beforeEvaluationCall is not null)
        {
            await beforeEvaluationCall(cancellationToken);
        }

        var evaluation = await request.Evaluator.EvaluateAsync(
            messages,
            response,
            request.EvaluationChatConfiguration,
            item.EvaluationContexts,
            cancellationToken);
        if (!evaluation.Metrics.TryGetValue(request.Options.ScoreMetricName, out var metric) || metric is not NumericMetric numeric || numeric.Value is null)
        {
            throw new InvalidOperationException($"Evaluator did not return numeric metric '{request.Options.ScoreMetricName}'.");
        }

        var evidence = evaluation.Metrics.Values
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .Select(ToEvidence)
            .ToArray();
        if (evidence.SelectMany(static value => value.Diagnostics)
            .Any(static diagnostic => diagnostic.Severity == EvaluationDiagnosticSeverity.Error))
        {
            throw new InvalidOperationException("Evaluator reported an error diagnostic for one or more metrics.");
        }

        var rawScore = numeric.Value.Value;
        if (!double.IsFinite(rawScore))
        {
            throw new InvalidOperationException($"Evaluator returned a non-finite value for metric '{request.Options.ScoreMetricName}'.");
        }

        if (rawScore < request.Options.MetricMinimum || rawScore > request.Options.MetricMaximum)
        {
            throw new InvalidOperationException($"Evaluator score for '{request.Options.ScoreMetricName}' fell outside its configured range.");
        }

        var normalized = (rawScore - request.Options.MetricMinimum) /
            (request.Options.MetricMaximum - request.Options.MetricMinimum);
        if (request.Options.ScoreDirection == SkillOptScoreDirection.LowerIsBetter)
        {
            normalized = 1 - normalized;
        }

        return new CaseObservation(normalized, response.Text, evidence);
    }

    public static double Mean(IEnumerable<double> scores)
    {
        var values = scores.ToArray();
        return values.Length == 0 ? 0 : values.Average();
    }

    public static SkillOptSplitReport Report(
        string split,
        IReadOnlyList<SkillOptCase> cases,
        IReadOnlyList<CaseObservation> observations,
        string scoreMetricName) =>
        new(split, cases.Count, cases.Count == 0 ? null : Mean(observations.Select(static observation => observation.Score)),
            cases.Select((item, index) => new SkillOptCaseScore(item.Id, observations[index].Score,
                observations[index].Metrics.FirstOrDefault(metric => metric.Name == scoreMetricName)?.Reason,
                observations[index].Metrics)).ToArray());

    private static SkillOptMetricEvidence ToEvidence(EvaluationMetric metric)
    {
        var numeric = metric is NumericMetric numericMetric ? numericMetric.Value : null;
        var boolean = metric is BooleanMetric booleanMetric ? booleanMetric.Value : null;
        var text = metric is StringMetric stringMetric ? stringMetric.Value : null;
        return new SkillOptMetricEvidence(metric.Name, metric.GetType().Name, numeric, boolean, text,
            metric.Reason, metric.Interpretation?.ToString(),
            metric.Diagnostics?.Select(static diagnostic =>
                new SkillOptEvaluationDiagnostic(diagnostic.Severity, diagnostic.Message)).ToArray() ?? []);
    }

    public static string ReadJson(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new JsonException("Optimizer response did not contain a JSON object.");
        }

        return response[start..(end + 1)];
    }

    public static List<SkillOptEdit> ParseEdits(string json, SkillOptOutcome? outcome)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SkillOptEdit>();
        foreach (var item in edits.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("op", out var opValue))
            {
                continue;
            }

            var op = opValue.GetString()?.ToLowerInvariant() switch
            {
                "append" => SkillOptEditOperation.Append,
                "insert_after" => SkillOptEditOperation.InsertAfter,
                "replace" => SkillOptEditOperation.Replace,
                "delete" => SkillOptEditOperation.Delete,
                _ => (SkillOptEditOperation?)null
            };
            if (op is null)
            {
                continue;
            }

            var target = item.TryGetProperty("target", out var targetValue) ? targetValue.GetString() ?? string.Empty : string.Empty;
            var content = item.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? string.Empty : string.Empty;
            if ((op is SkillOptEditOperation.InsertAfter or SkillOptEditOperation.Replace or SkillOptEditOperation.Delete) && string.IsNullOrEmpty(target))
            {
                continue;
            }

            result.Add(new SkillOptEdit
            {
                Operation = op.Value,
                Target = target,
                Content = content,
                SourceOutcome = outcome
            });
        }

        return result;
    }

    public static string? ReadReason(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("reasoning", out var value) ? value.GetString() : null;
    }

    public static string ApplyEdits(string skill, IReadOnlyList<SkillOptEdit> edits, out int applied)
    {
        applied = 0;
        foreach (var edit in edits)
        {
            if (edit.Operation == SkillOptEditOperation.Append)
            {
                skill = skill.TrimEnd() + "\n\n" + edit.Content.Trim() + "\n";
                applied++;
                continue;
            }

            var first = skill.IndexOf(edit.Target, StringComparison.Ordinal);
            if (first < 0 || skill.IndexOf(edit.Target, first + edit.Target.Length, StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            skill = edit.Operation switch
            {
                SkillOptEditOperation.InsertAfter => skill.Insert(first + edit.Target.Length, edit.Content),
                SkillOptEditOperation.Replace => skill.Remove(first, edit.Target.Length).Insert(first, edit.Content),
                SkillOptEditOperation.Delete => skill.Remove(first, edit.Target.Length),
                _ => skill
            };
            applied++;
        }

        return skill;
    }
}

internal sealed record CaseObservation(
    double Score,
    string ResponseText,
    IReadOnlyList<SkillOptMetricEvidence> Metrics);

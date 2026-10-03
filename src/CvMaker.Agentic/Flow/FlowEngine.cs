using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CvMaker.Agentic.Agents;
using CvMaker.Agentic.Ai;

namespace CvMaker.Agentic.Flow;

/// <summary>
/// Executes a JSON flow definition: sequential steps, an agent call per step,
/// one conditional for the capped revision pass.
///
/// Deliberately small. It supports what cv-generation-flow.json actually uses
/// and nothing else — a general expression evaluator here would be an
/// interpreter nobody asked for, and every feature in it is a place a prompt
/// author can create a loop.
/// </summary>
public sealed partial class FlowEngine(
    AgentCatalog agents,
    IAgentRuntime runtime,
    ILogger<FlowEngine> logger)
{
    [GeneratedRegex(@"\{\{\s*(input|steps)\.([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    public async Task<FlowResult> ExecuteAsync(
        FlowDocument flow, IReadOnlyDictionary<string, string> input, CancellationToken ct = default)
    {
        var context = new FlowContext();
        foreach (var (k, v) in input) context.Input[k] = v;

        string? skipToId = null;

        foreach (var step in flow.Steps)
        {
            ct.ThrowIfCancellationRequested();

            if (skipToId is not null)
            {
                if (!string.Equals(step.Id, skipToId, StringComparison.OrdinalIgnoreCase)) continue;
                skipToId = null;
            }

            var sw = Stopwatch.StartNew();

            switch (step.Type.ToLowerInvariant())
            {
                case "agent-call":
                {
                    var agent = agents.Find(step.Agent ?? string.Empty);
                    if (agent is null)
                    {
                        return new FlowResult(false, null,
                            $"Flow references unknown agent '{step.Agent}'.", context.Log);
                    }

                    var prompt = Interpolate(step.Prompt ?? string.Empty, context);
                    var reply = await runtime.InvokeAsync(agent, prompt, ct);

                    if (!reply.Success)
                    {
                        context.Log.Add($"{step.Id}: failed after {sw.ElapsedMilliseconds}ms");
                        return new FlowResult(false, null, reply.Error ?? "Agent call failed.", context.Log);
                    }

                    if (!string.IsNullOrEmpty(step.OutputKey))
                        context.StepOutputs[step.OutputKey] = StripCodeFence(reply.Content);

                    context.Log.Add($"{step.Id}: {agent.Slug} v{agent.Version} in {sw.ElapsedMilliseconds}ms");
                    break;
                }

                case "conditional":
                {
                    var holds = EvaluateCondition(step.Condition, context);
                    if (!holds && !string.IsNullOrEmpty(step.SkipTo)) skipToId = step.SkipTo;
                    context.Log.Add($"{step.Id}: condition {(holds ? "held" : "did not hold")}");
                    break;
                }

                case "noop":
                    break;

                default:
                    logger.LogWarning("Unknown step type '{Type}' in '{Id}' — skipped", step.Type, step.Id);
                    break;
            }
        }

        var outputKey = flow.Output?.From;
        if (string.IsNullOrEmpty(outputKey) || !context.StepOutputs.TryGetValue(outputKey, out var output))
            return new FlowResult(false, null, $"Flow produced no '{outputKey}' output.", context.Log);

        // Intermediate outputs travel with the result so the orchestrator can
        // surface the critique, not just the composed CV.
        return new FlowResult(true, output, null, context.Log, context.StepOutputs);
    }

    private static string Interpolate(string template, FlowContext context) =>
        PlaceholderRegex().Replace(template, match =>
        {
            var scope = match.Groups[1].Value;
            var key = match.Groups[2].Value;

            var source = scope.Equals("input", StringComparison.OrdinalIgnoreCase)
                ? context.Input
                : context.StepOutputs;

            // An unresolved placeholder becomes empty rather than the literal
            // "{{steps.foo}}", which a model would otherwise dutifully treat as
            // content and echo into a CV.
            return source.TryGetValue(key, out var value) ? value : string.Empty;
        });

    /// <summary>
    /// Evaluates <c>steps.&lt;key&gt;.&lt;field&gt; == &lt;value&gt;</c> against a step's
    /// JSON output. Only equality, only against a step output — the one form
    /// the CV flow needs.
    /// </summary>
    private bool EvaluateCondition(string? condition, FlowContext context)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;

        var parts = condition.Split("==", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return true;

        var path = parts[0].Split('.', StringSplitOptions.TrimEntries);
        if (path.Length != 3 || !path[0].Equals("steps", StringComparison.OrdinalIgnoreCase)) return true;

        if (!context.StepOutputs.TryGetValue(path[1], out var json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(path[2], out var prop)) return false;

            var actual = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
            return string.Equals(actual, parts[1].Trim('"'), StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            logger.LogWarning("Condition '{Condition}' could not read step output as JSON", condition);
            return false;
        }
    }

    /// <summary>
    /// Models wrap JSON in ```json fences despite being told not to, often
    /// enough that treating it as a hard error would fail generations for a
    /// cosmetic reason.
    /// </summary>
    internal static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }
}

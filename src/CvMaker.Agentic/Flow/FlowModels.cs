using System.Text.Json;
using System.Text.Json.Serialization;

namespace CvMaker.Agentic.Flow;

/// <summary>A flow definition, loaded from <c>flows/*.json</c>.</summary>
public sealed class FlowDocument
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("steps")]
    public List<FlowStep> Steps { get; set; } = [];

    [JsonPropertyName("output")]
    public FlowOutput? Output { get; set; }
}

public sealed class FlowOutput
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;
}

public sealed class FlowStep
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>"agent-call", "conditional" or "noop".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>Prompt template with {{input.*}} and {{steps.*}} placeholders.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("outputKey")]
    public string? OutputKey { get; set; }

    /// <summary>For conditionals: <c>steps.&lt;key&gt;.&lt;field&gt; == &lt;value&gt;</c>.</summary>
    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    /// <summary>Step id to jump to when the condition does not hold.</summary>
    [JsonPropertyName("skipTo")]
    public string? SkipTo { get; set; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }
}

/// <summary>Input and per-step output, threaded through execution.</summary>
public sealed class FlowContext
{
    public Dictionary<string, string> Input { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> StepOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = [];
}

public sealed record FlowResult(
    bool Success,
    string? Output,
    string? Error,
    IReadOnlyList<string> Log,
    IReadOnlyDictionary<string, string>? StepOutputs = null);

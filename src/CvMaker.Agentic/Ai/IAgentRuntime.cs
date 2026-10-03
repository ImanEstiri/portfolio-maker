using CvMaker.Agentic.Agents;

namespace CvMaker.Agentic.Ai;

public sealed record AgentReply(bool Success, string Content, string? Error);

/// <summary>
/// One turn against one agent. Deliberately minimal: the flow engine owns
/// sequencing, so a backend only has to answer "given these instructions and
/// this prompt, what did the model say".
/// </summary>
public interface IAgentRuntime
{
    /// <summary>False when no model backend is configured and replies are mocked.</summary>
    bool IsConfigured { get; }

    Task<AgentReply> InvokeAsync(AgentDefinition agent, string prompt, CancellationToken ct = default);
}

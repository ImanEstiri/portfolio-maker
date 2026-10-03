using System.Collections.Concurrent;
using System.Text;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using CvMaker.Agentic.Agents;

namespace CvMaker.Agentic.Ai;

/// <summary>
/// Azure AI Foundry backend.
///
/// Agents are created on first use and reused for the life of the process; each
/// invocation creates a thread and deletes it again. Nothing deletes the agents
/// themselves, so every process start leaves a fresh set in the Foundry project
/// (they hold instructions only, no user data). A CV generation is a one-shot pipeline — there is no
/// multi-turn conversation to resume — so the thread has no value past the
/// reply, and it holds the pasted job posting and the user's profile facts
/// verbatim on Azure's side.
///
/// "Discarded" used to mean "stopped referring to it", which is not the same as
/// deleted: every generation left a copy of that text in Foundry, somewhere
/// `DELETE /api/me/data` cannot reach. Deletion is now explicit and runs in a
/// finally, because a timed-out or failed run leaves exactly the same residue.
///
/// Three modes:
///   1. Managed identity, in Azure
///   2. DefaultAzureCredential, for local dev via `az login`
///   3. Mock replies when no endpoint is configured (selected in Program.cs,
///      not here)
/// Mode 3 is what lets the whole product run and be tested with no Azure
/// account at all — see <see cref="MockAgentRuntime"/>.
///
/// Configuration:
///   AzureAIFoundry:Endpoint           — the agents endpoint URI
///   AzureAIFoundry:ModelDeploymentName — fallback model
///   AzureAIFoundry:UseManagedIdentity  — true where a managed identity exists (not on Fly)
/// </summary>
public sealed class FoundryAgentRuntime : IAgentRuntime
{
    private readonly PersistentAgentsClient? _client;
    private readonly ILogger<FoundryAgentRuntime> _logger;
    private readonly string _fallbackModel;

    /// <summary>slug+version → provisioned Foundry agent id.</summary>
    private readonly ConcurrentDictionary<string, string> _provisioned = new(StringComparer.Ordinal);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);

    public bool IsConfigured => _client is not null;

    public FoundryAgentRuntime(IConfiguration configuration, ILogger<FoundryAgentRuntime> logger)
    {
        _logger = logger;
        _fallbackModel = configuration["AzureAIFoundry:ModelDeploymentName"] ?? "gpt-4.1";

        var endpoint = configuration["AzureAIFoundry:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning(
                "AzureAIFoundry:Endpoint is not configured — the Foundry runtime is inactive.");
            return;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            _logger.LogError("AzureAIFoundry:Endpoint '{Endpoint}' is not an absolute URI", endpoint);
            return;
        }

        var useManagedIdentity = string.Equals(
            configuration["AzureAIFoundry:UseManagedIdentity"], "true", StringComparison.OrdinalIgnoreCase);

        var credential = useManagedIdentity
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(
                new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true });

        _client = new PersistentAgentsClient(uri.ToString(), credential);
        _logger.LogInformation("Foundry runtime active against {Host}", uri.Host);
    }

    public async Task<AgentReply> InvokeAsync(
        AgentDefinition agent, string prompt, CancellationToken ct = default)
    {
        if (_client is null)
            return new AgentReply(false, string.Empty, "Foundry runtime is not configured.");

        try
        {
            var agentId = await EnsureProvisionedAsync(agent, ct);

            var thread = (await _client.Threads.CreateThreadAsync(cancellationToken: ct)).Value;

            try
            {
                return await RunOnThreadAsync(agent, thread.Id, agentId, prompt, ct);
            }
            finally
            {
                // The thread holds the pasted job posting and the profile facts, verbatim, on
                // Azure's side. Nothing here referenced it again after the reply was read, so
                // it was pure residue — and residue that DELETE /api/me/data cannot reach,
                // which quietly made the erasure promise untrue.
                //
                // Deleted in a finally rather than after the happy path: a run that times out
                // or errors leaves exactly the same data behind, and those are the cases where
                // it would accumulate fastest.
                await DeleteThreadAsync(thread.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Foundry invocation failed for {Slug}", agent.Slug);
            return new AgentReply(false, string.Empty, "The model backend failed.");
        }
    }

    /// <summary>
    /// Deletes a thread on a best-effort basis.
    ///
    /// Deliberately swallows its own failures and takes no cancellation token: this runs in a
    /// finally, and the two things it must never do are mask the real error from the operation
    /// that just failed, or skip the cleanup because the caller's token was already cancelled —
    /// which is precisely the timeout case.
    /// </summary>
    private async Task DeleteThreadAsync(string threadId)
    {
        try
        {
            await _client!.Threads.DeleteThreadAsync(threadId);
        }
        catch (Exception ex)
        {
            // Logged rather than ignored: a persistent failure here means personal data is
            // accumulating in Azure, which is worth being able to see in the logs.
            _logger.LogWarning(ex, "Could not delete Foundry thread {ThreadId}", threadId);
        }
    }

    private async Task<AgentReply> RunOnThreadAsync(
        AgentDefinition agent, string threadId, string agentId, string prompt, CancellationToken ct)
    {
        await _client!.Messages.CreateMessageAsync(
            threadId: threadId,
            role:     MessageRole.User,
            content:  prompt,
            cancellationToken: ct);

        var run = (await _client.Runs.CreateRunAsync(
            threadId:    threadId,
            assistantId: agentId,
            cancellationToken: ct)).Value;

        var deadline = DateTime.UtcNow + RunTimeout;

        while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress)
        {
            if (DateTime.UtcNow > deadline)
            {
                _logger.LogWarning("Run for {Slug} exceeded {Timeout}s", agent.Slug, RunTimeout.TotalSeconds);
                return new AgentReply(false, string.Empty, "The model run timed out.");
            }

            await Task.Delay(PollInterval, ct);
            run = (await _client.Runs.GetRunAsync(threadId, run.Id, ct)).Value;
        }

        if (run.Status != RunStatus.Completed)
        {
            var reason = run.LastError?.Message ?? run.Status.ToString();
            _logger.LogError("Run for {Slug} ended {Status}: {Reason}", agent.Slug, run.Status, reason);
            return new AgentReply(false, string.Empty, $"The model run {run.Status}: {reason}");
        }

        var text = new StringBuilder();
        await foreach (var msg in _client.Messages.GetMessagesAsync(
            threadId: threadId,
            order:    ListSortOrder.Descending,
            cancellationToken: ct))
        {
            if (msg.Role != MessageRole.Agent) continue;

            foreach (var content in msg.ContentItems)
            {
                if (content is MessageTextContent textContent)
                    text.Append(textContent.Text);
            }

            // Newest agent message only.
            break;
        }

        return new AgentReply(true, text.ToString(), null);
    }

    /// <summary>
    /// Creates the Foundry agent on first use and caches it per slug+version.
    ///
    /// Keying on version matters: a prompt edit bumps the version in
    /// definition.json, which forces a new Foundry agent rather than silently
    /// keeping the old instructions alive behind a familiar slug.
    /// </summary>
    private async Task<string> EnsureProvisionedAsync(AgentDefinition agent, CancellationToken ct)
    {
        var key = $"{agent.Slug}@{agent.Version}";
        if (_provisioned.TryGetValue(key, out var existing)) return existing;

        // Temperature is declared in definition.json but not passed here, so the
        // Foundry backend ignores it. CreateAgentAsync does accept one (checked
        // against Azure.AI.Agents.Persistent 1.1.0); wiring it up is a known
        // follow-up, not something the SDK prevents.
        var created = (await _client!.Administration.CreateAgentAsync(
            model:        string.IsNullOrWhiteSpace(agent.Model) ? _fallbackModel : agent.Model,
            name:         key,
            instructions: agent.Instructions,
            cancellationToken: ct)).Value;

        var id = created.Id;
        _provisioned[key] = id;

        _logger.LogInformation("Provisioned Foundry agent {Key} as {Id}", key, id);
        return id;
    }
}

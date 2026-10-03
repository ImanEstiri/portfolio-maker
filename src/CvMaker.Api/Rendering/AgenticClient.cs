using System.Net.Http.Json;
using CvMaker.Shared;

namespace CvMaker.Api.Rendering;

/// <summary>
/// Talks to CvMaker.Agentic.
///
/// The agent service returns a <see cref="ComposeResult"/> carrying its own
/// <see cref="FailureCause"/>, so a composition that failed because the model
/// broke the schema stays distinguishable from one that failed because the
/// service was down. Only transport failures are classified here.
/// </summary>
public sealed class AgenticClient(HttpClient http, ILogger<AgenticClient> logger)
{
    public async Task<ComposeResult> ComposeAsync(ComposeRequest request, CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync("/api/compose/cv", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Agent service returned {Status}", response.StatusCode);
                return new ComposeResult(false, null, "The tailoring service failed.",
                    FailureCause.AgentUnavailable, []);
            }

            var result = await response.Content.ReadFromJsonAsync<ComposeResult>(ct);
            return result ?? new ComposeResult(false, null, "The tailoring service returned nothing.",
                FailureCause.AgentUnavailable, []);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ComposeResult(false, null, "Tailoring timed out.", FailureCause.AgentTimeout, []);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Agent service unreachable");
            return new ComposeResult(false, null, "The tailoring service is unavailable.",
                FailureCause.AgentUnavailable, []);
        }
    }
}

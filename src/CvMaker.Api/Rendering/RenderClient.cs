using System.Net;
using System.Net.Http.Json;
using CvMaker.Shared;

namespace CvMaker.Api.Rendering;

public sealed record RenderRequest(
    string TexSource,
    string Engine,
    IReadOnlyDictionary<string, string>? Assets);

public sealed record RenderOutcome(byte[]? Pdf, string? Error, FailureCause Cause);

/// <summary>
/// Talks to CvMaker.Render.
///
/// Every failure is mapped to a <see cref="FailureCause"/> here rather than in
/// the worker, because this is the only place that can tell the difference
/// between "the renderer is down" and "the renderer rejected this document" —
/// a distinction the deferred quota-refund policy depends on
/// (docs/decisions/0004-quota-and-refunds.md).
/// </summary>
public sealed class RenderClient(HttpClient http, ILogger<RenderClient> logger)
{
    public async Task<RenderOutcome> RenderAsync(
        string texSource,
        string engine,
        IReadOnlyDictionary<string, string>? assets,
        CancellationToken ct)
    {
        try
        {
            var response = await http.PostAsJsonAsync(
                "/render", new RenderRequest(texSource, engine, assets), ct);

            if (response.IsSuccessStatusCode)
            {
                var pdf = await response.Content.ReadAsByteArrayAsync(ct);
                return new RenderOutcome(pdf, null, FailureCause.None);
            }

            // 422 is the renderer's "this document does not typeset" — a
            // property of the input, not an outage.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var problem = await SafeReadProblemAsync(response, ct);
                var timedOut = problem?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;

                return new RenderOutcome(
                    null,
                    problem ?? "Typesetting failed.",
                    timedOut ? FailureCause.RenderTimeout : FailureCause.BindingFailed);
            }

            logger.LogError("Renderer returned {Status}", response.StatusCode);
            return new RenderOutcome(null, "Typesetting service error.", FailureCause.RenderCrashed);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The HttpClient deadline fired, not the caller — the renderer is
            // wedged or the document is pathological.
            return new RenderOutcome(null, "Typesetting timed out.", FailureCause.RenderTimeout);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Renderer unreachable");
            return new RenderOutcome(null, "Typesetting service unavailable.", FailureCause.RenderUnavailable);
        }
    }

    private static async Task<string?> SafeReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(ct);
            return problem?.Detail;
        }
        catch (Exception)
        {
            // A malformed error body must not mask the real failure.
            return null;
        }
    }
}
